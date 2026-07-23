using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSopAddin.Infrastructure;
using SwSopAddin.Services;

namespace SwSopAddin.Tests
{
    /// <summary>
    /// Part A Phase 0 — ExplodeLayoutPlanner / ExplodeLayout 纯几何逻辑单测。
    ///
    /// 范围:零 COM,只测 SwSopAddin.Services 里 internal 的纯函数(InternalsVisibleTo 已在
    /// ExplodeServiceTests.cs 里确认可用)。生产代码本身(ExplodeLayout.cs / ExplodeLayoutPlanner.cs)
    /// 本次未改动一个字符,这里只是补测试。
    /// </summary>
    [TestClass]
    public class ExplodeLayoutPlannerTests
    {
        private const double Tol = 1e-6;

        private static ExplodeOptions DefaultOpt() => new ExplodeOptions();

        private static ComponentGeometry MakeBox(string name, double[] box, int index = 0)
        {
            return new ComponentGeometry
            {
                ComponentName = name,
                BaseName = name,
                Box = box,
                IsSkipped = false,
                Index = index
            };
        }

        // ===== Plan: 空输入 / 全跳过 =====

        [TestMethod]
        public void Plan_EmptyList_ReturnsEmptyResult()
        {
            var result = ExplodeLayoutPlanner.Plan(new List<ComponentGeometry>(), DefaultOpt());
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Placements.Count);
            Assert.IsNull(result.AssemblyCenter);
        }

        [TestMethod]
        public void Plan_AllSkipped_ReturnsEmptyResult()
        {
            var comps = new List<ComponentGeometry>
            {
                MakeBox("Part1", new double[] { 0, 0, 0, 0.1, 0.1, 0.1 }),
            };
            comps[0].IsSkipped = true;

            var result = ExplodeLayoutPlanner.Plan(comps, DefaultOpt());
            Assert.AreEqual(0, result.Placements.Count);
        }

        [TestMethod]
        public void Plan_InvalidBox_TreatedAsInactive()
        {
            var comps = new List<ComponentGeometry>
            {
                MakeBox("NoBox", null),
            };
            var result = ExplodeLayoutPlanner.Plan(comps, DefaultOpt());
            Assert.AreEqual(0, result.Placements.Count);
        }

        [TestMethod]
        public void Plan_SingleBody_RemainsAsTheCenteredAnchor()
        {
            // 单一主体就是装配基准，不应被推离原始位置。
            var comps = new List<ComponentGeometry>
            {
                MakeBox("Body1", new double[] { -0.05, -0.05, -0.05, 0.05, 0.05, 0.05 }, index: 0),
            };
            var result = ExplodeLayoutPlanner.Plan(comps, DefaultOpt());

            Assert.AreEqual(0, result.Placements.Count);
        }

        [TestMethod]
        public void Plan_LargestBodyStaysCentered_OtherBodiesMoveAlongSignedAssemblyAxis()
        {
            var comps = new List<ComponentGeometry>
            {
                MakeBox("Housing", new double[] { -0.05, -0.05, -0.05, 0.05, 0.05, 0.05 }, index: 0),
                MakeBox("LeftCover", new double[] { -0.15, -0.04, -0.04, -0.07, 0.04, 0.04 }, index: 1),
                MakeBox("TopSupport", new double[] { -0.03, 0.08, -0.03, 0.03, 0.14, 0.03 }, index: 2),
            };

            var result = ExplodeLayoutPlanner.Plan(comps, DefaultOpt());

            Assert.AreEqual(2, result.Placements.Count);
            Assert.IsFalse(result.Placements.Any(p => p.ComponentName == "Housing"));
            var left = result.Placements.Single(p => p.ComponentName == "LeftCover");
            var top = result.Placements.Single(p => p.ComponentName == "TopSupport");
            CollectionAssert.AreEqual(new double[] { -1, 0, 0 }, left.Direction);
            CollectionAssert.AreEqual(new double[] { 0, 1, 0 }, top.Direction);
        }

        // ===== ClassifyRole 优先级:名字关键词 > 长径比 > 体积占比 =====

