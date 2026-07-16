using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace SwSopAddin.Infrastructure
{
    /// <summary>
    /// 用户配置的根对象。结构对应技术方案 §5.4 的 JSON。
    /// 字段统一 PascalCase + JSON 序列化保持原名(camelCase 也可,这里偷个懒走 .NET 默认)。
    /// </summary>
    public class ConfigStore
    {
        public int SchemaVersion { get; set; } = 2;

        public ExplodeOptions Explode { get; set; } = new ExplodeOptions();
        public DrawingOptions Drawing { get; set; } = new DrawingOptions();
        public BalloonOptions Balloon { get; set; } = new BalloonOptions();
        public PdfOptions Pdf { get; set; } = new PdfOptions();
        public AiAdvisorOptions AiAdvisor { get; set; } = new AiAdvisorOptions();
        /// <summary>W11+ SOP 工程图整体布局配置(标题栏预留、iso 比例、BOM zone 等)。</summary>
        public LayoutOptionsConfig Layout { get; set; } = new LayoutOptionsConfig();

        public static ConfigStore LoadOrCreate()
        {
            AppPaths.EnsureDirs();
            var path = AppPaths.ConfigJson;

            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var cfg = JsonConvert.DeserializeObject<ConfigStore>(json);
                    if (cfg != null) return cfg;
                }
                catch (Exception)
                {
                    // 配置文件损坏 → 备份原件,写入默认值
                    try { File.Copy(path, path + ".broken." + DateTime.UtcNow.Ticks, false); }
                    catch { /* 备份失败不阻塞 */ }
                }
            }

            var fresh = new ConfigStore();
            fresh.Save();
            return fresh;
        }

        public void Save()
        {
            AppPaths.EnsureDirs();
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(AppPaths.ConfigJson, json);
        }
    }

    /// <summary>
    /// 爆炸视图风格。
    /// Legacy = W6 旧行为(所有零件沿单一方向等距推开);
    /// SmartHybrid = W10 混合智能(主体按位置发散 + 紧固件同轴均匀排开 / 径向成组)。
    /// 序列化成字符串(StringEnumConverter),config.json 里可读。
    /// 旧 config.json 无此字段时 → 反序列化保留 ExplodeOptions 构造默认值 SmartHybrid。
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ExplodeStyle
    {
        Legacy,
        SmartHybrid
    }

    public class ExplodeOptions
    {
        public double DefaultDistanceMm { get; set; } = 80;
        public string SkipPropertyName { get; set; } = "SOP_Skip";

        /// <summary>
        /// 组件名前缀过滤(只过滤命名规则,不是属性)。
        /// W6+ 改成空数组 — 默认不过滤(让 16 件等所有组件都进 explode 流程)。
        /// 用户在 config.json 里加前缀列表即可启用过滤(例如 ["FRAME_", "BASE_"])。
        /// </summary>
        public string[] SkipNamePrefixes { get; set; } = new string[0];

        // ===== W10 智能爆炸(SmartHybrid)参数 =====
        // 单位:凡带 Mm 后缀的是毫米(与 DefaultDistanceMm 一致),纯几何层内部换算成米(SW API 用米)。
        // 改 Style = Legacy 即可一键回退旧行为。

        /// <summary>爆炸风格。默认 SmartHybrid;改 Legacy 秒回退 W6 旧行为。</summary>
        public ExplodeStyle Style { get; set; } = ExplodeStyle.SmartHybrid;

        // --- 主体件发散 ---
        /// <summary>主体件基准爆炸距离 = 该系数 × 装配对角线。默认 0.35。</summary>
        public double BodyBaseDistanceFraction { get; set; } = 0.35;
        /// <summary>离心系数:离装配中心越远的件推得越远。默认 0.8。</summary>
        public double DistanceSpreadK { get; set; } = 0.8;
        /// <summary>大件阻尼:越大的件推得越近(防重叠)。默认 0.5,取值 [0,1)。</summary>
        public double SizeDampingK { get; set; } = 0.5;
        /// <summary>爆炸距离下限(mm)。默认 20。</summary>
        public double MinDistanceMm { get; set; } = 20;
        /// <summary>爆炸距离上限(mm)。默认 300。</summary>
        public double MaxDistanceMm { get; set; } = 300;
        /// <summary>质心与装配中心重合(0 向量)时的退化方向。默认 +Z。</summary>
        public double[] DefaultDivergeAxis { get; set; } = new double[] { 0, 0, 1 };

        // --- 紧固件分类 ---
        /// <summary>文件名含这些关键词(不分大小写)判为紧固件。</summary>
        public string[] FastenerNameKeywords { get; set; } =
            new[] { "bolt", "screw", "nut", "washer", "pin", "pillar", "bush", "guide",
                "螺栓", "螺钉", "螺母", "垫圈", "导柱", "导套", "gb" };
        /// <summary>长径比 ≥ 此值判为紧固件(细长件,如螺栓)。默认 3.0。</summary>
        public double FastenerAspectRatio { get; set; } = 3.0;
        /// <summary>体积占装配比 &lt; 此值才可能是紧固件。默认 0.02。</summary>
        public double FastenerVolumeFraction { get; set; } = 0.02;
        /// <summary>最长边 &lt; 此值(mm)且体积小 → 紧固件(螺母/垫圈)。默认 30。</summary>
        public double FastenerMaxSizeMm { get; set; } = 30;

        // --- 同轴堆叠 ---
        /// <summary>两轴夹角 &lt; 此值(度)视为同轴。默认 5。</summary>
        public double CoaxialAngleTolDeg { get; set; } = 5.0;
        /// <summary>质心到轴的垂距 &lt; 此值(mm)视为同轴。默认 5。</summary>
        public double CoaxialRadialTolMm { get; set; } = 5.0;
        /// <summary>同轴堆叠件沿轴的均匀间距(mm)。默认 20。</summary>
        public double CoaxialSpacingMm { get; set; } = 20;

        // --- 单件紧固件径向 ---
        /// <summary>不成组的单件紧固件径向外推距离 = 该系数 × 装配对角线。默认 0.15。</summary>
        public double FastenerRadialDistanceFraction { get; set; } = 0.15;

        /// <summary>
        /// 对未形成同轴堆叠的紧固件优先创建真正的 AddRadialExplodeStep。
        /// 组件缺少有效轴或发散实体时自动回退到线性步骤。
        /// </summary>
        public bool EnableRadialSteps { get; set; } = true;
    }

    /// <summary>
    /// W11+ SOP 工程图整体布局配置。SchemaVersion 2 兼容:旧 config.json 无此段 → 反序列化
    /// 用 LayoutOptionsConfig 构造默认值,等同用户未手填。SopWorkflow.BuildLayoutOptions
    /// 把它映射到 SwSopAddin.Layout.LayoutOptions(运行时 POCO)。
    ///
    /// 命名:用 LayoutOptionsConfig 区别于 SwSopAddin.Layout.LayoutOptions(后者是
    /// LayoutService 吃的运行时类型,Infrastructure 不能反向依赖 Layout)。
    /// </summary>
    public class LayoutOptionsConfig
    {
        // ===== 标题栏禁区 =====
        /// <summary>A3 标题栏物理宽度(米)。</summary>
        public double TitleBlockWidthMeters { get; set; } = 0.18;
        /// <summary>A3 标题栏物理高度(米)。</summary>
        public double TitleBlockHeightMeters { get; set; } = 0.05;

        // ===== iso 主视图 =====
        /// <summary>iso 目标高度占可用 sheet 高度的比例(0..1)。</summary>
        public double IsoViewHeightFraction { get; set; } = 0.7;
        /// <summary>iso 最终物理高度下限(米),F15 safety。</summary>
        public double IsoMinHeightMeters { get; set; } = 0.05;

        // ===== BOM zone =====
        /// <summary>BOM 预留宽度(sheet 右侧、标题栏左侧的竖直条带宽度,米)。</summary>
        public double BomReservedWidthMeters { get; set; } = 0.16;
        /// <summary>BOM 顶部预留高度(sheet 顶部往下多少米内不放 BOM,米)。</summary>
        public double BomReservedHeightMeters { get; set; } = 0.07;

        // ===== 图纸规格 =====
        /// <summary>图纸规格,A0/A1/A2/A3/A4。GetSheetBounds 用。</summary>
        public string PaperSize { get; set; } = "A3";
    }

    public class DrawingOptions
    {
        public string TemplatePath { get; set; } = @"D:\Templates\SOP_A3.drwdot";
        public string PaperSize { get; set; } = "A3";
    }

    public class BalloonOptions
    {
        public string Style { get; set; } = "Circle";
        public string ArrowStyle { get; set; } = "Filled";
    }

    public class PdfOptions
    {
        public string OutputDir { get; set; } = @"D:\SOP_Output";
        public bool EmbedFonts { get; set; } = true;
    }

    public class AiAdvisorOptions
    {
        public bool Enabled { get; set; } = false;
        public string BaseUrl { get; set; } = "https://api.minimaxi.com/anthropic";

        /// <summary>
        /// 明文 API key,写进 %AppData%\SwSopAddin\config.json。
        /// 生产建议:改用 ANTHROPIC_API_KEY 环境变量(走 EffectiveApiKey),把这里的值清空。
        /// </summary>
        public string ApiKey { get; set; } = "";

        public string Model { get; set; } = "MiniMax-M3";
        public int MaxRounds { get; set; } = 3;

        /// <summary>
        /// AI 重建路径会先删除旧 explode step，再调用 COM 重建。不同装配体上重建尚不可靠，
        /// 默认只保留评估结果，禁止它修改模型；验证完成后可在配置中显式开启。
        /// </summary>
        public bool ApplyChanges { get; set; } = false;

        /// <summary>
        /// 实际用的 API key — 优先读 ANTHROPIC_API_KEY 环境变量,fallback 到明文 ApiKey。
        /// W6+:明文 ApiKey 即将废弃,留作本地开发 / 旧配置兼容。
        /// 调用方统一走 EffectiveApiKey,不要直接读 ApiKey。
        /// </summary>
        public string EffectiveApiKey
        {
            get
            {
                string envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                if (!string.IsNullOrEmpty(envKey)) return envKey;
                return ApiKey ?? "";
            }
        }

        /// <summary>
        /// EffectiveApiKey 的来源 — 方便诊断(用户问"为啥我改了 config.json 的 ApiKey 没生效"时直接打印)。
        /// 不在 JSON 里持久化,只读自 env + this。
        /// </summary>
        public string ApiKeySource
        {
            get
            {
                string envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                if (!string.IsNullOrEmpty(envKey)) return "env:ANTHROPIC_API_KEY";
                if (!string.IsNullOrEmpty(ApiKey)) return "config.json:ApiKey";
                return "(none)";
            }
        }
    }
}
