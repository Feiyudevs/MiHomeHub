// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Models;

namespace MiHomeHub.ViewModels;

/// <summary>设备选择弹窗的三种用法。</summary>
public enum TargetPickerMode
{
    /// <summary>桌面组件分区「添加设备 / 场景」：勾选即时写回分区，没有确定按钮。</summary>
    ImmediateCommit,

    /// <summary>触发条件 / 添加动作：单选一台设备，确定后带回。</summary>
    SingleDevice,

    /// <summary>全局快捷键：设备 + 手动场景多选，确定后带回控制卡片。</summary>
    MultipleTargets,
}

/// <summary>
/// 全局唯一的设备选择弹窗 VM：三种模式共用同一套房间分组、搜索、卡片网格与选中逻辑。
/// - 即时提交：勾选 / 取消直接写回桌面组件分区（<see cref="DesktopGroupVm"/>）
/// - 单选：点一台设备选中，点确定带回
/// - 多选：设备与手动场景混排，勾选顺序即执行顺序
/// </summary>
public partial class TargetPickerVm : ObservableObject
{
    private const string NoRoom = "未分配房间";

    /// <summary>场景组的标题，同时也是筛选条上那枚胶囊的文案。</summary>
    public const string SceneGroupTitle = "场景";

    private readonly List<PickerItem> _all = new();

    private readonly List<PickerGroup> _rooms = new();
    private PickerGroup? _sceneGroup;

    /// <summary>正在显示的那一组；null = 「全部」。</summary>
    private PickerGroup? _active;

    private TargetPickerMode _mode = TargetPickerMode.SingleDevice;

    /// <summary>即时提交模式下的目标分区。</summary>
    public DesktopGroupVm? Target { get; private set; }

    /// <summary>即时提交模式下的目标分区（内部字段）。</summary>
    private DesktopGroupVm? _group;

    /// <summary>多选模式的勾选顺序（键见 <see cref="KeyOf"/>），决定带回的顺序。</summary>
    private readonly List<string> _order = new();

    private string _emptySubtitle = "点一台设备选中，确定后继续";
    private string _multiSubtitleBase = "点卡片勾选，可多选";

    public ObservableCollection<PickerGroup> Groups { get; } = new();

