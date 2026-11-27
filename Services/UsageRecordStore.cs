// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MiHomeHub.Services;

/// <summary>一笔用水记录：米家云端 usage-report 事件上报的四个值。</summary>
public sealed class UsageRecord
{
    /// <summary>这单结束的时间。</summary>
    public DateTime Time { get; init; }

    /// <summary>这单用水（升）。</summary>
    public int WaterL { get; init; }

    /// <summary>这单用气（升，1 m³ = 1000 L）。</summary>
    public int GasL { get; init; }

    /// <summary>这单用时（秒）。</summary>
    public int Seconds { get; init; }

    /// <summary>这单的目标水温（℃）。</summary>
    public int TargetTemp { get; init; }

    public string Signature => $"{Time.Ticks}|{WaterL}|{GasL}|{Seconds}|{TargetTemp}";
}

/// <summary>
/// 热水器的「使用记录」：读米家云端保存的设备事件历史。
///
/// 米家 App 里那页「使用记录」就是这条链路——插件对 usage-report 服务的
/// set-usage-record 事件（siid.eiid = 7.1）调 getDeviceData，每次用水结束上报一条
/// {用水 L, 用气 L, 用时 s, 目标温度}。这里用同一套登录态直接取，按时间倒序分页，
/// 在本地留一份缓存（最多 12000 条，够画近半年），断网/刚启动时先用缓存。
/// </summary>
public static class UsageRecordStore
{
    private const string Uri = "/user/get_user_device_data";
    private const string EventKey = "7.1";
    private const int PageLimit = 1000;

    /// <summary>一次补齐最多翻几页：近半年大约九千多条，十二页留足余量。</summary>
    private const int MaxBackfillPages = 12;

    /// <summary>缓存留最近这么多条（近半年逐单记录用）。</summary>
    private const int CacheKeep = 12000;

    private static readonly string CachePath = Path.Combine(AppPaths.Dir, "usage-records.json");
    private static readonly object Gate = new();

    private static Dictionary<string, CacheEntry>? _cache;

    private sealed class CacheEntry
    {
        [JsonPropertyName("fetchedAt")] public long FetchedAt { get; set; }
        /// <summary>已经确认拉到过的最早时间（0 = 还不知道）。用来判断要不要往前翻页补齐。</summary>
        [JsonPropertyName("coveredFrom")] public long CoveredFrom { get; set; }
        [JsonPropertyName("items")] public List<CacheItem> Items { get; set; } = new();
    }

    private sealed class CacheItem
    {
        [JsonPropertyName("time")] public long Time { get; set; }
        [JsonPropertyName("water")] public int WaterL { get; set; }
        [JsonPropertyName("gas")] public int GasL { get; set; }
        [JsonPropertyName("seconds")] public int Seconds { get; set; }
        [JsonPropertyName("temp")] public int TargetTemp { get; set; }
    }

