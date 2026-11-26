// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia.Controls;
using Avalonia.Interactivity;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

public partial class RestartConfirmDialogView : UserControl
{
    public RestartConfirmDialogView()
    {
        InitializeComponent();
    }

    /// <summary>先把选择落盘，再拉新实例并退出当前进程（单实例互斥体由 App 那边放掉）。</summary>
    private void OnRestartNow(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.CommitGpuChoice();
        if (TopLevel.GetTopLevel(this) is MainWindow main)
            App.RestartApplication(main);
    }
}
