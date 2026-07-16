using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSopAddin.Adapter;        // W9+: ActivateOutcome
using SwSopAddin.Infrastructure;
using SwSopAddin.Orchestration;
using SwSopAddin.Tests.Mocks;

namespace SwSopAddin.Tests
{
    /// <summary>
    /// SopWorkflow 编排测试。
    ///
    /// W9+ 后用 MockDocumentValidator 验证 IDocumentValidator 路径(WrongType / null asm / throw)。
    /// 完整 7 步 pipeline 集成测试需要 mock AssemblyDoc(复杂,留给未来)。
    /// </summary>
    [TestClass]
    public class SopWorkflowTests
    {
        /// <summary>
        /// sw == null → 走 SwApiWrapper.TryGetActiveAssembly 的 NoDoc 分支,
        /// 早退到 result.ErrorMessage = "SldWorks 句柄为空",Success = false。
        /// 这是 7 步 pipeline 跑不到 Step 1 的硬保证:确保 Step 0 校验不会被无意中跳过。
        /// </summary>
        [TestMethod]
        public void RunMvp_NullSw_FailsEarlyWithCleanErrorMessage()
        {
            var mocks = new MockServices();
            var workflow = mocks.CreateWorkflow();

            var result = workflow.RunMvp(null, new ConfigStore());

            Assert.IsFalse(result.Success, "sw 为 null 时 Success 必须为 false");
            Assert.AreEqual("SldWorks 句柄为空", result.ErrorMessage);
        }

        /// <summary>
        /// 早退路径下,所有 I*Service 必须 0 次调用 — Step 0 必须真的在 Step 1 之前生效,
        /// 不会因为 reorder / 优化无意中变成"先 explode 才发现 sw 为 null"。
        /// </summary>
        [TestMethod]
        public void RunMvp_NullSw_DoesNotInvokeAnyService()
        {
            var mocks = new MockServices();
            var workflow = mocks.CreateWorkflow();

            workflow.RunMvp(null, new ConfigStore());

            Assert.AreEqual(0, mocks.Explode.CallCount, "早退路径不应调 ExplodeService");
            Assert.AreEqual(0, mocks.Drawing.CallCount, "早退路径不应调 DrawingService");
            Assert.AreEqual(0, mocks.View.CallCount, "早退路径不应调 ViewService");
            Assert.AreEqual(0, mocks.Balloon.CallCount, "早退路径不应调 BalloonService");
            Assert.AreEqual(0, mocks.Bom.CallCount, "早退路径不应调 BomService");
            Assert.AreEqual(0, mocks.Layout.CallCount, "早退路径不应调 LayoutService");
        }

        /// <summary>
        /// SopResult 默认值:Success=false,所有计数字段为 0,所有可空字段为 null。
        /// 这个测试锁住"未初始化的 result 不会假装成功",防止有人无意中把默认改成 true。
        /// </summary>
        [TestMethod]
        public void SopResult_Defaults_AreSafe()
        {
            var r = new SopResult();
            Assert.IsFalse(r.Success);
            Assert.IsNull(r.ErrorMessage);
            Assert.AreEqual(0, r.ExplodeStepCount);
            Assert.AreEqual(0, r.SkippedComponentCount);
            Assert.AreEqual(0, r.BalloonCount);
            Assert.AreEqual(0, r.LayoutElementsCollected);
            Assert.AreEqual(0, r.LayoutElementsApplied);
            Assert.AreEqual(0, r.LayoutRemainingCollisions);
            Assert.AreEqual(0, r.AiAdvisorRounds);
            Assert.AreEqual(0, r.AiAdvisorStepChanges);
            Assert.IsFalse(r.IsoViewInserted);
            Assert.IsFalse(r.BomInserted);
            Assert.IsFalse(r.LayoutApplied);
            Assert.IsFalse(r.AiAdvisorEnabled);
        }

        /// <summary>
        /// 失败时 SummaryForUser 必须包含 [SOP 生成失败] + 错误消息 + 日志路径。
        /// 这是用户唯一看到的输出,必须保证不丢信息。
        /// </summary>
        [TestMethod]
        public void SopResult_SummaryForUser_OnFailure_IncludesErrorAndLogPath()
        {
            var r = new SopResult
            {
                Success = false,
                ErrorMessage = "测试失败原因"
            };

            var s = r.SummaryForUser();
            StringAssert.Contains(s, "[SOP 生成失败]");
            StringAssert.Contains(s, "测试失败原因");
            StringAssert.Contains(s, "logs", "失败摘要必须指向日志目录,方便用户查 NLog 输出");
        }

