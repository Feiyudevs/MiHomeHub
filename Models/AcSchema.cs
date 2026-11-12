// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiHome.Models;

/// <summary>空调面板里一个可选段（模式/风速），选中态由设备当前值驱动。</summary>
public sealed partial class AcOptionItem : ObservableObject
{
    public required int Value { get; init; }
    public required string Label { get; init; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 从设备 MIoT 规格解析出的空调控制参数。不同型号的空调属性布局差异很大
/// （温度 piid、模式取值、风速所在服务都不同），必须按设备规格驱动，不能写死。
/// </summary>
public sealed class AcControlSchema
{
    public bool Valid { get; set; }

    /// <summary>空调服务 siid 与 模式 piid（如 siid=2 / piid=2）。</summary>
    public int ModeSiid { get; set; } = 2;
    public int ModePiid { get; set; } = 2;

    /// <summary>目标温度 siid/piid 与范围（如 siid=2 / piid=4，16–31 步进 0.5）。</summary>
    public int TempSiid { get; set; } = 2;
    public int TempPiid { get; set; } = 4;
    public double TempMin { get; set; } = 16;
    public double TempMax { get; set; } = 30;
    public double TempStep { get; set; } = 1;

    /// <summary>风扇档位 siid/piid（多数在独立 fan-control 服务，如 siid=3 / piid=2）。</summary>
    public int FanSiid { get; set; } = 3;
    public int FanPiid { get; set; } = 2;

    /// <summary>设备实际支持的模式（来自 value-list），如 制冷/除湿/送风/制热。</summary>
    public List<AcOptionItem> Modes { get; } = new();

    /// <summary>设备实际支持的风速档（来自 value-list），如 自动/一档…Max档。</summary>
    public List<AcOptionItem> Fans { get; } = new();
}
