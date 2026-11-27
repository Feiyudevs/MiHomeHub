// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiHomeHub.Services;

/// <summary>体脂秤上的一个成员（米家「成员管理」里的那几位）。</summary>
public sealed class ScaleMember
{
    public string AccountId { get; init; } = "";
    public string Name { get; init; } = "";
    public int Sex { get; init; }
    public int Height { get; init; }
    public int AccountCode { get; init; }

    /// <summary>主账号：默认展示他的记录。</summary>
    public bool IsPrimary => AccountCode == 1;
}

/// <summary>一次称重记录。重量必有；体脂/阻抗只有光脚测量的那几次才算得出来。</summary>
public sealed class ScaleRecord
{
    public string AccountId { get; init; } = "";
    public DateTime Time { get; init; }
    public double WeightKg { get; init; }
    public double Bmi { get; init; }
    public double BodyFatPercent { get; init; }
    public double Impedance { get; init; }

    public bool HasComposition => BodyFatPercent > 0;
}

public sealed class ScaleHistory
{
    public static readonly ScaleHistory Empty = new();

    public IReadOnlyList<ScaleMember> Members { get; init; } = Array.Empty<ScaleMember>();
    public IReadOnlyList<ScaleRecord> Records { get; init; } = Array.Empty<ScaleRecord>();

    public bool HasAny => Records.Count > 0;
}

/// <summary>
/// 体脂秤历史：读米家 eco 接口（和米家 App「体脂秤 → 历史记录」同一份数据）。
///
/// - 成员：<c>/eco/scale/account/list</c>
/// - 记录：<c>/eco/scale/getData</c>，按成员一页 20 条、由新到旧翻
///   （接口要求 <c>beginTime</c> 是较新的一端、<c>endTime</c> 是较旧的一端，写反会 40000）
///
/// 只读：不调用 add / batchAdd / delete，避免往用户云端塞重复记录。
/// </summary>
public static class ScaleHistoryService
{
    private const int PageSize = 20;
    private const int MaxPages = 20;

    public static async Task<ScaleHistory> LoadAsync(MijiaClient client, string did, string model,
        int days = 365, int maxMembers = 4)
    {
        var uid = client.UserId;
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(did)) return ScaleHistory.Empty;

        var members = await FetchMembersAsync(client, did, uid).ConfigureAwait(false);
        if (members.Count == 0) return ScaleHistory.Empty;

        var records = new List<ScaleRecord>();
        foreach (var member in members.Take(maxMembers))
            records.AddRange(await FetchRecordsAsync(client, did, model, uid, member.AccountId, days).ConfigureAwait(false));

        records.Sort((a, b) => a.Time.CompareTo(b.Time));
        return new ScaleHistory { Members = members, Records = records };
    }

    /// <summary>成员列表：名字、性别、身高、accountCode（1 = 主账号）。</summary>
    public static async Task<List<ScaleMember>> FetchMembersAsync(MijiaClient client, string did, string uid)
    {
        var list = new List<ScaleMember>();
        JsonElement raw;
        try
        {
            raw = await client.EcoRequestAsync("/eco/scale/account/list",
                new { uid, deviceId = did, source = 2 }).ConfigureAwait(false);
        }
        catch
        {
            return list;
        }

        if (!TryResultArray(raw, out var arr)) return list;
        foreach (var item in arr.EnumerateArray())
        {
            var accountId = Text(item, "accountId");
            if (string.IsNullOrEmpty(accountId)) continue;

            list.Add(new ScaleMember
            {
                AccountId = accountId,
                Name = Text(item, "name"),
                Sex = Int(item, "sex"),
                Height = Int(item, "height"),
                AccountCode = Int(item, "accountCode")
            });
        }

        return list;
    }

    /// <summary>某个成员最近若干天的称重记录（最多 20 条一页，自动往前翻）。</summary>
    public static async Task<List<ScaleRecord>> FetchRecordsAsync(MijiaClient client, string did, string model,
        string uid, string accountId, int days = 365)
    {
        var list = new List<ScaleRecord>();
        if (string.IsNullOrEmpty(accountId)) return list;

        var now = DateTimeOffset.Now;
        var begin = now;
        var end = now.AddDays(-Math.Clamp(days, 1, 365));

        for (var page = 0; page < MaxPages; page++)
        {
            JsonElement raw;
            try
            {
                raw = await client.EcoRequestAsync("/eco/scale/getData", new
                {
                    uid,
                    accountId,
                    did,
                    model,
                    param = new
                    {
                        filter = 1,
                        filterTime = 0,
                        filterMode = 1,
                        pageSize = PageSize,
                        beginTime = begin.ToUnixTimeMilliseconds(),
                        endTime = end.ToUnixTimeMilliseconds()
                    }
                }).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            if (!TryResultArray(raw, out var arr)) break;

            var count = 0;
            DateTime oldest = default;
            foreach (var item in arr.EnumerateArray())
            {
                count++;
                var record = ParseRecord(item);
                if (record is null) continue;
                list.Add(record);
                if (oldest == default || record.Time < oldest) oldest = record.Time;
            }

            if (count < PageSize || oldest == default) break;
            begin = oldest.AddSeconds(-1);
            if (begin < end) break;
        }

        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    private static ScaleRecord? ParseRecord(JsonElement item)
    {
        var accountId = Text(item, "accountId");

        var time = Time(item);
        if (time == default) return null;

        if (!item.TryGetProperty("data", out var dataEl)) return null;
        var dataJson = dataEl.ValueKind == JsonValueKind.String ? dataEl.GetString() : dataEl.GetRawText();
        if (string.IsNullOrWhiteSpace(dataJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var data = doc.RootElement;

            var weight = Num(data, "weight");
            var bmi = Num(data, "bmi");
            var bfp = Num(data, "bfp");
            var impedance = Num(data, "bodyRes");

            // 老记录把成分塞在 bodyResData 的字符串里
            if (data.TryGetProperty("bodyResData", out var nested) && nested.ValueKind == JsonValueKind.String)
            {
                var text = nested.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        using var inner = JsonDocument.Parse(text);
                        if (bfp <= 0) bfp = Num(inner.RootElement, "bfp");
                    }
                    catch { }
                }
            }

            if (weight <= 0) return null;
            return new ScaleRecord
            {
                AccountId = accountId,
                Time = time,
                WeightKg = weight,
                Bmi = bmi,
                BodyFatPercent = bfp,
                Impedance = impedance
            };
        }
        catch
        {
            return null;
        }
    }

    private static DateTime Time(JsonElement item)
    {
        if (item.TryGetProperty("createTime", out var c) && c.ValueKind == JsonValueKind.Number &&
            c.TryGetInt64(out var ms) && ms > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

        return default;
    }

    private static bool TryResultArray(JsonElement raw, out JsonElement result)
    {
        result = default;
        if (raw.ValueKind != JsonValueKind.Object) return false;
        if (raw.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
            return false;
        return raw.TryGetProperty("result", out result) && result.ValueKind == JsonValueKind.Array;
    }

    private static string Text(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => ""
        };
    }

    private static int Int(JsonElement obj, string name)
    {
        var text = Text(obj, name);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
    }

    private static double Num(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return 0;
    }
}
