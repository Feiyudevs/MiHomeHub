// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

namespace MiHomeHub.Services.Setup;

/// <summary>
/// 安装位置约定：默认装到当前用户目录（无需管理员），升级时锁死为已装位置。
/// 程序目录与用户数据目录分离（数据在 %APPDATA%\Feiyu\MiHomeHub，卸载时可选删除）。
/// </summary>
internal static class SetupPaths
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        SetupProtocol.ProductName);

    public static string AppDir(string root) => Path.Combine(root, SetupProtocol.AppDirName);
    public static string AppExe(string root) => Path.Combine(AppDir(root), SetupProtocol.ExeName);
    public static string RootUninstaller(string root) => Path.Combine(root, SetupProtocol.UninstallerName);

    /// <summary>与 Services/AppPaths 保持一致：%APPDATA%\Feiyu\MiHomeHub。</summary>
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Feiyu", "MiHomeHub");

    /// <summary>读卸载注册表里的 InstallLocation；没有或目录已丢失时返回 null。</summary>
    public static string? FindInstalledRoot()
    {
        try
        {
            var location = SetupRegistry.ReadString(SetupProtocol.UninstallKeyPath, "InstallLocation");
            if (string.IsNullOrWhiteSpace(location)) return null;
            var root = SetupFiles.Normalize(location);
            if (File.Exists(AppExe(root))) return root;
            return Directory.Exists(root) ? root : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>安装目标：已装过就沿用旧位置（升级不允许改目录）。</summary>
    public static string ResolveTargetRoot()
        => FindInstalledRoot() ?? SetupFiles.Normalize(DefaultRoot);

    /// <summary>
    /// 安装目录校验：拒绝需要管理员权限的位置（Program Files/Windows/盘根）、网络路径，
    /// 以及「非空且不是本程序安装目录」的文件夹（卸载要整目录删，不能误删用户文件）。
    /// </summary>
    public static void ValidateTargetRoot(string root)
    {
        var full = SetupFiles.Normalize(root);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new SetupFailureException("检查安装位置", $"不支持网络路径：{full}", fixHint: "请换到本机用户目录");

        var volumeRoot = Path.GetPathRoot(full) ?? string.Empty;
        if (string.Equals(full, volumeRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new SetupFailureException("检查安装位置", $"不能直接安装到盘根：{full}");

        foreach (var protectedDir in ProtectedDirs())
        {
            if (string.IsNullOrEmpty(protectedDir)) continue;
            if (SetupFiles.IsUnder(full, protectedDir))
                throw new SetupFailureException("检查安装位置",
                    $"该位置需要管理员权限：{full}", fixHint: "请安装到当前用户目录");
        }

        if (Directory.Exists(full))
        {
            var entries = Directory.GetFileSystemEntries(full);
            if (entries.Length > 0 && !LooksLikeOurInstall(full))
                throw new SetupFailureException("检查安装位置",
                    $"目标文件夹非空，且不是本程序的安装目录：{full}", fixHint: "请删除该文件夹或换一个位置");
        }

        SetupFiles.Check(full);
    }

    public static bool LooksLikeOurInstall(string root)
    {
        if (File.Exists(AppExe(root))) return true;
        if (File.Exists(RootUninstaller(root)) && Directory.Exists(AppDir(root))) return true;
        // 重装兜底：程序文件被删/损坏但卸载登记仍指向这里，允许覆盖安装修复
        return IsRegisteredInstall(root);
    }

    private static bool IsRegisteredInstall(string root)
    {
        try
        {
            var location = SetupRegistry.ReadString(SetupProtocol.UninstallKeyPath, "InstallLocation");
            return location is not null
                   && string.Equals(SetupFiles.Normalize(location), SetupFiles.Normalize(root),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string[] ProtectedDirs()
    {
        var list = new List<string>();
        try { list.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)); } catch { }
        try { list.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)); } catch { }
        try { list.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows)); } catch { }
        try { list.Add(Environment.GetFolderPath(Environment.SpecialFolder.System)); } catch { }
        return list.ToArray();
    }
}
