// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MiHomeHub.Models;
using MiHomeHub.Services;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 首页「数据看板」里的体脂秤卡片：最近一次称重的体重 / 体脂率 / BMI，和近 30 天的体重曲线。
/// 数据来自米家 eco 接口（和米家 App 的「历史记录」同一份），半小时补拉一次；本地不伪造历史。
/// </summary>
public partial class MainViewModel
{
    private ScaleHistory _scaleHistory = ScaleHistory.Empty;
    private DateTime _scaleFetchAt = DateTime.MinValue;
    private bool _scaleBusy;
    private bool _applyingScale;

    [ObservableProperty] private bool _hasScale;
    [ObservableProperty] private IReadOnlyList<OptionItem> _scaleSources = Array.Empty<OptionItem>();
    [ObservableProperty] private string _selectedScaleMember = AppSettingsStore.GetTrendSource("scale");
    [ObservableProperty] private string _selectedScaleRange = NormalizeScaleRange(AppSettingsStore.GetTrendSource("scaleRange"));

    /// <summary>体重卡能查的范围：30 天 / 90 天 / 半年 / 1 年，数据一次拉满一年，切换只是换窗口。</summary>
    public IReadOnlyList<OptionItem> ScaleRangeOptions { get; } = new List<OptionItem>
    {
        new() { Label = "30 天", Value = "30" },
        new() { Label = "90 天", Value = "90" },
        new() { Label = "半年", Value = "180" },
        new() { Label = "1 年", Value = "365" }
    };

    [ObservableProperty] private string _scaleTrendValue = "—";
    [ObservableProperty] private string _scaleTrendNote = "";
    [ObservableProperty] private string _scaleTrendMeta = "";
    [ObservableProperty] private string _scaleTrendDelta = "";
    [ObservableProperty] private bool _hasScaleDelta;
    [ObservableProperty] private IReadOnlyList<double> _scaleTrendSeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<string> _scaleTrendLabels = Array.Empty<string>();
    [ObservableProperty] private string _scaleTrendRange = "";
    [ObservableProperty] private string _scaleTrendPlaceholder = "正在读取称重记录…";
    [ObservableProperty] private bool _showScaleChart;
    [ObservableProperty] private bool _canPickScaleMember;

