using SolidWorks.Interop.sldworks;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Services
{
    /// <summary>M3 工程图生成接口。W2.3 MVP 只做"从模板新建工程图"这一步;绑装配体 + 标题栏填充留 W3。</summary>
    public interface IDrawingService
    {
        /// <summary>
        /// 用 config.Drawing.TemplatePath(找不到则回退 SW 标准模板)创建一张新工程图。
        /// actualTemplatePath(out):实际使用的模板路径(W2.5 修:返给 SopResult 记录"真实用到的模板")。
        /// 失败抛异常,异常消息给用户看。调用方负责 ReleaseComObject 返回的 DrawingDoc。
        /// </summary>
        DrawingDoc NewFromTemplate(ISldWorks sw, ConfigStore config, out string actualTemplatePath);
    }
}
