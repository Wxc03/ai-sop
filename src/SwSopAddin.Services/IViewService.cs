using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace SwSopAddin.Services
{
    /// <summary>M4 视图插入接口 — W3.1 MVP,W6-fix 多路径 fallback。</summary>
    public interface IViewService
    {
        /// <summary>
        /// 插"爆炸等轴测"主视图。
        /// W6-fix:新增 ISldWorks 参数,内部多路径 fallback:
        ///   P1 CreateDrawViewFromModelView2 *Isometric
        ///   P2 CreateDrawViewFromModelView2 *Front
        ///   P3 sw.ActivateDoc(asm) + sw.ActivateDoc(drw) + retry P1 (state transition)
        ///   P4 sw.LoadFile2(asm) 强制全加载 + retry P1
        ///   P5 InsertModelInPredefinedView
        ///   P6 CreateDrawViewFromModelView3 *等轴测 (中文,W6-fix 唯一 work 路径)
        ///   P6.5 V3 *前视
        ///   P7/P7b Create1stAngleViews2 (加正交视图)
        ///   P8 Create3rdAngleViews2 (备选)
        ///
        /// W7+:加 explodeViewName 参数 — 来自 ExplodeService.Create 实际切到的 asm explode view 名
        /// (SW 自动命名,可能是"爆炸视图2",不是预期的"_SOP_Explode_Translate")。
        /// null/空 才 fallback 到 asm.GetExplodedViewNames2 列表第一个。
        /// 返回 IView,调用方负责 ReleaseComObject。
        ///
        /// Part B(多 sheet 架构就绪化):targetSheet 默认 null,今天单 sheet 流程零行为变化。
        /// 非 null 时插视图前先 drw.ActivateSheet(targetSheet.GetName()),把插入目标切到指定 sheet
        /// (未来多 sheet 拆分时,每个 sheet 分组调一次本方法用不同 targetSheet)。
        /// </summary>
        View InsertExplodedIso(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, string explodeViewName, double x, double y, double z, Sheet targetSheet = null);

        /// <summary>
        /// 插"装配原始等轴测"辅视图(临时切回非爆炸状态插入,再恢复)。
        /// W3.4 编排时只用 ExplodedIso;OriginalIso 留给 W4 智能布局时用。
        /// </summary>
        View InsertOriginalIso(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, double x, double y, double z);

        /// <summary>
        /// W6-fix:诊断用 — 跑一遍所有路径但不真正创建视图,记录每条路径的成败。
        /// 给 UI / Log 提供"为什么 view 插不进去"的明细。
        /// </summary>
        ViewInsertDiagnostics Diagnose(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm);
    }

    /// <summary>W6-fix M4 诊断报告:每条路径的尝试结果。</summary>
    public class ViewInsertDiagnostics
    {
        public List<ViewPathAttempt> Attempts { get; } = new List<ViewPathAttempt>();
        public bool AnySucceeded => Attempts.Any(a => a.Succeeded);
        public string AsmTitle { get; set; }
        public string DrwTitle { get; set; }
        public int ViewCountBefore { get; set; }
        public int ViewCountAfter { get; set; }

        public override string ToString()
        {
            return $"M4 诊断:Asm='{AsmTitle}' Drw='{DrwTitle}' ViewCount {ViewCountBefore}->{ViewCountAfter}\n"
                + string.Join("\n", Attempts.Select((a, i) => $"  P{i+1} {a.PathName}: {a.Outcome}"));
        }
    }

    public class ViewPathAttempt
    {
        public string PathName { get; set; }
        public bool Succeeded { get; set; }
        public string Outcome { get; set; }
        public string ErrorDetail { get; set; }
    }
}
