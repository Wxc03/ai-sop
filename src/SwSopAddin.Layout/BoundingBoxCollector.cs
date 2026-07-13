using System;
using NLog;
using SolidWorks.Interop.sldworks;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.1 — 从 SW COM 对象(VIEW / BalloonAnnotation / BomTableAnnotation)采集包围盒。
    /// 全部 try/catch,失败返 null + 日志警告。Layout 主流程不会被一个失败元素拖垮。
    ///
    /// COM API 不确定性(W3.5 + W4.3 经验):
    /// - View.GetOutline() → Object,通常是 double[4] {Left, Top, Right, Bottom},SW 坐标,Y 向上
    /// - BalloonAnnotation.BalloonPosition → Object,通常是 double[3] {X, Y, Z},只有点无尺寸
    ///   → 这里用 LayoutOptions.BalloonSizeMeters 估算包围盒(矩形)
    /// - BomTableAnnotation 本身不实现 IAnnotation(反射确认,W12);位置须经
    ///   (bom as ITableAnnotation).GetAnnotation() 拿到 Annotation 对象再 as IAnnotation
    /// </summary>
    public class BoundingBoxCollector
    {
        private static readonly Logger Log = Logging.ForType(typeof(BoundingBoxCollector));

        private readonly LayoutOptions _options;

        public BoundingBoxCollector(LayoutOptions options = null)
        {
            _options = options ?? LayoutOptions.Default;
        }

        public LayoutRect? TryCollect(object comRef, LayoutElementKind kind)
        {
            if (comRef == null) return null;
            switch (kind)
            {
                case LayoutElementKind.View:
                    return TryCollectView(comRef as View);
                case LayoutElementKind.BomTable:
                    return TryCollectBomTable(comRef as BomTableAnnotation);
                default:
                    // W6-fix:Balloon kind 暂不支持 — SW 2024 interop 没暴露 BalloonAnnotation 强类型
                    // (DLL 里只有 BalloonStack / BalloonOptions,没有 BalloonAnnotation class)
                    // 走反射需要更多工作,先跳过。F14+ 在 M4 修好后再加。
                    return null;
            }
        }

        /// <summary>
        /// View.GetOutline() — 拿 view 在 sheet 上的外接矩形(米,Y 向上)。
        /// </summary>
        private LayoutRect? TryCollectView(View view)
        {
            if (view == null) return null;
            try
            {
                object outline = view.GetOutline();
                double[] arr = ToDoubleArray(outline, 4);
                if (arr == null)
                {
                    Log.Warn("TryCollectView: GetOutline 返不可解析类型 {0}",
                        outline?.GetType().FullName ?? "null");
                    return null;
                }
                double left = arr[0], top = arr[1], right = arr[2], bottom = arr[3];
                double x = Math.Min(left, right);
                double y = Math.Min(top, bottom);
                double w = Math.Abs(right - left);
                double h = Math.Abs(bottom - top);
                if (w <= 1e-6 || h <= 1e-6)
                {
                    Log.Warn("TryCollectView: 退化矩形 w={0:F6} h={1:F6}", w, h);
                    return null;
                }
                Log.Info("TryCollectView: '{0}' rect=[{1:F4},{2:F4} {3:F4}x{4:F4}]",
                    view.Name, x, y, w, h);
                return new LayoutRect(x, y, w, h);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "TryCollectView 失败");
                return null;
            }
        }

        /// <summary>
        /// W6-fix:Balloon 包围盒采集暂禁用。
        /// SW 2024 interop 没暴露 BalloonAnnotation 强类型,AutoBalloon5 返 untyped object。
        /// 走反射拿 BalloonPosition 后续可加,目前 LayoutService.CollectAll 也不会传 balloon comRef 进来。
        /// </summary>

        /// <summary>
        /// W10+ 修复:BOM 现在参与 layout 碰撞检测(view 避开 BOM,BOM 锚定不动)。
        /// 实现:
        ///   1. 用 bomTable.IGetPosition() 拿 BOM 在 SW 坐标系的位置(单点,top-left,Y down)
        ///   2. 估算 BOM 宽高 — W6-fix 模板 bom-standard.sldbomtbt 4 行 × 4 列,实测约 100×32mm
        ///      (精确值要 GetTotalBoundingBox 但 SW 2024 interop 缺,接受估算)
        ///   3. SW Y-down → Layout Y-up 转换(用 sheet height,A3 = 0.297m 兜底)
        /// 失败返 null 让 layout 跳过 BOM,view 仍能 layout(降级)。
        /// </summary>
        private LayoutRect? TryCollectBomTable(BomTableAnnotation bom)
        {
            if (bom == null) return null;
            try
            {
                // W12 fix: BomTableAnnotation 反射确认只实现 IBomTableAnnotation,不实现 IAnnotation —
                // (bom as IAnnotation) 之前必然 null,此方法自 W10+ 起从未真正采集到 BOM 位置,
                // 一直静默降级(BOM 从未参与 layout 碰撞检测)。
                // 正确路径:BomTableAnnotation 同时是 ITableAnnotation(通用表格注解),
                // ITableAnnotation.GetAnnotation() 返回真正实现 IAnnotation 的 Annotation 对象。
                ITableAnnotation tbl = bom as ITableAnnotation;
                if (tbl == null) return null;
                Annotation annObj = tbl.GetAnnotation();
                IAnnotation ann = annObj as IAnnotation;
                if (ann == null) return null;
                object posObj = ann.GetPosition();
                double[] pos = ToDoubleArray(posObj, 3);
                if (pos == null || pos.Length < 2) return null;
                double bomX_swd = pos[0];      // SW X(top-left)
                double bomY_swd = pos[1];      // SW Y(top, Y down)

                // 估算 BOM 宽高 — W6-fix 模板 + gb_a3 默认 BOM 尺寸
                double bomW = 0.10;   // 100mm 宽
                double bomH = 0.032;  // 32mm 高(4 行 × ~8mm)

                // SW → Layout Y 转换
                //   LayoutRect 用 Y-up(sheet bottom = 0, sheet top = sheetH)
                //   SW 坐标用 Y-down(sheet top = 0, sheet bottom = sheetH)
                //   layout y_top = sheetH - bomY_swd  (SW y_top → Layout y_top 翻转)
                //   layout y_bottom = sheetH - (bomY_swd + bomH)
                double sheetH = 0.297;  // A3 兜底,跟 LayoutService.GetSheetBounds 一致
                double layoutY_top = sheetH - bomY_swd;
                double layoutX_left = bomX_swd;

                Log.Info("TryCollectBomTable: SW pos=({0:F3},{1:F3} swd) → layout=({2:F3},{3:F3} {4:F3}x{5:F3})",
                    bomX_swd, bomY_swd, layoutX_left, layoutY_top, bomW, bomH);
                return new LayoutRect(layoutX_left, layoutY_top, bomW, bomH);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "TryCollectBomTable 失败");
                return null;
            }
        }

        private static double[] ToDoubleArray(object obj, int expected)
        {
            if (obj == null) return null;
            try
            {
                if (obj is double[] d)
                {
                    if (d.Length < expected) return null;
                    return d;
                }
                if (obj is Array a && a.Length >= expected)
                {
                    var arr = new double[expected];
                    for (int i = 0; i < expected; i++)
                    {
                        arr[i] = Convert.ToDouble(a.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                    }
                    return arr;
                }
            }
            catch { /* 转换失败返 null */ }
            return null;
        }
    }
}