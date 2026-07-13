using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using SolidWorks.Interop.sldworks;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.1 — 把布局结果写回 SW COM 对象。
    /// 全部 try/catch:某个元素回写失败不影响其他元素。
    ///
    /// COM 写回策略:
    /// - View.Position = [X, Y, 0] — SW 把视图底边原点平移到 (X, Y),视图内部内容等比缩放跟随
    /// - BalloonAnnotation.BalloonPosition = [X, Y, 0] — 球标位置(中心)平移
    /// - BomTableAnnotation — 不写回(锚定元素,在 W5.1 不动;真要移动需要用 GetTotalBoundingBox 之外的方式,留给 W5.3)
    /// </summary>
    public class LayoutApplier
    {
        private static readonly Logger Log = Logging.ForType(typeof(LayoutApplier));

        public int Apply(IEnumerable<LayoutElement> elements)
        {
            if (elements == null) return 0;
            int applied = 0;
            foreach (var e in elements)
            {
                if (e == null || e.ComRef == null) continue;
                try
                {
                    if (TryApplyOne(e)) applied++;
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Apply 元素 '{0}' (kind={1}) 失败", e.Label, e.Kind);
                }
            }
            Log.Info("LayoutApplier: 应用 {0} / {1} 个元素", applied, elements.Count());
            return applied;
        }

        private static bool TryApplyOne(LayoutElement e)
        {
            switch (e.Kind)
            {
                case LayoutElementKind.View:
                    return TryApplyView(e);
                case LayoutElementKind.Balloon:
                    // W6-fix:球标回写暂禁用(SW 2024 interop 没强类型,走反射后续再加)
                    Log.Info("Balloon '{0}' 暂不回写(SW 2024 限制)", e.Label);
                    return false;
                case LayoutElementKind.BomTable:
                    // BomTable 锚定,不写回
                    Log.Info("BomTable '{0}' 锚定不动", e.Label);
                    return false;
                default:
                    Log.Warn("Apply: 未知 kind={0}", e.Kind);
                    return false;
            }
        }

        private static bool TryApplyView(LayoutElement e)
        {
            if (!(e.ComRef is View v))
            {
                Log.Warn("TryApplyView: ComRef 类型错 {0}", e.ComRef?.GetType().FullName ?? "null");
                return false;
            }
            try
            {
                // W6-fix:view 默认位置 P6 (V3 *等轴测) 放 (0.0158, 0.1114) 偏左下。
                // 没碰撞时强制居中(0.15, 0.15 米 = 模板中央偏左,留 BOM 表在右上空间)。
                double x, y;
                if (e.Rect.X < 0.05 || e.Rect.X > 0.35)
                {
                    x = 0.15;
                }
                else
                {
                    x = e.Rect.X;
                }
                if (e.Rect.Y < 0.05 || e.Rect.Y > 0.25)
                {
                    y = 0.15;
                }
                else
                {
                    y = e.Rect.Y;
                }
                // W10+ 修复:set_Position(Object) 在 SW 2024 是 stub,设了 view 不移动。
                // 改用 set_IPosition(ref x); set_IPosition(ref y) 单值设,可能 work。
                try
                {
                    double xx = x, yy = y;
                    v.set_IPosition(ref xx);
                    v.set_IPosition(ref yy);
                    Log.Info("View '{0}' set_IPosition x={1:F4} y={2:F4}", v.Name, xx, yy);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "set_IPosition 失败,fallback set_Position");
                    v.Position = new double[] { x, y, 0 };
                }
                Log.Info("View '{0}' Position -> [{1:F4},{2:F4}]", v.Name, x, y);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "TryApplyView '{0}' 失败", v.Name);
                return false;
            }
        }

        // W6-fix:Balloon 回写暂禁用。SW 2024 interop 没暴露 BalloonAnnotation 强类型,
        // 改回写需要反射 / dynamic,后续再加。
        // private static bool TryApplyBalloon(LayoutElement e) { ... }
    }
}