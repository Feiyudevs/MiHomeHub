using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MijiaPanel.Models;

/// <summary>房间筛选芯片：可选中，选中态由 VM 统一维护；每个房间配一枚细线圆角图标。</summary>
public sealed partial class RoomChip : ObservableObject
{
    // 统一 24 栅格、只描边不填充，配合模板里的 RoundLineCap/RoundJoin 呈现圆润细线风格。
    private const string House = "M4 10.8 L12 4 L20 10.8 V19 A1.6 1.6 0 0 1 18.4 20.6 H5.6 A1.6 1.6 0 0 1 4 19 Z M9.8 20.6 V14.4 A0.9 0.9 0 0 1 10.7 13.5 H13.3 A0.9 0.9 0 0 1 14.2 14.4 V20.6";
    private const string Sofa = "M6.5 12.5 V9.5 A1.5 1.5 0 0 1 8 8 H16 A1.5 1.5 0 0 1 17.5 9.5 V12.5 M3 12.5 A1.5 1.5 0 0 1 4.5 11 H19.5 A1.5 1.5 0 0 1 21 12.5 V16.5 A1.5 1.5 0 0 1 19.5 18 H4.5 A1.5 1.5 0 0 1 3 16.5 Z M9.5 13 V17 M14.5 13 V17 M6 18 V20 M18 18 V20";
    private const string Dining = "M8.5 3 V7 A2.5 2.5 0 0 0 13.5 7 V3 M11 7 V21 M16.5 21 V3.5 M16.5 3.5 C19.4 4.6 19.9 8.4 16.5 11";
    private const string Bed = "M3.5 20 V6.5 M3.5 14 H20.5 V17.5 H3.5 M6 17.5 V20 M18 17.5 V20 M7 14 V11 A1.5 1.5 0 0 1 8.5 9.5 H12 A1.5 1.5 0 0 1 13.5 11 V14";
    private const string Book = "M12 6.6 C10.4 5.1 7.6 4.6 4.5 5.2 A0.9 0.9 0 0 0 3.8 6.1 V18.1 A0.9 0.9 0 0 0 4.9 19 C7.8 18.4 10.4 18.9 12 20.2 C13.6 18.9 16.2 18.4 19.1 19 A0.9 0.9 0 0 0 20.2 18.1 V6.1 A0.9 0.9 0 0 0 19.5 5.2 C16.4 4.6 13.6 5.1 12 6.6 Z M12 6.6 V20.2";
    private const string Pot = "M4.5 12 H19.5 V15 A4.5 4.5 0 0 1 15 19.5 H9 A4.5 4.5 0 0 1 4.5 15 Z M6.5 9 H17.5 M12 9 V7 M4.5 13.7 H2.8 M19.5 13.7 H21.2 M9.2 5.4 C9.2 4.3 8.3 3.8 8.3 2.8 M13.2 5.4 C13.2 4.3 12.3 3.8 12.3 2.8";
    private const string Tub = "M3.5 12.5 H20.5 V15.3 A4.6 4.6 0 0 1 15.9 19.9 H8.1 A4.6 4.6 0 0 1 3.5 15.3 Z M6.5 12.5 V7.2 A2.3 2.3 0 0 1 11.1 7.2 V11.5";
    private const string Shower = "M5.5 8.5 A6.5 6.5 0 0 1 18.5 8.5 Z M8 11.5 V14 M12 11.5 V14 M16 11.5 V14 M10 17 V18.5 M14 17 V18.5";
    private const string Sink = "M3.5 10.5 H20.5 V12.6 A4.4 4.4 0 0 1 16.1 17 H7.9 A4.4 4.4 0 0 1 3.5 12.6 Z M9 17 V20.5 M15 17 V20.5 M12 10.5 V6 A2 2 0 0 1 16 6 V7.5";
    private const string Door = "M6 20.5 V5 A1.5 1.5 0 0 1 7.5 3.5 H16.5 A1.5 1.5 0 0 1 18 5 V20.5 M4 20.5 H20 M14.6 12 V12.1";
    private const string Window = "M4.5 4 H19.5 A1.5 1.5 0 0 1 21 5.5 V18.5 A1.5 1.5 0 0 1 19.5 20 H4.5 A1.5 1.5 0 0 1 3 18.5 V5.5 A1.5 1.5 0 0 1 4.5 4 Z M3 12 H21 M12 4 V20";
    private const string Stairs = "M4 20 H8 V16 H12 V12 H16 V8 H20";
    private const string Hanger = "M12 7.4 V5.6 C12 4.2 13.1 3.6 14 4.4 M12 7.4 L4 14 A1.6 1.6 0 0 0 5 17 H19 A1.6 1.6 0 0 0 20 14 L12 7.4";
    private const string Box = "M3.8 8.2 L12 4 L20.2 8.2 V15.8 L12 20 L3.8 15.8 Z M3.8 8.2 L12 12.4 L20.2 8.2 M12 12.4 V20";
    private const string Washer = "M5.5 3.2 H18.5 A1.6 1.6 0 0 1 20.1 4.8 V19.2 A1.6 1.6 0 0 1 18.5 20.8 H5.5 A1.6 1.6 0 0 1 3.9 19.2 V4.8 A1.6 1.6 0 0 1 5.5 3.2 Z M3.9 7.2 H20.1 M12 10.2 A4 4 0 0 1 12 18.2 A4 4 0 0 1 12 10.2 M7 5.2 H10.4 M17.2 5.2 V5.21";
    private const string Screen = "M4 4.5 H20 A1.5 1.5 0 0 1 21.5 6 V15 A1.5 1.5 0 0 1 20 16.5 H4 A1.5 1.5 0 0 1 2.5 15 V6 A1.5 1.5 0 0 1 4 4.5 Z M9 20.5 H15 M12 16.5 V20.5";
    private const string Dumbbell = "M3.5 8 V16 M6.5 5 V19 M17.5 5 V19 M20.5 8 V16 M6.5 12 H17.5";
    private const string Car = "M3.5 16 V11.5 L6.5 6 H17.5 L20.5 11.5 V16 M3.5 16 H20.5 M7 16 V18.5 M17 16 V18.5 M3.5 11.5 H20.5";
    private const string Plant = "M12 21 V12.5 M12 12.5 C12 8.6 9.3 6 5 5.6 C5 9.6 8.2 12 12 12.5 Z M12 12.5 C12 9.3 14.2 6.9 18.7 6.4 C18.7 10.4 15.8 12.4 12 12.5 Z M8.8 17.5 H15.2 L14.6 21 H9.4 Z";
    private const string Cup = "M4.5 8.5 H15 V13 A4.5 4.5 0 0 1 10.5 17.5 H9 A4.5 4.5 0 0 1 4.5 13 Z M15 10 H17.2 A2.3 2.3 0 0 1 17.2 14.6 H15 M5.5 20 H15.5 M7.5 6 C7.5 5 8.5 4.6 8.5 3.6 M11.5 6 C11.5 5 12.5 4.6 12.5 3.6";

