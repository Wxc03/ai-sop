using System;
using SolidWorks.Interop.sldworks;
using SwSopAddin.Adapter;        // W8+: IDocumentValidator / ActivateOutcome
using SwSopAddin.Infrastructure;
using SwSopAddin.Layout;
using SwSopAddin.Orchestration;
using SwSopAddin.Services;

namespace SwSopAddin.Tests.Mocks
{
    /// <summary>
    /// Hand-rolled mock 集合 — 6 个 I*Service 接口各一个,行为可配(返指定值 / 抛指定异常)。
    /// 不用 Moq 是为了:
    ///   1) 少一个外部依赖,csproj 保持只引 MSTest
    ///   2) 6 个接口一共 ~6 个方法,自己写比配 Moq 的 Expression/It.Is 更直接
    ///   3) 调用记录(call count)自己加一个 int 字段就够
    /// </summary>
    public class MockExplodeService : IExplodeService
    {
        public int CallCount;
        public ExplodeResult NextResult = new ExplodeResult
        {
            ExplodedViewName = "MockView_SOP_Explode",
            StepCount = 5,
        };
        public Exception ThrowOnCall;

        public ExplodeResult Create(AssemblyDoc asm, ConfigStore config)
        {
            CallCount++;
            if (ThrowOnCall != null) throw ThrowOnCall;
            return NextResult;
        }
    }

    public class MockDrawingService : IDrawingService
    {
        public int CallCount;
        public string TemplatePathToReturn = @"D:\Mock\Template.drwdot";
        public Exception ThrowOnCall;

        // 拿不到真的 DrawingDoc(COM 类型),所以返 null — 调用方 SopWorkflow 会 rb.Track(SafeCloseDoc) 内部 if (string.IsNullOrEmpty(title)) return;
        public DrawingDoc NewFromTemplate(ISldWorks sw, ConfigStore config, out string actualTemplatePath)
        {
            CallCount++;
            actualTemplatePath = TemplatePathToReturn;
            if (ThrowOnCall != null) throw ThrowOnCall;
            return null;  // 走 null 路径,SopWorkflow 会 catch 住走 happy path(Layout/Export 跳过 isoView 相关)
        }
    }

    public class MockViewService : IViewService
    {
        public int CallCount;
        public int InsertOriginalCallCount;
        public View NextView = null;  // null = 走"插视图失败"路径,SopWorkflow 不 throw,balloon/bom 跳过
        public Exception ThrowOnCall;
        public ViewInsertDiagnostics LastDiagnose;
        public string LastExplodeViewName;  // W7+:记录调用方传入的 explodeViewName,验证参数传递

        public View InsertExplodedIso(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, string explodeViewName, double x, double y, double z, Sheet targetSheet = null)
        {
            CallCount++;
            LastExplodeViewName = explodeViewName;
            if (ThrowOnCall != null) throw ThrowOnCall;
            return NextView;
        }

        public View InsertOriginalIso(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, double x, double y, double z)
        {
            CallCount++;
            InsertOriginalCallCount++;
            if (ThrowOnCall != null) throw ThrowOnCall;
            return NextView;
        }

        public ViewInsertDiagnostics Diagnose(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm)
        {
            CallCount++;
            var d = new ViewInsertDiagnostics { AsmTitle = "MockAsm", DrwTitle = "MockDrw" };
            LastDiagnose = d;
            return d;
        }
    }

    public class MockBalloonService : IBalloonService
    {
        public int CallCount;
        public int NextBalloonCount = 4;
        public Exception ThrowOnCall;

        public int ApplyAutoBalloon(DrawingDoc drw, View view, int startNumber = 1)
        {
            CallCount++;
            if (ThrowOnCall != null) throw ThrowOnCall;
            return NextBalloonCount;
        }
    }

    public class MockBomService : IBomService
    {
        public int CallCount;
        public BomInsertResult NextResult = new BomInsertResult
        {
            Success = true,
            TemplateUsed = "MockBomTemplate",
            RowCount = 4,
            BomTable = null,  // LayoutService 不依赖 BomTable 是否真的 COM
        };
        public Exception ThrowOnCall;

        public BomInsertResult ApplyBomTable(DrawingDoc drw, View targetView, string configuration = "")
        {
            CallCount++;
            if (ThrowOnCall != null) throw ThrowOnCall;
            return NextResult;
        }
    }

