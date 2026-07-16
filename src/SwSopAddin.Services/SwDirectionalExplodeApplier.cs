using System;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using NLog;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Services
{
    /// <summary>
    /// Part A Phase 1 — IExplodeApplier 的 SW 2024 生产实现。
    ///
    /// 关键设计:沿用现有 cfg.IAddExplodeStep(distance, reverseDir, false, false) 调用不变
    /// (M7 AiExplodeAdvisor 的 StepCount/IGetExplodeStep 全套机制依赖真实 ExplodeStep,
    /// 换成 PresentationTransform 会让它对 SmartHybrid 爆炸静默失效 — 见 Part A 计划)。
    ///
    /// 只修复 w3-m4-m6-root-cause 里记录的"单选裸组件,selection set 不够"问题:
    /// 额外选中一个能定义方向的基准面(Front/Top/Right 三选一,取法向与目标方向点积绝对值最大者),
    /// 构建 2 项选择集后再调 IAddExplodeStep。
    ///
    /// 基准面法向不走 IMathUtility/CreateVector,而是用组件 Transform2.ArrayData 的 3x3 旋转矩阵
    /// (SW 标准布局:局部 X/Y/Z 轴对应 Right/Front/Top 基准面法向)直接算世界坐标方向 — 反射已确认
    /// IComponent2.Transform2 / IMathTransform.ArrayData 存在,纯数值计算,不必再引入 MathUtility COM 调用。
    ///
    /// 已知局限(需真机验证,见 Part A Phase 3):这只是"轴对齐近似"——SmartHybrid 算出的方向是任意
    /// 连续向量,IAddExplodeStep 只能沿已选参考的法向/轴向移动,近似度对非轴对齐主体件不保证理想。
    /// </summary>
    internal class SwDirectionalExplodeApplier : IRadialExplodeApplier
    {
        private static readonly Logger Log = Logging.ForType(typeof(SwDirectionalExplodeApplier));

        // 每个候选基准面:可能的名字(英文优先,中文兜底,同 ViewService 的多候选 fallback 风格)+
        // 该基准面法向对应组件局部坐标的哪根轴(SW 标准基准面布局:Right=X,Front=Y,Top=Z)。
        private struct PlaneCandidate
        {
            public string[] Names;
            public Func<double[], double[], double[], double[]> AxisSelector; // (worldX, worldY, worldZ) -> normal
        }

        private static readonly PlaneCandidate[] Candidates =
        {
            new PlaneCandidate { Names = new[] { "Right Plane", "右视基准面" }, AxisSelector = (x, y, z) => x },
            new PlaneCandidate { Names = new[] { "Front Plane", "前视基准面" }, AxisSelector = (x, y, z) => y },
            new PlaneCandidate { Names = new[] { "Top Plane", "上视基准面" }, AxisSelector = (x, y, z) => z },
        };

        private readonly AssemblyDoc _asm;
        private readonly IConfiguration _cfg;
        private readonly string _asmTitle;
        private readonly bool _enableRadialSteps;

        public SwDirectionalExplodeApplier(AssemblyDoc asm, IConfiguration cfg, bool enableRadialSteps = true)
        {
            _asm = asm ?? throw new ArgumentNullException(nameof(asm));
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _enableRadialSteps = enableRadialSteps;
            try { _asmTitle = StripAssemblyExtension(((ModelDoc2)_asm).GetTitle()); } catch { _asmTitle = null; }
        }

        /// <summary>
        /// GetTitle() 对装配体有时带扩展名(跟 Part 文档不同,真机行为不保证一致 — 见 SW 论坛录制宏惯例),
        /// SelectByID2 的三段式命名要求纯标题(不含扩展名),这里防御性去掉常见后缀。
        /// </summary>
        private static string StripAssemblyExtension(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            const string ext = ".sldasm";
            if (title.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return title.Substring(0, title.Length - ext.Length);
            return title;
        }

        public object ResolveComponent(string componentName, int index)
        {
            try
            {
                object[] compsRaw = (object[])_asm.GetComponents(true);
                if (compsRaw == null) return null;

                IComponent2 byName = null;
                for (int i = 0; i < compsRaw.Length; i++)
                {
                    var c = compsRaw[i] as IComponent2;
                    if (c != null && byName == null &&
                        string.Equals(c.Name2, componentName, StringComparison.Ordinal))
                    {
                        byName = c;
                    }
                }

                if (byName != null)
                {
                    ReleaseAllExcept(compsRaw, byName);
                    return byName;
                }

                IComponent2 byIndex = (index >= 0 && index < compsRaw.Length)
                    ? compsRaw[index] as IComponent2
                    : null;
                ReleaseAllExcept(compsRaw, byIndex);
                return byIndex;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ResolveComponent 失败: {0}", componentName);
                return null;
            }
        }

        public bool ApplyPlacement(object component, double[] direction, double distanceMeters, out string stepName)
        {
            stepName = null;
            IComponent2 comp = component as IComponent2;
            if (comp == null || direction == null || direction.Length != 3) return false;

            try
            {
                double[] worldX, worldY, worldZ;
                if (!TryGetComponentAxes(comp, out worldX, out worldY, out worldZ))
                {
                    Log.Warn("ApplyPlacement: 拿不到组件变换矩阵: {0}", comp.Name2);
                    return false;
                }

                int bestIdx = -1;
                double bestAbsDot = -1;
                double bestDot = 0;
                for (int i = 0; i < Candidates.Length; i++)
                {
                    double[] normal = Candidates[i].AxisSelector(worldX, worldY, worldZ);
                    double dot = Vec3.Dot(direction, normal);
                    double absDot = Math.Abs(dot);
                    if (absDot > bestAbsDot)
                    {
                        bestAbsDot = absDot;
                        bestDot = dot;
                        bestIdx = i;
                    }
                }
                if (bestIdx < 0) return false;

                bool reverseDir = bestDot < 0;
                Log.Info("ApplyPlacement: {0} 选中基准面候选[{1}] dot={2:F3} reverseDir={3}",
                    comp.Name2, string.Join("/", Candidates[bestIdx].Names), bestDot, reverseDir);

                ModelDoc2 modelDoc = (ModelDoc2)_asm;
                try { modelDoc.ClearSelection2(true); } catch { /* 清失败不阻塞 */ }

                if (!comp.Select2(false, 0))
                {
                    Log.Warn("ApplyPlacement: 组件选择失败: {0}", comp.Name2);
                    return false;
                }

                bool planeSelected = false;
                foreach (var planeName in Candidates[bestIdx].Names)
                {
                    // SW 对"选中装配体子组件拥有的基准面"要求三段式 Name@Component@顶层装配体标题
                    // (两段式 Name@Component 在真机上对子组件基准面 100% 选不中 — 见 Part A Phase 3+ 诊断)。
                    // 三段式失败时仍尝试两段式兜底,防止极端情况下 _asmTitle 取值本身有误。
                    var candidateNames = string.IsNullOrEmpty(_asmTitle)
                        ? new[] { planeName + "@" + comp.Name2 }
                        : new[] { planeName + "@" + comp.Name2 + "@" + _asmTitle, planeName + "@" + comp.Name2 };

                    foreach (var selName in candidateNames)
                    {
                        try
                        {
                            planeSelected = modelDoc.Extension.SelectByID2(
                                selName, "PLANE", 0, 0, 0, true, 1, null, 0);
                            if (planeSelected)
                            {
                                Log.Info("ApplyPlacement: 基准面选中成功 '{0}'", selName);
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warn(ex, "ApplyPlacement: 选基准面异常 '{0}'", selName);
                        }
                    }
                    if (planeSelected) break;
                }

                if (!planeSelected)
                {
                    Log.Warn("ApplyPlacement: 候选基准面 [{0}] 都选不中,组件={1} asmTitle={2} — 只靠单选组件调用 IAddExplodeStep(真机验证过的已知失败模式,可能仍返 null)",
                        string.Join("/", Candidates[bestIdx].Names), comp.Name2, _asmTitle ?? "(null)");
                }

                // IAddExplodeStep 的 ExplDist 采用 SolidWorks 标准长度单位:米。
                // distanceMeters 已是 ExplodeLayoutPlanner 输出的米，不能再转成毫米；此前
                // 79.7 mm 会被解释成 79.7 m，组件被推到视窗外，看上去像“消失”。
                ExplodeStep step = (ExplodeStep)_cfg.IAddExplodeStep(distanceMeters, reverseDir, false, false);
                if (step == null)
                {
                    Log.Warn("ApplyPlacement: IAddExplodeStep 返 null: {0}", comp.Name2);
                    DiagnoseWithAddExplodeStep2(comp, distanceMeters, bestIdx, reverseDir);
                    return false;
                }

                stepName = step.Name;
                Marshal.ReleaseComObject(step);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ApplyPlacement 异常");
                return false;
            }
        }

        /// <summary>
        /// 真正的径向步骤需要组件(Mark=1)、爆炸轴(Mark=34)、发散方向(Mark=64)。
        /// 组件体上没有满足条件的实体时安全回退到原有线性步骤，避免单个紧固件让整个
        /// SmartHybrid 失败。
        /// </summary>
        public bool ApplyRadialPlacement(object component, ExplodePlacement placement, out string stepName)
        {
            stepName = null;
            IComponent2 comp = component as IComponent2;
            if (!_enableRadialSteps || comp == null || placement == null)
                return ApplyPlacement(component, placement?.Direction, placement?.DistanceMeters ?? 0, out stepName);

            Body2 body = null;
            object axisEntity = null;
            object divergeEntity = null;
            try
            {
                var modelDoc = (ModelDoc2)_asm;
                modelDoc.ClearSelection2(true);
                body = comp.GetBody() as Body2;
                if (body == null || !TryFindRadialEntities(body, out axisEntity, out divergeEntity))
                {
                    Log.Info("ApplyRadialPlacement: {0} 缺有效轴/发散实体，回退线性步骤", comp.Name2);
                    return ApplyPlacement(comp, placement.Direction, placement.DistanceMeters, out stepName);
                }

                if (!comp.Select2(false, 1) || !SelectEntity(axisEntity, true, 34) || !SelectEntity(divergeEntity, true, 64))
                {
                    Log.Warn("ApplyRadialPlacement: {0} 径向选择集建立失败，回退线性步骤", comp.Name2);
                    return ApplyPlacement(comp, placement.Direction, placement.DistanceMeters, out stepName);
                }

                int error;
                ExplodeStep step = (ExplodeStep)_cfg.AddRadialExplodeStep(
                    placement.DistanceMeters, 0.0, true, out error);
                if (step == null || error != 0)
                {
                    if (step != null) Marshal.ReleaseComObject(step);
                    Log.Warn("ApplyRadialPlacement: {0} AddRadialExplodeStep 失败 Error={1}({2})，回退线性步骤",
                        comp.Name2, error, DecodeCreateExplodeStepError(error));
                    return ApplyPlacement(comp, placement.Direction, placement.DistanceMeters, out stepName);
                }

                stepName = step.Name;
                Marshal.ReleaseComObject(step);
                ((ModelDoc2)_asm).EditRebuild3();
                Log.Info("ApplyRadialPlacement: {0} 成功，距离={1:F1}mm", comp.Name2, placement.DistanceMeters * 1000.0);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ApplyRadialPlacement: {0} 异常，回退线性步骤", comp?.Name2 ?? "(null)");
                return ApplyPlacement(comp, placement.Direction, placement.DistanceMeters, out stepName);
            }
            finally
            {
                ReleaseCom(axisEntity);
                if (!ReferenceEquals(divergeEntity, axisEntity)) ReleaseCom(divergeEntity);
                if (body != null) ReleaseCom(body);
            }
        }

        private static bool TryFindRadialEntities(Body2 body, out object axisEntity, out object divergeEntity)
        {
            axisEntity = null;
            divergeEntity = null;

            // 圆柱/圆锥面是 API 明确支持的爆炸轴。若没有，使用第一条直线边作轴。
            object[] faces = body.GetFaces() as object[];
            foreach (object candidate in faces ?? new object[0])
            {
                var face = candidate as Face2;
                var surface = face?.GetSurface() as Surface;
                bool validAxis = surface != null && (surface.IsCylinder() || surface.IsCone());
                if (surface != null) ReleaseCom(surface);
                if (validAxis && axisEntity == null) axisEntity = candidate;
                else ReleaseCom(candidate);
            }

            object[] edges = body.GetEdges() as object[];
            foreach (object candidate in edges ?? new object[0])
            {
                var edge = candidate as Edge;
                var curve = edge?.GetCurve() as Curve;
                bool isLine = curve != null && curve.IsLine();
                if (curve != null) ReleaseCom(curve);
                if (!isLine) { ReleaseCom(candidate); continue; }
                if (axisEntity == null) axisEntity = candidate;
                else if (divergeEntity == null) divergeEntity = candidate;
                else ReleaseCom(candidate);
            }

            // 真实径向操作还必须有一个与轴成角的发散实体；缺少时由调用方退回线性。
            if (axisEntity == null || divergeEntity == null)
            {
                ReleaseCom(axisEntity);
                ReleaseCom(divergeEntity);
                axisEntity = divergeEntity = null;
                return false;
            }
            return true;
        }

        private static bool SelectEntity(object entity, bool append, int mark)
        {
            if (entity is IEntity selectable) return selectable.Select2(append, mark);
            return false;
        }

        private static void ReleaseCom(object value)
        {
            if (value == null) return;
            try { Marshal.ReleaseComObject(value); } catch { }
        }

        /// <summary>
        /// Part A Phase 3+ 诊断专用(方案 B,2026-07-12):IAddExplodeStep 返 null 时,
        /// 紧跟着试一次 AddExplodeStep2 —— 目的只是读它的 Error out 参数,拿到 SW 真实拒绝原因,
        /// 不改变 ApplyPlacement 的返回值/统计(调用方仍然按 IAddExplodeStep 失败处理)。
        /// ExplDirIndex 直接复用 bestIdx(0=X/1=Y/2=Z,与 Candidates 数组顺序、
        /// swExplodeDirectionIndex_e 反射确认的枚举值完全一致),ExplAng=0 表示纯平移、不旋转,
        /// RotAxisIndex 传 -1(ExplAng=0 时不应被使用)。
        /// 注意:若 AddExplodeStep2 意外成功,会在模型里真的留下一个未被 processed/failed 统计到的
        /// ExplodeStep(诊断调用本身有副作用)—— 这种情况下日志会明确标出,便于识别。
        /// </summary>
        private void DiagnoseWithAddExplodeStep2(IComponent2 comp, double explDistMeters, int dirIndex, bool reverseDir)
        {
            try
            {
                int error;
                object stepObj = _cfg.AddExplodeStep2(
                    explDistMeters,   // ExplDist (meters)
                    dirIndex,         // ExplDirIndex
                    reverseDir,       // ReverseDir
                    0.0,              // ExplAng(纯平移,不旋转)
                    -1,               // RotAxisIndex(ExplAng=0 时不使用)
                    false,            // ReverseAng
                    false,            // RotateAboutOrigin
                    false,            // AutoSpaceComponentsOnDrag
                    out error);

                Log.Warn("ApplyPlacement 诊断: AddExplodeStep2({0}) ExplDirIndex={1} reverseDir={2} → Error={3}({4}), stepObj={5}",
                    comp.Name2, dirIndex, reverseDir, error, DecodeCreateExplodeStepError(error),
                    stepObj == null ? "null" : "非null(意外成功,留下了未统计的真实 step)");

                if (stepObj != null)
                {
                    try { Marshal.ReleaseComObject(stepObj); } catch { /* 忽略释放失败 */ }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ApplyPlacement 诊断: AddExplodeStep2 调用本身抛异常: {0}", comp.Name2);
            }
        }

        /// <summary>swCreateExplodeStepError_e 反射确认的枚举值,纯本地映射,不引入 swconst 依赖。</summary>
        private static string DecodeCreateExplodeStepError(int code)
        {
            switch (code)
            {
                case 0: return "Successful";
                case 1: return "Generic";
                case 2: return "NoExplodeView";
                case 3: return "NoComponents";
                case 4: return "InvalidRadialAxis";
                case 5: return "OpenExplodePMP";
                case 6: return "EditingComponentInContext";
                default: return "Unknown";
            }
        }

        /// <summary>
        /// 组件局部 X/Y/Z 轴在世界坐标下的方向单位向量,取自 Transform2.ArrayData 的 3x3 旋转矩阵
        /// (列主序:[0..2]=局部X轴,[3..5]=局部Y轴,[6..8]=局部Z轴 — SW MathTransform 标准布局)。
        /// </summary>
        private static bool TryGetComponentAxes(IComponent2 comp, out double[] worldX, out double[] worldY, out double[] worldZ)
        {
            worldX = worldY = worldZ = null;
            MathTransform xf = null;
            try
            {
                xf = comp.Transform2;
                if (xf == null) return false;
                object arrObj = xf.ArrayData;
                var arr = arrObj as double[];
                if (arr == null || arr.Length < 9) return false;

                worldX = new double[] { arr[0], arr[1], arr[2] };
                worldY = new double[] { arr[3], arr[4], arr[5] };
                worldZ = new double[] { arr[6], arr[7], arr[8] };
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (xf != null) Marshal.ReleaseComObject(xf);
            }
        }

        private static void ReleaseAllExcept(object[] comps, IComponent2 keep)
        {
            foreach (object co in comps)
            {
                if (!ReferenceEquals(co, keep))
                {
                    try { Marshal.ReleaseComObject(co); } catch { /* 忽略释放失败 */ }
                }
            }
        }
    }
}
