using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MiHome.Services;

namespace MiHome.Models;

/// <summary>房间筛选芯片：可选中，选中态由 VM 统一维护；每个房间按名称匹配一枚图标 key。</summary>
public sealed partial class RoomChip : ObservableObject
{
    public RoomChip(string name)
    {
        Name = name;
        IconKey = ResolveIcon(name);
    }

    public string Name { get; }

    /// <summary>图标 key，对应 Assets/Nav/&lt;key&gt;.svg，见 <see cref="Services.NavIcons"/>。</summary>
    public string IconKey { get; }

    [ObservableProperty] private bool _isSelected;

    /// <summary>按房间名关键词匹配图标，覆盖米家返回的常见房型命名。</summary>
    private static string ResolveIcon(string name)
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
public sealed class DeviceGroup
{
    public required string Name { get; init; }
    public required int Total { get; init; }
    public required List<MijiaDevice> Items { get; init; }
    public string TitleText => $"{Name} · {Items.Count} 台设备";
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
            GlowBrush = glow,
            IconBgBrush = new SolidColorBrush(Alpha(color, 0x26)),
            IconFgBrush = new SolidColorBrush(color)
        };
    }

    // 整圆同样用两段半圆弧拼，避免闭合弧被解析成空路径。
    private static Geometry SceneIconFor(string name)
    {
        string data = name switch
        {
            _ when name.Contains("回家") || name.Contains("到家") => "M3,10.5 L12,3 L21,10.5 M5.5,9.5 V20 H18.5 V9.5 M9.5,20 V14 H14.5 V20",
            _ when name.Contains("离家") || name.Contains("外出") || name.Contains("出门") => "M14,4 H19 V20 H14 M3,12 H14 M10,8 L14,12 L10,16",
            _ when name.Contains("睡眠") || name.Contains("晚安") || name.Contains("睡觉") => "M20,14.5 A8.5,8.5 0 1 1 9.5,4 A7,7 0 0 0 20,14.5 Z",
            _ when name.Contains("起床") || name.Contains("早晨") || name.Contains("早安") => "M12,12 m-4,0 a4,4 0 1 0 8,0 a4,4 0 1 0 -8,0 M12,2 V5 M12,19 V22 M2,12 H5 M19,12 H22 M4.9,4.9 L7,7 M17,17 L19.1,19.1 M19.1,4.9 L17,7 M7,17 L4.9,19.1",
            _ when name.Contains("清扫") || name.Contains("扫地") || name.Contains("清洁") => "M12,12 m-9,0 a9,9 0 1 0 18,0 a9,9 0 1 0 -18,0 M12,12 m-3.5,0 a3.5,3.5 0 1 0 7,0 a3.5,3.5 0 1 0 -7,0 M12,3 V6 M12,18 V21 M3,12 H6 M18,12 H21",
            _ when name.Contains("净化") || name.Contains("空气") || name.Contains("新风") => "M12,3 S18,9.5 18,14 A6,6 0 1 1 6,14 C6,9.5 12,3 12,3 Z",
            _ when name.Contains("观影") || name.Contains("影院") => "M3,5 H21 V19 H3 Z M8,5 V19 M16,5 V19 M3,10 H8 M16,10 H21 M3,15 H8 M16,15 H21",
            _ when name.Contains("布防") || name.Contains("安防") || name.Contains("门锁") => "M12,22 S20,18 20,12 V5 L12,2 L4,5 V12 C4,18 12,22 12,22 Z M9,12 L11,14 L15,10",
            _ => "M12,3 L13.9,8.6 L19.5,10.5 L13.9,12.4 L12,18 L10.1,12.4 L4.5,10.5 L10.1,8.6 Z M19,15 L19.9,17.6 L22.5,18.5 L19.9,19.4 L19,22 L18.1,19.4 L15.5,18.5 L18.1,17.6 Z"
        };
        return Geometry.Parse(data);
    }
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
}
