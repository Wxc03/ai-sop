using System;

namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.1 — 图纸边界(米,Y 轴向上)。
    /// 注意 SW 图纸原点是左下角,所以 X=[0, Width], Y=[0, Height]。
    /// </summary>
    public readonly struct SheetBounds
    {
        public double Width { get; }
        public double Height { get; }

        public SheetBounds(double widthMeters, double heightMeters)
        {
            if (widthMeters <= 0)
                throw new ArgumentException($"图纸宽度必须 > 0: {widthMeters}", nameof(widthMeters));
            if (heightMeters <= 0)
                throw new ArgumentException($"图纸高度必须 > 0: {heightMeters}", nameof(heightMeters));
            Width = widthMeters;
            Height = heightMeters;
        }

        /// <summary>A3 图纸 = 420mm x 297mm。</summary>
        public static SheetBounds A3 => new SheetBounds(0.420, 0.297);

        /// <summary>A4 图纸 = 297mm x 210mm(横放还是竖放由 caller 决定)。</summary>
        public static SheetBounds A4Landscape => new SheetBounds(0.297, 0.210);

        public LayoutRect AsRect => new LayoutRect(0, 0, Width, Height);

        public bool Contains(LayoutRect rect)
            => rect.Left >= -1e-9 && rect.Right <= Width + 1e-9
            && rect.Bottom >= -1e-9 && rect.Top <= Height + 1e-9;

        public override string ToString()
            => $"Sheet[{Width}x{Height}]";
    }
}