    partial void OnHasScaleChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPickScaleRange));
        RaiseTrendRowFlags();
    }

    private static string NormalizeScaleRange(string? raw) =>
        raw is "30" or "90" or "180" or "365" ? raw : "30";

    private int ScaleRangeDays =>
        int.TryParse(SelectedScaleRange, out var days) ? days : 30;

    private static string ScaleWindowText(int days) => days switch
    {
        365 => "近 1 年",
        180 => "近半年",
        _ => $"近 {days} 天"
    };

    partial void OnSelectedScaleRangeChanged(string value)
    {
        AppSettingsStore.SetTrendSource("scaleRange", NormalizeScaleRange(value));
        if (IsTrendPreview)
        {
            ApplyPreviewData();
            return;
        }
        // 预览卡没有真实记录：换范围重画示例曲线，别把卡刷成「还没有称重记录」
        if (IsScalePreview) ApplyScalePreview();
        else ApplyScaleData();
    }

    /// <summary>家里有没有体脂秤（规格是 scale 类设备）。</summary>
    internal static bool IsScaleDevice(MijiaDevice d) =>
        d.SpecType.Contains(":scale:", StringComparison.OrdinalIgnoreCase)
        || d.Model.StartsWith("yunmai.scales", StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedScaleMemberChanged(string value)
    {
        if (_applyingScale) return;
        AppSettingsStore.SetTrendSource("scale", value);
        ApplyScaleData();
    }

    /// <summary>
    /// 半小时补拉一次（称重不是高频数据，没必要跟着设备轮询跑）。
    /// 由 <see cref="RefreshTrendCards"/> 顺手唤起，失败静默——卡片保留上一次的读数。
    /// </summary>
    private async Task EnsureScaleHistoryAsync(bool force = false)
    {
        if (_client is not { } client || _scaleBusy) return;
        if (!force && (DateTime.Now - _scaleFetchAt).TotalMinutes < 30) return;
        if (string.IsNullOrEmpty(client.UserId)) return;

        var device = _allDevices.FirstOrDefault(IsScaleDevice);
        if (device is null) return;

        _scaleBusy = true;
        try
        {
            var history = await ScaleHistoryService.LoadAsync(client, device.TargetDid, device.Model, days: 365);
            _scaleHistory = history;
            _scaleFetchAt = DateTime.Now;
            ApplyScaleData();
        }
        catch
        {
            // 云端暂时不通就保留上一次的记录，不把卡片刷成空；没拿到过数据时给一句能看懂的话
            _scaleFetchAt = DateTime.Now;
            if (!_scaleHistory.HasAny && !ShowScaleChart)
                ScaleTrendPlaceholder = "暂时读不到称重记录";
        }
        finally
        {
            _scaleBusy = false;
        }
    }

    /// <summary>把拉到的记录摊到卡片上：成员下拉、最近一次读数、近 30 天曲线。</summary>
    private void ApplyScaleData()
    {
        _applyingScale = true;
        try
        {
            var members = _scaleHistory.Members;
            ScaleSources = members
                .Select(m => new OptionItem
                {
                    Label = string.IsNullOrWhiteSpace(m.Name) ? "成员" : m.Name,
                    Value = m.AccountId,
                    Meta = m.IsPrimary ? "主账号" : null
                })
                .ToList();

            var current = members.FirstOrDefault(m => m.AccountId == SelectedScaleMember)
                          ?? members.FirstOrDefault(m => m.IsPrimary)
                          ?? members.FirstOrDefault();

            SelectedScaleMember = current?.AccountId ?? "";
            CanPickScaleMember = members.Count > 1;

            var records = _scaleHistory.Records
                .Where(r => r.AccountId == SelectedScaleMember)
                .OrderBy(r => r.Time)
                .ToList();

            if (records.Count == 0)
            {
                ScaleTrendValue = "—";
                ScaleTrendNote = "";
                ScaleTrendMeta = "";
                ScaleTrendDelta = "";
                HasScaleDelta = false;
                ScaleTrendSeries = Array.Empty<double>();
                ScaleTrendLabels = Array.Empty<string>();
                ScaleTrendRange = "";
                ShowScaleChart = false;
                ScaleTrendPlaceholder = "还没有称重记录";
                return;
            }

            var latest = records[^1];
            ScaleTrendValue = $"{latest.WeightKg:0.0}";
            ScaleTrendNote = TimeText(latest.Time);
            ScaleTrendMeta = latest.HasComposition
                ? $"体脂 {latest.BodyFatPercent:0.0}% · BMI {latest.Bmi:0.0}"
                : (latest.Bmi > 0 ? $"仅体重 · BMI {latest.Bmi:0.0}" : "仅体重");

            // 一天取最后一次，按选中的窗口（最多一年）截，够画曲线又不至于把同一天的多次称重压成锯齿
            var rangeDays = ScaleRangeDays;
            var daily = records
                .GroupBy(r => r.Time.Date)
                .Select(g => g.Last())
                .TakeLast(rangeDays)
                .ToList();

            ScaleTrendSeries = daily.Select(r => r.WeightKg).ToList();
            // 悬停气泡只显示称重日期：同年省掉年份，跨年补上
            var thisYear = DateTime.Now.Year;
            ScaleTrendLabels = daily
                .Select(r => r.Time.Year == thisYear ? r.Time.ToString("M/d") : r.Time.ToString("yyyy/M/d"))
                .ToList();
            ShowScaleChart = daily.Count >= 3;
            ScaleTrendPlaceholder = ShowScaleChart
                ? ""
                : $"已记录 {daily.Count} 天 · 攒够 3 天后出曲线";

            var min = daily.Min(r => r.WeightKg);
            var max = daily.Max(r => r.WeightKg);
            ScaleTrendRange = daily.Count >= 2
                ? $"{ScaleWindowText(rangeDays)} {min:0.0}–{max:0.0}kg"
                : "";

            if (records.Count >= 2)
            {
                var delta = latest.WeightKg - records[^2].WeightKg;
                ScaleTrendDelta = $"较上次 {delta:+0.0;-0.0;0.0}kg";
                HasScaleDelta = Math.Abs(delta) >= 0.05;
            }
            else
            {
                ScaleTrendDelta = "";
                HasScaleDelta = false;
            }
        }
        finally
        {
            _applyingScale = false;
        }
    }

    private static string TimeText(DateTime time)
    {
        var now = DateTime.Now;
        if (time.Date == now.Date) return $"今天 {time:HH:mm} 称重";
        if (time.Date == now.Date.AddDays(-1)) return $"昨天 {time:HH:mm} 称重";
        if ((now.Date - time.Date).TotalDays < 7) return $"{time:MM-dd HH:mm} 称重";
        return $"{time:MM-dd} 称重";
    }

    /// <summary>退出登录 / 切账号：清掉上一账号的称重记录，别让卡片停在别人的体重上。</summary>
    private void ResetScaleHistory()
    {
        _scaleHistory = ScaleHistory.Empty;
        _scaleFetchAt = DateTime.MinValue;
        _scaleBusy = false;
        ApplyScaleData();
    }
}
