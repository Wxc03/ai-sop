using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NLog;
using SolidWorks.Interop.sldworks;
using SwSopAddin.Adapter;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Services
{
    /// <summary>
    /// M2 爆炸引擎 — W6-fix:AutoExplode fallback。
    /// W3-fix 2 已知 IAddExplodeStep 在 BenchVice/PressTool(多组件装配)返 null,
    ///   Piston(单件)work。Root cause:Select2(false,0)+IAddExplodeStep 模式 SW 在多组件装配上 workless。
    /// 修复:多组件上 IAddExplodeStep 全 null 时,fallback 到 asm.AutoExplode()(SW 启发式,1 步启发式).
    /// F4 过滤保持不变(ShouldSkip 读 SkipNamePrefixes)。
    /// </summary>
    public class ExplodeService : IExplodeService
    {
        private static readonly Logger Log = Logging.ForType(typeof(ExplodeService));

        /// <summary>
        /// Part A Phase 2 — dispatcher。config.Explode.Style==Legacy 走老逻辑;否则试 SmartHybridCreate,
        /// 任何异常或 0 个 placement 生效都兜底回退 LegacyCreate,保证 SmartHybrid 出问题不影响 7 步流水线。
        /// </summary>
        public ExplodeResult Create(AssemblyDoc asm, ConfigStore config)
        {
            if (asm == null) throw new ArgumentNullException(nameof(asm));
            if (config == null) throw new ArgumentNullException(nameof(config));

            if (config.Explode.Style == ExplodeStyle.Legacy)
            {
                return LegacyCreate(asm, config);
            }

            try
            {
                return SmartHybridCreate(asm, config);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Create: SmartHybridCreate 异常 — 回退 LegacyCreate");
                return LegacyCreate(asm, config);
            }
        }

        /// <summary>
        /// W2.2 起的原始实现,3-fallback 路径(IAddExplodeStep 逐件 → AutoExplode → TranslateComponent)。
        /// Part A Phase 2 重构:整体原样保留,只改了访问修饰符和方法名(public Create → private LegacyCreate)。
        /// </summary>
        private ExplodeResult LegacyCreate(AssemblyDoc asm, ConfigStore config)
        {
            if (asm == null) throw new ArgumentNullException(nameof(asm));
            if (config == null) throw new ArgumentNullException(nameof(config));

            Log.Info("ExplodeService.Create start: distance={0}mm, skipPrefixes=[{1}]",
                config.Explode.DefaultDistanceMm,
                string.Join(",", config.Explode.SkipNamePrefixes ?? new string[0]));

            var result = new ExplodeResult();

            // 1. 拿当前激活配置
            IConfiguration cfg = SwApiWrapper.GetActiveConfiguration(asm);
            string cfgName = cfg.Name;
            result.ExplodedViewName = cfgName + "_SOP_Explode";
            Log.Info("Target config='{0}'", cfgName);

            // 2. 列组件
            object[] compsRaw = (object[])asm.GetComponents(true);
            int totalComps = compsRaw?.Length ?? 0;
            Log.Info("Total components: {0}", totalComps);

            if (totalComps == 0)
            {
                Log.Warn("装配体没有子件,无需爆炸");
                return result;
            }

            // Part A Phase 3+ 修复(2026-07-12 真机诊断确认,Error=2/NoExplodeView 100% 复现):
            // IAddExplodeStep 在配置上还没有任何已存在爆炸视图时会统一失败。以前这里先跑完整个
            // 逐组件循环、最后才 CreateExplodedView() —— 顺序反了。改成循环开始前先建好 + 激活。
            string preCreatedViewName = CreateAndActivateExplodedView(asm, cfgName);
            if (!string.IsNullOrEmpty(preCreatedViewName))
            {
                result.ExplodedViewName = preCreatedViewName;
            }

            // 3. 过滤 + 逐组件加 explode step
            int processed = 0;
            int skippedCount = 0;
            int failed = 0;

            // W6-fix:10 个组件中 8 个是 PATTERN 副本,SW 默认 light-weight。
            // Light-weight 组件 IAddExplodeStep 处理不了(返 null),先强制解析成 fully-resolved 再 explode。
            try
            {
                asm.ResolveAllLightWeightComponents(false);
                Log.Info("ResolveAllLightWeightComponents 调用成功(强制 light-weight → fully-resolved)");
                // 重新拿组件列表(解析后状态可能变化)
                compsRaw = (object[])asm.GetComponents(true);
                Log.Info("解析后重新拿 components: {0} 个", compsRaw?.Length ?? 0);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ResolveAllLightWeightComponents 失败 — 继续原流程");
            }

            foreach (object co in compsRaw)
            {
                IComponent2 c = (IComponent2)co;
                try
                {
                    string baseName = GetComponentBaseName(c);
                    if (ShouldSkip(c, baseName, config))
                    {
                        result.SkippedComponents.Add(c.Name2 + " (file=" + baseName + ")");
                        skippedCount++;
                        Log.Debug("Skip: {0} (file='{1}')", c.Name2, baseName);
                        continue;
                    }

                    // 选单个组件(Mark=0 才是 SW API 有效的)
                    bool sel = c.Select2(false, 0);
                    if (!sel)
                    {
                        Log.Warn("无法选中组件: {0}", c.Name2);
                        failed++;
                        continue;
                    }

                    // 退到 W3.5 老 API:IAddExplodeStep(4 参数,返 ExplodeStep 强类型)
                    // 之前 AddExplodeStep2 9 参数全部 errs=2,这个 4 参数 W3.5 已知 work
                    ExplodeStep step = (ExplodeStep)cfg.IAddExplodeStep(
                        config.Explode.DefaultDistanceMm,  // ExplDist
                        false,                               // ReverseDir
                        false,                               // RigidSubassembly
                        false);                              // ExplodeRelated
                    if (step != null)
                    {
                        processed++;
                        Marshal.ReleaseComObject(step);
                    }
                    else
                    {
                        Log.Warn("IAddExplodeStep 返 null: {0}", c.Name2);
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "爆炸 {0} 异常", c.Name2);
                    failed++;
                }
                finally
                {
                    if (c != null) Marshal.ReleaseComObject(c);
                }
            }
            result.StepCount = processed;
            Log.Info("ExplodeService.Create done(phase 1): processed={0}, skipped={1}, failed={2}, totalComps={3}",
                processed, skippedCount, failed, totalComps);

            if (skippedCount > 0)
            {
                Log.Info("Skipped components ({0}): {1}",
                    result.SkippedComponents.Count,
                    string.Join(", ", result.SkippedComponents));
            }

            // ===== W6-fix M2:AutoExplode fallback =====
            // 当 IAddExplodeStep 全部失败(processed=0 但还有未跳过组件)时,
            // 退到 SW 自带的 AutoExplode 启发式 — 至少让 asm 进入 exploded 显示状态。
            // AutoExplode 不创建持久 ExplodeStep,只是把 asm 暂时性"撑开",后续 ShowExploded2(true) 可用。
            if (processed == 0 && failed > 0)
            {
                Log.Warn("IAddExplodeStep 全部失败 (processed=0, failed={0}, total={1}) — 试 AutoExplode 启发式",
                    failed, totalComps);
                try
                {
                    bool autoOk = asm.AutoExplode();
                    Log.Info("AutoExplode 返 {0}", autoOk);
                    if (autoOk)
                    {
                        processed = 1;
                        result.StepCount = 1;
                        result.ExplodedViewName = cfgName + "_SOP_Explode_Auto";
                        result.UsedAutoExplodeFallback = true;  // 标记,UI / Log 用
                        Log.Info("AutoExplode 成功 — StepCount 视作 1,ExplodedViewName='{0}'",
                            result.ExplodedViewName);

                        // W6-fix:SW 2024 interop 没暴露 AssemblyDoc.AddExplodeView,跳过持久化命名
                        // 后续 M4 ViewService 拿 explode view 名 时 GetExplodedViewNames2 返空,会退到 Default 配置
                        // (asm 已被 AutoExplode 撑开到 exploded 显示状态,view 不会"未爆炸")
                    }
                    else
                    {
                        Log.Warn("AutoExplode 返 false — M2 0 步,后续 M4 视图不会爆炸显示");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "AutoExplode 失败 — M2 0 步");
                }
            }

            // ===== W6-fix M2 第 3 方案:TranslateComponent 手动造真 step =====
            // AutoExplode 不创建真实 ExplodeStep,AutoBalloon5 / InsertBomTable4 找不到组件。
            // 宏录制显示:用户手动选中组件 + Part.TranslateComponent(无参)造真 step。
            // 注意:这里不等 processed==0,AutoExplode 会设 processed=1 但没真 step,这里继续走造真 step。
            if (failed > 0 && result.SkippedComponents.Count < totalComps)
            {
                Log.Warn("AutoExplode 没造真 step — 试 TranslateComponent(0,0,0.05) 逐组件造 step");
                int translateOk = 0;
                foreach (object co in compsRaw)
                {
                    IComponent2 c = (IComponent2)co;
                    try
                    {
                        string baseName = GetComponentBaseName(c);
                        if (ShouldSkip(c, baseName, config)) continue;

                        // 选中此组件(宏录制里模式:先选组件,再选 asm,然后 TranslateComponent)
                        string asmTitle = ((IModelDoc2)asm).GetTitle();
                        ((ModelDoc2)asm).Extension.SelectByID2(
                            c.Name2 + "@" + asmTitle,
                            "COMPONENT", 0, 0, 0, true, 0, null, 0);
                        ((ModelDoc2)asm).Extension.SelectByID2(asmTitle, "COMPONENT", 0, 0, 0, true, 0, null, 0);

                        // 沿 Z+ 5cm 推一下(类似用户拖动) — 宏里是无参版,用 0,0,0.05
                        try
                        {
                            asm.TranslateComponent();
                            translateOk++;
                            Log.Info("TranslateComponent 成功: {0} (z+5cm via selection)", c.Name2);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn(ex, "TranslateComponent 失败: {0}", c.Name2);
                        }
                        finally
                        {
                            try { ((ModelDoc2)asm).ClearSelection2(true); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(ex, "TranslateComponent 选中失败");
                    }
                    finally
                    {
                        if (c != null) Marshal.ReleaseComObject(c);
                    }
                }
                if (translateOk > 0)
                {
                    processed = translateOk;
                    result.StepCount = translateOk;
                    result.ExplodedViewName = cfgName + "_SOP_Explode_Translate";
                    result.UsedAutoExplodeFallback = false;
                    result.UsedTranslateFallback = true;  // 新标记
                    Log.Info("TranslateComponent 造 {0} 个真 step", translateOk);

                    // W7+ 修复 TranslateComponent → 16 step → 但 SW 的 active explode view 仍是 default,
                    // 所以 GetNumberOfExplodeSteps() 返 1(默认 view 的 step 数)。
                    // 完整流程:CreateExplodedView() 创建新 view + ShowExploded2(true, name) 激活它。
                    try
                    {
                        bool created = asm.CreateExplodedView();
                        if (!created)
                        {
                            Log.Warn("ExplodeService: CreateExplodedView() 返 false");
                        }

                        // 拿所有 explode view names,找最新创建的
                        // (CreateExplodedView 不返回 name,得从 names 列表里挑)
                        object namesObj = asm.GetExplodedViewNames2(cfgName);
                        string newViewName = null;
                        if (namesObj is string[] names && names.Length > 0)
                        {
                            // 找到我们的 _SOP_Explode_Translate 那个
                            foreach (var n in names)
                            {
                                if (n != null && n.Contains("_SOP_Explode_Translate"))
                                {
                                    newViewName = n;
                                    break;
                                }
                            }
                            if (newViewName == null)
                            {
                                // 兜底:取最后一个(最新创建的通常在末尾)
                                newViewName = names[names.Length - 1];
                            }
                        }
                        Log.Info("ExplodeService: 找到 explode view name='{0}'", newViewName ?? "(null)");

                        if (!string.IsNullOrEmpty(newViewName))
                        {
                            // W7+ 修复:report 真实 view 名(SW 自动命名,可能是"爆炸视图2"),
                            // 而不是预期的 "_SOP_Explode_Translate"。后续 Step 3 ViewService
                            // 必须用这个真名 ShowExploded2,否则切到错的 view → AutoBalloon5 0 个。
                            result.ExplodedViewName = newViewName;
                            // 关键:ShowExploded2(true, name) 切到新 view,
                            // 之后 GetNumberOfExplodeSteps() 才读这个 view 的 step 数
                            bool shown = asm.ShowExploded2(true, newViewName);
                            if (shown)
                            {
                                Log.Info("ExplodeService: ShowExploded2(true, '{0}') 成功 — 16 step 现在是 active", newViewName);
                            }
                            else
                            {
                                Log.Warn("ExplodeService: ShowExploded2 返 false — active view 可能没切");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(ex, "ExplodeService: CreateExplodedView/ShowExploded2 异常");
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Part A Phase 2 — SmartHybrid 爆炸:几何规划(ExplodeLayoutPlanner)+ 落位执行(IExplodeApplier)。
        /// 0 个 placement 或全部 ApplyPlacement 失败都直接回退 LegacyCreate(由调用方 Create 或本方法自己判断)。
        /// </summary>
        private ExplodeResult SmartHybridCreate(AssemblyDoc asm, ConfigStore config)
        {
            Log.Info("SmartHybridCreate start");

            IConfiguration cfg = SwApiWrapper.GetActiveConfiguration(asm);
            string cfgName = cfg.Name;

            try
            {
                asm.ResolveAllLightWeightComponents(false);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "SmartHybridCreate: ResolveAllLightWeightComponents 失败 — 继续原流程");
            }

            object[] compsRaw = (object[])asm.GetComponents(true);
            int totalComps = compsRaw?.Length ?? 0;
            Log.Info("SmartHybridCreate: total components: {0}", totalComps);

            if (totalComps == 0)
            {
                Log.Warn("SmartHybridCreate: 装配体没有子件,无需爆炸");
                return new ExplodeResult { ExplodedViewName = cfgName + "_SOP_Explode_SmartHybrid" };
            }

            var geoms = new List<ComponentGeometry>();
            var skippedNames = new List<string>();
            for (int i = 0; i < compsRaw.Length; i++)
            {
                IComponent2 c = compsRaw[i] as IComponent2;
                if (c == null) continue;
                try
                {
                    string baseName = GetComponentBaseName(c);
                    bool skip = ShouldSkip(c, baseName, config);
                    if (skip)
                    {
                        skippedNames.Add(c.Name2 + " (file=" + baseName + ")");
                    }

                    double[] box = skip ? null : SwApiWrapper.GetComponentWorldBox(c);
                    geoms.Add(new ComponentGeometry
                    {
                        ComponentName = c.Name2,
                        BaseName = baseName,
                        Box = box,
                        IsSkipped = skip,
                        Index = i
                    });
                }
                finally
                {
                    Marshal.ReleaseComObject(c);
                }
            }

            ExplodeLayoutResult plan = ExplodeLayoutPlanner.Plan(geoms, config.Explode);
            Log.Info("SmartHybridCreate: Plan 产出 {0} 个 placement(总组件 {1}, 跳过 {2})",
                plan.Placements.Count, totalComps, skippedNames.Count);

            // Part A Phase 3a 诊断日志:逐组件记录分类/分组/方向的真实数据,
            // 用于确认"物理同轴的两个组件(如导柱/导套)是否被判成不同 Role、从而拿到不一致方向"这一假设,
            // 而不必再靠真机截图肉眼推测(见 memory / 计划 Part A Phase 3+)。
            foreach (var p in plan.Placements)
            {
                var g = geoms.FirstOrDefault(x => x.ComponentName == p.ComponentName);
                string dimsMm = g != null
                    ? string.Join(",", g.Dims.OrderBy(d => d).Select(d => (d * 1000.0).ToString("F1")))
                    : "?";
                string axis = (g != null && p.Role == ExplodeRole.Fastener)
                    ? string.Join(",", ExplodeLayoutPlanner.PrimaryAxis(g).Select(a => a.ToString("F2")))
                    : "-";
                Log.Info(
                    "SmartHybridCreate diag: {0} baseName={1} dimsMm=[{2}] role={3} groupId={4} stackOrder={5} primaryAxis=[{6}] dir=[{7:F3},{8:F3},{9:F3}] distMm={10:F1}",
                    p.ComponentName, g?.BaseName ?? "?", dimsMm, p.Role, p.CoaxialGroupId, p.StackOrder, axis,
                    p.Direction[0], p.Direction[1], p.Direction[2], p.DistanceMeters * 1000.0);
            }

            if (plan.Placements.Count == 0)
            {
                Log.Warn("SmartHybridCreate: Plan 空结果 — 回退 LegacyCreate");
                return LegacyCreate(asm, config);
            }

            // Part A Phase 3+ 修复(2026-07-12 真机诊断确认,Error=2/NoExplodeView 100% 复现):
            // 以前是逐组件加完 step 之后才 CreateExplodedView() —— 顺序反了,导致 IAddExplodeStep
            // 在"配置上还没有任何已存在爆炸视图"时统一返回 null。改成 ApplyPlan 之前先建好 + 激活,
            // 这样每个 IAddExplodeStep 调用都能挂到一个已存在的 active explode view 上。
            string preCreatedViewName = CreateAndActivateExplodedView(asm, cfgName);

            IExplodeApplier applier = new SwDirectionalExplodeApplier(asm, cfg);
            int processed, failed;
            ApplyPlan(plan, applier, out processed, out failed);
            Log.Info("SmartHybridCreate: ApplyPlan done processed={0} failed={1}", processed, failed);

            if (processed == 0)
            {
                Log.Warn("SmartHybridCreate: 全部 placement 失败 — 回退 LegacyCreate");
                return LegacyCreate(asm, config);
            }

            var result = new ExplodeResult
            {
                StepCount = processed,
                SkippedComponents = skippedNames,
                ExplodedViewName = !string.IsNullOrEmpty(preCreatedViewName)
                    ? preCreatedViewName
                    : cfgName + "_SOP_Explode_SmartHybrid"
            };

            return result;
        }

        /// <summary>
        /// Part A Phase 3+ 修复(2026-07-12):在逐组件加 explode step 之前先建一个空爆炸视图并激活,
        /// 让 IAddExplodeStep/AddExplodeStep2 有一个已存在的 active explode view 可以挂 —— 真机诊断
        /// (AddExplodeStep2 的 Error out 参数)确认,没有它时两者都会统一返回 Error=2(NoExplodeView)/null。
        /// 返回创建的 view 名(SW 自动命名,如"爆炸视图2"),失败返回 null(调用方按老逻辑降级)。
        /// </summary>
        private static string CreateAndActivateExplodedView(AssemblyDoc asm, string cfgName)
        {
            try
            {
                bool created = asm.CreateExplodedView();
                if (!created)
                {
                    Log.Warn("CreateAndActivateExplodedView: CreateExplodedView() 返 false");
                }

                object namesObj = asm.GetExplodedViewNames2(cfgName);
                string viewName = null;
                if (namesObj is string[] names && names.Length > 0)
                {
                    viewName = names[names.Length - 1]; // 刚创建的通常在末尾
                }
                Log.Info("CreateAndActivateExplodedView: view name='{0}'", viewName ?? "(null)");

                if (!string.IsNullOrEmpty(viewName))
                {
                    bool shown = asm.ShowExploded2(true, viewName);
                    Log.Info("CreateAndActivateExplodedView: ShowExploded2(true, '{0}') = {1}", viewName, shown);
                }
                return viewName;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "CreateAndActivateExplodedView 异常");
                return null;
            }
        }

        /// <summary>
        /// Part A Phase 2 — 纯逻辑:逐 placement 解析组件 + 应用落位,统计成功/失败数。
        /// 抽出为静态方法方便用 RecordingExplodeApplier fake 单测,不依赖真 COM。
        /// </summary>
        internal static void ApplyPlan(ExplodeLayoutResult plan, IExplodeApplier applier, out int processed, out int failed)
        {
            processed = 0;
            failed = 0;
            if (plan == null || applier == null) return;

            foreach (var placement in plan.Placements)
            {
                object comp = applier.ResolveComponent(placement.ComponentName, placement.Index);
                if (comp == null)
                {
                    Log.Warn("ApplyPlan: ResolveComponent 找不到组件: {0}", placement.ComponentName);
                    failed++;
                    continue;
                }

                string stepName;
                bool ok = applier.ApplyPlacement(comp, placement.Direction, placement.DistanceMeters, out stepName);
                if (ok)
                {
                    processed++;
                }
                else
                {
                    Log.Warn("ApplyPlan: ApplyPlacement 失败: {0}", placement.ComponentName);
                    failed++;
                }
            }
        }

        /// <summary>
        /// F4 过滤:用 c.IGetModelDoc() 拿 model doc 路径,Path.GetFileNameWithoutExtension 拿 base name。
        /// 之前用 c.GetPathName() 返 null(GetPathName 对 light-weight 不可靠),
        /// 改用 IGetModelDoc().GetPathName()。
        /// 暴露为 internal 让 SwSopAddin.Tests 走单元测试(配合 InternalsVisibleTo)。
        /// </summary>
        internal bool ShouldSkip(IComponent2 c, string baseName, ConfigStore config)
        {
            if (string.IsNullOrEmpty(baseName)) return false;  // 虚拟组件无文件,默认不过滤

            var prefixes = config.Explode.SkipNamePrefixes;
            if (prefixes == null) return false;

            foreach (string pfx in prefixes)
            {
                if (!string.IsNullOrEmpty(pfx) && baseName.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>拿 component 对应 .sldprt 文件的 base name(无扩展名)。失败返 null。
        /// 暴露为 internal 让 SwSopAddin.Tests 走单元测试。</summary>
        internal static string GetComponentBaseName(IComponent2 c)
        {
            try
            {
                // 路径 1:c.IGetModelDoc().GetPathName() — 强类型,对 light-weight 也工作
                ModelDoc2 modelDoc = c.IGetModelDoc();
                if (modelDoc != null)
                {
                    string p = modelDoc.GetPathName();
                    if (!string.IsNullOrEmpty(p))
                    {
                        return Path.GetFileNameWithoutExtension(p);
                    }
                }
                // 路径 2:c.GetPathName() — fallback
                string p2 = c.GetPathName();
                if (!string.IsNullOrEmpty(p2))
                {
                    return Path.GetFileNameWithoutExtension(p2);
                }
                // 路径 3:c.GetImportedPath() — 外部参考的 import path
                string p3 = c.GetImportedPath();
                if (!string.IsNullOrEmpty(p3))
                {
                    return Path.GetFileNameWithoutExtension(p3);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "GetComponentBaseName 失败");
            }
            return null;
        }
    }
}
