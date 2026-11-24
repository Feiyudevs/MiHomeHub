// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text.Json;

namespace MiHome.Services;

/// <summary>
/// 项目自带的本地配置：仓库根 <c>MiHome.settings.json</c>（随编译复制到 exe 同级目录）。
/// 里面是**可留空的两个变量字段**：后台地址与 app_id。更新检查与匿名统计共用同一套后台，
/// 所以只写一份；开源分发留空时应用就只用 GitHub、不上报。私有值写
/// <c>MiHome.settings.local.json</c>（已 gitignore，只覆盖非空字段），
/// 环境变量 <c>MIHOME_API_BASE</c> / <c>MIHOME_APP_ID</c> 可临时覆盖。
/// </summary>
internal sealed class AppSettings
{
    /// <summary>后台根地址，如 <c>https://example.com/api</c>；空 = 不用后台（更新只走 GitHub、统计不发送）。</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>本应用在后台的编号；0 = 未登记。</summary>
    public int AppId { get; set; }

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

        // 本机覆盖：只覆盖非空字段
        if (!string.IsNullOrWhiteSpace(local.Endpoint)) settings.Endpoint = local.Endpoint.Trim();
        if (local.AppId > 0) settings.AppId = local.AppId;
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
