using System.Collections.Generic;
using System.Linq;

namespace SwSopAddin.Layout
{
    /// <summary>
    /// W5.1 — 一对碰撞元素(a, b)。b 总是非 null,a 总是非 null。
    /// </summary>
    public readonly struct ElementPair
    {
        public LayoutElement A { get; }
        public LayoutElement B { get; }
        public ElementPair(LayoutElement a, LayoutElement b) { A = a; B = b; }
        public override string ToString() => $"({A?.Label ?? "?"} <-> {B?.Label ?? "?"})";
    }

    /// <summary>
    /// W5.1 — 矩形碰撞检测器。纯几何逻辑,无 COM 依赖,便于单元测试。
    /// O(n²) 全配对扫描;n 典型 = 视图(1-3) + 球标(0-30) + BOM(1),不会超过 50,够用。
    /// 后续 F14+ 如果球标超 200 再上 R-tree 或 sweep-line。
    /// </summary>
    public class CollisionDetector
    {
        /// <summary>
        /// 找出所有碰撞对。tolerance > 0 把元素视作 padding 之后的胖框,用于"保持最小间距"。
        /// </summary>
        public IReadOnlyList<ElementPair> Detect(IEnumerable<LayoutElement> elements, double tolerance = 0)
        {
            var list = elements?.Where(e => e != null && !e.Rect.IsEmpty).ToList()
                       ?? new List<LayoutElement>();
            var result = new List<ElementPair>();
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (LayoutRect.Overlaps(list[i].Rect, list[j].Rect, tolerance))
                    {
                        result.Add(new ElementPair(list[i], list[j]));
                    }
                }
            }
            return result;
        }

        // W6-fix:AnyOutOfBounds 暂禁用 — SW 2024 interop 缺 GetSheetWidth/Height,
        // sheet bounds 推算不准,AnyOutOfBounds 没意义。F16 分页触发条件另写。
        // public bool AnyOutOfBounds(IEnumerable<LayoutElement> elements, SheetBounds sheet) { ... }
    }
}