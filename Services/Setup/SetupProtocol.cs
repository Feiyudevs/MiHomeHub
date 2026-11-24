// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MiHome.Services.Setup;

/// <summary>
/// 自安装协议：主程序与安装外壳共享的唯一事实来源（外壳通过 Compile Include 链接本文件）。
/// 这里只允许出现 BCL 类型，禁止引用 MiHome 的其它类型。
/// </summary>
internal static class SetupProtocol
{
    public const string ProductName = "MiHome";
    public const string ExeName = "MiHome.exe";

    /// <summary>旧版安装根上的独立卸载器名：仅为兼容与清理保留，新安装不再生成。</summary>
    public const string UninstallerName = "Uninstall MiHome.exe";

    /// <summary>外壳解压 payload 的临时目录前缀（GUID 后缀）。</summary>
    public const string SetupTempPrefix = "MiHome-Setup-";

    /// <summary>卸载搬家中转副本前缀（GUID 后缀，.exe）。</summary>
    public const string CleanupTempPrefix = "MiHome-Cleanup-";

    public const string LeaseSuffix = ".lease";
    public const string LogFileName = "MiHome-setup.log";
    public const string LogMutexName = @"Local\MiHome-Setup-Log";

    public const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductName;
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = ProductName;

    public const string AppDirName = "app";
    public const string OldDirName = "app.old";
    public const string NewDirName = "app.new";
    public const string PreviousDirName = "app.previous";
    public const string LockFileName = ".setup.lock";

    /// <summary>允许出现在参数表里的名字（严格白名单，防参数串位）。</summary>
    public static readonly HashSet<string> AllowedOptionNames = new(StringComparer.Ordinal)
    {
        "package",
        "temp-dir",
        "self-test-root",
        "ready-event",
        "shown-event",
        "launcher-pid",
    };

    /// <summary>允许出现的纯开关（无值，解析后固定映射为 "1"）：更新时无界面安装。</summary>
    public static readonly HashSet<string> AllowedFlags = new(StringComparer.Ordinal)
    {
        "silent",
    };

    private static readonly Regex EventPattern =
        new(@"^Local\\MiHome-(Setup|Uninstall)-[0-9a-f]{32}$", RegexOptions.Compiled);

    public static bool IsUninstallerIdentity(string? exePathWithoutExtension)
        => !string.IsNullOrEmpty(exePathWithoutExtension)
           && exePathWithoutExtension.StartsWith("Uninstall ", StringComparison.OrdinalIgnoreCase)
           && exePathWithoutExtension.IndexOf(ProductName, StringComparison.OrdinalIgnoreCase) >= 0;

    public static bool IsValidEventName(string? name)
        => !string.IsNullOrEmpty(name) && EventPattern.IsMatch(name);

    public static string NewSetupTempDir()
        => Path.Combine(Path.GetTempPath(), SetupTempPrefix + Guid.NewGuid().ToString("N"));

    public static string NewCleanupExePath()
        => Path.Combine(Path.GetTempPath(), CleanupTempPrefix + Guid.NewGuid().ToString("N") + ".exe");

    public static string NewEventName(string kind)
        => $@"Local\MiHome-{kind}-{Guid.NewGuid():N}";

    /// <summary>
    /// 解析 <c>--name value</c> 名值对（从 <paramref name="start"/> 开始）。
    /// 未知参数、缺值、单横线参数一律抛出——参数串位会直接把文件复制到错误目录。
    /// </summary>
    public static Dictionary<string, string> ParsePairs(string[] args, int start)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = start; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"多余的参数：{arg}");

            var name = arg[2..];
            if (AllowedFlags.Contains(name))
            {
                map[name] = "1";
                continue;
            }
            if (!AllowedOptionNames.Contains(name))
                throw new ArgumentException($"不认识的参数：{arg}");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"参数缺少值：{arg}");

            map[name] = args[++i];
        }
        return map;
    }

    public static int ParsePid(string? value)
        => int.TryParse(value, out var pid) && pid > 0 ? pid : -1;
}