    /// <summary>顶部分类筛选条：第一项固定「全部」，随后场景 / 各房间。</summary>
    public ObservableCollection<PickerTab> Tabs { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _title = "选择设备";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _footerHint = "";

    public string ConfirmText => _mode == TargetPickerMode.SingleDevice ? "确定" : "确定，带回所选";

    /// <summary>关窗回调：右上角关闭与点遮罩都走这里。</summary>
    public Action? CloseRequested { get; set; }

    public bool HasQuery => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsEmptyResult => Groups.Count == 0;

    public string EmptyText => _mode switch
    {
        TargetPickerMode.MultipleTargets => "没有匹配的设备或场景",
        _ => _active?.IsScene == true ? $"没有匹配的{_active.Title}" : "没有匹配的设备",
    };

    /// <summary>底部「全部移出」只在即时提交模式出现。</summary>
    public bool ShowClearAll => _mode == TargetPickerMode.ImmediateCommit;

    /// <summary>底部「取消 / 确定」在需要二次确认的模式出现。</summary>
    public bool ShowConfirm => _mode != TargetPickerMode.ImmediateCommit;

    public bool CanConfirm => _mode == TargetPickerMode.SingleDevice
        ? SelectedItem != null
        : _order.Count > 0;

    public string SelectionText
    {
        get
        {
            if (_mode == TargetPickerMode.ImmediateCommit) return $"已选 {CountSelection()} 项";
            if (_mode == TargetPickerMode.SingleDevice)
                return SelectedItem == null ? "还没选" : "已选 1 项";
            return _order.Count == 0 ? "还没选" : $"已选 {_order.Count} 项";
        }
    }

    // ===== 配置入口 =====

    /// <summary>桌面组件分区：设备 + 手动场景，勾选即时写回。</summary>
    public void ConfigureForGroup(DesktopGroupVm group)
    {
        _mode = TargetPickerMode.ImmediateCommit;
        Target = group;
        _group = group;
        _all.Clear();
        _order.Clear();

        foreach (var d in group.Main.AllDevices)
            _all.Add(new PickerItem { Device = d, IsIn = group.Contains(d.Did) });

        foreach (var s in group.Main.SceneItems)
            _all.Add(new PickerItem { Scene = s, IsIn = group.ContainsScene(s.Scene.SceneId) });

        FooterHint = "";
        Title = "添加设备 / 场景";
        NotifyMode();
        ApplyFilter();
        UpdateSubtitle();
    }

    /// <summary>触发条件 / 添加动作：单项设备。</summary>
    public void ConfigureSingleDevice(IEnumerable<MijiaDevice> devices, string? selectedDid,
        string emptySubtitle, string footerHint)
    {
        _mode = TargetPickerMode.SingleDevice;
        Target = null;
        _group = null;
        _all.Clear();
        _order.Clear();
        _emptySubtitle = emptySubtitle;
        FooterHint = footerHint;

        foreach (var d in devices)
            _all.Add(new PickerItem
            {
                Device = d,
                IsIn = selectedDid != null && MijiaScene.SameDid(d.Did, selectedDid),
            });

        Title = "选择设备";
        NotifyMode();
        ApplyFilter();
        UpdateSubtitle();
    }

    /// <summary>全局快捷键：设备 + 手动场景多选，勾选顺序即执行顺序。</summary>
    public void ConfigureMultiple(HotkeyTargetPickerRequest request)
    {
        _mode = TargetPickerMode.MultipleTargets;
        Target = null;
        _group = null;
        _all.Clear();
        _order.Clear();
        _multiSubtitleBase = "点卡片勾选，可多选；勾选顺序就是执行顺序";
        FooterHint = request.FooterHint;

        foreach (var d in request.Devices)
            _all.Add(new PickerItem { Device = d, IsIn = request.SelectedIds.Contains(d.Did) });

        foreach (var s in request.SceneCandidates)
            _all.Add(new PickerItem { Scene = s, IsIn = request.SelectedIds.Contains(s.Scene.SceneId) });

        // 预选顺序沿用上次保存的顺序
        foreach (var id in request.SelectedIds)
        {
            var item = _all.FirstOrDefault(i => IdOf(i) == id && i.IsIn);
            if (item != null) _order.Add(KeyOf(item));
        }

        Title = "选择控制目标";
        NotifyMode();
        ApplyFilter();
        UpdateSubtitle();
    }

    // ===== 结果 =====

    /// <summary>单选模式的当前选择。</summary>
    public PickerItem? SelectedItem => _all.FirstOrDefault(i => i.IsIn);

    public MijiaDevice? SelectedDevice => _mode == TargetPickerMode.SingleDevice ? SelectedItem?.Device : null;

    /// <summary>多选模式带回的控制卡片（默认动作：设备切换 / 场景执行 / 自动化开启）。</summary>
    public List<HotkeyStep> SelectedSteps
    {
        get
        {
            var steps = new List<HotkeyStep>();
            foreach (var key in _order)
            {
                var item = _all.FirstOrDefault(i => KeyOf(i) == key);
                if (item == null) continue;

                var type = TypeOf(item);
                steps.Add(new HotkeyStep
                {
                    Type = type,
                    Kind = DefaultKind(type),
                    Id = IdOf(item),
                    Name = item.Name,
                });
            }

            return steps;
        }
    }

    // ===== 交互 =====

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        ApplyFilter();
        UpdateSubtitle();
    }

