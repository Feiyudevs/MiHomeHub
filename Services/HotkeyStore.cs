// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>hotkeys.json 的落盘结构：总开关 + 绑定列表。</summary>
public sealed class HotkeyStoreData
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("items")] public List<HotkeyBinding> Items { get; set; } = new();
}

/// <summary>
/// 全局快捷键持久化在 %APPDATA%\Feiyu\MiHome\hotkeys.json。
/// 与 app.json 一样写盘出 UI 线程；带版本号，短时间连续改动只落最后一份，避免旧值覆盖新值。
/// </summary>
public static class HotkeyStore
{
    private static readonly string FilePath = Path.Combine(AppPaths.Dir, "hotkeys.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object IOLock = new();
    private static int _pendingVersion;

    public static HotkeyStoreData Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new HotkeyStoreData();
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<HotkeyStoreData>(json, Options);
            if (data == null) return new HotkeyStoreData();
            data.Items ??= new List<HotkeyBinding>();
            foreach (var binding in data.Items) binding.NormalizeLegacySteps();
            // 没有 id 的脏数据直接丢弃：删除 / 启停都按 id 定位；没有步骤的绑定按下也没东西可做
            data.Items.RemoveAll(x => string.IsNullOrEmpty(x.Id) || x.VirtualKey <= 0 || x.Steps.Count == 0);
            return data;
        }
        catch
        {
            // 文件损坏时按「全空 + 默认启用」，不影响启动
            return new HotkeyStoreData();
        }
    }

    public static void Save(bool enabled, IEnumerable<HotkeyBinding> items)
    {
        var snapshot = new HotkeyStoreData { Enabled = enabled, Items = new List<HotkeyBinding>(items) };
        var payload = JsonSerializer.Serialize(snapshot, Options);
        var version = Interlocked.Increment(ref _pendingVersion);

        _ = Task.Run(() =>
        {
            try
            {
                lock (IOLock)
                {
                    // 排队期间又改过一次：本次结果已过期，直接放弃，让后一次落盘
                    if (version != Volatile.Read(ref _pendingVersion)) return;
                    Directory.CreateDirectory(AppPaths.Dir);
                    File.WriteAllText(FilePath, payload);
                }
            }
            catch
            {
                // 写入失败不影响本次运行，下次改动会再写
            }
        });
    }
}
