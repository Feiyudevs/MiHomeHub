// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;
using MiHome.Services;

namespace MiHome.ViewModels;

/// <summary>
/// 「添加到桌面组件」选择分区弹窗：已有分区多选，底部「新建分区」按需展开名称输入，
/// 点「确定」才把设备一次性加入所有选中的分区（含新建的）。
/// 分区列表来自 desktop.json 的布局配置，总开关关着时也能选，真正加入时会自动打开。
/// </summary>
public partial class DesktopGroupPickVm : ObservableObject
{
    public ObservableCollection<GroupPickItem> Groups { get; } = new();

    /// <summary>副标题：点名是哪台设备要加进去。</summary>
    [ObservableProperty] private string _subtitle = "";

    /// <summary>新建分区的名称输入（预填房间名/设备名）。</summary>
    [ObservableProperty] private string _newName = "";

    /// <summary>是否已展开「新建分区」输入框：默认收起，点按钮才出现。</summary>
    [ObservableProperty] private bool _isCreating;

    /// <summary>一个分区都没有时显示空态。</summary>
    public bool IsEmpty => Groups.Count == 0;

    public MijiaDevice? Device { get; private set; }

    /// <summary>关窗回调：由窗口挂上，右上角关闭与点遮罩都走这里。</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>「新建分区」展开回调：由窗口挂上，负责把光标放进输入框。</summary>
    public Action? CreateRevealed { get; set; }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    public void Bind(MijiaDevice device)
    {
        Device = device;
        Subtitle = $"选择「{device.Name}」要加入的分区";
        IsCreating = false;
        NewName = SuggestName(device);
        Reload();
    }

    /// <summary>每次打开都按当前分区重建列表（分区可能被解散/改名/加过设备）。</summary>
    public void Reload()
    {
        Groups.Clear();
        foreach (var g in DesktopHost.Layout.Groups)
        {
            // 媒体分区不接受设备，出现在这里只会误导
            if (g.IsMedia) continue;
            Groups.Add(new GroupPickItem
            {
                Id = g.Id,
                Name = g.Name,
                Count = g.Dids.Count,
                Contains = Device != null && g.Dids.Contains(Device.Did)
            });
        }
        OnPropertyChanged(nameof(IsEmpty));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    /// <summary>点一行：切换选中。设备已在里面的分区不能再选（右侧显示「已添加」）。</summary>
    [RelayCommand]
    private void Toggle(GroupPickItem? item)
    {
        if (item == null || item.Contains) return;
        item.IsSelected = !item.IsSelected;
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    /// <summary>展开新建分区：把名称输入框露出来，焦点由窗口负责。</summary>
    [RelayCommand]
    private void ShowCreate()
    {
        if (IsCreating) return;
        IsCreating = true;
        CreateRevealed?.Invoke();
    }

    /// <summary>至少有选中分区、或填了新建分区名，才允许确定。</summary>
    private bool CanConfirm
        => Groups.Any(g => g.IsSelected)
           || (IsCreating && !string.IsNullOrWhiteSpace(NewName));

    /// <summary>确定：先按需新建分区，再依次加入所有选中的分区，然后关窗。</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (Device == null) return;

        if (IsCreating && !string.IsNullOrWhiteSpace(NewName))
            DesktopHost.CreateGroupWithDevice(Device, NewName);

        foreach (var item in Groups.Where(g => g.IsSelected).ToList())
            DesktopHost.AddDeviceToGroup(item.Id, Device);

        CloseRequested?.Invoke();
    }

    partial void OnNewNameChanged(string value) => ConfirmCommand.NotifyCanExecuteChanged();

    /// <summary>新分区名的预填：优先房间名，其次设备名（与自动建组同规则）。</summary>
    private static string SuggestName(MijiaDevice device)
        => string.IsNullOrWhiteSpace(device.RoomName) || device.RoomName == "未分配房间"
            ? (string.IsNullOrWhiteSpace(device.Name) ? "新分区" : device.Name)
            : device.RoomName;
}

/// <summary>弹窗里的一条已有分区（可勾选）。</summary>
public sealed partial class GroupPickItem : ObservableObject
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required int Count { get; init; }

    /// <summary>这台设备是否已经在该分区里（右侧显示「已添加」，且不可勾选）。</summary>
    public required bool Contains { get; init; }

    /// <summary>是否被勾选（确定时一起加入）。</summary>
    [ObservableProperty] private bool _isSelected;

    public string CountText => Count == 0 ? "空分区" : $"{Count} 台设备";
}
