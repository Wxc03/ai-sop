using System;
using System.IO;
using System.Runtime.InteropServices;
using NLog;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;  // W4.1 PDF 导出需要 swUserPreferenceToggle_e / swExportDataFileType_e / swSaveAsVersion_e 等
using SwSopAddin.Adapter;
using SwSopAddin.Infrastructure;
using SwSopAddin.Layout;  // W6-fix:W5 智能布局 wiring
using SwSopAddin.Services;

namespace SwSopAddin.Orchestration
{
    /// <summary>
    /// W3 + W6-fix + W7+ 流程编排 — 串 Step 1(爆炸)/ Step 1.5(AI 评估)/ Step 2(建工程图)/
    /// Step 3(插爆炸视图)/ Step 4(打标)/ Step 5(插 BOM)/ Step 6(智能布局)/ Step 7(自动存盘)。
    /// 失败时全回滚。
    ///
    /// W7+:拆出 RunStepN_* 私有方法,RunMvp 顺序调用 + RollbackManager 兜底;
    /// RunStep(stepNumber) 公开派发器,允许 OnStepByStep 单独触发某一步(调试用)。
    /// </summary>
    public class SopWorkflow : ISopWorkflow
    {
        private static readonly Logger Log = Logging.ForType(typeof(SopWorkflow));

        private readonly IExplodeService _explode;
        private readonly IDrawingService _drawing;
        private readonly IViewService _view;
        private readonly IBalloonService _balloon;
        private readonly IBomService _bom;
        private readonly ILayoutService _layout;  // W6-fix
        private readonly IDocumentValidator _validator;  // W8+:抽象 SW ActiveDoc 校验,让 SopWorkflow 注入 mock → 解锁完整 7 步 pipeline 测试

        public SopWorkflow()
            : this(new ExplodeService(), new DrawingService(), new ViewService(), new BalloonService(), new BomService(), new LayoutService(), new SwDocumentValidator())
        { }

        public SopWorkflow(
            IExplodeService explode,
            IDrawingService drawing,
            IViewService view,
            IBalloonService balloon,
            IBomService bom,
            ILayoutService layout = null,
            IDocumentValidator validator = null)  // W8+:null → new SwDocumentValidator()(生产实现)
        {
            _explode = explode ?? throw new ArgumentNullException(nameof(explode));
            _drawing = drawing ?? throw new ArgumentNullException(nameof(drawing));
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _balloon = balloon ?? throw new ArgumentNullException(nameof(balloon));
            _bom = bom ?? throw new ArgumentNullException(nameof(bom));
            _layout = layout;  // null 也允许,Wiring 失败时降级到原 RunMvp 行为
            _validator = validator ?? new SwDocumentValidator();
        }

