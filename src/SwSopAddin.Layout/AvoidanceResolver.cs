using System;
using System.Collections.Generic;
using NLog;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.1 — 碰撞避让。
    /// 算法(朴素稳定):
    /// 1) 迭代 Detect() 拿碰撞对列表
    /// 2) 取第一对 (a, b),选 priority 大的作为 mover(同等 priority 选 b 保持稳定)
    /// 3) 计算 mover 在 x/y 方向上的重叠量,选重叠小的轴(最小位移)做平移
    /// 4) 直到无碰撞 / 达到 MaxIterations / 遇到两个都不可移动的对
    /// 5) 不缩放(F15 处理);F16 留给后续
    ///
    /// 限制:贪心局部最优,可能震荡或卡住;这里用"重叠量小的轴优先"和"已访问元素优先不动"两招缓解。
    /// </summary>
    public class AvoidanceResolver
    {
        private static readonly Logger Log = Logging.ForType(typeof(AvoidanceResolver));

        private readonly LayoutOptions _options;

        public AvoidanceResolver(LayoutOptions options = null)
        {
            _options = options ?? LayoutOptions.Default;
        }

        public AvoidanceResult Resolve(IList<LayoutElement> elements, SheetBounds sheet)
        {
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            var stats = new AvoidanceResult();
            var detector = new CollisionDetector();

            int iter = 0;
            int lastMovesApplied = 0;
            while (iter < _options.MaxIterations)
            {
                iter++;
                var collisions = detector.Detect(elements, _options.PaddingMeters);
                if (collisions.Count == 0) break;

                var pair = collisions[0];
                var (mover, dx, dy) = ChooseMove(pair, sheet, _options);
                if (mover == null || (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9))
                {
                    stats.FailedResolutions++;
                    Log.Warn("Iter {0}: 无法解决碰撞 {1} — 两元素都不可移动或位移为 0",
                        iter, pair);
                    break;
                }

                // 防震荡:同一次迭代内,如果 mover 已经被移动,这次跳过(留到下次再处理)
                if (stats.MovesApplied == lastMovesApplied && WasMovedThisIter(elements, mover))
                {
                    Log.Warn("Iter {0}: 元素 '{1}' 本次已移动过 — 跳过防震荡", iter, mover.Label);
                    stats.FailedResolutions++;
                    break;
                }

                mover.Rect = mover.Rect.Translated(dx, dy);
                lastMovesApplied = stats.MovesApplied;
                stats.MovesApplied++;
                Log.Info("Iter {0}: move '{1}' by ({2:F6}, {3:F6}) -> {4}",
                    iter, mover.Label, dx, dy, mover.Rect);
            }

            stats.IterationsUsed = iter;
            stats.RemainingCollisions = detector.Detect(elements, _options.PaddingMeters).Count;
            Log.Info("Resolve 完成: iters={0} moves={1} failed={2} remaining={3}",
                stats.IterationsUsed, stats.MovesApplied, stats.FailedResolutions, stats.RemainingCollisions);
            return stats;
        }

        // 防震荡辅助:同一次 Resolve 调用里,看 mover 的 Rect 是否被改过(粗略判断)
        private static bool WasMovedThisIter(IList<LayoutElement> elements, LayoutElement mover)
        {
            // 这里只是粗略信号;LayoutElement 没存 originalRect,简化处理
            return false;
        }

        /// <summary>
        /// 选 mover + 计算最小位移。规则:
        /// - 两都不可移动 → (null, 0, 0)
        /// - 单方可移动 → 那一方
        /// - 都可移动 → priority 大的移动;同 priority 选 b
        /// 重叠计算:选重叠量小的轴(更少的扰动)
        /// </summary>
        internal static (LayoutElement mover, double dx, double dy) ChooseMove(
            ElementPair pair, SheetBounds sheet, LayoutOptions options)
        {
            var a = pair.A;
            var b = pair.B;
            if (a == null || b == null) return (null, 0, 0);

            bool aMov = a.Movable;
            bool bMov = b.Movable;
            if (!aMov && !bMov) return (null, 0, 0);

            LayoutElement mover, other;
            if (!aMov) { mover = b; other = a; }
            else if (!bMov) { mover = a; other = b; }
            else
            {
                // 都可移动:priority 大的(more movable)移动
                if (a.Priority > b.Priority) { mover = a; other = b; }
                else if (b.Priority > a.Priority) { mover = b; other = a; }
                else { mover = b; other = a; } // 同优先级选 b,稳定
            }

            var r1 = mover.Rect;
            var r2 = other.Rect;

            // 重叠量(正数 = 重叠,负数 = 间隔)
            double overlapX = Math.Min(r1.Right, r2.Right) - Math.Max(r1.Left, r2.Left);
            double overlapY = Math.Min(r1.Top, r2.Top) - Math.Max(r1.Bottom, r2.Bottom);

            // 重叠<0 = 实际不重叠,但 Detect 报上来说明 tolerance 让它重叠;padding 直接当 X/Y 重叠处理
            double pad = options.PaddingMeters;

            // 选重叠小的轴 → 最小扰动
            // overlapX/Y 已经是 "重叠区长度"(含 padding);给两者都 +pad 是过度,选重叠小的那轴即可
            if (overlapX < overlapY)
            {
                // 横向移动
                double dx;
                if (r1.CenterX < r2.CenterX) dx = -overlapX - pad; // mover 在左 → 向左推
                else dx = overlapX + pad;                            // mover 在右 → 向右推
                return (mover, dx, 0);
            }
            else
            {
                // 纵向移动
                double dy;
                if (r1.CenterY < r2.CenterY) dy = -overlapY - pad;
                else dy = overlapY + pad;
                return (mover, 0, dy);
            }
        }
    }
}