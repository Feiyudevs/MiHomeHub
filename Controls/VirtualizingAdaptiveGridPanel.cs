using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Generators;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace MiHome.Controls;

/// <summary>
/// 自适应网格 + UI 虚拟化：只生成视口内可见的卡片容器，屏幕外设备不进可视树。
/// 布局规则与 AdaptiveGridPanel 一致：按可用宽度自动算列数、行等高、可选 MaxRows。
/// 容器带回收池：滚出视口的卡片隐藏入池复用（不销毁重建），来回拖动滚动条不产生分配churn。
/// </summary>
public sealed class VirtualizingAdaptiveGridPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<VirtualizingAdaptiveGridPanel, double>(nameof(MinItemWidth), 220d);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<VirtualizingAdaptiveGridPanel, double>(nameof(Spacing), 14d);

    public static readonly StyledProperty<int> MaxRowsProperty =
        AvaloniaProperty.Register<VirtualizingAdaptiveGridPanel, int>(nameof(MaxRows), 0);

    /// <summary>单个子项的最小宽度，决定最多能排几列。</summary>
    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>列间距与行间距。</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>最多显示几行，0 表示不限。</summary>
    public int MaxRows
    {
        get => GetValue(MaxRowsProperty);
        set => SetValue(MaxRowsProperty, value);
    }

    /// <summary>回收池每 key 上限：超过则真销毁，避免滚完整表后隐藏容器无限累积。</summary>
    private const int MaxPooledPerKey = 64;

    /// <summary>视口外缓冲（视口高度比例）：减少来回拖动时的生成/回收抖动。</summary>
    private const double BufferFraction = 0.5;

    /// <summary>标记「项自身即容器」的哨兵 key。</summary>
    private static readonly object OwnContainerKey = new();

    static VirtualizingAdaptiveGridPanel()
    {
        AffectsMeasure<VirtualizingAdaptiveGridPanel>(MinItemWidthProperty, SpacingProperty, MaxRowsProperty);
    }

    private Rect _viewport;
    private readonly Dictionary<int, Control> _realized = new();
    private readonly Dictionary<Control, object?> _keys = new();
    private readonly Dictionary<object, Stack<Control>> _pool = new();
    private int _columns;
    private double _itemWidth;
    private double _rowHeight;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;
        InvalidateMeasure();
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        // 集合变化：全部丢弃（含回收池）并重新布局；设备刷新不频繁，重建成本可接受
        ClearAll();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var gen = ItemContainerGenerator;
        var items = Items;

        if (gen is null || items.Count == 0)
        {
            ClearAll();
            return default;
        }

        double width = !double.IsInfinity(availableSize.Width)
            ? availableSize.Width
            : (Bounds.Width > 0 ? Bounds.Width : 0);

        if (width <= 0)
            return default;

        var layout = BuildLayout(width, items.Count);
        int rows = MaxRows > 0 ? Math.Min(layout.Rows, MaxRows) : layout.Rows;

        var vp = _viewport;
        bool hasVp = vp is { Height: > 0 };
        double vpTop = hasVp ? vp.Top : 0;
        double vpBottom = hasVp
            ? vp.Bottom
            : (double.IsInfinity(availableSize.Height) ? vpTop + 400 : vpTop + availableSize.Height);

        // 先用缓存行高（或 90 估算）算可见窗口，实测后再校正
        double stride = (_rowHeight > 0 ? _rowHeight : 90) + Spacing;
        double buffer = hasVp ? vp.Height * BufferFraction : 0;
        double top = Math.Max(0, vpTop - buffer);
        double bottom = vpBottom + buffer;
        if (bottom < top)
            bottom = top;

        int firstRow = Math.Max(0, (int)(top / stride));
        int lastRow = (int)(bottom / stride);
        if (lastRow > rows - 1)
            lastRow = rows - 1;
        if (lastRow < firstRow)
            lastRow = firstRow;

        int startIndex = firstRow * layout.Columns;
        int endIndex = Math.Min(items.Count, (lastRow + 1) * layout.Columns);

        var keep = new HashSet<int>();
        double maxH = 0;

        // 首次没有行高时确保 realize 首行实测
        if (_rowHeight <= 0)
        {
            var c0 = GetOrCreate(gen, items, 0);
            c0.Measure(new Size(layout.ItemWidth, double.PositiveInfinity));
            if (c0.DesiredSize.Height > maxH)
                maxH = c0.DesiredSize.Height;
            keep.Add(0);
            if (startIndex > 0)
                startIndex = 0;
        }

        for (int i = startIndex; i < endIndex; i++)
        {
            var c = GetOrCreate(gen, items, i);
            // 必须显式测量：否则 Arrange 时 Avalonia 会隐式补测，来回滚动变成 measure/arrange 风暴
            c.Measure(new Size(layout.ItemWidth, double.PositiveInfinity));
            if (c.DesiredSize.Height > maxH)
                maxH = c.DesiredSize.Height;
            keep.Add(i);
        }

        if (maxH > 0)
            _rowHeight = maxH;

        // 窗口之外的已实现容器 → 入回收池（隐藏复用）
        var indexSnapshot = new List<int>(_realized.Keys);
        foreach (var idx in indexSnapshot)
            if (!keep.Contains(idx))
                Unrealize(gen, idx);

        _columns = layout.Columns;
        _itemWidth = layout.ItemWidth;

        double totalHeight = rows > 0 ? rows * _rowHeight + (rows - 1) * Spacing : 0;
        double reportWidth = !double.IsInfinity(availableSize.Width) ? availableSize.Width : width;
        return new Size(reportWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double stride = _rowHeight + Spacing;
        int columns = _columns > 0 ? _columns : 1;
        foreach (var kv in _realized)
        {
            int index = kv.Key;
            var c = kv.Value;
            int row = index / columns;
            int col = index % columns;
            double x = col * (_itemWidth + Spacing);
            double y = row * stride;
            double h = _rowHeight > 0 ? _rowHeight : c.DesiredSize.Height;
            c.Arrange(new Rect(x, y, _itemWidth, h));
        }

        return finalSize;
    }

    private Control GetOrCreate(ItemContainerGenerator gen, IReadOnlyList<object?> items, int index)
    {
        if (_realized.TryGetValue(index, out var existing))
            return existing;

        var item = items[index];
        Control container;

        if (gen.NeedsContainer(item, index, out var recycleKey))
        {
            if (recycleKey is not null && _pool.TryGetValue(recycleKey, out var stack) && stack.Count > 0)
            {
                // 从回收池复用：重新绑数据即可，不新建可视树
                container = stack.Pop();
                container.SetCurrentValue(Visual.IsVisibleProperty, true);
            }
            else
            {
                container = gen.CreateContainer(item, index, recycleKey);
                AddInternalChild(container);
            }

            gen.PrepareItemContainer(container, item, index);
            gen.ItemContainerPrepared(container, item, index);
            _keys[container] = recycleKey;
        }
        else
        {
            container = (Control)item!;
            if (!_keys.ContainsKey(container))
            {
                gen.PrepareItemContainer(container, container, index);
                AddInternalChild(container);
                gen.ItemContainerPrepared(container, item, index);
                _keys[container] = OwnContainerKey;
            }
            container.SetCurrentValue(Visual.IsVisibleProperty, true);
        }

        _realized[index] = container;
        return container;
    }

    private void Unrealize(ItemContainerGenerator gen, int index)
    {
        if (!_realized.TryGetValue(index, out var c))
            return;
        _realized.Remove(index);
        if (!_keys.TryGetValue(c, out var key))
            return;

        if (ReferenceEquals(key, OwnContainerKey))
        {
            c.SetCurrentValue(Visual.IsVisibleProperty, false);
            return;
        }

        gen.ClearItemContainer(c);
        c.SetCurrentValue(Visual.IsVisibleProperty, false);

        if (key is null)
        {
            // 无回收 key 的容器只能销毁
            RemoveInternalChild(c);
            _keys.Remove(c);
            return;
        }

        if (!_pool.TryGetValue(key, out var pool))
            _pool[key] = pool = new Stack<Control>();

        if (pool.Count >= MaxPooledPerKey)
        {
            // 池满：真销毁，防隐藏容器无限累积
            RemoveInternalChild(c);
            _keys.Remove(c);
        }
        else
        {
            pool.Push(c);
        }
    }

    private void ClearAll()
    {
        if (_keys.Count == 0 && _realized.Count == 0)
            return;

        var gen = ItemContainerGenerator;
        foreach (var c in new List<Control>(_keys.Keys))
            gen?.ClearItemContainer(c);

        foreach (var c in new List<Control>(Children))
            RemoveInternalChild(c);

        _realized.Clear();
        _pool.Clear();
        _keys.Clear();
    }

    private readonly record struct LayoutInfo(int Columns, int Rows, double ItemWidth);

    private LayoutInfo BuildLayout(double availableWidth, int count)
    {
        var spacing = Math.Max(0, Spacing);
        var minItemWidth = Math.Max(1, MinItemWidth);

        if (double.IsInfinity(availableWidth))
            return new LayoutInfo(1, count, minItemWidth);

        var maxColumns = (int)Math.Floor((availableWidth + spacing) / (minItemWidth + spacing));
        var columns = Math.Max(1, maxColumns);
        var itemWidth = Math.Max(1, (availableWidth - spacing * (columns - 1)) / columns);
        var rows = (int)Math.Ceiling(count / (double)columns);
        return new LayoutInfo(columns, rows, itemWidth);
    }

    // ---- INavigableContainer 抽象成员实现 ----

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap) => null;

    protected override Control? ScrollIntoView(int index)
    {
        if (_columns > 0 && _rowHeight > 0)
        {
            int row = index / _columns;
            double y = row * (_rowHeight + Spacing);
            var sv = this.FindAncestorOfType<ScrollViewer>();
            if (sv is not null)
                sv.Offset = new Vector(sv.Offset.X, y);
        }

        return ContainerFromIndex(index);
    }

    protected override Control? ContainerFromIndex(int index) =>
        _realized.TryGetValue(index, out var c) ? c : null;

    protected override int IndexFromContainer(Control container)
    {
        foreach (var kv in _realized)
            if (kv.Value == container)
                return kv.Key;
        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() => _realized.Values;
}