        public SopResult RunMvp(ISldWorks sw, ConfigStore config)
        {
            Log.Info("=== SopWorkflow.RunMvp START (W6-fix 编排,W5 已 wiring) ===");
            var result = new SopResult();
            ModelDoc2 drwAsModelDoc2 = null;  // 用于 Step 7 存盘

            using (var rb = new RollbackManager())
            {
                try
                {
                    // ---------- Step 0:文档校验 ----------
                    // W8+:通过注入的 IDocumentValidator 调用 — 测试时传 MockDocumentValidator,
                    // 生产路径 _validator 默认是 SwDocumentValidator(包 SwApiWrapper.TryGetActiveAssembly)。
                    var outcome = _validator.TryGetActiveAssembly(sw, out AssemblyDoc asm, out string msg);
                    if (outcome != ActivateOutcome.Ok)
                    {
                        result.ErrorMessage = msg;
                        Log.Warn("文档校验失败: {0}", msg);
                        return result;
                    }
                    Log.Info("激活装配体 OK: '{0}'", asm != null ? ((IModelDoc2)asm).GetPathName() : "(null mock)");
                    rb.Track(() =>
                    {
                        // W9+ 集成测试兼容 null asm — production SwDocumentValidator 返 Ok 时 asm 永远非 null,
                        // 但 mock 测试场景可能传 null,这里 defensive null check。
                        if (asm == null) return;
                        try { Marshal.ReleaseComObject(asm); }
                        catch (Exception ex) { Log.Warn(ex, "ReleaseComObject(asm) 失败"); }
                    });

                    // ---------- Step 1:爆炸 ----------
                    var explodeResult = RunStep1_Explode(asm, config, result);

                    // ---------- Step 1.5: AI 爆炸评估(可选) ----------
                    RunStep15_AiAdvisor(sw, asm, explodeResult, config, result);

                    // ---------- Step 2:工程图 ----------
                    Log.Info("Step 2/7: 建工程图");
                    string actualTemplatePath;
                    DrawingDoc drw = _drawing.NewFromTemplate(sw, config, out actualTemplatePath);
                    drwAsModelDoc2 = (ModelDoc2)drw;
                    result.DrawingTemplateUsed = actualTemplatePath;
                    rb.Track(() => SafeCloseDoc(sw, drw != null ? ((IModelDoc2)drw).GetPathName() : null));
                    Log.Info("Step 2 OK: 模板='{0}'", actualTemplatePath);

                    // ---------- Step 3:插爆炸等轴测视图 ----------
                    View isoView = RunStep3_View(sw, drw, asm, config, result);
                    if (isoView != null) rb.Track(() => SafeReleaseView(isoView));

                    // Part B(多 sheet 架构就绪化):RunMvp 路径一次性收集 isoView + 全部 movable view,
                    // Step 4/5/6 共用同一个 ctx,不再各自重复调 TryGetFirstView/CollectMovableViews。
                    // RunStep(单步调试模式)没有跨步状态,继续用各 case 独立的重新推导逻辑,不受影响。
                    var ctx = new DrawingViewContext
                    {
                        IsoView = isoView,
                        AllViews = CollectMovableViews(drw),
                    };

                    // ---------- Step 4:球标 ----------
                    RunStep4_Balloon(sw, drw, asm, explodeResult, result, ctx);

                    // ---------- Step 5:BOM ----------
                    BomTableAnnotation bomTable = RunStep5_Bom(sw, drw, asm, result, ctx);
                    if (bomTable != null) rb.Track(() => SafeReleaseBom(bomTable));

                    // ---------- Step 6:W5 智能布局 ----------
                    RunStep6_Layout(sw, drw, asm, bomTable, config, result, ctx);

                    // ---------- Step 7:工程图导出 PDF + 备份 SLDDRW ----------
                    Log.Info("Step 7/7: 工程图导出 PDF 到 config.Pdf.OutputDir");
                    try
                    {
                        result.DrawingSavedPath = TryExportPdf(drwAsModelDoc2, asm, sw, config);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(ex, "Step 7 PDF 导出失败 — 工程图保留在 SW 当前窗口");
                    }

                    // ---------- 全部 OK,提交 ----------
                    rb.Commit();
                    result.Success = true;
                    Log.Info("=== SopWorkflow.RunMvp END (success) ===");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "SopWorkflow.RunMvp 失败");
                    result.ErrorMessage = ex.Message;
                    result.Success = false;
                    Log.Info("=== SopWorkflow.RunMvp END (failed) ===");
                }
            }

            return result;
        }