    /// <summary>本地缓存的记录（按时间倒序）。</summary>
    public static IReadOnlyList<UsageRecord> Cached(string did)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var entry = _cache!.TryGetValue(did, out var e) ? e : null;
            return entry is null ? Array.Empty<UsageRecord>() : entry.Items
                .Select(i => new UsageRecord
                {
                    Time = DateTimeOffset.FromUnixTimeSeconds(i.Time).LocalDateTime,
                    WaterL = i.WaterL,
                    GasL = i.GasL,
                    Seconds = i.Seconds,
                    TargetTemp = i.TargetTemp
                })
                .ToList();
        }
    }

    /// <summary>缓存是不是还新：没拉过或超过给定时长就该重新拉。</summary>
    public static bool IsFresh(string did, TimeSpan maxAge)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (_cache!.TryGetValue(did, out var e) is false) return false;
            var at = DateTimeOffset.FromUnixTimeSeconds(e.FetchedAt).LocalDateTime;
            return DateTime.Now - at < maxAge;
        }
    }

    /// <summary>已经确认拉到过 <paramref name="from"/> 之前的数据（不会因为那几天没用水就误判成没覆盖）。</summary>
    public static bool Covers(string did, DateTime from)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (_cache!.TryGetValue(did, out var e) is false || e.CoveredFrom == 0) return false;
            var covered = DateTimeOffset.FromUnixTimeSeconds(e.CoveredFrom).LocalDateTime;
            return covered <= from.AddHours(20);
        }
    }

    /// <summary>
    /// 保证本地记录覆盖到 <paramref name="from"/>：覆盖够就只拉最新一页，
    /// 不够就往前翻页补齐；合并后写回缓存。
    /// </summary>
    public static async Task<IReadOnlyList<UsageRecord>> EnsureAsync(
        MijiaClient client, string did, long uid, DateTime from, DateTime to)
    {
        var cached = Cached(did).ToList();
        var needBackfill = !Covers(did, from);

        var start = needBackfill ? from : cached.Count > 0 ? cached[0].Time.AddSeconds(-1) : from;
        var end = to;
        var fetched = new List<UsageRecord>();
        var pages = needBackfill ? MaxBackfillPages : 1;
        DateTime? covered = needBackfill ? null : CoveredFrom(did);

        for (var i = 0; i < pages; i++)
        {
            var page = await FetchPageAsync(client, did, uid, start, end);
            if (page.Count == 0)
            {
                // 这一段确实没有更早的记录了：覆盖范围算到查询起点
                covered = Earlier(covered, start);
                break;
            }

            fetched.AddRange(page);
            var oldest = page[^1].Time;
            covered = Earlier(covered, oldest);

            if (oldest <= start.AddMinutes(1))
            {
                covered = Earlier(covered, start);
                break;
            }

            // 这一页没拉满，说明查询区间里就这些了
            if (page.Count < PageLimit)
            {
                covered = Earlier(covered, start);
                break;
            }

            end = oldest.AddSeconds(-1);
            if (!needBackfill) break;
        }

        var merged = Merge(fetched, cached);
        Put(did, merged, covered);
        return merged;
    }

    /// <summary>已经确认覆盖到的最早时间（null = 还没拉过）。</summary>
    private static DateTime? CoveredFrom(string did)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (_cache!.TryGetValue(did, out var e) is false || e.CoveredFrom == 0) return null;
            return DateTimeOffset.FromUnixTimeSeconds(e.CoveredFrom).LocalDateTime;
        }
    }

    private static DateTime? Earlier(DateTime? a, DateTime b) =>
        a is null || b < a.Value ? b : a;

    private static async Task<List<UsageRecord>> FetchPageAsync(
        MijiaClient client, string did, long uid, DateTime from, DateTime to)
    {
        if (to <= from) return new List<UsageRecord>();

        var ret = await client.RequestRawAsync(Uri, new
        {
            did,
            uid = uid.ToString(CultureInfo.InvariantCulture),
            type = "event",
            key = EventKey,
            time_start = new DateTimeOffset(from).ToUnixTimeSeconds(),
            time_end = new DateTimeOffset(to).ToUnixTimeSeconds(),
            limit = PageLimit
        });

        if (!IsOk(ret)) return new List<UsageRecord>();
        return Parse(ret);
    }

    private static bool IsOk(JsonElement ret) =>
        ret.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() == 0;

    /// <summary>把事件数组拆成一条条记录；同一时间同一组数值只留一条。</summary>
    private static List<UsageRecord> Parse(JsonElement ret)
    {
        var list = new List<UsageRecord>();
        if (!ret.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in arr.EnumerateArray())
        {
            if (!item.TryGetProperty("time", out var t) || t.ValueKind != JsonValueKind.Number) continue;
            if (!item.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String) continue;

            var fields = ParseFields(v.GetString() ?? "");
            if (fields is null) continue;

            var unix = t.GetInt64();
            var sig = $"{unix}|{fields[0]}|{fields[1]}|{fields[2]}|{fields[3]}";
            if (!seen.Add(sig)) continue;

            list.Add(new UsageRecord
            {
                Time = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime,
                WaterL = fields[0],
                GasL = fields[1],
                Seconds = fields[2],
                TargetTemp = fields[3]
            });
        }

        list.Sort((a, b) => b.Time.CompareTo(a.Time));
        return list;
    }

    /// <summary>value 是 [{piid:1,value:22},{piid:2,value:46},{piid:3,value:427},{piid:4,value:48}] 这种数组。</summary>
    private static int[]? ParseFields(string raw)
    {
        if (raw.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var vals = new int[4];
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                if (!p.TryGetProperty("piid", out var pi) || pi.ValueKind != JsonValueKind.Number) continue;
                if (!p.TryGetProperty("value", out var va) || va.ValueKind != JsonValueKind.Number) continue;
                var idx = pi.GetInt32() - 1;
                if (idx is >= 0 and < 4) vals[idx] = va.GetInt32();
            }
            return vals;
        }
        catch
        {
            return null;
        }
    }

    private static List<UsageRecord> Merge(List<UsageRecord> fetched, List<UsageRecord> cached)
    {
        var map = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        foreach (var r in fetched) map[r.Signature] = r;
        foreach (var r in cached)
            if (!map.ContainsKey(r.Signature)) map[r.Signature] = r;

        var merged = map.Values.OrderByDescending(r => r.Time).Take(CacheKeep).ToList();
        return merged;
    }

    private static void Put(string did, IReadOnlyList<UsageRecord> records, DateTime? covered)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var entry = _cache!.TryGetValue(did, out var e) ? e : new CacheEntry();
            entry.Items = records.Take(CacheKeep).Select(r => new CacheItem
            {
                Time = new DateTimeOffset(r.Time).ToUnixTimeSeconds(),
                WaterL = r.WaterL,
                GasL = r.GasL,
                Seconds = r.Seconds,
                TargetTemp = r.TargetTemp
            }).ToList();
            entry.FetchedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
            if (covered is { } c) entry.CoveredFrom = new DateTimeOffset(c).ToUnixTimeSeconds();
            _cache[did] = entry;
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_cache is not null) return;
        _cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(CachePath)) return;
            var doc = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(
                File.ReadAllText(CachePath, Encoding.UTF8));
            if (doc is not null) _cache = new Dictionary<string, CacheEntry>(doc, StringComparer.Ordinal);
        }
        catch
        {
            // 缓存坏了就当没有，下一次拉取会重写
        }
    }

    private static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(CachePath, json, new UTF8Encoding(false));
        }
        catch
        {
            // 写缓存失败不影响展示
        }
    }
}
