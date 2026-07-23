using System;
using System.Collections.Generic;
using System.Linq;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Services
{
    /// <summary>
    /// W10 智能爆炸(SmartHybrid)的纯几何布局规划器。零 COM,全 internal,单测全覆盖。
    ///
    /// 输入:各组件的世界坐标包围盒(米) + ExplodeOptions。
    /// 输出:每个非跳过组件的爆炸方向单位向量 + 距离(米)。
    ///
    /// 三种落位:
    /// - 主体件:方向 = normalize(质心 - 装配中心) 向外发散,距离按到中心距离 + 尺寸归一化。
    /// - 同轴紧固件堆叠(≥2 件共轴):沿公共轴正方向均匀等距排开。
    /// - 单件紧固件:从主轴径向外推。
    ///
    /// 单位:opt 里带 Mm 的字段是毫米,这里入口统一换算成米(SW API 用米)。
    /// </summary>
    internal static class ExplodeLayoutPlanner
    {
        public static ExplodeLayoutResult Plan(IReadOnlyList<ComponentGeometry> comps, ExplodeOptions opt)
        {
            if (comps == null) throw new ArgumentNullException(nameof(comps));
            if (opt == null) throw new ArgumentNullException(nameof(opt));

            var result = new ExplodeLayoutResult();

            var active = comps.Where(c => c != null && !c.IsSkipped && c.HasValidBox).ToList();
            if (active.Count == 0) return result;

            double[] center = ComputeAssemblyCenter(active);
            double diag = ComputeAssemblyDiagonal(active);
            double asmVolume = UnionBoxVolume(active);
            double[] mainAxis = MainAxis(active);

            result.AssemblyCenter = center;
            result.AssemblyDiagonal = diag;

            var bodies = new List<ComponentGeometry>();
            var fasteners = new List<ComponentGeometry>();
            foreach (var g in active)
            {
                if (ClassifyRole(g, opt, asmVolume, diag) == ExplodeRole.Fastener) fasteners.Add(g);
                else bodies.Add(g);
            }

            // --- 主体件:以最大主体为基准，沿装配主轴展开 ---
            // 主壳体/底座留在原位，其他主体件按相对位置投影到一个主轴上。这会保留
            // 端盖-轴承-轴等组件的装配阅读关系，而不是把所有件沿对角线向外抛散。
            ComponentGeometry anchor = FindLargestBody(bodies, center);
            double[] bodyReferenceCenter = anchor != null ? anchor.Center : center;
            foreach (var b in bodies)
            {
                if (opt.KeepLargestBodyCentered && ReferenceEquals(b, anchor)) continue;
                result.Placements.Add(new ExplodePlacement
                {
                    ComponentName = b.ComponentName,
                    Index = b.Index,
                    Direction = opt.SnapBodyDirectionsToAssemblyAxes
                        ? AxisAlignedDivergenceDirection(bodyReferenceCenter, b.Center, opt)
                        : DivergenceDirection(bodyReferenceCenter, b.Center, opt),
                    DistanceMeters = NormalizedDistance(b, bodyReferenceCenter, diag, opt),
                    Role = ExplodeRole.Body
                });
            }

            // --- 同轴紧固件堆叠 ---
            var groups = DetectCoaxialGroups(fasteners, opt);
            var stacked = new HashSet<ComponentGeometry>();
            int groupId = 0;
            foreach (var grp in groups)
            {
                if (grp.Members.Count < 2) continue;
                grp.Id = groupId++;
                AssignStackDistances(grp, opt, result.Placements, bodyReferenceCenter);
                foreach (var m in grp.Members) stacked.Add(m);
            }

            // --- 单件紧固件:径向外推 ---
            double radialDist = opt.FastenerRadialDistanceFraction * diag;
            foreach (var f in fasteners)
            {
                if (stacked.Contains(f)) continue;
                result.Placements.Add(new ExplodePlacement
                {
                    ComponentName = f.ComponentName,
                    Index = f.Index,
                    // 默认沿零件自身轴向拆开，和螺钉/销/导柱的实际装配路径一致。
                    // 径向爆炸仍可在配置中显式启用，用于确实需要围绕中心散开的场景。
                    Direction = opt.UseRadialForIsolatedFasteners
                        ? RadialDirection(f, bodyReferenceCenter, mainAxis, opt)
                        : AxialOutwardDirection(f, bodyReferenceCenter),
                    DistanceMeters = radialDist,
                    Role = ExplodeRole.Fastener,
                    UseRadialStep = opt.EnableRadialSteps && opt.UseRadialForIsolatedFasteners
                });
            }

            return result;
        }

        // ===== 装配尺度 =====

        /// <summary>各组件包围盒的并集盒 [xmin,ymin,zmin,xmax,ymax,zmax]。active 非空且都 HasValidBox。</summary>
        internal static double[] UnionBox(IReadOnlyList<ComponentGeometry> active)
        {
            double xmin = double.MaxValue, ymin = double.MaxValue, zmin = double.MaxValue;
            double xmax = double.MinValue, ymax = double.MinValue, zmax = double.MinValue;
            foreach (var g in active)
            {
                var b = g.Box;
                if (b[0] < xmin) xmin = b[0];
                if (b[1] < ymin) ymin = b[1];
                if (b[2] < zmin) zmin = b[2];
                if (b[3] > xmax) xmax = b[3];
                if (b[4] > ymax) ymax = b[4];
                if (b[5] > zmax) zmax = b[5];
            }
            return new double[] { xmin, ymin, zmin, xmax, ymax, zmax };
        }

        /// <summary>装配中心 = 并集盒中心(非质心均值,避免大件把中心拽偏)。</summary>
        internal static double[] ComputeAssemblyCenter(IReadOnlyList<ComponentGeometry> active)
        {
            double[] u = UnionBox(active);
            return new double[]
            {
                (u[0] + u[3]) / 2.0,
                (u[1] + u[4]) / 2.0,
                (u[2] + u[5]) / 2.0
            };
        }

        /// <summary>并集盒对角线,作距离归一化基准。</summary>
        internal static double ComputeAssemblyDiagonal(IReadOnlyList<ComponentGeometry> active)
        {
            double[] u = UnionBox(active);
            double dx = u[3] - u[0], dy = u[4] - u[1], dz = u[5] - u[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        internal static double UnionBoxVolume(IReadOnlyList<ComponentGeometry> active)
        {
            double[] u = UnionBox(active);
            return Math.Abs(u[3] - u[0]) * Math.Abs(u[4] - u[1]) * Math.Abs(u[5] - u[2]);
        }

        // ===== 分类 =====

        /// <summary>
        /// 主体件 vs 紧固件。优先依据可复用的几何标准：装配相对尺寸、薄板形态、
        /// 杆件截面对称性及体积；名称只在用户显式启用时作为最后的辅助信号。
        /// </summary>
        internal static ExplodeRole ClassifyRole(ComponentGeometry g, ExplodeOptions opt, double asmVolume, double assemblyDiagonal = 0)
        {
            string lname = (g.BaseName ?? "").ToLowerInvariant();
            double[] dims = g.Dims.OrderBy(d => d).ToArray(); // 升序 [thin,mid,long]
            double thin = dims[0];
            double mid = Math.Max(dims[1], Vec3.Eps);
            double longest = dims[2];

            // 大到占据装配显著尺度的组件是主结构，即使它很薄或很长。
            if (assemblyDiagonal > Vec3.Eps && longest / assemblyDiagonal >= opt.StructuralMinSizeFraction)
                return ExplodeRole.Body;

            // 薄而宽的件是板/盖/夹具，不是螺钉。此规则不依赖文件命名。
            bool isPlanarPlate = thin / mid <= opt.PlateThicknessRatio && mid / Math.Max(longest, Vec3.Eps) >= opt.PlatePlanarRatio;
            if (isPlanarPlate) return ExplodeRole.Body;

            if (opt.UseNameHeuristics && opt.StructuralNameKeywords != null)
            {
                foreach (var kw in opt.StructuralNameKeywords)
                {
                    if (!string.IsNullOrEmpty(kw) && lname.Contains(kw.ToLowerInvariant()))
                        return ExplodeRole.Body;
                }
            }
            if (opt.UseNameHeuristics && opt.FastenerNameKeywords != null)
            {
                foreach (var kw in opt.FastenerNameKeywords)
                {
                    if (!string.IsNullOrEmpty(kw) && lname.Contains(kw.ToLowerInvariant()))
                        return ExplodeRole.Fastener;
                }
            }

            double crossSectionSimilarity = thin / mid;
            double longRatio = longest / mid;
            double volRatio = asmVolume > Vec3.Eps ? g.Volume / asmVolume : 1.0;

            if (volRatio < opt.FastenerVolumeFraction)
            {
                // 杆/销/螺钉应有两条近似相等的截面边；仅仅又长又薄的板条不应误判。
                if (crossSectionSimilarity >= opt.RodCrossSectionSimilarity && longRatio >= opt.FastenerAspectRatio)
                    return ExplodeRole.Fastener;
                if (longest < opt.FastenerMaxSizeMm / 1000.0) return ExplodeRole.Fastener;  // 小型螺母/垫圈
            }
            return ExplodeRole.Body;
        }

        // ===== 主体件方向 + 距离 =====

        /// <summary>发散方向 = normalize(质心 - 装配中心);退化(重合)时用 DefaultDivergeAxis。</summary>
        internal static double[] DivergenceDirection(double[] center, double[] centroid, ExplodeOptions opt)
        {
            double[] n = Vec3.Normalize(Vec3.Sub(centroid, center));
            if (n != null) return n;
            double[] fallback = Vec3.Normalize(opt.DefaultDivergeAxis ?? new double[] { 0, 0, 1 });
            return fallback ?? new double[] { 0, 0, 1 };
        }

        /// <summary>
        /// 选取相对基准件偏移量最大的世界坐标轴，并保留正负号。工程图中的主要拆分
        /// 通常沿装配的局部 X/Y/Z 方向；将方向吸附到轴能避免斜向、放射状的“拉伸”外观。
        /// </summary>
        internal static double[] AxisAlignedDivergenceDirection(double[] center, double[] centroid, ExplodeOptions opt)
        {
            double[] offset = Vec3.Sub(centroid, center);
            int dominant = 0;
            for (int i = 1; i < 3; i++)
            {
                if (Math.Abs(offset[i]) > Math.Abs(offset[dominant])) dominant = i;
            }
            if (Math.Abs(offset[dominant]) < Vec3.Eps)
                return DivergenceDirection(center, centroid, opt);

            double[] direction = AxisUnit(dominant);
            if (offset[dominant] < 0) direction[dominant] = -1;
            return direction;
        }

        /// <summary>沿单件自身主轴、朝远离主件的一侧移动，适用于未进入同轴堆叠的螺钉和销。</summary>
        internal static double[] AxialOutwardDirection(ComponentGeometry component, double[] assemblyCenter)
        {
            double[] axis = PrimaryAxis(component);
            double[] offset = Vec3.Sub(component.Center, assemblyCenter);
            return Vec3.Dot(offset, axis) < 0 ? Vec3.Scale(axis, -1) : axis;
        }

        internal static ComponentGeometry FindLargestBody(IReadOnlyList<ComponentGeometry> bodies, double[] assemblyCenter)
        {
            if (bodies == null || bodies.Count == 0) return null;
            return bodies
                .OrderByDescending(b => b.Volume)
                .ThenBy(b => Vec3.Distance(b.Center, assemblyCenter))
                .First();
        }

        /// <summary>
        /// 距离 = 基准 ×(1 + 离心) ×(1 - 大件阻尼),clamp 到 [MinDistanceMm, MaxDistanceMm]。
        /// 离中心越远推越远,件越大推越近(防重叠)。
        /// </summary>
        internal static double NormalizedDistance(ComponentGeometry g, double[] center, double diag, ExplodeOptions opt)
        {
            double safeDiag = Math.Max(diag, Vec3.Eps);
            double distToCenter = Vec3.Distance(g.Center, center);
            double sizeFactor = g.MaxDim / safeDiag;                     // 0..~1
            double baseDist = opt.BodyBaseDistanceFraction * diag;
            double d = baseDist * (1 + opt.DistanceSpreadK * (distToCenter / safeDiag));
            d = d * (1 - opt.SizeDampingK * sizeFactor);

            double minM = opt.MinDistanceMm / 1000.0;
            double maxM = opt.MaxDistanceMm / 1000.0;
            if (d < minM) d = minM;
            if (d > maxM) d = maxM;
            return d;
        }

        // ===== 同轴组 =====

        /// <summary>
        /// 组件主轴单位向量(AABB 近似):取"另外两边最接近"的那根轴的第三边作为轴 ——
        /// 旋转对称体的另外两边(直径方向)应大致相等,不管第三边比它们大(螺栓长轴/导套高度)
        /// 还是小(垫圈厚度)。比"细长→长边/否则→短边"的固定比例阈值更通用,
        /// 尤其能处理导套这类"高度只比直径略大、够不到细长比阈值"的矮胖形状(见 Part A Phase 3b)。
        /// </summary>
        internal static double[] PrimaryAxis(ComponentGeometry g)
        {
            double[] dims = g.Dims;
            int axisIdx = 0;
            double bestDiff = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                int k = (i + 2) % 3;
                double diff = Math.Abs(dims[j] - dims[k]);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    axisIdx = i;
                }
            }
            return AxisUnit(axisIdx);
        }

        private static double[] AxisUnit(int idx)
        {
            var r = new double[] { 0, 0, 0 };
            r[idx] = 1;
            return r;
        }

        /// <summary>全局主轴 = 并集盒最长边方向;退化取 +Z。</summary>
        internal static double[] MainAxis(IReadOnlyList<ComponentGeometry> active)
        {
            double[] u = UnionBox(active);
            double[] dims = { Math.Abs(u[3] - u[0]), Math.Abs(u[4] - u[1]), Math.Abs(u[5] - u[2]) };
            int idxMax = 0;
            for (int i = 1; i < 3; i++) if (dims[i] > dims[idxMax]) idxMax = i;
            if (dims[idxMax] < Vec3.Eps) return new double[] { 0, 0, 1 };
            return AxisUnit(idxMax);
        }

        /// <summary>
        /// 按几何(不依赖 mate)把紧固件归组:轴夹角 &lt; CoaxialAngleTolDeg 且质心到组轴垂距 &lt; CoaxialRadialTolMm。
        /// </summary>
        internal static List<CoaxialGroup> DetectCoaxialGroups(IReadOnlyList<ComponentGeometry> fasteners, ExplodeOptions opt)
        {
            var groups = new List<CoaxialGroup>();
            if (fasteners == null) return groups;

            double cosTol = Math.Cos(opt.CoaxialAngleTolDeg * Math.PI / 180.0);
            double radialTolM = opt.CoaxialRadialTolMm / 1000.0;

            foreach (var f in fasteners)
            {
                double[] axis = PrimaryAxis(f);
                double[] cen = f.Center;
                CoaxialGroup target = null;
                foreach (var grp in groups)
                {
                    bool angleOk = Math.Abs(Vec3.Dot(grp.Axis, axis)) > cosTol;
                    bool perpOk = Vec3.PerpendicularDistance(cen, grp.AxisPoint, grp.Axis) < radialTolM;
                    if (angleOk && perpOk) { target = grp; break; }
                }
                if (target == null)
                {
                    target = new CoaxialGroup { Axis = axis, AxisPoint = cen };
                    groups.Add(target);
                }
                target.Members.Add(f);
            }
            return groups;
        }

        /// <summary>兼容旧调用：同轴堆叠默认沿组轴正方向展开。</summary>
        internal static void AssignStackDistances(CoaxialGroup grp, ExplodeOptions opt, List<ExplodePlacement> outList)
        {
            AssignStackDistances(grp, opt, outList, null);
        }

        /// <summary>
        /// 同轴堆叠从装配中心向外展开。位于中心负侧的组沿负轴移动，并倒序处理，
        /// 使端盖、轴承、垫圈等仍按从内到外的装配顺序排列。
        /// </summary>
        internal static void AssignStackDistances(CoaxialGroup grp, ExplodeOptions opt, List<ExplodePlacement> outList, double[] assemblyCenter)
        {
            double spacing = opt.CoaxialSpacingMm / 1000.0;
            double[] direction = grp.Axis;
            if (assemblyCenter != null)
            {
                double[] groupCenter = new double[3];
                foreach (var member in grp.Members) groupCenter = Vec3.Add(groupCenter, member.Center);
                groupCenter = Vec3.Scale(groupCenter, 1.0 / grp.Members.Count);
                if (Vec3.Dot(Vec3.Sub(groupCenter, assemblyCenter), grp.Axis) < 0)
                    direction = Vec3.Scale(grp.Axis, -1);
            }

            var ordered = grp.Members
                .OrderBy(m => Vec3.Dot(Vec3.Sub(m.Center, grp.AxisPoint), direction))
                .ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i];
                outList.Add(new ExplodePlacement
                {
                    ComponentName = m.ComponentName,
                    Index = m.Index,
                    Direction = direction,
                    DistanceMeters = spacing * (i + 1),
                    Role = ExplodeRole.Fastener,
                    CoaxialGroupId = grp.Id,
                    StackOrder = i
                });
            }
        }

        /// <summary>单件紧固件径向方向 = 质心相对主轴的垂直分量归一化;退化时用发散方向。</summary>
        internal static double[] RadialDirection(ComponentGeometry f, double[] center, double[] mainAxis, ExplodeOptions opt)
        {
            double[] w = Vec3.Sub(f.Center, center);
            double proj = Vec3.Dot(w, mainAxis);
            double[] radial = Vec3.Sub(w, Vec3.Scale(mainAxis, proj));
            double[] n = Vec3.Normalize(radial);
            if (n != null) return n;
            return DivergenceDirection(center, f.Center, opt);
        }
    }
}
