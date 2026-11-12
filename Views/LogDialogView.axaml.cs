// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>操作日志弹窗：复制 / 清空按钮的事件处理随本控件走。</summary>
public partial class LogDialogView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public LogDialogView()
    {
        InitializeComponent();
    }

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (Vm != null && !string.IsNullOrEmpty(Vm.LogText) && clipboard != null)
            await clipboard.SetTextAsync(Vm.LogText);
    }

    private void OnClearLog(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.LogText = "";
    }
}
