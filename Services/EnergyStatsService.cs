// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiHomeHub.Models;

namespace MiHomeHub.Services;

/// <summary>一台设备的云端用电统计（今天逐小时 + 今天/昨天累计）。小时值为 NaN 表示那一小时没数据。</summary>
public sealed class DeviceEnergy
{
    public required string Did { get; init; }

    /// <summary>今天 0–23 点各自用了多少度（kWh），没数据的小时是 NaN。</summary>
    public double[] HourlyKwh { get; } = Enumerable.Repeat(double.NaN, 24).ToArray();

    /// <summary>今天累计用了多少度。</summary>
    public double TodayKwh { get; set; }

    /// <summary>昨天累计用了多少度（云端没给就是 NaN）。</summary>
    public double YesterdayKwh { get; set; } = double.NaN;

    /// <summary>按天的历史（含今天），日期升序；云端只给有记录的日子，没用的天不会出现。</summary>
    public List<(DateTime Date, double Kwh)> Days { get; } = new();

    /// <summary>今天有电可用（可能是 0）。</summary>
    public bool HasData { get; set; }

    /// <summary>小时统计可用（柱子靠它；只有日统计时柱子得退回本地采样）。</summary>
    public bool HasHourly => HourlyKwh.Any(v => !double.IsNaN(v));
}

/// <summary>
/// 米家「电量统计」云接口（app/v2/user/statistics）：设备只报累计耗电量（power-consumption）时，
/// 规格本身给不出当天逐小时用了多少，米家 App 里的用电统计走的是云端统计表，这里照同一套口径取。
///
/// key 因型号而异：插座/空调多数是「siid.piid」，一部分空调是固定字面量 powerCost；
/// 每台设备第一次探测成功后按型号把 key 记进 energy-keys.json，之后不再试错。
/// 统计维度也分：多数机型只申请了按天，按小时会回 -8（invalid params），所以先按天取、再试小时。
/// </summary>
public static class EnergyStatsService
{
    private const string HourType = "stat_hour_v3";
    private const string DayType = "stat_day_v3";

    private static readonly string KeyFile = Path.Combine(AppPaths.Dir, "energy-keys.json");
    private static readonly Dictionary<string, string> KeyByModel = LoadKeys();
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedFail = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(2);

    /// <summary>这台设备上次探测成功、可直接用的统计 key；没探测过就是 null。</summary>
    public static string? CachedKey(MijiaDevice d) =>
        d.Model is { Length: > 0 } model && KeyByModel.TryGetValue(model, out var key) ? key : null;

    /// <summary>
    /// 按型号给出要试的统计 key：power-consumption / electric-power 的 siid.piid，加上空调常见的 powerCost。
    /// 规格里一个用电属性都没有的设备（灯、开关）直接返回空——它们本来就没有统计表。
    /// </summary>
    public static IReadOnlyList<string> CandidateKeys(MijiaDevice d)
    {
        if (d.Spec is not { } spec) return Array.Empty<string>();

        var keys = new List<string>(3);
        foreach (var name in new[] { "power-consumption", "electric-power" })
        {
            var prop = spec.FindMetric(name) ?? spec.FindProp(name);
            if (prop == null) continue;
            var key = $"{prop.Siid}.{prop.Piid}";
            if (!keys.Contains(key)) keys.Add(key);
        }
        if (keys.Count == 0) return Array.Empty<string>();

        if (!keys.Contains("powerCost")) keys.Add("powerCost");
        return keys;
    }

    /// <summary>这台设备是否可能有用电统计（型号已探测到 key，或规格里有用电属性）。</summary>
    public static bool MightHaveStats(MijiaDevice d) =>
        CachedKey(d) != null || CandidateKeys(d).Count > 0;

