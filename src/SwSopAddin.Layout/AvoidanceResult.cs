namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.1 — 避让结果统计。
    /// </summary>
    public class AvoidanceResult
    {
        /// <summary>实际跑过的迭代次数。</summary>
        public int IterationsUsed { get; set; }

        /// <summary>成功应用的位移次数。</summary>
        public int MovesApplied { get; set; }

        /// <summary>失败解决(达到上限或两个元素都不可移动)的次数。</summary>
        public int FailedResolutions { get; set; }

        /// <summary>避让结束后,剩余的碰撞对数(&gt;0 说明还没完全解干净)。</summary>
        public int RemainingCollisions { get; set; }

        /// <summary>全解干净了吗?</summary>
        public bool Clean => RemainingCollisions == 0;
    }
}