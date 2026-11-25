// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;

namespace MiHomeHub.Services;

/// <summary>
/// 应用数据根目录：%APPDATA%\Feiyu\MiHomeHub。
/// 登录态、设备列表缓存、图标缓存、主题与窗口位置都放在这里。
/// </summary>
public static class AppPaths
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Feiyu", "MiHomeHub");

    /// <summary>改名前的数据目录（MiHome），仅用于一次性迁移。</summary>
    public static readonly string LegacyDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Feiyu", "MiHome");

    /// <summary>
    /// 品牌改名后首次启动：把旧目录里的登录态、设备缓存、图标、桌面布局与偏好整体搬过来。
    /// 只有新目录还没有登录态/设备缓存时才执行；旧目录保留不删，迁移失败也不阻塞启动。
    /// </summary>
    public static void TryMigrateLegacy()
    {
        try
        {
            if (!Directory.Exists(LegacyDir)) return;
            if (File.Exists(Path.Combine(Dir, "auth.json")) || File.Exists(Path.Combine(Dir, "devices.json")))
                return;
            CopyDirectory(LegacyDir, Dir);
        }
        catch
        {
            // 迁移失败就当没有旧数据：用户重新登录即可，不能因此起不来。
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            try
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
            catch
            {
                // 单个文件失败（占用/损坏）不影响其余数据
            }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}
