using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;
using MiHome.Services;

namespace MiHome.ViewModels;

/// <summary>
/// 「添加到桌面组件」选择分区弹窗：列出全部已有分区，点一下即把设备加入该分区；
/// 底部输入名称可新建分区。分区列表来自 desktop.json 的布局配置，
/// 总开关关着时也能选，真正加入时会自动把桌面组件打开。
/// </summary>
public partial class DesktopGroupPickVm : ObservableObject
{
    public ObservableCollection<GroupPickItem> Groups { get; } = new();

    /// <summary>副标题：点名是哪台设备要加进去。</summary>
    [ObservableProperty] private string _subtitle = "";

    /// <summary>新建分区的名称输入（预填房间名/设备名）。</summary>
    [ObservableProperty] private string _newName = "";

    /// <summary>一个分区都没有时显示空态，引导用户直接新建。</summary>
    public bool IsEmpty => Groups.Count == 0;

    public MijiaDevice? Device { get; private set; }

    /// <summary>关窗回调：由窗口挂上，右上角关闭与点遮罩都走这里。</summary>
    public Action? CloseRequested { get; set; }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    public void Bind(MijiaDevice device)
    {
        Device = device;
        Subtitle = $"选择「{device.Name}」要加入的分区";
        NewName = SuggestName(device);
        Reload();
    }

    /// <summary>每次打开都按当前分区重建列表（分区可能被解散/改名/加过设备）。</summary>
    public void Reload()
    {
        Groups.Clear();
        foreach (var g in DesktopHost.Layout.Groups)
        {
            Groups.Add(new GroupPickItem
            {
                Id = g.Id,
                Name = g.Name,
                Count = g.Dids.Count,
                Contains = Device != null && g.Dids.Contains(Device.Did)
            });
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>点某个已有分区：加入并关窗（已在里面就只把分区亮出来）。</summary>
    [RelayCommand]
    private void Pick(GroupPickItem? item)
    {
        if (item == null || Device == null) return;
        DesktopHost.AddDeviceToGroup(item.Id, Device);
        CloseRequested?.Invoke();
    }

    /// <summary>新建分区：用输入框里的名字建组并放入这台设备。</summary>
    [RelayCommand]
    private void Create()
    {
        if (Device == null) return;
        DesktopHost.CreateGroupWithDevice(Device, NewName);
        CloseRequested?.Invoke();
    }

    /// <summary>新分区名的预填：优先房间名，其次设备名（与自动建组同规则）。</summary>
    private static string SuggestName(MijiaDevice device)
        => string.IsNullOrWhiteSpace(device.RoomName) || device.RoomName == "未分配房间"
            ? (string.IsNullOrWhiteSpace(device.Name) ? "新分区" : device.Name)
            : device.RoomName;
}

/// <summary>弹窗里的一条已有分区。</summary>
public sealed class GroupPickItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required int Count { get; init; }

    /// <summary>这台设备是否已经在该分区里（右侧显示「已添加」）。</summary>
    public required bool Contains { get; init; }

    public string CountText => Count == 0 ? "空分区" : $"{Count} 台设备";
}
