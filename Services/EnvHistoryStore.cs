// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiHomeHub.Services;

/// <summary>某台设备在一格里的读数：任一项都可能为空（没这个属性，或者离线没读到）。</summary>
public sealed record EnvReading(double? Temp, double? Humidity, double? Power, double? EnergyKwh = null,
    double? WaterLiters = null, double? Tds = null, double? GasM3 = null);

/// <summary>
/// 一条环境采样：全屋口径的温湿度与功率，外加每台设备自己的那一份。
/// 卡片默认看全屋口径；切到某台设备时读 <see cref="ByDevice"/> 里它自己的历史。
/// </summary>
public sealed record EnvPoint(DateTime Time, double? Temp, double? Humidity, double? Power,
    IReadOnlyDictionary<string, EnvReading>? ByDevice = null);

/// <summary>
/// 首页「环境与能耗」用的本地历史：每 5 分钟记一格，保留 7 天。
///
/// 米家云接口只给当前读数，没有历史序列，所以曲线全部靠这里攒：
/// 宁可启动时曲线是空的，也不拿当前值回填出假的过去。落盘文件很小（7 天约 2000 点），
/// 只在真正追加新格时写一次，同一格内的刷新只改内存里的最后一点。
/// </summary>
public sealed class EnvHistoryStore
{
    /// <summary>采样间隔（分钟）。改这个值要同步改 <see cref="MainViewModel.TrendStepMinutes"/> 的口径。</summary>
    public const int SampleMinutes = 5;

    private const int KeepDays = 7;
    private const int MaxPoints = KeepDays * 24 * 60 / SampleMinutes;

    private readonly string _file;
    private readonly List<EnvPoint> _points = new();

    /// <summary>冷启动基准：首帧温湿度常是登录前设备上报的旧缓存，等下一次读数靠近它才开始攒。</summary>
    private double? _baselineTemp;
    private double? _baselineHumidity;
    private int _baselineMisses;

    /// <summary>相邻两格温湿度允许的最大跳变；超过就当作噪声，不写进这一项。</summary>
    private const double MaxTempJump = 2.5;
    private const double MaxHumidityJump = 20;

    public EnvHistoryStore(string file)
    {
        _file = file;
        Load();
    }

    /// <summary>正式数据目录下的历史文件（%APPDATA%\Feiyu\MiHomeHub\env-history.json）。</summary>
    public static EnvHistoryStore ForAppData() => new(Path.Combine(AppPaths.Dir, "env-history.json"));

    public IReadOnlyList<EnvPoint> Points => _points;

    /// <summary>
    /// 记一次采样。不足一格的更新最后一格（只覆盖本次读到的项，没读到的不抹掉旧值），
    /// 满一格才追加并落盘。返回是否新增了一个采样点。
    /// <paramref name="byDevice"/> 是每台设备各自的读数，供卡片按设备查看。
    /// </summary>
    public bool Sample(DateTime now, double? temp, double? humidity, double? power,
        IReadOnlyDictionary<string, EnvReading>? byDevice = null)
    {
        // 刚启动时第一帧温度常是登录前设备报的旧缓存（会出现 29℃ 掉到 15℃ 这种不可能的跳变）。
        // 先只记基准，等下一帧读数靠近它再开始攒，最多耽误一个轮询周期。
        if (_points.Count == 0 && temp is { } firstTemp && _baselineMisses < 3)
        {
            var settled = _baselineTemp is { } baselineTemp
                          && Math.Abs(baselineTemp - firstTemp) <= 1.5
                          && (_baselineHumidity is not { } baselineHum
                              || humidity is not { } firstHum
                              || Math.Abs(baselineHum - firstHum) <= 15);

            if (settled)
            {
                _baselineTemp = null;
                _baselineHumidity = null;
            }
            else
            {
                _baselineTemp = firstTemp;
                _baselineHumidity = humidity;
                _baselineMisses++;
                return false;
            }
        }

        if (_points.Count > 0)
        {
            var last = _points[^1];
            var since = now - last.Time;
            if (since < TimeSpan.Zero) return false; // 系统时间被回拨，不写乱序点

            // 室内温湿度在 5 分钟里的真实变化远小于这个幅度，超出的按噪声丢掉这一项，
            // 免得一次陈旧上报在曲线上拉出尖刺，还把「最高」顶成不可能的值
            if (last.Temp is { } lastTemp && temp is { } t && Math.Abs(t - lastTemp) > MaxTempJump) temp = null;
            if (last.Humidity is { } lastHum && humidity is { } h && Math.Abs(h - lastHum) > MaxHumidityJump) humidity = null;
            byDevice = GuardDevices(byDevice, last.ByDevice);

            if (since < TimeSpan.FromMinutes(SampleMinutes))
            {
                _points[^1] = new EnvPoint(last.Time,
                    temp ?? last.Temp, humidity ?? last.Humidity, power ?? last.Power,
                    MergeDevices(last.ByDevice, byDevice));
                return false;
            }
        }

        _points.Add(new EnvPoint(now, temp, humidity, power, byDevice));
        _baselineMisses = 0;
        Trim(now);
        Save();
        return true;
    }

