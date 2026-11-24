// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text.Json;

namespace MiHome.Services;

/// <summary>
/// 项目自带的本地配置：仓库根 <c>MiHome.settings.json</c>（随编译复制到 exe 同级目录）。
/// 里面是**可留空的变量字段**，开源分发时全部为空，应用就只用 GitHub/不统计；
/// 自建部署或有私有后台时，开发者在本机把字段填上即可，不需要改代码、不写注册表。
/// 环境变量仍可覆盖（<c>MIHOME_BACKEND_BASE</c> 等），便于临时联调。
/// </summary>
internal sealed class AppSettings
{
    /// <summary>后台更新根地址，如 <c>https://example.com/api</c>；空 = 不用后台。</summary>
    public string BackendEndpoint { get; set; } = "";

    /// <summary>更新后台的 app_id；0 = 不用后台。</summary>
    public int BackendAppId { get; set; }

    /// <summary>匿名统计上报根地址；空 = 不发送。</summary>
    public string TelemetryEndpoint { get; set; } = "";

    /// <summary>统计后台的 app_id；0 = 不发送，只本地留痕。</summary>
    public int TelemetryAppId { get; set; }

    /// <summary>配置文件路径（exe 同级）。</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "MiHome.settings.json");

    /// <summary>本机覆盖文件（exe 同级，已被 .gitignore 排除）：只填自己需要的字段，优先于基础文件。</summary>
    public static string LocalPath => Path.Combine(AppContext.BaseDirectory, "MiHome.settings.local.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static AppSettings? _current;
    private static readonly object Gate = new();

    public static AppSettings Current
    {
        get
        {
            if (_current is not null) return _current;
            lock (Gate)
            {
                if (_current is null) _current = Load();
                return _current;
            }
        }
    }

    private static AppSettings Load()
    {
        var settings = Read(FilePath) ?? new AppSettings();
        var local = Read(LocalPath);
        if (local is null) return settings;

        // 本机覆盖：只覆盖非空字段，避免把基础文件里的其它设置抹掉
        if (!string.IsNullOrWhiteSpace(local.BackendEndpoint)) settings.BackendEndpoint = local.BackendEndpoint.Trim();
        if (local.BackendAppId > 0) settings.BackendAppId = local.BackendAppId;
        if (!string.IsNullOrWhiteSpace(local.TelemetryEndpoint)) settings.TelemetryEndpoint = local.TelemetryEndpoint.Trim();
        if (local.TelemetryAppId > 0) settings.TelemetryAppId = local.TelemetryAppId;
        return settings;
    }

    private static AppSettings? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
        }
        catch
        {
            // 配置写坏了就当没配：保持开源默认（不用后台、不上报）
            return null;
        }
    }
}
