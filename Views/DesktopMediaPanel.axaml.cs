// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 桌面媒体分区的内容：音响身份（点击换音箱）、播放状态、播放控制与音量入口。
/// 这块分区没有标题栏（整卡可拖），菜单入口就是右上角那枚 ⋯。
/// 贴桌面窗永远不激活，所以这里只有按钮与命令，没有需要键盘焦点的控件。
/// </summary>
public partial class DesktopMediaPanel : UserControl
{
    public DesktopMediaPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 音量 / 换音箱浮层：和分区菜单一样只能开独立小窗（Popup 会被压在普通窗口之下），
    /// 所以把按钮的屏幕矩形整个传过去，浮层才知道往哪贴、下方够不够放。
    /// </summary>
    private void OnVolumeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopMediaVm vm || sender is not Control anchor) return;
        vm.OpenVolume(ScreenRect(anchor));
    }

    private void OnSpeakerClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopMediaVm vm || sender is not Control anchor) return;
        vm.OpenSpeakerPicker(ScreenRect(anchor));
    }

    /// <summary>媒体卡没有标题栏，⋯ 就是这块分区唯一的菜单入口（外观设置 / 关闭媒体分区）。</summary>
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
