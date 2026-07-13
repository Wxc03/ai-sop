using System;
using System.Globalization;

namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.1 — 包围盒(SW 图纸坐标,米为单位,Y 轴向上)。
    /// 纯值类型,无 COM 依赖。
    /// 注意:SW 图纸原点是左下角,Y 向上;View.GetOutline 返 [Left, Top, Right, Bottom] 时 Top&gt;Bottom。
    /// 我们存 (X, Y, Width, Height) 时统一 X=min(L,R), Y=min(T,B), 这样 Top/Bottom 仍可通过属性还原。
    /// </summary>
    public readonly struct LayoutRect : IEquatable<LayoutRect>
    {
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }

        public LayoutRect(double x, double y, double width, double height)
        {
            if (width < 0)
                throw new ArgumentException($"width 不能为负: {width}", nameof(width));
            if (height < 0)
                throw new ArgumentException($"height 不能为负: {height}", nameof(height));
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public double Left => X;
        public double Right => X + Width;
        public double Bottom => Y;
        public double Top => Y + Height;
        public double CenterX => X + Width / 2.0;
        public double CenterY => Y + Height / 2.0;

        public bool IsEmpty => Width <= 0 || Height <= 0;

        /// <summary>
        /// AABB 重叠检测。
        /// tolerance:正数表示需要更宽松(两个框可以相隔 tolerance 才不重叠),负数严格。
        /// tolerance=0 是标准 AABB。
        /// </summary>
        public static bool Overlaps(LayoutRect a, LayoutRect b, double tolerance = 0)
        {
            if (a.IsEmpty || b.IsEmpty) return false;
            return a.Left < b.Right - tolerance
                && a.Right > b.Left + tolerance
                && a.Bottom < b.Top - tolerance
                && a.Top > b.Bottom + tolerance;
        }

        public LayoutRect Translated(double dx, double dy)
            => new LayoutRect(X + dx, Y + dy, Width, Height);

        public LayoutRect Inflated(double pad)
        {
            if (pad <= 0) return this;
            return new LayoutRect(X - pad, Y - pad, Width + 2 * pad, Height + 2 * pad);
        }

        /// <summary>
        /// 把框 clamp 在 sheet 内。如超出,平移回到边界内(只平移,不缩放)。
        /// 缩放由 F15 单独处理。
        /// </summary>
        public LayoutRect ClampedTo(LayoutRect bounds)
        {
            double dx = 0, dy = 0;
            if (Left < bounds.Left) dx = bounds.Left - Left;
            else if (Right > bounds.Right) dx = bounds.Right - Right;
            if (Bottom < bounds.Bottom) dy = bounds.Bottom - Bottom;
            else if (Top > bounds.Top) dy = bounds.Top - Top;
            return Translated(dx, dy);
        }

        public override string ToString()
            => $"[{X.ToString("F4", CultureInfo.InvariantCulture)}, {Y.ToString("F4", CultureInfo.InvariantCulture)}, {Width.ToString("F4", CultureInfo.InvariantCulture)} x {Height.ToString("F4", CultureInfo.InvariantCulture)}]";

        public bool Equals(LayoutRect other)
            => X.Equals(other.X) && Y.Equals(other.Y) && Width.Equals(other.Width) && Height.Equals(other.Height);

        public override bool Equals(object obj) => obj is LayoutRect r && Equals(r);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = X.GetHashCode();
                h = (h * 397) ^ Y.GetHashCode();
                h = (h * 397) ^ Width.GetHashCode();
                h = (h * 397) ^ Height.GetHashCode();
                return h;
            }
        }
    }
}