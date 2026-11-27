// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace MiHomeHub.Services;

/// <summary>
/// 一张数据卡的静态定义：key、标题、单位、默认尺寸与主色。
/// 首页看板的 7 张大卡 + 5 张空气小卡共用这一份，桌面组件、设置页都从这里取，
/// 避免「卡面上的名字」和「设置里的名字」各写一套后对不上。
/// </summary>
public sealed record DataCardDef(
    string Key,
    string Title,
    string Description,
    string Unit,
    string AccentHex,
    bool Compact)
{
    /// <summary>默认设计宽度（DIP）：大卡 400、空气小卡 200。</summary>
    public double DefaultWidth => Compact ? 200 : 400;

    /// <summary>默认设计高度（DIP）：大卡 200、空气小卡 100。</summary>
    public double DefaultHeight => Compact ? 100 : 200;
}

/// <summary>数据卡指标清单（顺序与首页看板一致）。</summary>
public static class DataCardCatalog
{
    public const string Temp = "temp";
    public const string Hum = "hum";
    public const string Power = "power";
    public const string Water = "water";
    public const string Gas = "gas";
    public const string Tds = "tds";
    public const string Scale = "scale";

    public static IReadOnlyList<DataCardDef> All { get; } = new[]
    {
        new DataCardDef(Temp, "温度", "室内温度曲线与舒适度", "℃", "#FFC07A", false),
        new DataCardDef(Hum, "湿度", "室内湿度曲线", "%", "#8FD9FF", false),
        new DataCardDef(Power, "用电", "用电量与用电趋势", "kWh", "#B3A6FF", false),
        new DataCardDef(Water, "用水", "热水器、净水器的用水量", "L", "#5FB4FF", false),
        new DataCardDef(Gas, "用气", "燃气热水器的用气量", "m³", "#FFB066", false),
        new DataCardDef(Tds, "水质", "净水器出水 TDS", "ppm", "#7BE3C4", false),
        new DataCardDef(Scale, "体重", "体重、体脂率与体重曲线", "kg", "#8FE3A8", false),
        // 空气小卡：只报当前读数，不画曲线，默认 200×100
        new DataCardDef("pm25", "PM2.5", "PM2.5 细颗粒物浓度", "µg/m³", "#8FD9FF", true),
        new DataCardDef("pm10", "PM10", "PM10 可吸入颗粒物浓度", "µg/m³", "#B3A6FF", true),
        new DataCardDef("hcho", "甲醛", "室内甲醛浓度", "mg/m³", "#FFB066", true),
        new DataCardDef("tvoc", "TVOC", "挥发性有机物浓度", "mg/m³", "#7BE3C4", true),
        new DataCardDef("co2", "二氧化碳", "室内二氧化碳浓度", "ppm", "#FFC07A", true),
    };

    private static readonly Dictionary<string, DataCardDef> Map =
        All.ToDictionary(def => def.Key, StringComparer.Ordinal);

    public static DataCardDef? Find(string? key)
        => key != null && Map.TryGetValue(key, out var def) ? def : null;

    public static bool IsAir(string? key) => Find(key)?.Compact == true;

    public static bool IsKnown(string? key) => Find(key) != null;

    /// <summary>空气指标对应的完整定义（标题、单位、档位口径都来自这里）。</summary>
    public static AirMetric? AirOf(string? key)
        => key == null ? null : AirMetrics.ByKey(key);

    /// <summary>主色画刷：每张卡只有一根强调色，线、柱、数值、档位都从它派生。</summary>
    public static Color Accent(string? key)
        => Find(key) is { } def ? Color.Parse(def.AccentHex) : Color.Parse("#8FD9FF");

    /// <summary>
    /// 图表下方的渐隐填充：与首页同名趋势卡使用同一组起始透明度，
    /// 同一份曲线放在桌面和首页看起来才是同一条。
    /// </summary>
    public static IBrush ChartFill(string? key)
    {
        var accent = Accent(key);
        // 首页深色主题 TrendTempFill = #47、Hum/Power/Water/Gas/Scale = #3D、Quality = #36
        var alpha = key switch
        {
            Temp => 0x47,
            Tds => 0x36,
            Hum or Power or Water or Gas or Scale => 0x3D,
            _ => 0x3D,
        };
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new Avalonia.Media.GradientStop(Color.FromArgb((byte)alpha, accent.R, accent.G, accent.B), 0),
                new Avalonia.Media.GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1),
            },
        };
    }
}
