using System;
using SolidWorks.Interop.sldworks;

namespace SwSopAddin.Adapter
{
    /// <summary>
    /// IDocumentValidator 生产实现 — 包 SwApiWrapper.TryGetActiveAssembly + 翻译 enum。
    /// W8+:让 SopWorkflow 接受注入。生产路径跟 W6-fix 行为完全一致。
    /// </summary>
    public class SwDocumentValidator : IDocumentValidator
    {
        public ActivateOutcome TryGetActiveAssembly(ISldWorks sw, out AssemblyDoc asm, out string message)
        {
            asm = null;
            message = null;

            if (sw == null)
            {
                message = "SldWorks 句柄为空";
                return ActivateOutcome.NoDoc;
            }

            SwApiWrapper.ActivateResult r = SwApiWrapper.TryGetActiveAssembly(sw, out asm, out message);
            switch (r)
            {
                case SwApiWrapper.ActivateResult.Ok: return ActivateOutcome.Ok;
                case SwApiWrapper.ActivateResult.NoDoc: return ActivateOutcome.NoDoc;
                case SwApiWrapper.ActivateResult.WrongType: return ActivateOutcome.WrongType;
                default:
                    throw new InvalidOperationException("Unknown SwApiWrapper.ActivateResult: " + r);
            }
        }
    }
}
