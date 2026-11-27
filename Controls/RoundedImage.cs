// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MiHomeHub.Controls;

/// <summary>
/// 铺满圆角容器、并按圆角裁切的图片。自定义图标位大多是一枚带底色的圆角块，
/// 普通 Image 按 Uniform 摆会在四周留出底色；这里按 UniformToFill 取源图中间一块，
/// 再用圆角裁剪画满整个控件，PNG 和 SVG 都能贴到四边，又不会把方角画到圆角块外面。
/// </summary>
public sealed class RoundedImage : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<RoundedImage, IImage?>(nameof(Source));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<RoundedImage, CornerRadius>(nameof(CornerRadius));

    static RoundedImage()
    {
        AffectsRender<RoundedImage>(SourceProperty, CornerRadiusProperty);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var source = Source;
        if (source is null) return;

        var size = source.Size;
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0 || size.Width <= 0 || size.Height <= 0) return;

        // 等比放大到刚好盖住控件，多出来的那一边从中间裁掉，而不是留白
        var sourceWidth = size.Width;
        var sourceHeight = size.Height;
        var boundsAspect = bounds.Width / bounds.Height;
        var sourceAspect = sourceWidth / sourceHeight;
        if (sourceAspect > boundsAspect)
            sourceWidth = sourceHeight * boundsAspect;
        else
            sourceHeight = sourceWidth / boundsAspect;

        var sourceRect = new Rect(
            (size.Width - sourceWidth) / 2,
            (size.Height - sourceHeight) / 2,
            sourceWidth,
            sourceHeight);

        var radius = CornerRadius;
        using (context.PushRenderOptions(new RenderOptions
               {
                   BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality,
               }))
        {
            if (radius.TopLeft <= 0 && radius.TopRight <= 0 && radius.BottomRight <= 0 && radius.BottomLeft <= 0)
            {
                context.DrawImage(source, sourceRect, bounds);
                return;
            }

            using (context.PushClip(new RoundedRect(
                       bounds, radius.TopLeft, radius.TopRight, radius.BottomRight, radius.BottomLeft)))
            {
                context.DrawImage(source, sourceRect, bounds);
            }
        }
    }
}