    /// <summary>点一张卡片：三种模式三种语义。</summary>
    [RelayCommand]
    private void Toggle(PickerItem? item)
    {
        if (item == null) return;

        switch (_mode)
        {
            case TargetPickerMode.ImmediateCommit:
                if (_group == null) return;
                if (item.IsScene)
                {
                    if (item.IsIn) _group.RemoveScene(item.Scene!);
                    else _group.AddScene(item.Scene!);
                }
                else
                {
                    if (item.IsIn) _group.RemoveDevice(item.Device!);
                    else _group.AddDevice(item.Device!);
                }
                item.IsIn = !item.IsIn;
                UpdateSubtitle();
                break;

            case TargetPickerMode.SingleDevice:
                if (item.Device == null) return;
                foreach (var i in _all) i.IsIn = ReferenceEquals(i, item);
                UpdateSubtitle();
                break;

            default:
                if (item.IsIn)
                {
                    item.IsIn = false;
                    _order.Remove(KeyOf(item));
                }
                else
                {
                    item.IsIn = true;
                    if (!_order.Contains(KeyOf(item))) _order.Add(KeyOf(item));
                }
                UpdateSubtitle();
                break;
        }

        NotifySelection();
    }

    /// <summary>「全部移出」：把一个分区里的设备与场景一次清空（分区本身保留）。</summary>
    [RelayCommand]
    private void ClearAll()
    {
        if (_mode != TargetPickerMode.ImmediateCommit || _group == null) return;

        foreach (var item in _all.Where(i => i.IsIn).ToList())
        {
            if (item.IsScene) _group.RemoveScene(item.Scene!);
            else _group.RemoveDevice(item.Device!);
            item.IsIn = false;
        }

        UpdateSubtitle();
        NotifySelection();
    }

    /// <summary>点分类胶囊：只显示那一组（<paramref name="tab"/> 的分组为空就是「全部」）。</summary>
    public void SelectTab(PickerTab? tab)
    {
        _active = tab?.Group;
        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t.Group, _active);
        RebuildGroups();
    }

    private void NotifyMode()
    {
        OnPropertyChanged(nameof(ShowClearAll));
        OnPropertyChanged(nameof(ShowConfirm));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(EmptyText));
        NotifySelection();
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedItem));
    }

    private int CountSelection() => _all.Count(i => i.IsIn);

    private void UpdateSubtitle()
    {
        switch (_mode)
        {
            case TargetPickerMode.ImmediateCommit:
            {
                var devices = _group?.Devices.Count ?? 0;
                var scenes = _group?.Scenes.Count ?? 0;
                if (devices + scenes == 0)
                {
                    Subtitle = "勾选设备或场景加入这个分区";
                    break;
                }

                var parts = new List<string>();
                if (devices > 0) parts.Add($"{devices} 台设备");
                if (scenes > 0) parts.Add($"{scenes} 个场景");
                Subtitle = $"已选 {string.Join(" · ", parts)} · 再点一次可移出";
                break;
            }

            case TargetPickerMode.SingleDevice:
                Subtitle = SelectedItem?.Name is { } name ? $"已选「{name}」，点确定继续" : _emptySubtitle;
                break;

            default:
                Subtitle = _order.Count > 0
                    ? $"{_multiSubtitleBase} · 已选 {_order.Count} 项"
                    : _multiSubtitleBase;
                break;
        }
    }

    // ===== 分组与筛选 =====

    private void ApplyFilter()
    {
        var q = SearchText.Trim();

        _rooms.Clear();
        foreach (var g in _all
                     .Where(i => i.Device != null)
                     .Where(i => q.Length == 0
                                 || i.Device!.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || i.Device!.RoomName.Contains(q, StringComparison.OrdinalIgnoreCase))
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

        var scenes = _all
            .Where(i => i.Scene != null)
            .Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.IsIn)
            .ThenBy(i => i.Name, StringComparer.CurrentCulture)
            .ToList();

        // 场景组只要列表里有就留着（搜空了也显示一组空态，和桌面组件弹窗一致）
        _sceneGroup = _all.Any(i => i.Scene != null)
            ? new PickerGroup(SceneGroupTitle, scenes, true)
            : null;

        _active = null;
        RebuildTabs();
        RebuildGroups();
    }

    private void RebuildTabs()
    {
        Tabs.Clear();
        Tabs.Add(new PickerTab(null, "全部", true));
        if (_sceneGroup != null) Tabs.Add(new PickerTab(_sceneGroup, SceneGroupTitle, false));
        foreach (var room in _rooms)
            Tabs.Add(new PickerTab(room, room.Title, false));
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        if (_active is null)
        {
            foreach (var room in _rooms) Groups.Add(room);
            if (_sceneGroup != null) Groups.Add(_sceneGroup);
        }
        else
        {
            Groups.Add(_active);
        }

        OnPropertyChanged(nameof(IsEmptyResult));
        OnPropertyChanged(nameof(EmptyText));
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

    private static string IdOf(PickerItem item) => item.Device?.Did ?? item.Scene?.Scene.SceneId ?? "";

    private static string KeyOf(PickerItem item) => $"{TypeOf(item)}:{IdOf(item)}";

    private static HotkeyTargetType TypeOf(PickerItem item)
        => item.Device != null ? HotkeyTargetType.Device : HotkeyTargetType.Scene;

    private static HotkeyActionKind DefaultKind(HotkeyTargetType type) => type switch
    {
        HotkeyTargetType.Scene => HotkeyActionKind.RunScene,
        _ => HotkeyActionKind.ToggleDevice,
    };
}

