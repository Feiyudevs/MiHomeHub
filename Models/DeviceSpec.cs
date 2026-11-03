using System;
using System.Collections.Generic;
using System.Linq;

namespace MiHome.Models;

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

    public bool IsOn => Name == "on" || Name.StartsWith("on-", StringComparison.Ordinal);

    public bool IsBool => Format == "bool";

    public bool IsNumeric => Format is "int" or "uint8" or "uint16" or "uint32" or "int8"
        or "int16" or "int32" or "int64" or "uint64" or "float" or "double";

    public bool IsText => Format == "string";

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

/// <summary>一台设备（按 spec_type 共享）的完整规格。</summary>
public sealed class DeviceSpec
{
    public List<SpecPropertyDef> Properties { get; } = new();
    public List<SpecActionDef> Actions { get; } = new();

    public bool Valid => Properties.Count > 0 || Actions.Count > 0;

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
        if (name == "relative-humidity")
            return Properties.FirstOrDefault(x => x.Readable && x.Name == "humidity");
        return null;
    }

    public SpecPropertyDef? FindProp(string name) =>
        Properties.FirstOrDefault(x => x.Readable && x.Name == name);

    /// <summary>
    /// 耗材余量：滤芯/刷头 life-level 与电量 battery-level（0–100 百分比）。
    /// battery 只在量程不超过 100 时才算百分比，避免把毫伏读数当成余量。
    /// </summary>
    public IEnumerable<SpecPropertyDef> ConsumableProps =>
        Properties.Where(p => p.Readable && p.IsNumeric &&
            (p.Name.Contains("life-level", StringComparison.Ordinal) ||
             p.Name == "battery-level" ||
             (p.Name == "battery" && (!p.HasRange || p.Max <= 100))));

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
