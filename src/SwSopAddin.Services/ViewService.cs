using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NLog;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;  // swDocumentTypes_e for LoadFile2
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Services
{
    /// <summary>
    /// M4 视图插入 — W6-fix 多路径 fallback。
    /// W3.5 + W4.3 经验:
    /// - CreateDrawViewFromModelView2/3 在 BenchVice/PressTool 上返 null
    /// - InsertModelInPredefinedView 返 true 但 GetViewCount 不变(只返 sheet)
    /// - GetFirstView 返 'Sheet1' 后 GetNextView 链断
    /// W6-fix 加 3 条新路径:sw.ActivateDoc 状态切换、sw.LoadFile2 强制全加载、详细诊断
    /// </summary>
    public class ViewService : IViewService
    {
        private static readonly Logger Log = Logging.ForType(typeof(ViewService));

        public View InsertExplodedIso(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, string explodeViewName, double x, double y, double z, Sheet targetSheet = null)
        {
            if (drw == null) throw new ArgumentNullException(nameof(drw));
            if (asm == null) throw new ArgumentNullException(nameof(asm));
            if (sw == null) throw new ArgumentNullException(nameof(sw));

            string modelName = GetModelName(asm);
            Log.Info("InsertExplodedIso: model='{0}', explodeViewName='{1}'",
                modelName, explodeViewName ?? "(null → 走 GetExplodedViewNames2 fallback)");

            string asmTitle = ((IModelDoc2)asm).GetTitle();
            string drwTitle = ((IModelDoc2)drw).GetTitle();

            // W7+ 修复:用 ExplodeService.Create 实际切到的 view 名(可能是 SW 自动命名的"爆炸视图2")。
            // null/空才 fallback 到 asm.GetExplodedViewNames2 列表第一个(单步调试 / Tests 用)。
            // 关键:全程 3 处 ShowExploded2 必须用同一个 view 名,否则 P7b 后切错 view → AutoBalloon5 0 个。
            string preExplodeName = !string.IsNullOrEmpty(explodeViewName)
                ? explodeViewName
                : GetExplodeViewNameForDefault(asm);
            try
            {
                sw.ActivateDoc(asmTitle);
                Log.Info("插入 view 前先激活 asm: '{0}'", asmTitle);
            }
            catch (Exception ex) { Log.Warn(ex, "激活 asm 失败"); }
            if (!string.IsNullOrEmpty(preExplodeName))
            {
                try
                {
                    asm.ShowExploded2(true, preExplodeName);
                    Log.Info("view 创建前 ShowExploded2: '{0}' (来源:{1})",
                        preExplodeName,
                        !string.IsNullOrEmpty(explodeViewName) ? "参数" : "GetExplodedViewNames2 fallback");
                }
                catch (Exception ex) { Log.Warn(ex, "pre-ShowExploded2 失败"); }
            }
            // 切回 drawing,准备插 view
            try { sw.ActivateDoc(drwTitle); } catch { }

            // Part B(多 sheet 架构就绪化):targetSheet 非 null 时先切到目标 sheet,
            // 保证后续 P1-P8 的插入落在指定 sheet 上(今天单 sheet 流程恒为 null,零行为变化)。
            if (targetSheet != null)
            {
                try
                {
                    bool activated = drw.ActivateSheet(targetSheet.GetName());
                    Log.Info("ActivateSheet('{0}') 返 {1}", targetSheet.GetName(), activated);
                }
                catch (Exception ex) { Log.Warn(ex, "ActivateSheet(targetSheet) 失败"); }
            }

            // P1:CreateDrawViewFromModelView2 *Isometric
            View view = TryCreate(drw, modelName, "*Isometric", x, y, z, "P1: V2 *Isometric");
            if (view == null)
            {
                // P2:*Front
                Log.Warn("P1 返 null,试 P2");
                view = TryCreate(drw, modelName, "*Front", x, y, z, "P2: V2 *Front");
            }
            if (view == null)
            {
                // P3:状态切换 — 激活 asm 再激活 drw,有时能让 SW 内部 state 复位
                Log.Warn("P2 返 null,试 P3:ActivateDoc(asm) + ActivateDoc(drw)");
                try { sw.ActivateDoc(asmTitle); }
                catch (Exception ex) { Log.Warn(ex, "P3 ActivateDoc(asm) 失败"); }
                try { sw.ActivateDoc(drwTitle); }
                catch (Exception ex) { Log.Warn(ex, "P3 ActivateDoc(drw) 失败"); }
                view = TryCreate(drw, modelName, "*Isometric", x, y, z, "P3: V2 *Isometric (after activate)");
            }
            if (view == null)
            {
                // P4:LoadFile2 强制全加载(覆盖 light-weight)
                Log.Warn("P3 返 null,试 P4:LoadFile2 强制全加载 asm");
                try
                {
                    // SW 2024 interop 的 LoadFile2(modelName, documentType) overload 返 bool(成功/失败)
                    bool loaded = sw.LoadFile2(modelName, "SLDASM");
                    Log.Info("P4 LoadFile2 返 {0}", loaded);
                    // 重新激活 drw
                    try { sw.ActivateDoc(drwTitle); }
                    catch { }
                    view = TryCreate(drw, modelName, "*Isometric", x, y, z, "P4: V2 *Isometric (after LoadFile2)");
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "P4 LoadFile2 失败");
                }
            }
            if (view == null)
            {
                // P5:InsertModelInPredefinedView(老 W3.5 路径)
                Log.Warn("P4 返 null,试 P5:InsertModelInPredefinedView");
                bool ok = drw.InsertModelInPredefinedView(modelName);
                Log.Info("P5 InsertModelInPredefinedView 返 {0}", ok);
                if (ok)
                {
                    // W8+ 修复:P5 成功但 FindFirstModelView 找不到真 model view 时,
                    // SW 创了 placeholder view(没 model load 的空 view)。
                    // 后续 P6 也会创真 iso view → 两个 iso 重叠。
                    // 修法:用 view.SuppressState = 1 隐藏 placeholder,只保留 P6 真 iso view。
                    // (DrawingDoc.SuppressView() 无参数,不是针对单 view — 用 IView.SuppressState)
                    try
                    {
                        object viewsObj2 = drw.GetViews();
                        var enumerable2 = viewsObj2 as System.Collections.IEnumerable;
                        if (enumerable2 != null)
                        {
                            foreach (var o in enumerable2)
                            {
                                if (o is View v2 && v2.IsModelLoaded() == false)
                                {
                                    v2.SuppressState = 1;  // 1 = swViewSuppressionStateSuppressed
                                    Log.Info("P5 SuppressState placeholder: name='{0}'", v2.GetName2());
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(ex, "P5 迭代 view 找 placeholder 失败");
                    }
                    view = FindFirstModelView(drw);
                }
            }
            if (view == null)
            {
                // P6:V3 *等轴测(W6-fix:宏录制发现 SW 中文环境要用中文 view 名 "*前视" 才 work)
                Log.Warn("P5 返 null,试 P6:V3 *等轴测(中文名)");
                try
                {
                    view = (View)drw.CreateDrawViewFromModelView3(modelName, "*等轴测", x, y, z);
                    // W7+ 修复 J:加成功/null log,避免 1.4 秒空白让人不知道 P6/P6.5/P7 谁 work
                    Log.Info("P6 V3 *等轴测 {0}",
                        view != null ? "成功 view=" + view.Name : "返 null");
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "P6 V3 异常");
                }
            }
            if (view == null)
            {
                // P6.5:V3 *前视(中文 front,宏录制里这个 work)
                Log.Warn("P6 返 null,试 P6.5:V3 *前视");
                try
                {
                    view = (View)drw.CreateDrawViewFromModelView3(modelName, "*前视", x, y, z);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "P6.5 V3 异常");
                }
            }
            if (view == null)
            {
                // P7:Create1stAngleViews2 — 一次性建三视图(前/上/右),在多 API 都失败时最稳
                // SW 2024 interop 签名是 Create1stAngleViews2(modelName) 单参,无 x/y/z
                Log.Warn("P6 返 null,试 P7:Create1stAngleViews2 一次性建三视图");
                try
                {
                    bool ok7 = drw.Create1stAngleViews2(modelName);
                    Log.Info("P7 Create1stAngleViews2 返 {0}", ok7);
                    if (ok7) view = FindFirstModelView(drw);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "P7 异常");
                }
            }
            // SOP 单页只需要一个爆炸等轴测。过去即使 P6 已成功，仍强制调用
            // Create1stAngleViews2 添加三张正交视图；它们既不用于球标/BOM，又会被旧 Step 6
            // 当作可移动视图并覆盖等轴测的位置。P7 只保留为所有单视图 API 都失败时的兜底。
            // 这里仍重申爆炸显示态，保证 P7 fallback 或 API 状态切换后插入的 view 引用正确状态。
            if (!string.IsNullOrEmpty(preExplodeName))
            {
                try
                {
                    asm.ShowExploded2(true, preExplodeName);
                    Log.Info("View 创建后确认 ShowExploded2: '{0}'", preExplodeName);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "View 创建后 ShowExploded2 失败");
                }
            }
            if (view == null)
            {
                // P8:Create3rdAngleViews2 — 备选(美标三视图)
                Log.Warn("P7 返 null,试 P8:Create3rdAngleViews2");
                try
                {
                    bool ok8 = drw.Create3rdAngleViews2(modelName);
                    Log.Info("P8 Create3rdAngleViews2 返 {0}", ok8);
                    if (ok8) view = FindFirstModelView(drw);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "P8 异常");
                }
            }
            if (view == null)
            {
                throw new InvalidOperationException(
                    "M4 视图插入全部 8 条路径都失败。model='" + modelName +
                    "',asm='" + asmTitle + "',drw='" + drwTitle + "'。详情见上方 NLog 警告。");
            }
            Log.Info("View 拿到: name='{0}'", view.Name);

            // W6-fix:不设 view.ReferencedConfiguration = "爆炸视图1"(那是 display state 不是 config,
            // SW 静默忽略)。改用 asm.ShowExploded2(true, 爆炸视图1) 强制 asm 进 exploded 显示态,
            // view 自动反映这个状态,AutoBalloon5/InsertBomTable4 就能找到组件。
            // W7+ 修复 J:asm.ShowExploded2 只设 asm 级 explode state,view 自己有 DisplayState
            // 字段,AutoBalloon5 用 view.DisplayState 找组件 — 显式设让 view 引用 exploded state。
            // (W6-fix 的"view 自动反映"假设不成立 — SW 2024 interop 没自动联动)
            // W7+:用 preExplodeName(同一 view 名,从头到尾一致)
            if (!string.IsNullOrEmpty(preExplodeName))
            {
                try
                {
                    asm.ShowExploded2(true, preExplodeName);
                    Log.Info("Asm 切到爆炸显示态: '{0}'", preExplodeName);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "asm.ShowExploded2(true, {0}) 失败", preExplodeName);
                }

                // IView.ShowExploded 才是工程图视图绑定装配爆炸状态的 API。
                // DisplayState 只是显示状态名称；把“爆炸视图1”写进去在 SW 2024 会静默保持
                // Display State-1，结果工程图只显示原装配。这里显式启用并读回验证。
                try
                {
                    bool beforeExploded = view.IsExploded();
                    bool showOk = view.ShowExploded(true);
                    bool afterExploded = view.IsExploded();
                    Log.Info("View '{0}' ShowExploded(true): {1} -> {2}, call={3}",
                        view.Name, beforeExploded, afterExploded, showOk);
                    if (!afterExploded)
                    {
                        Log.Warn("View '{0}' 未进入爆炸状态；工程图可能仍显示原装配", view.Name);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "view.ShowExploded(true) 失败");
                }

                // DisplayState 不负责绑定 explode view，保留赋值仅作旧版本兼容和诊断。
                try
                {
                    string before = view.DisplayState ?? "(null)";
                    view.DisplayState = preExplodeName;
                    Log.Info("view.DisplayState: '{0}' -> '{1}' (仅诊断，不用于爆炸绑定)",
                        before, view.DisplayState ?? "(null)");
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "view.DisplayState = '{0}' 失败", preExplodeName);
                }
            }
            else
            {
                // 没拿到 explode view 名时,单参版 ShowExploded2(true) 试试
                try { asm.ShowExploded2(true, "Default"); }
                catch { /* 忽略 */ }
            }

            // W6-fix 批 1:view 改成 shaded 模式(2 = SHADED,SW 标准常量)
            // 重要:bench vice 等零件多/SW 显示用 wireframe 很难看
            try
            {
                int beforeMode = view.GetDisplayMode();
                view.SetDisplayMode(2);  // 2 = SHADED
                int afterMode = view.GetDisplayMode();
                // W8+ 候选根因 O 验证:之前修复 J 修了 SetDisplayMode 但没验证是否真生效,
                // 跟 set_DisplayState 一样可能是 stub。这里 GetDisplayMode 读回对照。
                Log.Info("View '{0}' SetDisplayMode(2=SHADED): {1} -> {2} (若 !=2 则 SW 2024 stub,AutoBalloon5 对 wireframe 0 个)",
                    view.Name, beforeMode, afterMode);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "view.SetDisplayMode(SHADED) 失败");
            }

            return view;
        }

        public View InsertOriginalIso(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm, double x, double y, double z)
        {
            if (sw == null) throw new ArgumentNullException(nameof(sw));
            if (drw == null) throw new ArgumentNullException(nameof(drw));
            if (asm == null) throw new ArgumentNullException(nameof(asm));

            string modelName = GetModelName(asm);
            string asmTitle = ((IModelDoc2)asm).GetTitle();
            string drwTitle = ((IModelDoc2)drw).GetTitle();
            string explodeViewName = GetExplodeViewNameForDefault(asm);
            try
            {
                sw.ActivateDoc(asmTitle);
                if (!string.IsNullOrEmpty(explodeViewName)) asm.ShowExploded2(false, explodeViewName);
                Log.Info("InsertOriginalIso: assembly switched to collapsed state (explode='{0}')", explodeViewName ?? "(none)");

                sw.ActivateDoc(drwTitle);
                View view = null;
                try { view = (View)drw.CreateDrawViewFromModelView3(modelName, "*等轴测", x, y, z); }
                catch (Exception ex) { Log.Warn(ex, "InsertOriginalIso: V3 creation failed"); }
                if (view == null) view = TryCreate(drw, modelName, "*Isometric", x, y, z, "Original: V2 *Isometric");
                if (view == null) throw new InvalidOperationException("Unable to insert the original assembly isometric view.");

                try
                {
                    bool before = view.IsExploded();
                    view.ShowExploded(false);
                    view.SetDisplayMode(2);
                    view.ScaleDecimal = 0.56;
                    PositionOriginalAboveBom(drw, view, x, y);
                    Log.Info("Original view '{0}' ShowExploded(false): {1} -> {2}; scale={3:F2}", view.Name, before, view.IsExploded(), view.ScaleDecimal);
                }
                catch (Exception ex) { Log.Warn(ex, "Original view display setup failed"); }
                return view;
            }
            finally
            {
                try
                {
                    sw.ActivateDoc(asmTitle);
                    if (!string.IsNullOrEmpty(explodeViewName)) asm.ShowExploded2(true, explodeViewName);
                    Log.Info("InsertOriginalIso: restored exploded state '{0}'", explodeViewName ?? "(none)");
                }
                catch (Exception ex) { Log.Warn(ex, "InsertOriginalIso: failed to restore exploded state"); }
                try { sw.ActivateDoc(drwTitle); } catch { }
            }
        }

        private static void PositionOriginalAboveBom(DrawingDoc drw, View view, double preferredX, double preferredY)
        {
            const double margin = 0.008;
            const double bomLeft = 0.225;
            const double bomTop = 0.155;

            try
            {
                Sheet sheet = (Sheet)drw.GetCurrentSheet();
                double sheetWidth = 0, sheetHeight = 0;
                sheet.GetSize(ref sheetWidth, ref sheetHeight);
                if (sheetWidth <= 0 || sheetHeight <= 0) throw new InvalidOperationException("Invalid sheet size");

                // The original assembly is a reference view: place it above the lower-right BOM
                // whenever that zone has room.  The region is always inset from the drawing frame.
                double regionLeft = bomLeft;
                double regionRight = sheetWidth - margin;
                double regionBottom = bomTop;
                double regionTop = sheetHeight - margin;
                view.Position = new double[] { preferredX, preferredY, 0 };

                double[] outline = ReadOutline(view);
                if (outline == null) throw new InvalidOperationException("GetOutline returned no usable rectangle");
                double width = outline[2] - outline[0];
                double height = outline[1] - outline[3];
                double availableWidth = regionRight - regionLeft;
                double availableHeight = regionTop - regionBottom;
                if (width > availableWidth || height > availableHeight)
                {
                    double factor = Math.Min(availableWidth / width, availableHeight / height) * 0.96;
                    view.ScaleDecimal *= factor;
                    outline = ReadOutline(view);
                    Log.Info("Original view scaled to fit BOM-above zone: factor={0:F3}, scale={1:F3}", factor, view.ScaleDecimal);
                }

                if (outline == null) throw new InvalidOperationException("GetOutline failed after scaling");
                double dx = outline[0] < regionLeft ? regionLeft - outline[0]
                    : outline[2] > regionRight ? regionRight - outline[2] : 0;
                double dy = outline[3] < regionBottom ? regionBottom - outline[3]
                    : outline[1] > regionTop ? regionTop - outline[1] : 0;
                if (dx != 0 || dy != 0)
                {
                    double[] pos = ReadPosition(view.Position);
                    if (pos == null) throw new InvalidOperationException("View.Position returned no usable point");
                    view.Position = new[] { pos[0] + dx, pos[1] + dy, pos[2] };
                }

                double[] finalOutline = ReadOutline(view);
                Log.Info("Original view BOM-above placement: region=[{0:F3},{1:F3}]-[{2:F3},{3:F3}], outline=[{4:F3},{5:F3}]-[{6:F3},{7:F3}]",
                    regionLeft, regionBottom, regionRight, regionTop,
                    finalOutline[0], finalOutline[3], finalOutline[2], finalOutline[1]);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Original view BOM-above placement failed; retaining requested position");
            }
        }

        private static double[] ReadOutline(View view)
        {
            var values = ReadDoubleArray(view.GetOutline());
            if (values == null || values.Length < 4) return null;
            return new[]
            {
                Math.Min(values[0], values[2]), Math.Max(values[1], values[3]),
                Math.Max(values[0], values[2]), Math.Min(values[1], values[3])
            };
        }

        private static double[] ReadPosition(object raw)
        {
            var values = ReadDoubleArray(raw);
            return values != null && values.Length >= 3 ? new[] { values[0], values[1], values[2] } : null;
        }

        private static double[] ReadDoubleArray(object raw)
        {
            var sequence = raw as System.Collections.IEnumerable;
            if (sequence == null) return null;
            var values = new List<double>();
            foreach (var value in sequence)
            {
                try { values.Add(Convert.ToDouble(value)); }
                catch { return null; }
            }
            return values.ToArray();
        }

        public ViewInsertDiagnostics Diagnose(ISldWorks sw, DrawingDoc drw, AssemblyDoc asm)
        {
            var diag = new ViewInsertDiagnostics();
            try
            {
                diag.AsmTitle = ((IModelDoc2)asm).GetTitle();
                diag.DrwTitle = ((IModelDoc2)drw).GetTitle();
                diag.ViewCountBefore = drw.GetViewCount();
                string modelName = GetModelName(asm);

                TryDiagnosePath(diag, drw, modelName, "*Isometric", 0.05, 0.15, 0, "P1: V2 *Isometric", useV3: false);
                TryDiagnosePath(diag, drw, modelName, "*Front", 0.05, 0.15, 0, "P2: V2 *Front", useV3: false);
                TryDiagnosePath(diag, drw, modelName, "*Isometric", 0.05, 0.15, 0, "P6: V3 *Isometric", useV3: true);

                // P5 单独测(它创建 view 通过 template placeholder)
                try
                {
                    bool ok = drw.InsertModelInPredefinedView(modelName);
                    diag.Attempts.Add(new ViewPathAttempt
                    {
                        PathName = "P5: InsertModelInPredefinedView",
                        Succeeded = ok,
                        Outcome = ok ? "true" : "false",
                    });
                }
                catch (Exception ex)
                {
                    diag.Attempts.Add(new ViewPathAttempt
                    {
                        PathName = "P5: InsertModelInPredefinedView",
                        Succeeded = false,
                        Outcome = "exception",
                        ErrorDetail = ex.Message,
                    });
                }

                diag.ViewCountAfter = drw.GetViewCount();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Diagnose 失败");
            }
            return diag;
        }

        private void TryDiagnosePath(ViewInsertDiagnostics diag, DrawingDoc drw, string modelName,
            string viewName, double x, double y, double z, string label, bool useV3)
        {
            try
            {
                View v = useV3
                    ? (View)drw.CreateDrawViewFromModelView3(modelName, viewName, x, y, z)
                    : (View)drw.CreateDrawViewFromModelView2(modelName, viewName, x, y, z);
                diag.Attempts.Add(new ViewPathAttempt
                {
                    PathName = label,
                    Succeeded = v != null,
                    Outcome = v != null ? "view=" + v.Name : "null",
                });
                if (v != null) Marshal.ReleaseComObject(v);
            }
            catch (Exception ex)
            {
                diag.Attempts.Add(new ViewPathAttempt
                {
                    PathName = label,
                    Succeeded = false,
                    Outcome = "exception",
                    ErrorDetail = ex.Message,
                });
            }
        }

        /// <summary>统一包一层,失败返 null 而不 throw。</summary>
        private View TryCreate(DrawingDoc drw, string modelName, string viewName, double x, double y, double z, string label)
        {
            try
            {
                View v = (View)drw.CreateDrawViewFromModelView2(modelName, viewName, x, y, z);
                Log.Info("{0} 返 {1}", label, v != null ? "view=" + v.Name : "null");
                return v;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "{0} 异常", label);
                return null;
            }
        }

        /// <summary>
        /// W4.3 新增:拿 Default 配置下 AutoExplode 创建的 explode view 真名字。
        /// 之前用错了 GetExplodedViewConfigurationName(currentCfg),但它签名是 explode view → config,
        /// 不是 config → explode view。改用 GetExplodedViewNames2(currentCfg) 直接拿 config 下视图列表。
        /// </summary>
        private static string GetExplodeViewNameForDefault(AssemblyDoc asm)
        {
            try
            {
                string currentCfg = ((IModelDoc2)asm).ConfigurationManager.ActiveConfiguration.Name;
                Log.Info("GetExplodeViewNameForDefault: currentCfg='{0}'", currentCfg);

                object namesObj = asm.GetExplodedViewNames2(currentCfg);
                string name = FirstStringFromObjectArray(namesObj);
                if (!string.IsNullOrEmpty(name))
                {
                    Log.Info("GetExplodedViewNames2 返 '{0}'", name);
                    return name;
                }

                object allObj = asm.GetExplodedViewNames();
                if (allObj is object[] arr)
                {
                    foreach (var o in arr)
                    {
                        string candidate = o as string;
                        if (string.IsNullOrEmpty(candidate)) continue;
                        try
                        {
                            string attachedCfg = asm.GetExplodedViewConfigurationName(candidate);
                            if (string.Equals(attachedCfg, currentCfg, StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Info("GetExplodedViewNames + GetExplodedViewConfigurationName 找到 '{0}'", candidate);
                                return candidate;
                            }
                        }
                        catch { }
                    }
                    if (arr.Length > 0 && arr[0] is string first)
                    {
                        Log.Warn("没找到 attach 到 '{0}' 的 explode view,fallback 用 GetExplodedViewNames[0]='{1}'", currentCfg, first);
                        return first;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "GetExplodeViewNameForDefault 失败");
            }
            return null;
        }

        private static string FirstStringFromObjectArray(object obj)
        {
            if (obj == null) return null;
            try
            {
                var arr = (object[])obj;
                if (arr.Length > 0 && arr[0] is string s) return s;
            }
            catch { }
            return null;
        }

        private static View FindFirstModelView(DrawingDoc drw)
        {
            int count = drw.GetViewCount();
            Log.Info("FindFirstModelView: GetViewCount={0}", count);

            try
            {
                object viewsObj = drw.GetViews();
                if (viewsObj != null)
                {
                    var enumerable = viewsObj as System.Collections.IEnumerable;
                    if (enumerable != null)
                    {
                        foreach (var o in enumerable)
                        {
                            if (o is View v)
                            {
                                string n = v.GetName2();
                                if (IsRealModelViewName(n))
                                {
                                    return v;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "GetViews 路径失败,退到 GetFirstView 迭代");
            }

            object firstObj = drw.GetFirstView();
            if (firstObj == null) return null;
            View vOld = (View)firstObj;
            int guard = 0;
            while (vOld != null && guard < 50)
            {
                guard++;
                string n = vOld.GetName2();
                if (IsRealModelViewName(n))
                {
                    return vOld;
                }
                object nextObj = vOld.GetNextView();
                if (nextObj == null) return null;
                vOld = (View)nextObj;
            }

            Log.Warn("FindFirstModelView: 两种路径都失败");
            return null;
        }

        /// <summary>
        /// 名字不是 sheet 也不是"图纸"(中文)。SW 多语言环境下 sheet 名是 "Sheet1"(EN)或 "图纸1"(CN)。
        /// W6-fix:之前只过滤 "Sheet" 前缀,中文环境漏过 sheet 误判为 model view。
        /// </summary>
        private static bool IsRealModelViewName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("Sheet", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("图纸", StringComparison.OrdinalIgnoreCase)) return false;  // CN sheet
            return true;
        }

        private static string GetModelName(AssemblyDoc asm)
        {
            string p = ((IModelDoc2)asm).GetPathName();
            if (string.IsNullOrEmpty(p))
            {
                throw new InvalidOperationException(
                    "装配体未保存过,无法插入视图。请先在 SW 中保存装配体(.SLDASM)再重试。");
            }
            return p;
        }
    }
}
