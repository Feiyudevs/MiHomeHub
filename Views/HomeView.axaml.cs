// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void OnGotoScenes(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SelectedNavIndex = 2;
    }

    private void OnGotoRooms(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SelectedNavIndex = 1;
    }

    /// <summary>「数据看板」标题行双击：真实数据 ⇄ 演示数据。</summary>
    private void OnTrendHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount >= 2 && DataContext is MainViewModel vm)
        {
            // 标题旁的「编辑」按钮不算标题：连着点两下按钮不该顺手把首页切成演示数据
            if (e.Source is Visual source &&
                (source is Button || source.FindAncestorOfType<Button>() is not null)) return;
            vm.ToggleTrendPreview();
        }
    }
}