    /// <summary>
    /// 近 <paramref name="lookback"/> 内某一项攒到了多少格、从第一格到最后一格跨了多久。
    /// 早期数据太少时用它决定横轴画多宽，而不是一律铺满 24 小时。
    /// </summary>
    public (int Count, TimeSpan Span) Coverage(Func<EnvPoint, double?> pick, DateTime now, TimeSpan lookback)
    {
        DateTime? first = null;
        DateTime? last = null;
        var count = 0;
        var from = now - lookback;

        foreach (var p in _points)
        {
            if (p.Time < from || p.Time > now) continue;
            if (pick(p) is null) continue;
            count++;
            first ??= p.Time;
            last = p.Time;
        }

        var span = first is { } f && last is { } l ? l - f : TimeSpan.Zero;
        return (count, span);
    }

    /// <summary>
    /// 把最近 <paramref name="window"/> 的场景按 <paramref name="step"/> 回放成定长序列。
    /// 该格附近没有采样就填 NaN——曲线控件会把前后两个有值的点照样连起来，不会断开。
    /// </summary>
    public double[] Resample(Func<EnvPoint, double?> pick, DateTime now, TimeSpan window, TimeSpan step)
    {
        var slots = Math.Max(2, (int)Math.Round(window / step) + 1);
        var series = new double[slots];
        Array.Fill(series, double.NaN);
        if (_points.Count == 0) return series;

        var tolerance = step * 0.6;
        var start = now - step * (slots - 1);
        var cursor = 0;

        for (var i = 0; i < slots; i++)
        {
            var target = start + step * i;
            while (cursor < _points.Count && _points[cursor].Time < target - tolerance)
                cursor++;
            if (cursor >= _points.Count) break;

            var point = _points[cursor];
            if (point.Time > target + tolerance) continue; // 这一格是空的，留着 NaN
            if (pick(point) is { } value) series[i] = value;
        }
        return series;
    }

    /// <summary>最近一次有值的采样（用于「当前」读数与趋势无关时的兜底）。</summary>
    public double? Latest(Func<EnvPoint, double?> pick)
    {
        for (var i = _points.Count - 1; i >= 0; i--)
            if (pick(_points[i]) is { } v) return v;
        return null;
    }

    /// <summary>某台设备最近一次有值的读数：重启后拿它给合成水表/气表接上原来的累计值。</summary>
    public double? LatestDevice(string did, Func<EnvReading, double?> pick)
    {
        if (string.IsNullOrEmpty(did)) return null;

        for (var i = _points.Count - 1; i >= 0; i--)
        {
            var by = _points[i].ByDevice;
            if (by is null || !by.TryGetValue(did, out var reading)) continue;
            if (pick(reading) is { } v) return v;
        }
        return null;
    }

    /// <summary>
    /// 这台设备在这一项上最早的一条采样时刻；从没采到过就返回 null。
    /// 卡片刚切到某台设备时用它划界：在它开始上报之前，那段曲线沿用全屋口径，
    /// 切换那一刻之后再接这台设备自己的读数——旧数据不删也不空一大截。
    /// </summary>
    public DateTime? FirstAt(string did, Func<EnvReading, double?> pick)
    {
        if (string.IsNullOrEmpty(did)) return null;

        foreach (var p in _points)
            if (p.ByDevice is { } by && by.TryGetValue(did, out var reading) && pick(reading) is { })
                return p.Time;
        return null;
    }