        [TestMethod]
        public void ClassifyRole_NameKeywordMatch_ReturnsFastener_EvenIfBigAndCubic()
        {
            // 名称辅助只在用户显式启用时生效。
            var opt = DefaultOpt();
            opt.UseNameHeuristics = true;
            var g = MakeBox("Bolt_M8x40", new double[] { 0, 0, 0, 0.5, 0.5, 0.5 });
            double asmVolume = g.Volume * 2; // volRatio = 0.5,远超 FastenerVolumeFraction

            Assert.AreEqual(ExplodeRole.Fastener, ExplodeLayoutPlanner.ClassifyRole(g, opt, asmVolume));
        }

        [TestMethod]
        public void ClassifyRole_StructuralKeywordKeepsThinBasePlateAsBody()
        {
            var opt = DefaultOpt();
            var g = MakeBox("Base Plate", new double[] { 0, 0, 0, 0.006, 0.02, 0.144 });

            Assert.AreEqual(ExplodeRole.Body, ExplodeLayoutPlanner.ClassifyRole(g, opt, asmVolume: 0.1));
        }

        [TestMethod]
        public void ClassifyRole_PlanarComponentIsBodyWithoutAnyNamingConvention()
        {
            var opt = DefaultOpt();
            var g = MakeBox("Part_001", new double[] { 0, 0, 0, 0.006, 0.06, 0.08 });

            Assert.AreEqual(ExplodeRole.Body, ExplodeLayoutPlanner.ClassifyRole(g, opt, asmVolume: 0.1));
        }

        [TestMethod]
        public void ClassifyRole_RodLikeComponentIsFastenerWithoutAnyNamingConvention()
        {
            var opt = DefaultOpt();
            var g = MakeBox("Part_002", new double[] { 0, 0, 0, 0.008, 0.008, 0.08 });

            Assert.AreEqual(ExplodeRole.Fastener, ExplodeLayoutPlanner.ClassifyRole(g, opt, asmVolume: 0.1));
        }

        [TestMethod]
        public void ClassifyRole_ElongatedSmallVolume_ReturnsFastener_ByAspectRatio()
        {
            // 无关键词命中,但细长(长径比 >= 3.0)且体积占比小
            var opt = DefaultOpt();
            var g = MakeBox("Pin1", new double[] { 0, 0, 0, 0.001, 0.001, 0.05 }); // 50mm x 1mm x 1mm
            double asmVolume = g.Volume / 0.001; // volRatio 远小于 FastenerVolumeFraction(0.02)

            Assert.AreEqual(ExplodeRole.Fastener, ExplodeLayoutPlanner.ClassifyRole(g, opt, asmVolume));
        }

        [TestMethod]
        public void ClassifyRole_SmallCubicSmallVolume_ReturnsFastener_ByMaxSize()
        {
            // 无关键词,长径比不够(立方体),但最长边 < FastenerMaxSizeMm(30mm)且体积占比小
            var opt = DefaultOpt();
            var g = MakeBox("Nut1", new double[] { 0, 0, 0, 0.01, 0.01, 0.01 }); // 10mm 立方体
            double asmVolume = g.Volume / 0.001;

            Assert.AreEqual(ExplodeRole.Fastener, ExplodeLayoutPlanner.ClassifyRole(g, opt, asmVolume));
        }

        [TestMethod]
        public void ClassifyRole_LargeVolume_ReturnsBody_DespiteElongatedShape()
        {
            // 体积占比不小(>= FastenerVolumeFraction)时,即便细长也判 Body
            // (体积占比检查是分类的前置门槛:volRatio < FastenerVolumeFraction 才继续判长径比/尺寸)
            var opt = DefaultOpt();
            var g = MakeBox("LongFrame", new double[] { 0, 0, 0, 0.05, 0.05, 1.0 }); // 长径比 20,但占比大
            double asmVolume = g.Volume * 1.5; // volRatio = 1/1.5 = 0.667,远超 0.02

            Assert.AreEqual(ExplodeRole.Body, ExplodeLayoutPlanner.ClassifyRole(g, opt, asmVolume));
        }

        [TestMethod]
        public void ClassifyRole_CubicLargeSize_ReturnsBody()
        {
            var opt = DefaultOpt();
            var g = MakeBox("Block1", new double[] { 0, 0, 0, 0.1, 0.1, 0.1 }); // 100mm 立方,长径比 1
            double asmVolume = g.Volume * 1.2;

            Assert.AreEqual(ExplodeRole.Body, ExplodeLayoutPlanner.ClassifyRole(g, opt, asmVolume));
        }

