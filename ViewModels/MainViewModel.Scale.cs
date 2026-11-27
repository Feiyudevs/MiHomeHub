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

    /// <summary>点阵每一格的下沿 / 上沿：一格横轴压了多次称重时，画出这一段的波动范围。</summary>
    [ObservableProperty] private IReadOnlyList<double> _scaleTrendLows = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _scaleTrendHighs = Array.Empty<double>();
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
                ScaleTrendLows = Array.Empty<double>();
                ScaleTrendHighs = Array.Empty<double>();
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

            // 一天只留最后一次：同一天翻来覆去上秤，不该在图上叠出一串锯齿
            var daily = records
                .GroupBy(r => r.Time.Date)
                .Select(g => g.Last())
                .OrderBy(r => r.Time)
                .ToList();

            // 用户选的跨度先按今天取；这一段没记录就把窗口整体后推到最近一次称重，
            // 点数还不够画就把跨度放大——两个月没上秤时，别只剩一张空坐标轴
            var filter = FitScaleWindow(daily, DateTime.Today, ScaleRangeDays);
            var inWindow = daily
                .Where(r => r.Time.Date >= filter.Start && r.Time.Date <= filter.End)
                .ToList();

            // 窗口里只有一段有记录（例如一年只称了两个月）时，横轴收口到首尾称重日：
            // 按整年铺格子会把点全挤到右边，左边一大块空白，看着像卡没铺满
            var window = filter;
            if (inWindow.Count > 0)
            {
                var first = inWindow[0].Time.Date;
                var last = inWindow[^1].Time.Date;
                if ((first - filter.Start).Days >= TrimGapDays || (filter.End - last).Days >= TrimGapDays)
                    window = new ScaleWindow(first, last, (last - first).Days + 1);
            }

            ScaleTrendSeries = ScaleBuckets(inWindow, window, out var lows, out var highs, out var labels);
            ScaleTrendLows = lows;
            ScaleTrendHighs = highs;
            ScaleTrendLabels = labels;
            ShowScaleChart = inWindow.Count >= 3;
            ScaleTrendPlaceholder = ShowScaleChart
                ? ""
                : $"已记录 {inWindow.Count} 天 · 攒够 3 天后出曲线";

            var min = inWindow.Count > 0 ? inWindow.Min(r => r.WeightKg) : 0;
            var max = inWindow.Count > 0 ? inWindow.Max(r => r.WeightKg) : 0;
            ScaleTrendRange = inWindow.Count >= 2
                ? $"{ScaleRangeText(window, filter)} {min:0.0}–{max:0.0}kg"
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

    /// <summary>实际画出来的那一截：起点、终点、跨度（天）。</summary>
    private readonly record struct ScaleWindow(DateTime Start, DateTime End, int Span);

    /// <summary>可放大的跨度档位，和下拉里的 30 / 90 / 半年 / 1 年对齐。</summary>
    private static readonly int[] ScaleLadder = { 30, 90, 180, 365 };

    /// <summary>一张卡最多铺这么多个点：再多点就叠在一起了，只有超了才让一格横轴代表几天。</summary>
    private const int ScaleMaxBuckets = 30;

    /// <summary>
    /// 让选中的跨度落到真的有称重的那几天上：先按今天取窗；不足 3 天记录就把窗口整体后推到
    /// 最近一次称重；还不够画就往上放大跨度。返回的窗口就是图上实际画的那一段。
    /// </summary>
    private static ScaleWindow FitScaleWindow(IReadOnlyList<ScaleRecord> daily, DateTime today, int preferred)
    {
        var last = daily[^1].Time.Date;
        var span = preferred;
        var end = today;

        if (CountScaleDays(daily, today, span) < 3 && last < today) end = last;

        var count = CountScaleDays(daily, end, span);
        if (count < 3)
        {
            // 只有真的能多出称重日才放大：一年只称过两次，撑成一年也只是两个点
            foreach (var bigger in ScaleLadder)
            {
                if (bigger <= preferred) continue;
                if (CountScaleDays(daily, end, bigger) >= 3) { span = bigger; break; }
            }
        }

        return new ScaleWindow(end.Date.AddDays(-(span - 1)), end.Date, span);
    }

    private static int CountScaleDays(IReadOnlyList<ScaleRecord> daily, DateTime end, int span)
    {
        var start = end.Date.AddDays(-(span - 1));
        var count = 0;
        foreach (var r in daily)
            if (r.Time.Date >= start && r.Time.Date <= end.Date) count++;
        return count;
    }

    /// <summary>
    /// 把窗口里的称重日摊到横轴上：称重日放得下就一天一格，一点落在一个真实的称重日；
    /// 只有多到会挤在一起（超过 <see cref="ScaleMaxBuckets"/> 个点）才按固定天数合并，
    /// 均值做点、区间最低到最高做短竖线。
    /// </summary>
    private static double[] ScaleBuckets(IReadOnlyList<ScaleRecord> daily, ScaleWindow window,
        out double[] lows, out double[] highs, out string[] labels)
    {
        var span = Math.Max(1, window.Span);

        var inWindow = new List<ScaleRecord>();
        foreach (var r in daily)
        {
            if (r.Time.Date < window.Start || r.Time.Date > window.End) continue;
            inWindow.Add(r);
        }

        var bucketDays = ResolveBucketDays(inWindow, window, span);
        var count = Math.Max(1, (int)Math.Ceiling(span / (double)bucketDays));

        var slots = new List<ScaleRecord>?[count];
        foreach (var r in inWindow)
        {
            var idx = (r.Time.Date - window.Start).Days / bucketDays;
            idx = Math.Clamp(idx, 0, count - 1);
            (slots[idx] ??= new List<ScaleRecord>()).Add(r);
        }

        var averages = new double[count];
        lows = new double[count];
        highs = new double[count];
        labels = new string[count];
        Array.Fill(averages, double.NaN);
        Array.Fill(lows, double.NaN);
        Array.Fill(highs, double.NaN);

        var thisYear = DateTime.Now.Year;
        string DayText(DateTime d) => d.Year == thisYear ? d.ToString("M/d") : d.ToString("yyyy/M/d");

        for (var i = 0; i < count; i++)
        {
            var slot = slots[i];
            if (slot is null)
            {
                // 空格也要有标签：悬停到这片空白时，气泡写的是它代表的那几天
                var from = window.Start.AddDays(i * (double)bucketDays);
                var to = window.Start.AddDays(Math.Min(span, (i + 1) * (double)bucketDays) - 1);
                labels[i] = from == to ? DayText(from) : $"{DayText(from)}–{DayText(to)}";
                continue;
            }

            // 有称重的格子按真实称重日标：一格只压到一天时，别硬写成「8/5–8/6」这两天一段
            var firstDay = slot[0].Time.Date;
            var lastDay = slot[^1].Time.Date;
            labels[i] = firstDay == lastDay ? DayText(firstDay) : $"{DayText(firstDay)}–{DayText(lastDay)}";

            var sum = 0.0;
            var lo = double.MaxValue;
            var hi = double.MinValue;
            foreach (var r in slot)
            {
                sum += r.WeightKg;
                if (r.WeightKg < lo) lo = r.WeightKg;
                if (r.WeightKg > hi) hi = r.WeightKg;
            }
            averages[i] = sum / slot.Count;
            lows[i] = lo;
            highs[i] = hi;
        }

        return averages;
    }

    /// <summary>
    /// 一格横轴压几天：称重日不超过 <see cref="ScaleMaxBuckets"/> 就一天一格，
    /// 点落在真实称重日上，横轴空着也认；真挤了才从小到大试最小合并天数，刚好压进上限就停。
    /// </summary>
    private static int ResolveBucketDays(IReadOnlyList<ScaleRecord> days, ScaleWindow window, int span)
    {
        var offsets = new List<int>(days.Count);
        foreach (var r in days)
        {
            var offset = (r.Time.Date - window.Start).Days;
            if (offset < 0 || offset >= span || offsets.Contains(offset)) continue;
            offsets.Add(offset);
        }
        if (offsets.Count <= ScaleMaxBuckets) return 1;

        var occupied = new HashSet<int>();
        for (var bucketDays = 2; bucketDays < span; bucketDays++)
        {
            occupied.Clear();
            foreach (var offset in offsets) occupied.Add(offset / bucketDays);
            if (occupied.Count <= ScaleMaxBuckets) return bucketDays;
        }
        return span;
    }

    /// <summary>
    /// 卡片右侧的窗口说明：请求的那段从头到尾都有记录就写「近 30 天」，
    /// 后推或收口过就写实际画出来的日期段，别让「近 1 年」指着一个两个月的横轴。
    /// </summary>
    private static string ScaleRangeText(ScaleWindow window, ScaleWindow filter) =>
        window.Start == filter.Start && window.End == filter.End && filter.End.Date == DateTime.Today
            ? ScaleWindowText(window.Span)
            : DateRangeText(window.Start, window.End);

    /// <summary>日期段：同年只写 M/d，跨年给起点补上年份。</summary>
    private static string DateRangeText(DateTime start, DateTime end)
    {
        var head = start.Year == DateTime.Now.Year ? start.ToString("M/d") : start.ToString("yyyy/M/d");
        return $"{head}–{end:M/d}";
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
