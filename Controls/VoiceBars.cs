// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MiHome.Controls;

/// <summary>
/// 小爱声纹条：5 根竖条按正弦节律起伏，用来替代静态声纹图标，让入口看起来「在听」。
/// 用缩放而不是改高度，避免每帧触发布局；只在本控件挂到可视树且 IsActive 时跑定时器，
/// 面板收起 / 窗口关闭后自动停，避免常驻动画空耗 CPU。
/// </summary>
public class VoiceBars : UserControl
{
    /// <summary>每根条的相位差，让 5 根条错峰起伏，而不是整体上下齐动。</summary>
    private static readonly double[] Phases = { 0.0, 0.9, 1.8, 2.7, 3.6 };

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<VoiceBars, IBrush?>(nameof(BarBrush));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<VoiceBars, bool>(nameof(IsActive), true);

    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<VoiceBars, bool>(nameof(IsBusy));

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>是否律动；false 时停表并复位成等高的静态条。</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>发送指令中：节奏加快、幅度更明显，像在回话。</summary>
    public bool IsBusy
    {
        get => GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    private readonly Border[] _bars = new Border[5];
    private DispatcherTimer? _timer;
    private double _angle;

    static VoiceBars()
    {
        BarBrushProperty.Changed.AddClassHandler<VoiceBars>((x, _) => x.ApplyBrush());
        IsActiveProperty.Changed.AddClassHandler<VoiceBars>((x, _) => x.SyncTimer());
    }

    public VoiceBars()
    {
        IsHitTestVisible = false;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        for (var i = 0; i < _bars.Length; i++)
        {
            var bar = new Border
            {
                Margin = new Thickness(0.75, 0),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RenderTransform = new ScaleTransform(1, 1)
            };
            Grid.SetColumn(bar, i);
            grid.Children.Add(bar);
            _bars[i] = bar;
        }
        Content = grid;

        ApplyBrush();
        AttachedToVisualTree += (_, _) => SyncTimer();
        DetachedFromVisualTree += (_, _) => StopTimer();
    }

    private void ApplyBrush()
    {
        foreach (var bar in _bars) bar.Background = BarBrush;
    }

    private void SyncTimer()
    {
        if (IsActive && VisualRoot != null)
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                _timer.Tick += (_, _) => Tick();
            }
            _timer.Start();
            return;
        }
        StopTimer();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        foreach (var bar in _bars) ((ScaleTransform)bar.RenderTransform!).ScaleY = 1;
    }

    private void Tick()
    {
        _angle += IsBusy ? 0.5 : 0.2;              // 弧度/帧：发送中约 2.5 倍速
        for (var i = 0; i < _bars.Length; i++)
        {
            var bar = _bars[i];
            var scale = IsBusy
                ? 0.22 + 0.78 * (0.5 + 0.5 * Math.Sin(_angle * (1 + i * 0.32) + Phases[i]))
                : 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(_angle * (1 + i * 0.18) + Phases[i]));
            ((ScaleTransform)bar.RenderTransform!).ScaleY = scale;
        }
    }
}
