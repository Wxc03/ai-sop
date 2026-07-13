using SolidWorks.Interop.sldworks;
using SwSopAddin.Infrastructure;
using SwSopAddin.Layout;

namespace SwSopAddin.Orchestration
{
    /// <summary>SOP 生成总流程接口。Host 的 OnGenerate 调这个。</summary>
    public interface ISopWorkflow
    {
        /// <summary>
        /// W3 版:Step1=爆炸, Step2=建工程图, Step3=插爆炸等轴测视图,
        ///        Step4=AutoBalloon 球标, Step5=插 BOM, Step6=工程图自动存盘。
        /// 失败会回滚已做的临时改动。
        /// </summary>
        SopResult RunMvp(ISldWorks sw, ConfigStore config);
    }

    /// <summary>运行结果汇总。供 Host 显示给用户 + 写日志。</summary>
    public class SopResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        // Step 1 爆炸
        public string ExplodedViewName { get; set; }
        public int ExplodeStepCount { get; set; }
        public int SkippedComponentCount { get; set; }

        // Step 2 工程图
        public string DrawingTemplateUsed { get; set; }
        public string DrawingSavedPath { get; set; }   // W3.4 新增:自动存盘路径

        // Step 3-5
        public bool IsoViewInserted { get; set; }     // W3.4 新增
        public int BalloonCount { get; set; }         // W3.4 新增
        public bool BomInserted { get; set; }         // W3.4 新增
        public LayoutRect? IsoTargetRect { get; set; } // Phase 3 新增:iso 视图自动布局目标矩形,供日志/debug

        // Step 7 — W6-fix W5 wiring 智能布局
        public bool LayoutApplied { get; set; }
        public int LayoutElementsCollected { get; set; }
        public int LayoutElementsApplied { get; set; }
        public int LayoutRemainingCollisions { get; set; }
        public string LayoutNotes { get; set; }

        // Step 1.5 — AI 爆炸评估(可选)
        public bool AiAdvisorEnabled { get; set; }
        public int AiAdvisorRounds { get; set; }
        public int AiAdvisorStepChanges { get; set; }
        public string AiAdvisorSkippedReason { get; set; }
        public string AiAdvisorLastError { get; set; }

        public string SummaryForUser()
        {
            if (!Success)
            {
                return "[SOP 生成失败]\n\n" + (ErrorMessage ?? "未知错误") +
                       "\n\n日志: %AppData%\\SwSopAddin\\logs";
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[SOP 生成成功]");
            sb.AppendLine();
            sb.AppendLine("爆炸视图: " + (ExplodedViewName ?? "(未创建)"));
            sb.AppendLine("爆炸步骤: " + ExplodeStepCount + " (跳过 " + SkippedComponentCount + " 个)");
            sb.AppendLine();
            sb.AppendLine("工程图:   " + (DrawingSavedPath ?? "(未保存到磁盘 — 见 SW 当前窗口)"));
            sb.AppendLine("模板:     " + (DrawingTemplateUsed ?? "(未指定)"));
            sb.AppendLine("等轴测:   " + (IsoViewInserted ? "已插入" : "未插入"));
            sb.AppendLine("球标:     " + (BalloonCount > 0 ? BalloonCount + " 个" : "(无)"));
            sb.AppendLine("BOM:      " + (BomInserted ? "已插入" : "未插入"));
            if (LayoutApplied)
            {
                sb.AppendLine("布局:     " + (LayoutNotes ?? "已应用"));
                if (LayoutRemainingCollisions > 0)
                    sb.AppendLine("          ⚠ 仍有 " + LayoutRemainingCollisions + " 对碰撞未解");
            }
            if (AiAdvisorEnabled)
            {
                if (!string.IsNullOrEmpty(AiAdvisorSkippedReason))
                    sb.AppendLine("AI 评估:  跳过 — " + AiAdvisorSkippedReason);
                else if (!string.IsNullOrEmpty(AiAdvisorLastError))
                    sb.AppendLine("AI 评估:  失败 — " + AiAdvisorLastError);
                else
                    sb.AppendLine("AI 评估:  " + AiAdvisorRounds + " 轮,改了 " + AiAdvisorStepChanges + " 个 step");
            }
            sb.AppendLine();
            sb.AppendLine("W6 起 W5 智能布局已接入(碰撞避让 + 超界缩放);F16 分页待实现。");
            return sb.ToString();
        }
    }
}
