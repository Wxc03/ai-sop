using System;
using SolidWorks.Interop.sldworks;

namespace SwSopAddin.Adapter
{
    /// <summary>
    /// W8+ :抽象 SW ActiveDoc 校验逻辑,让 SopWorkflow 可注入 mock。
    /// 之前的 Step 0 调 static SwApiWrapper.TryGetActiveAssembly 不可 mock,
    /// 所以 7 步 pipeline 测试只能写早退路径。注入 IDocumentValidator 后可以。
    ///
    /// 接口独立 enum (ActivateOutcome) — 不复用 SwApiWrapper.ActivateResult,
    /// 因为 SwApiWrapper.ActivateResult 是 static class 内部 enum,引用不友好。
    /// SwDocumentValidator 实现负责翻译。
    /// </summary>
    public interface IDocumentValidator
    {
        ActivateOutcome TryGetActiveAssembly(ISldWorks sw, out AssemblyDoc asm, out string message);
    }

    public enum ActivateOutcome
    {
        Ok,
        NoDoc,
        WrongType,
    }
}
