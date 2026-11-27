// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia.Controls;
using MiHomeHub.Controls;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

public partial class TrendCardDialogView : UserControl
{
    private MainViewModel? _vm;
    private TrendCardReorderDrag? _reorder;

    public TrendCardDialogView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        var vm = DataContext as MainViewModel;
        if (vm == _vm) return;

        _vm = vm;
        // 行集合与首页共用同一份 ViewModel 数据：拖动改的是集合顺序，提交时由 VM 落盘并通知首页
        _reorder = vm is null ? null : new TrendCardReorderDrag(CardOptionsList, vm.TrendCardOptions, vm.CommitTrendCardOrder);
    }
}
