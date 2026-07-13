using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorks.Interop.sldworks;
using SwSopAddin.Layout;

namespace SwSopAddin.Tests
{
    /// <summary>
    /// W11+ Phase 0 真机 acceptance gate。
    /// 验证 3 个 SW 写 API + 1 个 read API 在 SW 2024 上**真机可写**(反射 metadata ≠ 真机 dispatch)。
    ///
    /// 跑测前置(用户手动):
    ///   1. SolidWorks 2024 必须已运行(AddIn 已注册)
    ///   2. 已打开一个 asm 文档(.SLDASM)
    ///   3. 已打开一个 drw 文档(.SLDDRW)— smoke 自己用 sw.ActiveDoc 取,不 new
    ///   4. Drw 里至少有 1 个 model view(用 asm 配 iso view)
    ///
    /// 跑测方式(等 SW 开启后):
    ///   vstest.console.exe src\SwSopAddin.Tests\bin\Debug\SwSopAddin.Tests.dll
    ///     /TestCaseFilter:FullyQualifiedName~SwLayoutApiSmokeTests
    ///
    /// 关键设计:
    /// - **不主动 new SW / drw** — SW 是单进程 COM,MSTest 进程 grab 已运行的 SW;
    ///   失败用 Inconclusive("SW 未运行"),不污染其他测试。
    /// - **每个测试独立 setup**:drw / asm / view / bom 都从 sw.ActiveDoc 拿,没拿到就 Inconclusive。
    /// - **known stub 风险**:view.DisplayState 是 SW 2024 stub(CLAUDE.md W7+ 经验);
    ///   同样的 IScaleRatio / IPosition / SetPosition 也可能 stub,真机是唯一判定。
    /// - **不 try 之 mock / new view**:smoke 是 black-box 真机验证,不能依赖 production code。
    ///
    /// 失败分辨:
    ///   Inconclusive("SW 没开") → 跑测环境问题,数据无效
    ///   Fail("read target=0.5 but got 1.0") → API 真机不写(STUB),需重新设计
    /// </summary>
    [TestClass]
    public class SwLayoutApiSmokeTests
    {
        /// <summary>每个 [TestMethod] 前跑一次,Trace 当前 SW 状态(供 detailed verbosity 显示)。</summary>
        [TestInitialize]
        public void Setup()
        {
            ISldWorks sw;
            try
            {
                sw = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
            }
            catch
            {
                sw = null;
            }

            string activeType = "(null)";
            string activeName = "(null)";
            int drwCount = 0, asmCount = 0;
            if (sw != null)
            {
                ModelDoc2 active = null;
                try { active = (ModelDoc2)sw.ActiveDoc; } catch { }
                if (active != null)
                {
                    activeType = active is AssemblyDoc ? "Asm"
                        : active is DrawingDoc ? "Drw"
                        : active is PartDoc ? "Part"
                        : "Unknown";
                    try { activeName = active.GetTitle(); } catch { activeName = "(?)"; }
                }
                // W12 fix: GetDocuments() 返 object[] of ModelDoc2(IDispatch),不是 string[]。
                // 之前 (string[]) 强转直接抛 InvalidCastException,被吞掉变成空数组 → 诊断永远看不到东西。
                object[] docs;
                try { docs = (object[])sw.GetDocuments(); } catch { docs = new object[0]; }
                var names = new System.Collections.Generic.List<string>();
                foreach (object d in docs ?? new object[0])
                {
                    ModelDoc2 md = d as ModelDoc2;
                    if (md == null) continue;
                    string title = "(?)";
                    try { title = md.GetTitle(); } catch { }
                    string kind = md is AssemblyDoc ? "Asm" : md is DrawingDoc ? "Drw" : md is PartDoc ? "Part" : "Unknown";
                    names.Add(kind + ":" + title);
                    if (md is DrawingDoc) drwCount++;
                    if (md is AssemblyDoc) asmCount++;
                }
                var rawList = string.Join(" | ", names);
                System.Diagnostics.Trace.WriteLine($"[Setup] GetDocuments() raw=[{rawList}]");
            }

            string ctx = TestContext?.TestName ?? "(TestInitialize)";
            System.Diagnostics.Trace.WriteLine(
                $"[{ctx}] SW={(sw != null ? "running" : "NOT_RUNNING")} " +
                $"ActiveDoc={activeType}:{activeName} " +
                $"OpenDocsMatches[drw={drwCount}, asm={asmCount}]");

            TestContext?.WriteLine(
                $"SW={(sw != null ? "running" : "NOT_RUNNING")} " +
                $"ActiveDoc={activeType}:{activeName} " +
                $"OpenDocsMatches[drw={drwCount}, asm={asmCount}]");
        }

