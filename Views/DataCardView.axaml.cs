// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MiHomeHub.Services;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

public partial class DataCardView : UserControl
{
    public DataCardView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 右上角「⋯」：和右键打开同一份 <see cref="CardMenuWindow"/>，
    /// 锚点取按钮本身，菜单贴着按钮下沿、右缘对齐。
    /// </summary>
    private void OnMoreClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DataCardVm vm || sender is not Control anchor) return;
        DesktopHost.OpenCardMenu(vm, ScreenRect(anchor));
        e.Handled = true;
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
