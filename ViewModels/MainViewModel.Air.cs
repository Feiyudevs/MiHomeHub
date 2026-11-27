// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MiHomeHub.Models;
using MiHomeHub.Services;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 首页「数据看板」里的空气小卡：PM2.5、PM10、甲醛、TVOC、二氧化碳，一项一枚。
///
/// 和温度曲线那类大卡不同，小卡只报当前一个数值，不画曲线：
/// 读数来自规格里的 pm2.5 / pm10 / hcho / tvoc / co2 属性（轮询每 4 拍读一次），
/// 家里没有任何设备能报某个指标时，那枚卡铺一份示例数据并标「预览」。
/// 卡片显隐、排序与大卡共用同一套 key（app.json 里的 trendCards / trendCardOrder）。
/// </summary>
public partial class MainViewModel
{
    public AirCardItem AirPm25 { get; } = new(AirMetrics.Pm25);
    public AirCardItem AirPm10 { get; } = new(AirMetrics.Pm10);
    public AirCardItem AirHcho { get; } = new(AirMetrics.Hcho);
    public AirCardItem AirTvoc { get; } = new(AirMetrics.Tvoc);
    public AirCardItem AirCo2 { get; } = new(AirMetrics.Co2);

    /// <summary>五枚空气小卡，刷新时统一遍历。</summary>
    private IReadOnlyList<AirCardItem> AirCardItems => new[] { AirPm25, AirPm10, AirHcho, AirTvoc, AirCo2 };

    /// <summary>首页实际铺出来的空气小卡数量（用户关掉的、没开的不算）。</summary>
    [ObservableProperty] private int _airCardCount;

    partial void OnAirCardCountChanged(int value)
    {
        OnPropertyChanged(nameof(ShowAirRow));
        OnPropertyChanged(nameof(TrendCardCount));
        OnPropertyChanged(nameof(DataBoardVisible));
    }

    /// <summary>空气小卡那一排的显示条件：整段在、首拉骨架已退场、至少有一枚小卡开着。</summary>
    public bool ShowAirRow => ShowEnvTrend && !ShowTrendSkeleton && AirCardCount > 0;

    /// <summary>
    /// 重算五枚小卡的读数、档位与来源。每拍跟着 <c>RefreshTrendCards</c> 走一次，
    /// 只在文字真的变了才通知界面，卡片不会整排闪烁。
    /// </summary>
    private void RefreshAirCards()
    {
        foreach (var item in AirCardItems)
        {
            var metric = item.Metric;
            var source = IsTrendPreview ? null : PickAirSource(metric);
            var preview = source is null;
            var value = source is not null && source.AirReadings.TryGetValue(metric.Standard, out var reading)
                ? reading
                : metric.PreviewValue;
            var grade = AirMetrics.Grade(metric, value);

            item.IsOn = CardShown(metric.Key);
            item.IsPreview = preview;
            item.ValueText = AirMetrics.FormatValue(metric, value);
            item.GradeText = grade.Label;
            item.IsGood = grade.Tone == AirTone.Good;
            item.IsMild = grade.Tone == AirTone.Mild;
            item.IsPoor = grade.Tone == AirTone.Poor;
            // 卡上不写「示例数据」这种多余的字：示例态只是右下角空着，鼠标移入会浮出「预览」小标
            item.SourceText = preview ? "" : AirSourceLabel(source!);
            item.TipText = AirTip(item, value, grade, source);
        }

        var shown = AirCardItems.Count(item => item.IsOn);
        if (AirCardCount != shown) AirCardCount = shown;
    }

    /// <summary>
    /// 每个指标选一台设备当来源：在线的优先，其次按设备类型（净化器、空气检测仪、
    /// 传感器这类专门测空气的排在普通设备前面），最后按房间 / 名称稳定排序。
    /// 气体浓度不做全屋平均——把两个房间的甲醛平均掉没有意义。
    /// </summary>
    private MijiaDevice? PickAirSource(AirMetric metric) =>
        _allDevices
            .Where(d => d.AirReadings.ContainsKey(metric.Standard))
            .OrderBy(d => d.IsOnline ? 0 : 1)
            .ThenBy(AirSourceRank)
            .ThenBy(d => d.RoomName, DeviceNameComparer)
            .ThenBy(d => d.Name, DeviceNameComparer)
            .FirstOrDefault();

    private static int AirSourceRank(MijiaDevice d)
    {
        var category = d.CategoryName;
        if (category.Contains("purifier", StringComparison.OrdinalIgnoreCase)) return 0;
        if (category.Contains("air-quality", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("air-monitor", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("sensor", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    /// <summary>卡上那句来源：优先房间（同一个房间里有几台都能读出这个数），没分房间才写设备名。</summary>
    private static string AirSourceLabel(MijiaDevice d)
    {
        var room = (d.RoomName ?? "").Trim();
        return room.Length == 0 || room == "未分配房间" ? d.Name : room;
    }

    private static string AirTip(AirCardItem item, double value, AirGrade grade, MijiaDevice? source)
    {
        var metric = item.Metric;
        var head = $"{metric.Subject} {AirMetrics.FormatValue(metric, value)}{metric.Unit} · {grade.Label}";
        if (source is null)
            return $"{head} · 示例数据，接入能检测{metric.Subject}的设备后会自动换成真实读数";

        var room = (source.RoomName ?? "").Trim();
        var where = room.Length == 0 || room == "未分配房间" ? source.Name : $"{source.Name}（{room}）";
        return $"{head} · 数据来自 {where}";
    }

    /// <summary>家里有没有能报某个空气指标的设备（编辑弹窗据此判断要不要留那句「暂无设备」）。</summary>
    private bool HasAirMetric(string standard) =>
        _allDevices.Any(d => d.Spec?.FindMetric(standard) is not null);
}
