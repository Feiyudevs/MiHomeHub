// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MiHomeHub.Services;

namespace MiHomeHub.Models;

/// <summary>房间筛选芯片：可选中，选中态由 VM 统一维护；每个房间按名称匹配一枚图标 key。</summary>
public sealed partial class RoomChip : ObservableObject
{
    public RoomChip(string name)
    {
        Name = name;
        IconKey = ResolveIcon(name);
        _customIcon = CustomIconStore.Get(CustomIconStore.RoomScope, name);
    }

    public string Name { get; }

    /// <summary>「全部」是伪房间，固定在第一行：不参与拖拽排序，悬停也不亮抓手指柄。</summary>
    public bool IsLocked => Name == "全部";

    /// <summary>图标 key，对应 Assets/Nav/&lt;key&gt;.svg，见 <see cref="Services.NavIcons"/>。</summary>
    public string IconKey { get; }

    /// <summary>用户给这个房间设的自定义图标；null = 用按房间名匹配的默认图标。</summary>
    [ObservableProperty] private CustomIcon? _customIcon;

    public bool HasCustomIcon => CustomIcon is not null;

    /// <summary>侧栏导航行模板的 Tag：把默认 key 与自定义图标打包给转换器判断显示哪一套。</summary>
    public NavIconRef IconTag => new() { Key = IconKey, Custom = CustomIcon };

    /// <summary>默认 SVG / 细线图标的不透明度：有自定义图标时整组隐到 0，让位给用户那张图。</summary>
    public double DefaultIconOpacity => HasCustomIcon ? 0 : 1;

    partial void OnCustomIconChanged(CustomIcon? value)
    {
        OnPropertyChanged(nameof(HasCustomIcon));
        OnPropertyChanged(nameof(IconTag));
        OnPropertyChanged(nameof(DefaultIconOpacity));
    }

    /// <summary>设置弹窗改完图标后从存储里重新取一枚（存储事件统一走这里刷新）。</summary>
    public void ReloadCustomIcon() =>
        CustomIcon = CustomIconStore.Get(CustomIconStore.RoomScope, Name);

    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// 这个房间里还有灯开着（在线且开，与首页「灯光」统计卡同一口径）：
    /// 侧栏在房间名右侧点亮一枚小灯泡，一眼看出哪个房间忘了关灯。
    /// 伪房间「全部」永不点亮 —— 它不指某一间房。
    /// </summary>
    [ObservableProperty] private bool _hasLightOn;

    /// <summary>按房间名关键词匹配图标，覆盖米家返回的常见房型命名。</summary>
    internal static string ResolveIcon(string name)
    {
        if (Has(name, "全部", "全屋", "所有", "整个"))
            return "house";

        // 顺序敏感：先匹配「洗手间」再匹配「洗手区」等高相似词。
        if (Has(name, "洗手间", "卫生间", "主卫", "次卫", "客卫", "公卫", "厕所", "马桶", "卫浴", "淋浴"))
            return "shower";
        if (Has(name, "浴室", "浴缸"))
            return "tub";
        if (Has(name, "洗手", "盥洗", "洗漱", "台盆"))
            return "sink";
        if (Has(name, "洗衣"))
            return "washer";
        if (Has(name, "厨"))
            return "pot";
        if (Has(name, "餐"))
            return "dining";
        if (Has(name, "茶"))
            return "cup";
        if (Has(name, "客厅", "起居"))
            return "sofa";
        if (Has(name, "书", "阅读", "办公", "学习"))
            return "book";
        if (Has(name, "卧", "儿童房", "老人房", "客房", "保姆房", "婴儿房", "睡"))
            return "bed";
        if (Has(name, "阳台", "露台", "飘窗", "窗"))
            return "window";
        if (Has(name, "衣帽", "更衣", "衣柜", "衣橱"))
            return "hanger";
        if (Has(name, "储", "杂", "仓库", "设备间", "机房", "配电"))
            return "box";
        if (Has(name, "影音", "影院", "娱乐", "游戏", "视听"))
            return "screen";
        if (Has(name, "健身", "运动", "瑜伽"))
            return "dumbbell";
        if (Has(name, "车库", "车位"))
            return "car";
        if (Has(name, "花园", "庭院", "院子", "菜园"))
            return "plant";
        if (Has(name, "楼梯"))
            return "stairs";
        if (Has(name, "玄关", "门厅", "门口", "入户", "走廊", "过道"))
            return "door";
        return "house";
    }

    private static bool Has(string name, params string[] keys) =>
        keys.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
}

