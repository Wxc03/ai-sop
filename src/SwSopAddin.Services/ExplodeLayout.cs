using System;
using System.Collections.Generic;

namespace SwSopAddin.Services
{
    // W10 智能爆炸 — 纯几何数据结构 + 向量运算。零 COM 依赖,全 internal,
    // 由 SwSopAddin.Tests 通过 InternalsVisibleTo 单测覆盖(照 ExplodeService.ShouldSkip 范式)。
    // 约定:所有坐标/向量用 double[3](x,y,z),包围盒用 double[6](xmin,ymin,zmin,xmax,ymax,zmax),
    // 单位一律米(与 SW API 一致);config 里的毫米在 ExplodeLayoutPlanner 入口换算成米。

    /// <summary>零件在爆炸布局里的角色。</summary>
    internal enum ExplodeRole
    {
        Body,      // 主体件 — 按相对装配中心的位置向外发散
        Fastener   // 紧固件 — 同轴堆叠均匀排开 / 单件径向外推
    }

    /// <summary>单个组件的几何快照(由 COM 层读 GetBox 填充,纯层只读)。</summary>
    internal sealed class ComponentGeometry
    {
        /// <summary>SW 组件全名(c.Name2),后续 SelectByID2 用。</summary>
        public string ComponentName { get; set; }
        /// <summary>无扩展名文件名,紧固件关键词分类用。</summary>
        public string BaseName { get; set; }
        /// <summary>世界坐标包围盒 [xmin,ymin,zmin,xmax,ymax,zmax](米)。</summary>
        public double[] Box { get; set; }
        /// <summary>是否被 SkipNamePrefixes 过滤(过滤件不参与布局)。</summary>
        public bool IsSkipped { get; set; }
        /// <summary>在原始组件列表里的索引(回填 explode step 用)。</summary>
        public int Index { get; set; }

        /// <summary>包围盒中心(用作质心近似)。Box 非法时返回 (0,0,0)。</summary>
        public double[] Center
        {
            get
            {
                if (!HasValidBox) return new double[] { 0, 0, 0 };
                return new double[]
                {
                    (Box[0] + Box[3]) / 2.0,
                    (Box[1] + Box[4]) / 2.0,
                    (Box[2] + Box[5]) / 2.0
                };
            }
        }

        /// <summary>三维尺寸 (dx,dy,dz)。Box 非法时返回全 0。</summary>
        public double[] Dims
        {
            get
            {
                if (!HasValidBox) return new double[] { 0, 0, 0 };
                return new double[]
                {
                    Math.Abs(Box[3] - Box[0]),
                    Math.Abs(Box[4] - Box[1]),
                    Math.Abs(Box[5] - Box[2])
                };
            }
        }

        public double Volume
        {
            get
            {
                double[] d = Dims;
                return d[0] * d[1] * d[2];
            }
        }

        /// <summary>最长边。</summary>
        public double MaxDim
        {
            get
            {
                double[] d = Dims;
                return Math.Max(d[0], Math.Max(d[1], d[2]));
            }
        }

        public bool HasValidBox => Box != null && Box.Length == 6;
    }

    /// <summary>一个组件的爆炸落位:方向 + 距离 + 角色。</summary>
    internal sealed class ExplodePlacement
    {
        public string ComponentName { get; set; }
        public int Index { get; set; }
        /// <summary>爆炸方向单位向量。</summary>
        public double[] Direction { get; set; }
        /// <summary>爆炸距离(米)。</summary>
        public double DistanceMeters { get; set; }
        public ExplodeRole Role { get; set; }
        /// <summary>所属同轴组 id;-1 表示不属于任何组。</summary>
        public int CoaxialGroupId { get; set; } = -1;
        /// <summary>在同轴堆叠里的顺序(0 起);非堆叠件为 0。</summary>
        public int StackOrder { get; set; }
    }

    /// <summary>Plan 的完整输出。</summary>
    internal sealed class ExplodeLayoutResult
    {
        public List<ExplodePlacement> Placements { get; set; } = new List<ExplodePlacement>();
        public double[] AssemblyCenter { get; set; }
        public double AssemblyDiagonal { get; set; }
    }

    /// <summary>同轴紧固件组(如一串螺栓,或螺栓-垫圈-螺母堆叠)。</summary>
    internal sealed class CoaxialGroup
    {
        public int Id { get; set; }
        /// <summary>组的公共轴向单位向量。</summary>
        public double[] Axis { get; set; }
        /// <summary>轴上一点(取第一个成员质心)。</summary>
        public double[] AxisPoint { get; set; }
        public List<ComponentGeometry> Members { get; set; } = new List<ComponentGeometry>();
    }

    /// <summary>double[3] 向量运算。纯静态,无状态,无 COM。</summary>
    internal static class Vec3
    {
        public const double Eps = 1e-9;

        public static double[] Sub(double[] a, double[] b)
            => new double[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

        public static double[] Add(double[] a, double[] b)
            => new double[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };

        public static double[] Scale(double[] a, double s)
            => new double[] { a[0] * s, a[1] * s, a[2] * s };

        public static double Dot(double[] a, double[] b)
            => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        public static double[] Cross(double[] a, double[] b)
            => new double[]
            {
                a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0]
            };

        public static double Length(double[] a)
            => Math.Sqrt(Dot(a, a));

        public static double Distance(double[] a, double[] b)
            => Length(Sub(a, b));

        /// <summary>归一化;近零向量返回 null(调用方决定退化处理)。</summary>
        public static double[] Normalize(double[] a)
        {
            double len = Length(a);
            if (len < Eps) return null;
            return new double[] { a[0] / len, a[1] / len, a[2] / len };
        }

        /// <summary>点 p 到过 axisPoint、方向 axisDir(需已归一化)的直线的垂直距离。</summary>
        public static double PerpendicularDistance(double[] p, double[] axisPoint, double[] axisDir)
        {
            double[] w = Sub(p, axisPoint);
            double proj = Dot(w, axisDir);
            double[] parallel = Scale(axisDir, proj);
            double[] perp = Sub(w, parallel);
            return Length(perp);
        }
    }
}
