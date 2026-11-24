// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MiHome.Services.Telemetry;

internal sealed class TelemetryEvent
{
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public Dictionary<string, object?>? Props { get; set; }
    public long Ts { get; set; }
}

internal sealed class TelemetryConfig
{
    /// <summary>用户是否在设置里打开了「匿名使用统计」；默认关闭。</summary>
    public bool Enabled { get; set; }
    /// <summary>
    /// 上报基地址，最终请求 <c>{endpoint}/track</c>。
    /// **不进本地配置文件**：只从环境变量 <c>MIHOME_TELEMETRY_ENDPOINT</c> 读取，
    /// 开源分发的代码与用户机器上都不该出现私有后台地址。
    /// </summary>
    [JsonIgnore]
    public string Endpoint { get; set; } = TelemetryService.DefaultEndpoint;
    /// <summary>软件在统计后台的编号（0 = 未登记，只本地留痕）；同样只从环境变量读取，不落盘。</summary>
    [JsonIgnore]
    public int AppId { get; set; }
    /// <summary>本机随机 UUID，不含任何硬件指纹；设置里可重置。</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>尚未上报的前台活跃毫秒，随任意上报搭便车发出。</summary>
    public long PendingActiveMs { get; set; }
}

/// <summary>
/// 可选匿名使用统计：默认关闭，只有用户显式开启后才上报。
/// 只报「发生了什么」（启动、登录、设备规模、控制成败、更新链路），
/// 不报账号、家庭/设备名称、令牌、地址等可识别信息。
/// </summary>
internal static class TelemetryService
{
    /// <summary>
    /// 默认不内置任何上报地址：开源版本不带私有后台，地址由本机配置
    /// （<c>telemetry.json</c> 的 <c>Endpoint</c> 或环境变量 <c>MIHOME_TELEMETRY_ENDPOINT</c>）提供；
    /// 没配地址时即使打开开关也不会发出请求。
    /// </summary>
    public const string DefaultEndpoint = "";

    private const int BatchSize = 20;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    private static readonly object Gate = new();
    private static readonly Queue<TelemetryEvent> Queue = new();
    private static readonly List<TelemetryEvent> Recent = new();
    private static readonly SemaphoreSlim Sending = new(1, 1);

    private static TelemetryConfig _config = new();
    private static bool _loaded;
    private static DateTime _lastFlush = DateTime.MinValue;
    private static DateTime _lastActivePersist = DateTime.MinValue;
    private static Timer? _ticker;
    private static bool _foreground;
    private static bool _shutdown;

    private static readonly HttpClient Http = CreateClient();

    public static string ConfigFile => Path.Combine(AppPaths.Dir, "telemetry.json");

    /// <summary>事件是否会被发送：用户开启 + 已配置 app_id + 地址可用。</summary>
    public static bool IsSending => EnsureLoaded().Enabled && _config.AppId > 0 && !string.IsNullOrWhiteSpace(_config.Endpoint);

    /// <summary>用户开关状态（可能已开启但还没配置 app_id，此时只本地留痕）。</summary>
    public static bool IsEnabled
    {
        get { return EnsureLoaded().Enabled; }
        set { SetEnabled(value); }
    }

    /// <summary>本机匿名设备标识（更新灰度分桶也用它）；首次读取时生成并落盘。</summary>
    public static string DeviceId
    {
        get
        {
            var cfg = EnsureLoaded();
            if (!string.IsNullOrEmpty(cfg.DeviceId)) return cfg.DeviceId;
            lock (Gate)
            {
                if (string.IsNullOrEmpty(cfg.DeviceId))
                {
                    cfg.DeviceId = Guid.NewGuid().ToString("N");
                    Save();
                }
                return cfg.DeviceId;
            }
        }
    }

    /// <summary>「关于」页展示的一句话状态。</summary>
    public static string StatusText
    {
        get
        {
            var cfg = EnsureLoaded();
            if (!cfg.Enabled) return "已关闭：不会产生任何网络请求";
            if (cfg.AppId <= 0) return "已开启，但还没有登记 app_id：事件只留在本机";
            if (string.IsNullOrWhiteSpace(cfg.Endpoint)) return "已开启，但没有上报地址：事件只留在本机";
            return "已开启：只上报匿名事件，可在下方查看最近内容";
        }
    }

