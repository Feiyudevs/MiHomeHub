// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiHomeHub.Models;

namespace MiHomeHub.Services;

/// <summary>画面源自检停在哪个阶段：配置 → 网关 → 流 → 可用。</summary>
public enum CameraProbeStage
{
    Config,
    Gateway,
    Stream,
    Ready
}

/// <summary>画面源自检的错误类别；界面按它给下一步动作，日志按它排查。</summary>
public enum CameraProbeCode
{
    None,
    BadAddress,
    Timeout,
    Unreachable,
    HttpError,
    BadResponse,
    StreamMissing
}

/// <summary>
/// 一次自检的完整结论。只含阶段、错误码和流名，不含账号、令牌和视频地址，
/// 所以可以直接复制给用户排查。
/// </summary>
public sealed record CameraProbeResult(
    CameraProbeStage Stage,
    CameraProbeCode Code,
    bool Ready,
    string Hint,
    string BaseUrl,
    string? StreamName,
    IReadOnlyList<string> Streams,
    bool LoopbackHost,
    string SecurityHint)
{
    public string CodeText => $"{Stage}/{Code}";
}

/// <summary>
/// 摄像头画面桥。
///
/// 米家云端（api.mijia.tech）只提供 MIoT 属性读写：开关、移动侦测、云存储开关这些能拿到，
/// **画面拿不到**——实时流走的是本地 P2P（MISS/TUTK 固件，或 MIoT-Spec 的 p2p-stream / start-stream
/// SRTP 通道），必须由带 P2P 原生库的实现去握手解密，云端 HTTP 接口不含视频数据。
///
/// 因此这里的做法是接一个本地 go2rtc（https://github.com/AlexxIT/go2rtc，v1.9.13 起内置
/// xiaomi/miss 拉流）：go2rtc 用小米账号登录拿密钥，在局域网直连摄像头，转成
/// WebRTC / RTSP。本类只负责探测它、拼地址和给出可执行的下一步，视频由浏览器或本机播放器渲染。
/// </summary>
public static class CameraBridge
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>go2rtc 根地址（设置里可改），默认本机 1984。</summary>
    public static string BaseUrl
    {
        get
        {
            var raw = (AppSettingsStore.Go2rtcBase ?? "").Trim();
            if (raw.Length == 0) raw = "http://127.0.0.1:1984";
            return raw.TrimEnd('/');
        }
    }

    /// <summary>go2rtc 控制台（添加小米账号、查看已发现设备）。</summary>
    public static string ConsoleUrl => BaseUrl + "/";

    /// <summary>预览页：WebRTC 优先，浏览器原生渲染，不用额外解码器。</summary>
    public static string PlayUrl(string stream) =>
        $"{BaseUrl}/webrtc.html?src={Uri.EscapeDataString(stream)}";

    /// <summary>
    /// 约定：go2rtc 里的流名直接用摄像头 did，界面按钮就不用让用户再填一次名字。
    /// did 里只有数字和点，不需要额外转义。
    /// </summary>
    public static string StreamName(MijiaDevice device) => device.Did;

    /// <summary>
    /// 本机播放器地址。go2rtc 默认把 RTSP 开在 8554；画面源装在别的机器上时用那台机器的 IP。
    /// </summary>
    public static string RtspUrl(string stream)
    {
        var host = "127.0.0.1";
        if (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && !uri.IsLoopback && uri.Host.Length > 0)
            host = uri.Host;
        return $"rtsp://{host}:8554/{Uri.EscapeDataString(stream)}";
    }

    /// <summary>
    /// 推荐的 go2rtc 配置片段：控制接口和 RTSP 只绑本机，避免同网段任何人打开控制台
    /// 就能看到画面或改配置。WebRTC 仍留 8555，但把候选地址也收在本机。
    /// </summary>
    public const string RecommendedConfig =
        "api:\n" +
        "  listen: \"127.0.0.1:1984\"\n" +
        "rtsp:\n" +
        "  listen: \"127.0.0.1:8554\"\n" +
        "webrtc:\n" +
        "  listen: \"127.0.0.1:8555\"\n" +
        "  candidates:\n" +
        "    - \"127.0.0.1:8555\"\n";

    /// <summary>
    /// 探测画面源并把结论分成「配置 / 网关 / 流 / 可用」四段。
    /// 传 null 只验证网关本身，传设备则顺带确认这台摄像头登记过。
    /// </summary>
    public static async Task<CameraProbeResult> ProbeAsync(MijiaDevice? device, CancellationToken ct = default)
    {
        var baseUrl = BaseUrl;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Fail(CameraProbeStage.Config, CameraProbeCode.BadAddress,
                "画面源地址不是有效的 http/https 地址。填 http://127.0.0.1:1984，或那台常开设备的地址。",
                baseUrl, false);
        }

        var loopback = uri.IsLoopback;
        var security = loopback
            ? ""
            : "画面源地址不在本机：只建议在可信内网使用，并在 go2rtc 里把 api / rtsp 绑定到 127.0.0.1。";

        string json;
        try
        {
            using var resp = await Http.GetAsync(baseUrl + "/api/streams", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                var hint = status is 401 or 403
                    ? $"画面源拒绝访问（HTTP {status}）：它要求登录或只允许本机访问，确认地址和 go2rtc 的 api listen 一致。"
                    : $"画面源返回 HTTP {status}：确认这个端口是 go2rtc 的 api，而不是网页或别的服务。";
                return Fail(CameraProbeStage.Gateway, CameraProbeCode.HttpError, hint, baseUrl, loopback, security);
            }

            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(CameraProbeStage.Gateway, CameraProbeCode.Timeout,
                "连画面源超时。确认 go2rtc 已启动，地址和端口与它的 api listen 一致（默认 127.0.0.1:1984）。",
                baseUrl, loopback, security);
        }
        catch (Exception ex)
        {
            return Fail(CameraProbeStage.Gateway, CameraProbeCode.Unreachable,
                $"连不上画面源（{ex.GetBaseException().Message}）。确认 go2rtc 已启动、地址和端口没写错。",
                baseUrl, loopback, security);
        }

        List<string> streams;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Fail(CameraProbeStage.Gateway, CameraProbeCode.BadResponse,
                    "画面源返回的不是流列表。确认地址指向 go2rtc 的 api（默认 1984 端口）。",
                    baseUrl, loopback, security);
            streams = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        }
        catch (JsonException)
        {
            return Fail(CameraProbeStage.Gateway, CameraProbeCode.BadResponse,
                "画面源返回的内容无法解析。确认地址指向 go2rtc 的 api（默认 1984 端口）。",
                baseUrl, loopback, security);
        }

        if (device == null)
        {
            return new CameraProbeResult(CameraProbeStage.Ready, CameraProbeCode.None, true,
                "画面源已接通。", baseUrl, null, streams, loopback, security);
        }

        var wanted = StreamName(device);
        var name = streams.FirstOrDefault(s => string.Equals(s, wanted, StringComparison.Ordinal))
                   ?? streams.FirstOrDefault(s => s.Contains(wanted, StringComparison.Ordinal));

        if (name == null)
        {
            var hint = streams.Count == 0
                ? "画面源已接通，但里面还没有任何流：在控制台里用小米账号登录，把摄像头添加一次。"
                : $"画面源已接通，但还没认到这台摄像机（{wanted}）：在控制台里添加它一次，或把流名改成 did。";
            return new CameraProbeResult(CameraProbeStage.Stream, CameraProbeCode.StreamMissing, false,
                hint, baseUrl, null, streams, loopback, security);
        }

        return new CameraProbeResult(CameraProbeStage.Ready, CameraProbeCode.None, true,
            "画面源已接通，点「打开画面」即可查看。", baseUrl, name, streams, loopback, security);
    }

    private static CameraProbeResult Fail(CameraProbeStage stage, CameraProbeCode code, string hint,
        string baseUrl, bool loopback, string security = "")
        => new(stage, code, false, hint, baseUrl, null, Array.Empty<string>(), loopback, security);

    /// <summary>兼容旧调用：只问「网关在不在」。</summary>
    public static async Task<bool> PingAsync() => (await ProbeAsync(null).ConfigureAwait(false)).Ready;

    /// <summary>已配置的流名；go2rtc 未启动或没有流时返回空列表。</summary>
    public static async Task<List<string>> GetStreamNamesAsync()
        => (await ProbeAsync(null).ConfigureAwait(false)).Streams.ToList();

    /// <summary>用默认浏览器打开（go2rtc 的 WebRTC 页面需要浏览器环境）。</summary>
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
    /// 用本机播放器打开 RTSP。2K 摄像头多为 H.265，浏览器 WebRTC 常常黑屏，
    /// 而 mpv / VLC / PotPlayer 都能直接解。找不到播放器时返回 false，由界面提示复制地址。
    /// </summary>
    public static bool TryOpenPlayer(string url, out string playerName, out string? error)
    {
        playerName = "";
        error = null;
        if (!TryFindPlayer(out var path, out playerName))
            return false;

        try
        {
            var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            if (string.Equals(playerName, "mpv", StringComparison.OrdinalIgnoreCase))
            {
                info.ArgumentList.Add("--force-window=yes");
                info.ArgumentList.Add("--keep-open=no");
                info.ArgumentList.Add("--title=MiHomeHub 摄像头");
            }
            else if (string.Equals(playerName, "VLC", StringComparison.OrdinalIgnoreCase))
            {
                info.ArgumentList.Add("--no-video-title-show");
            }

            info.ArgumentList.Add(url);
            Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>按「常见安装路径 → PATH」找 mpv / VLC / PotPlayer，不下载、不捆绑。</summary>
    private static bool TryFindPlayer(out string path, out string name)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new (string Name, string Path)[]
        {
            ("mpv", Path.Combine(programFiles, "mpv", "mpv.exe")),
            ("mpv", Path.Combine(localAppData, "Programs", "mpv", "mpv.exe")),
            ("mpv", Path.Combine(userProfile, "scoop", "apps", "mpv", "current", "mpv.exe")),
            ("VLC", Path.Combine(programFiles, "VideoLAN", "VLC", "vlc.exe")),
            ("VLC", Path.Combine(programFilesX86, "VideoLAN", "VLC", "vlc.exe")),
            ("VLC", Path.Combine(localAppData, "Programs", "VLC", "vlc.exe")),
            ("PotPlayer", Path.Combine(programFiles, "DAUM", "PotPlayer", "PotPlayerMini64.exe")),
            ("PotPlayer", Path.Combine(programFilesX86, "DAUM", "PotPlayer", "PotPlayerMini64.exe")),
        };

        foreach (var (candidateName, candidatePath) in candidates)
        {
            if (File.Exists(candidatePath))
            {
                path = candidatePath;
                name = candidateName;
                return true;
            }
        }

        foreach (var exe in new[] { "mpv.exe", "vlc.exe", "PotPlayerMini64.exe" })
        {
            var found = FindOnPath(exe);
            if (found != null)
            {
                path = found;
                name = exe.StartsWith("mpv", StringComparison.OrdinalIgnoreCase) ? "mpv"
                    : exe.StartsWith("vlc", StringComparison.OrdinalIgnoreCase) ? "VLC" : "PotPlayer";
                return true;
            }
        }

        path = "";
        name = "";
        return false;
    }

    private static string? FindOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // PATH 里可能有非法项，跳过即可
            }
        }

        return null;
    }
}
