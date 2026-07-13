using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Services
{
    /// <summary>M2 爆炸引擎接口。W2.2 MVP 版,只用配置常量距离,W3 起升级到智能距离推断。</summary>
    public interface IExplodeService
    {
        ExplodeResult Create(AssemblyDoc asm, ConfigStore config);
    }

    /// <summary>爆炸结果。调用方据此知道下一步该 ShowExploded 哪个视图。</summary>
    public class ExplodeResult
    {
        /// <summary>本次创建的爆炸视图名。约定:配置名 + "_SOP_Explode",W2.5 验证时能在 SW FeatureManager 里看到。</summary>
        public string ExplodedViewName { get; set; }

        /// <summary>成功添加的 ExplodeStep 数量(=被爆炸的非跳过组件数)。</summary>
        public int StepCount { get; set; }

        /// <summary>被过滤规则跳过的组件名列表(只记录名字用于日志,不在 SW 里显示)。</summary>
        public List<string> SkippedComponents { get; set; } = new List<string>();

        /// <summary>
        /// W6-fix:本次爆炸是否走了 AutoExplode fallback(SW 启发式,只 1 步)。
        /// true 时 StepCount 实际是 1 但精度低于手动 IAddExplodeStep。
        /// UI 可以用这个提示用户"启发式爆炸,精度有限"。
        /// </summary>
        public bool UsedAutoExplodeFallback { get; set; }

        /// <summary>
        /// W6-fix:本次爆炸是否走了 TranslateComponent fallback(选中组件 + 平移造真 step)。
        /// 比 AutoExplode 好 — AutoBalloon5 / InsertBomTable4 能找到组件。
        /// </summary>
        public bool UsedTranslateFallback { get; set; }
    }
}
