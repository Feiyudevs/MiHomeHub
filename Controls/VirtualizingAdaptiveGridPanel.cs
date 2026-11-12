// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

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
/// 等高卡片的自适应虚拟化网格。离屏容器先解除数据绑定，再进入按回收 key 区分的有界池；
/// 同一索引仍在视口内时保留原容器，滚动进入的新索引优先使用池中的容器。
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

    // 首页每个房间各有一个面板：限制离屏留存，同时允许滚动一行时直接复用。
    private const int MinPoolLimit = 2;
    private const int MaxPoolLimit = 64;
    private const double EstimatedRowHeight = 90;
    private const double BufferFraction = 0.5;
    private static readonly object ItemIsOwnContainer = new();

    private Rect _viewport;
    private bool _hasViewport;
    private readonly Dictionary<int, Control> _realized = new();
    private readonly Dictionary<object, Stack<Control>> _recyclePool = new();

    // null 是「已生成但不可回收」；ItemIsOwnContainer 才表示无需生成容器的 Control 项。
    private readonly Dictionary<Control, object?> _keys = new();
    private int _pooledCount;
    private int _poolLimit = MinPoolLimit;
    private int _columns;
    private double _itemWidth;
    private double _rowHeight;
    private double _heightMeasureWidth;

    static VirtualizingAdaptiveGridPanel()
    {
        AffectsMeasure<VirtualizingAdaptiveGridPanel>(MinItemWidthProperty, SpacingProperty, MaxRowsProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        _hasViewport = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (_hasViewport && e.EffectiveViewport == _viewport)
            return;

        _hasViewport = true;
        _viewport = e.EffectiveViewport;
        InvalidateMeasure();
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        // 集合变化后索引与数据的对应关系失效；池中模板也可能已被替换。
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

        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : Bounds.Width;
        if (width <= 0)
            return default;

        var layout = BuildLayout(width, items.Count);
        int rows = MaxRows > 0 ? Math.Min(layout.Rows, MaxRows) : layout.Rows;
        var range = GetVisibleRange(layout.Columns, rows, items.Count);
        SetPoolLimit(range.End - range.Start);

        // 先回收离屏容器，随后进入视口的项目可在本次测量中直接复用它们。
        RecycleOutside(gen, range);

        bool needsHeight = _rowHeight <= 0 || Math.Abs(layout.ItemWidth - _heightMeasureWidth) > 0.5;
        if (needsHeight && (_rowHeight <= 0 || range.Start < range.End || !_hasViewport))
        {
            // 只测一个代表项；调整窗口宽度时不再生成首项到当前滚动位置的全部容器。
            int probeIndex = range.Start < range.End ? range.Start : 0;
            var probe = GetOrCreate(gen, items, probeIndex);
            probe.Measure(new Size(layout.ItemWidth, double.PositiveInfinity));
            _rowHeight = probe.DesiredSize.Height > 0 ? probe.DesiredSize.Height : EstimatedRowHeight;
            _heightMeasureWidth = layout.ItemWidth;
            range = GetVisibleRange(layout.Columns, rows, items.Count);
            SetPoolLimit(range.End - range.Start);
            RecycleOutside(gen, range);
        }

        double maxHeight = _rowHeight;
        for (int i = range.Start; i < range.End; i++)
        {
            var container = GetOrCreate(gen, items, i);
            container.Measure(new Size(layout.ItemWidth, double.PositiveInfinity));
            maxHeight = Math.Max(maxHeight, container.DesiredSize.Height);
        }

        if (maxHeight > _rowHeight)
        {
            _rowHeight = maxHeight;
            // 新高度可能改变起始索引，按修正后的范围补齐并回收。
            range = GetVisibleRange(layout.Columns, rows, items.Count);
            RecycleOutside(gen, range);
            for (int i = range.Start; i < range.End; i++)
                GetOrCreate(gen, items, i).Measure(new Size(layout.ItemWidth, double.PositiveInfinity));
        }

        _columns = layout.Columns;
        _itemWidth = layout.ItemWidth;
        return new Size(width, GetTotalHeight(rows));
    }

    private (int Start, int End) GetVisibleRange(int columns, int rows, int count)
    {
        // 初次布局只需测一个代表项来估计总高度；待框架给出有效视口后再填充。
        if (!_hasViewport || _viewport.Width <= 0 || _viewport.Height <= 0)
            return (0, 0);

        double height = _rowHeight > 0 ? _rowHeight : EstimatedRowHeight;
        double spacing = Math.Max(0, Spacing);
        double stride = height + spacing;
        double buffer = _viewport.Height * BufferFraction;
        double top = _viewport.Top - buffer;
        double bottom = _viewport.Bottom + buffer;
        double totalHeight = rows * height + Math.Max(0, rows - 1) * spacing;

        // 不相交的分组不能通过钳制索引被误判为第一行可见。
        if (bottom <= 0 || top >= totalHeight)
            return (0, 0);

        int firstRow = (int)Math.Clamp(Math.Floor(Math.Max(0, top) / stride), 0, rows);
        int endRow = (int)Math.Clamp(Math.Ceiling(Math.Min(totalHeight, bottom) / stride), firstRow, rows);
        return (Math.Min(count, firstRow * columns), Math.Min(count, endRow * columns));
    }

    private double GetTotalHeight(int rows) =>
        rows > 0 ? rows * (_rowHeight > 0 ? _rowHeight : EstimatedRowHeight)
            + (rows - 1) * Math.Max(0, Spacing) : 0;

    protected override Size ArrangeOverride(Size finalSize)
    {
        double spacing = Math.Max(0, Spacing);
        double stride = _rowHeight + spacing;
        int columns = Math.Max(1, _columns);
        foreach (var (index, container) in _realized)
        {
            int row = index / columns;
            int col = index % columns;
            container.Arrange(new Rect(col * (_itemWidth + spacing), row * stride,
                _itemWidth, _rowHeight > 0 ? _rowHeight : container.DesiredSize.Height));
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
            if (recycleKey is not null && _recyclePool.TryGetValue(recycleKey, out var pool)
                && pool.Count > 0)
            {
                container = pool.Pop();
                _pooledCount--;
                if (pool.Count == 0)
                    _recyclePool.Remove(recycleKey);
                container.SetCurrentValue(Visual.IsVisibleProperty, true);
                gen.PrepareItemContainer(container, item, index);
            }
            else
            {
                container = gen.CreateContainer(item, index, recycleKey);
                gen.PrepareItemContainer(container, item, index);
                AddInternalChild(container);
                _keys[container] = recycleKey;
            }
            _realized[index] = container;
            gen.ItemContainerPrepared(container, item, index);
        }
        else
        {
            container = (Control)item!;
            if (!_keys.ContainsKey(container))
            {
                gen.PrepareItemContainer(container, item, index);
                AddInternalChild(container);
                _keys[container] = ItemIsOwnContainer;
                _realized[index] = container;
                gen.ItemContainerPrepared(container, item, index);
            }
            container.SetCurrentValue(Visual.IsVisibleProperty, true);
            _realized[index] = container;
        }
        return container;
    }

    private void RecycleOutside(ItemContainerGenerator gen, (int Start, int End) range)
    {
        foreach (int index in new List<int>(_realized.Keys))
            if (index < range.Start || index >= range.End)
                Recycle(gen, index);
    }

    private void Recycle(ItemContainerGenerator gen, int index)
    {
        if (!_realized.Remove(index, out var container))
            return;

        var key = _keys[container];
        if (ReferenceEquals(key, ItemIsOwnContainer))
        {
            // Control 项保持已准备状态，不能 Clear 或作为其他项目的容器复用。
            container.SetCurrentValue(Visual.IsVisibleProperty, false);
            return;
        }

        // null recycle key 只表示不能入池，并不免除 ClearItemContainer。
        gen.ClearItemContainer(container);
        if (key is not null && _pooledCount < _poolLimit)
        {
            container.SetCurrentValue(Visual.IsVisibleProperty, false);
            if (!_recyclePool.TryGetValue(key, out var pool))
                _recyclePool[key] = pool = new Stack<Control>();
            pool.Push(container);
            _pooledCount++;
        }
        else
        {
            RemoveInternalChild(container);
            _keys.Remove(container);
        }
    }

    private void SetPoolLimit(int visibleCount)
    {
        _poolLimit = Math.Clamp(visibleCount, MinPoolLimit, MaxPoolLimit);
        if (_pooledCount <= _poolLimit)
            return;

        foreach (var key in new List<object>(_recyclePool.Keys))
        {
            var pool = _recyclePool[key];
            while (pool.Count > 0 && _pooledCount > _poolLimit)
            {
                var container = pool.Pop();
                _pooledCount--;
                RemoveInternalChild(container);
                _keys.Remove(container);
            }
            if (pool.Count == 0)
                _recyclePool.Remove(key);
            if (_pooledCount <= _poolLimit)
                break;
        }
    }

    private void ClearAll()
    {
        var gen = ItemContainerGenerator;
        var realized = new List<Control>(_realized.Values);
        _realized.Clear();
        foreach (var container in realized)
            if (!ReferenceEquals(_keys[container], ItemIsOwnContainer))
                gen?.ClearItemContainer(container);

        // 池中容器已经 Clear；Control 项不调用 ClearItemContainer。
        foreach (var container in new List<Control>(Children))
            RemoveInternalChild(container);
        _recyclePool.Clear();
        _keys.Clear();
        _pooledCount = 0;
        _rowHeight = 0;
        _heightMeasureWidth = 0;
    }

    private readonly record struct LayoutInfo(int Columns, int Rows, double ItemWidth);

    private LayoutInfo BuildLayout(double availableWidth, int count)
    {
        double spacing = Math.Max(0, Spacing);
        double minItemWidth = Math.Max(1, MinItemWidth);
        int columns = Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (minItemWidth + spacing)));
        double itemWidth = Math.Max(1, (availableWidth - spacing * (columns - 1)) / columns);
        int rows = (int)Math.Ceiling(count / (double)columns);
        return new LayoutInfo(columns, rows, itemWidth);
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap) => null;

    protected override Control? ScrollIntoView(int index)
    {
        if (_columns > 0 && _rowHeight > 0)
        {
            int row = index / _columns;
            var sv = this.FindAncestorOfType<ScrollViewer>();
            if (sv is not null)
                sv.Offset = new Vector(sv.Offset.X, row * (_rowHeight + Math.Max(0, Spacing)));
        }
        return ContainerFromIndex(index);
    }

    protected override Control? ContainerFromIndex(int index) =>
        _realized.TryGetValue(index, out var container) ? container : null;

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, realized) in _realized)
            if (ReferenceEquals(realized, container))
                return index;
        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() => _realized.Values;
}
