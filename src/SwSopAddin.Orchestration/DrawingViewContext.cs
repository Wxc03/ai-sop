using SolidWorks.Interop.sldworks;

namespace SwSopAddin.Orchestration
{
    /// <summary>
    /// Part B(多 sheet 架构就绪化)—— RunMvp 单次 Step 3 构造,Step 4/5/6 共用,
    /// 取代各步骤各自重复调 TryGetFirstView/CollectMovableViews。
    /// 今天单 sheet 流程里 Sheet/SheetName/BalloonNumberOffset 恒为默认值(null/0),
    /// 只有 IsoView/AllViews/BomTable 被实际使用 —— 其余字段是为未来多 sheet 拆分留的占位。
    /// </summary>
    public class DrawingViewContext
    {
        public Sheet Sheet { get; set; }
        public string SheetName { get; set; }
        public View IsoView { get; set; }
        public View[] AllViews { get; set; }          // 未来:只含该 sheet 分组的 view
        public int BalloonNumberOffset { get; set; }   // 未来:承接上一个 sheet 的球标编号延续
        public BomTableAnnotation BomTable { get; set; }
    }
}
