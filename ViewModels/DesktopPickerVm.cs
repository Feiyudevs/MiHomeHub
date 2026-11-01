using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;

namespace MiHome.ViewModels;

/// <summary>
/// 添加设备弹窗：全量设备 + 搜索 + 勾选；勾选即时写回分区，没有「确定」二次提交。
/// 设备按**房间**分组（与主窗口侧栏的房间芯片同序：设备多的房间在前，未分配房间垫底），
/// 每组内是网格卡片，卡片副标题显示设备类别。
/// </summary>
public partial class DesktopPickerVm : ObservableObject
{
    private const string NoRoom = "未分配房间";

    private readonly List<PickerItem> _all = new();

    public ObservableCollection<PickerGroup> Groups { get; } = new();

    /// <summary>顶部房间直达条：第一项固定是「全部房间」（回到顶部），后面是当前匹配到的各房间。</summary>
    public ObservableCollection<PickerTab> Tabs { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _subtitle = "勾选即加入分区";

    public DesktopGroupVm? Target { get; private set; }

    /// <summary>关窗回调：由窗口挂上，右上角关闭与点遮罩都走这里。</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>搜索框右边的「清除」是否显示。</summary>
    public bool HasQuery => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>搜不到任何设备时显示空态。</summary>
    public bool IsEmptyResult => Groups.Count == 0;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    public void Bind(DesktopGroupVm target)
    {
        Target = target;
        _all.Clear();

        foreach (var d in target.Main.AllDevices)
        {
            _all.Add(new PickerItem
            {
                Device = d,
                IsIn = target.Contains(d.Did)
            });
        }

        ApplyFilter();
        UpdateSubtitle();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    /// <summary>按房间重建分组；分区里已有的设备在房间内排前面，方便直接取消。</summary>
    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        var matched = _all.Where(item =>
            q.Length == 0
            || item.Device.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || item.Device.RoomName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Groups.Clear();

        // 房间顺序与主窗口侧栏一致：设备多的房间靠前，「未分配房间」永远垫底
        foreach (var g in matched
                     .GroupBy(i => RoomOf(i.Device))
                     .OrderBy(g => string.Equals(g.Key, NoRoom, StringComparison.Ordinal) ? 1 : 0)
                     .ThenByDescending(g => g.Count())
                     .ThenBy(g => g.Key, StringComparer.CurrentCulture))
        {
            var items = g
                .OrderByDescending(i => i.IsIn)
                .ThenBy(i => i.Device.Name, StringComparer.CurrentCulture)
                .ToList();

            Groups.Add(new PickerGroup(g.Key, items));
        }

        RebuildTabs();
        OnPropertyChanged(nameof(IsEmptyResult));
    }

    /// <summary>
    /// 重建房间直达条：房号顺序与分组完全一致，所以 tab 的下标与 <see cref="Groups"/> 的下标错开 1。
    /// 过滤后原来的激活房间没了就回到「全部房间」。
    /// </summary>
    private void RebuildTabs()
    {
        var active = Tabs.FirstOrDefault(t => t.IsActive)?.RoomKey;

        Tabs.Clear();
        Tabs.Add(new PickerTab(null, "全部房间", active is null));
        foreach (var group in Groups)
            Tabs.Add(new PickerTab(group.Title, group.Title, active == group.Title));
    }

    /// <summary>点了某个房间 tab：切换高亮，返回它在 <see cref="Groups"/> 里的下标（-1 表示「全部房间」）。</summary>
    public int ActivateTab(PickerTab? tab)
    {
        if (tab == null) return -1;
        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t, tab);
        return tab.RoomKey is null ? -1 : Groups.ToList().FindIndex(g => g.Title == tab.RoomKey);
    }

    private static string RoomOf(MijiaDevice device)
        => string.IsNullOrWhiteSpace(device.RoomName) ? NoRoom : device.RoomName;

    /// <summary>设备大类名（卡片副标题）。房间已经在分组标题上了，副标题改显示类别才有信息量。</summary>
    internal static string CategoryOf(MijiaDevice device)
    {
        switch (device.IconKey)
        {
            case "light": return "灯光";
            case "switch": return "开关";
            case "socket": return "插座";
            case "curtain": return "窗帘";
            case "ac": return "空调";
        }
        return device.IsSpeaker ? "音箱" : "设备";
    }

    private void UpdateSubtitle()
    {
        var count = Target?.Devices.Count ?? 0;
        Subtitle = count == 0 ? "勾选设备加入这个分区" : $"已选 {count} 台设备 · 再点一次可移出";
    }

    [RelayCommand]
    private void Toggle(PickerItem? item)
    {
        if (item == null || Target == null) return;
        if (item.IsIn)
        {
            Target.RemoveDevice(item.Device);
            item.IsIn = false;
        }
        else
        {
            Target.AddDevice(item.Device);
            item.IsIn = true;
        }
        UpdateSubtitle();
    }

    /// <summary>全部移出：把一个分区里的设备一次清空（分区本身保留）。</summary>
    [RelayCommand]
    private void ClearAll()
    {
        if (Target == null) return;
        foreach (var item in _all.ToList())
        {
            if (!item.IsIn) continue;
            Target.RemoveDevice(item.Device);
            item.IsIn = false;
        }
        UpdateSubtitle();
    }
}

/// <summary>弹窗里的一条设备：点整卡即切换「是否已加入分区」，选中态由卡面主色表达。</summary>
public partial class PickerItem : ObservableObject
{
    public required MijiaDevice Device { get; init; }

    /// <summary>卡片副标题：设备类别（房间已经做成分组标题了，副标题再写房间是重复信息）。</summary>
    public string Category => DesktopPickerVm.CategoryOf(Device);

    [ObservableProperty] private bool _isIn;
}

/// <summary>顶部房间直达条上的一枚胶囊。<see cref="RoomKey"/> 为 null 表示「全部房间」。</summary>
public partial class PickerTab : ObservableObject
{
    public PickerTab(string? roomKey, string title, bool isActive)
    {
        RoomKey = roomKey;
        Title = title;
        _isActive = isActive;
    }

    public string? RoomKey { get; }

    public string Title { get; }

    [ObservableProperty] private bool _isActive;
}

/// <summary>一个房间分组（标题 = 房间名 + 该房间的设备）。</summary>
public sealed class PickerGroup : ObservableObject
{
    public PickerGroup(string title, IEnumerable<PickerItem> items)
    {
        Title = title;
        Items = new ObservableCollection<PickerItem>(items);
    }

    public string Title { get; }

    public ObservableCollection<PickerItem> Items { get; }

    public string CountText => Items.Count.ToString();
}
