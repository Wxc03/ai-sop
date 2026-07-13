using SolidWorks.Interop.sldworks;

namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.1 — 智能布局服务入口接口。
    /// 流程:采集 (View/Balloon/BOM 包围盒) → 检测碰撞 → 避让 → 回写。
    /// W5.2 加 F15:超界 view 自动缩放 → 再避让。
    /// </summary>
    public interface ILayoutService
    {
        /// <summary>
        /// 对当前工程图执行 F14 碰撞检测 + 避让 + F15 超界缩放。
        /// - drw:目标工程图文档
        /// - views:参与布局的视图(M4 阶段返回的 views)
        /// - bomTable:BOM(M6 阶段产生;可为空)
        /// W6-fix:balloons 暂不传 — SW 2024 interop 没暴露 BalloonAnnotation 强类型,
        /// AutoBalloon5 返 untyped object,无法在 LayoutService 里安全采集包围盒。
        /// 等 M4 真修好 + 走反射或 view.GetBalloons() 后再加。
        /// 返回 LayoutResult 包含是否完全解干净 + 统计信息。
        /// </summary>
        /// Part B(多 sheet 架构就绪化):targetSheet 默认 null,今天单 sheet 流程零行为变化。
        /// 非 null 时布局按该 sheet 的尺寸计算(GetSheetBounds 用 targetSheet 而非当前活动 sheet)。
        LayoutResult ApplyLayout(
            DrawingDoc drw,
            View[] views,
            BomTableAnnotation bomTable,
            LayoutOptions options = null,
            Sheet targetSheet = null);

        /// <summary>
        /// 只做检测,不避让不写回 — 用于 dry-run / 日志。
        /// </summary>
        CollisionReport DetectOnly(
            DrawingDoc drw,
            View[] views,
            BomTableAnnotation bomTable,
            LayoutOptions options = null);

        /// <summary>
        /// Phase 3 — 把 iso 主视图自动居中 + 按 IsoViewHeightFraction 缩放。
        /// 调用 SopLayoutPlanner.Compute 算目标矩形,再把 ScaleRatio/Position 写回 isoView。
        /// 失败(读不到包围盒/planner 没产出 IsoTarget/COM 写异常)一律返回 Success=false,不 throw —
        /// 不能因为这个新逻辑失败拖垮 Step 3 主流程。
        /// </summary>
        /// Part B(多 sheet 架构就绪化):targetSheet 默认 null,今天单 sheet 流程零行为变化。
        IsoPlacementResult ApplyIsoPlacement(
            DrawingDoc drw,
            View isoView,
            LayoutOptions options = null,
            Sheet targetSheet = null);
    }

    /// <summary>Phase 3 — iso 视图自动布局结果,供 SopWorkflow 记日志/debug。</summary>
    public class IsoPlacementResult
    {
        public bool Success { get; set; }
        public LayoutRect? IsoTargetRect { get; set; }
        public double AppliedScaleRatio { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>
    /// W5.1 — 布局结果总览(给 Orchestration / UI 用)。
    /// </summary>
    public class LayoutResult
    {
        public bool Success { get; set; }
        public int ElementsCollected { get; set; }
        public int ElementsApplied { get; set; }

        /// <summary>F15 缩放结果(null 表示未启用)。</summary>
        public ScalingResult Scaling { get; set; }

        /// <summary>F14 避让结果。</summary>
        public AvoidanceResult Avoidance { get; set; }

        /// <summary>F14+F15 全干净吗?</summary>
        public bool FullyResolved => Avoidance != null && Avoidance.Clean;

        /// <summary>F15 触发过吗?</summary>
        public bool ScalingTriggered => Scaling != null && Scaling.ScaledCount > 0;

        public string Notes { get; set; }
    }

    /// <summary>
    /// W5.1 — 只检测不避让的报告。
    /// </summary>
    public class CollisionReport
    {
        public int ElementsScanned { get; set; }
        public int CollisionCount { get; set; }
        public string Summary { get; set; }
    }
}