    /// <summary>
    /// 区间用电量（kWh）：按采样间隔对功率做梯形积分。<paramref name="did"/> 为空表示全屋叠加，
    /// 否则只算这台设备。
    /// 同时给出采样覆盖率——覆盖率太低时前端不显示这个数字，免得把「刚装好一小时」读成全天用电。
    /// </summary>
    public (double Kwh, double Coverage) Energy(DateTime from, DateTime to, string? did = null)
    {
        var total = (to - from).TotalSeconds;
        if (total <= 0) return (0, 0);

        double kwh = 0, covered = 0;
        for (var i = 1; i < _points.Count; i++)
        {
            var a = _points[i - 1];
            var b = _points[i];
            if (PowerOf(a, did) is not { } pa || PowerOf(b, did) is not { } pb) continue;

            var segStart = a.Time > from ? a.Time : from;
            var segEnd = b.Time < to ? b.Time : to;
            if (segEnd <= segStart) continue;

            var hours = (segEnd - segStart).TotalHours;
            // 功率是 W、时长是 h，乘积是 Wh，换算成度要除以 1000
            kwh += (pa + pb) / 2 * hours / 1000;
            covered += (segEnd - segStart).TotalSeconds;
        }

        // 只有累计耗电量（power-consumption）的设备：空调、浴霸这类不给瞬时功率，
        // 直接用这一格里的计数器差值（计数器本身就是 kWh，不用再积分）。
        foreach (var deviceDid in CounterDids(did))
        {
            if (HasWatts(deviceDid, from, to)) continue; // 有瞬时功率就以它为准，免得同一台设备算两遍
            if (CounterDelta(deviceDid, from, to) is not { } d) continue;
            kwh += d.Kwh;
            covered = Math.Max(covered, d.Seconds);
        }

        return (kwh, Math.Clamp(covered / total, 0, 1));
    }

    /// <summary>
    /// 今天逐小时的平均功率：24 格，还没到的时段与没有采样的时段都是 NaN。
    /// <paramref name="did"/> 为空表示全屋叠加，否则只算这台设备。
    /// </summary>
    public double[] HourlyPower(DateTime now, string? did = null)
    {
        var buckets = new double[24];
        Array.Fill(buckets, double.NaN);
        var today = now.Date;
        var sum = new double[24];
        var count = new int[24];

        foreach (var p in _points)
        {
            if (p.Time.Date != today || PowerOf(p, did) is not { } watts) continue;
            var hour = p.Time.Hour;
            sum[hour] += watts;
            count[hour]++;
        }
        for (var h = 0; h < 24; h++)
            if (count[h] > 0) buckets[h] = sum[h] / count[h];

        // 没瞬时功率的设备（空调这类）按累计电量差折成这一小时的平均功率
        foreach (var deviceDid in CounterDids(did))
        {
            if (HasWatts(deviceDid, today, now)) continue;
            foreach (var (hour, watts) in CounterHourly(deviceDid, today, now))
                buckets[hour] = double.IsNaN(buckets[hour]) ? watts : buckets[hour] + watts;
        }

        return buckets;
    }

    /// <summary>
    /// 区间用水量（升）：每一格的水表读数与上一格比较，只累加正向增量——
    /// 水表清零、换滤芯造成的回落不算用水。覆盖率太低时前端只报「今天记了多少」，
    /// 不把刚启动这两小时当成一整天的用水量。
    /// </summary>
    public (double Liters, double Coverage, TimeSpan Span) Water(DateTime from, DateTime to, string? did = null) =>
        Usage(from, to, did, WaterOf, WaterDids);

    /// <summary>区间用气量（m³）：口径与用水一致，气表同样是只增计数器。</summary>
    public (double M3, double Coverage, TimeSpan Span) Gas(DateTime from, DateTime to, string? did = null) =>
        Usage(from, to, did, GasOf, GasDids);

