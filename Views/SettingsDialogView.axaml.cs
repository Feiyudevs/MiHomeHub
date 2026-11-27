// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia.Controls;
using Avalonia.Interactivity;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

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

    /// <summary>设置页内联的分区外观也能打开同一套 HSV 调色盘。</summary>
    private async void OnCustomColorClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var appearance = vm.DesktopAppearance;
        var initial = string.IsNullOrWhiteSpace(appearance.ColorHex)
            ? DesktopAppearanceVm.PresetHexes[0]
            : appearance.ColorHex;
        var picked = await ColorPickerWindow.PickAsync(owner, initial);
        if (!string.IsNullOrWhiteSpace(picked)) appearance.PickColorCommand.Execute(picked);
    }
}
