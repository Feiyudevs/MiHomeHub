// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MiHomeHub.Controls;

/// <summary>
/// 小爱声纹条：4 根竖条按正弦节律起伏，用来替代静态声纹图标，让入口看起来「在听」。
/// 用缩放而不是改高度，避免每帧触发布局；只在本控件挂到可视树且 IsActive 时跑定时器，
/// 面板收起 / 窗口关闭后自动停，避免常驻动画空耗 CPU。
/// </summary>
public class VoiceBars : UserControl
{
    /// <summary>条数：14~16px 的槽里放 5 根会被缝隙挤成一片糊块，4 根才留得住能看出来的间隙。</summary>
    private const int BarCount = 4;

    /// <summary>每根条的相位差，让各条错峰起伏，而不是整体上下齐动。</summary>
    private static readonly double[] Phases = { 0.0, 1.0, 2.0, 3.0 };

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

    private readonly Border[] _bars = new Border[BarCount];
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
        var columns = new ColumnDefinitions();
        for (var i = 0; i < BarCount; i++)
            columns.Add(new ColumnDefinition(1, GridUnitType.Star));
        var grid = new Grid
        {
            ColumnDefinitions = columns,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        for (var i = 0; i < _bars.Length; i++)
        {
            var bar = new Border
            {
                // 4 根条时槽宽 4px，左右各留 0.75 → 条宽约 2.5px、缝隙 1.5px；
                // 缝隙必须留到条宽的六成左右，否则缩到 14~16px 的图标尺寸就会糊成一整块
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