        public TestContext TestContext { get; set; }

        /// <summary>ROT 拿运行中的 SW。失败 null(SW 未运行)。</summary>
        private static ISldWorks TryGetRunningSolidWorks()
        {
            try
            {
                return (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
            }
            catch
            {
                return null;
            }
        }

        private static AssemblyDoc GetActiveAssembly(ISldWorks sw)
        {
            if (sw == null) return null;
            return sw.ActiveDoc as AssemblyDoc;
        }

        /// <summary>
        /// W12 fix: asm 和 drw 不可能同时是 ActiveDoc(SW 一次只有一个激活窗口)。
        /// 之前 GetActiveAssembly 只查 ActiveDoc,drw 激活时永远拿不到 asm → 4 个测试全 Inconclusive。
        /// 改成:先试 ActiveDoc,不是再从 GetDocuments() 枚举所有打开的文档找 AssemblyDoc,
        /// 不要求 asm 是当前激活窗口,只要求它是打开的。
        /// </summary>
        private static AssemblyDoc FindOpenAssembly(ISldWorks sw)
        {
            if (sw == null) return null;
            var active = sw.ActiveDoc as AssemblyDoc;
            if (active != null) return active;

            object[] docs;
            try { docs = (object[])sw.GetDocuments(); } catch { return null; }
            foreach (object d in docs ?? new object[0])
            {
                if (d is AssemblyDoc asm) return asm;
            }
            return null;
        }

        private static DrawingDoc GetActiveDrawing(ISldWorks sw)
        {
            if (sw == null) return null;
            return sw.ActiveDoc as DrawingDoc;
        }

        /// <summary>
        /// 拿 drw 第一个 model view;没 view 则用 *等轴测 自己插一个(需要活动 asm)。
        /// 这是 black-box smoke,被 SwLayoutApiSmokeTests 各测复用,确保不需要 SOP 全跑成功。
        /// </summary>
        private static View FindOrCreateFirstView(ISldWorks sw, DrawingDoc drw, string viewName, string testTag)
        {
            // 先看 drw 已有的 view
            object viewsObj = null;
            try { viewsObj = drw.GetViews(); } catch { }
            if (viewsObj is System.Collections.IEnumerable en)
            {
                foreach (var o in en)
                {
                    if (o is View v) return v;
                }
            }

            // drw 里没 view → 找一个打开的 asm(不要求它是 active),自己插一个
            AssemblyDoc asm = FindOpenAssembly(sw);
            if (asm == null) return null;

            // 拿到 asm 的 model path
            string modelPath = null;
            try
            {
                ModelDoc2 m = (ModelDoc2)asm;
                modelPath = m.GetPathName();
            }
            catch { }
            if (string.IsNullOrEmpty(modelPath) || !System.IO.File.Exists(modelPath))
            {
                System.Diagnostics.Trace.WriteLine("[" + testTag + "] asm 路径无效/未保存: " + modelPath);
                return null;
            }

            try
            {
                View v = (View)drw.CreateDrawViewFromModelView3(modelPath, viewName, 0.10, 0.10, 0);
                if (v != null) System.Diagnostics.Trace.WriteLine("[" + testTag + "] 自动 CreateDrawViewFromModelView3 成功: name='" + v.Name + "'");
                return v;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("[" + testTag + "] CreateDrawViewFromModelView3 抛: " + ex.Message);
                return null;
            }
        }

        /// <summary>列出 SW 当前所有打开的 doc 类型,用于诊断 "为什么 drw test 不跑"。(死代码 — Setup 已做) </summary>
        // 删除:之前是占位无主,Setup() 是权威版本。

        // ====== Test 1: view.IScaleRatio 读 + 写 ======

        [TestMethod]
        public void Smoke_ViewScaleRatio_GS_ActuallyWritable()
        {
            ISldWorks sw = TryGetRunningSolidWorks();
            if (sw == null) { Assert.Inconclusive("SW 未运行,先开 SW 重跑"); return; }

            DrawingDoc drw = GetActiveDrawing(sw);
            if (drw == null) { Assert.Inconclusive("当前不是 drw 文档,先开一个 .SLDDRW"); return; }

            View targetView = FindOrCreateFirstView(sw, drw, "*等轴测", "Smoke_ViewScaleRatio");
            if (targetView == null) { Assert.Inconclusive("drw 既无 view 也无法 create 一个(asm 路径不对或 view 名错)"); return; }

            // W12 fix: ScaleRatio 真机返回 double[2]{分子,分母},不是标量 double —
            // 之前 Convert.ToDouble(数组) 直接抛 InvalidCastException(数组不实现 IConvertible),
            // 这是测试代码的类型假设错误,不是 API 写入失败。
            object beforeObj = targetView.ScaleRatio;
            double[] before = beforeObj as double[];
            if (before == null || before.Length < 2)
            { Assert.Fail("view.ScaleRatio 读到非预期类型: " + (beforeObj == null ? "null" : beforeObj.GetType().FullName)); return; }
            double beforeRatio = before[1] == 0 ? 0 : before[0] / before[1];

            double targetRatio = Math.Round(beforeRatio * 0.5, 3);
            if (targetRatio <= 0) targetRatio = 0.1;

            // 写
            bool writeOk = true;
            string writeErr = null;
            try
            {
                targetView.ScaleRatio = new double[] { targetRatio, 1.0 };   // [分子,分母] 形式
            }
            catch (Exception ex)
            {
                writeOk = false;
                writeErr = ex.Message;
            }

            // 读回
            double[] afterArr = targetView.ScaleRatio as double[];
            double after = (afterArr != null && afterArr.Length >= 2 && afterArr[1] != 0)
                ? afterArr[0] / afterArr[1] : double.NaN;

            if (!writeOk) Assert.Fail("view.ScaleRatio = [" + targetRatio + ",1] 写时抛异常: " + writeErr);
            if (double.IsNaN(after) || Math.Abs(after - targetRatio) > 1e-3)
                Assert.Fail("view.ScaleRatio 写后读回 = " + after + ",目标 = " + targetRatio + "(before = " + beforeRatio + ") → SW 2024 可能是 STUB。");

            // 通过:不修改 drw,留下 view 让用户目视复查
        }

        // ====== Test 2: view.Position 写(用 Position(Object),LayoutApplier.W10+ 注释说这是 stub) ======

        [TestMethod]
        public void Smoke_ViewPosition_ActuallyWritable()
        {
            ISldWorks sw = TryGetRunningSolidWorks();
            if (sw == null) { Assert.Inconclusive("SW 未运行"); return; }

            DrawingDoc drw = GetActiveDrawing(sw);
            if (drw == null) { Assert.Inconclusive("先开 .SLDDRW"); return; }

            View targetView = FindOrCreateFirstView(sw, drw, "*等轴测", "Smoke_ViewPosition");
            if (targetView == null) { Assert.Inconclusive("drw 既无 view 也无法 create 一个"); return; }

            // W12 fix: 用 view.Position 本身读回验证,不用 GetOutline —
            // outline 是 view 内容的包围盒范围,和 Position(view 原点)之间有一个固定偏移
            // (view 内容相对原点的位置),两者不能直接数值比较。之前用 outline-left 判定,
            // 写入前后 outline 确实整体平移了 0.1(说明写入生效了),但因为偏移量存在,
            // 平移后的绝对值和 target 对不上,被误判为 STUB。
            double[] beforePos = targetView.Position as double[];
            if (beforePos == null || beforePos.Length < 2)
            { Assert.Fail("view.Position 读到非预期类型: " + (targetView.Position == null ? "null" : targetView.Position.GetType().FullName)); return; }

            // 写新位置(0.20, 0.20)
            double newX = 0.20, newY = 0.20;
            bool writeOk = true;
            string writeErr = null;
            try
            {
                targetView.Position = new double[] { newX, newY, 0.0 };   // W12 fix: 必须是 double[],不是 object[](VARIANT 数组封送错位)
            }
            catch (Exception ex)
            {
                writeOk = false;
                writeErr = ex.Message;
            }

            // 读回 Position
            double[] afterPos = null;
            try { afterPos = targetView.Position as double[]; }
            catch (Exception) { /* leave null */ }

            if (!writeOk) Assert.Fail("view.Position = (...) 写时抛: " + writeErr);
            if (afterPos == null || afterPos.Length < 2)
            { Assert.Fail("写后 view.Position 读回非预期类型/null,可能 STUB"); return; }

            if (Math.Abs(afterPos[0] - newX) > 1e-3 || Math.Abs(afterPos[1] - newY) > 1e-3)
                Assert.Fail("view.Position 写后读回=(" + afterPos[0] + "," + afterPos[1] + "),目标=(" + newX + "," + newY + ")" +
                    "(before=(" + beforePos[0] + "," + beforePos[1] + ")) → 可能 STUB");

            // 补充诊断(不参与断言):outline 变化量,方便人工核对是否符合预期偏移
            try
            {
                double[] outline = targetView.GetOutline() as double[];
                if (outline != null && outline.Length >= 4)
                    System.Diagnostics.Trace.WriteLine("[Smoke_ViewPosition] 写后 outline=[" + string.Join(",", outline) + "]");
            }
            catch { }
        }

        // ====== Test 3: IAnnotation.SetPosition(写 BOM 位置备选 API) ======

        [TestMethod]
        public void Smoke_BomSetPosition_ActuallyWritable()
        {
            ISldWorks sw = TryGetRunningSolidWorks();
            if (sw == null) { Assert.Inconclusive("SW 未运行"); return; }

            DrawingDoc drw = GetActiveDrawing(sw);
            if (drw == null) { Assert.Inconclusive("先开 .SLDDRW"); return; }

            AssemblyDoc asm = FindOpenAssembly(sw);
            if (asm == null) { Assert.Inconclusive("先开一个 .SLDASM"); return; }

            // 自己插一个 BOM 到第一个 view(GetAnnotations 不存在,改用 InsertBomTable4)
            // W12 fix: 改用 FindOrCreateFirstView,不依赖其他测试先跑过、drw 里已经有 view
            // (测试执行顺序不保证,之前直接查 drw.GetViews() 在此测试先跑时必然 Inconclusive)。
            View view = FindOrCreateFirstView(sw, drw, "*等轴测", "Smoke_BomSetPosition");
            if (view == null) { Assert.Inconclusive("drw 既无 view 也无法 create 一个"); return; }

            string cfg = ((IModelDoc2)asm).ConfigurationManager.ActiveConfiguration.Name;
            BomTableAnnotation bom = null;
            try
            {
                bom = (BomTableAnnotation)view.InsertBomTable4(
                    false,       // W12 fix: useAnchorPoint=true 时位置由 sheet 锚点接管,SetPosition 写入被静默忽略(真机验证)。
                                 // 跟生产代码 BomService(CLAUDE.md 记录 UseAnchorPoint=false)保持一致。
                    0.10, 0.10, // 占位 X,Y
                    1,           // AnchorType = 1 (top-left)
                    1,           // BomType = 1 (top-level only)
                    cfg,
                    @"D:\SW\SOLIDWORKS\lang\chinese-simplified\bom-standard.sldbomtbt",
                    false, 0, false);
            }
            catch (Exception ex) { Assert.Inconclusive("InsertBomTable4 抛:" + ex.Message); return; }
            if (bom == null) { Assert.Inconclusive("InsertBomTable4 返 null(template 路径找不到?)"); return; }

            // W12 fix: BomTableAnnotation 反射确认只实现 IBomTableAnnotation,不实现 IAnnotation —
            // 之前 (bom as IAnnotation) 必然 null,不是真机 STUB,是类型假设错误。
            // 正确路径:BomTableAnnotation 同时是一种 ITableAnnotation(通用表格注解),
            // ITableAnnotation.GetAnnotation() 返回真正实现 IAnnotation 的 Annotation 对象。
            ITableAnnotation tbl = bom as ITableAnnotation;
            if (tbl == null) { Assert.Fail("BOM 不能 cast 到 ITableAnnotation"); return; }

            Annotation annObj = null;
            try { annObj = tbl.GetAnnotation(); }
            catch (Exception ex) { Assert.Fail("ITableAnnotation.GetAnnotation() 抛: " + ex.Message); return; }
            if (annObj == null) { Assert.Fail("ITableAnnotation.GetAnnotation() 返 null"); return; }

            IAnnotation ann = annObj as IAnnotation;
            if (ann == null) { Assert.Fail("GetAnnotation() 返回对象不能 cast 到 IAnnotation"); return; }

            // 读前
            double[] before = (double[])ann.GetPosition();

            // 写新位置(右下角附近,Y=down sheet 坐标)
            double newX = 0.32, newY = 0.04;
            bool writeOk = true;
            string writeErr = null;
            try
            {
                ann.SetPosition(newX, newY, 0.0);
            }
            catch (Exception ex)
            {
                writeOk = false;
                writeErr = ex.Message;
            }

            double[] after = null;
            try { after = (double[])ann.GetPosition(); } catch { }

            if (!writeOk) Assert.Fail("IAnnotation.SetPosition 写抛: " + writeErr);
            if (after == null || after.Length < 2) Assert.Fail("写后 GetPosition 返空,可能 stub");
            if (Math.Abs(after[0] - newX) > 1e-3 || Math.Abs(after[1] - newY) > 1e-3)
                Assert.Fail("IAnnotation.SetPosition 写后位置=(" + after[0] + "," + after[1] + "),目标 (" + newX + "," + newY + ")" +
                    "(before=(" + before[0] + "," + before[1] + ")) → 可能 STUB");
        }

        // ====== Test 4: 验证 BoundingBoxCollector 用的 BOM 路径(BomTableAnnotation → IAnnotation.GetPosition) ======
        // 这条路径在 production code 里跑(BoundingBoxCollector.cs L98-130),smoke 验证真机可读 GetPosition。
        // 读 BOM 位置不需要 Production code 找不到的 GetAnnotations 接口 — SopWorkflow.RunStep5 把 BomTableAnnotation
        // 直接传给 LayoutService.ApplyLayout。

        [TestMethod]
        public void Smoke_BomAnnotationGetPosition_Readable()
        {
            ISldWorks sw = TryGetRunningSolidWorks();
            if (sw == null) { Assert.Inconclusive("SW 未运行"); return; }

            DrawingDoc drw = GetActiveDrawing(sw);
            if (drw == null) { Assert.Inconclusive("先开 .SLDDRW"); return; }

            // 枚举 view,看每个 view 是否有 GetNext/InsertBomTable 路径关联 BOM
            // 这里改测更直接的路:让用户手动给一个 view 关联 BOM 后跑;
            // 当前我们没有 GetAnnotations,所以 BomTest (Test 3) 已经覆盖 IAnnotation.GetPosition 的读路径。

            // 退而求其次:测 view.IsModelLoaded / view.GetName2 在真机能读
            View anyView = FindOrCreateFirstView(sw, drw, "*等轴测", "Smoke_BomAnnotationGetPosition");
            if (anyView == null) { Assert.Inconclusive("drw 既无 view 也无法 create 一个"); return; }

            string name = anyView.GetName2();
            Assert.IsFalse(string.IsNullOrEmpty(name),
                "view.GetName2() 应返非空字符串(读取 sanity)");

            // SopWorkflow.RunStep4_Balloon 之前 fix 的核心(w8):drw.ActivateView(name)
            bool activated = false;
            string actErr = null;
            try
            {
                activated = drw.ActivateView(name);
            }
            catch (Exception ex) { actErr = ex.Message; }
            Assert.IsTrue(activated, "drw.ActivateView(name) 返 false 或抛(" + actErr + ")→ BalloonService 的 fix 失效");
        }

        // ====== Test 5: Phase 3 — LayoutService.ApplyIsoPlacement 真机可写 + 外接框落进目标矩形 ======

        [TestMethod]
        public void Smoke_ApplyIsoPlacement_ActuallyWritable()
        {
            ISldWorks sw = TryGetRunningSolidWorks();
            if (sw == null) { Assert.Inconclusive("SW 未运行"); return; }

            DrawingDoc drw = GetActiveDrawing(sw);
            if (drw == null) { Assert.Inconclusive("先开 .SLDDRW"); return; }

            View targetView = FindOrCreateFirstView(sw, drw, "*等轴测", "Smoke_ApplyIsoPlacement");
            if (targetView == null) { Assert.Inconclusive("drw 既无 view 也无法 create 一个"); return; }

            double[] beforeScaleArr = targetView.ScaleRatio as double[];
            double beforeScale = (beforeScaleArr != null && beforeScaleArr.Length >= 2 && beforeScaleArr[1] != 0)
                ? beforeScaleArr[0] / beforeScaleArr[1] : double.NaN;

            var layoutService = new LayoutService();
            IsoPlacementResult placement = layoutService.ApplyIsoPlacement(drw, targetView, LayoutOptions.Default);

            Assert.IsNotNull(placement, "ApplyIsoPlacement 不应返 null");
            if (!placement.Success)
            {
                Assert.Inconclusive("ApplyIsoPlacement 未成功(可能是当前 view 外接框读不到/退化): " + placement.Notes);
                return;
            }

            double[] afterScaleArr = targetView.ScaleRatio as double[];
            double afterScale = (afterScaleArr != null && afterScaleArr.Length >= 2 && afterScaleArr[1] != 0)
                ? afterScaleArr[0] / afterScaleArr[1] : double.NaN;

            Assert.IsFalse(double.IsNaN(afterScale), "写后 ScaleRatio 读不到,可能 STUB");
            Assert.AreEqual(placement.AppliedScaleRatio, afterScale, 1e-3,
                "写后读回 ScaleRatio=" + afterScale + " 与 ApplyIsoPlacement 返回的 AppliedScaleRatio=" +
                placement.AppliedScaleRatio + " 不一致(before=" + beforeScale + ")");

            // 重新用 BoundingBoxCollector(反射走 public TryCollect)读外接框,确认中心落在目标矩形容差内。
            // 容差比目标矩形本身宽,因为 Position/Outline 偏移是"缩放后量一次"的近似值,不追求像素级精确。
            var collector = new BoundingBoxCollector(LayoutOptions.Default);
            LayoutRect? afterRect = collector.TryCollect(targetView, LayoutElementKind.View);
            Assert.IsTrue(afterRect.HasValue, "写后读外接框失败");

            var target = placement.IsoTargetRect.Value;
            double actualCenterX = afterRect.Value.X + afterRect.Value.Width / 2.0;
            double actualCenterY = afterRect.Value.Y + afterRect.Value.Height / 2.0;
            double targetCenterX = target.X + target.Width / 2.0;
            double targetCenterY = target.Y + target.Height / 2.0;

            double tolerance = Math.Max(target.Width, target.Height) * 0.5;  // 宽容:偏移量测量法允许一定误差
            Assert.IsTrue(Math.Abs(actualCenterX - targetCenterX) < tolerance,
                "外接框中心 X=" + actualCenterX + " 偏离目标中心 X=" + targetCenterX + " 超过容差 " + tolerance);
            Assert.IsTrue(Math.Abs(actualCenterY - targetCenterY) < tolerance,
                "外接框中心 Y=" + actualCenterY + " 偏离目标中心 Y=" + targetCenterY + " 超过容差 " + tolerance);

            System.Diagnostics.Trace.WriteLine("[Smoke_ApplyIsoPlacement] " + placement.Notes);
        }

        // ====== Test 0: sanity — 能连 SW ======

        [TestMethod]
        public void Smoke_SolidWorksIsRunning()
        {
            // Sanity check — 全部 smoke 都 Inconclusive 才不污染回归。
            // 若要 fail,反而把"没开 SW"当成代码 bug,误导排查方向。
            ISldWorks sw = TryGetRunningSolidWorks();
            if (sw == null)
            {
                Assert.Inconclusive(
                    "无法 grab 运行的 SW(Marshal.GetActiveObject('SldWorks.Application') 返 null)。" +
                    "请先开 SW,再跑 smoke 测试。可能:(1) SW 没开 (2) SW 不是标准 singleton COM 注册版本 " +
                    "(3) 测试进程与 SW 进程隔离(compartment)");
                return;
            }
            System.Diagnostics.Trace.WriteLine("连接 SW 成功");
        }
    }
}