/// <summary>一次「换图标」请求：房间按名称、场景按 scene_id、设备按 did，Label 用作弹窗标题。</summary>
/// <param name="FromEditor">true = 从场景编辑弹窗里发起；独立场景窗要用自己的窗口当宿主，主窗口据此让路。</param>
public sealed record CustomIconTarget(string Scope, string Key, string Label, bool FromEditor = false)
{
    public bool IsRoom => Scope == CustomIconStore.RoomScope;
    public bool IsDevice => Scope == CustomIconStore.DeviceScope;
}

/// <summary>侧栏导航行 Tag 的图标识描述：默认 key + 可选的自定义图标。
/// 转换器同时兼容裸字符串（首页 / 智能 / 快捷键那三行仍直接写 key）。</summary>
public sealed class NavIconRef
{
    public required string Key { get; init; }
    public CustomIcon? Custom { get; init; }
}

/// <summary>标题栏家庭下拉项：一次 homelist 拉取后固定，选中后按 Id 重拉该家庭设备。</summary>
public sealed partial class HomeOption : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required long Uid { get; init; }

    /// <summary>当前展示中的家庭，驱动下拉菜单勾选态。</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>下拉项的最小契约：OptionPicker 只认这几样，规格下拉项（SpecOptionVm）也能直接当选项用。</summary>
public interface IPickOption
{
    string Label { get; }
    object Value { get; }
    bool IsSelected { get; set; }

    /// <summary>行尾的弱色补充文字（如音箱所在房间），没有就不占位。</summary>
    string? Meta { get; }
}

/// <summary>自绘下拉项：Value 是与调用方状态直接比对的任意值（bool、字符串…）。</summary>
public sealed partial class OptionItem : ObservableObject, IPickOption
{
    public required string Label { get; init; }
    public required object Value { get; init; }

    /// <summary>见 <see cref="IPickOption.Meta"/>：同名的设备靠它区分。</summary>
    public string? Meta { get; init; }

    /// <summary>当前选中项，由 OptionPicker 统一维护，驱动浮层的柔色底。</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>首页按房间分组的设备区块。</summary>
public sealed partial class DeviceGroup : ObservableObject
{
    public required string Name { get; init; }
    public required int Total { get; init; }
    public required List<MijiaDevice> Items { get; init; }
    public string TitleText => $"{Name} · {Items.Count} 台设备";

    /// <summary>房间图标 key：与侧栏房间同一套匹配规则，标题左侧显示的图标因此两处一致。</summary>
    public string IconKey => RoomChip.ResolveIcon(Name);

    /// <summary>用户给这个房间设的自定义图标；null = 继续用默认线性图标。</summary>
    [ObservableProperty] private CustomIcon? _customIcon;

    public bool HasCustomIcon => CustomIcon is not null;

    /// <summary>默认图标的不透明度：有自定义图标时隐到 0。</summary>
    public double DefaultIconOpacity => HasCustomIcon ? 0 : 1;

    partial void OnCustomIconChanged(CustomIcon? value)
    {
        OnPropertyChanged(nameof(HasCustomIcon));
        OnPropertyChanged(nameof(DefaultIconOpacity));
    }

    /// <summary>自定义图标变更后同步到首页这一组标题。</summary>
    public void ReloadCustomIcon() =>
        CustomIcon = CustomIconStore.Get(CustomIconStore.RoomScope, Name);

    /// <summary>该房间是否展开：点标题行切换，收起后卡片全部让位，只留一行标题。</summary>
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>
    /// 这个房间里还亮着的灯数（在线且开，与侧栏小灯泡、「灯光」统计卡同一口径）。
    /// 首页房间标题据此显示「N 盏灯亮着」和「一键全关」；为 0 时整组收起不占位。
    /// </summary>
    [ObservableProperty] private int _lightOnCount;

    public bool HasLightOn => LightOnCount > 0;

    public string LightOnText => $"{LightOnCount} 盏灯亮着";

    partial void OnLightOnCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasLightOn));
        OnPropertyChanged(nameof(LightOnText));
    }

    /// <summary>房间卡片区是否可见（收起时整块不参与布局）。</summary>
    public bool ItemsVisible => IsExpanded;

    /// <summary>折叠箭头角度：展开朝下（0），收起朝右（-90），与「已隐藏」区一致。</summary>
    public double ChevronAngle => IsExpanded ? 0 : -90;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ItemsVisible));
        OnPropertyChanged(nameof(ChevronAngle));
    }
}

