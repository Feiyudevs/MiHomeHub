// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MiHomeHub.Controls;

/// <summary>
/// 圆形进度环：下载进度用，替代原来的细横条。
/// <see cref="Value"/> 取 0~1；静态时也可能先画一圈底色环，让位置看起来不是空白。
/// </summary>
public class CircularProgress : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<CircularProgress, double>(nameof(Value));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<CircularProgress, double>(nameof(Thickness), 3.0);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<CircularProgress, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<CircularProgress, IBrush?>(nameof(FillBrush));

    static CircularProgress()
    {
        AffectsRender<CircularProgress>(ValueProperty, ThicknessProperty, TrackBrushProperty, FillBrushProperty);
    }

    /// <summary>进度，0~1（超出自动夹紧）。</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>环宽（px）。</summary>
    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 1) return;

        var thickness = Math.Clamp(Thickness, 1, side / 2 - 0.5);
        var radius = (side - thickness) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var fill = FillBrush ?? Brushes.White;
        var track = TrackBrush ?? new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));

        // 底环：整圆淡线，未下载时也能看出这里有个进度环
        context.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);

        var progress = Math.Clamp(Value, 0, 1);
        if (progress <= 0) return;

        // 满值直接画整圆：起点与终点重合时 ArcTo 画不出来
        if (progress >= 0.999)
        {
            context.DrawEllipse(null, new Pen(fill, thickness, lineCap: PenLineCap.Round),
                center, radius, radius);
            return;
        }

        var startAngle = -Math.PI / 2;              // 12 点方向起步
        var sweep = progress * Math.PI * 2;
        var endAngle = startAngle + sweep;
        Point OnCircle(double angle) => new(center.X + radius * Math.Cos(angle),
                                           center.Y + radius * Math.Sin(angle));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(OnCircle(startAngle), false);
            ctx.ArcTo(OnCircle(endAngle), new Size(radius, radius), 0,
                sweep > Math.PI, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(fill, thickness, lineCap: PenLineCap.Round), geometry);
    }
}
