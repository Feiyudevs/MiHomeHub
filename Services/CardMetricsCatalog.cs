// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
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
    /// 插座用 electric-power（float，单位 watt）而不是 power-consumption ——
    /// 后者是累计用电量（无单位），当功率显示会读出完全错误的数字。</summary>
    private static readonly HashSet<string> MetricNames = new(StringComparer.Ordinal)
    {
        "temperature", "relative-humidity", "humidity", "pm2.5", "pm10", "illuminance",
        "co2", "tvoc", "tds-in", "air-quality", "water-level",
        "electric-power", "voltage", "electric-current",
        "motion-detected", "contact-state", "occupancy",
        "smoke-state", "leak", "door-state", "submersion-state"
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
        if (string.IsNullOrEmpty(category)) return d.IconKey;
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
            var p = spec.FindMetric(name) ?? spec.FindProp(name);
            if (p != null) list.Add(p);
        }
        return list;
    }

    /// <summary>普通轮询每拍要读的外显字段（不含温度/湿度这类慢速读数）。</summary>
    public static List<SpecPropertyDef> FastProps(MijiaDevice d) =>
        SelectedProps(d).Where(p => !SlowNames.Contains(p.Name)).ToList();

    /// <summary>高级设置里可选的读数项：只读数值、非枚举、且有展示意义。</summary>
    public static List<SpecPropertyDef> Candidates(MijiaDevice d)
    {
        var spec = d.Spec;
        if (spec == null) return new List<SpecPropertyDef>();
        var seen = new HashSet<(int, int)>();
        var list = new List<SpecPropertyDef>();
        foreach (var p in spec.Properties)
        {
            if (!p.Readable || !p.IsNumeric || p.Options.Count > 0) continue;
            if (!MetricNames.Contains(p.Name)) continue;
            if (!seen.Add((p.Siid, p.Piid))) continue;
            list.Add(p);
        }
        return list;
    }

    /// <summary>面板上的候选标题：属性中文名 + 单位（如「功耗 W」）。</summary>
    public static string DisplayLabel(SpecPropertyDef p)
    {
        var unit = UnitOf(p);
        return string.IsNullOrEmpty(unit) ? p.Desc : $"{p.Desc} · {unit}";
    }

    /// <summary>单位：规格给的英文键已在 DeviceSpecService 归一化，这里只兜底。</summary>
    private static string UnitOf(SpecPropertyDef p) => p.Name switch
    {
        "electric-power" or "power-consumption" or "power" or "energy-consumption" =>
            p.Unit.Length > 0 ? p.Unit : "W",
        "voltage" => p.Unit.Length > 0 ? p.Unit : "V",
        "electric-current" or "current" => p.Unit.Length > 0 ? p.Unit : "A",
        _ => p.Unit
    };

    /// <summary>温度的原值可能是 0.1℃（超过 60 视为已放大）。</summary>
    public static double NormalizeTemperature(double v) => v > 60 ? v / 10 : v;

    /// <summary>湿度的原值可能是 0.1%（超过 100 视为已放大）。</summary>
    public static double NormalizeHumidity(double v) => v > 100 ? v / 10 : v;

    /// <summary>把轮询读到的原始值格式化成卡片副标题里的一段文字。</summary>
    public static string FormatValue(SpecPropertyDef p, double raw)
    {
        switch (p.Name)
        {
            case "temperature":
            case "target-temperature":
                return $"{NormalizeTemperature(raw):0.#}℃";
            case "relative-humidity":
            case "humidity":
                return $"{NormalizeHumidity(raw):0.#}%";
            case "electric-power":
            {
                // 个别型号（chuangmi.plug.212a01 等）unit 写 watt，但值域上限到 655 万，
                // 实际是 0.01W 编码；上限过百万的按百分之一瓦缩回，其余按 W 直读。
                var watts = p.HasRange && p.Max >= 1_000_000 ? raw / 100 : raw;
                return watts >= 1000 ? $"{watts:0}W" : $"{watts:0.#}W";
            }
            case "power-consumption":
            case "power":
                return raw >= 1000 ? $"{raw:0}W" : $"{raw:0.#}W";
        }
        return $"{raw:0.#}{UnitOf(p)}";
    }
}
