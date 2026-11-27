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

/// <summary>数据卡右键菜单：切换大小 / 外观设置 / 关闭（保留位置） / 移除（删配置）。</summary>
public partial class CardMenuWindow : Window
{
    private PixelRect _anchor;
    private DateTime _openedAt;

    public CardMenuWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated;
    }

    public DataCardVm? Target { get; private set; }

    public void Bind(DataCardVm vm, PixelRect anchor)
    {
        Target = vm;
        DataContext = vm;
        _anchor = anchor;
        Position = new PixelPoint(anchor.X, anchor.Y);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if ((DateTime.UtcNow - _openedAt).TotalMilliseconds < 300) return;
        Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _openedAt = DateTime.UtcNow;
        PopupPlacement.AnchorTo(this, _anchor);
        Activate();
        Dispatcher.UIThread.Post(() => Card.Classes.Add("open"), DispatcherPriority.Render);
    }

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DataCardVm vm || sender is not Button { Tag: string tag }) return;

        switch (tag)
        {
            case "size":
                Close();
                DesktopHost.SetDataCardSize(vm, vm.IsFull);
                break;
            case "appearance":
                Close();
                DesktopHost.OpenAppearance();
                break;
            case "close":
                Close();
                DesktopHost.CloseDataCard(vm);
                break;
            case "remove":
                // 移除是不可逆的删配置：菜单里点一次就执行（用户已经在右键菜单里明确选了这项）
                Close();
                DesktopHost.RemoveDataCard(vm);
                break;
        }
    }
}