    private (double Value, double Coverage, TimeSpan Span) Usage(DateTime from, DateTime to, string? did,
        Func<EnvPoint, string, double?> valueOf, Func<string?, IEnumerable<string>> dids)
    {
        var total = (to - from).TotalSeconds;
        if (total <= 0) return (0, 0, TimeSpan.Zero);

        double value = 0, bestCovered = 0;
        TimeSpan bestSpan = TimeSpan.Zero;

        foreach (var deviceDid in dids(did))
        {
            double? prev = null;
            DateTime prevAt = default;
            DateTime? firstAt = null, lastAt = null;
            double covered = 0;

            foreach (var p in _points)
            {
                if (p.Time > to) break;
                if (valueOf(p, deviceDid) is not { } current)
                {
                    // 中间断档：下一格不能跨过这段空白去和旧值相减
                    if (p.Time >= from) prev = null;
                    continue;
                }
                if (p.Time < from)
                {
                    // 区间起点前最多留一格做基准（15 分钟内），否则 00:00 后的第一段用水会被吃掉
                    prev = p.Time >= from.AddMinutes(-15) ? current : null;
                    prevAt = p.Time;
                    continue;
                }
                if (prev is { } before)
                {
                    var delta = current - before;
                    if (delta > 0) value += delta;
                    var seconds = (p.Time - prevAt).TotalSeconds;
                    if (seconds > 0 && seconds <= SampleMinutes * 3 * 60)
                        covered += seconds;
                }
                firstAt ??= p.Time;
                lastAt = p.Time;
                prev = current;
                prevAt = p.Time;
            }

            if (firstAt is { } f && lastAt is { } l && l - f > bestSpan) bestSpan = l - f;
            if (covered > bestCovered) bestCovered = covered;
        }

        return (value, Math.Clamp(bestCovered / total, 0, 1), bestSpan);
    }

    /// <summary>
    /// 今天逐小时用水量（升）：24 格，还没到的时段与没有采样的时段都是 NaN；
    /// 有采样但没有用水的小时记 0，柱子留空但悬停能看到 0 L。
    /// </summary>
    public double[] HourlyWater(DateTime now, string? did = null) =>
        HourlyUsage(now, did, WaterOf, WaterDids);

    /// <summary>今天逐小时用气量（m³），口径与 <see cref="HourlyWater"/> 一致。</summary>
    public double[] HourlyGas(DateTime now, string? did = null) =>
        HourlyUsage(now, did, GasOf, GasDids);

    private double[] HourlyUsage(DateTime now, string? did,
        Func<EnvPoint, string, double?> valueOf, Func<string?, IEnumerable<string>> dids)
    {
        var buckets = new double[24];
        Array.Fill(buckets, double.NaN);
        var today = now.Date;
        var seen = new bool[24];

        foreach (var deviceDid in dids(did))
        {
            double? prev = null;
            DateTime prevAt = default;

            foreach (var p in _points)
            {
                if (p.Time > now) break;
                if (valueOf(p, deviceDid) is not { } current)
                {
                    prev = null;
                    continue;
                }

                var inToday = p.Time >= today;
                if (prev is { } before && inToday)
                {
                    var seconds = (p.Time - prevAt).TotalSeconds;
                    if (seconds > 0 && seconds <= SampleMinutes * 3 * 60)
                    {
                        var hour = p.Time.Hour;
                        seen[hour] = true;
                        var delta = current - before;
                        if (delta > 0)
                            buckets[hour] = double.IsNaN(buckets[hour]) ? delta : buckets[hour] + delta;
                    }
                }

                prev = current;
                prevAt = p.Time;
            }
        }

        for (var h = 0; h < 24; h++)
            if (seen[h] && double.IsNaN(buckets[h])) buckets[h] = 0;

        return buckets;
    }

    /// <summary>这台净水器某一格的出水 TDS；没有采到就返回 null（给水质卡画曲线用）。</summary>
    public double? TdsOf(EnvPoint p, string did) =>
        p.ByDevice is { } by && by.TryGetValue(did, out var reading) ? reading.Tds : null;

