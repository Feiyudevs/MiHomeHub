// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace MiHomeHub.Models;

/// <summary>通用规格属性的控件形态，由 format / value-list / value-range 决定。</summary>
public enum SpecControlKind
{
    /// <summary>bool 属性 → 开关。</summary>
    Toggle,

    /// <summary>带 value-range 的数值 → 滑杆。</summary>
    Slider,

    /// <summary>带 value-list 的数值 → 下拉。</summary>
    Choice,

    /// <summary>无法编辑的属性（只读值、无范围的数值、字符串）→ 只读展示。</summary>
    Value
}

public sealed class SpecOptionDef
{
    public required int Value { get; init; }
    public required string Label { get; init; }
}

/// <summary>规格里的一个属性（已展平，带所属服务信息）。</summary>
public sealed class SpecPropertyDef
{
    public int Siid { get; init; }
    public int Piid { get; init; }

    /// <summary>属性短名（urn 第 4 段），如 on / brightness / target-temperature。</summary>
    public required string Name { get; init; }

    /// <summary>展示名：云端中文 description，缺失时退回短名。</summary>
    public required string Desc { get; init; }

    /// <summary>未做括号/长度清理的原始展示名，供悬停气泡展示完整含义；为空时回落 <see cref="Desc"/>。</summary>
    public string RawDesc { get; init; } = "";

    /// <summary>所属服务的短名与中文名，用于分组标题。</summary>
    public required string ServiceName { get; init; }
    public required string ServiceDesc { get; init; }

    /// <summary>bool / int / uint8 / uint16 / uint32 / float / string …</summary>
    public required string Format { get; init; }

    public bool Readable { get; init; }
    public bool Writable { get; init; }

    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; } = 1;
    public bool HasRange { get; init; }

    public List<SpecOptionDef> Options { get; init; } = new();

    /// <summary>展示单位，规格未给出时按属性名兜底（温度 °C、湿度 % 等）。</summary>
    public string Unit { get; init; } = "";

    /// <summary>
    /// 耗电量计数器的单位倍率：规格描述写「单位:0,01Kwh」的为 0.01、写「单位:1Wh」的为 0.001，
    /// 其余计数器按度（1）。值本身是整数计数器，不乘这个倍率就会把 62 度的月用电读成 61921 度。
    /// 非耗电量属性恒为 1。个别第三方型号的单位说明不可信，解析后再按型号校准。
    /// </summary>
    public double UnitScale { get; set; } = 1;

    /// <summary>
    /// 原始值到展示值的倍率：电流 mA→A、电压 mV→V、功率 kW→W、耗电量计数器→度。
    /// 展示与卡片读数都要乘它；写入类属性不受影响（这些计量属性都是只读）。
    /// </summary>
    public double ValueScale { get; set; } = 1;

    /// <summary>
    /// 打包属性里的 bit 段（按 uint32 从低位 0 起算）：文光汇这类插座把功率/电压塞进一个
    /// 32 位 data 属性，高 20 位是功率、低 12 位是电压；非打包属性为 -1。
    /// </summary>
    public int BitHigh { get; init; } = -1;
    public int BitLow { get; init; } = -1;

    public bool IsPacked => BitHigh >= 0 && BitLow >= 0 && BitHigh >= BitLow && BitHigh < 32;

    /// <summary>取出打包属性里的原始读数；普通属性原样返回。</summary>
    public double Decode(double raw)
    {
        if (!IsPacked) return raw;
        var value = raw <= 0 ? 0u : raw >= uint.MaxValue ? uint.MaxValue : (uint)Math.Round(raw);
        var width = BitHigh - BitLow + 1;
        var mask = width >= 32 ? uint.MaxValue : (uint)((1UL << width) - 1);
        return (value >> BitLow) & mask;
    }

    public bool IsOn => Name == "on" || Name.StartsWith("on-", StringComparison.Ordinal);

    public bool IsBool => Format == "bool";

    public bool IsNumeric => Format is "int" or "uint8" or "uint16" or "uint32" or "int8"
        or "int16" or "int32" or "int64" or "uint64" or "float" or "double";

    public bool IsText => Format == "string";

    /// <summary>电量类属性的短名（不同机型/代际的叫法）。</summary>
    public bool IsBatteryName => Name is "battery" or "battery-level" or "charge-progress"
        or "charge-level" or "battery-percentage";

    /// <summary>值域像百分比：量程落在 2–100。用来区分「电量状态量（0 低 / 1 正常）」。</summary>
    public bool LooksPercent => HasRange && Max <= 100 && Max > 1;

    public SpecControlKind Kind
    {
        get
        {
            if (IsBool) return SpecControlKind.Toggle;
            if (!IsNumeric) return SpecControlKind.Value;
            if (Options.Count > 0) return SpecControlKind.Choice;
            if (HasRange) return SpecControlKind.Slider;
            return SpecControlKind.Value;
        }
    }

    public bool InSwitchService => ServiceName is "switch" or "outlet";
}

/// <summary>规格里的一个动作。P0 只展示无入参动作。</summary>
public sealed class SpecActionDef
{
    public int Siid { get; init; }
    public int Aiid { get; init; }
    public required string Name { get; init; }
    public required string Desc { get; init; }
    public required string ServiceDesc { get; init; }
    public int InputCount { get; init; }
}

/// <summary>规格里的一个事件。arguments 是同一服务里作为出参的属性 piid 列表。</summary>
public sealed class SpecEventDef
{
    public int Siid { get; init; }
    public int Eiid { get; init; }

    /// <summary>事件短名（urn 第 4 段），如 lock-event。</summary>
    public required string Name { get; init; }

