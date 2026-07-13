namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.1 — 布局选项(单位米)。独立 POCO,不依赖 ConfigStore,方便单元测试和复用。
    /// </summary>
    public class LayoutOptions
    {
        /// <summary>元素之间最小间距(米)。默认 3mm = 0.003。</summary>
        public double PaddingMeters { get; set; } = 0.003;

        /// <summary>避让算法最大迭代次数。超限就停,即使还有碰撞。</summary>
        public int MaxIterations { get; set; } = 50;

        /// <summary>单次移动是否允许跨过 sheet 边界(超界留给 F15 处理)。默认 true,先内移,不缩放。</summary>
        public bool AllowOutOfBoundsMoves { get; set; } = true;

        /// <summary>
        /// 球标默认尺寸估算(SW 圆形球标 ~3mm 直径,SW 方形 ~4mm)。
        /// BalloonPosition API 只给位置,不给 bounding box,这里用默认估算。
        /// </summary>
        public double BalloonSizeMeters { get; set; } = 0.004;

        // ====== W5.2 F15:超界自动缩放 ======

        /// <summary>是否启用 F15:view 超出 sheet 时按比例缩小。默认 true。</summary>
        public bool AutoScaleToFit { get; set; } = true;

        /// <summary>view 缩放下限(默认 0.1 = 10%)。低于此不再缩,F16 接管分页。</summary>
        public double MinViewScale { get; set; } = 0.1;

        /// <summary>view 与 sheet 边界的最小留白(米,默认 5mm)。</summary>
        public double SheetMarginMeters { get; set; } = 0.005;

        /// <summary>图纸规格,GetSheetBounds 用(SW 2024 interop 缺 GetSheetWidth,只能走配置推算)。</summary>
        public string PaperSize { get; set; } = "A3";

        // ====== W11 智能布局(SopLayoutPlanner) ======

        /// <summary>
        /// 标题栏物理宽度(米)。A3 默认 0.18m,A4 等其他规格按比例缩。
        /// 标题栏贴 sheet 右下角,BOM 不能压在上面。
        /// </summary>
        public double TitleBlockWidthMeters { get; set; } = 0.18;

        /// <summary>标题栏物理高度(米)。A3 默认 0.05m。</summary>
        public double TitleBlockHeightMeters { get; set; } = 0.05;

        /// <summary>
        /// iso 主视图目标高度占"可用 sheet 高度"的比例。
        /// "可用高度"= sheet.H - 标题栏高 - 上下留白。默认 0.7(70%)。
        /// 改 0.5=iso 更小、留更多 BOM / 标题栏空间;改 0.9=iso 几乎占满。
        /// </summary>
        public double IsoViewHeightFraction { get; set; } = 0.7;

        /// <summary>
        /// BOM 预留宽度(sheet 右侧、标题栏左侧的竖直条带宽度,米)。
        /// A3 默认 0.16m,容纳 4 行 BOM 不挤。
        /// </summary>
        public double BomReservedWidthMeters { get; set; } = 0.16;

        /// <summary>
        /// BOM 顶部预留高度(sheet 顶部往下多少米内不放 BOM,米)。
        /// 默认 0.07m,让 iso 居中后跟 BOM 不撞。
        /// </summary>
        public double BomReservedHeightMeters { get; set; } = 0.07;

        /// <summary>
        /// iso 主视图最终物理高度下限(米)。F15 safety:M4 算比例后若仍小到这个值,
        /// F15 才接管强制放大 / 整图重排;默认 0.05m。
        /// </summary>
        public double IsoMinHeightMeters { get; set; } = 0.05;

        public static LayoutOptions Default => new LayoutOptions();
    }
}