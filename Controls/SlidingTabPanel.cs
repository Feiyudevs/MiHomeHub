// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls;

namespace MiHome.Controls;

/// <summary>
/// 横向 Tab 容器：把选中底色做成一整块共享滑块，SelectedIndex 变化时从 A 位移到 B 并同步变宽。
/// </summary>
public class SlidingTabPanel : Panel
{
    private readonly Canvas _layer = new() { IsHitTestVisible = false };
    private readonly Border _indicator = new() { Classes = { "TabSlider" }, IsVisible = false };
    private bool _indicatorReady;

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<SlidingTabPanel, int>(nameof(SelectedIndex), 0);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SlidingTabPanel, double>(nameof(Spacing), 4);

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public SlidingTabPanel()
    {
        Children.Add(_layer);
        _layer.Children.Add(_indicator);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == SelectedIndexProperty || e.Property == SpacingProperty)
            InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0d;
        var height = 0d;
        var count = 0;

        foreach (var child in Children)
        {
            if (child == _layer || !child.IsVisible) continue;
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            var desired = child.DesiredSize;
            width += desired.Width;
            height = Math.Max(height, desired.Height);
            count++;
        }

        if (count > 1) width += Spacing * (count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _layer.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));

        var visible = new List<Control>();
        var x = 0d;
        foreach (var child in Children)
        {
            if (child == _layer || !child.IsVisible) continue;
            var desired = child.DesiredSize;
            child.Arrange(new Rect(x, 0, desired.Width, desired.Height));
            x += desired.Width + Spacing;
            visible.Add(child);
        }

        UpdateIndicator(visible);
        return finalSize;
    }

    private void UpdateIndicator(List<Control> tabs)
    {
        if (SelectedIndex < 0 || SelectedIndex >= tabs.Count)
        {
            _indicator.IsVisible = false;
            return;
        }

        var target = tabs[SelectedIndex];
        if (target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
        {
            _indicator.IsVisible = false;
            return;
        }

        Canvas.SetLeft(_indicator, target.Bounds.Left);
        Canvas.SetTop(_indicator, target.Bounds.Top);
        _indicator.Width = target.Bounds.Width;
        _indicator.Height = target.Bounds.Height;

        if (_indicatorReady) return;
        _indicator.IsVisible = true;
        _indicator.Classes.Add("ready");
        _indicatorReady = true;
    }
}
