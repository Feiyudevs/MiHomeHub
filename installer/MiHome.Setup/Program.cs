// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using MiHome.Services.Setup;

namespace MiHome.Setup;

/// <summary>
/// 自安装器外壳（无 UI 框架）：
/// 1) 解压内嵌 payload.zip 到 %TEMP%；
/// 2) 挂起启动主程序（--setup），由主程序 Avalonia 界面完成安装；
/// 3) 旧版卸载器身份兼容：安装根的 Uninstall MiHome.exe 走 <see cref="UninstallChain"/> 中转；
/// 4) 退出时清理临时目录。
/// </summary>
internal static class SetupShellProgram
{
    private const int MaxWaitMs = 30 * 60 * 1000;

    [STAThread]
    private static int Main(string[] args)
    {
        try { Directory.SetCurrentDirectory(Path.GetTempPath()); } catch { }

        var exePath = Environment.ProcessPath ?? string.Empty;
        var exeName = Path.GetFileNameWithoutExtension(exePath);

        if (args.Length > 0 && args[0].Equals("--relocate", StringComparison.OrdinalIgnoreCase))
            return UninstallChain.RunRelocate(args);

        if (SetupProtocol.IsUninstallerIdentity(exeName))
            return UninstallChain.LaunchFromInstalled(exePath, args);

        if (args.Length > 0 && args[0].Equals("--preview", StringComparison.OrdinalIgnoreCase))
            return RunPreview(exePath, args);

        return RunSetup(exePath, args);
    }

    /// <summary>预览安装包界面：解压 payload 供界面显示体积/许可，但绝不执行安装事务。</summary>
    private static int RunPreview(string exePath, string[] args)
    {
        var variant = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1].ToLowerInvariant()
            : "install";
        if (variant is not ("install" or "update" or "reinstall" or "uninstall" or "error"))
        {
            ShellMessageBox.Error("预览参数错误：只支持 install / update / reinstall / uninstall / error");
            return 2;
        }

        var tempDir = SetupProtocol.NewSetupTempDir();
        SetupLease? lease = null;
        try
        {
            Directory.CreateDirectory(tempDir);
            lease = SetupLease.TryAcquire(tempDir, string.Empty);
            ExtractPayload(tempDir);

            var appExe = Path.Combine(tempDir, SetupProtocol.ExeName);
            if (!SetupFiles.WaitReadable(appExe))
                throw new SetupFailureException("解压安装包", "解压后主程序不可读");

            var shownEventName = SetupProtocol.NewEventName("Setup");
            using var shownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, shownEventName);

            var arguments = new StringBuilder();
            arguments.Append("--preview ").Append(variant);
            Append(arguments, "--temp-dir", tempDir);
            Append(arguments, "--shown-event", shownEventName);
            Append(arguments, "--launcher-pid", Environment.ProcessId.ToString());

