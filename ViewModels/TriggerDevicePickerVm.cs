// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;

namespace MiHome.ViewModels;

/// <summary>
/// 触发条件的「① 设备」选择弹窗。视觉与交互照搬桌面组件的「添加设备」弹窗
/// （同一套 PickCard 卡片网格、房间分组、顶部胶囊筛选、搜索框），差别只有两点：
/// 这里是**单选**，并且选完要点「确定」才生效（添加设备那边是勾选即写回分区）。
/// 关闭 / 确定由窗口接住，视图模型只负责列表与选中项。
/// </summary>
public partial class TriggerDevicePickerVm : ObservableObject
{
    private const string NoRoom = "未分配房间";

    private readonly List<PickerItem> _all = new();
    private readonly List<PickerGroup> _rooms = new();

    /// <summary>正在显示的那一组；null = 「全部房间」。</summary>
    private PickerGroup? _active;

    public ObservableCollection<PickerGroup> Groups { get; } = new();

    /// <summary>顶部分组筛选条：第一项固定是「全部房间」，随后是各房间。</summary>
    public ObservableCollection<PickerTab> Tabs { get; } = new();

    [ObservableProperty] private string _searchText = "";

    /// <summary>副标题：顺带告诉人当前选中了哪台。</summary>
    [ObservableProperty] private string _subtitle = "点一台设备选中，确定后设置触发条件";

    /// <summary>底部常驻提示：随用途变（触发条件 / 添加动作），由窗口 SetContext 定制。</summary>
    [ObservableProperty] private string _footerHint = "确定后回到触发条件，继续选事件或状态";

    /// <summary>未选中时的副标题：默认是触发条件的说法，别的用途在弹窗前改掉。</summary>
    private string _emptySubtitle = "点一台设备选中，确定后设置触发条件";

    /// <summary>按用途定制两处文案（副标题的未选中态 + 底部提示）。</summary>
    public void SetContext(string emptySubtitle, string footerHint)
    {
        _emptySubtitle = emptySubtitle;
        FooterHint = footerHint;
        UpdateSubtitle();
    }

    /// <summary>关窗回调：由窗口挂上，右上角关闭与点遮罩都走这里。</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>当前选中的那台（null = 还没选）。</summary>
    public PickerItem? Selected { get; private set; }

    public MijiaDevice? SelectedDevice => Selected?.Device;

    /// <summary>底部「确定」是否可用。</summary>
    public bool HasSelection => Selected != null;

    public bool HasQuery => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsEmptyResult => Groups.Count == 0;

    public string EmptyText => "没有匹配的设备";

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    /// <summary>灌入设备清单并按 did 回填现有选择（编辑已有条件时用）。</summary>
    public void Load(IEnumerable<MijiaDevice> devices, string? selectedDid)
    {
        _all.Clear();
        Selected = null;

        foreach (var d in devices)
        {
            var item = new PickerItem { Device = d };
            if (selectedDid != null && MijiaScene.SameDid(d.Did, selectedDid))
            {
                item.IsIn = true;
                Selected = item;
            }
            _all.Add(item);
        }

        ApplyFilter();
        UpdateSubtitle();
        OnPropertyChanged(nameof(HasSelection));
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        ApplyFilter();
    }

    /// <summary>单选：点哪张卡就把哪张设成唯一选中，点同一张再点一次也不取消（条件必须有设备）。</summary>
    [RelayCommand]
    private void Select(PickerItem? item)
    {
        if (item == null || item.Device == null) return;

        foreach (var i in _all) i.IsIn = ReferenceEquals(i, item);
        Selected = item;
        UpdateSubtitle();
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>点分类胶囊：只显示那一组（group 为 null 就是「全部房间」）。</summary>
    public void SelectTab(PickerTab? tab)
    {
        _active = tab?.Group;
        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t.Group, _active);
        RebuildGroups();
    }

    /// <summary>按搜索词重建各房间分组；房多的靠前、「未分配房间」垫底（与添加设备弹窗同序）。</summary>
    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        var matched = _all.Where(item =>
            q.Length == 0
            || item.Device!.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || item.Device!.RoomName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _rooms.Clear();

        foreach (var g in matched
                     .GroupBy(i => RoomOf(i.Device!))
                     .OrderBy(g => string.Equals(g.Key, NoRoom, StringComparison.Ordinal) ? 1 : 0)
                     .ThenByDescending(g => g.Count())
                     .ThenBy(g => g.Key, StringComparer.CurrentCulture))
        {
            var items = g
                .OrderByDescending(i => i.IsIn)
                .ThenBy(i => i.Device!.Name, StringComparer.CurrentCulture)
                .ToList();

            _rooms.Add(new PickerGroup(g.Key, items));
        }

        _active = null;
        RebuildTabs();
        RebuildGroups();
    }

    private void RebuildTabs()
    {
        Tabs.Clear();
        Tabs.Add(new PickerTab(null, "全部房间", _active is null));
        foreach (var room in _rooms)
            Tabs.Add(new PickerTab(room, room.Title, ReferenceEquals(_active, room)));
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        if (_active is null)
        {
            foreach (var room in _rooms) Groups.Add(room);
        }
        else Groups.Add(_active);

        OnPropertyChanged(nameof(IsEmptyResult));
    }

    private void UpdateSubtitle()
        => Subtitle = Selected?.Device is { } d
            ? $"已选「{d.Name}」，点确定继续"
            : _emptySubtitle;

    private static string RoomOf(MijiaDevice device)
        => string.IsNullOrWhiteSpace(device.RoomName) ? NoRoom : device.RoomName;
}
