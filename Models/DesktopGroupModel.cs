using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MiHome.Models;

/// <summary>
/// 桌面分区（一个独立贴在桌面上的设备分组窗）。
/// 坐标为 DIP，相对虚拟桌面左上角，保证换分辨率/多屏时位置可还原。
/// </summary>
public sealed class DesktopGroupModel
{
    public const double DefaultWidth = 320;
    /// <summary>最窄也要放得下一整张设备卡（图标 + 名称 + 开关），否则文字会挤成省略号。</summary>
    public const double MinWidth = 260;
    public const double MaxWidth = 640;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "新分区";

    public double X { get; set; } = 96;

    public double Y { get; set; } = 96;

    public double Width { get; set; } = DefaultWidth;

    public bool Collapsed { get; set; }

    /// <summary>分区内容形态：device = 设备宫格，media = 音响播放。旧配置缺字段按设备分区处理。</summary>
    public string Kind { get; set; } = ZoneDevice;

    /// <summary>分区里的设备 did，顺序即显示顺序。</summary>
    public List<string> Dids { get; set; } = new();

    /// <summary>分区里的场景 scene_id，顺序即显示顺序。</summary>
    public List<string> SceneIds { get; set; } = new();

    [JsonIgnore]
    public bool IsMedia => string.Equals(Kind, ZoneMedia, StringComparison.Ordinal);

    public const string ZoneDevice = "device";
    public const string ZoneMedia = "media";
}

/// <summary>桌面组件外观：背景色 / 背景透明度 / 毛玻璃。三项互相独立。</summary>
public sealed class DesktopAppearance
{
    /// <summary>毛玻璃（窗口级亚克力模糊）。关掉后靠底色本身撑观感。</summary>
    public bool Glass { get; set; } = true;

    /// <summary>背景透明度 0–1.0，同时缩放底色 Alpha 与磨砂强度。</summary>
    public double Opacity { get; set; } = 0.10;

    /// <summary>自定义背景色（#RRGGBB）；空串表示跟随当前主题卡片色。</summary>
    public string BgColor { get; set; } = "";

    public DesktopAppearance Clone() => new() { Glass = Glass, Opacity = Opacity, BgColor = BgColor };

    /// <summary>落到磨砂/底色上的实际透明度（只读派生，不写回配置）。</summary>
    [JsonIgnore]
    public double ClampedOpacity => Math.Clamp(Opacity, 0.0, 1.0);
}

/// <summary>桌面组件整体布局（外观 + 全部分区 + 总开关），持久化到 desktop.json。</summary>
public sealed class DesktopLayout
{
    /// <summary>总开关：关掉后所有分区窗收起，但配置保留。</summary>
    public bool Enabled { get; set; } = true;

    public DesktopAppearance Appearance { get; set; } = new();

    public List<DesktopGroupModel> Groups { get; set; } = new();

    /// <summary>媒体分区被手动关闭过：为 true 时不再自动放回桌面，重新开启只走设置页开关。</summary>
    public bool MediaHidden { get; set; }

    [JsonIgnore]
    public bool HasGroups => Groups.Count > 0;
}
