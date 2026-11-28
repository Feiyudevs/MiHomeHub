// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MiHomeHub.Services.MacOS;
using MiHomeHub.Services.Setup;
using MiHomeHub.Services.Telemetry;

namespace MiHomeHub.Services.Update;

/// <summary>GitHub Release 里的更新清单（update.json）。字段全部来自打包脚本生成。</summary>
internal sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string Setup { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Mandatory { get; set; }
    /// <summary>后台清单下发的主下载地址（自有对象存储/CDN 直链）；为空时只用 GitHub。</summary>
    public string? PackageUrl { get; set; }
    /// <summary>后台标记的强制更新：客户端不应允许跳过。</summary>
    public bool Force { get; set; }
    /// <summary>网盘兜底页（百度/夸克等），自动下载彻底失败时给用户一个入口。</summary>
    [JsonPropertyName("disk_url")]
    public string? DiskUrl { get; set; }
    /// <summary>清单来源：backend（后台 check-update）或 github（Release 里的 update.json）。</summary>
    public string Source { get; set; } = "github";
    /// <summary>更新通道：stable（默认）或 test；通道不符的实例直接拒绝这份清单。</summary>
    public string Channel { get; set; } = "stable";
    /// <summary>可选有效期：过了这个时间清单作废（测试通道用来兜住泄露后长期可用）。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>安装包的额外下载地址（自建 CDN 兜底）；为空时只用 GitHub。</summary>
    public string[] Mirrors { get; set; } = Array.Empty<string>();
    /// <summary>后台灰度百分比（0~100）；仅用于状态文案。</summary>
    public int? RolloutPercent { get; set; }
    /// <summary>后台判定「本机暂未进入灰度」：版本更新，但按灰度策略先不下发。</summary>
    public bool Deferred { get; set; }
}

/// <summary>检查结果缓存：24 小时内不重复请求更新源，离线时也能显示上次查到的版本。</summary>
internal sealed class UpdateCacheState
{
    public DateTime CheckedAt { get; set; }
    /// <summary>缓存属于哪个通道：切换通道后旧缓存立即作废，避免拿稳定通道的清单去比测试版本。</summary>
    public string Channel { get; set; } = "stable";
    public UpdateManifest Manifest { get; set; } = new();
}

internal enum UpdateState
{
    UpToDate,
    Available,
    Failed,
}

internal sealed record UpdateCheckResult(UpdateState State, UpdateManifest? Manifest, string Message)
{
    public static UpdateCheckResult Failed(string message) => new(UpdateState.Failed, null, message);
}

/// <summary>
/// 更新检查与安装：正式通道优先读后台 <c>/track/check-update</c>（地址由本机配置提供，带灰度/强制），
/// 后台不可用时回落 GitHub Release 的 <c>update.json</code>；安装包优先从后台给出的直链下载，
/// 失败再试 GitHub。下载后能拿到 SHA256 就强校验，拿不到按 HTTPS 直链继续（并记录日志）。
/// 替换/回滚仍交给自研安装器。
/// </summary>
internal static class UpdateService
{
    private const string RepoBase = "https://github.com/Feiyudevs/MiHomeHub";

    /// <summary>
    /// macOS 版产物发布在 fork（上游仓库尚未合并 macOS 流水线）。
    /// 可用环境变量 <c>MIHOMEHUB_UPDATE_REPO</c> 覆盖，方便内网/私有仓库。
    /// </summary>
    private const string MacRepoBase = "https://github.com/woaiCarlos/MiHomeHub";

    private static bool IsMacOs => OperatingSystem.IsMacOS();

    /// <summary>当前架构对应的发布标识：osx-arm64 / osx-x64（Windows 固定 win-x64）。</summary>
    private static string CurrentRid =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";

    /// <summary>清单文件名：Windows 是仓库根的 update.json，macOS 是每个架构一份 update-macos-&lt;rid&gt;.json。</summary>
    private static string ManifestFileName => IsMacOs ? $"update-macos-{CurrentRid}.json" : "update.json";