            var exitCode = LaunchAndWait(appExe, arguments.ToString(), tempDir, shownEvent);
            SetupLog.Info("preview-shell", "ok", $"variant={variant} exit={exitCode}");
            if (exitCode != 0)
                ShellMessageBox.Error("预览界面启动失败，未做任何系统改动。\n\n诊断日志：" + SetupLog.FilePath);
            return exitCode;
        }
        catch (Exception ex)
        {
            SetupLog.Fail("preview-shell", ex, path: tempDir);
            ShellMessageBox.Error("预览启动失败：" + ex.Message + "\n\n诊断日志：" + SetupLog.FilePath);
            return 2;
        }
        finally
        {
            lease?.Dispose();
            DeleteTempWithBackoff(tempDir);
        }
    }

    private static int RunSetup(string exePath, string[] args)
    {
        string? selfTestRoot = null;
        var silent = false;
        try
        {
            var pairs = SetupProtocol.ParsePairs(args, 0);
            selfTestRoot = pairs.GetValueOrDefault("self-test-root");
            silent = pairs.GetValueOrDefault("silent") is not null;
        }
        catch (Exception ex)
        {
            SetupLog.Fail("shell-args", ex, details: string.Join(' ', args));
            ShellMessageBox.Error("安装程序参数错误：" + ex.Message);
            return 2;
        }

        UninstallChain.Cleanup();

        var tempDir = SetupProtocol.NewSetupTempDir();
        SetupLease? lease = null;
        try
        {
            Directory.CreateDirectory(tempDir);
            lease = SetupLease.TryAcquire(tempDir, selfTestRoot ?? string.Empty);

            SetupLog.Info("shell-extract", "ok", $"temp={tempDir}");
            ExtractPayload(tempDir);

            var appExe = Path.Combine(tempDir, SetupProtocol.ExeName);
            if (!SetupFiles.WaitReadable(appExe))
                throw new SetupFailureException("解压安装包", "解压后主程序不可读");

            var shownEventName = SetupProtocol.NewEventName("Setup");
            using var shownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, shownEventName);

            var arguments = BuildArguments(exePath, tempDir, shownEventName, selfTestRoot, silent);

            var exitCode = LaunchAndWait(appExe, arguments, tempDir, shownEvent);
            if (exitCode != 0 && !silent && !shownEvent.WaitOne(0))
            {
                // 应用一次都没进（窗口未显示）：大概率杀软/首次解压竞态，重试一次
                // 静默安装不显示窗口，没有这个信号，绝不能重试——否则会把安装事务跑两遍
                SetupLog.Fail("shell-launch-retry", new InvalidOperationException($"exit={exitCode}"),
                    details: "窗口未显示，重试一次");
                exitCode = LaunchAndWait(appExe, arguments, tempDir, shownEvent);
            }

            // 静默失败由主程序自己弹错误框（它知道失败步骤），外壳不再重复弹
            if (exitCode != 0 && !silent && selfTestRoot is null)
                ShellMessageBox.Error("安装程序未能完成。\n\n诊断日志：" + SetupLog.FilePath);

            SetupLog.Info("shell-finish", "ok", $"exit={exitCode}");
            return exitCode;
        }
        catch (Exception ex)
        {
            SetupLog.Fail("shell", ex, path: tempDir);
            if (selfTestRoot is null)
                ShellMessageBox.Error("安装程序启动失败：" + ex.Message + "\n\n诊断日志：" + SetupLog.FilePath);
            return 2;
        }
        finally
        {
            lease?.Dispose();
            DeleteTempWithBackoff(tempDir);
        }
    }

    private static string BuildArguments(
        string exePath, string tempDir, string shownEventName, string? selfTestRoot, bool silent)
    {
        var sb = new StringBuilder();
        sb.Append("--setup");
        if (silent) sb.Append(" --silent");
        Append(sb, "--package", exePath);
        Append(sb, "--temp-dir", tempDir);
        Append(sb, "--shown-event", shownEventName);
        Append(sb, "--launcher-pid", Environment.ProcessId.ToString());
        Append(sb, "--ready-event", shownEventName);
        if (selfTestRoot is not null) Append(sb, "--self-test-root", selfTestRoot);
        return sb.ToString();
    }

    private static int LaunchAndWait(string appExe, string arguments, string tempDir, EventWaitHandle shownEvent)
    {
        var child = ShellNative.StartSuspended(appExe, arguments, tempDir);
        SetupLog.Info("application-entry", "ok", $"pid={child.Pid}");

        try
        {
            var code = ShellNative.WaitForExit(child, MaxWaitMs);
            if (code is null)
            {
                SetupLog.Fail("shell-wait", new TimeoutException("安装界面长时间无响应"), details: $"pid={child.Pid}");
                ShellNative.Terminate(child);
                return 3;
            }
            return code.Value;
        }
        finally
        {
            ShellNative.Close(child);
        }
    }

    /// <summary>解压内嵌 payload.zip；逐项做路径校验，防 zip-slip。</summary>
    private static void ExtractPayload(string destination)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                           ?? throw new SetupFailureException("解压安装包",
                               "安装包损坏：缺少内嵌 payload", fixHint: "请重新下载安装包");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Contains("..", StringComparison.Ordinal))
                throw new SetupFailureException("解压安装包", $"安装包包含非法路径：{entry.FullName}");

            var target = SetupFiles.Check(Path.Combine(destination, name));
            if (!SetupFiles.IsUnder(target, destination))
                throw new SetupFailureException("解压安装包", $"安装包路径越界：{entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void Append(StringBuilder sb, string name, string value)
        => sb.Append(' ').Append(name).Append(' ').Append(Quote(value));

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        return value.Contains(' ') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
    }

    private static void DeleteTempWithBackoff(string dir)
    {
        for (var i = 0; i < 12; i++)
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
        SetupLog.Fail("shell-cleanup", new IOException($"临时目录删除失败：{dir}"), path: dir);
    }
}
