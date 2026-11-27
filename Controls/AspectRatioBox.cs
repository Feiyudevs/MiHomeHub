// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;

namespace MiHomeHub.Controls;

/// <summary>
/// 按宽度算高度的容器：宽度取父级给的空间，高度按比例推出，画面就不会在上下留黑边。
/// 摄像头预览、放大窗用它；不传 Ratio 时按 16:9。
/// </summary>
public sealed class AspectRatioBox : Panel
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectRatioBox, double>(nameof(Ratio), 16.0 / 9.0);

    /// <summary>宽 / 高。NaN 或非正数时退到 16:9。</summary>
    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var ratio = double.IsNaN(Ratio) || Ratio <= 0 ? 16.0 / 9.0 : Ratio;
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        if (width <= 0) width = 640;
        var height = width / ratio;

        // 高度不能被父级裁掉一半：有上限时收紧宽度，仍保持比例
        if (!double.IsInfinity(availableSize.Height) && availableSize.Height > 0 && height > availableSize.Height)
        {
            height = availableSize.Height;
            width = height * ratio;
        }

        var size = new Size(width, height);
        foreach (var child in Children)
        {
            child.Measure(size);
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.Arrange(new Rect(finalSize));
        }
        return finalSize;
    }

    static AspectRatioBox()
    {
        AffectsMeasure<AspectRatioBox>(RatioProperty);
        ClipToBoundsProperty.OverrideDefaultValue<AspectRatioBox>(true);
    }
}
