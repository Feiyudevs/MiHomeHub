// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.ComponentModel;
using Avalonia.Controls;
using MiHomeHub.Models;
using MiHomeHub.Services;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

/// <summary>
/// 场景详情独立弹窗的窗口壳：里面就是 <see cref="SceneEditorView"/>，
/// 数据与保存 / 删除 / 新建等命令仍挂在主视图模型上，和主窗口里那份完全一致。
/// 打开 / 关闭由 <see cref="MainViewModel.IsSceneEditorOpen"/> 驱动，用户直接关窗时反向收编辑态。
/// </summary>
public partial class SceneEditorWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _iconPickerOpen;

    public SceneEditorWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.IconEditorRequested += OnIconEditorRequested;
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.IconEditorRequested -= OnIconEditorRequested;
        };
    }

    /// <summary>按屏幕居中显示（不依赖父窗口，桌面弹窗发起时也能弹在视野中间）。</summary>
    public void ShowCentered()
    {
        PopupPlacement.CenterOnScreen(this, Width, Height);
        if (!IsVisible) Show();
        Activate();
    }

    /// <summary>独立场景窗里点图标：只有本窗承载编辑面板时才由这里接管，内嵌模式仍交给主窗口。</summary>
    private async void OnIconEditorRequested(object? sender, CustomIconTarget target)
    {
        if (!target.FromEditor || _vm.IsSceneEditorInline) return;
        if (_iconPickerOpen) return;
        _iconPickerOpen = true;
        try
        {
            var window = new IconPickerWindow(target);
            await window.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _vm.Log("[IconPicker] 打开图标选择窗失败：" + ex.Message);
        }
        finally
        {
            _iconPickerOpen = false;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSceneEditorOpen) && !_vm.IsSceneEditorOpen)
            Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // 用户自己关掉窗口（Esc / Alt+F4）：把编辑态一起收掉，免得下次打开残留半个面板
        if (_vm.IsSceneEditorOpen) _vm.CloseSceneEditorCommand.Execute(null);
    }
}
