using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

namespace SwSopAddin.Services
{
    /// <summary>
    /// W7+ ApplyRebuild 抽象 — 把"删 + 加 explode step"的 COM 序列抽成 3 个方法,
    /// 让 ApplyRebuild 主体只负责编排(遍历 / try/finally / 计数),不直接调 COM。
    /// 测试用 fake IExplodeStepEditor 验证调用顺序和失败恢复逻辑,不需真 SW。
    /// </summary>
    public interface IExplodeStepEditor
    {
        /// <summary>
        /// 拿 stepIndex 位置的 explode step 关联的 component。null 跳过此 change。
        /// 返 object(不是 IComponent2)是为了让测试能传任意对象(避免测试代码依赖 COM RCW)。
        /// 生产实现 SwExplodeStepEditor 内部 cast 到 IComponent2。
        /// </summary>
        object GetComponentForStep(int stepIndex);

        /// <summary>删 stepIndex 位置的 explode step。返 false 跳过此 change。</summary>
        bool TryDeleteStep(int stepIndex);

        /// <summary>选 component + 加新 explode step。返 false 跳过此 change。
        /// component 必须由 GetComponentForStep 返回(测试场景里要保持身份)。</summary>
        bool TryAddStep(object component, double distance, bool reverse);
    }

    /// <summary>
    /// IExplodeStepEditor 的 SW 2024 interop 生产实现。
    /// 持 AssemblyDoc + IConfiguration 引用,按 SW API 文档调 COM。
    /// 注意:不持有 RCW 长期引用 — 每次调完都 ReleaseComObject,避免跨调用持有 COM 内存。
    /// </summary>
    public class SwExplodeStepEditor : IExplodeStepEditor
    {
        private readonly AssemblyDoc _asm;
        private readonly IConfiguration _cfg;

        public SwExplodeStepEditor(AssemblyDoc asm, IConfiguration cfg)
        {
            _asm = asm ?? throw new System.ArgumentNullException(nameof(asm));
            _cfg = cfg ?? throw new System.ArgumentNullException(nameof(cfg));
        }

        public object GetComponentForStep(int stepIndex)
        {
            try
            {
                object stepObj = _cfg.IGetExplodeStep(stepIndex);
                if (stepObj == null) return null;
                IExplodeStep step = stepObj as IExplodeStep;
                if (step == null)
                {
                    Marshal.ReleaseComObject(stepObj);
                    return null;
                }
                IComponent2 comp = (IComponent2)step.GetComponent(0);
                // step 自身释放,comp 由 ApplyRebuild 释放(IComponent2 是新拿的,生命周期独立)
                Marshal.ReleaseComObject(step);
                return comp;
            }
            catch
            {
                return null;
            }
        }

        public bool TryDeleteStep(int stepIndex)
        {
            try
            {
                // 拿 step name — 重新 IGetExplodeStep(比缓存干净,避免悬挂引用)
                object stepObj = _cfg.IGetExplodeStep(stepIndex);
                if (stepObj == null) return false;
                IExplodeStep step = stepObj as IExplodeStep;
                if (step == null)
                {
                    Marshal.ReleaseComObject(stepObj);
                    return false;
                }
                string stepName = step.Name;
                Marshal.ReleaseComObject(step);
                if (string.IsNullOrEmpty(stepName)) return false;
                return _cfg.DeleteExplodeStep(stepName);
            }
            catch
            {
                return false;
            }
        }

        public bool TryAddStep(object component, double distance, bool reverse)
        {
            IComponent2 comp = component as IComponent2;
            if (comp == null) return false;
            try
            {
                // 选 component 前先清空选择
                try { ((ModelDoc2)_asm).ClearSelection2(true); } catch { /* 清失败不阻塞 */ }
                if (!comp.Select2(false, 0)) return false;
                // AI response distance is expressed in mm, while the SW API takes meters.
                ExplodeStep newStep = (ExplodeStep)_cfg.IAddExplodeStep(distance / 1000.0, reverse, false, false);
                if (newStep == null) return false;
                Marshal.ReleaseComObject(newStep);  // 不持有 — ApplyRebuild 拿完就行
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