    public class MockLayoutService : ILayoutService
    {
        public int CallCount;
        public LayoutResult NextResult = new LayoutResult
        {
            Success = true,
            ElementsCollected = 1,
            ElementsApplied = 1,
            // Clean 是 computed property(RemainingCollisions==0),所以设 RemainingCollisions=0
            Avoidance = new AvoidanceResult { RemainingCollisions = 0 },
            Notes = "mock layout",
        };
        public Exception ThrowOnCall;
        /// <summary>W11+ 验证 BuildLayoutOptions 透传用:记录最新一次收到的 LayoutOptions。</summary>
        public LayoutOptions LastOptions { get; private set; }

        public LayoutResult ApplyLayout(DrawingDoc drw, View[] views, BomTableAnnotation bomTable, LayoutOptions options = null, Sheet targetSheet = null)
        {
            CallCount++;
            LastOptions = options;
            if (ThrowOnCall != null) throw ThrowOnCall;
            return NextResult;
        }

        public CollisionReport DetectOnly(DrawingDoc drw, View[] views, BomTableAnnotation bomTable, LayoutOptions options = null)
        {
            CallCount++;
            if (ThrowOnCall != null) throw ThrowOnCall;
            return new CollisionReport { ElementsScanned = 1, CollisionCount = 0, Summary = "mock" };
        }

        public int ApplyIsoPlacementCallCount;
        public IsoPlacementResult NextIsoResult = new IsoPlacementResult
        {
            Success = true,
            AppliedScaleRatio = 1.0,
            Notes = "mock iso placement",
        };

        public IsoPlacementResult ApplyIsoPlacement(DrawingDoc drw, View isoView, LayoutOptions options = null, Sheet targetSheet = null)
        {
            ApplyIsoPlacementCallCount++;
            if (ThrowOnCall != null) throw ThrowOnCall;
            return NextIsoResult;
        }
    }

    /// <summary>
    /// 把 6 个 mock 装一起,new 一下就有全套 — 测试代码不用挨个 new。
    /// 选 RunWithMocks(...) 模式而不是 fixture,因为 MSTest 3 的 [TestInitialize] 不支持 DI
    /// 注入(必须用反射重写 instance),而构造函数注入就一行写完。
    /// W8+:加 MockDocumentValidator(默认返 NoDoc 让 RunMvp 走早退路径;测试可设 Outcome 改返其他 enum)。
    /// </summary>
    public class MockServices
    {
        public MockExplodeService Explode { get; } = new MockExplodeService();
        public MockDrawingService Drawing { get; } = new MockDrawingService();
        public MockViewService View { get; } = new MockViewService();
        public MockBalloonService Balloon { get; } = new MockBalloonService();
        public MockBomService Bom { get; } = new MockBomService();
        public MockLayoutService Layout { get; } = new MockLayoutService();
        public MockDocumentValidator Validator { get; } = new MockDocumentValidator();

        public SopWorkflow CreateWorkflow(ILayoutService layoutOverride = null)
        {
            return new SopWorkflow(
                Explode,
                Drawing,
                View,
                Balloon,
                Bom,
                layoutOverride ?? Layout,
                Validator);
        }
    }

    /// <summary>
    /// W8+:Mock IDocumentValidator — 让测试不依赖真实 SW asm。
    /// 默认返 NoDoc + null asm + 默认 message 跟 SwDocumentValidator 一致
    /// ("SldWorks 句柄为空" when sw is null),让现有 RunMvp_NullSw_* 测试无需改期望值。
    /// 测试可设 Outcome / Asm / Message / ThrowOnCall 字段改变行为。
    /// </summary>
    public class MockDocumentValidator : IDocumentValidator
    {
        public int CallCount;
        public ActivateOutcome NextOutcome = ActivateOutcome.NoDoc;
        public AssemblyDoc NextAsm = null;
        public string NextMessage = "SldWorks 句柄为空";  // W8+:跟 SwDocumentValidator(sw==null 时)一致
        public Exception ThrowOnCall;

        public ActivateOutcome TryGetActiveAssembly(ISldWorks sw, out AssemblyDoc asm, out string message)
        {
            CallCount++;
            if (ThrowOnCall != null) throw ThrowOnCall;
            asm = NextAsm;
            message = NextMessage;
            return NextOutcome;
        }
    }
}