/// <summary>快捷键「选择控制目标」的入参。</summary>
public sealed class HotkeyTargetPickerRequest
{
    public List<MijiaDevice> Devices { get; init; } = new();
    public List<SceneCardItem> SceneCandidates { get; init; } = new();

    /// <summary>打开弹窗时要预先勾上的目标 id（did / scene_id），顺序即已选顺序。</summary>
    public List<string> SelectedIds { get; init; } = new();

    public string FooterHint { get; init; } = "确定后回到编辑卡，再逐张选择打开 / 关闭 / 执行";
}

/// <summary>弹窗里的一条设备或场景：点整卡即切换选中，选中态由卡面底色、描边与名称色表达。</summary>
public partial class PickerItem : ObservableObject
{
    public MijiaDevice? Device { get; init; }

    public SceneCardItem? Scene { get; init; }

    public bool IsScene => Scene != null;

    /// <summary>设备头像是网络图：图标与占位几何二选一，场景卡两者都不显示（走场景自带图标）。</summary>
    public bool HasPhoto => Device?.Icon != null;

    public bool HasGlyph => Device is { Icon: null };

    public string Name => Scene?.Name ?? Device?.Name ?? "";

    /// <summary>卡片副标题：设备类别（房间已经做成分组标题了，副标题再写房间是重复信息）。</summary>
    public string Category => Scene != null
        ? TargetPickerVm.SceneGroupTitle
        : TargetPickerVm.CategoryOf(Device!);

    [ObservableProperty] private bool _isIn;
}

/// <summary>顶部分类直达条上的一枚胶囊。<see cref="Group"/> 为 null 表示「全部」。</summary>
public partial class PickerTab : ObservableObject
{
    public PickerTab(PickerGroup? group, string title, bool isActive)
    {
        Group = group;
        Title = title;
        _isActive = isActive;
    }

    public PickerGroup? Group { get; }

    public string Title { get; }

    [ObservableProperty] private bool _isActive;
}

/// <summary>一个分组（标题 = 房间名 / 场景 + 该组的卡片）。</summary>
public sealed class PickerGroup
{
    public PickerGroup(string title, IEnumerable<PickerItem> items, bool isScene = false)
    {
        Title = title;
        Items = new ObservableCollection<PickerItem>(items);
        IsScene = isScene;
    }

    public string Title { get; }

    public bool IsScene { get; }

    public ObservableCollection<PickerItem> Items { get; }

    public string CountText => Items.Count.ToString();
}