    /// <summary>要按水表算用量的设备：指定了就只看它，否则把历史里所有报过水表的设备都列出来。</summary>
    private IEnumerable<string> WaterDids(string? did)
    {
        if (!string.IsNullOrEmpty(did))
        {
            yield return did;
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _points)
        {
            if (p.ByDevice is not { } by) continue;
            foreach (var (deviceDid, reading) in by)
                if (reading.WaterLiters is not null && seen.Add(deviceDid)) yield return deviceDid;
        }
    }

    private static double? WaterOf(EnvPoint p, string did) =>
        p.ByDevice is { } by && by.TryGetValue(did, out var reading) ? reading.WaterLiters : null;

    /// <summary>要按气表算用量的设备：指定了就只看它，否则把历史里所有报过气表的设备都列出来。</summary>
    private IEnumerable<string> GasDids(string? did)
    {
        if (!string.IsNullOrEmpty(did))
        {
            yield return did;
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _points)
        {
            if (p.ByDevice is not { } by) continue;
            foreach (var (deviceDid, reading) in by)
                if (reading.GasM3 is not null && seen.Add(deviceDid)) yield return deviceDid;
        }
    }

    private static double? GasOf(EnvPoint p, string did) =>
        p.ByDevice is { } by && by.TryGetValue(did, out var reading) ? reading.GasM3 : null;

    /// <summary>取某一格的功率：空 did = 全屋叠加值，否则取该设备自己的那项。</summary>
    private static double? PowerOf(EnvPoint p, string? did) =>
        string.IsNullOrEmpty(did)
            ? p.Power
            : p.ByDevice is { } by && by.TryGetValue(did, out var reading) ? reading.Power : null;

    /// <summary>取某一格的累计耗电量（kWh）。</summary>
    private static double? CounterOf(EnvPoint p, string did) =>
        p.ByDevice is { } by && by.TryGetValue(did, out var reading) ? reading.EnergyKwh : null;

    /// <summary>要按计数器算电量的设备：指定了就只看它，否则把历史里所有报过累计电量的设备都列出来。</summary>
    private IEnumerable<string> CounterDids(string? did)
    {
        if (!string.IsNullOrEmpty(did))
        {
            yield return did;
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _points)
        {
            if (p.ByDevice is not { } by) continue;
            foreach (var (deviceDid, reading) in by)
                if (reading.EnergyKwh is not null && seen.Add(deviceDid)) yield return deviceDid;
        }
    }

    private bool HasWatts(string did, DateTime from, DateTime to)
    {
        foreach (var p in _points)
            if (p.Time >= from && p.Time <= to && PowerOf(p, did) is not null) return true;
        return false;
    }

    /// <summary>区间内的计数器差值与跨度；差值会跟着设备重启/清零变负，负的一律当没数据。</summary>
    private (double Kwh, double Seconds)? CounterDelta(string did, DateTime from, DateTime to)
    {
        double? first = null, last = null;
        var firstAt = default(DateTime);
        var lastAt = default(DateTime);

        foreach (var p in _points)
        {
            if (p.Time < from || p.Time > to) continue;
            if (CounterOf(p, did) is not { } kwh) continue;
            if (first is null)
            {
                first = kwh;
                firstAt = p.Time;
            }
            last = kwh;
            lastAt = p.Time;
        }

        if (first is not { } a || last is not { } b) return null;
        var delta = b - a;
        if (delta < 0) return null;
        return (delta, (lastAt - firstAt).TotalSeconds);
    }

    /// <summary>把某个小时的计数器差值折成平均功率（W）。</summary>
    private IEnumerable<(int Hour, double Watts)> CounterHourly(string did, DateTime day, DateTime until)
    {
        double?[] first = new double?[24], last = new double?[24];
        var firstAt = new DateTime[24];
        var lastAt = new DateTime[24];

        foreach (var p in _points)
        {
            if (p.Time.Date != day || p.Time > until) continue;
            if (CounterOf(p, did) is not { } kwh) continue;
            var h = p.Time.Hour;
            if (first[h] is null)
            {
                first[h] = kwh;
                firstAt[h] = p.Time;
            }
            last[h] = kwh;
            lastAt[h] = p.Time;
        }

        for (var h = 0; h < 24; h++)
        {
            if (first[h] is not { } a || last[h] is not { } b) continue;
            if (lastAt[h] <= firstAt[h]) continue; // 这一小时只有一个读数，差值没有意义
            var delta = b - a;
            if (delta < 0) continue;
            var hours = Math.Max((lastAt[h] - firstAt[h]).TotalHours, 5.0 / 60);
            yield return (h, delta / hours * 1000);
        }
    }

