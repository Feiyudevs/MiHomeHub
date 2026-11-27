// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using CommunityToolkit.Mvvm.ComponentModel;
using MiHomeHub.Services;

namespace MiHomeHub.Models;

/// <summary>
/// 首页「数据看板」里的一张空气小卡：一个指标一枚卡，只报一个数值 + 一句档位。
/// 数值、档位、来源每拍由 <c>MainViewModel</c> 回填，卡片本身不做计算。
/// </summary>
public sealed partial class AirCardItem : ObservableObject
{
    public AirCardItem(AirMetric metric)
    {
        Metric = metric;
        Key = metric.Key;
        Title = metric.Title;
        UnitText = metric.Unit;
    }

    public AirMetric Metric { get; }

    /// <summary>卡片 key（pm25 / pm10 / hcho / tvoc / co2）：显隐与排序都用它。</summary>
    public string Key { get; }

    public string Title { get; }

    /// <summary>下面五个布尔量只是给样式类用：卡上色条、标题、读数按指标各取一个色相。</summary>
    public bool IsPm25 => Key == "pm25";
    public bool IsPm10 => Key == "pm10";
    public bool IsHcho => Key == "hcho";
    public bool IsTvoc => Key == "tvoc";
    public bool IsCo2 => Key == "co2";

    /// <summary>这张卡在不在首页；关掉时首页这枚小卡整块让位。</summary>
    [ObservableProperty] private bool _isOn = true;

    /// <summary>卡面读数（只含数字，单位单独一枚，字号不同）。</summary>
    [ObservableProperty] private string _valueText = "—";

    /// <summary>单位：µg/m³ / mg/m³ / ppm。</summary>
    [ObservableProperty] private string _unitText;

    /// <summary>档位文字：优 / 良 / 达标 / 偏高 / 超标。</summary>
    [ObservableProperty] private string _gradeText = "";

    /// <summary>档位配色：三档互斥，由样式类按布尔值切换颜色。</summary>
    [ObservableProperty] private bool _isGood = true;
    [ObservableProperty] private bool _isMild;
    [ObservableProperty] private bool _isPoor;

    /// <summary>当前是不是示例数据（没有能报这个指标的设备，或整段演示）。</summary>
    [ObservableProperty] private bool _isPreview;

    /// <summary>读数来自哪个房间 / 哪台设备，示例数据时写「示例数据」。</summary>
    [ObservableProperty] private string _sourceText = "";

    /// <summary>悬停提示：指标全称、完整读数、档位口径与来源设备。</summary>
    [ObservableProperty] private string _tipText = "";
}
