// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MiHomeHub.Services;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

/// <summary>
/// 桌面监控分区的内容：一张 16:9 实时画面 + 身份行（这台分区看的是哪台摄像头）。
/// 这块分区没有标题栏，整张卡当拖动面，菜单入口在身份行最右那枚 ⋯。
/// 取流跟着可视树走：挂上就开、卸掉（折叠 / 收起）就停，看不见的卡片不占解码线程。
/// </summary>
public partial class DesktopCameraPanel : UserControl
{
    public DesktopCameraPanel()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is DesktopCameraVm vm) vm.AttachLive();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is DesktopCameraVm vm) vm.StopLive();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>点画面 / 身份行：打开这台摄像头的详情（声音、大画面都在那边）。</summary>
    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopCameraVm vm) vm.OpenPanelCommand.Execute(null);
    }

    /// <summary>没有标题栏的分区里，⋯ 是唯一的菜单入口（外观设置 / 关闭监控分区）。</summary>
    private void OnMoreClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopCameraVm vm || sender is not Control anchor) return;
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
