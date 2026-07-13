using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SwSopAddin.Layout
{
    /// <summary>
    /// W11 — SOP 工程图整体布局规划器(纯函数,无 COM 依赖,单测 100% 覆盖)。
    ///
    /// 设计目标(用户确认):
    /// 1. iso 主视图居中 = sheet 几何中心(中心是 sheet.Width/2, sheet.Height/2)
    /// 2. iso 高度 = (sheet.H - 标题栏.H - 上下留白) × IsoViewHeightFraction(默认 0.7)
    /// 3. BOM 智能右下 = 标题栏左侧预留条带,避开标题栏(贴底贴右,但避开标题栏矩形)
    ///
    /// 输入:sheet 尺寸 + iso 当前 bbox(从 BoundingBoxCollector 拿)+ 可选 BOM bbox + LayoutOptions。
    /// 输出:SopLayoutPlan { IsoTarget, BomTarget, TitleBlockRect, Notes } — caller 写回 SW。
    ///
    /// 坐标系统:全用 LayoutRect(Y-up,sheet 原点左下角)。SW Y-down 的翻转留给 LayoutApplier。
    /// </summary>
    internal static class SopLayoutPlanner
    {
        /// <summary>规划输出。caller 据此把 Iso/Bom 写到对应 Rect 位置。</summary>
        internal sealed class SopLayoutPlan
        {
            /// <summary>iso 主视图目标矩形(null 表示不算)。</summary>
            public LayoutRect? IsoTarget { get; set; }
            /// <summary>BOM 目标矩形(null 表示无 BOM 不算)。</summary>
            public LayoutRect? BomTarget { get; set; }
            /// <summary>标题栏禁区(BOM 不能压上去,仅供日志/调试)。</summary>
            public LayoutRect TitleBlockRect { get; set; }
            /// <summary>说明:超界 / clamp / 冲突等,UI/debug 用。</summary>
            public List<string> Notes { get; set; } = new List<string>();
            /// <summary>iso 目标比例 = targetH / currentH(供 Phase 3 写 view.IScaleRatio)。</summary>
            public double IsoScaleFactor { get; set; } = 1.0;
        }

        /// <summary>
        /// 算 SOP 工程图整体布局。
        /// </summary>
        /// <param name="sheet">图纸真实尺寸(米),由 LayoutService.GetSheetBounds 算。</param>
        /// <param name="currentIsoRect">iso 主视图当前 bbox(刚 M4 插入后 SW 默认比例);null 跳过 iso 算。</param>
        /// <param name="bomRect">BOM 当前 bbox(M6 插入后从 BoundingBoxCollector 拿);null 跳过 BOM 算。</param>
        /// <param name="opt">布局选项(IsoViewHeightFraction、TitleBlock、 BomReserved…)。</param>
        internal static SopLayoutPlan Compute(
            SheetBounds sheet,
            LayoutRect? currentIsoRect,
            LayoutRect? bomRect,
            LayoutOptions opt)
        {
            if (opt == null) throw new ArgumentNullException(nameof(opt));
            var plan = new SopLayoutPlan();

            // ===== 标题栏禁区(A3 默认右下 0.18 × 0.05) =====
            double tbW = Clamp(opt.TitleBlockWidthMeters, 0.05, sheet.Width);
            double tbH = Clamp(opt.TitleBlockHeightMeters, 0.01, sheet.Height);
            // 标题栏 = sheet 右下,Left = sheet.W - tbW, Top = tbH, Bottom = 0
            // (LayoutRect.Y 是底,bottom=0,top=tbH)
            plan.TitleBlockRect = new LayoutRect(
                x: sheet.Width - tbW,
                y: 0,
                width: tbW,
                height: tbH);

            // ===== iso 目标 =====
            if (currentIsoRect.HasValue)
            {
                var iso = ComputeIsoTarget(sheet, currentIsoRect.Value, plan.TitleBlockRect, opt, plan.Notes);
                plan.IsoTarget = iso.target;
                plan.IsoScaleFactor = iso.scaleFactor;
            }

            // ===== BOM 目标 =====
            if (bomRect.HasValue)
            {
                plan.BomTarget = ComputeBomTarget(sheet, bomRect.Value, plan.TitleBlockRect, opt, plan.Notes);
            }

            return plan;
        }

        // ===== iso 目标计算 =====

        private static (LayoutRect target, double scaleFactor) ComputeIsoTarget(
            SheetBounds sheet, LayoutRect currentIso, LayoutRect titleBlock,
            LayoutOptions opt, List<string> notes)
        {
            double margin = Math.Max(opt.SheetMarginMeters, 0);

            // 1. 可用宽度 = sheet.W - 右侧竖条带 - 留白(给 BOM zone 留位置,不让 iso 压上去)
            //    W14 修:标题栏和 BOM 预留区实际是"同一条竖直带"上下叠放(真机验证:BOM 锚点
            //    X=0.234 与标题栏左边缘 0.42-0.18=0.24 几乎重合,差 6mm),不是左右并排的两块
            //    区域——旧公式把两者相加扣减(0.18+0.16=0.34,占 420mm 图纸宽度的 81%),导致
            //    可用宽度被错算成只剩 70mm,是真机 iso 视图贴左边界、缩得很小的直接原因。
            //    改成只扣一次这条带的实际宽度(取较大值,保证 BOM 和标题栏都不会被 iso 压到)。
            double bomZoneW = Clamp(opt.BomReservedWidthMeters, 0.05, sheet.Width);
            double rightBandW = Math.Max(titleBlock.Width, bomZoneW);
            double usableW = sheet.Width - rightBandW - 2 * margin;
            if (usableW <= 0)
            {
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "iso 可用宽度 ≤ 0 (sheetW={0:F4} titleW={1:F4} bomZoneW={2:F4}),跳过 iso 算",
                    sheet.Width, titleBlock.Width, bomZoneW));
                return (currentIso, 1.0);
            }

            // 2. 可用高度 = sheet.H - 标题栏 - 上下留白
            double usableH = sheet.Height - titleBlock.Height - 2 * margin;
            if (usableH <= 0)
            {
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "iso 可用高度 ≤ 0 (sheetH={0:F4} titleH={1:F4}),跳过 iso 算", sheet.Height, titleBlock.Height));
                return (currentIso, 1.0);
            }

            // 3. 目标高 = usableH × IsoViewHeightFraction,但不小于 IsoMinHeightMeters
            double targetH = Math.Max(usableH * opt.IsoViewHeightFraction, opt.IsoMinHeightMeters);
            double currentH = currentIso.Height;
            if (currentH <= 1e-9)
            {
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "iso 当前高度={0:F6} ≤ 0,跳过缩放", currentH));
                return (currentIso, 1.0);
            }
            double scaleH = targetH / currentH;

            // 4. 目标宽也按 IsoViewHeightFraction 比例(等比缩放前提下,受可用宽度 cap)
            double aspectRatio = currentIso.Width / currentH;  // 原始 view 宽高比
            double naturalW = targetH * aspectRatio;
            // cap:若按 IsoViewHeightFraction 算出的宽 > usableW,降 scale 让宽不超
            double scale = scaleH;
            if (naturalW > usableW)
            {
                double capW = usableW;
                scale = capW / currentIso.Width;
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "iso 按高缩放超出可用宽,改按宽 cap:scale {0:F3}×→{1:F3}×", scaleH, scale));
            }
            double newH = currentH * scale;
            double newW = currentIso.Width * scale;
            // 应用 IsoMinHeightMeters 下限
            if (newH < opt.IsoMinHeightMeters)
            {
                double adjust = opt.IsoMinHeightMeters / newH;
                scale *= adjust;
                newH *= adjust;
                newW *= adjust;
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "iso 触发 IsoMinHeightMeters 下限,scale 调整到 {0:F3}×", scale));
            }

            // 5. 居中——可用区在 sheet 左侧(标题栏/BOM 预留区叠放共占 sheet 右侧 rightBandW 宽的一条带),
            //    可用区范围是 [margin, sheet.Width - rightBandW - margin],居中点 = margin + usableW/2。
            //    旧公式把居中点算进了右侧预留区里,导致 iso 贴到 sheet 右边界(真机复现:0.42m 宽 sheet 上
            //    算出 x=0.345,右边缘 0.415 几乎贴死 sheet 边界)。
            double rightReservedW = rightBandW;
            double availCenterX = margin + usableW / 2.0;
            double x = availCenterX - newW / 2.0;
            double y = sheet.Height / 2.0 - newH / 2.0;

            // 6. 防越界(右边界 = 可用区右沿,不能滑进标题栏/BOM 预留区)。
            //    左边界(sheet 物理边缘)优先于右边界(标题栏/BOM 只是"预留",不是硬边界)——
            //    newW 由 IsoMinHeightMeters 下限反推放大后可能略超 usableW(比如 BOM 预留区太宽
            //    挤压出的可用宽本身就很窄),此时若两边界都要满足是不可能的,情愿让 iso 略微
            //    压进 BOM 预留区,也不能让它滑出 sheet 左边缘。
            if (y < margin) y = margin;
            if (y + newH > sheet.Height - margin) y = sheet.Height - margin - newH;
            double minX = margin;
            double maxX = sheet.Width - rightReservedW - margin - newW;
            if (maxX < minX)
            {
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "iso 宽度 {0:F4} 超出可用宽 {1:F4}(受 IsoMinHeightMeters 下限反推放大),"
                    + "已贴左边界,右侧会压进标题栏/BOM 预留区", newW, usableW));
                x = minX;
            }
            else
            {
                x = Math.Max(minX, Math.Min(x, maxX));
            }

            if (Math.Abs(scale - 1.0) > 0.01)
            {
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "iso 缩放 {0:F3}×(当前 {1:F4}m → 目标 {2:F4}m),可用区居中 ({3:F4},{4:F4}) {5:F4}x{6:F4}",
                    scale, currentH, newH, x, y, newW, newH));
            }
            return (new LayoutRect(x, y, newW, newH), scale);
        }

        // ===== BOM 目标计算(智能右下,避开标题栏) =====

        private static LayoutRect ComputeBomTarget(
            SheetBounds sheet, LayoutRect bomRect, LayoutRect titleBlock,
            LayoutOptions opt, List<string> notes)
        {
            double margin = Math.Max(opt.SheetMarginMeters, 0);
            // 1. BOM 区域 = 标题栏左侧的竖直条带
            double zoneW = Clamp(opt.BomReservedWidthMeters, 0.05, sheet.Width);
            double zoneLeft = sheet.Width - titleBlock.Width - zoneW;
            double zoneTop = sheet.Height - Clamp(opt.BomReservedHeightMeters, 0, sheet.Height);
            double zoneBottom = titleBlock.Height + margin;  // 避开标题栏
            double zoneH = zoneTop - zoneBottom;

            if (zoneW < bomRect.Width + margin)
            {
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "BOM 预留宽 {0:F4} < BOM 宽 {1:F4}+留白,BOM 可能与 iso 重叠", zoneW, bomRect.Width));
            }
            if (zoneH < bomRect.Height + margin)
            {
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "BOM 预留高 {0:F4} < BOM 高 {1:F4}+留白,BOM 可能与 iso 重叠", zoneH, bomRect.Height));
            }

            // 2. BOM 目标:贴 zone 右下角,留 SheetMargin
            //    zoneBottom 已含 margin(标题栏上方一格留白),BOM 底贴 zoneBottom 即可。
            //    否则 x/y 各加 margin 会变"双重留白",BOM 偏中。
            double x = zoneLeft + zoneW - bomRect.Width - margin;
            double y = zoneBottom;  // LayoutRect.Y 是底,贴 zone 底部

            notes.Add(string.Format(CultureInfo.InvariantCulture,
                "BOM 智能右下: zone=({0:F4},{1:F4} {2:F4}x{3:F4}), bomTarget=({4:F4},{5:F4} {6:F4}x{7:F4})",
                zoneLeft, zoneBottom, zoneW, zoneH, x, y, bomRect.Width, bomRect.Height));
            return new LayoutRect(x, y, bomRect.Width, bomRect.Height);
        }

        private static double Clamp(double v, double min, double max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}