    /// <summary>启动时调用：读配置、补设备标识、开始前台计时与后台 flush。</summary>
    public static void Init()
    {
        var cfg = EnsureLoaded();
        if (string.IsNullOrEmpty(cfg.DeviceId))
        {
            cfg.DeviceId = Guid.NewGuid().ToString("N");
            Save();
        }
        if (!cfg.Enabled) return;

        Track("app_launch", "启动应用", new Dictionary<string, object?>
        {
            ["silent"] = !AppSettingsStore.ShowMainWindowOnStartup,
        });

        StartTicker();
    }

    /// <summary>退出时调用：停计时、把剩余时长和队列尽量发出去（最多等 3 秒，不阻塞关闭）。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_shutdown) return;
            _shutdown = true;
        }
        try { _ticker?.Dispose(); } catch { }
        PersistActive();
        try { FlushAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    public static void Track(string name, string? label = null, Dictionary<string, object?>? props = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var cfg = EnsureLoaded();
        if (!cfg.Enabled) return;

        var evt = new TelemetryEvent
        {
            Name = name,
            Label = Truncate(label, 50),
            Props = SanitizeProps(props),
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        lock (Gate)
        {
            Queue.Enqueue(evt);
            Recent.Add(evt);
            while (Recent.Count > 5) Recent.RemoveAt(0);
        }
        MaybeFlush();
    }

    public static void SetEnabled(bool on)
    {
        var cfg = EnsureLoaded();
        if (cfg.Enabled == on) return;
        cfg.Enabled = on;
        if (on)
        {
            // 开启即算一次启动事件，便于立刻看到一条真实数据
            lock (Gate) Queue.Enqueue(new TelemetryEvent
            {
                Name = "app_launch",
                Label = "启动应用",
                Props = new Dictionary<string, object?> { ["reason"] = "telemetry_enabled" },
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            if (string.IsNullOrEmpty(cfg.DeviceId)) cfg.DeviceId = Guid.NewGuid().ToString("N");
            StartTicker();
        }
        else
        {
            lock (Gate) Queue.Clear();
            try { _ticker?.Dispose(); } catch { }
            _ticker = null;
        }
        Save();
        MaybeFlush();
    }

    /// <summary>最近 5 条事件的原文，给「查看最近上报内容」用；不含设备标识。</summary>
    public static IReadOnlyList<string> RecentLines(int count = 5)
    {
        List<TelemetryEvent> items;
        lock (Gate)
        {
            items = Recent.Skip(Math.Max(0, Recent.Count - count)).ToList();
        }
        return items.Select(e =>
        {
            var props = e.Props is { Count: > 0 }
                ? " " + string.Join(", ", e.Props.Select(kv => $"{kv.Key}={kv.Value}"))
                : "";
            return $"{e.Name}{(e.Label is null ? "" : $"（{e.Label}）")}{props}";
        }).ToList();
    }

    // ─────────────────────────────── 上报 ───────────────────────────────

    private static void MaybeFlush()
    {
        var count = 0;
        lock (Gate) count = Queue.Count;
        if (count == 0 && _config.PendingActiveMs <= 0) return;
        if (count < BatchSize && DateTime.UtcNow - _lastFlush < FlushInterval) return;
        _ = FlushAsync();
    }

    private static async Task FlushAsync()
    {
        var cfg = EnsureLoaded();
        if (!cfg.Enabled || cfg.AppId <= 0 || string.IsNullOrWhiteSpace(cfg.Endpoint)) return;
        if (!await Sending.WaitAsync(0)) return;

        List<TelemetryEvent> batch;
        long activeMs;
        lock (Gate)
        {
            if (Queue.Count == 0 && _config.PendingActiveMs <= 0)
            {
                Sending.Release();
                return;
            }
            batch = Queue.ToList();
            activeMs = Math.Max(0, _config.PendingActiveMs);
            _lastFlush = DateTime.UtcNow;
        }

        var body = new
        {
            app_id = cfg.AppId,
            device_id = cfg.DeviceId,
            app_version = typeof(TelemetryService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            platform = "Windows",
            os = "Windows " + Environment.OSVersion.Version.Build,
            active_ms = activeMs,
            events = batch.Select(e => new
            {
                name = e.Name,
                label = e.Label,
                props = e.Props,
                ts = e.Ts,
            }).ToArray(),
        };

        try
        {
            using var timeout = new CancellationTokenSource(HttpTimeout);
            using var content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(TrackUrl(cfg.Endpoint), content, timeout.Token);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (text.Contains("\"ok\":false", StringComparison.Ordinal)) throw new InvalidOperationException("服务端未受理");

            lock (Gate)
            {
                for (var i = 0; i < batch.Count; i++) Queue.Dequeue();
                _config.PendingActiveMs = Math.Max(0, _config.PendingActiveMs - activeMs);
            }
            Save();
        }
        catch
        {
            // 失败不清队列也不清时长：下次上报自动合并补发（服务端按设备累计，重复时长不会算两次）
        }
        finally
        {
            Sending.Release();
        }
    }

    private static string TrackUrl(string endpoint)
        => endpoint.TrimEnd('/') + "/track";

    // ─────────────────────────────── 前台计时 ───────────────────────────────

    private static void StartTicker()
    {
        if (_ticker is not null) return;
        _ticker = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static void Tick()
    {
        try
        {
            var cfg = _config;
            if (!cfg.Enabled || _shutdown) return;

            _foreground = IsForegroundProcess();
            if (_foreground)
            {
                lock (Gate) _config.PendingActiveMs += 1000;
            }
            if (DateTime.UtcNow - _lastActivePersist > TimeSpan.FromSeconds(60))
            {
                _lastActivePersist = DateTime.UtcNow;
                PersistActive();
                MaybeFlush();
            }
        }
        catch
        {
            // 计时失败不影响主流程
        }
    }

    private static bool IsForegroundProcess()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    // ─────────────────────────────── 配置 ───────────────────────────────

    private static TelemetryConfig EnsureLoaded()
    {
        if (_loaded) return _config;
        lock (Gate)
        {
            if (_loaded) return _config;
            _config = LoadConfig();
            _loaded = true;
            return _config;
        }
    }

    private static TelemetryConfig LoadConfig()
    {
        var cfg = new TelemetryConfig();
        try
        {
            if (File.Exists(ConfigFile))
            {
                cfg = JsonSerializer.Deserialize<TelemetryConfig>(File.ReadAllText(ConfigFile), Json) ?? cfg;
            }
        }
        catch { /* 配置损坏按默认（关闭）处理 */ }

        // 地址与编号来自项目变量字段文件（MiHome.settings.json），不写进 telemetry.json、也不内置默认值
        var settings = AppSettings.Current;
        cfg.Endpoint = settings.TelemetryEndpoint?.Trim() ?? "";
        cfg.AppId = settings.TelemetryAppId;

        // 环境变量优先，便于开发和 CI 自检
        var envOn = Environment.GetEnvironmentVariable("MIHOME_TELEMETRY")?.Trim();
        if (envOn is "1" or "true" or "on") cfg.Enabled = true;
        else if (envOn is "0" or "false" or "off") cfg.Enabled = false;
        var envEndpoint = Environment.GetEnvironmentVariable("MIHOME_TELEMETRY_ENDPOINT")?.Trim();
        if (!string.IsNullOrEmpty(envEndpoint)) cfg.Endpoint = envEndpoint;
        var envApp = Environment.GetEnvironmentVariable("MIHOME_TELEMETRY_APP_ID")?.Trim();
        if (int.TryParse(envApp, out var appId)) cfg.AppId = appId;

        return cfg;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Dir);
            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(_config, Json), new UTF8Encoding(false));
        }
        catch { /* 写不进去不影响本次运行 */ }
    }

    private static void PersistActive()
    {
        lock (Gate)
        {
            if (_config.PendingActiveMs <= 0) return;
            var copy = _config;
            try
            {
                File.WriteAllText(ConfigFile, JsonSerializer.Serialize(copy, Json), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ─────────────────────────────── 工具 ───────────────────────────────

    private static Dictionary<string, object?>? SanitizeProps(Dictionary<string, object?>? props)
    {
        if (props is null || props.Count == 0) return null;
        var clean = new Dictionary<string, object?>();
        foreach (var (key, value) in props)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 32) continue;
            if (value is string s) clean[key] = Truncate(s, 40);
            else if (value is null) continue;
            else clean[key] = value;
        }
        return clean.Count == 0 ? null : clean;
    }

    private static string? Truncate(string? text, int max)
    {
        if (text is null) return null;
        var t = text.Trim();
        if (t.Length <= max) return t;
        return t[..max];
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MiHome", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
}