        // ===== DivergenceDirection 退化情形 =====

        [TestMethod]
        public void DivergenceDirection_CentroidEqualsCenter_FallsBackToDefaultAxis()
        {
            var opt = DefaultOpt();
            opt.DefaultDivergeAxis = new double[] { 0, 1, 0 };
            double[] center = { 1, 2, 3 };
            double[] centroid = { 1, 2, 3 }; // 完全重合 → Sub 得零向量 → Normalize 返回 null

            var dir = ExplodeLayoutPlanner.DivergenceDirection(center, centroid, opt);

            Assert.AreEqual(0, dir[0], Tol);
            Assert.AreEqual(1, dir[1], Tol);
            Assert.AreEqual(0, dir[2], Tol);
        }

        [TestMethod]
        public void DivergenceDirection_NonDegenerateOffset_ReturnsNormalizedVector()
        {
            var opt = DefaultOpt();
            double[] center = { 0, 0, 0 };
            double[] centroid = { 3, 4, 0 }; // 长度 5

            var dir = ExplodeLayoutPlanner.DivergenceDirection(center, centroid, opt);

            Assert.AreEqual(0.6, dir[0], Tol);
            Assert.AreEqual(0.8, dir[1], Tol);
            Assert.AreEqual(0.0, dir[2], Tol);
        }

        [TestMethod]
        public void DivergenceDirection_NullDefaultAxis_FallsBackToPlusZ()
        {
            var opt = DefaultOpt();
            opt.DefaultDivergeAxis = null;
            double[] center = { 0, 0, 0 };
            double[] centroid = { 0, 0, 0 };

            var dir = ExplodeLayoutPlanner.DivergenceDirection(center, centroid, opt);

            Assert.AreEqual(0, dir[0], Tol);
            Assert.AreEqual(0, dir[1], Tol);
            Assert.AreEqual(1, dir[2], Tol);
        }

        [TestMethod]
        public void AxisAlignedDivergenceDirection_SnapsDiagonalOffsetToDominantSignedAxis()
        {
            var opt = DefaultOpt();
            double[] center = { 0, 0, 0 };
            double[] centroid = { -0.08, 0.03, 0.02 };

            var dir = ExplodeLayoutPlanner.AxisAlignedDivergenceDirection(center, centroid, opt);

            CollectionAssert.AreEqual(new double[] { -1, 0, 0 }, dir);
        }

        // ===== NormalizedDistance clamp =====

        [TestMethod]
        public void NormalizedDistance_ClampsToMinDistanceMm()
        {
            var opt = DefaultOpt();
            opt.MinDistanceMm = 50; // 50mm = 0.05m,故意抬高下限
            var g = MakeBox("Tiny", new double[] { 0, 0, 0, 0.001, 0.001, 0.001 });
            double[] center = g.Center; // 距中心 0 → baseDist 也很小

            double d = ExplodeLayoutPlanner.NormalizedDistance(g, center, diag: 0.01, opt);

            Assert.AreEqual(0.05, d, Tol);
        }

        [TestMethod]
        public void NormalizedDistance_ClampsToMaxDistanceMm()
        {
            var opt = DefaultOpt();
            opt.MaxDistanceMm = 100; // 100mm = 0.1m,故意压低上限
            opt.BodyBaseDistanceFraction = 5.0; // 放大基准距离,确保触顶
            var g = MakeBox("Far", new double[] { 9, 9, 9, 9.1, 9.1, 9.1 });
            double[] center = { 0, 0, 0 };

            double d = ExplodeLayoutPlanner.NormalizedDistance(g, center, diag: 1.0, opt);

            Assert.AreEqual(0.1, d, Tol);
        }

        [TestMethod]
        public void NormalizedDistance_WithinRange_NotClamped()
        {
            var opt = DefaultOpt();
            var g = MakeBox("Mid", new double[] { 0.1, 0, 0, 0.15, 0.05, 0.05 });
            double[] center = { 0, 0, 0 };
            double diag = 1.0;

            double d = ExplodeLayoutPlanner.NormalizedDistance(g, center, diag, opt);

            double minM = opt.MinDistanceMm / 1000.0;
            double maxM = opt.MaxDistanceMm / 1000.0;
            Assert.IsTrue(d >= minM - Tol && d <= maxM + Tol);
        }

