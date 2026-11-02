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
/// 添加设备/场景弹窗：全量设备与场景 + 搜索 + 勾选；勾选即时写回分区，没有「确定」二次提交。
/// 顶部是分类筛选条（不是滚动定位）：点「全部房间」列出所有匹配到的房间，点某个房间或「场景」下面只显示那一组。
/// 搜索是跨全部房间的，所以改搜索词会退回「全部房间」，免得命中被筛选条藏起来。
/// 设备按**房间**分组（与主窗口侧栏的房间芯片同序：设备多的房间在前，未分配房间垫底），卡片副标题显示设备类别。
/// </summary>
public partial class DesktopPickerVm : ObservableObject
{
    private const string NoRoom = "未分配房间";

    /// <summary>场景组的标题，同时也是直达条上那枚胶囊的文案。</summary>
    public const string SceneGroupTitle = "场景";

    private readonly List<PickerItem> _all = new();
    private readonly List<PickerItem> _allScenes = new();

    /// <summary>当前搜索词命中的各房间分组（与显示与否无关，筛选条要点哪个才显示哪个）。</summary>
    private readonly List<PickerGroup> _rooms = new();

    private PickerGroup? _sceneGroup;

    /// <summary>正在显示的那一组；null = 「全部房间」（此时 <see cref="Groups"/> 是全部房间）。</summary>
    private PickerGroup? _active;

    public ObservableCollection<PickerGroup> Groups { get; } = new();

    /// <summary>顶部分组筛选条：第一项固定是「全部房间」，随后是场景（有场景才有）与各房间。</summary>
    public ObservableCollection<PickerTab> Tabs { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _subtitle = "勾选即加入分区";

    public DesktopGroupVm? Target { get; private set; }

    /// <summary>关窗回调：由窗口挂上，右上角关闭与点遮罩都走这里。</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>搜索框右边的「清除」是否显示。</summary>
    public bool HasQuery => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>搜不到任何内容时显示空态。</summary>
    public bool IsEmptyResult => Groups.Count == 0;

    /// <summary>空态文案跟着当前筛选走：选的是场景就说场景。</summary>
    public string EmptyText => _active?.IsScene == true ? "没有匹配的场景" : "没有匹配的设备";

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    public void Bind(DesktopGroupVm target)
    {
        Target = target;
        _all.Clear();
        _allScenes.Clear();

        foreach (var d in target.Main.AllDevices)
        {
            _all.Add(new PickerItem
            {
                Device = d,
                IsIn = target.Contains(d.Did)
            });
        }

        foreach (var s in target.Main.SceneItems)
        {
            _allScenes.Add(new PickerItem
            {
                Scene = s,
                IsIn = target.ContainsScene(s.Scene.SceneId)
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

    /// <summary>按搜索词重建各组；分区里已有的内容在组内排前面，方便直接取消。词变了就退回「全部房间」。</summary>
    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        var matched = _all.Where(item =>
            q.Length == 0
            || item.Device!.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || item.Device!.RoomName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _rooms.Clear();

        // 房间顺序与主窗口侧栏一致：设备多的房间靠前，「未分配房间」永远垫底
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

        // 有场景可挑就一直留着这枚入口，搜空了也只是那组显示空态
        _sceneGroup = _allScenes.Count == 0 ? null : new PickerGroup(SceneGroupTitle, _allScenes
            .Where(i => q.Length == 0 || i.Scene!.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.IsIn)
            .ThenBy(i => i.Scene!.Name, StringComparer.CurrentCulture), true);

        _active = null;
        RebuildTabs();
        RebuildGroups();
    }

    /// <summary>重建分组筛选条：「全部房间」永远在第一位，场景入口紧跟其后，再是各房间。</summary>
    private void RebuildTabs()
    {
        Tabs.Clear();
        Tabs.Add(new PickerTab(null, "全部房间", _active is null));
        if (_sceneGroup != null)
            Tabs.Add(new PickerTab(_sceneGroup, SceneGroupTitle, ReferenceEquals(_active, _sceneGroup)));
        foreach (var room in _rooms)
            Tabs.Add(new PickerTab(room, room.Title, ReferenceEquals(_active, room)));
    }

    /// <summary>点分类胶囊：只显示那一组（<paramref name="tab"/> 的分组为空就是「全部房间」）。</summary>
    public void SelectTab(PickerTab? tab)
    {
        _active = tab?.Group;
        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t.Group, _active);
        RebuildGroups();
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

    private void UpdateSubtitle()
    {
        var devices = Target?.Devices.Count ?? 0;
        var scenes = Target?.Scenes.Count ?? 0;
        if (devices + scenes == 0)
        {
            Subtitle = "勾选设备或场景加入这个分区";
            return;
        }

        var parts = new List<string>();
        if (devices > 0) parts.Add($"{devices} 台设备");
        if (scenes > 0) parts.Add($"{scenes} 个场景");
        Subtitle = $"已选 {string.Join(" · ", parts)} · 再点一次可移出";
    }

    [RelayCommand]
    private void Toggle(PickerItem? item)
    {
        if (item == null || Target == null) return;
        if (item.IsScene)
        {
            if (item.IsIn) Target.RemoveScene(item.Scene!);
            else Target.AddScene(item.Scene!);
        }
        else
        {
            if (item.IsIn) Target.RemoveDevice(item.Device!);
            else Target.AddDevice(item.Device!);
        }
        item.IsIn = !item.IsIn;
        UpdateSubtitle();
    }

    /// <summary>全部移出：把一个分区里的设备与场景一次清空（分区本身保留）。</summary>
    [RelayCommand]
    private void ClearAll()
    {
        if (Target == null) return;
        foreach (var item in _all.Concat(_allScenes).ToList())
        {
            if (!item.IsIn) continue;
            if (item.IsScene) Target.RemoveScene(item.Scene!);
            else Target.RemoveDevice(item.Device!);
            item.IsIn = false;
        }
        UpdateSubtitle();
    }
}

/// <summary>弹窗里的一条设备或场景：点整卡即切换「是否已加入分区」，选中态由卡面主色表达。</summary>
public partial class PickerItem : ObservableObject
{
    public MijiaDevice? Device { get; init; }

    public SceneCardItem? Scene { get; init; }

    public bool IsScene => Scene != null;

    /// <summary>设备头像是网络图：图标与占位几何二选一，场景卡两者都不显示。</summary>
    public bool HasPhoto => Device?.Icon != null;

    public bool HasGlyph => Device is { Icon: null };

    public string Name => Scene?.Name ?? Device?.Name ?? "";

    /// <summary>卡片副标题：设备类别（房间已经做成分组标题了，副标题再写房间是重复信息）。</summary>
    public string Category => Scene != null ? DesktopPickerVm.SceneGroupTitle : DesktopPickerVm.CategoryOf(Device!);

    [ObservableProperty] private bool _isIn;
}

/// <summary>顶部分组直达条上的一枚胶囊。<see cref="Group"/> 为 null 表示「全部房间」。</summary>
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

/// <summary>一个分组（标题 = 房间名或「场景」 + 该组的卡片）。</summary>
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
