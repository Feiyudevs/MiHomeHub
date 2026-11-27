// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MiHomeHub.Models;

namespace MiHomeHub.Services;

/// <summary>
/// 卡片「外显参数」：卡片副标题上显示哪几项规格读数，按设备类型（spec_type 大类）分组配置。
/// 插座默认外显功率而不是温度；其余设备沿用温度 + 湿度。用户可在设备详情的高级设置里
/// 一键把某个读数加进卡片或移出，设置写回 app.json 的 cardMetrics，对同类设备一起生效。
/// </summary>
public static class CardMetricsCatalog
{
    /// <summary>可外显的读数属性短名：只读、数值型，且确实有展示意义的指标。
    /// 插座用 electric-power（float，单位 watt）看瞬时功率；power-consumption 是累计用电量，按度展示。</summary>
    private static readonly HashSet<string> MetricNames = new(StringComparer.Ordinal)
    {
        "temperature", "relative-humidity", "humidity", "pm2.5", "pm10", "pm1", "illuminance",
        "co2", "tvoc", "hcho", "noise", "dust", "tds-in", "air-quality", "water-level",
        "electric-power", "power-consumption", "voltage", "electric-current",
        "motion-detected", "contact-state", "occupancy",
        "smoke-state", "leak", "door-state", "submersion-state"
    };

    /// <summary>标准指标键 → MIoT 规格里可能出现的短名别名。</summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
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

    /// <summary>慢速读数：每 4 拍读一次（温湿度、耗材），其余外显字段跟普通轮询一拍读一次。</summary>
    private static readonly HashSet<string> SlowNames = new(StringComparer.Ordinal)
    {
        "temperature", "relative-humidity", "humidity"
    };

    /// <summary>插座默认外显功率（electric-power，单位 watt）。</summary>
    private static readonly string[] SocketDefaults = { "electric-power" };

    /// <summary>其余类型沿用原来的温度 + 湿度。</summary>
    private static readonly string[] GenericDefaults = { "temperature", "relative-humidity" };

    /// <summary>spec_type 大类归一化：插座/开关代际叫法不同，但属于同一类设备。</summary>
    public static string TypeKey(MijiaDevice d)
    {
        var category = d.CategoryName;
        if (string.IsNullOrEmpty(category))
        {
            // 部分第三方插座没有 spec_type，图标按 model 猜成 socket；仍按插座处理，
            // 否则默认外显会落到温度/湿度，卡片上永远没有功率/电量读数。
            return d.IconKey == "socket" ? "outlet" : d.IconKey;
        }
        return category switch
        {
            "smart-outlet" => "outlet",
            "multi-switch" or "switch-button" or "scene-switch" => "switch",
            _ => category
        };
    }

    public static bool IsSocketType(string typeKey) => typeKey == "outlet";

    /// <summary>类型的中文名，只用于面板标题，未知类型回落「同类设备」。</summary>
    public static string TypeLabel(MijiaDevice d) => TypeKey(d) switch
    {
        "outlet" => "插座",
        "switch" => "开关",
        "light" => "灯",
        "air-conditioner" => "空调",
        "curtain" => "窗帘",
        "fan" => "风扇",
        "camera" => "摄像头",
        "bath-heater" => "浴霸",
        "sensor_ht" => "温湿度传感器",
        "sensor" => "传感器",
        "humidifier" => "加湿器",
        "purifier" => "净化器",
        "vacuum" => "扫地机",
        "waterheater" => "热水器",
        "speaker" => "音箱",
        "television" => "电视",
        _ => "同类设备"
    };

    /// <summary>该类型的默认外显字段（用户没手动调过时用）。</summary>
    public static string[] Defaults(MijiaDevice d) =>
        Defaults(TypeKey(d));

    public static string[] Defaults(string typeKey) =>
        IsSocketType(typeKey) ? SocketDefaults : GenericDefaults;

    /// <summary>该类型当前生效的外显字段：用户配置优先，没配过用默认。</summary>
    public static IReadOnlyList<string> EffectiveNames(MijiaDevice d) => EffectiveNames(TypeKey(d));

    public static IReadOnlyList<string> EffectiveNames(string typeKey) =>
        AppSettingsStore.CardMetrics.TryGetValue(typeKey, out var names)
            ? names
            : Defaults(typeKey);

    /// <summary>当前生效外显字段对应的规格属性（同名字段取第一个可读项）。</summary>
    public static List<SpecPropertyDef> SelectedProps(MijiaDevice d)
    {
        var spec = d.Spec;
        if (spec == null) return new List<SpecPropertyDef>();
        var list = new List<SpecPropertyDef>();
        foreach (var name in EffectiveNames(d))
        {
            var p = FindSelectedMetric(spec, name);
            if (p is { IsNumeric: true, Writable: false }) list.Add(p);
        }
        return list;
    }

    /// <summary>把规格短名归一到外显配置使用的标准键；不在指标表内返回 null。</summary>
    public static string? StandardMetricName(string name) => StandardName(name);

    /// <summary>普通轮询每拍要读的外显字段（不含温度/湿度这类慢速读数）。</summary>
    public static List<SpecPropertyDef> FastProps(MijiaDevice d) =>
        SelectedProps(d).Where(p => !SlowNames.Contains(p.Name)).ToList();

    /// <summary>高级设置里可选的读数项：只读数值、非枚举、且有展示意义。</summary>
    public static List<SpecPropertyDef> Candidates(MijiaDevice d)
    {
        var spec = d.Spec;
        if (spec == null) return new List<SpecPropertyDef>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<SpecPropertyDef>();
        foreach (var p in spec.Properties)
        {
            // 只读的实测值才算读数；可写的是保护阈值/目标值，不能混进读数卡
            if (p.Writable || !p.Readable || !p.IsNumeric || p.Options.Count > 0) continue;
            var standard = StandardName(p.Name);
            if (standard == null) continue;
            // 同一指标只在读数卡里出现一次（power / electric-power 这类别名并存时不重复铺卡）；
            // 打包属性里的功率/电压共用同一个 siid.piid，但指标不同，都要保留
            if (!seenNames.Add(standard)) continue;
            list.Add(p);
        }
        return list;
    }

