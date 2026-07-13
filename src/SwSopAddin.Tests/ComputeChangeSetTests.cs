using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSopAddin.Services;

namespace SwSopAddin.Tests
{
    /// <summary>
    /// AiExplodeAdvisor.ComputeChangeSet 纯逻辑测试。
    ///
    /// 范围:这是 POC 3 闭环的关键纯函数 — 把 AI 的 steps 转成 PendingChange 列表。
    /// 阈值规则:distance 差 > 0.5mm 算"变",reverse 严格相等才算"未变"。
    /// 越界 StepIndex 直接忽略(防御 AI 给的索引对不上,可能因为 snapshot 跟 asm 状态已经不同步)。
    /// </summary>
    [TestClass]
    public class ComputeChangeSetTests
    {
        private List<ExplodeStepSnapshot> _current;
        private List<AiExplodeStep> _aiSteps;

        [TestInitialize]
        public void Setup()
        {
            // 默认 3 个 step,AI 全部要求保持不变
            _current = new List<ExplodeStepSnapshot>
            {
                new ExplodeStepSnapshot { Index = 0, StepName = "Step1", ComponentName = "BASE", DistanceMm = 80, ReverseDir = false },
                new ExplodeStepSnapshot { Index = 1, StepName = "Step2", ComponentName = "SHAFT", DistanceMm = 50, ReverseDir = true },
                new ExplodeStepSnapshot { Index = 2, StepName = "Step3", ComponentName = "WHEEL", DistanceMm = 30, ReverseDir = false },
            };
            _aiSteps = new List<AiExplodeStep>
            {
                new AiExplodeStep { StepIndex = 0, DistanceMm = 80, Reverse = false, Reason = "no change" },
                new AiExplodeStep { StepIndex = 1, DistanceMm = 50, Reverse = true, Reason = "no change" },
                new AiExplodeStep { StepIndex = 2, DistanceMm = 30, Reverse = false, Reason = "no change" },
            };
        }

        [TestMethod]
        public void ComputeChangeSet_AllUnchanged_ReturnsEmpty()
        {
            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, _aiSteps);
            Assert.AreEqual(0, changes.Count, "AI 返的值跟当前完全一致时,0 变更");
        }

        [TestMethod]
        public void ComputeChangeSet_DistanceOverThreshold_GeneratesChange()
        {
            _aiSteps[0].DistanceMm = 100;  // 80 -> 100,差 20mm

            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, _aiSteps);

            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual(0, changes[0].StepIndex);
            Assert.AreEqual(80, changes[0].OldDistance);
            Assert.AreEqual(100, changes[0].NewDistance);
        }

        [TestMethod]
        public void ComputeChangeSet_DistanceWithinThreshold_Ignored()
        {
            // 0.5mm 阈值:差 0.4mm 应忽略(浮点抖动保护)
            _aiSteps[0].DistanceMm = 80.4;

            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, _aiSteps);

            Assert.AreEqual(0, changes.Count, "距离差 ≤ 0.5mm 应忽略(浮点保护阈值)");
        }

        [TestMethod]
        public void ComputeChangeSet_DistanceExactlyAtThreshold_Ignored()
        {
            // 边界值:刚好 0.5mm — 用 > 严格大于所以这个应忽略
            _aiSteps[0].DistanceMm = 80.5;

            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, _aiSteps);

            Assert.AreEqual(0, changes.Count, "差恰好 0.5mm 时,'>' 严格大于 → 忽略");
        }

        [TestMethod]
        public void ComputeChangeSet_DistanceJustOverThreshold_GeneratesChange()
        {
            _aiSteps[0].DistanceMm = 80.5001;

            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, _aiSteps);

            Assert.AreEqual(1, changes.Count, "差 0.5001mm 刚过阈值,应生成 change");
        }

        [TestMethod]
        public void ComputeChangeSet_ReverseChanged_GeneratesChange()
        {
            _aiSteps[0].Reverse = true;  // false -> true,距离不变

            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, _aiSteps);

            Assert.AreEqual(1, changes.Count, "reverse 变化必须生成 change(即使距离不变)");
            Assert.IsFalse(changes[0].OldReverse);
            Assert.IsTrue(changes[0].NewReverse);
        }

        [TestMethod]
        public void ComputeChangeSet_StepIndexOutOfRange_Ignored()
        {
            // AI 给的索引 >= current.Count,防御 AI 跟 snapshot 错位
            _aiSteps.Add(new AiExplodeStep { StepIndex = 99, DistanceMm = 100, Reverse = false });

            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, _aiSteps);

            Assert.AreEqual(0, changes.Count, "越界 step_index 必须忽略,不能 NRE 也不能算 change");
        }

        [TestMethod]
        public void ComputeChangeSet_NegativeStepIndex_Ignored()
        {
            _aiSteps.Add(new AiExplodeStep { StepIndex = -1, DistanceMm = 100, Reverse = false });

            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, _aiSteps);

            Assert.AreEqual(0, changes.Count, "负 step_index 必须忽略");
        }

        [TestMethod]
        public void ComputeChangeSet_NullAiStep_Ignored()
        {
            _aiSteps.Add(null);
            _aiSteps.Add(new AiExplodeStep { StepIndex = 0, DistanceMm = 100, Reverse = false });

            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, _aiSteps);

            Assert.AreEqual(1, changes.Count, "null aiStep 必须跳过,不能 NRE");
        }

        [TestMethod]
        public void ComputeChangeSet_MultipleChanges_AllDetected()
        {
            _aiSteps[0].DistanceMm = 100;  // change 1
            _aiSteps[2].Reverse = true;    // change 2
            // _aiSteps[1] 不变

            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, _aiSteps);

            Assert.AreEqual(2, changes.Count);
            Assert.IsTrue(changes.Any(c => c.StepIndex == 0));
            Assert.IsTrue(changes.Any(c => c.StepIndex == 2));
            Assert.IsFalse(changes.Any(c => c.StepIndex == 1), "未变的 step[1] 不应在结果里");
        }

        [TestMethod]
        public void ComputeChangeSet_NullCurrent_ReturnsEmpty()
        {
            var changes = AiExplodeAdvisor.ComputeChangeSet(null, _aiSteps);
            Assert.AreEqual(0, changes.Count, "null current 必须返空(防御 NRE)");
        }

        [TestMethod]
        public void ComputeChangeSet_NullAiSteps_ReturnsEmpty()
        {
            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, null);
            Assert.AreEqual(0, changes.Count, "null aiSteps 必须返空(防御 NRE)");
        }

        [TestMethod]
        public void ComputeChangeSet_PreservesStepNameForApplyRebuild()
        {
            // ApplyRebuild 删 step 要用 StepName,ComputeChangeSet 必须把它带过去
            _aiSteps[0].DistanceMm = 100;

            var changes = AiExplodeAdvisor.ComputeChangeSet(_current, _aiSteps);

            Assert.AreEqual("Step1", changes[0].StepName,
                "StepName 必须从 snapshot 复制到 PendingChange,ApplyRebuild 删 step 靠它");
            Assert.AreEqual("BASE", changes[0].ComponentName,
                "ComponentName 也得带过去(日志 / 排障用)");
        }
    }
}
