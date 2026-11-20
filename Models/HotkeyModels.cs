// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MiHome.Models;

/// <summary>控制目标的类型：设备 / 手动场景 / 自动化。</summary>
public enum HotkeyTargetType
{
    Device = 0,
    Scene = 1,
    Automation = 2,
}

/// <summary>一步控制动作的类型。设备可开 / 关 / 切换，场景只能执行，自动化可开启 / 关闭 / 切换。</summary>
public enum HotkeyActionKind
{
    /// <summary>设备开关切换（当前态取反）。</summary>
    ToggleDevice = 0,

    /// <summary>打开设备（整机电源 / 单路开关 / 窗帘打开）。</summary>
    DeviceOn = 1,

    /// <summary>关闭设备。</summary>
    DeviceOff = 2,

    /// <summary>执行手动场景。</summary>
    RunScene = 3,

    /// <summary>自动化启停切换（当前启停态取反）。</summary>
    ToggleAutomation = 4,

    /// <summary>开启自动化。</summary>
    AutomationOn = 5,

    /// <summary>关闭自动化。</summary>
    AutomationOff = 6,
}

/// <summary>组合键里的修饰键，位标志与 Win32 无关，仅为本地存储/匹配服务。</summary>
[Flags]
public enum HotkeyModifier
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>一次按下的组合键（修饰键 + 主键 Win32 虚拟键码）。</summary>
public readonly record struct HotkeyCombo(HotkeyModifier Modifiers, int VirtualKey)
{
    public bool IsEmpty => VirtualKey <= 0;
}

/// <summary>
/// 一条快捷键里的一步控制：目标 + 对目标做的动作。
/// 设备用 did，场景 / 自动化用 scene_id；Name 是绑定当时的名称快照。
/// </summary>
public sealed class HotkeyStep
{
    [JsonPropertyName("type")] public HotkeyTargetType Type { get; set; }
    [JsonPropertyName("kind")] public HotkeyActionKind Kind { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

/// <summary>
/// 一条全局快捷键绑定：一组组合键 + 一串按顺序执行的控制步骤。
/// 一步可以是「关灯」「开灯」「执行场景」「关掉自动化」，整串按下后逐条下发。
/// </summary>
public sealed class HotkeyBinding
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>用户给这条快捷键起的名字；空 = 用第一步目标名兜底。</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";

    /// <summary>控制步骤（界面上的「控制卡片」），顺序即执行顺序。</summary>
    [JsonPropertyName("steps")] public List<HotkeyStep> Steps { get; set; } = new();

    /// <summary><see cref="HotkeyModifier"/> 的位组合。</summary>
    [JsonPropertyName("modifiers")] public int Modifiers { get; set; }

    /// <summary>Win32 虚拟键码。</summary>
    [JsonPropertyName("key")] public int VirtualKey { get; set; }

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    // ===== 旧格式：只用于兼容读取，迁移后不再写出 =====

    /// <summary>旧的「一个绑定一个动作类型」。</summary>
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HotkeyActionKind? LegacyKind { get; set; }

    /// <summary>更早的单目标字段。</summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyTargetId { get; set; }

    [JsonPropertyName("targetName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyTargetName { get; set; }

    /// <summary>上一版的多目标字段（同一动作类型打全部目标）。</summary>
    [JsonPropertyName("targets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<HotkeyTarget>? LegacyTargets { get; set; }

    /// <summary>把旧格式升级成步骤列表，并清掉兼容字段。</summary>
    public void NormalizeLegacySteps()
    {
        Steps ??= new List<HotkeyStep>();

        if (Steps.Count == 0)
        {
            var kind = LegacyKind ?? HotkeyActionKind.ToggleDevice;

            if (LegacyTargets is { Count: > 0 })
            {
                foreach (var t in LegacyTargets)
                    Steps.Add(MakeStep(kind, null, t.Id, t.Name));
            }
            else if (!string.IsNullOrWhiteSpace(LegacyTargetId))
            {
                Steps.Add(MakeStep(kind, null, LegacyTargetId!, LegacyTargetName));
            }
        }

        LegacyKind = null;
        LegacyTargetId = null;
        LegacyTargetName = null;
        LegacyTargets = null;

        Steps.RemoveAll(s => string.IsNullOrWhiteSpace(s.Id));
    }

    /// <summary>按动作类型推断目标类型。</summary>
    public static HotkeyTargetType TypeOf(HotkeyActionKind kind) => kind switch
    {
        HotkeyActionKind.RunScene => HotkeyTargetType.Scene,
        HotkeyActionKind.ToggleAutomation
            or HotkeyActionKind.AutomationOn
            or HotkeyActionKind.AutomationOff => HotkeyTargetType.Automation,
        _ => HotkeyTargetType.Device,
    };

    private static HotkeyStep MakeStep(HotkeyActionKind kind, HotkeyTargetType? type, string? id, string? name)
    {
        var targetType = type ?? TypeOf(kind);
        var targetId = id ?? "";
        return new HotkeyStep
        {
            Type = targetType,
            Kind = kind,
            Id = targetId,
            Name = string.IsNullOrWhiteSpace(name) ? targetId : name!,
        };
    }
}

/// <summary>旧版多目标字段的落盘形状，仅供反序列化兼容，不再写入。</summary>
public sealed class HotkeyTarget
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}