    /// <summary>按标准键找属性，先认标准名，再认 `-density` 等厂商别名。</summary>
    public static SpecPropertyDef? FindSelectedMetric(DeviceSpec spec, string name) =>
        spec.FindMetric(name) ??
        spec.Properties.FirstOrDefault(x => x.Readable && !x.Writable && x.IsNumeric &&
            Aliases.TryGetValue(name, out var aliases) && aliases.Contains(x.Name));

    /// <summary>把规格短名归一到卡片配置使用的标准键；不在指标表内返回 null。</summary>
    private static string? StandardName(string name)
    {
        if (MetricNames.Contains(name)) return name;
        foreach (var (standard, aliases) in Aliases)
        {
            if (aliases.Contains(name)) return standard;
        }
        return null;
    }

    /// <summary>面板上的候选标题：属性中文名 + 单位（如「功耗 W」）。</summary>
    public static string DisplayLabel(SpecPropertyDef p)
    {
        var unit = UnitOf(p);
        return string.IsNullOrEmpty(unit) ? p.Desc : $"{p.Desc} · {unit}";
    }

    /// <summary>单位：规格给的英文键已在 DeviceSpecService 归一化，这里只兜底；个别机型写 none 也当没写。</summary>
    private static string UnitOf(SpecPropertyDef p)
    {
        var unit = p.Unit.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : p.Unit;
        return p.Name switch
        {
            "power-consumption" or "energy-consumption" =>
                unit.Length > 0 ? unit : "度",
            "electric-power" or "power" or "surge-power" =>
                unit.Length > 0 ? unit : "W",
            "voltage" or "electric-voltage" => unit.Length > 0 ? unit : "V",
            "electric-current" or "current" => unit.Length > 0 ? unit : "A",
            "pm2.5" or "pm2.5-density" or "pm10" or "pm10-density" or "pm1" or "pm1-density" =>
                unit.Length > 0 ? unit : "µg/m³",
            "co2" or "co2-density" => unit.Length > 0 ? unit : "ppm",
            "hcho" or "hcho-density" => unit.Length > 0 ? unit : "mg/m³",
            "noise" or "noise-decibel" => unit.Length > 0 ? unit : "dB",
            "tvoc" or "tvoc-density" or "voc-density" => unit,
            _ => unit
        };
    }

    /// <summary>温度的原值可能是 0.1℃（超过 60 视为已放大）。</summary>
    public static double NormalizeTemperature(double v) => v > 60 ? v / 10 : v;

    /// <summary>湿度的原值可能是 0.1%（超过 100 视为已放大）。</summary>
    public static double NormalizeHumidity(double v) => v > 100 ? v / 10 : v;

    /// <summary>把轮询读到的原始值格式化成卡片副标题里的一段文字。</summary>
    public static string FormatValue(SpecPropertyDef p, double raw)
    {
        // 规格编码值先折算成展示值：耗电量 0.01 度计数器、mA、mV、kW 都在 ValueScale 里；
        // 打包属性（文光汇 v9wh 的 5.1）先按 bit 段取出功率/电压再乘倍率
        var value = p.Decode(raw) * p.ValueScale;
        switch (p.Name)
        {
            case "temperature":
            case "target-temperature":
                return $"{NormalizeTemperature(raw):0.#}℃";
            case "relative-humidity":
            case "humidity":
                return $"{NormalizeHumidity(raw):0.#}%";
            case "electric-power":
            case "power":
            case "surge-power":
            {
                // 个别型号（chuangmi.plug.212a01 等）unit 写 watt，但值域上限到 655 万，
                // 实际是 0.01W 编码；上限过百万的按百分之一瓦缩回，其余按 W 直读。
                var watts = p.HasRange && p.Max >= 1_000_000 ? value / 100 : value;
                return watts >= 1000 ? $"{watts:0}W" : $"{watts:0.#}W";
            }
            case "power-consumption":
            case "energy-consumption":
                return value >= 100 ? $"{value:0}度" : $"{value:0.##}度";
            case "voltage":
            case "electric-voltage":
                return $"{FormatNumber(value)}V";
            case "electric-current":
            case "current":
                return value >= 1 ? $"{FormatNumber(value)}A" : $"{TrimTrailingZeros(value, 3)}A";
            // 甲醛等小数读数常低于 0.1（如 0.003 mg/m³）；一位小数会被抹成 0。
            // 保留有效小数，去掉末尾的 0，例如 0.003、0.05、1.2；整数仍显示整数。
            case "hcho":
            case "hcho-density":
                return $"{TrimTrailingZeros(value, 3)}{UnitOf(p)}";
        }
        return $"{FormatNumber(value)}{UnitOf(p)}";
    }

    /// <summary>通用读数：一位小数；末尾是 0 就按整数显示。</summary>
    private static string FormatNumber(double value) =>
        Math.Abs(value) >= 1000 ? $"{value:0}" : $"{value:0.#}";

    /// <summary>去掉固定小数位格式末尾的 0 和多余小数点，避免出现 0.0030 / 1.20 这类尾巴。</summary>
    private static string TrimTrailingZeros(double value, int decimals) =>
        value.ToString($"0.{"".PadRight(decimals, '0')}", CultureInfo.InvariantCulture)
             .TrimEnd('0').TrimEnd('.');
}
