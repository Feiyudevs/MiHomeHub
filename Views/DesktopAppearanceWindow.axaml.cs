// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>桌面分区外观弹窗：背景色 / 背景透明度 / 毛玻璃，改动即时生效。</summary>
public partial class DesktopAppearanceWindow : Window
{
    private readonly DesktopAppearanceVm _vm = new();

    public DesktopAppearanceWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.CloseRequested = Close;
    }

    /// <summary>每次打开都按当前设置回填，避免显示上次的旧值。</summary>
    public void Reload() => _vm.Reload();

    /// <summary>
    /// 预设色块：走代码而不是 XAML 里的 Command 绑定。
    /// 色块在 ItemsControl 的 DataTemplate 里，DataContext 已经被换成了 ColorSwatch，
    /// 命令要靠 $parent[Window] 绕出去，编译绑定在这条路径上拿不到命令（点了没反应）。
    /// 用 Tag 带出 hex、在窗口里直接调 VM，最稳。
    /// </summary>
    private void OnSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) _vm.PickColorCommand.Execute(hex);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }
}
