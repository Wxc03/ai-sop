using System;
using System.IO;
using NLog;
using SolidWorks.Interop.sldworks;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Services
{
    /// <summary>
    /// M6 明细表 — W3.3 MVP。
    /// 用 IView.InsertBomTable4 在指定视图上插标准 BOM(BOM Type 0 = 仅顶层零件清单)。
    /// 默认锚定到图纸右下角(AnchorType=4)。
    /// W3.3 不取行数(留给 W4 智能布局阶段)。
    /// </summary>
    public class BomService : IBomService
    {
        private static readonly Logger Log = Logging.ForType(typeof(BomService));

        // SW 锚点枚举常用 int 值
        // 1=top-left, 2=top-right, 3=bottom-left, 4=bottom-right
        // W6-fix 批 4:BottomRight=4 不行(sheet 右下角是标题栏),改用 TopLeft=1 + X=0.05 Y=0.27
        private const int AnchorTopLeft = 1;
        // BOM 类型:0=标准零件清单,1=仅数量(顶层),2=缩进清单
        // W6-fix:宏录制用 TopLevelOnly=1
        private const int BomTypeTopLevelOnly = 1;
        // W6-fix:SW 自带 BOM 模板路径(宏录制里就是这个能 work)
        private const string BomTemplateFileName = "New BOM template.sldbomtbt";
        private const string ProjectBomTemplatePath = @"D:\Source\SwSopAddin\New BOM template.sldbomtbt";

        public BomInsertResult ApplyBomTable(DrawingDoc drw, View targetView, string configuration = "")
        {
            if (drw == null) throw new ArgumentNullException(nameof(drw));
            if (targetView == null) throw new ArgumentNullException(nameof(targetView));

            // SolidWorks hosts the add-in and sets AppDomain.BaseDirectory to its own install
            // folder.  Resolve from this assembly instead, so the template travels with the
            // registered add-in on every machine.
            string assemblyDirectory = Path.GetDirectoryName(typeof(BomService).Assembly.Location);
            string deployedTemplatePath = Path.Combine(assemblyDirectory, BomTemplateFileName);
            string templatePath = File.Exists(ProjectBomTemplatePath)
                ? ProjectBomTemplatePath
                : deployedTemplatePath;
            if (!File.Exists(templatePath))
                throw new FileNotFoundException("Configured BOM template was not found.", templatePath);
            Log.Info("ApplyBomTable: view='{0}', config='{1}', template='{2}'", targetView.Name, configuration ?? "", templatePath);

            // W6-fix 批 4:回退到宏里原位置 (0.234, 0.268) — SW 工程师常用的"右上方"
            //   BenchVice 重叠问题单独处理(缩 view 或加 F16)
            BomTableAnnotation bom = (BomTableAnnotation)targetView.InsertBomTable4(
                UseAnchorPoint: false,
                X: 0.234,
                Y: 0.145,
                AnchorType: AnchorTopLeft,
                BomType: BomTypeTopLevelOnly,
                Configuration: configuration ?? "",
                TableTemplate: templatePath,
                Hidden: false,
                IndentedNumberingType: 0,
                DetailedCutList: false);

            if (bom == null)
            {
                Log.Warn("InsertBomTable4 返回 null — BOM 插入失败 (template='{0}')", templatePath);
                return new BomInsertResult { Success = false, TemplateUsed = templatePath };
            }

            Log.Info("BOM table inserted: view='{0}', template='{1}'", targetView.Name, templatePath);
            return new BomInsertResult
            {
                Success = true,
                TemplateUsed = templatePath,
                BomTable = bom,
            };
        }
    }
}
