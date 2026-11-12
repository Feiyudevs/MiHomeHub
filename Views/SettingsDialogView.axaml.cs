// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia.Controls;
using Avalonia.Interactivity;
using MiHome.ViewModels;

namespace MiHome.Views;

public partial class SettingsDialogView : UserControl
{
    public SettingsDialogView()
    {
        InitializeComponent();
    }

    /// <summary>顶部 Tab 切换：Tag 即分栏序号，点哪个就切到哪一栏。</summary>
    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var idx)
            && DataContext is MainViewModel vm)
        {
            vm.SettingsTab = idx;
        }
    }

    /// <summary>预设色块：色块在 ItemsControl 的 DataTemplate 里，DataContext 已是 ColorSwatch，
    /// 用 Tag 带出 hex 直接调分区外观 VM 选色（避开编译绑定拿不到父级命令的问题）。</summary>
    private void OnSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } && DataContext is MainViewModel vm)
        {
            vm.DesktopAppearance.PickColorCommand.Execute(hex);
        }
    }
}