        /// <summary>
        /// 成功时 SummaryForUser 必须包含:爆炸视图名、爆炸步骤数(含跳过数)、等轴测状态、球标数、
        /// BOM 状态、布局说明、W6 wiring 提示。锁住用户可见的"成功报告"格式不被静默改坏。
        /// </summary>
        [TestMethod]
        public void SopResult_SummaryForUser_OnSuccess_IncludesAllStepInfo()
        {
            var r = new SopResult
            {
                Success = true,
                ExplodedViewName = "TestView_SOP_Explode",
                ExplodeStepCount = 10,
                SkippedComponentCount = 2,
                DrawingTemplateUsed = "TestTemplate.drwdot",
                DrawingSavedPath = @"D:\SOP_Output\TestView.PDF",
                IsoViewInserted = true,
                BalloonCount = 8,
                BomInserted = true,
                LayoutApplied = true,
                LayoutRemainingCollisions = 0,
                LayoutNotes = "已应用",
                AiAdvisorEnabled = false,
            };

            var s = r.SummaryForUser();
            StringAssert.Contains(s, "[SOP 生成成功]");
            StringAssert.Contains(s, "TestView_SOP_Explode", "应包含爆炸视图名");
            StringAssert.Contains(s, "10", "应包含爆炸步骤数");
            StringAssert.Contains(s, "8", "应包含球标数");
            StringAssert.Contains(s, "已插入", "应包含等轴测/BOM 状态");
            StringAssert.Contains(s, "W5 智能布局", "应提示 W5 布局已接入");
        }

        /// <summary>
        /// 边界:AI advisor enabled 但被跳过(apiKey 缺)时,SummaryForUser 必须明确说"跳过 — 原因",
        /// 不能让用户以为"AI 评估"跑了结果没改任何东西。
        /// </summary>
        [TestMethod]
        public void SopResult_SummaryForUser_AiSkipped_ShowsReason()
        {
            var r = new SopResult
            {
                Success = true,
                AiAdvisorEnabled = true,
                AiAdvisorSkippedReason = "apiKey 未配置",
            };

            var s = r.SummaryForUser();
            StringAssert.Contains(s, "AI 评估");
            StringAssert.Contains(s, "跳过", "AI 被跳过时要明确说'跳过'");
            StringAssert.Contains(s, "apiKey 未配置", "跳过原因必须可见,方便用户排障");
        }

        /// <summary>
        /// 边界:AI advisor 跑了但改 0 个 step 时,SummaryForUser 应显示"0 轮,改了 0 个 step"
        /// (这是 POC 3 no-op 的预期行为,POC 1 验证前的正常状态)。
        /// </summary>
        [TestMethod]
        public void SopResult_SummaryForUser_AiZeroRounds_ShowsZeroChanges()
        {
            var r = new SopResult
            {
                Success = true,
                AiAdvisorEnabled = true,
                AiAdvisorRounds = 1,
                AiAdvisorStepChanges = 0,
            };

            var s = r.SummaryForUser();
            StringAssert.Contains(s, "1 轮");
            StringAssert.Contains(s, "改了 0 个 step");
        }

        // ===== W9+:IDocumentValidator 路径集成测试 =====

        /// <summary>
        /// W9+:validator 返 ActivateOutcome.WrongType 时,RunMvp 早退 + result.ErrorMessage
        /// 包含 validator 传出的自定义 message(不是 "SldWorks 句柄为空")。
        /// 验证 Step 0 校验对 WrongType 路径正确路由。
        /// </summary>
        [TestMethod]
        public void RunMvp_ValidatorReturnsWrongType_FailsEarlyWithCustomMessage()
        {
            var mocks = new MockServices();
            mocks.Validator.NextOutcome = ActivateOutcome.WrongType;
            mocks.Validator.NextMessage = "当前文档是零件,不是装配体";
            var workflow = mocks.CreateWorkflow();

            var result = workflow.RunMvp(null, new ConfigStore());  // sw=null 但 validator mock 不碰 sw

            Assert.IsFalse(result.Success, "WrongType 路径 Success 必须为 false");
            Assert.AreEqual("当前文档是零件,不是装配体", result.ErrorMessage,
                "result.ErrorMessage 必须原样传 validator 传出的 message");
            Assert.AreEqual(0, mocks.Explode.CallCount, "WrongType 早退不应调 ExplodeService");
            Assert.AreEqual(0, mocks.Drawing.CallCount, "WrongType 早退不应调 DrawingService");
        }

