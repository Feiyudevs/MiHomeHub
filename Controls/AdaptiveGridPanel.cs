using System;
using Avalonia;
using Avalonia.Controls;

namespace MiHome.Controls;

/// <summary>
/// 自适应网格：按可用宽度自动计算列数，子项等宽拉伸排列。
/// 用于设备卡、场景卡等列表，窗口变宽/变窄时自动增减列数而不留空档。
/// </summary>
public sealed class AdaptiveGridPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, double>(nameof(MinItemWidth), 220d);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, double>(nameof(Spacing), 14d);

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

    static AdaptiveGridPanel()
    {
        AffectsMeasure<AdaptiveGridPanel>(MinItemWidthProperty, SpacingProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Children.Count;
        if (count == 0)
            return default;

        var layout = BuildLayout(availableSize.Width, count);
        var height = 0d;

        for (var row = 0; row < layout.Rows; row++)
        {
            var rowHeight = 0d;
            for (var index = row * layout.Columns; index < Math.Min(count, (row + 1) * layout.Columns); index++)
            {
                Children[index].Measure(new Size(layout.ItemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, Children[index].DesiredSize.Height);
            }

            height += rowHeight;
        }

        height += Spacing * (layout.Rows - 1);
        var width = double.IsInfinity(availableSize.Width) ? layout.ItemWidth : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count;
        if (count == 0)
            return finalSize;

        var layout = BuildLayout(finalSize.Width, count);
        var y = 0d;

        for (var row = 0; row < layout.Rows; row++)
        {
            var start = row * layout.Columns;
            var end = Math.Min(count, start + layout.Columns);
            var rowHeight = 0d;
            for (var index = start; index < end; index++)
                rowHeight = Math.Max(rowHeight, Children[index].DesiredSize.Height);

            for (var index = start; index < end; index++)
            {
                var col = index - start;
                Children[index].Arrange(new Rect(col * (layout.ItemWidth + Spacing), y, layout.ItemWidth, rowHeight));
            }

            y += rowHeight + Spacing;
        }

        return finalSize;
    }

    private LayoutInfo BuildLayout(double availableWidth, int count)
    {
        var spacing = Math.Max(0, Spacing);
        var minItemWidth = Math.Max(1, MinItemWidth);

        if (double.IsInfinity(availableWidth))
            return new LayoutInfo(count, 1, minItemWidth);

        var columns = (int)Math.Floor((availableWidth + spacing) / (minItemWidth + spacing));
        columns = Math.Clamp(columns, 1, count);

        var itemWidth = Math.Max(1, (availableWidth - spacing * (columns - 1)) / columns);
        var rows = (int)Math.Ceiling(count / (double)columns);
        return new LayoutInfo(columns, rows, itemWidth);
    }

    private readonly record struct LayoutInfo(int Columns, int Rows, double ItemWidth);
}
