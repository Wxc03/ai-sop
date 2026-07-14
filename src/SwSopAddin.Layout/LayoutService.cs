using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using SolidWorks.Interop.sldworks;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.1 + W5.2 — F14 碰撞检测 + F15 超界缩放 + 自动避让编排。
    /// 流程:
    ///   1. BoundingBoxCollector 采集所有 View / Balloon / BOM 包围盒
    ///   2. (F15) OutOfBoundsScaler:view 超出 sheet 时按比例缩小(IView::SetScaleRatio 真用 → W6 错)
    ///   3. (F14) CollisionDetector 列出所有碰撞对(带 padding)
    ///   4. (F14) AvoidanceResolver 贪心平移解决碰撞
    ///   5. LayoutApplier 写回 SW(只动 movable / 已被 scaler 改 Scale 的 view)
    ///   6. F16 留给后续 — scaler 压到 MinViewScale 仍超界时记到 Scaling.MinScaleClamped,等真要分页再做
    /// </summary>
    public class LayoutService : ILayoutService
    {
        private static readonly Logger Log = Logging.ForType(typeof(LayoutService));

        public LayoutResult ApplyLayout(
            DrawingDoc drw,
            View[] views,
            BomTableAnnotation bomTable,
            LayoutOptions options = null,
            Sheet targetSheet = null)
        {
            if (drw == null) throw new ArgumentNullException(nameof(drw));
            options = options ?? LayoutOptions.Default;

            var result = new LayoutResult();
            try
            {
                var sheet = GetSheetBounds(drw, options, targetSheet);
                Log.Info("ApplyLayout: sheet={0}", sheet);

                var collector = new BoundingBoxCollector(options);
                var elements = CollectAll(collector, views, bomTable);
                result.ElementsCollected = elements.Count;

                if (elements.Count == 0)
                {
                    result.Success = true;
                    result.Notes = "无可参与布局的元素(view/balloon/bom 都没拿到)";
                    Log.Warn("ApplyLayout: 0 元素 — 跳过避让");
                    return result;
                }

                // ===== W5.2 F15:超界自动缩放 =====
                if (options.AutoScaleToFit)
                {
                    var scaler = new OutOfBoundsScaler(options);
                    var scaling = scaler.Scale(elements, sheet);
                    result.Scaling = scaling;
                    if (scaling.MinScaleClamped > 0)
                    {
                        Log.Warn("ApplyLayout: {0} 个 view 压到 MinViewScale 仍超界 — F16 分页未实现,留待后续",
                            scaling.MinScaleClamped);
                    }
                }

                // ===== W5.1 F14:碰撞避让 =====
                var resolver = new AvoidanceResolver(options);
                var avoid = resolver.Resolve(elements, sheet);
                result.Avoidance = avoid;

                // ===== W10+ 整体居中:算 movable view 的 union bbox,平移让中心对齐 sheet 中心 =====
                CenterMovableElements(elements, sheet);

                var applier = new LayoutApplier();
                result.ElementsApplied = applier.Apply(elements);

                result.Success = true;
                var notes = new List<string>();
                if (result.Scaling != null && result.Scaling.ScaledCount > 0)
                    notes.Add($"F15 缩放 {result.Scaling.ScaledCount} 个 view");
                if (result.Scaling != null && result.Scaling.MinScaleClamped > 0)
                    notes.Add($"仍有 {result.Scaling.MinScaleClamped} 个 view 压到下限仍超界(F16 待实现)");
                if (avoid.Clean) notes.Add("F14 避让完全解干净");
                else notes.Add($"F14 仍有 {avoid.RemainingCollisions} 对碰撞未解决");
                result.Notes = string.Join("; ", notes);

                Log.Info("ApplyLayout 结束: collected={0} applied={1} clean={2}",
                    result.ElementsCollected, result.ElementsApplied, avoid.Clean);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ApplyLayout 失败");
                result.Success = false;
                result.Notes = "异常:" + ex.Message;
            }
            return result;
        }

        /// <summary>
        /// Phase 3 — iso 主视图自动居中 + 缩放。算法(w12-phase0-sw-api-writability 记忆确认的 API 形态):
        ///   1. 读 sheet 尺寸、当前 iso 外接框、算 SopLayoutPlanner.Compute 的目标矩形 + IsoScaleFactor(相对倍数)。
        ///   2. newScale = curScale * IsoScaleFactor,写 ScaleRatio(double[2]{分子,分母})。
        ///   3. 缩放会改变 view 原点(Position)相对外接框(GetOutline)的偏移量 — 缩放后重新量一次偏移,
        ///      再反推 Position 目标值,不能直接把 Position 设成目标矩形的 X/Y(两者不是一回事)。
        ///   4. Position 必须写 double[],不能写 object[](COM SAFEARRAY 类型不符会静默清零 X)。
        /// 任何一步失败都返回 Success=false + Notes,不 throw。
        /// </summary>
        public IsoPlacementResult ApplyIsoPlacement(
            DrawingDoc drw,
            View isoView,
            LayoutOptions options = null,
            Sheet targetSheet = null)
        {
            var result = new IsoPlacementResult();
            if (drw == null || isoView == null)
            {
                result.Notes = "drw/isoView 为空";
                return result;
            }
            options = options ?? LayoutOptions.Default;

            try
            {
                var sheet = GetSheetBounds(drw, options, targetSheet);
                var collector = new BoundingBoxCollector(options);

                LayoutRect? currentRect = collector.TryCollect(isoView, LayoutElementKind.View);
                if (currentRect == null)
                {
                    result.Notes = "无法读取当前 iso view 外接框(GetOutline 失败/退化)";
                    return result;
                }

                var plan = SopLayoutPlanner.Compute(sheet, currentRect, null, options);
                if (plan.IsoTarget == null)
                {
                    result.Notes = "planner 未产出 IsoTarget: " + string.Join("; ", plan.Notes);
                    return result;
                }
                var target = plan.IsoTarget.Value;

                double[] curScaleArr = isoView.ScaleRatio as double[];
                double curScale = (curScaleArr != null && curScaleArr.Length >= 2 && curScaleArr[1] != 0)
                    ? curScaleArr[0] / curScaleArr[1] : 1.0;

                // SW 偶尔在刚插入的 drawing view 上返回 [0,n] 或极小比例。继续相乘
                // 会把已很小的视图写成 0，导出的图纸只剩集中在一点的球标。
                const double minimumUsableScale = 1e-4;
                bool invalidScale = double.IsNaN(curScale) || double.IsInfinity(curScale) || curScale < minimumUsableScale;
                if (invalidScale)
                {
                    Log.Warn("ApplyIsoPlacement: view '{0}' 返回无效 ScaleRatio={1}; 使用 planner 比例 {2:F4} 作为绝对值",
                        isoView.Name, curScale, plan.IsoScaleFactor);
                    curScale = 1.0;
                }
                double newScale = invalidScale ? plan.IsoScaleFactor : curScale * plan.IsoScaleFactor;

                isoView.ScaleRatio = new double[] { newScale, 1.0 };

                // 缩放后重新量 Position <-> 外接框的偏移(不能假设缩放前量的偏移在缩放后还成立)
                double[] posAfterScale = isoView.Position as double[];
                LayoutRect? outlineAfterScale = collector.TryCollect(isoView, LayoutElementKind.View);
                if (posAfterScale == null || posAfterScale.Length < 2 || outlineAfterScale == null)
                {
                    result.Notes = string.Format(
                        "缩放已写入(scale {0:F3}→{1:F3})但缩放后读 Position/Outline 失败,跳过 Position 写入",
                        curScale, newScale);
                    result.AppliedScaleRatio = newScale;
                    return result;
                }

                double offsetX = outlineAfterScale.Value.X - posAfterScale[0];
                double offsetY = outlineAfterScale.Value.Y - posAfterScale[1];
                double newPosX = target.X - offsetX;
                double newPosY = target.Y - offsetY;

                isoView.Position = new double[] { newPosX, newPosY, 0.0 };

                result.Success = true;
                result.IsoTargetRect = target;
                result.AppliedScaleRatio = newScale;
                result.Notes = string.Format(
                    "iso 缩放 {0:F3}→{1:F3},目标矩形 [{2:F4},{3:F4} {4:F4}x{5:F4}],写 Position=({6:F4},{7:F4})",
                    curScale, newScale, target.X, target.Y, target.Width, target.Height, newPosX, newPosY);
                Log.Info("ApplyIsoPlacement: {0}", result.Notes);
                return result;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ApplyIsoPlacement 失败");
                result.Notes = "异常: " + ex.Message;
                return result;
            }
        }

        public CollisionReport DetectOnly(
            DrawingDoc drw,
            View[] views,
            BomTableAnnotation bomTable,
            LayoutOptions options = null)
        {
            if (drw == null) throw new ArgumentNullException(nameof(drw));
            options = options ?? LayoutOptions.Default;

            var report = new CollisionReport();
            var collector = new BoundingBoxCollector(options);
            var elements = CollectAll(collector, views, bomTable);
            report.ElementsScanned = elements.Count;

            var detector = new CollisionDetector();
            var collisions = detector.Detect(elements, options.PaddingMeters);
            report.CollisionCount = collisions.Count;
            report.Summary = collisions.Count == 0
                ? "无碰撞"
                : string.Join("; ", collisions.Take(5).Select(c => c.ToString()));
            return report;
        }

        /// <summary>
        /// 从各 SW COM 数组采集成 LayoutElement 列表。
        /// 采不到的元素直接跳过(W5.1 容忍 SW API 不稳)。
        /// W6-fix:balloons 暂不传 — SW 2024 interop 没暴露 BalloonAnnotation 强类型。
        /// </summary>
        private static List<LayoutElement> CollectAll(
            BoundingBoxCollector collector,
            View[] views,
            BomTableAnnotation bomTable)
        {
            var list = new List<LayoutElement>();

            if (views != null)
            {
                for (int i = 0; i < views.Length; i++)
                {
                    var v = views[i];
                    if (v == null) continue;
                    var rect = collector.TryCollect(v, LayoutElementKind.View);
                    if (rect == null) continue;
                    list.Add(LayoutElement.WithDefaults(
                        label: "View[" + i + "]:" + (v.Name ?? "?"),
                        kind: LayoutElementKind.View,
                        rect: rect.Value,
                        comRef: v));
                }
            }

            if (bomTable != null)
            {
                var rect = collector.TryCollect(bomTable, LayoutElementKind.BomTable);
                if (rect != null)
                {
                    list.Add(LayoutElement.WithDefaults(
                        label: "BOM",
                        kind: LayoutElementKind.BomTable,
                        rect: rect.Value,
                        comRef: bomTable));
                }
            }

            return list;
        }

        /// <summary>
        /// 拿图纸大小。Phase 3 起优先读真实 Sheet.GetSize(米,标准/自定义尺寸都准确) —
        /// IDrawingDoc.IGetCurrentSheet() 拿当前图纸,ISheet.GetSize(out width, out height) 真机确认可读。
        /// 读失败(sheet null/抛异常)才 fallback 到 LayoutOptions.PaperSize 字符串表,A3 兜底。
        ///
        /// Part B(多 sheet 架构就绪化):explicitSheet 非 null 时优先用它(未来跨 sheet 布局指定目标 sheet),
        /// 否则退回 drw.IGetCurrentSheet()(今天单 sheet 流程零行为变化)。
        /// </summary>
        internal static SheetBounds GetSheetBounds(DrawingDoc drw, LayoutOptions options, Sheet explicitSheet = null)
        {
            try
            {
                Sheet sheet = explicitSheet ?? drw?.IGetCurrentSheet();
                if (sheet != null)
                {
                    double width = 0, height = 0;
                    sheet.GetSize(ref width, ref height);
                    if (width > 0 && height > 0)
                    {
                        Log.Info("GetSheetBounds: 读到真实 Sheet.GetSize width={0:F4} height={1:F4}", width, height);
                        return new SheetBounds(width, height);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "GetSheetBounds: Sheet.GetSize 读取失败,fallback 到 PaperSize 表");
            }

            // fallback:PaperSize 字符串表
            if (options != null && !string.IsNullOrEmpty(options.PaperSize))
            {
                switch (options.PaperSize.ToUpperInvariant())
                {
                    case "A0": return new SheetBounds(1.189, 0.841);
                    case "A1": return new SheetBounds(0.841, 0.594);
                    case "A2": return new SheetBounds(0.594, 0.420);
                    case "A3": return new SheetBounds(0.420, 0.297);
                    case "A4": return new SheetBounds(0.297, 0.210);
                    default: break;
                }
            }
            Log.Warn("GetSheetBounds: 用 A3 兜底(SW 2024 interop 缺 GetSheetWidth/Height/Size)");
            return SheetBounds.A3;
        }

        /// <summary>
        /// W10+ 整体居中:算 movable view(排除 BomTable 这种 immovable)的 union bbox,
        /// 平移让 union center 对齐 sheet center。F14 避让后再做(避让后的位置做居中,
        /// 否则避让后 view 又被推偏)。
        /// </summary>
        private static void CenterMovableElements(
            System.Collections.Generic.List<LayoutElement> elements,
            SheetBounds sheet)
        {
            if (elements == null || elements.Count == 0) return;

            // 1. 算 movable elements 的 union bbox
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            int movableCount = 0;
            foreach (var e in elements)
            {
                if (!e.Movable) continue;  // BOM 等 immovable 跳过
                var r = e.Rect;
                if (r.X < minX) minX = r.X;
                if (r.Y < minY) minY = r.Y;
                if (r.Right > maxX) maxX = r.Right;
                if (r.Top > maxY) maxY = r.Top;
                movableCount++;
            }
            if (movableCount == 0) return;

            // 2. 算 center
            double unionCenterX = (minX + maxX) / 2;
            double unionCenterY = (minY + maxY) / 2;

            // 3. 算 sheet center(LayoutRect Y-up,跟 SheetBounds 一致)
            double sheetCenterX = sheet.Width / 2;
            double sheetCenterY = sheet.Height / 2;

            // 4. 平移 delta
            double dx = sheetCenterX - unionCenterX;
            double dy = sheetCenterY - unionCenterY;
            if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6) return;  // 已经在中心

            // 5. 应用到所有 movable elements(更新 Rect,不影响 immovable)
            foreach (var e in elements)
            {
                if (!e.Movable) continue;
                e.Rect = e.Rect.Translated(dx, dy);
            }
            Log.Info("CenterMovableElements: movable={0}, sheet center=({1:F4},{2:F4}), union center=({3:F4},{4:F4}), delta=({5:F4},{6:F4})",
                movableCount, sheetCenterX, sheetCenterY, unionCenterX, unionCenterY, dx, dy);
        }
    }
}
