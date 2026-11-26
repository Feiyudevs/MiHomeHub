// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MiHomeHub.Services;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

/// <summary>
/// 桌面空调分区的换空调浮层：独立顶层小窗，贴着空调那一行下方展开，失焦即收。
/// 选中的就是全局默认空调，首页与设置里的选择跟着一起变，不留第二份偏好。
/// </summary>
public partial class DesktopAcPickWindow : Window
{
    private PixelRect _anchor;
    private DateTime _openedAt;
    private DesktopAcVm? _target;

    /// <summary>这枚浮层是为哪个分区开的，宿主关闭 / 再次点按钮时据此配对。</summary>
    public DesktopAcVm? Target => _target;

    public DesktopAcPickWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated;
    }

    public void Bind(DesktopAcVm vm, PixelRect anchor)
    {
        _target = vm;
        _anchor = anchor;
        DataContext = vm;
        vm.ReloadAcPicks();

        // 先给个「按钮正下方」的粗略落点，避免窗口先在屏幕左上角闪一帧
        Position = new PixelPoint(anchor.X, anchor.Bottom);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _openedAt = DateTime.UtcNow;

        PopupPlacement.AnchorTo(this, _anchor, rightAligned: false);
        Activate();

        // 首帧先画在「收起」状态，等渲染循环跑起来再加 .open，过渡才有起点
        Dispatcher.UIThread.Post(() => Card.Classes.Add("open"), DispatcherPriority.Render);
    }

    private void OnPick(object? sender, RoutedEventArgs e)
    {
        if (_target == null || sender is not Button { Tag: string did }) return;
        _target.PickAc(did);
        Close();
    }

    /// <summary>宿主分区窗是 NOACTIVATE，开场 300ms 内的失活一律忽略，否则刚弹出就被吞掉。</summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if ((DateTime.UtcNow - _openedAt).TotalMilliseconds < 300) return;
        Close();
    }
}
