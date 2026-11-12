// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>
/// 设备列表本地缓存，落在 %APPDATA%\Feiyu\MiHome\devices.json。
/// 启动先读缓存秒开，联网成功后再写回；网络失败时继续用上次列表。
/// </summary>
public static class DeviceCacheStore
{
    private static readonly string Dir = AppPaths.Dir;
    private static readonly string FilePath = Path.Combine(Dir, "devices.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        // 房间名由 homelist 反查后写入 MijiaDevice.RoomName，缓存要一并带上
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public static List<MijiaDevice>? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var list = JsonSerializer.Deserialize<List<MijiaDevice>>(File.ReadAllText(FilePath), Json);
            return list is { Count: > 0 } ? list : null;
        }
        catch
        {
            // 文件损坏时当作没有缓存
            return null;
        }
    }

    public static void Save(IReadOnlyList<MijiaDevice> devices)
    {
        if (devices.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(devices, Json));
        }
        catch
        {
            // 写入失败不影响本次运行
        }
    }

    /// <summary>删除本地设备缓存；退出登录后不能再把上个账号的列表当秒开数据。</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // 删除失败则下次 Load 仍会读到旧数据，但不阻塞退出流程
        }
    }
}
