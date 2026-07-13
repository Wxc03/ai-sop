using System;

namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.1 — 元素种类,用于决定默认优先级 / 可移动性 / 可缩放性。
    /// </summary>
    public enum LayoutElementKind
    {
        Unknown = 0,
        /// <summary>SW Drawing View(爆炸等轴测、装配原始等轴测等)。</summary>
        View = 1,
        /// <summary>零件球标 BalloonAnnotation。</summary>
        Balloon = 2,
        /// <summary>明细表 BOM。</summary>
        BomTable = 3,
    }

    /// <summary>
    /// W5.1 — 参与布局的元素包装。
    /// ComRef 持有原 COM RCW(View / BalloonAnnotation / BomTableAnnotation)用于回写。
    /// Rect 是可变的(碰撞避让时会平移),ComRef 不变。
    /// Priority:越大越优先移动(默认 BomTable=0 不动, View=5, Balloon=10)。
    /// </summary>
    public sealed class LayoutElement
    {
        public string Label { get; }
        public LayoutElementKind Kind { get; }
        public LayoutRect Rect { get; set; }
        public object ComRef { get; }
        public bool Movable { get; }
        public bool Scalable { get; }
        public double Priority { get; }

        public LayoutElement(
            string label,
            LayoutElementKind kind,
            LayoutRect rect,
            object comRef,
            bool movable,
            bool scalable,
            double priority)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("label 不能为空", nameof(label));
            Label = label;
            Kind = kind;
            Rect = rect;
            ComRef = comRef;
            Movable = movable;
            Scalable = scalable;
            Priority = priority;
        }

        /// <summary>
        /// 工厂:按 kind 用一组保守默认值。
        /// BomTable 不可移动(右下角锚定);View 可移动可缩放;Balloon 仅可移动。
        /// </summary>
        public static LayoutElement WithDefaults(
            string label,
            LayoutElementKind kind,
            LayoutRect rect,
            object comRef)
        {
            switch (kind)
            {
                case LayoutElementKind.BomTable:
                    return new LayoutElement(label, kind, rect, comRef,
                        movable: false, scalable: false, priority: 0);
                case LayoutElementKind.View:
                    return new LayoutElement(label, kind, rect, comRef,
                        movable: true, scalable: true, priority: 5);
                case LayoutElementKind.Balloon:
                    return new LayoutElement(label, kind, rect, comRef,
                        movable: true, scalable: false, priority: 10);
                default:
                    return new LayoutElement(label, kind, rect, comRef,
                        movable: true, scalable: false, priority: 1);
            }
        }

        public override string ToString()
            => $"{Kind}[{Label}]@{Rect} prio={Priority} movable={Movable} scalable={Scalable}";
    }
}