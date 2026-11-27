// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MiHomeHub.Services;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 设置页「桌面数据卡」摘要：只报已投到桌面的张数与总数，逐张开 / 关统一在首页
/// 「数据看板」的编辑弹窗里做——首页与桌面两个去处摆在同一行，不再两处各写一套开关。
/// </summary>
public sealed partial class DesktopDataCardsVm : ObservableObject
{
    public DesktopDataCardsVm() => DesktopHost.DataCardStateChanged += Reload;

    /// <summary>「已投到桌面 3 张 · 共 12 张可选」；一张都没有时换一句引导。</summary>
    [ObservableProperty] private string _summaryText = "";

    /// <summary>设置页每次打开、以及桌面数据卡增删后按 desktop.json 的现状重算。</summary>
    public void Reload()
    {
        var total = DataCardCatalog.All.Count;
        var shown = DataCardCatalog.All.Count(def => DesktopHost.IsDataCardShown(def.Key));
        SummaryText = shown == 0
            ? $"还没有卡片放到桌面 · 共 {total} 张可选"
            : $"已投到桌面 {shown} 张 · 共 {total} 张可选";
    }
}