    public RoomChip(string name)
    {
        Name = name;
        Icon = Geometry.Parse(ResolveIcon(name));
    }

    public string Name { get; }
    public Geometry Icon { get; }
    [ObservableProperty] private bool _isSelected;

    /// <summary>按房间名关键词匹配图标，覆盖米家返回的常见房型命名。</summary>
    private static string ResolveIcon(string name)
    {
        if (Has(name, "全部", "全屋", "所有", "整个"))
            return House;

        // 顺序敏感：先匹配「洗手间」再匹配「洗手区」等高相似词。
        if (Has(name, "洗手间", "卫生间", "主卫", "次卫", "客卫", "公卫", "厕所", "马桶", "卫浴", "淋浴"))
            return Shower;
        if (Has(name, "浴室", "浴缸"))
            return Tub;
        if (Has(name, "洗手", "盥洗", "洗漱", "台盆"))
            return Sink;
        if (Has(name, "洗衣"))
            return Washer;
        if (Has(name, "厨"))
            return Pot;
        if (Has(name, "餐"))
            return Dining;
        if (Has(name, "茶"))
            return Cup;
        if (Has(name, "客厅", "起居"))
            return Sofa;
        if (Has(name, "书", "阅读", "办公", "学习"))
            return Book;
        if (Has(name, "卧", "儿童房", "老人房", "客房", "保姆房", "婴儿房", "睡"))
            return Bed;
        if (Has(name, "阳台", "露台", "飘窗", "窗"))
            return Window;
        if (Has(name, "衣帽", "更衣", "衣柜", "衣橱"))
            return Hanger;
        if (Has(name, "储", "杂", "仓库", "设备间", "机房", "配电"))
            return Box;
        if (Has(name, "影音", "影院", "娱乐", "游戏", "视听"))
            return Screen;
        if (Has(name, "健身", "运动", "瑜伽"))
            return Dumbbell;
        if (Has(name, "车库", "车位"))
            return Car;
        if (Has(name, "花园", "庭院", "院子", "菜园"))
            return Plant;
        if (Has(name, "楼梯"))
            return Stairs;
        if (Has(name, "玄关", "门厅", "门口", "入户", "走廊", "过道"))
            return Door;
        return House;
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
    public required Geometry Icon { get; init; }
    /// <summary>卡片右上光晕渐变（场景色低透明度）。</summary>
    public required IBrush GlowBrush { get; init; }
    public required IBrush IconBgBrush { get; init; }
    public required IBrush IconFgBrush { get; init; }
    [ObservableProperty] private bool _isRunning;

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
