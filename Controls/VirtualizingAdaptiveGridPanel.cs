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
///
/// 关键设计（v3）：容器按「设备索引」缓存，而不是按类型回收池复用。
/// 滚出视口的容器只隐藏、保留其数据绑定；滚回来时同一个索引拿回同一张卡片，
/// 不做 ClearItemContainer / PrepareItemContainer，因此不会重建文本布局。
/// 这一点很重要：反复 rebind 会让 Skia 的字体对象被反复创建（框架侧只增不减），
/// 来回拖动滚动条时内存持续上涨；按索引稳定复用后，滚动不再产生新的文本排版。
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

    /// <summary>缓存容器上限（相对可见容器数的倍数）：超出后按最久未访问淘汰，防常驻无上限。</summary>
    /// <remarks>
    /// 实测（真实进程 dotnet-dump + 工作集采样）：每个「隐藏但保留」的卡片容器仍挂着合成层与离屏纹理，
    /// 单张约 2~3MB 原生内存。原值 2.5 倍 / 硬上限 160 张，在设备多时会让进程稳定在 800MB 以上；
    /// 压到 1.25 倍 / 硬上限 48 张后，滚动仍能从缓存直接复用（不回退到重建），但常驻上限大幅下降。
    /// </remarks>
    private const double CacheRatio = 1.25;

    /// <summary>缓存硬上限，避免超大列表滚一圈后卡片全驻留。</summary>
    private const int MaxCacheHard = 48;

    /// <summary>视口外缓冲（视口高度比例）：减少来回拖动时的边界抖动。</summary>
    private const double BufferFraction = 0.5;

    static VirtualizingAdaptiveGridPanel()
    {
        AffectsMeasure<VirtualizingAdaptiveGridPanel>(MinItemWidthProperty, SpacingProperty, MaxRowsProperty);
    }

    private Rect _viewport;

    /// <summary>当前在可视树里可见（已排布）的容器：索引 → 容器。</summary>
    private readonly Dictionary<int, Control> _realized = new();

    /// <summary>隐藏的容器：索引 → 容器。保留数据绑定，滚回来直接显示，不重新准备。</summary>
    private readonly Dictionary<int, Control> _hidden = new();

    /// <summary>LRU：队首最久未用，用于淘汰。</summary>
    private readonly LinkedList<int> _lru = new();

    /// <summary>该容器对应的回收 key（用于销毁前的清理），null 表示项自身即容器。</summary>
    private readonly Dictionary<Control, object?> _keys = new();

    private int _columns;
    private double _itemWidth;
    private double _rowHeight;
    private double _layoutWidth;

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
        // 视口没实际变化就别再触发测量，避免「布局 → 视口变化 → 再布局」的自激
        if (e.EffectiveViewport == _viewport)
            return;

        _viewport = e.EffectiveViewport;
        InvalidateMeasure();
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        // 集合变化：索引与数据的对应关系失效，全部丢弃并重新布局
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

        // 列数或宽度变了：行高缓存作废（卡片宽度变了，高度可能跟着变）
        if (Math.Abs(width - _layoutWidth) > 0.5)
        {
            _layoutWidth = width;
            _rowHeight = 0;
        }

        var vp = _viewport;
        bool hasVp = vp is { Height: > 0 };
        double vpTop = hasVp ? vp.Top : 0;
        double vpBottom = hasVp
            ? vp.Bottom
            : (double.IsInfinity(availableSize.Height) ? vpTop + 400 : vpTop + availableSize.Height);

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

        // 首行实测：首次没有行高时先量一张，保证 stride 准确
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

        // 窗口外的可见容器 → 隐藏进缓存（不清绑定，滚回来零成本）
        if (_realized.Count > keep.Count)
        {
            var indexSnapshot = new List<int>(_realized.Keys);
            foreach (var idx in indexSnapshot)
                if (!keep.Contains(idx))
                    Hide(gen, idx);
        }

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

        // 这个索引之前生成过：直接显示原容器，数据绑定原样保留（关键：不 rebind）
        if (_hidden.Remove(index, out var cached))
        {
            Touch(index);
            cached.SetCurrentValue(Visual.IsVisibleProperty, true);
            _realized[index] = cached;
            return cached;
        }

        var item = items[index];
        Control container;

        if (gen.NeedsContainer(item, index, out var recycleKey))
        {
            container = gen.CreateContainer(item, index, recycleKey);
            AddInternalChild(container);
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
                _keys[container] = null;
            }
            container.SetCurrentValue(Visual.IsVisibleProperty, true);
        }

        _realized[index] = container;
        Touch(index);
        TrimCache(gen);
        return container;
    }

    /// <summary>可见 → 隐藏缓存。保留绑定，只切 IsVisible。</summary>
    private void Hide(ItemContainerGenerator gen, int index)
    {
        if (!_realized.Remove(index, out var c))
            return;

        if (!_keys.TryGetValue(c, out var key))
            return;

        if (key is null && ReferenceEquals(c, Items.Count > index ? Items[index] : null))
        {
            // 项自身即容器：不能回收，只能隐藏
            c.SetCurrentValue(Visual.IsVisibleProperty, false);
            return;
        }

        c.SetCurrentValue(Visual.IsVisibleProperty, false);
        _hidden[index] = c;
        Touch(index);
        TrimCache(gen);
    }

    /// <summary>超出上限时按 LRU 淘汰：真正解除绑定并移出可视树。</summary>
    private void TrimCache(ItemContainerGenerator gen)
    {
        int visible = _realized.Count;
        int limit = Math.Min(MaxCacheHard, (int)(Math.Max(visible, 8) * CacheRatio) + 16);

        while (_hidden.Count > limit && _lru.First is { } node)
        {
            int idx = node.Value;
            _lru.RemoveFirst();
            if (!_hidden.Remove(idx, out var c))
                continue; // 该索引已在可见集合，跳过

            if (_keys.TryGetValue(c, out var key) && key is not null)
                gen.ClearItemContainer(c);

            RemoveInternalChild(c);
            _keys.Remove(c);
        }
    }

    private void Touch(int index)
    {
        var node = _lru.Find(index);
        if (node is not null)
            _lru.Remove(node);
        _lru.AddLast(index);
    }

    private void ClearAll()
    {
        if (_keys.Count == 0 && _realized.Count == 0 && _hidden.Count == 0)
            return;

        var gen = ItemContainerGenerator;
        foreach (var c in new List<Control>(_keys.Keys))
            gen?.ClearItemContainer(c);

        foreach (var c in new List<Control>(Children))
            RemoveInternalChild(c);

        _realized.Clear();
        _hidden.Clear();
        _lru.Clear();
        _keys.Clear();
        _rowHeight = 0;
        _layoutWidth = 0;
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
