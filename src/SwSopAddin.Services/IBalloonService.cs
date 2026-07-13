using SolidWorks.Interop.sldworks;

namespace SwSopAddin.Services
{
    /// <summary>M5 球标接口 — W3.2 MVP。</summary>
    public interface IBalloonService
    {
        /// <summary>
        /// 对指定视图跑 AutoBalloon5,批量打标。球标序号和 BOM 联动(自动)。
        /// 返回插入的球标数量。
        /// W6-fix 注:SW 2024 interop 没暴露 BalloonAnnotation 强类型,AutoBalloon5 返 object/untyped,
        /// 所以这里只返 count。LayoutService 通过 view reflection 拿球标位置(W7 再做)。
        ///
        /// Part B(多 sheet 架构就绪化):startNumber 默认 1,本轮只加参数占位不接线。
        /// TODO 未来打通跨 sheet 球标编号连续性时,需要找到 AutoBalloon5/CreateAutoBalloonOptions
        /// 或球标标注本身能设置起始编号的 API(目前未反射确认存在),把 startNumber 接上去。
        /// </summary>
        int ApplyAutoBalloon(DrawingDoc drw, View view, int startNumber = 1);
    }
}