/// <summary>场景卡包装：按名称关键词分配装饰色与图标（米家接口不返回这些）。</summary>
public sealed partial class SceneCardItem : ObservableObject
{
    public required MijiaScene Scene { get; init; }
    public string Name => Scene.Name;

    public bool IsAutomation => Scene.IsAutomation;

    /// <summary>卡片主说明：自动化优先展示触发条件，手动场景展示动作摘要。</summary>
    public string Summary => IsAutomation
        ? (string.IsNullOrWhiteSpace(Scene.TriggerSummary) ? Scene.ActionSummary : Scene.TriggerSummary)
        : Scene.ActionSummary;

    /// <summary>卡片次说明：自动化补充动作摘要，手动场景补充动作条数。</summary>
    public string Detail => IsAutomation
        ? Scene.ActionSummary
        : (Scene.ActionCount > 0 ? $"{Scene.ActionCount} 个动作" : "无动作");

    /// <summary>启停态（自动化可切换；切换时先乐观更新，失败回滚）。</summary>
    [ObservableProperty] private bool _isEnabled;

    /// <summary>设备详情里的角色标签（触发 / 控制），列表页为空表示不显示。</summary>
    [ObservableProperty] private string _roleText = "";
    public bool HasRole => !string.IsNullOrEmpty(RoleText);

    partial void OnRoleTextChanged(string value) => OnPropertyChanged(nameof(HasRole));

    public required Geometry Icon { get; init; }

    /// <summary>用户给这条场景设的自定义图标；null = 用按名称匹配的默认线性图标。</summary>
    [ObservableProperty] private CustomIcon? _customIcon;

    public bool HasCustomIcon => CustomIcon is not null;

    /// <summary>默认线性图标的不透明度：有自定义图标时隐到 0。</summary>
    public double DefaultIconOpacity => HasCustomIcon ? 0 : 1;

    partial void OnCustomIconChanged(CustomIcon? value)
    {
        OnPropertyChanged(nameof(HasCustomIcon));
        OnPropertyChanged(nameof(DefaultIconOpacity));
    }

    /// <summary>自定义图标变更后从存储重取；各列表持有的是同一批实例，一次刷新全局生效。</summary>
    public void ReloadCustomIcon() =>
        CustomIcon = CustomIconStore.Get(CustomIconStore.SceneScope, Scene.SceneId);

    /// <summary>卡片右上光晕渐变（场景色低透明度）。</summary>
    public required IBrush GlowBrush { get; init; }
    public required IBrush IconBgBrush { get; init; }
    public required IBrush IconFgBrush { get; init; }
    [ObservableProperty] private bool _isRunning;

    /// <summary>在设备维度（抽屉 / 按设备分组）里绑定的设备 did；null 表示整条场景视角。</summary>
    [ObservableProperty] private string? _targetDid;

    /// <summary>能否「只执行本机动作」：绑定了设备，且场景里确有可直接下发的属性写入。</summary>
    public bool CanRunOnDevice => TargetDid != null && Scene.CanExecuteOnDevice(TargetDid);

    /// <summary>本机在这条场景里可下发的属性条数（按钮提示用）。</summary>
    public int DeviceActionCount => TargetDid == null ? 0 : Scene.WritableCountForDevice(TargetDid);

    public string DeviceRunTip => DeviceActionCount > 0
        ? $"只执行本机 {DeviceActionCount} 项动作"
        : "只执行本机动作";

    partial void OnTargetDidChanged(string? value)
    {
        OnPropertyChanged(nameof(CanRunOnDevice));
        OnPropertyChanged(nameof(DeviceActionCount));
        OnPropertyChanged(nameof(DeviceRunTip));
    }

    private static readonly string[] Palette =
    {
        "#9FA8FF", "#7FABFF", "#C7BFFF", "#818CF8", "#FCD34D",
        "#F87171", "#4ADE80", "#22D3EE", "#A78BFA"
    };

    private static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static SceneCardItem Wrap(MijiaScene scene, int index)
    {
        var hex = Palette[index % Palette.Length];
        var color = Color.Parse(hex);
        var glow = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Alpha(color, 0x33), 0),
                new GradientStop(Alpha(color, 0x14), 0.5),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)
            }
        };
        return new SceneCardItem
        {
            Scene = scene,
            IsEnabled = scene.Enable,
            Icon = SceneIconFor(scene.Name),
            CustomIcon = CustomIconStore.Get(CustomIconStore.SceneScope, scene.SceneId),
            GlowBrush = glow,
            IconBgBrush = new SolidColorBrush(Alpha(color, 0x26)),
            IconFgBrush = new SolidColorBrush(color)
        };
    }

    // 默认线稿统一由 SceneIcons 出：列表卡片 / 首页快捷场景 / 桌面分区 / 编辑弹窗预览同源，
    // 换图、改名后两处画法仍然一致，不会出现「列表一种、弹窗另一种」。
    public static Geometry SceneIconFor(string name) => SceneIcons.For(name);
}