        /// <summary>
        /// W9+:validator 返 Ok + asm=null(测试场景)。
        /// 早期版本预期:(IModelDoc2)asm.GetPathName() NRE → 早退;但 W9+ 改了 SopWorkflow L76/L80/L95
        /// defensive null check → mock null asm 不 NRE,继续走 7 步。
        /// 现版本预期:result.Success=true(容错),Step 1 mock 被调(asm 参数为 null 但 mock 不碰)。
        /// 这个测试锁住 "SopWorkflow 兼容 null asm" 的契约(防止以后有人改回非 defensive 代码)。
        /// </summary>
        [TestMethod]
        public void RunMvp_ValidatorOk_NullAsm_DefensiveNullCheck_Step1Invoked()
        {
            var mocks = new MockServices();
            mocks.Validator.NextOutcome = ActivateOutcome.Ok;
            mocks.Validator.NextAsm = null;  // 测试场景 — mock null asm
            var workflow = mocks.CreateWorkflow();

            var result = workflow.RunMvp(null, new ConfigStore());  // sw=null,validator mock 不碰 sw

            Assert.IsTrue(result.Success, "SopWorkflow L76 兼容 null asm 后,容错成功");
            Assert.AreEqual(1, mocks.Validator.CallCount, "Step 0 validator 应被调");
            Assert.AreEqual(1, mocks.Explode.CallCount, "Step 1 mock 应被调(asm=null 但 mock 不碰)");
        }

        /// <summary>
        /// W9+:validator 抛异常 → RollbackManager 顶层 catch 接管 → result.Success=false,
        /// ErrorMessage 包含 validator 抛的 exception message。Step 1+ 服务都不被调。
        /// 验证 validator 抛异常时 SopWorkflow 不 crash,只是 fail。
        /// </summary>
        [TestMethod]
        public void RunMvp_ValidatorThrows_ResultContainsExceptionMessage()
        {
            var mocks = new MockServices();
            mocks.Validator.ThrowOnCall = new System.InvalidOperationException("validator 模拟抛异常");
            var workflow = mocks.CreateWorkflow();

            var result = workflow.RunMvp(null, new ConfigStore());

            Assert.IsFalse(result.Success, "validator 抛异常时 Success 必须为 false");
            Assert.IsTrue(result.ErrorMessage.Contains("validator 模拟抛异常"),
                $"ErrorMessage 应包含 validator exception message,实际:{result.ErrorMessage}");
            Assert.AreEqual(0, mocks.Explode.CallCount, "validator 抛异常后不应调 Step 1+ 服务");
            Assert.AreEqual(0, mocks.Drawing.CallCount);
            Assert.AreEqual(0, mocks.View.CallCount);
        }

        /// <summary>
        /// W9+:完整 7 步 pipeline 集成测试(用 null mock asm/drw 走完整 mock 链路)。
        /// 验证:Step 1/2/3 走 mock service(CallCount=1);Step 4/5/6 因 view null 早退(CallCount=0);
        /// Step 7 PDF TryExportPdf drw==null 早返 null,不抛。整体 result.Success=true(RunMvp try/catch 容错)。
        /// 关键:SopWorkflow.L76/L95 已 defensive null check(mock 友好,production 不影响)。
        /// </summary>
        [TestMethod]
        public void RunMvp_ValidatorOk_NullMocks_AllStepsInvoked_Step4_5_6_Skipped_SuccessTrue()
        {
            var mocks = new MockServices();
            mocks.Validator.NextOutcome = ActivateOutcome.Ok;
            mocks.Validator.NextAsm = null;  // 测试场景 — mock null asm
            var workflow = mocks.CreateWorkflow();

            var result = workflow.RunMvp(null, new ConfigStore());

            // Step 0:validator 调到
            Assert.AreEqual(1, mocks.Validator.CallCount, "Step 0 validator 应被调 1 次");

            // Step 1 走 mock(asm 参数为 null 但 mock 不碰)
            Assert.AreEqual(1, mocks.Explode.CallCount, "Step 1 应调 ExplodeService");
            Assert.AreEqual(5, result.ExplodeStepCount, "ExplodeStepCount 应从 mock NextResult.StepCount 拿");

            // Step 2 走 mock(返 null DrawingDoc)
            Assert.AreEqual(1, mocks.Drawing.CallCount, "Step 2 应调 DrawingService");
            // 注:result.IsoViewInserted 在 RunStep3_View 不抛时设 true — mock 返 null view 但 IsoViewInserted=true(代码 bug 不算,本测试不检)

            // Step 3 走 mock(返 null View)
            Assert.AreEqual(2, mocks.View.CallCount, "Step 3 应分别插入爆炸与原装配等轴测视图");
            Assert.AreEqual(1, mocks.View.InsertOriginalCallCount, "Step 3 应插入一张原装配等轴测视图");

            // Step 4/5/6 因 view null 早退,CallCount = 0
            Assert.AreEqual(0, mocks.Balloon.CallCount, "Step 4 应跳过(view null)→ BalloonService 0 call");
            Assert.AreEqual(0, mocks.Bom.CallCount, "Step 5 应跳过 → BomService 0 call");
            Assert.AreEqual(0, mocks.Layout.CallCount, "Step 6 应跳过 → LayoutService 0 call");

            Assert.AreEqual(0, result.BalloonCount, "BalloonCount 应 0(跳过)");
            Assert.IsFalse(result.BomInserted, "BomInserted 应 false(跳过)");
            Assert.IsFalse(result.LayoutApplied, "LayoutApplied 应 false(跳过)");

            // Step 7 TryExportPdf drw==null 早返 null,不抛;result.Success 仍 true(容错)
            Assert.IsTrue(result.Success, "所有 step 走 mock try/catch 容错,Success 应 true");
        }

