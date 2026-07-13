using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSopAddin.Services;

namespace SwSopAddin.Tests
{
    /// <summary>
    /// Part A Phase 2 — ExplodeService.ApplyPlan 编排逻辑单测(走 fake IExplodeApplier)。
    ///
    /// 范围:逐 placement 调 ResolveComponent → ApplyPlacement 的编排、失败计数、单个失败不中断循环。
    /// 真实 COM 行为(SwDirectionalExplodeApplier 里选基准面/IAddExplodeStep 真调不调 work)不在这里测,
    /// 要靠真机验证(Part A Phase 3)。
    /// </summary>
    [TestClass]
    public class ExplodeServiceSmartHybridTests
    {
        /// <summary>
        /// 录制型 fake — 把每次调用记下来,按名字集合模拟指定组件的 Resolve/Apply 失败。
        /// 组件句柄直接用 componentName 字符串本身(不需要依赖真 COM 类型)。
        /// </summary>
        private class RecordingExplodeApplier : IExplodeApplier
        {
            public List<string> Calls = new List<string>();
            public HashSet<string> FailResolveFor = new HashSet<string>();
            public HashSet<string> FailApplyFor = new HashSet<string>();

            public object ResolveComponent(string componentName, int index)
            {
                Calls.Add("Resolve:" + componentName + ":" + index);
                if (FailResolveFor.Contains(componentName)) return null;
                return componentName;
            }

            public bool ApplyPlacement(object component, double[] direction, double distanceMeters, out string stepName)
            {
                string name = component as string;
                Calls.Add("Apply:" + name + ":" + distanceMeters);
                if (name != null && FailApplyFor.Contains(name))
                {
                    stepName = null;
                    return false;
                }
                stepName = name + "_Step";
                return true;
            }
        }

        private static ExplodeLayoutResult MakePlan(params string[] names)
        {
            var plan = new ExplodeLayoutResult();
            for (int i = 0; i < names.Length; i++)
            {
                plan.Placements.Add(new ExplodePlacement
                {
                    ComponentName = names[i],
                    Index = i,
                    Direction = new double[] { 0, 0, 1 },
                    DistanceMeters = 0.05,
                    Role = ExplodeRole.Body
                });
            }
            return plan;
        }

        // ===== 空 / null 防御 =====

        [TestMethod]
        public void ApplyPlan_NullPlan_ReturnsZeroZero()
        {
            var applier = new RecordingExplodeApplier();
            ExplodeService.ApplyPlan(null, applier, out int processed, out int failed);
            Assert.AreEqual(0, processed);
            Assert.AreEqual(0, failed);
            Assert.AreEqual(0, applier.Calls.Count, "null plan 不应调用 applier 任何方法");
        }

        [TestMethod]
        public void ApplyPlan_NullApplier_ReturnsZeroZero()
        {
            var plan = MakePlan("A");
            ExplodeService.ApplyPlan(plan, null, out int processed, out int failed);
            Assert.AreEqual(0, processed);
            Assert.AreEqual(0, failed);
        }

        [TestMethod]
        public void ApplyPlan_EmptyPlacements_ReturnsZeroZero()
        {
            var plan = new ExplodeLayoutResult();
            var applier = new RecordingExplodeApplier();
            ExplodeService.ApplyPlan(plan, applier, out int processed, out int failed);
            Assert.AreEqual(0, processed);
            Assert.AreEqual(0, failed);
        }

        // ===== 基础路径 =====

        [TestMethod]
        public void ApplyPlan_AllSucceed_CountsProcessed()
        {
            var plan = MakePlan("A", "B", "C");
            var applier = new RecordingExplodeApplier();

            ExplodeService.ApplyPlan(plan, applier, out int processed, out int failed);

            Assert.AreEqual(3, processed);
            Assert.AreEqual(0, failed);
            Assert.AreEqual(6, applier.Calls.Count, "3 个 placement × (Resolve+Apply) = 6");
        }

        [TestMethod]
        public void ApplyPlan_CallsInOrder_ResolveThenApply()
        {
            var plan = MakePlan("A");
            var applier = new RecordingExplodeApplier();

            ExplodeService.ApplyPlan(plan, applier, out _, out _);

            Assert.AreEqual("Resolve:A:0", applier.Calls[0]);
            Assert.AreEqual("Apply:A:0.05", applier.Calls[1]);
        }

        // ===== 失败恢复:单个失败不中断循环 =====

        [TestMethod]
        public void ApplyPlan_ResolveComponentFails_CountsAsFailed_ContinuesLoop()
        {
            var plan = MakePlan("A", "B", "C");
            var applier = new RecordingExplodeApplier { FailResolveFor = { "B" } };

            ExplodeService.ApplyPlan(plan, applier, out int processed, out int failed);

            Assert.AreEqual(2, processed, "A、C 应该成功");
            Assert.AreEqual(1, failed, "B resolve 失败算 1 个 failed");
            // B resolve 失败后不应该调 ApplyPlacement(comp==null 直接跳过)
            CollectionAssert.DoesNotContain(applier.Calls, "Apply:B:0.05");
        }

        [TestMethod]
        public void ApplyPlan_ApplyPlacementFails_CountsAsFailed_ContinuesLoop()
        {
            var plan = MakePlan("A", "B", "C");
            var applier = new RecordingExplodeApplier { FailApplyFor = { "B" } };

            ExplodeService.ApplyPlan(plan, applier, out int processed, out int failed);

            Assert.AreEqual(2, processed, "A、C 应该成功");
            Assert.AreEqual(1, failed, "B ApplyPlacement 失败算 1 个 failed");
            // B 即便 Apply 失败,Resolve 仍应该被调用过(顺序编排,不是提前跳过)
            CollectionAssert.Contains(applier.Calls, "Resolve:B:1");
        }

        [TestMethod]
        public void ApplyPlan_AllFail_ReturnsZeroProcessed()
        {
            var plan = MakePlan("A", "B");
            var applier = new RecordingExplodeApplier { FailApplyFor = { "A", "B" } };

            ExplodeService.ApplyPlan(plan, applier, out int processed, out int failed);

            Assert.AreEqual(0, processed);
            Assert.AreEqual(2, failed);
        }

        [TestMethod]
        public void ApplyPlan_MixedResolveAndApplyFailures_CorrectCounts()
        {
            var plan = MakePlan("A", "B", "C", "D");
            var applier = new RecordingExplodeApplier
            {
                FailResolveFor = { "A" },
                FailApplyFor = { "C" }
            };

            ExplodeService.ApplyPlan(plan, applier, out int processed, out int failed);

            Assert.AreEqual(2, processed, "B、D 成功");
            Assert.AreEqual(2, failed, "A resolve 失败 + C apply 失败");
        }
    }
}