        // ===== DetectCoaxialGroups / AssignStackDistances =====

        [TestMethod]
        public void DetectCoaxialGroups_TwoAlignedFasteners_GroupedTogether()
        {
            var opt = DefaultOpt();
            // 两个沿 Z 轴的细长螺栓,轴线重合(x=y=0),Z 方向不同位置
            var f1 = MakeBox("Bolt1", new double[] { -0.002, -0.002, 0, 0.002, 0.002, 0.02 }, index: 0);
            var f2 = MakeBox("Bolt2", new double[] { -0.002, -0.002, 0.03, 0.002, 0.002, 0.05 }, index: 1);

            var groups = ExplodeLayoutPlanner.DetectCoaxialGroups(new List<ComponentGeometry> { f1, f2 }, opt);

            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(2, groups[0].Members.Count);
        }

        [TestMethod]
        public void DetectCoaxialGroups_MisalignedFasteners_KeptSeparate()
        {
            var opt = DefaultOpt();
            // 一个沿 Z 轴细长件,一个沿 X 轴细长件 — 轴向夹角远超容差
            var f1 = MakeBox("BoltZ", new double[] { -0.002, -0.002, 0, 0.002, 0.002, 0.02 }, index: 0);
            var f2 = MakeBox("BoltX", new double[] { 0.5, -0.002, -0.002, 0.52, 0.002, 0.002 }, index: 1);

            var groups = ExplodeLayoutPlanner.DetectCoaxialGroups(new List<ComponentGeometry> { f1, f2 }, opt);

            Assert.AreEqual(2, groups.Count);
        }

        [TestMethod]
        public void DetectCoaxialGroups_NullInput_ReturnsEmptyList()
        {
            var groups = ExplodeLayoutPlanner.DetectCoaxialGroups(null, DefaultOpt());
            Assert.IsNotNull(groups);
            Assert.AreEqual(0, groups.Count);
        }

        [TestMethod]
        public void AssignStackDistances_OrdersByAxialProjection_EquallySpaced()
        {
            var opt = DefaultOpt();
            opt.CoaxialSpacingMm = 10; // 0.01m

            // 顺序故意打乱:构造 grp.Members = [远, 近, 中],验证按轴向投影重新排序
            var near = MakeBox("Near", new double[] { -0.002, -0.002, 0, 0.002, 0.002, 0.01 }, index: 0);
            var far = MakeBox("Far", new double[] { -0.002, -0.002, 0.04, 0.002, 0.002, 0.05 }, index: 1);
            var mid = MakeBox("Mid", new double[] { -0.002, -0.002, 0.02, 0.002, 0.002, 0.03 }, index: 2);

            var grp = new CoaxialGroup
            {
                Id = 0,
                Axis = new double[] { 0, 0, 1 },
                AxisPoint = near.Center,
                Members = new List<ComponentGeometry> { far, near, mid }
            };

            var placements = new List<ExplodePlacement>();
            ExplodeLayoutPlanner.AssignStackDistances(grp, opt, placements);

            Assert.AreEqual(3, placements.Count);
            Assert.AreEqual("Near", placements[0].ComponentName);
            Assert.AreEqual("Mid", placements[1].ComponentName);
            Assert.AreEqual("Far", placements[2].ComponentName);
            Assert.AreEqual(0.01, placements[0].DistanceMeters, Tol);
            Assert.AreEqual(0.02, placements[1].DistanceMeters, Tol);
            Assert.AreEqual(0.03, placements[2].DistanceMeters, Tol);
            Assert.AreEqual(0, placements[0].StackOrder);
            Assert.AreEqual(1, placements[1].StackOrder);
            Assert.AreEqual(2, placements[2].StackOrder);
        }

