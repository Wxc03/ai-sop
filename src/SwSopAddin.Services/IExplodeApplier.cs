namespace SwSopAddin.Services
{
    /// <summary>
    /// Part A Phase 1 — SmartHybrid 爆炸引擎的落位执行器抽象。
    /// 照抄 IExplodeStepEditor 的测试化范式:组件句柄用 object(不是 IComponent2),
    /// 让 RecordingExplodeApplier 测试 fake 不必依赖真 COM RCW。
    /// </summary>
    internal interface IExplodeApplier
    {
        /// <summary>按组件名(优先)或原始索引(兜底)找到组件句柄。找不到返回 null。</summary>
        object ResolveComponent(string componentName, int index);

        /// <summary>
        /// 把 ExplodeLayoutPlanner 算出的方向 + 距离应用到组件上,产出一个真实 ExplodeStep。
        /// 成功时 stepName 是新建 step 的名字(供日志/后续引用);失败返 false。
        /// </summary>
        bool ApplyPlacement(object component, double[] direction, double distanceMeters, out string stepName);
    }

    /// <summary>
    /// 可选能力：把未同轴分组的紧固件创建为真正的径向 explode step。
    /// 不是所有装配都提供可选的轴/发散实体，因此调用方仍可使用 IExplodeApplier 的线性路径。
    /// </summary>
    internal interface IRadialExplodeApplier : IExplodeApplier
    {
        bool ApplyRadialPlacement(object component, ExplodePlacement placement, out string stepName);
    }
}
