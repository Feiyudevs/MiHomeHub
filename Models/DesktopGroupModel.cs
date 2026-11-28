// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MiHomeHub.Models;

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

    /// <summary>
    /// 这块分区所在显示器的签名。那块屏被关掉时分区临时搬到还在用的屏上，
    /// X/Y 记录不动，等屏重新插上或下次开机回来时落回原位。
    /// </summary>
    public string ScreenSig { get; set; } = "";

    public double Width { get; set; } = DefaultWidth;

    /// <summary>
    /// 卡片高度（DIP）。设备 / 媒体 / 空调分区由内容撑高，只有数据卡用这个字段；
    /// 为了不改动老分区窗的 SizeToContent 逻辑，默认 0 = 不参与高度计算。
    /// </summary>
    public double Height { get; set; }

    public bool Collapsed { get; set; }

    /// <summary>置顶：脱离桌面层（不再跟随 Win+D），作为普通置顶窗浮在所有软件之上。</summary>
    public bool Topmost { get; set; }

    /// <summary>
    /// 数据卡是否在桌面上显示。关闭只把卡片收起来（保留位置与尺寸），
    /// 需要彻底删除时从「移除数据卡」走，删掉这条配置。
    /// </summary>
    public bool Shown { get; set; } = true;

    /// <summary>
    /// 分区内容形态：device = 设备宫格，media = 音响播放，ac = 空调控制卡，
    /// camera = 摄像头实时画面，data = 数据看板里的单张指标卡。旧配置缺字段按设备分区处理。
    /// </summary>
    public string Kind { get; set; } = ZoneDevice;

    /// <summary>数据卡的指标 key（temp/hum/power/.../co2）；只有 <see cref="IsData"/> 时有意义。</summary>
    public string Metric { get; set; } = "";

    /// <summary>分区里的设备 did，顺序即显示顺序。</summary>
    public List<string> Dids { get; set; } = new();

    /// <summary>分区里的场景 scene_id，顺序即显示顺序。</summary>
    public List<string> SceneIds { get; set; } = new();

    [JsonIgnore]
    public bool IsMedia => string.Equals(Kind, ZoneMedia, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsAc => string.Equals(Kind, ZoneAc, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsData => string.Equals(Kind, ZoneData, StringComparison.Ordinal);

    /// <summary>监控分区：一张 16:9 的实时画面 + 身份行，绑定设置里选中的那台摄像头。</summary>
    [JsonIgnore]
    public bool IsCamera => string.Equals(Kind, ZoneCamera, StringComparison.Ordinal);

    public const string ZoneDevice = "device";
    public const string ZoneMedia = "media";
    public const string ZoneAc = "ac";
    public const string ZoneCamera = "camera";
    public const string ZoneData = "data";
}

/// <summary>
/// 分区最后一次的落位（位置 / 宽度 / 所在屏）。
/// 媒体、空调分区被关掉时模型会从清单里移除，但「用户把它摆在哪」得单独记一份，
/// 从设置里重新打开时回到原地，而不是另找一个空位。
/// </summary>
public sealed class ZoneSpot
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = DesktopGroupModel.DefaultWidth;

    /// <summary>落位时所在显示器的签名；那块屏回来时按它落回同一块屏。</summary>
    public string ScreenSig { get; set; } = "";

    public static ZoneSpot From(DesktopGroupModel model) => new()
    {
        X = model.X,
        Y = model.Y,
        Width = model.Width,
        ScreenSig = model.ScreenSig
    };
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

    /// <summary>空调分区被手动关闭过：为 true 时不再自动放回桌面，重新开启只走设置页开关。</summary>
    public bool AcHidden { get; set; }

    /// <summary>
    /// 监控分区默认关着：解一路 1080p 画面是实打实的开销，得用户自己在设置里打开才出现。
    /// 打开后关掉分区会把这里置位，不再自动放回。
    /// </summary>
    public bool CameraHidden { get; set; } = true;

    /// <summary>媒体分区最后一次落位。为 null 表示从没摆过，重新打开时才挑默认空位。</summary>
    public ZoneSpot? MediaSpot { get; set; }

    /// <summary>空调分区最后一次落位。为 null 表示从没摆过，重新打开时才挑默认空位。</summary>
    public ZoneSpot? AcSpot { get; set; }

    /// <summary>监控分区最后一次落位。为 null 表示从没摆过，重新打开时才挑默认空位。</summary>
    public ZoneSpot? CameraSpot { get; set; }

    /// <summary>
    /// 外观迁移版本。滑杆语义每次变档（下限/上限重映射）都要 +1：
    /// 迁移只跑一次，否则用户手动选的浓度会在下次启动时被反复改写。
    /// 0 = 没有该字段的老数据。
    /// </summary>
    public int AppearanceVersion { get; set; }

    /// <summary>
    /// 落位迁移版本。默认落位规则变了就 +1：迁移只跑一次，
    /// 否则用户自己拖过的位置会在下次启动时被反复改回去。
    /// 0 = 老数据（分区可能落在「虚拟桌面并集」的右上角，也就是第二屏边上）。
    /// </summary>

    [JsonIgnore]
    public bool HasGroups => Groups.Count > 0;
}