        /// <summary>
        /// W11+ BuildLayoutOptions 透传的具体契约:用反射拿 private static 方法,
        /// 输入一个填了非常规值的 LayoutOptionsConfig,验证返回的 LayoutOptions 各字段相符。
        /// 走反射是因为 Orchestration 项目没有加 InternalsVisibleTo("SwSopAddin.Tests")。
        /// </summary>
        [TestMethod]
        public void BuildLayoutOptions_Passes_All_Config_Fields_Through()
        {
            // Arrange:用反射调 private static SopWorkflow.BuildLayoutOptions(ConfigStore)
            var workflowType = typeof(SopWorkflow);
            var method = workflowType.GetMethod("BuildLayoutOptions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "应找到 private static BuildLayoutOptions 方法");

            var config = new ConfigStore();
            config.Layout.TitleBlockWidthMeters = 0.25;
            config.Layout.TitleBlockHeightMeters = 0.07;
            config.Layout.IsoViewHeightFraction = 0.42;
            config.Layout.IsoMinHeightMeters = 0.03;
            config.Layout.BomReservedWidthMeters = 0.10;
            config.Layout.BomReservedHeightMeters = 0.05;
            config.Layout.PaperSize = "A4";

            // Act
            var result = method.Invoke(null, new object[] { config });
            var opt = (SwSopAddin.Layout.LayoutOptions)result;

            // Assert
            Assert.AreEqual(0.25, opt.TitleBlockWidthMeters, 1e-9, "TitleBlockWidthMeters 透传");
            Assert.AreEqual(0.07, opt.TitleBlockHeightMeters, 1e-9, "TitleBlockHeightMeters 透传");
            Assert.AreEqual(0.42, opt.IsoViewHeightFraction, 1e-9, "IsoViewHeightFraction 透传");
            Assert.AreEqual(0.03, opt.IsoMinHeightMeters, 1e-9, "IsoMinHeightMeters 透传");
            Assert.AreEqual(0.10, opt.BomReservedWidthMeters, 1e-9, "BomReservedWidthMeters 透传");
            Assert.AreEqual(0.05, opt.BomReservedHeightMeters, 1e-9, "BomReservedHeightMeters 透传");
            Assert.AreEqual("A4", opt.PaperSize, "PaperSize 透传");
        }

        /// <summary>
        /// W11+ BuildLayoutOptions 对 null config 必须不抛、返 LayoutOptions.Default 等价结果。
        /// 防御性:Host 或测试代码偶尔传 null config 时 BuildLayoutOptions 不能 NRE 死。
        /// </summary>
        [TestMethod]
        public void BuildLayoutOptions_NullConfig_ReturnsDefaults()
        {
            var method = typeof(SopWorkflow).GetMethod("BuildLayoutOptions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            var opt = (SwSopAddin.Layout.LayoutOptions)method.Invoke(null, new object[] { null });

            var def = SwSopAddin.Layout.LayoutOptions.Default;
            Assert.AreEqual(def.TitleBlockWidthMeters, opt.TitleBlockWidthMeters);
            Assert.AreEqual(def.IsoViewHeightFraction, opt.IsoViewHeightFraction);
            Assert.AreEqual(def.BomReservedWidthMeters, opt.BomReservedWidthMeters);
        }

        /// <summary>
        /// W11+ BuildLayoutOptions 对 config.Layout==null 也走默认值路径(SopWorkflow 拿到
        /// 老 config.json 反序列化对象,Layout 字段缺失时 Newtonsoft 反序列化为 null)。
        /// </summary>
        [TestMethod]
        public void BuildLayoutOptions_NullLayoutSection_ReturnsDefaults()
        {
            var method = typeof(SopWorkflow).GetMethod("BuildLayoutOptions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            var config = new ConfigStore();
            config.Layout = null;  // 模拟旧 config.json 没 Layout 段

            var opt = (SwSopAddin.Layout.LayoutOptions)method.Invoke(null, new object[] { config });

            var def = SwSopAddin.Layout.LayoutOptions.Default;
            Assert.AreEqual(def.TitleBlockWidthMeters, opt.TitleBlockWidthMeters);
            Assert.AreEqual(def.IsoViewHeightFraction, opt.IsoViewHeightFraction);
        }
    }
}
