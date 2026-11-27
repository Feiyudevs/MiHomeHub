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
    private TrendCardReorderDrag? _bigReorder;
    private TrendCardReorderDrag? _smallReorder;

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
        // 两列各挂一个拖动器：大卡、小卡的顺序互不干扰，拖哪一列只改那一列
        _bigReorder = vm is null
            ? null
            : new TrendCardReorderDrag(BigCardOptionsList, vm.BigTrendCardOptions, vm.CommitTrendCardOrder);
        _smallReorder = vm is null
            ? null
            : new TrendCardReorderDrag(SmallCardOptionsList, vm.SmallTrendCardOptions, vm.CommitTrendCardOrder);
    }
}
