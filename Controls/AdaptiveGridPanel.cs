// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace MiHomeHub.Controls;

/// <summary>
/// 自适应网格：按可用宽度自动计算列数，子项等宽拉伸排列。
/// 用于设备卡、场景卡等列表，窗口变宽/变窄时自动增减列数而不留空档。
/// 打开 <see cref="BalanceRows"/> 后可把总数均分到各行（如 7 张 → 3/2/2），每行再按行内张数铺满。
/// </summary>
public class AdaptiveGridPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, double>(nameof(MinItemWidth), 220d);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, double>(nameof(Spacing), 14d);

    public static readonly StyledProperty<int> MaxRowsProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, int>(nameof(MaxRows), 0);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, int>(nameof(MaxColumns), 0);

    /// <summary>是否只排可见子项。默认 false：所有子项各占一格，隐藏项留空但不挪动其他卡片。</summary>
    public static readonly StyledProperty<bool> SkipInvisibleProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, bool>(nameof(SkipInvisible), false);

    /// <summary>
    /// 行数均分：卡片不按「满列换行」切，而是把总数尽可能平均地分到每一行
    /// （7 张最多 3 列 → 3/2/2），每行按自己的张数重新计算宽度并铺满整行。
    /// 默认 false，保持原有「统一列宽、末行不拉伸」的宫格行为。
    /// </summary>
    public static readonly StyledProperty<bool> BalanceRowsProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, bool>(nameof(BalanceRows), false);

    /// <summary>只有一张卡时允许的最大宽度，0 表示不限制。避免单卡在整行里被拉得过宽。</summary>
    public static readonly StyledProperty<double> MaxSingleItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, double>(nameof(MaxSingleItemWidth), 0d);

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

    /// <summary>最多显示几行，0 表示不限。用于首页只展示一行、其余进详情页的场景。</summary>
    public int MaxRows
    {
        get => GetValue(MaxRowsProperty);
        set => SetValue(MaxRowsProperty, value);
    }

    /// <summary>最多排几列，0 表示不限。子项数量固定的卡片行用它收住列数，避免宽窗口多出空列。</summary>
    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>只排可见子项：卡片按数据有无隐藏时，剩下的卡片自动补位，不留空档。</summary>
    public bool SkipInvisible
    {
        get => GetValue(SkipInvisibleProperty);
        set => SetValue(SkipInvisibleProperty, value);
    }

    /// <summary>按行均分卡片数量，并让每一行各自铺满整行宽度。</summary>
    public bool BalanceRows
    {
        get => GetValue(BalanceRowsProperty);
        set => SetValue(BalanceRowsProperty, value);
    }

    /// <summary>单张卡片的最大宽度（0 = 不限制）；只在某一行只有一张卡时生效。</summary>
    public double MaxSingleItemWidth
    {
        get => GetValue(MaxSingleItemWidthProperty);
        set => SetValue(MaxSingleItemWidthProperty, value);
    }

    static AdaptiveGridPanel()
    {
        AffectsMeasure<AdaptiveGridPanel>(MinItemWidthProperty, SpacingProperty, MaxRowsProperty,
            MaxColumnsProperty, SkipInvisibleProperty, BalanceRowsProperty, MaxSingleItemWidthProperty);
    }

    /// <summary>被行数上限裁掉的子项统一排到画布外，避免内容画在原点。</summary>
    private static readonly Rect OffScreen = new(-10000, -10000, 0, 0);

    /// <summary>只排可见子项：隐藏的卡片不该继续占一格，否则会留下一块空档。
    /// 子类可以在这里换一种顺序（例如按用户拖动后的卡片顺序）。</summary>
    protected virtual List<Control> VisibleChildren()
    {
        var list = new List<Control>(Children.Count);
        foreach (var child in Children)
            if (!SkipInvisible || child.IsVisible) list.Add(child);
        return list;
    }

    /// <summary>实际排版的行数：自然行数与 MaxRows 取小。</summary>
    private int VisibleRows(int naturalRows) =>
        MaxRows <= 0 ? naturalRows : Math.Min(naturalRows, MaxRows);

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = VisibleChildren();
        var count = items.Count;
        if (count == 0)
            return default;

        var layout = BuildLayout(availableSize.Width, count);
        var rows = VisibleRows(layout.Rows.Count);
        var height = 0d;
        var measuredUntil = 0;

        for (var row = 0; row < rows; row++)
        {
            var rowLayout = layout.Rows[row];
            var rowHeight = 0d;
            for (var offset = 0; offset < rowLayout.Count; offset++)
            {
                var item = items[rowLayout.Start + offset];
                item.Measure(new Size(rowLayout.ItemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, item.DesiredSize.Height);
            }

            height += rowHeight;
            measuredUntil = rowLayout.Start + rowLayout.Count;
        }

        // 超出可见行数的子项按零尺寸测量，避免它们残留旧的期望尺寸
        for (var index = measuredUntil; index < count; index++)
            items[index].Measure(default);

        height += Spacing * (rows - 1);
        var width = double.IsInfinity(availableSize.Width) ? layout.Rows[0].ItemWidth : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var items = VisibleChildren();
        var count = items.Count;
        if (count == 0)
            return finalSize;

        var layout = BuildLayout(finalSize.Width, count);
        var rows = VisibleRows(layout.Rows.Count);
        var y = 0d;
        var arrangedUntil = 0;

        for (var row = 0; row < rows; row++)
        {
            var rowLayout = layout.Rows[row];
            var rowHeight = 0d;
            for (var offset = 0; offset < rowLayout.Count; offset++)
                rowHeight = Math.Max(rowHeight, items[rowLayout.Start + offset].DesiredSize.Height);

            var x = 0d;
            for (var offset = 0; offset < rowLayout.Count; offset++)
            {
                var item = items[rowLayout.Start + offset];
                item.Arrange(new Rect(x, y, rowLayout.ItemWidth, rowHeight));
                x += rowLayout.ItemWidth + Spacing;
            }

            y += rowHeight + Spacing;
            arrangedUntil = rowLayout.Start + rowLayout.Count;
        }

        // 超出可见行数的子项移到画布外：只排成零尺寸矩形还不够，
        // 子控件默认不裁切自身内容，文字/图标仍会画到原点叠在可见卡上
        for (var index = arrangedUntil; index < count; index++)
            items[index].Arrange(OffScreen);

        return finalSize;
    }

    private LayoutInfo BuildLayout(double availableWidth, int count)
    {
        var spacing = Math.Max(0, Spacing);
        var minItemWidth = Math.Max(1, MinItemWidth);

        if (double.IsInfinity(availableWidth))
            return new LayoutInfo(new[] { new RowLayout(0, count, minItemWidth) });

        // 按容器宽度与 MinItemWidth 算出最大列数，再受 MaxColumns 收住
        var maxColumns = (int)Math.Floor((availableWidth + spacing) / (minItemWidth + spacing));
        var columns = Math.Max(1, maxColumns);
        if (MaxColumns > 0)
            columns = Math.Min(columns, MaxColumns);

        // 常规：所有行统一使用该列数与 itemWidth，末行设备较少时不拉伸，
        // 保持与满行相同的卡片宽度。
        if (!BalanceRows)
        {
            var rows = (int)Math.Ceiling(count / (double)columns);
            var itemWidth = Math.Max(1, (availableWidth - spacing * (columns - 1)) / columns);
            var regular = new List<RowLayout>(rows);
            for (var row = 0; row < rows; row++)
            {
                var start = row * columns;
                regular.Add(new RowLayout(start, Math.Min(columns, count - start), itemWidth));
            }
            return new LayoutInfo(regular);
        }

        // 均分：先按最大列数算出行数，再把卡片尽量平均分到每一行
        // （7 张最多 3 列 → 3/2/2），每行按行内实际张数重新算宽度并铺满整行。
        var rowCount = (int)Math.Ceiling(count / (double)columns);
        var baseCount = count / rowCount;
        var extra = count % rowCount;
        var balanced = new List<RowLayout>(rowCount);
        var index = 0;

        for (var row = 0; row < rowCount; row++)
        {
            var rowItems = baseCount + (row < extra ? 1 : 0);
            var itemWidth = Math.Max(1, (availableWidth - spacing * (rowItems - 1)) / rowItems);
            if (rowItems == 1 && MaxSingleItemWidth > 0)
                itemWidth = Math.Min(itemWidth, MaxSingleItemWidth);

            balanced.Add(new RowLayout(index, rowItems, itemWidth));
            index += rowItems;
        }

        return new LayoutInfo(balanced);
    }

    private readonly record struct LayoutInfo(IReadOnlyList<RowLayout> Rows);

    private readonly record struct RowLayout(int Start, int Count, double ItemWidth);
}
