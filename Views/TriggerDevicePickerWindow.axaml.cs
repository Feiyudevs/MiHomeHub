// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MiHome.Models;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 触发条件的设备选择弹窗宿主：卡片网格 + 房间胶囊 + 搜索，视觉与桌面组件的
/// 「添加设备 / 场景」弹窗同一套；差别是单选，且必须点「确定」才把结果带回去。
/// </summary>
public partial class TriggerDevicePickerWindow : Window
{
    private const double CardPadding = 116;   // 卡片四周留给投影的 58×2

    private readonly TriggerDevicePickerVm _vm = new();

    public TriggerDevicePickerWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.CloseRequested = () => Close(null);
        Opened += (_, _) => FocusSearch();
    }

    /// <summary>灌入候选设备并按 did 回填当前选择。</summary>
    public void Load(IEnumerable<MijiaDevice> devices, string? selectedDid) => _vm.Load(devices, selectedDid);

    /// <summary>按用途定制文案：副标题的未选中态 + 底部提示（触发条件 / 添加动作说法不同）。</summary>
    public void SetContext(string emptySubtitle, string footerHint)
        => _vm.SetContext(emptySubtitle, footerHint);

    /// <summary>按父窗口（触发条件弹窗）的尺寸收一收，小窗口下也不会顶出屏幕。</summary>
    public void FitTo(Window owner)
    {
        var maxW = Math.Max(600, owner.ClientSize.Width - 40);
        var maxH = Math.Max(460, owner.ClientSize.Height - 40);

        var cardW = Math.Min(840, maxW - CardPadding);
        var cardH = Math.Min(580, maxH - CardPadding);

        Card.Width = cardW;
        Card.Height = cardH;
        Width = cardW + CardPadding;
        Height = cardH + CardPadding;

        Position = owner.PointToScreen(new Point(
            (owner.ClientSize.Width - Width) / 2,
            (owner.ClientSize.Height - Height) / 2));
    }

    private void FocusSearch() => Dispatcher.UIThread.Post(() =>
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }, DispatcherPriority.Input);

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
        e.Handled = true;
    }

    /// <summary>点分类胶囊：下面只留那一组（「全部房间」= 全部房间的设备），并把正文拉回顶部。</summary>
    private void OnRoomTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PickerTab tab }) return;
        _vm.SelectTab(tab);
        BodyScroll.Offset = new Vector(0, 0);
        e.Handled = true;
    }

    private void OnMaskPressed(object? sender, PointerPressedEventArgs e)
    {
        Close(null);
        e.Handled = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDevice is not { } device) return;
        Close(device);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            Close(null);
            return;
        }

        // 回车 = 确定，选完设备不用再去点按钮
        if (e.Key == Key.Enter && _vm.SelectedDevice != null)
        {
            OnConfirmClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
