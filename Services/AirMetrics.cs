// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using MiHomeHub.Models;

namespace MiHomeHub.Services;

/// <summary>空气读数的档位：绿（好）/ 琥珀（略高）/ 红（超标）。</summary>
public enum AirTone
{
    Good,
    Mild,
    Poor
}

/// <summary>一档判定：小于等于 <paramref name="Max"/> 时算这一档；最后一档的 Max 是无穷大。</summary>
public sealed record AirGrade(double Max, string Label, AirTone Tone);

/// <summary>
/// 首页「数据看板」里的一张空气小卡：一个指标、一个读数、一个档位。
/// 判定阈值按国标常用口径（GB/T 18883 甲醛 0.08、TVOC 0.60 mg/m³，PM 按 AQI 分级），
/// 只用来配色和写一句「优 / 达标 / 超标」，不做正式空气质量报告。
/// </summary>
public sealed class AirMetric
{
    public AirMetric(string key, string standard, string title, string subject, string unit,
        int decimals, bool normalizeMicro, double previewValue, IReadOnlyList<AirGrade> grades)
    {
        Key = key;
        Standard = standard;
        Title = title;
        Subject = subject;
        Unit = unit;
        Decimals = decimals;
        NormalizeMicro = normalizeMicro;
        PreviewValue = previewValue;
        Grades = grades;
    }

    /// <summary>卡片 key（pm25 / pm10 / hcho / tvoc / co2），存显隐与顺序用。</summary>
    public string Key { get; }

    /// <summary>MIoT 规格里的标准属性名（pm2.5 / pm10 / hcho / tvoc / co2）。</summary>
    public string Standard { get; }

    /// <summary>卡面标题（PM2.5 / 甲醛 …）。</summary>
    public string Title { get; }

    /// <summary>中文说明，只进悬停提示与预览说明。</summary>
    public string Subject { get; }

    /// <summary>展示单位：颗粒物 µg/m³、甲醛与 TVOC mg/m³、二氧化碳 ppm。</summary>
    public string Unit { get; }

    /// <summary>小数位：颗粒物与二氧化碳取整，甲醛三位、TVOC 两位。</summary>
    public int Decimals { get; }

    /// <summary>规格按 µg/m³ 报、而卡片按 mg/m³ 展示的指标（甲醛、TVOC）要做千分之一折算。</summary>
    public bool NormalizeMicro { get; }

    /// <summary>没有对应设备时铺的示例读数。</summary>
    public double PreviewValue { get; }

    public IReadOnlyList<AirGrade> Grades { get; }
}

/// <summary>
/// 空气指标表：首页空气小卡、轮询请求、读数回填三处共用同一份定义。
/// 单位一律按展示口径归一（甲醛 / TVOC 按 mg/m³，颗粒物 µg/m³，二氧化碳 ppm），
/// 这样卡片上的阈值判定不必再关心厂商规格写的是哪种单位。
/// </summary>
public static class AirMetrics
{
    public static readonly AirMetric Pm25 = new(
        "pm25", "pm2.5", "PM2.5", "PM2.5 细颗粒物", "µg/m³", 0, false, 28,
        new[]
        {
            new AirGrade(35, "优", AirTone.Good),
            new AirGrade(75, "良", AirTone.Good),
            new AirGrade(115, "轻度污染", AirTone.Mild),
            new AirGrade(250, "中度污染", AirTone.Poor),
            new AirGrade(double.PositiveInfinity, "重度污染", AirTone.Poor)
        });

    public static readonly AirMetric Pm10 = new(
        "pm10", "pm10", "PM10", "PM10 可吸入颗粒物", "µg/m³", 0, false, 46,
        new[]
        {
            new AirGrade(50, "优", AirTone.Good),
            new AirGrade(150, "良", AirTone.Good),
            new AirGrade(250, "轻度污染", AirTone.Mild),
            new AirGrade(420, "中度污染", AirTone.Poor),
            new AirGrade(double.PositiveInfinity, "重度污染", AirTone.Poor)
        });

    public static readonly AirMetric Hcho = new(
        "hcho", "hcho", "甲醛", "甲醛", "mg/m³", 3, true, 0.042,
        new[]
        {
            new AirGrade(0.08, "达标", AirTone.Good),
            new AirGrade(0.10, "偏高", AirTone.Mild),
            new AirGrade(double.PositiveInfinity, "超标", AirTone.Poor)
        });

    public static readonly AirMetric Tvoc = new(
        "tvoc", "tvoc", "TVOC", "TVOC 挥发性有机物", "mg/m³", 2, true, 0.72,
        new[]
        {
            new AirGrade(0.60, "达标", AirTone.Good),
            new AirGrade(1.00, "偏高", AirTone.Mild),
            new AirGrade(double.PositiveInfinity, "超标", AirTone.Poor)
        });

    public static readonly AirMetric Co2 = new(
        "co2", "co2", "CO₂", "二氧化碳", "ppm", 0, false, 1180,
        new[]
        {
            new AirGrade(800, "优", AirTone.Good),
            new AirGrade(1200, "良", AirTone.Good),
            new AirGrade(2000, "偏高", AirTone.Mild),
            new AirGrade(double.PositiveInfinity, "超标", AirTone.Poor)
        });

    /// <summary>首页空气小卡的固定顺序：先颗粒物，再甲醛、TVOC，最后二氧化碳。</summary>
    public static IReadOnlyList<AirMetric> All { get; } = new[] { Pm25, Pm10, Hcho, Tvoc, Co2 };

    public static AirMetric? ByKey(string? key)
    {
        foreach (var metric in All)
            if (string.Equals(metric.Key, key, StringComparison.Ordinal)) return metric;
        return null;
    }

    /// <summary>这台设备的规格里能报的空气指标（轮询请求与读数回填共用）。</summary>
    public static IEnumerable<(AirMetric Metric, SpecPropertyDef Prop)> Props(DeviceSpec spec)
    {
        foreach (var metric in All)
            if (spec.FindMetric(metric.Standard) is { } prop)
                yield return (metric, prop);
    }

    /// <summary>把规格原始值折算成卡片展示的单位：按 µg/m³ 报的甲醛 / TVOC 收成 mg/m³。</summary>
    public static double Normalize(AirMetric metric, SpecPropertyDef prop, double raw)
    {
        if (!metric.NormalizeMicro) return raw;
        var unit = prop.Unit ?? "";
        var lower = unit.ToLowerInvariant();
        var micro = lower.Contains("µg", StringComparison.Ordinal)
            || lower.Contains("μg", StringComparison.Ordinal)
            || lower.Contains("ug/m", StringComparison.Ordinal)
            || lower.Contains("microgram", StringComparison.Ordinal);
        return micro ? raw / 1000 : raw;
    }

    /// <summary>卡面读数：按指标的小数位取整，再抹掉末尾多余的 0（0.0030 → 0.003）。</summary>
    public static string FormatValue(AirMetric metric, double value)
    {
        var text = value.ToString("F" + metric.Decimals, CultureInfo.InvariantCulture);
        if (metric.Decimals > 0)
        {
            text = text.TrimEnd('0').TrimEnd('.');
            if (text.Length == 0 || text == "-") text = "0";
        }
        return text;
    }

    /// <summary>按阈值落到某一档；数值缺失（NaN）时给一档「—」。</summary>
    public static AirGrade Grade(AirMetric metric, double value)
    {
        if (double.IsNaN(value)) return new AirGrade(0, "—", AirTone.Good);
        foreach (var grade in metric.Grades)
            if (value <= grade.Max) return grade;
        return metric.Grades[^1];
    }
}
