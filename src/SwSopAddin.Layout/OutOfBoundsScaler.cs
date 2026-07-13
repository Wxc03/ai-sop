using System;
using System.Collections.Generic;
using NLog;
using SolidWorks.Interop.sldworks;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.2 — F15 超界自动缩放。
    /// 对每个 Scalable=true 的 view,如果 rect 超出 sheet(扣除 margin 后),按比例缩小其 Scale。
    ///
    /// 算法:
    /// 1. 对每个 scalable view:compute fit factor k = min(W_safe/W_cur, H_safe/H_cur)
    ///    k &lt; 1 → 需要缩小 → new_scale = old_scale * k
    ///    k &gt;= 1 → 已经 fit,不动
    /// 2. new_scale < MinViewScale → 不再缩(留给 F16 分页)
    /// 3. 写回 view.ScaleRatio(SCALE API 真的能用,W6 当时的"无 interop"判断是错的),
    ///    更新 element.Rect(按比例缩 width/height)
    /// 4. 迭代直到所有 view 都 fit(或达到 min)
    ///
    /// 限制:
    /// - 假设 view 在原位置不动(只缩);view 移动由避让算法处理
    /// - view.ScaleRatio 通过 IView::SetScaleRatio(double,double) 写入
    /// - ScaleRatio 用 uniform scale(同 x/y);非等比 view 留待 V2
    /// </summary>
    public class OutOfBoundsScaler
    {
        private static readonly Logger Log = Logging.ForType(typeof(OutOfBoundsScaler));

        private readonly LayoutOptions _options;

        public OutOfBoundsScaler(LayoutOptions options = null)
        {
            _options = options ?? LayoutOptions.Default;
        }

        public ScalingResult Scale(IEnumerable<LayoutElement> elements, SheetBounds sheet)
        {
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            var stats = new ScalingResult();

            var list = new List<LayoutElement>(elements);
            int iter = 0;
            const int maxIter = 10;  // 缩放一般 1-2 次收敛

            while (iter < maxIter)
            {
                iter++;
                bool anyChange = false;
                foreach (var e in list)
                {
                    if (e == null || !e.Scalable) continue;
                    if (e.Kind != LayoutElementKind.View) continue;
                    if (e.ComRef is View v && TryScaleView(e, v, sheet, stats))
                    {
                        anyChange = true;
                        stats.ScaledCount++;
                    }
                }
                if (!anyChange) break;
            }

            stats.IterationsUsed = iter;
            Log.Info("Scale 完成: iters={0} scaled={1} clamped={2}",
                stats.IterationsUsed, stats.ScaledCount, stats.MinScaleClamped);
            return stats;
        }

        private bool TryScaleView(LayoutElement e, View view, SheetBounds sheet, ScalingResult stats)
        {
            var r = e.Rect;
            double margin = _options.SheetMarginMeters;
            double maxW = sheet.Width - 2 * margin;
            double maxH = sheet.Height - 2 * margin;

            // 已经 fit
            if (r.Width <= maxW && r.Height <= maxH) return false;

            // 计算 fit factor k < 1
            double kx = (r.Width > 1e-9) ? maxW / r.Width : 1.0;
            double ky = (r.Height > 1e-9) ? maxH / r.Height : 1.0;
            double k = Math.Min(kx, ky);
            if (k >= 1.0) return false;

            // 拿 view 当前 scale(W6 时 memory 记错"无 interop",实际 IView::ScaleRatio 一直能用)
            // IView::ScaleRatio 返 double[2]={xScale, yScale},uniform 缩放时两值相等,取平均
            double currentScale = 1.0;
            try
            {
                object raw = view.ScaleRatio;
                if (raw is double[] scales && scales.Length >= 2)
                {
                    currentScale = (scales[0] + scales[1]) * 0.5;
                }
                else if (raw is double singleScale)
                {
                    currentScale = singleScale;
                }
                if (currentScale <= 1e-9) currentScale = 1.0;  // 防御:0 不会缩
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "GetScaleRatio 失败,view '{0}' 用 fallback 1.0", view.Name);
                currentScale = 1.0;
            }

            // 计算 new_scale,但不能低于 MinViewScale
            double newScale = currentScale * k;
            if (newScale < _options.MinViewScale)
            {
                // 还是不够,记 MinScaleClamped(F16 分页触发)
                Log.Info("View '{0}' 需缩 {1:F3}×才 fit,但 MinViewScale={2:F3} — 留给 F16 分页",
                    view.Name, k, _options.MinViewScale);
                stats.MinScaleClamped++;
                return false;
            }

            // 真缩:view.ScaleRatio = new double[] { newScale, newScale },更新 Rect
            try
            {
                view.ScaleRatio = new double[] { newScale, newScale };
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "SetScaleRatio({0}, {1}) 失败 — 留给 F16", newScale, newScale);
                stats.MinScaleClamped++;
                return false;
            }

            // 更新 element.Rect — 按新 scale 缩小 width/height,position(X,Y) 居中不变
            // 简化:view 中心不动,新 w/h = old w/h * (newScale/currentScale)
            double ratio = newScale / currentScale;
            var newRect = new LayoutRect(
                x: r.X,
                y: r.Y,
                width: r.Width * ratio,
                height: r.Height * ratio);
            e.Rect = newRect;

            Log.Info("View '{0}' 缩放: {1:F3}→{2:F3} (k={3:F3}, rect {4:F4}x{5:F4} → {6:F4}x{7:F4})",
                view.Name, currentScale, newScale, k,
                r.Width, r.Height, newRect.Width, newRect.Height);
            return true;
        }
    }

    /// <summary>
    /// W5.2 — F15 缩放结果。
    /// </summary>
    public class ScalingResult
    {
        public int IterationsUsed { get; set; }
        public int ScaledCount { get; set; }
        public int MinScaleClamped { get; set; }
        public bool FullyFits => MinScaleClamped == 0;
    }
}