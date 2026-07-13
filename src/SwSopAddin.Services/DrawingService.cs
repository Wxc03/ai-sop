using System;
using System.IO;
using NLog;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Services
{
    /// <summary>
    /// M3 工程图生成 — W2.3 MVP。
    /// 只负责"从模板新建工程图"这一步。
    /// W2.4 Orchestration 会调它,之后 W3 加"绑装配体"+"插入视图"。
    /// </summary>
    public class DrawingService : IDrawingService
    {
        private static readonly Logger Log = Logging.ForType(typeof(DrawingService));

        public DrawingDoc NewFromTemplate(ISldWorks sw, ConfigStore config, out string actualTemplatePath)
        {
            if (sw == null) throw new ArgumentNullException(nameof(sw));
            if (config == null) throw new ArgumentNullException(nameof(config));

            // W4.2:在 ResolveTemplatePath 之前 cache paperSize,让模板选择跟图纸尺寸对齐
            _lastPaperSizeHint = config.Drawing.PaperSize;

            string templatePath = ResolveTemplatePath(config.Drawing.TemplatePath);
            actualTemplatePath = templatePath;
            int paperSize = ParsePaperSize(config.Drawing.PaperSize);

            Log.Info("DrawingService.NewFromTemplate: template='{0}', paperSize={1} ({2})",
                templatePath, paperSize, config.Drawing.PaperSize);

            ModelDoc2 newDoc = (ModelDoc2)sw.NewDocument(templatePath, paperSize, 0, 0);
            if (newDoc == null)
            {
                throw new InvalidOperationException(
                    "NewDocument 返回 null。请检查模板路径和图纸规格: '" + templatePath + "', size=" + config.Drawing.PaperSize);
            }

            DrawingDoc drw = (DrawingDoc)newDoc;
            Log.Info("Drawing created: title='{0}'", newDoc.GetTitle());

            // 防御性显式激活:分步执行模式下,RunStep(3..7) 依赖 sw.ActiveDoc 就是这份新工程图。
            // NewDocument 通常会自动把新文档设为 ActiveDoc,但不无条件信任隐式行为——
            // 显式 ActivateDoc 一次,成本很低,能兜住偶发的“新建后未真正激活”情况。
            try { sw.ActivateDoc(newDoc.GetTitle()); }
            catch (Exception ex) { Log.Warn(ex, "NewFromTemplate: 显式 ActivateDoc 失败(不影响返回值)"); }

            return drw;
        }

        /// <summary>
        /// 检查模板存在;不存在时回退到 SW 自带的工程图模板。
        /// W4.2 修复:SW 真正"内嵌"的工程图模板在 C:\ProgramData\SolidWorks\SolidWorks 2024\templates\gb_aX.drwdot(国标 A0-A4 各种尺寸)
        /// D:\SW\SOLIDWORKS\data\templates\*.drwdot 那些只有 7 个国家的 default(空架子),没标题栏边框
        /// </summary>
        private static string ResolveTemplatePath(string configured)
        {
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                return configured;

            // W4.2:优先用 SW ProgramData 下的真工程图模板(带国标标题栏边框)
            // 用户 PaperSize="A3" → 用 gb_a3.drwdot;其他 A* 同理
            string paperSize = _lastPaperSizeHint ?? "A3";
            string aN = paperSize.Trim().ToUpper();
            string preferredDrwdot = $@"C:\ProgramData\SolidWorks\SolidWorks 2024\templates\gb_{aN.ToLower()}.drwdot";

            // 候选列表(按优先级)
            string[] candidates =
            {
                preferredDrwdot,                                                 // 1. 国标 A* (PaperSize 决定)
                @"C:\ProgramData\SolidWorks\SolidWorks 2024\templates\gb_a3.drwdot", // 2. 国标 A3 fallback
                // 退路:SW 7 国 default 模板(空架子,没标题栏)
                @"D:\SW\SOLIDWORKS\data\templates\gb.drwdot",
                @"D:\SW\SOLIDWORKS\data\templates\iso.drwdot",
                @"D:\SW\SOLIDWORKS\data\templates\din.drwdot",
                @"D:\SW\SOLIDWORKS\data\templates\ansi.drwdot",
                @"D:\SW\SOLIDWORKS\data\templates\bsi.drwdot",
                @"D:\SW\SOLIDWORKS\data\templates\jis.drwdot",
                @"D:\SW\SOLIDWORKS\data\templates\gost.drwdot",
            };
            foreach (var p in candidates)
            {
                if (File.Exists(p))
                {
                    Log.Warn("配置的模板 '{0}' 找不到,回退到 '{1}'", configured, p);
                    return p;
                }
            }
            throw new FileNotFoundException(
                "Drawing template not found. 配置的: '" + configured +
                "',且 SW 多个目录都未找到 .drwdot。请修改 config.json 的 Drawing.TemplatePath。", configured);
        }

        // W4.2 修法:把 paperSize 缓存给 ResolveTemplatePath 用
        private static string _lastPaperSizeHint;

        private static int ParsePaperSize(string s)
        {
            if (string.IsNullOrEmpty(s)) return (int)swDwgPaperSizes_e.swDwgPaperA3size;
            switch (s.Trim().ToUpper())
            {
                case "A0": return (int)swDwgPaperSizes_e.swDwgPaperA0size;
                case "A1": return (int)swDwgPaperSizes_e.swDwgPaperA1size;
                case "A2": return (int)swDwgPaperSizes_e.swDwgPaperA2size;
                case "A3": return (int)swDwgPaperSizes_e.swDwgPaperA3size;
                case "A4": return (int)swDwgPaperSizes_e.swDwgPaperA4size;
                case "A4V": return (int)swDwgPaperSizes_e.swDwgPaperA4sizeVertical;
                default:
                    Log.Warn("未知图纸规格 '{0}',回退 A3", s);
                    return (int)swDwgPaperSizes_e.swDwgPaperA3size;
            }
        }
    }
}
