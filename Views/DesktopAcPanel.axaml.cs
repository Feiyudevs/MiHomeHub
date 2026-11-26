// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MiHomeHub.Services;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

/// <summary>
/// 桌面空调分区：一张大空调控制卡。命令全部走 <see cref="DesktopAcVm"/>，
/// 与详情抽屉共用同一套下发路径；本文件只处理环弧几何刷新与浮层落点换算。
/// </summary>
public partial class DesktopAcPanel : UserControl
{
    private const double RingSize = 124;
    private const double RingThickness = 9;
    private DesktopAcVm? _vm;

    public DesktopAcPanel()
    {
        InitializeComponent();
        // 按住加减时连续走档：先等 420ms（避免误触连跳），之后每 180ms 一档。
        TempDownBtn.Delay = 420;
        TempDownBtn.Interval = 180;
        TempUpBtn.Delay = 420;
        TempUpBtn.Interval = 180;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as DesktopAcVm;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            Dispatcher.UIThread.Post(RefreshRing, DispatcherPriority.Render);
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesktopAcVm.TempRatio) or nameof(DesktopAcVm.TargetTemp)
            or nameof(DesktopAcVm.TempMin) or nameof(DesktopAcVm.TempMax))
        {
            Dispatcher.UIThread.Post(RefreshRing, DispatcherPriority.Render);
        }
    }

    /// <summary>按当前温度占比重画前景弧：从正上方起顺时针扫一段，不足一整圈时端点圆头。</summary>
    private void RefreshRing()
    {
        if (_vm == null || RingArc == null) return;
        RingArc.Data = BuildArc(RingSize, RingThickness, _vm.TempRatio);
    }

    /// <summary>生成一条从 12 点方向顺时针扫 ratio 圈的弧几何（0 = 不画，1 = 整圆）。</summary>
    private static Geometry? BuildArc(double size, double thickness, double ratio)
    {
        if (ratio <= 0.001) return null;

        var r = (size - thickness) / 2;
        var cx = size / 2;
        var cy = size / 2;
        var startAngle = -Math.PI / 2; // 12 点方向
        var sweep = ratio * Math.PI * 2;
        var endAngle = startAngle + sweep;

        var sx = cx + r * Math.Cos(startAngle);
        var sy = cy + r * Math.Sin(startAngle);
        var ex = cx + r * Math.Cos(endAngle);
        var ey = cy + r * Math.Sin(endAngle);

        static ArcSegment Arc(Point end, Size radius, bool large) => new()
        {
            Point = end,
            Size = radius,
            RotationAngle = 0,
            IsLargeArc = large,
            SweepDirection = SweepDirection.Clockwise
        };

        // 接近整圆时拆成两段半圆，避免单段 180°+ 弧被几何解析器退化掉
        if (ratio >= 0.999)
        {
            var midAngle = startAngle + Math.PI;
            var mx = cx + r * Math.Cos(midAngle);
            var my = cy + r * Math.Sin(midAngle);
            return new PathGeometry
            {
                Figures =
                {
                    new PathFigure { StartPoint = new Point(sx, sy), IsClosed = false,
                        Segments = { Arc(new Point(mx, my), new Size(r, r), false) } },
                    new PathFigure { StartPoint = new Point(mx, my), IsClosed = false,
                        Segments = { Arc(new Point(ex, ey), new Size(r, r), false) } }
                }
            };
        }

        return new PathGeometry
        {
            Figures =
            {
                new PathFigure
                {
                    StartPoint = new Point(sx, sy),
                    IsClosed = false,
                    Segments = { Arc(new Point(ex, ey), new Size(r, r), sweep > Math.PI) }
                }
            }
        };
    }

    // ===== 浮层落点：贴桌面窗不能用 Popup，独立小窗由 Host 按这里换算的锚点摆放 =====

    private void OnAcClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopAcVm vm || sender is not Control anchor) return;
        vm.OpenAcPicker(ScreenRect(anchor));
    }

    private void OnMoreClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopZoneVm vm || sender is not Control anchor) return;
        DesktopHost.OpenGroupMenu(vm, ScreenRect(anchor));
    }

    private static PixelRect ScreenRect(Control c)
    {
        var topLeft = c.PointToScreen(new Point(0, 0));
        var bottomRight = c.PointToScreen(new Point(c.Bounds.Width, c.Bounds.Height));
        return new PixelRect(
            topLeft.X,
            topLeft.Y,
            Math.Max(1, bottomRight.X - topLeft.X),
            Math.Max(1, bottomRight.Y - topLeft.Y));
    }
}
