// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiHome.Services;

/// <summary>
/// 设备组（did 形如 group.xxx）成员信息探针：把设备列表的原始响应原样落盘，
/// 看云端到底有没有给出组成员（App 能数出一组里几只灯，说明数据在某处）。
///
/// 用法：MiHome.exe --group-probe
/// 输出：%APPDATA%\Feiyu\MiHome\group-probe\home-device-list.json + summary.txt
/// </summary>
internal static class GroupProbe
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly StringBuilder Sb = new();

    private static void W(string s)
    {
        Console.WriteLine(s);
        Sb.AppendLine(s);
    }

    public static async Task RunAsync()
    {
        var dir = Path.Combine(AppPaths.Dir, "group-probe");
        Directory.CreateDirectory(dir);
        Sb.Clear();

        W($"MiHome 设备组探针  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { W("!! 没有登录态"); Finish(dir); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync())
        { W($"!! 续期失败：{auth.LastRefreshError ?? "未知"}"); Finish(dir); return; }

        var client = new MijiaClient(auth);
        var homes = await client.GetHomesAsync(forceRefresh: true);
        var home = homes.FirstOrDefault();
        if (home == null) { W("!! 没有家庭"); Finish(dir); return; }
        W($"家庭：{home.Name} id={home.Id}");

        var raw = await client.GetRawHomeDeviceListAsync(home);
        File.WriteAllText(Path.Combine(dir, "home-device-list.json"),
            JsonSerializer.Serialize(raw, Pretty));

        if (raw.TryGetProperty("device_info", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var groups = 0;
            foreach (var d in arr.EnumerateArray())
            {
                var did = d.TryGetProperty("did", out var didEl) ? didEl.ToString() : "";
                if (!did.StartsWith("group.", StringComparison.OrdinalIgnoreCase)) continue;
                groups++;
                W($"组设备：{did} name={(d.TryGetProperty("name", out var n) ? n.ToString() : "")}");
                // 打印这条记录的全部字段名，成员信息藏在哪个键上一眼就能看见
                var props = d.EnumerateObject().Select(p => p.Name).ToList();
                W($"  字段：{string.Join(", ", props)}");
            }
            W($"组设备共 {groups} 个");
        }
        else W("!! 响应里没有 device_info");

        Finish(dir);
    }

    private static void Finish(string dir)
        => File.WriteAllText(Path.Combine(dir, "summary.txt"), Sb.ToString());
}