        [TestMethod]
        public void AssignStackDistances_GroupOnNegativeSide_MovesOutwardAlongNegativeAxis()
        {
            var opt = DefaultOpt();
            opt.CoaxialSpacingMm = 10;
            var near = MakeBox("Near", new double[] { -0.002, -0.002, -0.02, 0.002, 0.002, -0.01 }, index: 0);
            var far = MakeBox("Far", new double[] { -0.002, -0.002, -0.05, 0.002, 0.002, -0.04 }, index: 1);
            var grp = new CoaxialGroup
            {
                Id = 0,
                Axis = new double[] { 0, 0, 1 },
                AxisPoint = near.Center,
                Members = new List<ComponentGeometry> { near, far }
            };

            var placements = new List<ExplodePlacement>();
            ExplodeLayoutPlanner.AssignStackDistances(grp, opt, placements, new double[] { 0, 0, 0 });

            Assert.AreEqual("Near", placements[0].ComponentName);
            Assert.AreEqual("Far", placements[1].ComponentName);
            CollectionAssert.AreEqual(new double[] { 0, 0, -1 }, placements[0].Direction);
            CollectionAssert.AreEqual(new double[] { 0, 0, -1 }, placements[1].Direction);
            Assert.AreEqual(0.01, placements[0].DistanceMeters, Tol);
            Assert.AreEqual(0.02, placements[1].DistanceMeters, Tol);
        }

        // ===== RadialDirection =====

        [TestMethod]
        public void RadialDirection_OffAxisComponent_ReturnsPerpendicularUnitVector()
        {
            var opt = DefaultOpt();
            double[] center = { 0, 0, 0 };
            double[] mainAxis = { 0, 0, 1 }; // 主轴沿 Z
            var f = MakeBox("Washer1", new double[] { 0.05, -0.01, 0.5, 0.07, 0.01, 0.52 }); // 偏 X

            var dir = ExplodeLayoutPlanner.RadialDirection(f, center, mainAxis, opt);

            // 结果应垂直于主轴(Z 分量应为 0),且是单位向量
            Assert.AreEqual(0.0, dir[2], Tol);
            double len = System.Math.Sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
            Assert.AreEqual(1.0, len, Tol);
        }

        [TestMethod]
        public void RadialDirection_OnAxisComponent_FallsBackToDivergenceDirection()
        {
            var opt = DefaultOpt();
            opt.DefaultDivergeAxis = new double[] { 0, 1, 0 };
            double[] center = { 0, 0, 0 };
            double[] mainAxis = { 0, 0, 1 };
            // 质心正好落在主轴上(x=y=0)→ 径向分量为零向量 → Normalize 返回 null → 退化走 DivergenceDirection
            var f = MakeBox("OnAxis", new double[] { -0.001, -0.001, 0, 0.001, 0.001, 0.01 });
            // OnAxis 的 Center 恰好是 (0,0,0.005),与 center (0,0,0) 相减后仍在轴上 → 触发退化

            var dir = ExplodeLayoutPlanner.RadialDirection(f, center, mainAxis, opt);

            // f.Center 与 center 相减在 Z 轴上,DivergenceDirection(center, f.Center) 会走正常归一化(非退化),
            // 因为 f.Center != center。这里改为验证:结果应等于 DivergenceDirection 的输出。
            var expected = ExplodeLayoutPlanner.DivergenceDirection(center, f.Center, opt);
            Assert.AreEqual(expected[0], dir[0], Tol);
            Assert.AreEqual(expected[1], dir[1], Tol);
            Assert.AreEqual(expected[2], dir[2], Tol);
        }

        [TestMethod]
        public void AxialOutwardDirection_UsesPartAxisAndSideOfAssembly()
        {
            var screw = MakeBox("Set Screw", new double[] { -0.002, -0.04, -0.002, 0.002, -0.01, 0.002 });

            var dir = ExplodeLayoutPlanner.AxialOutwardDirection(screw, new double[] { 0, 0, 0 });

            CollectionAssert.AreEqual(new double[] { 0, -1, 0 }, dir);
        }

        // ===== PrimaryAxis / MainAxis =====

        [TestMethod]
        public void PrimaryAxis_ElongatedAlongZ_ReturnsZUnit()
        {
            var g = MakeBox("Bolt", new double[] { 0, 0, 0, 0.002, 0.002, 0.04 }); // 40mm 长,2mm 截面
            var axis = ExplodeLayoutPlanner.PrimaryAxis(g);
            CollectionAssert.AreEqual(new double[] { 0, 0, 1 }, axis);
        }

        [TestMethod]
        public void PrimaryAxis_FlatWasher_ReturnsThicknessAxis()
        {
            // 扁平垫圈:X/Y 大,Z(厚度)小 且不满足细长比 → 轴 = 最短边方向(Z)
            var g = MakeBox("Washer", new double[] { 0, 0, 0, 0.02, 0.02, 0.002 });
            var axis = ExplodeLayoutPlanner.PrimaryAxis(g);
            CollectionAssert.AreEqual(new double[] { 0, 0, 1 }, axis);
        }

