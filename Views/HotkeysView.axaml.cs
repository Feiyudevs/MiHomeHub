// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

public partial class HotkeysView : UserControl
{
    public HotkeysView()
    {
        InitializeComponent();
    }

    /// <summary>新建：先在 VM 里重置草稿，再开编辑弹窗。</summary>
    private async void OnNewHotkeyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (vm.IsHotkeyEditorOpen) return;

        vm.BeginNewHotkey();
        await ShowEditor(vm, owner);
    }

    /// <summary>编辑已绑定：把组合键与卡片灌进草稿后打开同一个弹窗。</summary>
    private async void OnEditHotkeyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not Button { Tag: HotkeyItemVm item }) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (vm.IsHotkeyEditorOpen) return;

        vm.BeginEditHotkey(item);
        await ShowEditor(vm, owner);
    }

    private async Task ShowEditor(MainViewModel vm, Window owner)
    {
        try
        {
            var window = new HotkeyEditorWindow(vm);
            window.FitTo(owner);
            await window.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HotkeysView] 打开编辑弹窗失败：{ex}");
            if (vm.IsHotkeyEditorOpen) vm.CancelHotkeyEditorCommand.Execute(null);
        }
    }
}
