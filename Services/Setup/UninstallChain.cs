// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace MiHomeHub.Services.Setup;

/// <summary>
/// 卸载链：正在运行的 exe 无法删除自己所在目录，所以卸载永远是
/// 「安装根\app\MiHomeHub.exe → 复制自己到 %TEMP% → 中转进程等原进程退出 →
///  复制 app 目录到 %TEMP% → 从临时目录启动 MiHomeHub.exe --uninstall-worker」。
/// 外壳与主程序同源编译本文件。
/// </summary>
internal static class UninstallChain
{
    private static readonly Regex GuidTail =
        new("^[0-9a-f]{32}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>安装目录里的主程序收到 --uninstall：搬到临时目录后中转。</summary>
    public static int LaunchFromInstalled(string selfExePath, string[] args)
    {
        SetupLog.Info("uninstall-launch", "ok", $"self={selfExePath}");
        var exe = Path.GetFullPath(selfExePath);
        var selfDir = Path.GetDirectoryName(exe) ?? string.Empty;
        var root = string.Equals(Path.GetFileName(selfDir), SetupProtocol.AppDirName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(selfDir) ?? string.Empty
            : selfDir;
        var appDir = Path.Combine(root, SetupProtocol.AppDirName);

        if (!Directory.Exists(appDir) || !File.Exists(Path.Combine(appDir, SetupProtocol.ExeName)))
        {
            SetupLog.Fail("uninstall-launch", new InvalidOperationException($"没有找到安装目录：{appDir}"), path: appDir);
            return 2;
        }

        var copy = SetupProtocol.NewCleanupExePath();
        var lease = SetupLease.TryAcquire(copy);
        try
        {
            File.Copy(exe, copy, overwrite: true);
            SetupFiles.WaitReadable(copy, 5000);

            var readyEvent = FindOption(args, "ready-event") ?? SetupProtocol.NewEventName("Uninstall");
            var psi = new ProcessStartInfo
            {
                FileName = copy,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--relocate");
            psi.ArgumentList.Add(appDir);
            psi.ArgumentList.Add("-1");
            psi.ArgumentList.Add("--ready-event");
            psi.ArgumentList.Add(readyEvent);
            psi.ArgumentList.Add("--launcher-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("--package");
            psi.ArgumentList.Add(exe);

            Process.Start(psi);
            SetupLog.Info("uninstall-relocate", "ok", $"copy={copy}");
            return 0;
        }
        catch (Exception ex)
        {
            SetupLog.Fail("uninstall-relocate", ex, path: copy);
            lease?.Dispose();
            try { File.Delete(copy); } catch { }
            return 2;
        }
    }

    /// <summary>中转 worker：等原卸载进程退出 → 把 app 目录搬到临时目录 → 启动真正的卸载界面。</summary>
    public static int RunRelocate(string[] args)
    {
        if (args.Length < 3)
        {
            SetupLog.Fail("relocate-args", new ArgumentException("--relocate 参数不足"));
            return 2;
        }

        var appDir = SetupFiles.Check(args[1]);
        // 协议里 appDir 后面跟一个占位 "-1"，名值对从它之后开始
        var pairsStart = args[2].Equals("-1", StringComparison.Ordinal) ? 3 : 2;
        var pairs = SetupProtocol.ParsePairs(args, pairsStart);
        var launcherPid = SetupProtocol.ParsePid(pairs.GetValueOrDefault("launcher-pid"));

        SetupLog.Info("relocate-start", "ok", $"appDir={appDir} launcherPid={launcherPid}");
        WaitLauncher(launcherPid, 15000);

        string? tempDir = null;
        try
        {
            if (!Directory.Exists(appDir))
                throw new SetupFailureException("准备卸载", $"安装目录不存在：{appDir}");

            var root = Path.GetDirectoryName(appDir) ?? string.Empty;
            if (!string.Equals(Path.GetFileName(appDir), SetupProtocol.AppDirName, StringComparison.OrdinalIgnoreCase)
                || !SetupFiles.IsUnder(appDir, root))
                throw new SetupFailureException("准备卸载", $"安装目录不合法：{appDir}");

            tempDir = SetupProtocol.NewSetupTempDir();
            SetupFiles.CopyDirectory(appDir, tempDir);
            var tempExe = Path.Combine(tempDir, SetupProtocol.ExeName);
            if (!SetupFiles.WaitReadable(tempExe))
                throw new SetupFailureException("准备卸载", $"临时副本不可读：{tempExe}");

            var shownEvent = pairs.GetValueOrDefault("shown-event");
            if (!SetupProtocol.IsValidEventName(shownEvent))
                shownEvent = SetupProtocol.NewEventName("Uninstall");

            var psi = new ProcessStartInfo
            {
                FileName = tempExe,
                WorkingDirectory = tempDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--uninstall-worker");
            psi.ArgumentList.Add("--package");
            psi.ArgumentList.Add(pairs.GetValueOrDefault("package") ?? string.Empty);
            psi.ArgumentList.Add("--temp-dir");
            psi.ArgumentList.Add(tempDir);
            psi.ArgumentList.Add("--shown-event");
            psi.ArgumentList.Add(shownEvent!);
            psi.ArgumentList.Add("--launcher-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());

            SetupLog.Info("application-entry", "ok", $"worker={tempExe}");
            using var worker = Process.Start(psi);
            if (worker is null)
                throw new SetupFailureException("启动卸载界面", "无法启动卸载进程");

            if (!worker.WaitForExit(TimeSpan.FromMinutes(10)))
            {
                try { worker.Kill(entireProcessTree: true); } catch { }
                throw new SetupFailureException("启动卸载界面", "卸载界面长时间无响应");
            }

            SetupLog.Info("relocate-finish", "ok", $"exit={worker.ExitCode}");
            return worker.ExitCode;
        }
        catch (Exception ex)
        {
            SetupLog.Fail("relocate", ex, path: appDir);
            return 2;
        }
        finally
        {
            DeleteTempWithBackoff(tempDir);
            TryDeleteSelf();
        }
    }

    /// <summary>启动时清理 %TEMP% 下历史残留：只认两类白名单名字 + GUID 尾巴，活事务靠 lease 跳过。</summary>
    public static void Cleanup(int budgetMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var temp = Path.GetTempPath();
            foreach (var entry in Directory.EnumerateFileSystemEntries(temp))
            {
                if (sw.ElapsedMilliseconds > budgetMs) break;

                var name = Path.GetFileName(entry);
                bool isSetupDir = name.StartsWith(SetupProtocol.SetupTempPrefix, StringComparison.OrdinalIgnoreCase)
                                  && Directory.Exists(entry);
                bool isCleanupExe = name.StartsWith(SetupProtocol.CleanupTempPrefix, StringComparison.OrdinalIgnoreCase)
                                    && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                    && File.Exists(entry);
                if (!isSetupDir && !isCleanupExe) continue;

                var tail = name[(isSetupDir ? SetupProtocol.SetupTempPrefix.Length : SetupProtocol.CleanupTempPrefix.Length)..];
                if (!GuidTail.IsMatch(tail)) continue;

                var leasePath = entry + SetupProtocol.LeaseSuffix;
                if (File.Exists(leasePath))
                {
                    // 能排他打开 = 持有者已死 → 可删；打不开 = 活事务，跳过
                    try
                    {
                        using var fs = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        fs.Position = 0;
                        DeleteEntry(entry);
                        SetupLog.Info("cleanup", "ok", $"lease-dead={entry}");
                    }
                    catch
                    {
                        continue;
                    }
                    continue;
                }

                // 无 lease 的新残留（1 天内）保留，避免干扰仍在运行的旧安装器
                try
                {
                    var age = DateTime.Now - (isSetupDir ? Directory.GetCreationTime(entry) : File.GetCreationTime(entry));
                    if (age < TimeSpan.FromDays(1) || IsProcessUsingPath(entry))
                        continue;
                    DeleteEntry(entry);
                    SetupLog.Info("cleanup", "ok", $"stale={entry}");
                }
                catch (Exception ex)
                {
                    SetupLog.Fail("cleanup", ex, path: entry);
                }
            }
        }
        catch (Exception ex)
        {
            SetupLog.Fail("cleanup-root", ex);
        }
    }

    private static void DeleteEntry(string entry)
    {
        try
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else if (File.Exists(entry)) File.Delete(entry);
            try { File.Delete(entry + SetupProtocol.LeaseSuffix); } catch { }
        }
        catch { /* 单项失败不动整轮清理 */ }
    }

    private static bool IsProcessUsingPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var module = p.MainModule?.FileName;
                    if (module is not null && Path.GetFullPath(module).StartsWith(full, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    private static string? FindOption(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i].Equals("--" + name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void WaitLauncher(int pid, int timeoutMs)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(timeoutMs);
            SetupLog.Info("relocate-wait", "ok", $"launcher={pid}");
        }
        catch (ArgumentException)
        {
            SetupLog.Info("relocate-wait", "ok", $"launcher={pid} 已退出");
        }
        catch (Exception ex)
        {
            SetupLog.Fail("relocate-wait", ex);
        }
    }

    private static void DeleteTempWithBackoff(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        for (var i = 0; i < 20; i++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                try { File.Delete(dir + SetupProtocol.LeaseSuffix); } catch { }
                return;
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
        SetupLog.Fail("delete-temp", new IOException($"临时目录删除失败：{dir}"), path: dir);
    }

    private static void TryDeleteSelf()
    {
        try
        {
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self) && File.Exists(self)) File.Delete(self);
        }
        catch { /* 运行中的 exe 删不掉时留给 Cleanup */ }
    }
}