        [TestMethod]
        public void MainAxis_DegenerateBox_FallsBackToPlusZ()
        {
            var comps = new List<ComponentGeometry>
            {
                MakeBox("Point", new double[] { 0, 0, 0, 0, 0, 0 }),
            };
            var axis = ExplodeLayoutPlanner.MainAxis(comps);
            CollectionAssert.AreEqual(new double[] { 0, 0, 1 }, axis);
        }

        // ===== UnionBox / ComputeAssemblyCenter / ComputeAssemblyDiagonal sanity =====

        [TestMethod]
        public void UnionBox_MultipleComponents_ComputesEnclosingBox()
        {
            var comps = new List<ComponentGeometry>
            {
                MakeBox("A", new double[] { -1, -1, -1, 0, 0, 0 }),
                MakeBox("B", new double[] { 0, 0, 0, 2, 2, 2 }),
            };
            var box = ExplodeLayoutPlanner.UnionBox(comps);
            CollectionAssert.AreEqual(new double[] { -1, -1, -1, 2, 2, 2 }, box);
        }

        [TestMethod]
        public void ComputeAssemblyCenter_ReturnsUnionBoxMidpoint()
        {
            var comps = new List<ComponentGeometry>
            {
                MakeBox("A", new double[] { 0, 0, 0, 2, 4, 6 }),
            };
            var center = ExplodeLayoutPlanner.ComputeAssemblyCenter(comps);
            Assert.AreEqual(1.0, center[0], Tol);
            Assert.AreEqual(2.0, center[1], Tol);
            Assert.AreEqual(3.0, center[2], Tol);
        }

        [TestMethod]
        public void ComputeAssemblyDiagonal_UnitCube_ReturnsSqrt3()
        {
            var comps = new List<ComponentGeometry>
            {
                MakeBox("A", new double[] { 0, 0, 0, 1, 1, 1 }),
            };
            double diag = ExplodeLayoutPlanner.ComputeAssemblyDiagonal(comps);
            Assert.AreEqual(System.Math.Sqrt(3), diag, Tol);
        }

        [TestMethod]
        public void UnionBoxVolume_TwoBoxes_ReturnsEnclosingVolume()
        {
            var comps = new List<ComponentGeometry>
            {
                MakeBox("A", new double[] { 0, 0, 0, 1, 1, 1 }),
                MakeBox("B", new double[] { 1, 1, 1, 2, 2, 2 }),
            };
            double vol = ExplodeLayoutPlanner.UnionBoxVolume(comps);
            Assert.AreEqual(8.0, vol, Tol); // 并集盒是 [0,0,0,2,2,2] → 2*2*2=8
        }

        // ===== Vec3 pure math sanity (ExplodeLayout.cs) =====

        [TestMethod]
        public void Vec3_NormalizeZeroVector_ReturnsNull()
        {
            var n = Vec3.Normalize(new double[] { 0, 0, 0 });
            Assert.IsNull(n);
        }

        [TestMethod]
        public void Vec3_NormalizeNonZeroVector_ReturnsUnitVector()
        {
            var n = Vec3.Normalize(new double[] { 3, 4, 0 });
            Assert.AreEqual(0.6, n[0], Tol);
            Assert.AreEqual(0.8, n[1], Tol);
        }

        [TestMethod]
        public void Vec3_PerpendicularDistance_PointOnAxis_ReturnsZero()
        {
            double d = Vec3.PerpendicularDistance(
                p: new double[] { 0, 0, 5 },
                axisPoint: new double[] { 0, 0, 0 },
                axisDir: new double[] { 0, 0, 1 });
            Assert.AreEqual(0.0, d, Tol);
        }

        [TestMethod]
        public void Vec3_PerpendicularDistance_PointOffAxis_ReturnsCorrectDistance()
        {
            double d = Vec3.PerpendicularDistance(
                p: new double[] { 3, 4, 5 },
                axisPoint: new double[] { 0, 0, 0 },
                axisDir: new double[] { 0, 0, 1 });
            Assert.AreEqual(5.0, d, Tol); // sqrt(3^2+4^2) = 5,z 分量被轴向投影吸收
        }
    }
}
