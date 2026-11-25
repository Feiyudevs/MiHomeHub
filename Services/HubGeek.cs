// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MiHomeHub.Services;

/// <summary>
/// 中枢网关「米家自动化极客版」入口。
///
/// 极客版是**网关自己托管**的网页，不是云服务：网页固定走 80 端口，登录页只填 6 位一次性登录码；
/// 网页里的实时通道走 8082（只有做外网端口映射时才需要关心它）。
/// 所以端口不需要「生成」，会变的只有网关 IP：
///   1. 云端设备列表带 localip → 直接用；
///   2. 没带 / 不在同一网段 → 在当前网段的 /24 里扫一遍 80 端口，认页面内容兜底；
///   3. 还找不到 → 用户手填一次，记在 app.json 里。
/// 登录码由网关下发、米家 App 显示、几分钟就失效，没有对外接口，程序取不到也替不了。
/// </summary>
public static class HubGeek
{
    /// <summary>极客版网页端口：固定 80，浏览器直接 http://IP 即可。</summary>
    public const int WebPort = 80;

    /// <summary>极客版网页 WebSocket 端口：固定 8082，局域网内直连不用管。</summary>
    public const int WebSocketPort = 8082;

    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    /// <summary>把用户填的地址收敛成裸 IP；认不出返回空串。</summary>
    public static string NormalizeIp(string? ip)
    {
        var raw = (ip ?? "").Trim();
        if (raw.Length == 0) return "";
        if (raw.Contains("://", StringComparison.Ordinal))
            raw = raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..];
        raw = raw.Split('/')[0].Split(':')[0].Trim();
        return IPAddress.TryParse(raw, out var addr) && addr.AddressFamily == AddressFamily.InterNetwork
            ? addr.ToString()
            : "";
    }

    /// <summary>拼极客版网页地址；IP 为空返回空串。</summary>
    public static string BuildUrl(string? ip)
    {
        var bare = NormalizeIp(ip);
        return bare.Length == 0 ? "" : $"http://{bare}";
    }

    /// <summary>用默认浏览器打开（极客版是网页编辑器，必须浏览器环境）。</summary>
    public static bool Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在当前电脑所在网段的 /24 里找极客版网页。
    /// 先并发探 80 端口（快），对开着的再取首页确认是不是极客版，避免把路由器管理页认成网关。
    /// </summary>
    public static async Task<string?> FindAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var prefixes = LocalSubnets();
        if (prefixes.Count == 0) return null;

        var gates = new SemaphoreSlim(48);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hits = new List<string>();
        var tasks = new List<Task>();

        foreach (var prefix in prefixes)
        {
            for (var host = 1; host <= 254 && !cts.IsCancellationRequested; host++)
            {
                var ip = $"{prefix}.{host}";
                tasks.Add(Task.Run(async () =>
                {
                    await gates.WaitAsync(cts.Token).ConfigureAwait(false);
                    try
                    {
                        if (!await IsWebPortOpenAsync(ip, 350, cts.Token).ConfigureAwait(false))
                            return;
                        if (await LooksLikeGeekAsync(ip, cts.Token).ConfigureAwait(false))
                        {
                            lock (hits) hits.Add(ip);
                        }
                    }
                    catch
                    {
                        // 单个地址失败不影响整轮扫描
                    }
                    finally
                    {
                        gates.Release();
                    }
                }, cts.Token));
            }
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消就把已有结果带回去
        }

        lock (hits)
        {
            return hits.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
        }
    }

    private static async Task<bool> IsWebPortOpenAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(IPAddress.Parse(ip), WebPort, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>首页里认「极客版」字样的几个可靠特征；路由器/其他网页不会同时命中。</summary>
    private static async Task<bool> LooksLikeGeekAsync(string ip, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync($"http://{ip}/", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length == 0 || body.Length > 1_000_000) return false;
            return body.Contains("极客", StringComparison.Ordinal)
                || body.Contains("登录码", StringComparison.Ordinal)
                || body.Contains("geek", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>本机的私有 IPv4 网段前缀（如 192.168.31），最多取 3 个，避免虚拟网卡太多扫不完。</summary>
    private static List<string> LocalSubnets()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (!IsPrivate(ip)) continue;
                    var prefix = ip[..ip.LastIndexOf('.')];
                    if (prefix == "127.0.0") continue;
                    if (!result.Contains(prefix)) result.Add(prefix);
                }
            }
        }
        catch
        {
            // 网卡信息读不到就当做没有候选
        }
        return result.Take(3).ToList();
    }

    private static bool IsPrivate(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4 || !byte.TryParse(parts[0], out var a) || !byte.TryParse(parts[1], out var b))
            return false;
        return a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168);
    }
}
