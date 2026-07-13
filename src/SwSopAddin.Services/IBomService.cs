using SolidWorks.Interop.sldworks;

namespace SwSopAddin.Services
{
    /// <summary>BOM 插入结果(轻量 DTO + COM 引用,给 LayoutService 用)。</summary>
    public class BomInsertResult
    {
        /// <summary>实际用的 BOM 模板名(空字符串表示 SW 默认)。</summary>
        public string TemplateUsed { get; set; }

        /// <summary>行数(W3.3 不去取,留给 W4 智能布局阶段)。</summary>
        public int RowCount { get; set; }

        /// <summary>插入成功了吗?</summary>
        public bool Success { get; set; }

        /// <summary>W6-fix:BOM COM 引用(给 LayoutService.BoundingBoxCollector 用)。调用方负责 ReleaseComObject。</summary>
        public BomTableAnnotation BomTable { get; set; }
    }

    /// <summary>M6 明细表接口 — W3.3 MVP。</summary>
    public interface IBomService
    {
        /// <summary>
        /// 在指定视图上插标准 BOM 表(右下方锚定)。
        /// 返回 DTO + COM 引用,caller 用完 BomTable 后记得 ReleaseComObject。
        /// </summary>
        BomInsertResult ApplyBomTable(DrawingDoc drw, View targetView, string configuration = "");
    }
}
