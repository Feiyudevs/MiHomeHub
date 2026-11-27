// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MiHomeHub.Models;

namespace MiHomeHub.Services;

/// <summary>
/// 设备拟物图来源探针（只读）：把每个家庭的设备原始字段（icon / subclass_id / devPicInfo）
/// 与 productconfig 接口返回的 icon_real 落盘，确认新型号（如 trytogo 灯）的图从哪来。
/// 用法：MiHomeHub.exe --icon-probe
/// 输出：%APPDATA%\Feiyu\MiHomeHub\icon-probe.txt
/// </summary>
internal static class IconProbe
{
    private static readonly StringBuilder Sb = new();

    private static void W(string s)
    {
        Console.WriteLine(s);
        Sb.AppendLine(s);
    }

    private static string Short(string? s, int max = 90) =>
        string.IsNullOrEmpty(s) ? "-" : s.Length <= max ? s : s[..max] + "…";

    public static async Task RunAsync(string[] extraModels)
    {
        Sb.Clear();
        W($"MiHomeHub 拟物图探针  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { W("!! 没有登录态"); Finish(); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync())
        {
            W($"!! 续期失败：{auth.LastRefreshError ?? "未知"}");
            Finish();
            return;
        }

        var client = new MijiaClient(auth);
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        List<MijiaHome> homes;
        try { homes = await client.GetHomesAsync(forceRefresh: true); }
        catch (Exception ex) { W($"!! 家庭列表失败：{ex.Message}"); Finish(); return; }
        W($"家庭数：{homes.Count}");

        foreach (var home in homes)
        {
            W("");
            W($"=== 家庭 {home.Name}  id={home.Id}  uid={home.Uid} ===");
            JsonElement raw;
            try { raw = await client.GetRawHomeDeviceListAsync(home); }
            catch (Exception ex) { W($"  设备列表失败：{ex.Message}"); continue; }
            if (!raw.TryGetProperty("device_info", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                W("  没有 device_info");
                continue;
            }

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var model = el.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                var icon = el.TryGetProperty("icon", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : "";
                var subclass = el.TryGetProperty("subclass_id", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() : "";
                var pic = el.TryGetProperty("devPicInfo", out var dp) ? dp.ToString() : "";
                W($"  {name} | {model} | icon={Short(icon)} | subclass={Short(subclass)} | pic={Short(pic)}");
                if (!string.IsNullOrEmpty(model) && !models.ContainsKey(model))
                    models[model] = name;
            }
        }

        // 共享设备也列一下，避免漏掉只有共享权限的型号
        try
        {
            foreach (var d in await client.GetSharedDevicesAsync())
            {
                if (string.IsNullOrEmpty(d.Model) || models.ContainsKey(d.Model)) continue;
                models[d.Model] = d.Name;
                W($"  [共享] {d.Name} | {d.Model}");
            }
        }
        catch (Exception ex) { W($"共享设备失败：{ex.Message}"); }

        // 新版设备列表接口：字段比旧接口全，设备图标的真实字段名以这里为准
        if (homes.Count > 0)
        {
            W("");
            W($"=== /v2/home/home_device_list（家庭 {homes[0].Name}）===");
            try
            {
                var v2 = await client.GetRawHomeDeviceListV2Async(homes[0]);
                if (v2.TryGetProperty("device_info", out var arr2) && arr2.ValueKind == JsonValueKind.Array)
                {
                    var shown = 0;
                    foreach (var el in arr2.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        if (shown++ == 0)
                            W("首条字段：" + string.Join(", ", el.EnumerateObject().Select(p => p.Name)));
                        var name = el.TryGetProperty("name", out var n2) ? n2.GetString() ?? "" : "";
                        var model = el.TryGetProperty("model", out var m2) ? m2.GetString() ?? "" : "";
                        var hits = el.EnumerateObject()
                            .Where(p => p.Value.ValueKind == JsonValueKind.String &&
                                        (p.Name.ToLowerInvariant().Contains("icon") || p.Name.ToLowerInvariant().Contains("pic")))
                            .Select(p => $"{p.Name}={Short(p.Value.GetString(), 120)}")
                            .ToList();
                        if (hits.Count > 0) W($"  {name} | {model} | {string.Join(" | ", hits)}");
                        if (el.TryGetProperty("extra", out var ex) && ex.ValueKind == JsonValueKind.Object)
                        {
                            var exHits = ex.EnumerateObject()
                                .Where(p => p.Value.ValueKind == JsonValueKind.String &&
                                            (p.Name.ToLowerInvariant().Contains("icon") || p.Name.ToLowerInvariant().Contains("pic")))
                                .Select(p => $"extra.{p.Name}={Short(p.Value.GetString(), 120)}")
                                .ToList();
                            if (exHits.Count > 0) W($"    {name} | {string.Join(" | ", exHits)}");
                        }
                    }
                    W($"设备数：{shown}");
                }
                else W("没有 device_info");
            }
            catch (Exception ex) { W($"v2 设备列表失败：{ex.Message}"); }
        }

        W("");
        W($"=== productconfig/single_pull（{models.Count} 个型号）===");
        foreach (var kv in models.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var url = await client.GetProductIconUrlAsync(kv.Key);
                W($"{kv.Key}  [{kv.Value}]  =>  {(string.IsNullOrEmpty(url) ? "无" : url)}");
            }
            catch (Exception ex)
            {
                W($"{kv.Key}  [{kv.Value}]  =>  错误：{ex.Message}");
            }
        }

        foreach (var model in extraModels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var url = await client.GetProductIconUrlAsync(model);
                W($"[指定] {model}  =>  {(string.IsNullOrEmpty(url) ? "无" : url)}");
            }
            catch (Exception ex)
            {
                W($"[指定] {model}  =>  错误：{ex.Message}");
            }
        }

        Finish();
    }

    /// <summary>
    /// 把指定型号设备的完整原始响应落盘（新旧两个设备列表接口都取），用于确认图片字段的真实名字和层级。
    /// 用法：MiHomeHub.exe --icon-probe-raw togo
    /// </summary>
    public static async Task RunRawAsync(string modelFilter)
    {
        Sb.Clear();
        W($"MiHomeHub 设备原始响应导出  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  filter={modelFilter}");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { W("!! 没有登录态"); Finish(); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync())
        {
            W($"!! 续期失败：{auth.LastRefreshError ?? "未知"}");
            Finish();
            return;
        }

        var client = new MijiaClient(auth);
        List<MijiaHome> homes;
        try { homes = await client.GetHomesAsync(forceRefresh: true); }
        catch (Exception ex) { W($"!! 家庭列表失败：{ex.Message}"); Finish(); return; }

        var found = 0;
        foreach (var home in homes)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                JsonElement raw;
                try { raw = pass == 0 ? await client.GetRawHomeDeviceListAsync(home) : await client.GetRawHomeDeviceListV2Async(home); }
                catch (Exception ex) { W($"[{(pass == 0 ? "v1" : "v2")}] {home.Name} 失败：{ex.Message}"); continue; }

                var root = raw;
                if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("result", out var r0)) root = r0;
                if (!root.TryGetProperty("device_info", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;

                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var model = el.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                    if (!model.Contains(modelFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    found++;
                    W("");
                    W($"=== [{(pass == 0 ? "v1 /home/home_device_list" : "v2 /v2/home/home_device_list")}] {home.Name} | {model} ===");
                    W(el.GetRawText());
                }
            }
        }

        W("");
        W($"匹配设备数：{found}");
        if (found == 0) W("!! 这个登录账号里没有该型号的设备，无法导出");

        Finish();
    }

    public static async Task RunProductRawAsync(string[] models)
    {
        Sb.Clear();
        W($"MiHomeHub 产品配置原始查询  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { W("!! 没有登录态"); Finish(); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync())
        {
            W($"!! 续期失败：{auth.LastRefreshError ?? "未知"}");
            Finish();
            return;
        }

        var client = new MijiaClient(auth);
        var keys = new[] { "model", "name", "icon_real", "icon", "subclass_id", "pdid" };
        foreach (var model in models.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            W("");
            W($"=== {model}  keys={string.Join(",", keys)} ===");
            try
            {
                var r = await client.QueryProductConfigAsync(new[] { model }, keys);
                W(r.GetRawText());
            }
            catch (Exception ex)
            {
                W($"错误：{ex.Message}");
            }
        }

        Finish();
    }

    public static async Task RunPluginRawAsync(string[] models)
    {
        Sb.Clear();
        W($"MiHomeHub 插件元数据查询  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { W("!! 没有登录态"); Finish(); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync())
        {
            W($"!! 续期失败：{auth.LastRefreshError ?? "未知"}");
            Finish();
            return;
        }

        var client = new MijiaClient(auth);
        foreach (var model in models.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            W("");
            W($"=== {model} ===");
            try
            {
                var r = await client.FetchPluginAsync(model);
                W(r.GetRawText());
            }
            catch (Exception ex)
            {
                W($"错误：{ex.Message}");
            }
        }

        Finish();
    }

    private static void Finish()
    {
        try
        {
            var path = Path.Combine(AppPaths.Dir, "icon-probe.txt");
            File.WriteAllText(path, Sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"已写入 {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"写文件失败：{ex.Message}");
        }
    }
}