    /// <summary>取一台设备今天（含昨天用于对比）的云端用电。返回 null 表示云端没有它的统计表。</summary>
    public static async Task<DeviceEnergy?> FetchTodayAsync(MijiaClient client, MijiaDevice d)
    {
        if (string.IsNullOrEmpty(d.TargetDid)) return null;
        var model = d.Model ?? "";
        var key = CachedKey(d);
        var lastCode = 0;
        var lastMessage = "";

        StatSeries? day = null;

        if (key != null)
        {
            var cached = await ReadAsync(client, d, key, DayType);
            if (cached.Ok) day = cached;
            else
            {
                lastCode = cached.Code;
                lastMessage = cached.Message;
            }
        }

        if (day is null)
        {
            foreach (var candidate in CandidateKeys(d))
            {
                if (candidate == key) continue;
                await Gate.WaitAsync();
                try
                {
                    var probe = await ReadAsync(client, d, candidate, DayType);
                    lastCode = probe.Code;
                    lastMessage = probe.Message;
                    if (!probe.Ok) continue;

                    if (model.Length > 0 && KeyByModel.TryGetValue(model, out var old) && old != candidate)
                        Log($"改用电量统计 key：{model} {old} → {candidate}");
                    Remember(model, candidate);
                    key = candidate;
                    day = probe;
                    break;
                }
                finally
                {
                    Gate.Release();
                }
            }
        }

        if (day is null || key is null)
        {
            if (LoggedFail.Add(model))
                Log($"无云统计：{d.Name}（{model}）试过 {string.Join("/", CandidateKeys(d))} code={lastCode} {lastMessage}");
            return null;
        }

        var energy = new DeviceEnergy { Did = d.TargetDid };
        var today = DateTime.Now.Date;

        // 日统计：今天的值就是今天用了多少度；昨天那一笔留着对比；其余日子进历史
        var seenDays = new HashSet<DateTime>();
        foreach (var (time, value) in day.Items)
        {
            var kwh = NormalizeKwh(value);
            var date = time.Date;
            if (date == today)
            {
                energy.TodayKwh = kwh;
                energy.HasData = true;
            }
            else if (date == today.AddDays(-1))
            {
                energy.YesterdayKwh = kwh;
            }
            if (date <= today && seenDays.Add(date)) energy.Days.Add((date, kwh));
        }
        energy.Days.Sort((a, b) => a.Date.CompareTo(b.Date));

        // 小时统计：不是每个型号都申请了这一维度，失败就用本地采样铺柱子
        var hourly = await ReadAsync(client, d, key, HourType);
        if (hourly.Ok)
            foreach (var (time, value) in hourly.Items)
                if (time.Date == today)
                    energy.HourlyKwh[time.Hour] = NormalizeKwh(value);

        // 只有小时统计、没有日统计时，把小时值加起来当今天的总量
        if (!energy.HasData && energy.HasHourly)
        {
            energy.TodayKwh = energy.HourlyKwh.Where(v => !double.IsNaN(v)).Sum();
            energy.HasData = true;
        }

        if (energy.HasData && Logged.Add(model + "|" + key))
        {
            var sample = string.Join("/", energy.HourlyKwh.Where(v => !double.IsNaN(v)).Take(6).Select(v => $"{v:0.##}"));
            var daysText = string.Join("/", energy.Days.TakeLast(8).Select(x => $"{x.Date:M-dd}={x.Kwh:0.##}"));
            Log($"统计可用：{d.Name} 型号={model} key={key} 今日={energy.TodayKwh:0.##} 昨日={energy.YesterdayKwh:0.##} 天数={day.Items.Count} 日档={daysText} 小时样本={sample}");
        }

        return energy;
    }

    private static Task<StatSeries> ReadAsync(MijiaClient client, MijiaDevice d, string key, string dataType)
    {
        var now = DateTime.Now;
        var from = dataType == DayType ? now.Date.AddDays(-59) : now.Date;
        return client.GetStatisticsAsync(d.TargetDid, key, dataType, from, now);
    }

    /// <summary>统计接口的原始值可能是 0.01 度定点数；按量级判断，超过 1 万度/天说明是定点编码，按百分之一缩回。</summary>
    public static double NormalizeKwh(double value) => value > 10_000 ? value / 100 : value;

    private static void Remember(string model, string key)
    {
        if (model.Length == 0) return;
        KeyByModel[model] = key;
        try
        {
            File.WriteAllText(KeyFile, JsonSerializer.Serialize(KeyByModel));
        }
        catch
        {
            // 写不进去就只在本次运行内生效
        }
    }

    private static Dictionary<string, string> LoadKeys()
    {
        try
        {
            if (!File.Exists(KeyFile)) return new Dictionary<string, string>(StringComparer.Ordinal);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KeyFile));
            return raw ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>探测结果写进日志文件，方便核对哪些型号拿不到统计表、值域是否合理。</summary>
    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.Dir, "energy-stats.log"),
                $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响取数
        }
    }
}