    /// <summary>逐台设备挡住不可能的跳变（口径与全屋值一致），挡掉的项记空。</summary>
    private static IReadOnlyDictionary<string, EnvReading>? GuardDevices(
        IReadOnlyDictionary<string, EnvReading>? fresh,
        IReadOnlyDictionary<string, EnvReading>? previous)
    {
        if (fresh is null || fresh.Count == 0) return fresh;
        if (previous is null || previous.Count == 0) return fresh;

        var guarded = new Dictionary<string, EnvReading>(fresh.Count, StringComparer.Ordinal);
        var changed = false;

        foreach (var (did, reading) in fresh)
        {
            var temp = reading.Temp;
            var humidity = reading.Humidity;

            if (previous.TryGetValue(did, out var prev))
            {
                if (prev.Temp is { } pt && temp is { } tv && Math.Abs(tv - pt) > MaxTempJump)
                {
                    temp = null;
                    changed = true;
                }
                if (prev.Humidity is { } ph && humidity is { } hv && Math.Abs(hv - ph) > MaxHumidityJump)
                {
                    humidity = null;
                    changed = true;
                }
            }

            guarded[did] = temp == reading.Temp && humidity == reading.Humidity
                ? reading
                : new EnvReading(temp, humidity, reading.Power, reading.EnergyKwh,
                    reading.WaterLiters, reading.Tds, reading.GasM3);
        }

        return changed ? guarded : fresh;
    }

    /// <summary>同一格里重复采样：本次读到的项覆盖旧值，没读到的不抹掉上一笔。</summary>
    private static IReadOnlyDictionary<string, EnvReading>? MergeDevices(
        IReadOnlyDictionary<string, EnvReading>? old,
        IReadOnlyDictionary<string, EnvReading>? fresh)
    {
        if (fresh is null || fresh.Count == 0) return old;
        if (old is null || old.Count == 0) return fresh;

        var merged = new Dictionary<string, EnvReading>(old.Count + fresh.Count, StringComparer.Ordinal);
        foreach (var (did, reading) in old) merged[did] = reading;

        foreach (var (did, reading) in fresh)
        {
            merged[did] = merged.TryGetValue(did, out var prev)
                ? new EnvReading(reading.Temp ?? prev.Temp, reading.Humidity ?? prev.Humidity, reading.Power ?? prev.Power, reading.EnergyKwh ?? prev.EnergyKwh, reading.WaterLiters ?? prev.WaterLiters, reading.Tds ?? prev.Tds, reading.GasM3 ?? prev.GasM3)
                : reading;
        }
        return merged;
    }

    /// <summary>
    /// 载入后清一遍可疑读数：老文件里可能留着按旧规则记下的跳变，
    /// 不清掉的话「最高」会被一个陈旧值顶住好几天。
    /// </summary>
    private void Sanitize()
    {
        if (_points.Count == 0) return;

        for (var i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            var prev = i > 0 && _points[i].Time - _points[i - 1].Time <= TimeSpan.FromMinutes(15)
                ? _points[i - 1]
                : null;
            var next = i + 1 < _points.Count && _points[i + 1].Time - _points[i].Time <= TimeSpan.FromMinutes(15)
                ? _points[i + 1]
                : null;

            var temp = SpikeOrColdStart(p.Temp, prev?.Temp, next?.Temp, MaxTempJump);
            var humidity = SpikeOrColdStart(p.Humidity, prev?.Humidity, next?.Humidity, MaxHumidityJump);
            if (temp != p.Temp || humidity != p.Humidity)
                _points[i] = p with { Temp = temp, Humidity = humidity };
        }
    }

