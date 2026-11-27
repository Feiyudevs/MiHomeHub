// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace MiHomeHub.Services;

/// <summary>
/// 热水器「使用记录」接口探针（只读诊断，不写任何云端数据）。
/// 米家 App 里燃气热水器的使用记录到底来自哪个接口、能不能用本地登录态调到，
/// 把候选接口逐个打一遍，原始响应原样落盘。
///
/// 用法：MiHomeHub.exe --usage-probe
/// 输出：%APPDATA%\Feiyu\MiHomeHub\usage-probe\
///   summary.txt        每个接口的 code / message / 条数
///   01-xxx.json ...    各接口完整原始响应
///
/// 跑完确认结构后，决定业务层怎么取记录；探针本身可保留作诊断口。
/// </summary>
internal static class UsageProbe
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly StringBuilder Log = new();
    private static string _dir = "";
    private static int _idx;

    public static async Task RunAsync()
    {
        _dir = Path.Combine(AppPaths.Dir, "usage-probe");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir))
        {
            try { File.Delete(f); } catch { }
        }

        W($"MiHomeHub 使用记录接口探针  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"输出目录：{_dir}");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken))
        {
            W("!! 没有登录态：请先在主程序里扫码登录后重跑");
            Finish();
            return;
        }

        if (!auth.HasValidAuth())
        {
            W(".. serviceToken 已过期，尝试用 passToken 续期");
            var ok = await auth.RefreshServiceTokenAsync();
            W($".. 续期{(ok ? "成功" : "失败：" + (auth.LastRefreshError ?? "未知"))}");
            if (!ok)
            {
                Finish();
                return;
            }
        }

        var client = new MijiaClient(auth);

        // 目标设备：devices.json 里的燃气热水器（没有就退到第一个 water-heater 规格设备）
        var target = FindTarget();
        if (target is null)
        {
            W("!! devices.json 里没找到燃气热水器，退出");
            Finish();
            return;
        }

        var did = target["did"]?.GetValue<string>() ?? "";
        var uid = target["uid"]?.ToString() ?? "";
        var model = target["model"]?.GetValue<string>() ?? "";
        W($"目标设备：{target["name"]}  model={model}  did={did}  uid={uid}");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var start = now - 90L * 86400;   // 回看 90 天

        var cases = new List<(string Name, string Uri, object Body)>
        {
            // 米家插件「使用记录」页的真实调用：event + key=7.1（usage-report 的 set-usage-record 事件）
            ("event-7-1-90d", "/user/get_user_device_data",
                new { did, uid, type = "event", key = "7.1", time_start = start, time_end = now, limit = 300 }),
            // 插件里 key 是数组，试数组形态
            ("event-7-1-array", "/user/get_user_device_data",
                new { did, uid, type = "event", key = new[] { "7.1" }, time_start = start, time_end = now, limit = 300 }),
            // 近 30 天，排除时间窗过宽被截断的影响
            ("event-7-1-30d", "/user/get_user_device_data",
                new { did, uid, type = "event", key = "7.1", time_start = now - 30L * 86400, time_end = now, limit = 300 }),
            // 属性侧对照：single-water（多半为空，确认事件才是记录来源）
            ("prop-single-water", "/user/get_user_device_data",
                new { did, uid, type = "prop", key = "7.1", time_start = start, time_end = now, limit = 300 }),
            // 其它事件（正在用水/停止用水），确认同一接口能拿到别的记录
            ("event-3-2", "/user/get_user_device_data",
                new { did, uid, type = "event", key = "3.2", time_start = now - 30L * 86400, time_end = now, limit = 300 }),
            // App 侧「设备上报操作记录」
            ("raw-event-7-1", "/v2/user/get_device_data_raw",
                new { did, uid, type = "event", key = "7.1", time_start = start, time_end = now, limit = 300 }),
            // 探底：一年窗口 + 最大 limit(1000)，看云端到底保留多久、单页上限多少
            ("event-7-1-1y-1000", "/user/get_user_device_data",
                new { did, uid, type = "event", key = "7.1", time_start = now - 365L * 86400, time_end = now, limit = 1000 }),
            // 米家插件「使用记录」真正的统计调用：prop.7.single_water / single_gas，月度最多 6 条
            ("stat-month-water", "/v2/user/statistics",
                new { did, key = "prop.7.1", data_type = "stat_month_v3", time_start = now - 186L * 86400, time_end = now, limit = 6 }),
            ("stat-month-gas", "/v2/user/statistics",
                new { did, key = "prop.7.2", data_type = "stat_month_v3", time_start = now - 186L * 86400, time_end = now, limit = 6 }),
            ("stat-day-water", "/v2/user/statistics",
                new { did, key = "prop.7.1", data_type = "stat_day_v3", time_start = now - 31L * 86400, time_end = now, limit = 31 }),
            ("stat-hour-water", "/v2/user/statistics",
                new { did, key = "prop.7.1", data_type = "stat_hour_v3", time_start = now - now % 86400, time_end = now, limit = 24 }),
            ("stat-day-water-180", "/v2/user/statistics",
                new { did, key = "prop.7.1", data_type = "stat_day_v3", time_start = now - 186L * 86400, time_end = now, limit = 186 }),
            ("stat-day-gas-180", "/v2/user/statistics",
                new { did, key = "prop.7.2", data_type = "stat_day_v3", time_start = now - 186L * 86400, time_end = now, limit = 186 })
        };

        foreach (var (name, uri, body) in cases)
        {
            await ProbeAsync(client, name, uri, body);
        }

        Finish();
    }

    private static async Task ProbeAsync(MijiaClient client, string name, string uri, object body)
    {
        W("");
        W($"== [{_idx + 1:00}] {name}");
        W($"   POST {uri}");
        W($"   body {JsonSerializer.Serialize(body, Pretty).Replace(Environment.NewLine, " ")}");

        try
        {
            var ret = await client.RequestRawAsync(uri, body);
            var file = Path.Combine(_dir, $"{++_idx:00}-{name}.json");
            await File.WriteAllTextAsync(file, ret.GetRawText(), new UTF8Encoding(false));

            var code = ret.TryGetProperty("code", out var c) ? c.ToString() : "?";
            var message = ret.TryGetProperty("message", out var m) ? (m.ValueKind == JsonValueKind.String ? m.GetString() : m.ToString()) : "";
            var count = CountResult(ret);
            W($"   -> code={code} message={message} result条数={count}");
            W($"   -> 落盘 {Path.GetFileName(file)}");

            if (count > 0)
            {
                var first = FirstResult(ret);
                if (first.HasValue)
                {
                    var fe = first.Value;
                    var keys = string.Join(",", fe.ValueKind == JsonValueKind.Object
                        ? fe.EnumerateObject().Select(p => p.Name)
                        : new[] { fe.ValueKind.ToString() });
                    W($"   -> 首条字段：{keys}");
                    W($"   -> 首条内容：{Trim(fe.GetRawText(), 300)}");
                }
            }
        }
        catch (Exception ex)
        {
            W($"   !! 调用失败：{ex.Message}");
            _idx++;
        }
    }

    private static int CountResult(JsonElement ret)
    {
        foreach (var p in new[] { "result", "data", "list" })
        {
            if (ret.TryGetProperty(p, out var e))
            {
                if (e.ValueKind == JsonValueKind.Array) return e.GetArrayLength();
                if (e.ValueKind == JsonValueKind.Object) return 1;
            }
        }
        return 0;
    }

    private static JsonElement? FirstResult(JsonElement ret)
    {
        foreach (var p in new[] { "result", "data", "list" })
        {
            if (!ret.TryGetProperty(p, out var e)) continue;
            if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0) return e[0];
            if (e.ValueKind == JsonValueKind.Object) return e;
        }
        return null;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static JsonObject? FindTarget()
    {
        var path = Path.Combine(AppPaths.Dir, "devices.json");
        if (!File.Exists(path)) return null;
        try
        {
            var arr = JsonNode.Parse(File.ReadAllText(path)) as JsonArray;
            if (arr is null) return null;
            var heater = arr.OfType<JsonObject>()
                .FirstOrDefault(o => string.Equals(o["model"]?.GetValue<string>(), "zimi.waterheater.h01", StringComparison.OrdinalIgnoreCase));
            heater ??= arr.OfType<JsonObject>()
                .FirstOrDefault(o => (o["spec_type"]?.GetValue<string>() ?? "").Contains("water-heater", StringComparison.OrdinalIgnoreCase));
            return heater;
        }
        catch
        {
            return null;
        }
    }

    private static void W(string s) => Log.AppendLine(s);

    private static void Finish()
    {
        try
        {
            File.WriteAllText(Path.Combine(_dir, "summary.txt"), Log.ToString(), new UTF8Encoding(false));
        }
        catch { }
        Console.WriteLine(Log.ToString());
        Console.WriteLine($"== 结果目录：{_dir}");
    }
}