    private static string ReleaseRepoBase
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("MIHOMEHUB_UPDATE_REPO")?.Trim();
            if (!string.IsNullOrEmpty(env)) return env.TrimEnd('/');
            return IsMacOs ? MacRepoBase : RepoBase;
        }
    }

    /// <summary>正式通道：releases/latest 永远指向最新一个正式 Release（预发布不算）。</summary>
    private static string StableManifestUrl => ReleaseRepoBase + "/releases/latest/download/" + ManifestFileName;
    /// <summary>测试通道：固定的滚动 tag <c>test</c>，Release 标为 pre-release，正式通道永远看不到。</summary>
    private static string TestManifestUrl => ReleaseRepoBase + "/releases/download/test/" + ManifestFileName;
    /// <summary>镜像：仓库根的 update.json（jsDelivr 加速，GitHub 主站不通时兜底）；只镜像正式通道。</summary>
    private static string MirrorManifestUrl => "https://cdn.jsdelivr.net/gh/Feiyudevs/MiHomeHub@main/update.json";

    /// <summary>
    /// 可选的后台更新源：地址与 app_id 不写死在代码里（开源仓库不该带私有后台），
    /// 取自仓库根 <c>MiHomeHub.settings.json</c> 的 <c>endpoint</c> / <c>appId</c>
    /// （该文件随编译复制到 exe 同级，字段留空即不用后台）；环境变量
    /// <c>MIHOMEHUB_API_BASE</c> / <c>MIHOMEHUB_APP_ID</c> 可临时覆盖，方便联调。
    /// 两个都齐了才走后台；否则完全不碰后台，只用 GitHub Releases，开源用户开箱即用。
    /// </summary>
    private static (string BaseUrl, int AppId)? BackendSource
    {
        get
        {
            var settings = AppSettings.Current;
            var endpoint = Environment.GetEnvironmentVariable("MIHOMEHUB_API_BASE")?.Trim();
            if (string.IsNullOrWhiteSpace(endpoint)) endpoint = settings.Endpoint?.Trim();

            var appId = 0;
            var appRaw = Environment.GetEnvironmentVariable("MIHOMEHUB_APP_ID")?.Trim();
            if (!int.TryParse(appRaw, out appId) || appId <= 0) appId = settings.AppId;

            return string.IsNullOrWhiteSpace(endpoint) || appId <= 0
                ? null
                : (endpoint!.TrimEnd('/'), appId);
        }
    }

    public const string StableChannel = "stable";
    public const string TestChannel = "test";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = CreateClient();

    /// <summary>当前程序版本（三段），与「关于」页展示同源。</summary>
    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// 更新通道，默认 <c>stable</c>。普通用户拿不到测试包：只有本机显式写了
    /// 环境变量 <c>MIHOMEHUB_UPDATE_CHANNEL=test</c> 或 <c>%APPDATA%\Feiyu\MiHomeHub\update\channel.txt</c>
    /// 才会去读测试通道的清单；清单本身也带 channel 字段，通道不符的实例会直接拒绝。
    /// </summary>
    public static string CurrentChannel
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("MIHOMEHUB_UPDATE_CHANNEL")?.Trim().ToLowerInvariant();
            if (env is StableChannel or TestChannel) return env;

            try
            {
                var file = Path.Combine(AppPaths.Dir, "update", "channel.txt");
                if (File.Exists(file))
                {
                    var text = File.ReadAllText(file).Trim().ToLowerInvariant();
                    if (text is StableChannel or TestChannel) return text;
                }
            }
            catch { /* 读不到就当默认通道 */ }

            return StableChannel;
        }
    }

    public static bool IsTestChannel => CurrentChannel == TestChannel;

    private static string StateFile => Path.Combine(AppPaths.Dir, "update", "state.json");

    /// <summary>
    /// 本地联调口子：把更新源指到某个目录（放 update.json + 安装包）或任意 HTTP 基地址。
    /// 优先读环境变量 <c>MIHOMEHUB_UPDATE_SOURCE</code>，其次读
    /// <c>%APPDATA%\Feiyu\MiHomeHub\update\source.txt</c>（从资源管理器启动也能生效）。
    /// 两者都没有时走 GitHub；正式发版不依赖它。
    /// </summary>
    private static string? SourceOverride
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("MIHOMEHUB_UPDATE_SOURCE")?.Trim();
            if (!string.IsNullOrEmpty(env)) return env;

            try
            {
                var file = Path.Combine(AppPaths.Dir, "update", "source.txt");
                if (File.Exists(file))
                {
                    var text = File.ReadAllText(file).Trim();
                    if (text.Length > 0) return text;
                }
            }
            catch { /* 读不到就当没配 */ }

            return null;
        }
    }

    // ─────────────────────────────── 检查 ───────────────────────────────

    /// <summary>距上次成功检查不足 24 小时且非强制时，直接用缓存结果（避免每次开设置都打网络）。</summary>
    public static bool ShouldAutoCheck()
        => SourceOverride is not null || !IsFresh(ReadState());

    /// <summary>
    /// 只读本地缓存、不联网：启动时先用上次结果决定要不要提示。没有缓存或通道切换过返回 null；
    /// 版本比较按当前版本重算，缓存里旧的「有更新」在升级后不会误报。
    /// </summary>
    public static UpdateCheckResult? TryGetCachedResult()
    {
        var state = ReadState();
        if (state is null) return null;
        var cachedChannel = state.Channel?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(cachedChannel)) cachedChannel = StableChannel;
        if (cachedChannel != CurrentChannel) return null;
        return Evaluate(state.Manifest);
    }

    public static async Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken ct = default)
    {
        CleanupDownloads();

        var overridden = SourceOverride is not null;
        if (!force && !overridden && ReadState() is { } cached && IsFresh(cached))
            return Evaluate(cached.Manifest);
        UpdateManifest? manifest = null;
        var errors = new List<string>();

        // 正式通道先问后台：只有配置了后台、且后台不可用（网络/服务异常/该版本没配安装包）才回落 GitHub。
        // 测试通道与本地联调源永远不碰后台，避免把内部版本推到灰度桶里。
        // macOS 也不问后台：后台登记的是 Windows 的 .exe 直链，平台对不上。
        if (!overridden && !IsMacOs && CurrentChannel == StableChannel && BackendSource is { } backend)
        {
            try
            {
                manifest = await FetchBackendManifestAsync(backend.BaseUrl, backend.AppId, ct);
            }
            catch (Exception ex)
            {
                errors.Add("后台 " + Describe(ex));
            }
        }

        if (manifest is null)
        {
            foreach (var url in ManifestSources())
            {
                try
                {
                    manifest = await FetchManifestAsync(url, ct);
                    break;
                }
                catch (Exception ex)
                {
                    errors.Add(Describe(ex));
                }
            }
        }

        if (manifest is null)
            return UpdateCheckResult.Failed("检查更新失败：" + (errors.FirstOrDefault() ?? "无法连接更新服务器"));

        WriteState(manifest);
        return Evaluate(manifest);
    }

    private static UpdateCheckResult Evaluate(UpdateManifest manifest)
    {
        var latest = SetupFiles.NormalizeVersion(manifest.Version);
        if (latest is null)
            return UpdateCheckResult.Failed("更新清单格式不正确");

        // 后台按灰度判定本机暂不下发：版本更新但不在本批灰度里
        if (manifest.Deferred)
        {
            var pct = manifest.RolloutPercent is > 0 and < 100 ? $"（当前灰度 {manifest.RolloutPercent}%）" : "";
            return new UpdateCheckResult(UpdateState.UpToDate, null,
                $"新版本 {latest} 正在分批推送{pct}，暂未覆盖本机");
        }

        var cmp = SetupFiles.CompareVersions(latest, CurrentVersion);
        if (cmp > 0)
            return new UpdateCheckResult(UpdateState.Available, manifest,
                manifest.Force ? $"发现新版本 {latest}（强制更新）" : $"发现新版本 {latest}");
        if (cmp < 0)
            return new UpdateCheckResult(UpdateState.UpToDate, null, $"{CurrentVersion} 比服务器上的版本还新");
        return new UpdateCheckResult(UpdateState.UpToDate, null, "已是最新版本");
    }

    /// <summary>
    /// 后台 check-update（地址与 app_id 来自本机配置）：灰度桶按 device_id 哈希，响应里带强制更新与更新说明。
    /// 没有 SHA256 字段，能对上 GitHub 同名版本时借它的校验值；对不上就按 HTTPS 直链继续。
    /// </summary>
    private static async Task<UpdateManifest> FetchBackendManifestAsync(string baseUrl, int appId, CancellationToken ct)
    {
        var url = $"{baseUrl}/track/check-update" +
                  $"?app_id={appId}&platform=Windows" +
                  $"&app_version={Uri.EscapeDataString(CurrentVersion)}" +
                  $"&device_id={Uri.EscapeDataString(TelemetryService.DeviceId)}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var shouldUpdate = root.TryGetProperty("should_update", out var s) && s.ValueKind == JsonValueKind.True;
        var latest = root.TryGetProperty("latest_version", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim().TrimStart('v', 'V')
            : null;
        var packageUrl = root.TryGetProperty("package_url", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()?.Trim()
            : null;

        // 后台根本没有可用版本（还没登记过任何安装包）：交给 GitHub 兜底
        if (string.IsNullOrWhiteSpace(latest))
            throw new InvalidOperationException("后台还没有发布版本");

        var rollout = root.TryGetProperty("rolloutPercent", out var rp) && rp.TryGetInt32(out var rv) ? rv : (int?)null;

        if (!shouldUpdate)
        {
            var cmp = SetupFiles.CompareVersions(latest!, CurrentVersion);
            // 同版本 → 已是最新；版本更新但本机没进灰度 → 标 Deferred，别误报「发现新版本」
            return new UpdateManifest
            {
                Version = latest!,
                Channel = StableChannel,
                Source = "backend",
                Setup = DefaultSetupName(latest!),
                RolloutPercent = rollout,
                Deferred = cmp > 0,
            };
        }

        // 后台已经发了版本但还没填安装包直链：仍然把版本报给用户，不要偷偷回落 GitHub 假装“已是最新”
        // （下载时会明确提示去后台补 package_url）
        var manifest = new UpdateManifest
        {
            Version = latest!,
            Channel = StableChannel,
            Source = "backend",
            PackageUrl = string.IsNullOrWhiteSpace(packageUrl) ? null : packageUrl,
            Force = root.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True,
            DiskUrl = root.TryGetProperty("disk_url", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null,
            Notes = root.TryGetProperty("release_notes", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : "",
            Setup = SetupNameFromUrl(packageUrl, latest),
            Sha256 = "",
        };

        // 能对上 GitHub 同名正式版本时借它的 SHA256 和资产名：后台不含校验值，这是唯一的强校验来源
        if (await TryGetGithubCounterpartAsync(latest!, ct) is { } github)
        {
            manifest.Sha256 = github.Sha256;
            manifest.Setup = github.Setup;
            manifest.Mirrors = github.Mirrors;
        }

        Validate(manifest);
        return manifest;
    }

    /// <summary>GitHub 正式清单里是否有同一个版本；有就返回它（用于 SHA256 与兜底地址）。</summary>
    private static async Task<UpdateManifest?> TryGetGithubCounterpartAsync(string version, CancellationToken ct)
    {
        try
        {
            var github = await FetchManifestAsync(StableManifestUrl, ct);
            var a = SetupFiles.NormalizeVersion(github.Version);
            var b = SetupFiles.NormalizeVersion(version);
            return a is not null && a == b ? github : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SetupNameFromUrl(string? packageUrl, string version)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(packageUrl))
            {
                var path = Uri.TryCreate(packageUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : packageUrl;
                var name = Uri.UnescapeDataString(Path.GetFileName(path));
                if (name.EndsWith(SetupExtension, StringComparison.OrdinalIgnoreCase)) return name;
            }
        }
        catch { /* 用默认名 */ }
        return DefaultSetupName(version);
    }

    /// <summary>安装包扩展名：Windows 是 .exe，macOS 是 .dmg。</summary>
    private static string SetupExtension => IsMacOs ? ".dmg" : ".exe";

    private static string DefaultSetupName(string version) =>
        IsMacOs ? $"MiHomeHub-Setup-{version}-{CurrentRid}.dmg" : $"MiHomeHub-Setup-{version}-win-x64.exe";

    /// <summary>
    /// 给清单地址加一个时间戳参数，绕开 CDN/代理对 releases/latest 的 302 短时缓存。
    /// 不加的话刚发完新版，客户端可能还读到旧清单（GitHub 的 latest 跳转会被中间层缓存住）。
    /// </summary>
    private static string WithCacheBuster(string source)
    {
        try
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return source;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return source;
            var builder = new UriBuilder(uri);
            var query = builder.Query.TrimStart('?');
            var stamp = "mhh=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            builder.Query = string.IsNullOrEmpty(query) ? stamp : query + "&" + stamp;
            return builder.Uri.ToString();
        }
        catch
        {
            return source;
        }
    }

    private static async Task<UpdateManifest> FetchManifestAsync(string source, CancellationToken ct)
    {        string json;
        if (File.Exists(source))
        {
            // 本地目录联调：直接把 update.json 读进来
            json = await File.ReadAllTextAsync(source, ct);
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await Http.GetAsync(WithCacheBuster(source), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(timeout.Token);
        }

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions)
                       ?? throw new InvalidOperationException("更新清单是空的");

        Validate(manifest);
        return manifest;
    }

    private static void Validate(UpdateManifest manifest)
    {
        if (SetupFiles.NormalizeVersion(manifest.Version) is null)
            throw new InvalidOperationException("清单里的版本号不合法");
        var name = Path.GetFileName(manifest.Setup);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(SetupExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("清单里的安装包文件名不合法");
        var hash = manifest.Sha256?.Trim() ?? "";
        if (hash.Length > 0 && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new InvalidOperationException("清单里的 SHA256 不合法");
        // 后台清单不含校验值时允许为空，但必须至少有 HTTPS 直链，避免“什么校验都没有还去装”；
        // 后台只发了版本、还没配直链的情况例外：版本要照实报出来，下载时再提示去补地址。
        if (hash.Length == 0 && string.IsNullOrWhiteSpace(manifest.PackageUrl) &&
            !string.Equals(manifest.Source, "backend", StringComparison.Ordinal))
            throw new InvalidOperationException("清单缺少有效的 SHA256");

        var channel = ManifestChannel(manifest);
        if (channel is not (StableChannel or TestChannel))
            throw new InvalidOperationException("清单里的更新通道不合法");

        // 通道不符就拒绝：即使这份清单被镜像/转发出去，稳定通道的实例也不会被引到测试版本。
        // 本地联调口子（source.txt / 环境变量）例外，方便在一台机器上模拟两种通道。
        if (SourceOverride is null && channel != CurrentChannel)
            throw new InvalidOperationException($"更新通道不匹配（清单是 {channel}，本机是 {CurrentChannel}）");

        if (manifest.ExpiresAt is { } expires && DateTimeOffset.UtcNow > expires)
            throw new InvalidOperationException("更新清单已过期");
    }

    /// <summary>清单通道归一化：缺省视为 stable。</summary>
    private static string ManifestChannel(UpdateManifest manifest)
    {
        var raw = manifest.Channel?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(raw) ? StableChannel : raw;
    }

    // ─────────────────────────────── 下载 ───────────────────────────────

    /// <summary>下载安装包并校验 SHA256；返回可直接执行的本地路径。已下载过且校验通过则直接复用。</summary>
    public static async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(manifest.Setup);
        var dir = Path.Combine(Path.GetTempPath(), "MiHomeHub-Update-" + manifest.Version);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, fileName);

        if (File.Exists(target) && await VerifyAsync(target, manifest, ct))
        {
            progress?.Report(1);
            return target;
        }

        var candidates = new List<string>();
        if (SourceOverride is { } localBase)
            candidates.Add(JoinSource(localBase, fileName));
        // 后台给的直链优先，其次 GitHub（兜底），最后清单里的 mirrors
        if (!string.IsNullOrWhiteSpace(manifest.PackageUrl))
            candidates.Add(manifest.PackageUrl!);
        var channel = ManifestChannel(manifest);
        candidates.Add(channel == TestChannel
            ? $"{ReleaseRepoBase}/releases/download/test/{Uri.EscapeDataString(fileName)}"
            : $"{ReleaseRepoBase}/releases/latest/download/{Uri.EscapeDataString(fileName)}");
        candidates.AddRange(manifest.Mirrors.Where(m => !string.IsNullOrWhiteSpace(m)));

        Exception? lastError = null;
        foreach (var url in candidates)
        {
            try
            {
                await DownloadToFileAsync(url, target, progress, ct);
                if (await VerifyAsync(target, manifest, ct))
                    return target;

                File.Delete(target);
                throw new InvalidOperationException("安装包校验失败（SHA256 不匹配）");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        // 后台只给了版本没给直链时，说清楚该补什么，而不是丢一句泛泛的下载失败
        if (string.Equals(manifest.Source, "backend", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(manifest.PackageUrl))
            throw new InvalidOperationException(
                $"后台已发布 {manifest.Version}，但还没有配置安装包直链（package_url）；" +
                "请先在后台「版本管理」上传安装包并填 package_url，或把该版本发到 GitHub Releases");

        var hint = string.IsNullOrWhiteSpace(manifest.DiskUrl)
            ? ""
            : "；也可以点「网盘下载」手动取安装包";
        throw new InvalidOperationException("下载安装包失败：" +
                                            (lastError is null ? "没有可用的下载地址" : Describe(lastError)) +
                                            hint);
    }

    private static async Task DownloadToFileAsync(string url, string target, IProgress<double>? progress,
        CancellationToken ct)
    {
        var part = target + ".part";
        try
        {
            if (File.Exists(url))
            {
                // 本地目录联调：直接拷贝，进度只有完成态
                File.Copy(url, part, overwrite: true);
                File.Move(part, target, overwrite: true);
                progress?.Report(1);
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var dest = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                            81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, timeout.Token);
                    if (read <= 0) break;
                    await dest.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    received += read;
                    if (total > 0) progress?.Report(Math.Clamp(received / (double)total, 0, 1));
                }
            }

            File.Move(part, target, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    private static async Task<bool> MatchesAsync(string file, string expected, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(file);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Hex(hash).Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 校验安装包：有 SHA256 就强校验；后台清单没给校验值时只能做最基本的大小合理性检查
    /// （安装包不小于 1 MB），并依赖 HTTPS 直链本身。
    /// </summary>
    private static async Task<bool> VerifyAsync(string file, UpdateManifest manifest, CancellationToken ct)
    {
        var hash = manifest.Sha256?.Trim() ?? "";
        if (hash.Length == 64) return await MatchesAsync(file, hash, ct);
        try
        {
            return new FileInfo(file).Length >= 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }

    // ─────────────────────────────── 安装 ───────────────────────────────

    /// <summary>
    /// 只有从「已安装目录」启动的程序才允许自动更新：便携运行的 exe 被替换后
    /// 安装器会把新版装到默认目录并另起一个进程，用户会看到"更新完还多了一份"。
    /// </summary>
    public static bool IsRunningFromInstall(out string root)
    {
        root = string.Empty;
        try
        {
            // macOS：从 .app 包里启动才算「已安装」（便携运行是 bin/ 下的裸可执行文件）。
            if (IsMacOs)
            {
                if (!MacOsUpdater.TryGetRunningBundle(out var bundle)) return false;
                root = bundle;
                return true;
            }

            var installed = SetupPaths.FindInstalledRoot();
            if (installed is null) return false;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !SetupFiles.IsUnder(exe, SetupPaths.AppDir(installed)))
                return false;
            root = installed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 拉起已经下载好的安装包，走 <c>--silent</c> 无界面更新；调用方随后应立即退出主程序。
    /// macOS 上的产物是 .dmg：挂载镜像并把新版本换到当前 .app 位置，再重新拉起。
    /// </summary>
    public static void LaunchInstaller(string setupPath)
    {
        if (!File.Exists(setupPath))
            throw new FileNotFoundException("安装包不存在", setupPath);

        if (IsMacOs)
        {
            MacOsUpdater.InstallFromDmg(setupPath);
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = "--silent",
            WorkingDirectory = Path.GetDirectoryName(setupPath)!,
            UseShellExecute = true,
        });
    }

    // ─────────────────────────────── 缓存 ───────────────────────────────

    private static UpdateCacheState? ReadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            var json = File.ReadAllText(StateFile);
            return JsonSerializer.Deserialize<UpdateCacheState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsFresh(UpdateCacheState? state)
    {
        if (state is null) return false;
        // 通道切换后旧缓存立即作废：先在测试通道查过 1.0.9，切回 stable 不能继续显示它
        var cachedChannel = state.Channel?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(cachedChannel)) cachedChannel = StableChannel;
        if (cachedChannel != CurrentChannel) return false;
        return DateTime.UtcNow - state.CheckedAt < CheckInterval;
    }

    private static void WriteState(UpdateManifest manifest)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            var state = new UpdateCacheState
            {
                CheckedAt = DateTime.UtcNow,
                Channel = ManifestChannel(manifest),
                Manifest = manifest,
            };
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
        }
        catch
        {
            // 缓存写不进去不影响检查结果
        }
    }

    // ─────────────────────────────── 工具 ───────────────────────────────

    private static IEnumerable<string> ManifestSources()
    {
        var channel = CurrentChannel;
        if (SourceOverride is { } localBase)
        {
            yield return JoinSource(localBase, ManifestFileName);
            yield break; // 联调时只认指定源，避免悄悄回落到线上版本
        }
        if (channel == TestChannel)
        {
            yield return TestManifestUrl;
            yield break; // 测试通道不做镜像兜底：只认那一个滚动的 pre-release
        }
        yield return StableManifestUrl;
        // 镜像只有上游仓库根目录那份（Windows 版）update.json，macOS 清单没有对应镜像。
        if (!IsMacOs) yield return MirrorManifestUrl;
    }

    /// <summary>本地目录拼文件名；HTTP 基地址拼相对路径。</summary>
    private static string JoinSource(string baseOrDir, string file)
        => Directory.Exists(baseOrDir)
           || (Path.IsPathRooted(baseOrDir) && !baseOrDir.Contains("://", StringComparison.Ordinal))
            ? Path.Combine(baseOrDir, file)
            : baseOrDir.TrimEnd('/') + "/" + file;

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "还没有发布更新清单",
        HttpRequestException => "更新源暂时不可达",
        TaskCanceledException => "连接更新服务器超时",
        _ => ex.Message,
    };

    /// <summary>清掉两天前的更新下载目录（一次全量包 ~46 MB，失败/中断的残留不该长期占 %TEMP%）。</summary>
    private static void CleanupDownloads()
    {
        try
        {
            var root = Path.GetTempPath();
            foreach (var dir in Directory.EnumerateDirectories(root, "MiHomeHub-Update-*"))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < TimeSpan.FromDays(2)) continue;
                    Directory.Delete(dir, recursive: true);
                }
                catch { /* 文件被占用就留到下次 */ }
            }
        }
        catch { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MiHomeHubUpdater", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