/// <summary>环境概览明细里的一行读数（设备名 + 房间 + 「26.5℃ 48%」）。</summary>
public sealed class EnvReadingItem
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    /// <summary>主读数：优先温度，没有温度时退回湿度。</summary>
    public required string Value { get; init; }
    /// <summary>次要读数：温湿度同时存在时补湿度，其余情况为空。</summary>
    public string Extra { get; init; } = "";
}

/// <summary>环境概览明细里的一条耗材（跨设备的滤芯/刷头汇总）。</summary>
public sealed class ConsumableItem
{
    public required string DeviceName { get; init; }
    public required string Desc { get; init; }
    public required string ValueText { get; init; }
    /// <summary>0–100 的余量，未知为 -1；用于「不足的排前面、低的更靠前」排序。</summary>
    public int Percent { get; init; } = -1;
    public bool IsLow { get; init; }
    /// <summary>行内进度条填充宽度：固定 92px 轨道按百分比取宽，圆角由两端 Border 自己画，不走 ProgressBar 模板。</summary>
    public double BarWidth => Percent < 0 ? 0 : Math.Clamp(Percent, 0, 100) * 92.0 / 100.0;
}

/// <summary>「数据看板」编辑弹窗里的一行：一张卡的两个去处——首页看板、桌面组件。
/// 界面只改 <see cref="IsOn"/> / <see cref="IsOnDesktop"/>，落盘与重算交回 ViewModel 的回调，各卡不直接碰设置。</summary>
public sealed partial class TrendCardOptionItem : ObservableObject
{
    private readonly Action<TrendCardOptionItem>? _onToggled;
    private readonly Action<TrendCardOptionItem>? _onDesktopToggled;
    private bool _mute;
    private bool _muteDesktop;

    public TrendCardOptionItem(string key, string title, string desc,
        Action<TrendCardOptionItem>? onToggled = null,
        Action<TrendCardOptionItem>? onDesktopToggled = null)
    {
        Key = key;
        Title = title;
        Desc = desc;
        _onToggled = onToggled;
        _onDesktopToggled = onDesktopToggled;
    }

    /// <summary>设置里认的键：temp / hum / power / water / gas / tds。</summary>
    public string Key { get; }

    public string Title { get; }

    /// <summary>这张卡到底画的是什么，一句话说明。</summary>
    public string Desc { get; }

    /// <summary>家里有没有能给这张卡提供读数的设备；没有也照样铺，首页和桌面都显示示例数据。</summary>
    [ObservableProperty] private bool _hasDevice;

    /// <summary>弹窗里据此在卡下留一句「暂无设备 · 显示示例数据」。</summary>
    public bool IsMissingDevice => !HasDevice;

    partial void OnHasDeviceChanged(bool value) => OnPropertyChanged(nameof(IsMissingDevice));

    /// <summary>首页看板整块被隐藏时，「首页」开关先停用：点了也看不见，不如直接说清楚。</summary>
    [ObservableProperty] private bool _homeEnabled = true;

    /// <summary>是否出现在首页（和开关 TwoWay 绑定）。</summary>
    [ObservableProperty] private bool _isOn;

    /// <summary>是否已经放到桌面组件（独立开关，和首页显隐互不影响）。</summary>
    [ObservableProperty] private bool _isOnDesktop;

    partial void OnIsOnChanged(bool value)
    {
        if (_mute) return;
        _onToggled?.Invoke(this);
    }

    partial void OnIsOnDesktopChanged(bool value)
    {
        if (_muteDesktop) return;
        _onDesktopToggled?.Invoke(this);
    }

    /// <summary>打开弹窗时同步一次已有选择：静默改值，不触发回调（否则会把「跟随设备」固化成手动选择）。</summary>
    public void SyncOn(bool on)
    {
        _mute = true;
        IsOn = on;
        _mute = false;
    }

    /// <summary>同步桌面组件上的现状：同样静默，避免把一次打开弹窗写成增删操作。</summary>
    public void SyncDesktop(bool on)
    {
        _muteDesktop = true;
        IsOnDesktop = on;
        _muteDesktop = false;
    }
}
