// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

namespace MiHomeHub.Models;

/// <summary>
/// 灯具的控制参数（按设备规格解析）：亮度 / 色温真实的下发 siid-piid 与值域，
/// 外加「拨回可调色温」用的预设 mode 值。
/// 色温量程各型号差别很大（2700–6500、3000–6500、3000–6400、灯组 1700–7000），
/// 以前写死 2700–6500，把端点值发给量程更窄的灯时云端按越界拒收，灯就一动不动。
/// </summary>
public sealed class LightControlSchema
{
    /// <summary>亮度：siid / piid 与值域（多数灯是 light 服务的 2/2、1–100）。</summary>
    public int BrightSiid { get; set; } = 2;
    public int BrightPiid { get; set; } = 2;
    public bool HasBrightness { get; set; }
    public double BrightMin { get; set; } = 1;
    public double BrightMax { get; set; } = 100;
    public double BrightStep { get; set; } = 1;

    /// <summary>色温：siid / piid 与值域（单位 K）。没有这条属性（只有彩光的灯）时为 false。</summary>
    public int CtSiid { get; set; } = 2;
    public int CtPiid { get; set; } = 3;
    public bool HasColorTemp { get; set; }
    public double CtMin { get; set; } = 2700;
    public double CtMax { get; set; } = 6500;
    public double CtStep { get; set; } = 1;

    /// <summary>预设场景 mode（light 服务里可写的那条）。</summary>
    public int ModeSiid { get; set; }
    public int ModePiid { get; set; }

    /// <summary>「回到可调色温」对应的 mode 值：色温模式 / 空场景 / 无模式 / 正常模式。
    /// -1 表示该型号枚举里没有这样一档（或规格没给出），拖色温时不做拨回。</summary>
    public int ResetModeValue { get; set; } = -1;

    public bool Valid => HasBrightness || HasColorTemp;
}