        /// <summary>
        /// W7+:单步执行(分步调试用)。
        /// stepNumber 1-7,每个值对应 RunStepN_* 之一。
        /// 不走 RollbackManager(用户主动触发,失败要让用户看到中间状态)。
        /// 重要前提(对调用方):
        ///   1 / 1.5 / 2 — 需要 asm 为活动文档
        ///   3 / 4 / 5 / 6 / 7 — 需要 drw 为活动文档,且 SW 里能拿到关联 asm(用于 ShowExploded2)
        ///   真正在真机里跑分步模式时,先 RunStep(1) → RunStep(2) → 切到 drw → RunStep(3..7)
        ///   RunStep(3..7) 内部用 sw.ActiveDoc 拿 drw,用 SwApiWrapper 重新校验 asm(从 drawing 反推不容易,
        ///   失败就告诉用户"请先 RunStep 1")。
        /// </summary>
        public SopResult RunStep(ISldWorks sw, ConfigStore config, int stepNumber)
        {
            var result = new SopResult();
            if (stepNumber < 1 || stepNumber > 7)
            {
                result.ErrorMessage = "stepNumber 越界: " + stepNumber + " (有效 1-7)";
                Log.Warn("RunStep: {0}", result.ErrorMessage);
                return result;
            }
            Log.Info("=== SopWorkflow.RunStep START (step {0}/7) ===", stepNumber);

            try
            {
                if (stepNumber == 1 || stepNumber == 2)
                {
                    // 只要 asm
                    // W8+:通过注入的 IDocumentValidator 调用
                    var outcome = _validator.TryGetActiveAssembly(sw, out AssemblyDoc asm, out string msg);
                    if (outcome != ActivateOutcome.Ok)
                    {
                        result.ErrorMessage = msg;
                        Log.Warn("RunStep {0} 文档校验失败: {1}", stepNumber, msg);
                        return result;
                    }
                    try
                    {
                        if (stepNumber == 1) RunStep1_Explode(asm, config, result);
                        else /* == 2 */ _ = RunStep2_Drawing(sw, config, result);
                    }
                    finally
                    {
                        try { Marshal.ReleaseComObject(asm); } catch { /* 释放失败不阻塞 */ }
                    }
                }
                else
                {
                    // 3-7 需要 drw(活动) + asm(取不到时降级 — 走单步可能没 asm 可拿)
                    DrawingDoc drw = TryGetActiveDrawing(sw);
                    if (drw == null)
                    {
                        result.ErrorMessage = "步骤 3-7 需要工程图为活动文档,请先 RunStep(2) 建好工程图并激活它。" +
                            "(当前活动文档: " + DescribeActiveDoc(sw) + ")";
                        Log.Warn("RunStep {0}: drw 为 null,当前活动文档 = {1}", stepNumber, DescribeActiveDoc(sw));
                        return result;
                    }

                    // W12 同款修法(见 SwLayoutApiSmokeTests.FindOpenAssembly 注释):
                    // asm 和 drw 不可能同时是 ActiveDoc(SW 一次只有一个激活窗口)——
                    // 上面 TryGetActiveDrawing 已经确认 ActiveDoc 是 drw,这里如果还查 ActiveDoc
                    // 要求它是 asm,必然恒假,asm 永远拿不到。改成不要求 asm 是激活窗口,
                    // 只要求它还开着(GetDocuments() 枚举)。
                    AssemblyDoc asm = FindOpenAssembly(sw);
                    if (asm == null)
                    {
                        result.ErrorMessage = "步骤 3-7 需要装配体仍处于打开状态(用于 ShowExploded2)。" +
                            "请不要关闭原始 .SLDASM 窗口,或先 RunStep(1) 完成爆炸。";
                        Log.Warn("RunStep {0}: 打开的文档里找不到 AssemblyDoc", stepNumber);
                        return result;
                    }

                    try
                    {
                        ExplodeResult fakeExplode = new ExplodeResult();  // step 4-6 不严格需要真实 explodeResult
                        switch (stepNumber)
                        {
                            case 3: RunStep3_View(sw, drw, asm, config, result); break;
                            case 4: RunStep4_Balloon(sw, drw, asm, fakeExplode, result); break;
                            case 5: RunStep5_Bom(sw, drw, asm, result); break;
                            case 6:
                                BomTableAnnotation bomTable = null;
                                RunStep6_Layout(sw, drw, asm, bomTable, config, result);
                                break;
                            case 7:
                                try { result.DrawingSavedPath = TryExportPdf((ModelDoc2)drw, asm, sw, config); }
                                catch (Exception ex) { Log.Warn(ex, "Step 7 PDF 导出失败"); }
                                break;
                        }
                    }
                    finally
                    {
                        try { Marshal.ReleaseComObject(drw); } catch { }
                    }
                }

                result.Success = string.IsNullOrEmpty(result.ErrorMessage);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "RunStep {0} 失败", stepNumber);
                result.ErrorMessage = ex.Message;
                result.Success = false;
            }
            Log.Info("=== SopWorkflow.RunStep END (step {0}, success={1}) ===", stepNumber, result.Success);
            return result;
        }

        // ====================================================================
        //  Step helpers — RunMvp 和 RunStep 共用
        // ====================================================================

        /// <summary>Step 1:爆炸。返回 ExplodeResult(给后续 step / AI advisor 用)。</summary>
        private ExplodeResult RunStep1_Explode(AssemblyDoc asm, ConfigStore config, SopResult result)
        {
            Log.Info("Step 1/7: 爆炸");
            var explodeResult = _explode.Create(asm, config);
            result.ExplodedViewName = explodeResult.ExplodedViewName;
            result.ExplodeStepCount = explodeResult.StepCount;
            result.SkippedComponentCount = explodeResult.SkippedComponents.Count;
            if (explodeResult.StepCount == 0)
                Log.Warn("爆炸创建了 0 步 — 装配体可能无组件或全部被过滤");
            if (explodeResult.UsedAutoExplodeFallback)
                Log.Warn("⚠ 走了 AutoExplode fallback — 启发式 1 步,精度低于手动 IAddExplodeStep");
            return explodeResult;
        }

        /// <summary>Step 1.5:AI 爆炸评估(可选,config.AiAdvisor.Enabled 开关)。</summary>
        private void RunStep15_AiAdvisor(ISldWorks sw, AssemblyDoc asm, ExplodeResult explodeResult, ConfigStore config, SopResult result)
        {
            if (config.AiAdvisor == null || !config.AiAdvisor.Enabled) return;
            Log.Info("Step 1.5: AI 爆炸评估迭代");
            try
            {
                var advisor = new AiExplodeAdvisor(config.AiAdvisor);
                var advisorResult = advisor.RunIterations(sw, asm, explodeResult);
                result.AiAdvisorEnabled = advisorResult.Enabled;
                result.AiAdvisorRounds = advisorResult.Rounds.Count;
                result.AiAdvisorStepChanges = advisorResult.TotalStepChanges;
                result.AiAdvisorSkippedReason = advisorResult.SkippedReason;
                result.AiAdvisorLastError = advisorResult.LastError;
                // W7+ 修:不在这里覆盖 ExplodeStepCount。
                // advisorResult.FinalStepCount 来自 SnapshotExplodeSteps 用 cfg.GetNumberOfExplodeSteps(),
                // 但 SW 2024 这个 API 读的是 IConfiguration 的 explode view 状态,跟 ShowExploded2 切的
                // asm 级别 view 不一致 — 即使 ShowExploded2 成功切到 16 step 的新 view,
                // GetNumberOfExplodeSteps() 仍返 1(default view 的 step 数)。
                // 真值在 explodeResult.StepCount(ExplodeService.Create 内部算的,跟 SW 实际造的 step 对齐)。
                // AI 评估只是 advisory,不修改主结果。ApplyRebuild 真改了 step,下一次 RunStep1 才会反映。
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "AI 评估整体异常 — 继续原流程");
                result.AiAdvisorLastError = ex.Message;
            }
        }

        /// <summary>Step 2:工程图。返回 DrawingDoc(RunMvp 用,RunStep 单步模式丢弃)。</summary>
        private DrawingDoc RunStep2_Drawing(ISldWorks sw, ConfigStore config, SopResult result)
        {
            Log.Info("Step 2/7: 建工程图");
            string actualTemplatePath;
            DrawingDoc drw = _drawing.NewFromTemplate(sw, config, out actualTemplatePath);
            result.DrawingTemplateUsed = actualTemplatePath;
            Log.Info("Step 2 OK: 模板='{0}'", actualTemplatePath);
            return drw;
        }

        /// <summary>Step 3:插爆炸等轴测视图。失败返 null 不 throw(让球标/BOM 决定要不要继续)。</summary>
        private View RunStep3_View(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, ConfigStore config, SopResult result)
        {
            Log.Info("Step 3/7: 插爆炸等轴测主视图");
            View isoView = null;
            try
            {
                isoView = _view.InsertExplodedIso(sw, drw, asm, result.ExplodedViewName, 0.05, 0.15, 0);
                result.IsoViewInserted = true;

                // Phase 3:自动算坐标 + 写比例(iso 居中 + 按 IsoViewHeightFraction 缩放)
                if (isoView != null && _layout != null)
                {
                    var layoutOptions = BuildLayoutOptions(config);
                    var placement = _layout.ApplyIsoPlacement(drw, isoView, layoutOptions);
                    if (placement != null && placement.Success)
                    {
                        result.IsoTargetRect = placement.IsoTargetRect;
                        Log.Info("Step 3 iso 自动布局: {0}", placement.Notes);
                    }
                    else
                    {
                        Log.Info("Step 3 iso 自动布局跳过/失败: {0}", placement?.Notes ?? "(no result)");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Step 3 插爆炸视图失败 — 工程图仍可用");
                // 不 throw — 让球标/BOM/存盘决定要不要继续
            }
            return isoView;
        }

        /// <summary>Step 4:球标。需要 isoView != null,否则跳过。</summary>
        private void RunStep4_Balloon(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, ExplodeResult explodeResult, SopResult result)
        {
            Log.Info("Step 4/7: 球标(在爆炸视图上)");
            // 拿第一个 view 当 isoView(RunStep 单步时拿不到 RunMvp 时的引用,只能从 drw 重新拿)
            View isoView = TryGetFirstView(drw);
            if (isoView == null)
            {
                Log.Info("Step 4 跳过:drw 没有 view(可能 M4 还没跑过)");
                return;
            }
            try
            {
                string explodeView = explodeResult?.ExplodedViewName ?? "Default_SOP_Explode_Auto";
                try { asm.ShowExploded2(true, explodeView); }
                catch (Exception sx) { Log.Warn(sx, "ShowExploded2(true, {0}) 失败 — AutoBalloon5 可能受影响", explodeView); }
                int balloonCount = _balloon.ApplyAutoBalloon(drw, isoView);
                result.BalloonCount = balloonCount;
                Log.Info("球标插入: {0} 个", balloonCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Step 4 球标失败");
            }
        }

        /// <summary>
        /// Part B(多 sheet 架构就绪化)—— RunMvp 专用重载:用 ctx.IsoView 代替重新调 TryGetFirstView(drw)。
        /// 逻辑跟无 ctx 版本完全一致,只是 view 来源换成已经在 Step 3 收集好的 ctx。
        /// </summary>
        private void RunStep4_Balloon(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, ExplodeResult explodeResult, SopResult result, DrawingViewContext ctx)
        {
            Log.Info("Step 4/7: 球标(在爆炸视图上)");
            View isoView = ctx?.IsoView;
            if (isoView == null)
            {
                Log.Info("Step 4 跳过:ctx 没有 isoView(可能 M4 还没跑过)");
                return;
            }
            try
            {
                string explodeView = explodeResult?.ExplodedViewName ?? "Default_SOP_Explode_Auto";
                try { asm.ShowExploded2(true, explodeView); }
                catch (Exception sx) { Log.Warn(sx, "ShowExploded2(true, {0}) 失败 — AutoBalloon5 可能受影响", explodeView); }
                int balloonCount = _balloon.ApplyAutoBalloon(drw, isoView);
                result.BalloonCount = balloonCount;
                Log.Info("球标插入: {0} 个", balloonCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Step 4 球标失败");
            }
        }

        /// <summary>Step 5:BOM。需要 isoView != null,否则跳过。</summary>
        private BomTableAnnotation RunStep5_Bom(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, SopResult result)
        {
            Log.Info("Step 5/7: 插 BOM(在爆炸视图上)");
            View isoView = TryGetFirstView(drw);
            if (isoView == null)
            {
                Log.Info("Step 5 跳过:drw 没有 view(可能 M4 还没跑过)");
                return null;
            }
            try
            {
                // 强制进爆炸显示,跟 RunMvp 一致
                string explodeView = "Default_SOP_Explode_Auto";
                try { asm.ShowExploded2(true, explodeView); }
                catch (Exception sx) { Log.Warn(sx, "ShowExploded2 失败 — InsertBomTable4 可能受影响"); }
                string currentCfg = ((IModelDoc2)asm).ConfigurationManager.ActiveConfiguration.Name;
                var bomResult = _bom.ApplyBomTable(drw, isoView, currentCfg);
                result.BomInserted = bomResult.Success;
                Log.Info("BOM 插入: success={0}, template='{1}'", bomResult.Success, bomResult.TemplateUsed);
                return bomResult.BomTable;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Step 5 BOM 失败");
                return null;
            }
        }

        /// <summary>
        /// Part B(多 sheet 架构就绪化)—— RunMvp 专用重载:用 ctx.IsoView 代替重新调 TryGetFirstView(drw)。
        /// 逻辑跟无 ctx 版本完全一致,只是 view 来源换成已经在 Step 3 收集好的 ctx。
        /// </summary>
        private BomTableAnnotation RunStep5_Bom(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, SopResult result, DrawingViewContext ctx)
        {
            Log.Info("Step 5/7: 插 BOM(在爆炸视图上)");
            View isoView = ctx?.IsoView;
            if (isoView == null)
            {
                Log.Info("Step 5 跳过:ctx 没有 isoView(可能 M4 还没跑过)");
                return null;
            }
            try
            {
                // 强制进爆炸显示,跟 RunMvp 一致
                string explodeView = "Default_SOP_Explode_Auto";
                try { asm.ShowExploded2(true, explodeView); }
                catch (Exception sx) { Log.Warn(sx, "ShowExploded2 失败 — InsertBomTable4 可能受影响"); }
                string currentCfg = ((IModelDoc2)asm).ConfigurationManager.ActiveConfiguration.Name;
                var bomResult = _bom.ApplyBomTable(drw, isoView, currentCfg);
                result.BomInserted = bomResult.Success;
                Log.Info("BOM 插入: success={0}, template='{1}'", bomResult.Success, bomResult.TemplateUsed);
                if (ctx != null) ctx.BomTable = bomResult.BomTable;
                return bomResult.BomTable;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Step 5 BOM 失败");
                return null;
            }
        }

        /// <summary>Step 6:W5 智能布局。需要 _layout != null 且 drw 有 view。</summary>
        private void RunStep6_Layout(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, BomTableAnnotation bomTable, ConfigStore config, SopResult result)
        {
            if (_layout == null)
            {
                Log.Info("Step 6 跳过:_layout 未注入");
                return;
            }
            Log.Info("Step 6/7: W5 智能布局(碰撞避让 + 超界缩放)");
            try
            {
                // W10+ 修复:收集 drw 所有有效 model view(不只是第一个 iso)。
                // 之前只传 1 个 isoView,其他 ortho view(P7b 创的 3 个)不参与 layout,
                // 整体居中算法 union bbox 只有 1 个 view,效果微弱。
                // 现在收集所有 IsModelLoaded=true + SuppressState=0 的 view。
                var views = CollectMovableViews(drw);
                if (views.Length == 0)
                {
                    Log.Info("Step 6 跳过:drw 没有 model view");
                    return;
                }
                Log.Info("Step 6:收集到 {0} 个 movable view(layout 参与)", views.Length);

                var layoutOptions = BuildLayoutOptions(config);
                var layoutResult = _layout.ApplyLayout(drw, views, bomTable, layoutOptions);
                result.LayoutApplied = layoutResult.Success;
                result.LayoutElementsCollected = layoutResult.ElementsCollected;
                result.LayoutElementsApplied = layoutResult.ElementsApplied;
                result.LayoutRemainingCollisions = layoutResult.Avoidance?.RemainingCollisions ?? 0;
                result.LayoutNotes = layoutResult.Notes;
                Log.Info("布局完成: collected={0} applied={1} remaining={2}",
                    result.LayoutElementsCollected, result.LayoutElementsApplied, result.LayoutRemainingCollisions);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Step 6 智能布局失败");
            }
        }

        /// <summary>
        /// Part B(多 sheet 架构就绪化)—— RunMvp 专用重载:用 ctx.AllViews 代替重新调 CollectMovableViews(drw)。
        /// 逻辑跟无 ctx 版本完全一致,只是 view 列表来源换成已经在 Step 3 收集好的 ctx。
        /// </summary>
        private void RunStep6_Layout(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, BomTableAnnotation bomTable, ConfigStore config, SopResult result, DrawingViewContext ctx)
        {
            if (_layout == null)
            {
                Log.Info("Step 6 跳过:_layout 未注入");
                return;
            }
            Log.Info("Step 6/7: W5 智能布局(碰撞避让 + 超界缩放)");
            try
            {
                var views = ctx?.AllViews ?? new View[0];
                if (views.Length == 0)
                {
                    Log.Info("Step 6 跳过:ctx 没有 model view");
                    return;
                }
                Log.Info("Step 6:收集到 {0} 个 movable view(layout 参与)", views.Length);

                var layoutOptions = BuildLayoutOptions(config);
                var layoutResult = _layout.ApplyLayout(drw, views, bomTable, layoutOptions);
                result.LayoutApplied = layoutResult.Success;
                result.LayoutElementsCollected = layoutResult.ElementsCollected;
                result.LayoutElementsApplied = layoutResult.ElementsApplied;
                result.LayoutRemainingCollisions = layoutResult.Avoidance?.RemainingCollisions ?? 0;
                result.LayoutNotes = layoutResult.Notes;
                Log.Info("布局完成: collected={0} applied={1} remaining={2}",
                    result.LayoutElementsCollected, result.LayoutElementsApplied, result.LayoutRemainingCollisions);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Step 6 智能布局失败");
            }
        }

        /// <summary>
        /// W11+ 从 ConfigStore.Layout(LayoutOptionsConfig)转 LayoutOptions(运行时 POCO)。
        /// Infrastructure 项目不能反向依赖 Layout(DependencyRule:Host → Orchestration → Services/Layout/UI → Adapter),
        /// 所以 LayoutOptions(运行时)在 Orchestration 引用 Layout,这里手动字段映射而非直接 new LayoutOptions(config)。
        /// 缺字段 → 沿用 LayoutOptions.Default 的值(字段已有默认值,逐项 copy 即可)。
        /// </summary>
        private static LayoutOptions BuildLayoutOptions(ConfigStore config)
        {
            var opt = LayoutOptions.Default;
            if (config == null) return opt;
            var src = config.Layout;
            if (src == null) return opt;

            opt.TitleBlockWidthMeters = src.TitleBlockWidthMeters;
            opt.TitleBlockHeightMeters = src.TitleBlockHeightMeters;
            opt.IsoViewHeightFraction = src.IsoViewHeightFraction;
            opt.IsoMinHeightMeters = src.IsoMinHeightMeters;
            opt.BomReservedWidthMeters = src.BomReservedWidthMeters;
            opt.BomReservedHeightMeters = src.BomReservedHeightMeters;
            opt.PaperSize = src.PaperSize;
            return opt;
        }

        /// <summary>取 drw 的第一个 model view(用于 Step 4/5/6 拿 isoView)。
        /// W7+ 修:之前 drw.GetViews() 转 (object[]) 错 — 实际返 IEnumerable 不是数组;
        /// 而且第一个元素可能是 "图纸1" sheet,不是真 view。沿用 ViewService.FindFirstModelView 的逻辑:
        ///   GetViews() as IEnumerable → foreach + IsRealModelViewName 过滤
        ///   失败 fallback GetFirstView() + GetNextView() 链
        /// IsRealModelViewName 内联一份 — 简单"图纸"/"Sheet" 前缀排除。</summary>
        private static View TryGetFirstView(DrawingDoc drw)
        {
            if (drw == null) return null;
            try
            {
                int count = drw.GetViewCount();
                Log.Info("TryGetFirstView: drw.GetViewCount={0}", count);
                if (count == 0) return null;

                // 路径 1:GetViews() 当 IEnumerable 迭代 + 过滤 sheet
                try
                {
                    object viewsObj = drw.GetViews();
                    if (viewsObj is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var o in enumerable)
                        {
                            if (o is View v && IsRealModelViewName(v.GetName2()))
                                return v;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "TryGetFirstView: GetViews() 路径失败,退到 GetFirstView 链");
                }

                // 路径 2:GetFirstView + GetNextView 链
                View cur = (View)drw.GetFirstView();
                int guard = 0;
                while (cur != null && guard < 50)
                {
                    guard++;
                    if (IsRealModelViewName(cur.GetName2()))
                        return cur;
                    object nextObj = cur.GetNextView();
                    if (nextObj == null) break;
                    cur = (View)nextObj;
                }
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "TryGetFirstView 整体异常");
                return null;
            }
        }

        private static bool IsRealModelViewName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("Sheet", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("图纸", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>
        /// W10+ 收集 drw 所有 movable view(IsModelLoaded=true + SuppressState=0 + 名字不像 sheet)。
        /// 让 Step 6 layout 算法看到所有 4 个 model view(iso + 3 ortho),union bbox 居中才有意义。
        ///
        /// W14 修:真机日志证实路径 1(GetViews() 转 IEnumerable)在这台机器上恒定 0 命中
        /// (无异常,foreach 就是拿不到匹配 view)——TryGetFirstView 靠路径 2(GetFirstView+
        /// GetNextView 链)才拿到 view,但这里之前只有路径 1,导致 Step 6 每次都以
        /// "没有 model view" 跳过,W5 碰撞避让 + CenterMovableElements 从未真正执行过。
        /// 补上路径 2,收集全部匹配 view(不是像 TryGetFirstView 那样只拿第一个)。
        /// </summary>
        private static View[] CollectMovableViews(DrawingDoc drw)
        {
            var list = new System.Collections.Generic.List<View>();
            try
            {
                // 路径 1:GetViews() 当 IEnumerable 迭代
                try
                {
                    object viewsObj = drw.GetViews();
                    if (viewsObj is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var o in enumerable)
                        {
                            if (o is View v && IsRealModelViewName(v.GetName2()))
                                list.Add(v);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "CollectMovableViews: GetViews() 路径失败,退到 GetFirstView 链");
                }

                // 路径 2:GetFirstView + GetNextView 链(真机上这条才真正拿到 view)
                if (list.Count == 0)
                {
                    View cur = (View)drw.GetFirstView();
                    int guard = 0;
                    while (cur != null && guard < 50)
                    {
                        guard++;
                        if (IsRealModelViewName(cur.GetName2()))
                            list.Add(cur);
                        object nextObj = cur.GetNextView();
                        if (nextObj == null) break;
                        cur = (View)nextObj;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "CollectMovableViews 失败");
            }
            return list.ToArray();
        }

        /// <summary>取活动工程图。返 null 如果 active doc 不是 drw。</summary>
        private static DrawingDoc TryGetActiveDrawing(ISldWorks sw)
        {
            try
            {
                ModelDoc2 doc = (ModelDoc2)sw.ActiveDoc;
                if (doc == null) return null;
                if (doc.GetType() != (int)swDocumentTypes_e.swDocDRAWING) return null;
                return (DrawingDoc)doc;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "TryGetActiveDrawing 失败");
                return null;
            }
        }

        /// <summary>
        /// W12 同款修法:asm 不要求是 ActiveDoc(那个位置已经被 drw 占了),只要求它还开着。
        /// 先试 ActiveDoc(万一没有 drw 的场景复用这个方法),拿不到再从 GetDocuments() 枚举。
        /// </summary>
        private static AssemblyDoc FindOpenAssembly(ISldWorks sw)
        {
            if (sw == null) return null;
            var active = sw.ActiveDoc as AssemblyDoc;
            if (active != null) return active;

            try
            {
                object[] docs = (object[])sw.GetDocuments();
                foreach (object d in docs ?? new object[0])
                {
                    if (d is AssemblyDoc asm) return asm;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "FindOpenAssembly: GetDocuments 失败");
            }
            return null;
        }

        /// <summary>诊断用:描述当前 ActiveDoc 是什么,失败时给用户提示排查方向。</summary>
        private static string DescribeActiveDoc(ISldWorks sw)
        {
            try
            {
                ModelDoc2 doc = (ModelDoc2)sw.ActiveDoc;
                if (doc == null) return "(无, ActiveDoc==null)";
                return doc.GetTitle() + " (type=" + doc.GetType() + ")";
            }
            catch (Exception ex)
            {
                return "(读取失败: " + ex.Message + ")";
            }
        }

        /// <summary>回滚时关闭工程图。</summary>
        private static void SafeCloseDoc(ISldWorks sw, string title)
        {
            try
            {
                if (string.IsNullOrEmpty(title))
                {
                    Log.Info("回滚: 工程图无 path,跳过 CloseDoc");
                    return;
                }
                Log.Info("回滚: 关闭工程图 '{0}'", title);
                sw.CloseDoc(title);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "回滚关闭工程图 '{0}' 失败", title);
            }
        }

        private static void SafeReleaseView(View v)
        {
            try { if (v != null) Marshal.ReleaseComObject(v); }
            catch (Exception ex) { Log.Warn(ex, "ReleaseComObject(View) 失败"); }
        }

        private static void SafeReleaseBom(BomTableAnnotation bom)
        {
            try { if (bom != null) Marshal.ReleaseComObject(bom); }
            catch (Exception ex) { Log.Warn(ex, "ReleaseComObject(BOM) 失败"); }
        }

        /// <summary>
        /// W4.1:工程图导出为 PDF(主输出),同时备份 .SLDDRW 源文件方便用户手动再编辑。
        /// PDF 路径:config.Pdf.OutputDir / &lt;装配体图号或文件名&gt;.PDF
        /// 嵌字体(swPDFExportEmbedFonts = 1)+ 导出所有 sheet + 静默模式。
        /// 失败返 null,不 throw。
        /// </summary>
        private string TryExportPdf(ModelDoc2 drw, AssemblyDoc asm, ISldWorks sw, ConfigStore config)
        {
            if (drw == null) return null;

            string partNumber = GetPartNumber(asm);
            if (string.IsNullOrEmpty(partNumber))
            {
                string asmPath = ((IModelDoc2)asm).GetPathName();
                partNumber = string.IsNullOrEmpty(asmPath)
                    ? "Drawing_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                    : Path.GetFileNameWithoutExtension(asmPath);
                Log.Warn("装配体图号(PartNumber)为空,使用 fallback: '{0}'", partNumber);
            }

            string outDir = config?.Pdf?.OutputDir;
            if (string.IsNullOrEmpty(outDir)) outDir = @"D:\SOP_Output";

            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex)
            {
                Log.Warn(ex, "CreateDirectory '{0}' 失败,改用临时目录", outDir);
                outDir = Path.Combine(Path.GetTempPath(), "SOP_Output");
                Directory.CreateDirectory(outDir);
            }

            // ===== 主输出:PDF =====
            string pdfPath = Path.Combine(outDir, partNumber + ".PDF");
            try
            {
                drw.Extension.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swPDFExportEmbedFonts,
                    (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified,
                    true);
                Log.Info("PDF 嵌字体 toggle 已开");

                var exportData = (ExportPdfData)sw.GetExportFileData(
                    (int)swExportDataFileType_e.swExportPdfData);
                exportData.SetSheets(
                    (int)swExportDataSheetsToExport_e.swExportData_ExportAllSheets,
                    null);
                Log.Info("ExportPdfData: sheets=全部");

                int errs = 0, warns = 0;
                bool ok = drw.Extension.SaveAs(
                    pdfPath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    exportData, ref errs, ref warns);
                if (!ok)
                {
                    Log.Warn("PDF SaveAs 返 false: '{0}' errors={1} warnings={2}", pdfPath, errs, warns);
                    return null;
                }
                Log.Info("✅ PDF 导出成功: '{0}' (errors={1}, warnings={2})", pdfPath, errs, warns);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "PDF 导出失败: '{0}'", pdfPath);
                return null;
            }

            // ===== 备份:.SLDDRW 源文件 =====
            try
            {
                string slddrwPath = Path.Combine(outDir, partNumber + ".SLDDRW");
                int errs2 = 0, warns2 = 0;
                bool ok2 = drw.Extension.SaveAs(
                    slddrwPath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null, ref errs2, ref warns2);
                if (ok2) Log.Info("SLDDRW 备份: '{0}'", slddrwPath);
                else Log.Warn("SLDDRW 备份 SaveAs 返 false: '{0}' (errors={1}, warnings={2})", slddrwPath, errs2, warns2);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "SLDDRW 备份失败(非致命)");
            }

            return pdfPath;
        }

        /// <summary>
        /// 从装配体自定义属性读 PartNumber。失败返 null。
        /// W3 阶段:不取;MVP fallback 到文件名。
        /// </summary>
        private string GetPartNumber(AssemblyDoc asm)
        {
            return null;
        }
    }
}
