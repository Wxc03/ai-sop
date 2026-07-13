using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSopAddin.Infrastructure;
using SwSopAddin.Orchestration;
using SwSopAddin.Tests.Mocks;

namespace SwSopAddin.Tests
{
    /// <summary>
    /// SopWorkflow.RunStep 派发测试。
    ///
    /// 范围:只测"不碰 COM"的部分 — 越界 stepNumber 返错、null sw 走早退。
    /// 真正跑 step 1-7 里的业务逻辑(IExplodeService.Create / IDrawingService.NewFromTemplate 等)
    /// 需要真 SW,留给 OnStepByStep 真机验证。
    /// </summary>
    [TestClass]
    public class RunStepTests
    {
        [TestMethod]
        public void RunStep_StepNumberZero_ReturnsError()
        {
            var mocks = new MockServices();
            var workflow = mocks.CreateWorkflow();

            var result = workflow.RunStep(null, new ConfigStore(), 0);

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, "stepNumber 越界",
                "stepNumber=0 必须明确告诉用户'越界',不是默默返 false");
            StringAssert.Contains(result.ErrorMessage, "0",
                "错误消息应包含具体的越界值,方便排障");
        }

        [TestMethod]
        public void RunStep_StepNumberNegative_ReturnsError()
        {
            var mocks = new MockServices();
            var workflow = mocks.CreateWorkflow();

            var result = workflow.RunStep(null, new ConfigStore(), -1);

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, "stepNumber 越界");
            StringAssert.Contains(result.ErrorMessage, "-1");
        }

        [TestMethod]
        public void RunStep_StepNumberEight_ReturnsError()
        {
            // 1-7 有效,8+ 无效
            var mocks = new MockServices();
            var workflow = mocks.CreateWorkflow();

            var result = workflow.RunStep(null, new ConfigStore(), 8);

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, "stepNumber 越界");
            StringAssert.Contains(result.ErrorMessage, "1-7",
                "错误消息应明确告诉用户有效范围是 1-7");
        }

        [TestMethod]
        public void RunStep_StepNumberIntMax_ReturnsError()
        {
            // 防御 int.MaxValue
            var mocks = new MockServices();
            var workflow = mocks.CreateWorkflow();

            var result = workflow.RunStep(null, new ConfigStore(), int.MaxValue);

            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void RunStep_NullSw_AllStepNumbers_FailEarly()
        {
            // 1-7 全部 sw=null 早退 — 即使 stepNumber 合法
            var mocks = new MockServices();
            var workflow = mocks.CreateWorkflow();

            for (int step = 1; step <= 7; step++)
            {
                var result = workflow.RunStep(null, new ConfigStore(), step);
                Assert.IsFalse(result.Success, "step {0} sw=null 必须早退", step);
                Assert.IsTrue(
                    result.ErrorMessage != null && result.ErrorMessage.Length > 0,
                    "step {0} 早退时必须有 ErrorMessage", step);
            }
        }

        [TestMethod]
        public void RunStep_OutOfRange_DoesNotInvokeAnyService()
        {
            // 越界时必须不能调任何 service(没机会调,但锁住防回归)
            var mocks = new MockServices();
            var workflow = mocks.CreateWorkflow();

            workflow.RunStep(null, new ConfigStore(), 99);

            Assert.AreEqual(0, mocks.Explode.CallCount);
            Assert.AreEqual(0, mocks.Drawing.CallCount);
            Assert.AreEqual(0, mocks.View.CallCount);
            Assert.AreEqual(0, mocks.Balloon.CallCount);
            Assert.AreEqual(0, mocks.Bom.CallCount);
            Assert.AreEqual(0, mocks.Layout.CallCount);
        }

        [TestMethod]
        public void RunStep_NullSw_Step1_DoesNotInvokeExplode()
        {
            // step=1 sw=null → 走 SwApiWrapper.TryGetActiveAssembly 早退,不能进 ExplodeService
            // 锁住"sw 校验必须在 service 调用前"的契约
            var mocks = new MockServices();
            var workflow = mocks.CreateWorkflow();

            workflow.RunStep(null, new ConfigStore(), 1);

            Assert.AreEqual(0, mocks.Explode.CallCount,
                "sw=null 时即使 step=1 也不能调 ExplodeService.Create");
        }
    }
}
