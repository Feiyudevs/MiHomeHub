// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MiHomeHub.Controls;

/// <summary>
/// 摄像头画面：自己画位图，按 FrameVersion 刷新。
///
/// 用它而不是 Image，是因为同一个 WriteableBitmap 实例反复赋值不会让 Image 重绘，
/// 每帧新建位图又太费；这里每加一个版本号就主动 InvalidateVisual，画面才会真的动。
/// </summary>
public class CameraFrameView : Control
{
    public static readonly StyledProperty<IImage?> FrameProperty =
        AvaloniaProperty.Register<CameraFrameView, IImage?>(nameof(Frame));

    public static readonly StyledProperty<int> FrameVersionProperty =
        AvaloniaProperty.Register<CameraFrameView, int>(nameof(FrameVersion));

    public IImage? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public int FrameVersion
    {
        get => GetValue(FrameVersionProperty);
        set => SetValue(FrameVersionProperty, value);
    }

    static CameraFrameView()
    {
        AffectsRender<CameraFrameView>(FrameProperty, FrameVersionProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var frame = Frame;
        if (frame == null) return;

        var size = frame.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        // 等比铺满（相当于 Image 的 Uniform）：留边不裁切
        var scale = System.Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height);
        var w = size.Width * scale;
        var h = size.Height * scale;
        var x = (Bounds.Width - w) / 2;
        var y = (Bounds.Height - h) / 2;

        context.DrawImage(frame, new Rect(0, 0, size.Width, size.Height),
            new Rect(x, y, w, h));
    }
}