    /// <summary>展示名：中文 comment 优先，缺失时退回英文 description。</summary>
    public required string Desc { get; init; }

    public required string ServiceDesc { get; init; }

    /// <summary>出参属性（同服务的 piid），逐个对应触发源里的筛选键 prop.&lt;model&gt;.&lt;siid&gt;.&lt;piid&gt;。</summary>
    public List<int> ArgPiids { get; init; } = new();

    public string Key => $"event.{Siid}.{Eiid}";
}

/// <summary>一台设备（按 spec_type 共享）的完整规格。</summary>
public sealed class DeviceSpec
{
    public List<SpecPropertyDef> Properties { get; } = new();
    public List<SpecActionDef> Actions { get; } = new();

    /// <summary>可做触发条件的事件（门锁的开锁、门铃、低电量等都在这里）。</summary>
    public List<SpecEventDef> Events { get; } = new();

    /// <summary>
    /// 规格里出现过的服务短名（switch-sensor / scene / inching 等）。
    /// 用来区分「墙上的按键面板」和「单路通断模块」——两者设备大类都叫 switch。
    /// </summary>
    public HashSet<string> Services { get; } = new(StringComparer.Ordinal);

    public bool HasService(string name) => Services.Contains(name);

    public bool Valid => Properties.Count > 0 || Actions.Count > 0 || Events.Count > 0;

    public SpecPropertyDef? FindProp(int siid, int piid) =>
        Properties.FirstOrDefault(p => p.Siid == siid && p.Piid == piid);

    public SpecEventDef? FindEvent(int siid, int eiid) =>
        Events.FirstOrDefault(e => e.Siid == siid && e.Eiid == eiid);

    /// <summary>多键开关的每一路可写 on；无线按键/遥控器没有可写 on，结果为空。</summary>
    public List<SpecPropertyDef> OnChannels =>
        Properties.Where(p => p.Writable && p.IsBool && p.IsOn && p.InSwitchService).ToList();

    /// <summary>可写 on（不限服务），用于给非开关设备补一个电源开关。</summary>
    public SpecPropertyDef? AnyWritableOn =>
        Properties.FirstOrDefault(p => p.Writable && p.IsBool && p.IsOn);

    public SpecPropertyDef? FindMetric(string name)
    {
        var p = Properties.FirstOrDefault(x => x.Readable && x.Name == name);
        if (p != null) return p;
        if (MetricAliases.TryGetValue(name, out var aliases))
            // 别名只认只读读数：插座规格里的「功率阈值」也常叫 power，不能被当成实时功率
            return Properties.FirstOrDefault(x => x.Readable && !x.Writable && aliases.Contains(x.Name));
        return null;
    }

    /// <summary>
    /// 同一指标在不同厂商规格里的短名别名。米家标准名常带 `-density` 后缀
    /// （如 pm2.5-density、hcho-density），这里统一归到卡片与轮询使用的标准键。
    /// </summary>
    private static readonly Dictionary<string, string[]> MetricAliases = new(StringComparer.Ordinal)
    {
        ["relative-humidity"] = new[] { "humidity" },
        ["pm2.5"] = new[] { "pm2.5-density" },
        ["pm10"] = new[] { "pm10-density" },
        ["pm1"] = new[] { "pm1-density" },
        ["co2"] = new[] { "co2-density" },
        ["tvoc"] = new[] { "tvoc-density", "voc-density", "etvoc-density" },
        ["hcho"] = new[] { "hcho-density", "formaldehyde-density", "formaldehyde" },
        ["noise"] = new[] { "noise-decibel", "noise" },
        ["dust"] = new[] { "dust-density", "dust" },
        ["electric-power"] = new[] { "power", "surge-power", "active-power" },
        ["voltage"] = new[] { "electric-voltage" },
        ["electric-current"] = new[] { "current" },
        ["power-consumption"] = new[] { "energy-consumption", "power-consume", "electricity-consumption" }
    };

    public SpecPropertyDef? FindProp(string name) =>
        Properties.FirstOrDefault(x => x.Readable && x.Name == name);

    /// <summary>
    /// 耗材余量：滤芯/刷头 life-level 与电量（0–100 百分比）。
    /// 电量属性名各机型不统一，且同名不同义：净烟机 P2 的 battery-level 是
    /// 「0 低电量 / 1 正常」的状态量，真正的百分比在 charge-progress（1–100 percentage）。
    /// 所以电量只认值域落在 2–100 的属性，枚举/状态量一律不算，否则「正常」会被显示成 1%。
    /// </summary>
    public IEnumerable<SpecPropertyDef> ConsumableProps =>
        Properties.Where(p => p.Readable && p.IsNumeric && p.Options.Count == 0 &&
            (p.Name.Contains("life-level", StringComparison.Ordinal) ||
             (p.IsBatteryName && p.LooksPercent)));

    /// <summary>音箱音量属性：speaker 服务的 volume，可读可写、0–100。</summary>
    public SpecPropertyDef? SpeakerVolume =>
        Properties.FirstOrDefault(p => p.Readable && p.Writable && p.IsNumeric && p.Name == "volume");

    /// <summary>播放状态属性：play-control 服务的 playing-state（0 停止 / 1 播放中 / 2 暂停）。
    /// 音箱的 MIoT 规格里只有这个状态枚举，没有任何「曲目文本」属性 —— 歌名拿不到，界面就只报状态。</summary>
    public SpecPropertyDef? PlayingState =>
        Properties.FirstOrDefault(p => p.Readable && p.IsNumeric && p.Name == "playing-state");

    public SpecActionDef? FindAction(params string[] names) =>
        Actions.FirstOrDefault(a => names.Contains(a.Name, StringComparer.Ordinal));
}