    /// <summary>单独一格离谱、或头一格与第二格差太多（冷启动陈旧读数）时返回 null，其余原样返回。</summary>
    private static double? SpikeOrColdStart(double? value, double? prev, double? next, double threshold)
    {
        if (value is not { } v) return value;

        // 头一格：跟第二格差太多，多半是登录前缓存的旧读数
        if (prev is null && next is { } n && Math.Abs(v - n) > threshold) return null;

        // 单点尖刺：前后两格都正常，只有它自己离谱
        if (prev is { } pv && next is { } nv
            && Math.Abs(v - pv) > threshold && Math.Abs(v - nv) > threshold
            && Math.Abs(pv - nv) <= threshold)
            return null;

        // 末尾一格：跟上一格差太多，先当可疑值搁置，下一格采到就接上
        if (next is null && prev is { } lp && Math.Abs(v - lp) > threshold) return null;

        return value;
    }

    private void Trim(DateTime now)
    {
        var cutoff = now.AddDays(-KeepDays);
        var drop = 0;
        while (drop < _points.Count && _points[drop].Time < cutoff) drop++;
        if (drop > 0) _points.RemoveRange(0, drop);
        else if (_points.Count > MaxPoints) _points.RemoveRange(0, _points.Count - MaxPoints);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var raw = JsonSerializer.Deserialize<RawFile>(File.ReadAllText(_file));
            if (raw?.Points is not { Count: > 0 } list) return;

            var now = DateTime.Now;
            var cutoff = now.AddDays(-KeepDays);
            foreach (var p in list)
            {
                var time = new DateTime(p.T);
                // 时钟错乱（未来点/超期点）直接丢弃，避免曲线右端出现一条永远到不了的尾巴
                if (time < cutoff || time > now.AddMinutes(SampleMinutes)) continue;
                _points.Add(new EnvPoint(time, p.A, p.B, p.C, ParseDevices(p.D)));
            }
            _points.Sort((a, b) => a.Time.CompareTo(b.Time));
            Sanitize();
        }
        catch
        {
            // 文件损坏就当没有历史：曲线从空开始重新攒，不影响其它功能
            _points.Clear();
        }
    }

    private static IReadOnlyDictionary<string, EnvReading>? ParseDevices(Dictionary<string, double?[]>? raw)
    {
        if (raw is not { Count: > 0 }) return null;

        var map = new Dictionary<string, EnvReading>(raw.Count, StringComparer.Ordinal);
        foreach (var (did, values) in raw)
        {
            if (did.Length == 0 || values is null) continue;
            map[did] = new EnvReading(
                values.Length > 0 ? values[0] : null,
                values.Length > 1 ? values[1] : null,
                values.Length > 2 ? values[2] : null,
                values.Length > 3 ? values[3] : null,
                values.Length > 4 ? values[4] : null,
                values.Length > 5 ? values[5] : null,
                values.Length > 6 ? values[6] : null);
        }
        return map.Count > 0 ? map : null;
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);

            var raw = new RawFile
            {
                Points = _points.Select(p => new RawPoint
                {
                    T = p.Time.Ticks,
                    A = p.Temp,
                    B = p.Humidity,
                    C = p.Power,
                    D = p.ByDevice is { Count: > 0 } by
                        ? by.ToDictionary(kv => kv.Key,
                            kv => new double?[] { kv.Value.Temp, kv.Value.Humidity, kv.Value.Power, kv.Value.EnergyKwh, kv.Value.WaterLiters, kv.Value.Tds, kv.Value.GasM3 })
                        : null
                }).ToList()
            };
            var json = JsonSerializer.Serialize(raw);
            // 先写临时文件再替换：写到一半断电也不会留下半截 JSON
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch
        {
            // 落盘失败不阻塞轮询，内存里的曲线照常更新
        }
    }

    private sealed class RawFile
    {
        [JsonPropertyName("points")] public List<RawPoint>? Points { get; set; }
    }

    private sealed class RawPoint
    {
        [JsonPropertyName("t")] public long T { get; set; }
        [JsonPropertyName("a")] public double? A { get; set; }
        [JsonPropertyName("b")] public double? B { get; set; }
        [JsonPropertyName("c")] public double? C { get; set; }

        /// <summary>每台设备自己的读数：did → [温度, 湿度, 功率, 累计电量 kWh, 水表升数, 出水 TDS, 累计用气 m³]；老文件可能只有前几项。</summary>
        [JsonPropertyName("d")] public Dictionary<string, double?[]>? D { get; set; }
    }
}
