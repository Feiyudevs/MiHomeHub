// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MiHome.Services.Setup;

/// <summary>
/// 安装模式的最早入口（必须在单实例 Mutex / 桌面钩子 / 托盘之前调用）：
/// <c>--uninstall</c> 转交卸载链；<c>--setup</c>/<c>--uninstall-worker</c> 进入安装界面；
/// <c>--self-test-root</c> 走隔离的无界面自测。
/// </summary>
internal static class SetupEntry
{
    public static bool Active { get; private set; }
    public static SetupOptions? Options { get; private set; }

    private static EventHandleHolder? _shown;
    /// <summary>返回 true 表示本进程已完成任务，不应再启动主界面。</summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0) return false;

        try
        {
            if (args[0].Equals("--preview", StringComparison.OrdinalIgnoreCase))
                return ActivatePreview(args);

            if (args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
                return RunUninstallRelay(args);

            if (args[0].Equals("--relocate", StringComparison.OrdinalIgnoreCase))
            {
                // 卸载链把主程序自身复制到 %TEMP% 后，用本参数做目录搬家
                Environment.ExitCode = UninstallChain.RunRelocate(args);
                return true;
            }

            if (args[0].Equals("--setup", StringComparison.OrdinalIgnoreCase))
                return Activate(args, SetupMode.Install);

            if (args[0].Equals("--uninstall-worker", StringComparison.OrdinalIgnoreCase))
                return Activate(args, SetupMode.Uninstall);
        }
        catch (Exception ex)
        {
            SetupLog.Fail("setup-entry", ex, details: string.Join(' ', args));
            if (Options?.SelfTestRoot is { } testRoot)
                WriteSelfTestError(testRoot, "setup-entry", ex.Message, null);
            else
                SetupMessageBox.Error("MiHome", "安装程序启动失败：" + ex.Message + "\n\n" + SetupLog.FilePath);
            Environment.ExitCode = 2;
            return true;
        }

        return false;
    }

    /// <summary>安装界面真正显示后置位，外壳据此判定应用是否进入。</summary>
    public static void SignalShown()
    {
        try
        {
            if (Options?.ShownEvent is null) return;
            _shown = new EventHandleHolder(Options.ShownEvent);
            _shown.Set();
            SetupLog.Info("window-shown", "ok", Options.ShownEvent);
        }
        catch (Exception ex)
        {
            SetupLog.Fail("window-shown", ex);
        }
    }

    /// <summary>预览入口：只显示安装/卸载界面并跑假进度，不落盘、不写注册表。</summary>
    private static bool ActivatePreview(string[] args)
    {
        var variant = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1].ToLowerInvariant()
            : "install";
        if (variant is not ("install" or "update" or "reinstall" or "uninstall" or "error"))
            throw new ArgumentException("--preview 只支持 install / update / reinstall / uninstall / error");

        var mode = variant == "uninstall" ? SetupMode.Uninstall : SetupMode.Install;
        Options = new SetupOptions
        {
            Mode = mode,
            Preview = true,
            PreviewVariant = variant,
        };
        Active = true;
        SetupLog.Info("preview-entry", "ok", $"variant={variant}");
        return false;
    }

    private static bool Activate(string[] args, SetupMode mode)
    {
        var options = ParseOptions(args, mode);
        Options = options;
        Active = true;
        SetupLog.Info("application-entry", "ok", $"mode={mode} temp={options.TempDir}");

        if (options.IsSelfTest)
        {
            RunSelfTest(options);
            return true;
        }

        UninstallChain.Cleanup(6000);
        return false;
    }

    private static SetupOptions ParseOptions(string[] args, SetupMode mode)
    {
        var pairs = SetupProtocol.ParsePairs(args, 1);

        var tempDir = pairs.GetValueOrDefault("temp-dir");
        if (string.IsNullOrWhiteSpace(tempDir) || !Directory.Exists(tempDir))
            throw new ArgumentException("--temp-dir 缺失或不存在");

        var selfTest = pairs.GetValueOrDefault("self-test-root");
        if (selfTest is not null)
        {
            var full = SetupFiles.Normalize(selfTest);
            var temp = SetupFiles.Normalize(Path.GetTempPath());
            var name = Path.GetFileName(full);
            if (!SetupFiles.IsUnder(full, temp) || !name.StartsWith("MiHome-test-", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("隔离自测目录必须是 %TEMP% 下 MiHome-test- 前缀的目录");
            selfTest = full;
        }

        var shown = pairs.GetValueOrDefault("shown-event");
        if (shown is not null && !SetupProtocol.IsValidEventName(shown))
            throw new ArgumentException("--shown-event 不合法");

        return new SetupOptions
        {
            Mode = mode,
            PackagePath = pairs.GetValueOrDefault("package") ?? string.Empty,
            TempDir = SetupFiles.Normalize(tempDir),
            SelfTestRoot = selfTest,
            ShownEvent = shown,
            ReadyEvent = pairs.GetValueOrDefault("ready-event"),
            Silent = pairs.GetValueOrDefault("silent") is not null,
            LauncherPid = SetupProtocol.ParsePid(pairs.GetValueOrDefault("launcher-pid")),
        };
    }

    private static bool RunUninstallRelay(string[] args)
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self) || !File.Exists(self))
        {
            SetupMessageBox.Error("MiHome", "没有找到安装信息，无法卸载。\n\n" + SetupLog.FilePath);
            SetupLog.Fail("uninstall-relay", new IOException("未找到当前程序路径"));
            Environment.ExitCode = 2;
            return true;
        }

        var exit = UninstallChain.LaunchFromInstalled(self, args);
        if (exit != 0)
        {
            SetupMessageBox.Error("MiHome", "没有找到安装信息，无法卸载。\n\n" + SetupLog.FilePath);
            SetupLog.Fail("uninstall-relay", new IOException("无法启动卸载链"), path: self);
        }
        Environment.ExitCode = exit;
        return true;
    }

    // ────────────────────────────── 隔离自测（无界面） ──────────────────────────────

    private static void RunSelfTest(SetupOptions options)
    {
        var root = options.SelfTestRoot!;
        SetupResult result;
        try
        {
            Directory.CreateDirectory(root);
            var service = new SetupService(options);
            result = options.Mode == SetupMode.Install
                ? service.InstallAsync(desktopShortcut: true, telemetryOptIn: false, null).GetAwaiter().GetResult()
                : service.UninstallAsync(removeData: false, null).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            result = new SetupResult { Success = false, Root = root, Error = ex.Message, Step = "self-test" };
        }

        if (result.Success)
        {
            var checkError = options.Mode == SetupMode.Install
                ? VerifyInstalled(root)
                : VerifyUninstalled(root);

            if (checkError is null)
            {
                WriteSelfTestHelper(root, options.Mode, result);
                Environment.ExitCode = 0;
                SetupLog.Info("self-test", "pass", root);
            }
            else
            {
                WriteSelfTestError(root, "verify", checkError, SetupLog.FilePath);
                Environment.ExitCode = 1;
            }
        }
        else
        {
            WriteSelfTestError(root, result.Step ?? "self-test", result.Error ?? "未知错误", result.LogPath);
            Environment.ExitCode = 1;
        }
    }

    private static string? VerifyInstalled(string root)
    {
        if (!File.Exists(SetupPaths.AppExe(root)) || !SetupFiles.WaitReadable(SetupPaths.AppExe(root)))
            return "安装后主程序不可读";
        if (!Directory.Exists(SetupPaths.AppDir(root)))
            return "安装后 app 目录不存在";

        var arp = Path.Combine(root, "uninstall.json");
        if (!File.Exists(arp)) return "卸载登记未写入";
        var text = File.ReadAllText(arp);
        // 卸载入口已合并进主程序：UninstallString 必须指向 app\MiHome.exe --uninstall
        if (!text.Contains(SetupProtocol.ExeName, StringComparison.Ordinal)
            || !text.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
            return "卸载登记的 UninstallString 未指向主程序";
        if (!text.Contains("InstallLocation", StringComparison.Ordinal)) return "卸载登记缺少 InstallLocation";
        if (!text.Contains(SetupFiles.Normalize(root).Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase)
            && !text.Contains(SetupFiles.Normalize(root), StringComparison.OrdinalIgnoreCase))
            return "卸载登记的 InstallLocation 与安装目录不一致";
        if (!File.Exists(Path.Combine(root, "menu", SetupProtocol.ProductName + ".lnk")))
            return "开始菜单快捷方式未创建";
        return null;
    }

    private static string? VerifyUninstalled(string root)
    {
        if (Directory.Exists(SetupPaths.AppDir(root))) return "程序目录未删除";
        foreach (var name in new[] { SetupProtocol.OldDirName, SetupProtocol.NewDirName, SetupProtocol.PreviousDirName })
            if (Directory.Exists(Path.Combine(root, name))) return $"残留目录：{name}";
        if (File.Exists(SetupPaths.RootUninstaller(root))) return "卸载入口未删除";
        if (File.Exists(Path.Combine(root, "uninstall.json"))) return "卸载登记未删除";
        return null;
    }

    private static void WriteSelfTestHelper(string root, SetupMode mode, SetupResult result)
    {
        try
        {
            var text = string.Join(Environment.NewLine,
                $"mode={(mode == SetupMode.Install ? "install" : "uninstall")}",
                "success=1",
                $"path={result.Root}",
                $"version={result.Version ?? string.Empty}",
                $"log={SetupLog.FilePath}") + Environment.NewLine;
            File.WriteAllText(Path.Combine(root, "helper.txt"), text);
        }
        catch (Exception ex)
        {
            SetupLog.Fail("self-test-helper", ex, path: root);
        }
    }

    private static void WriteSelfTestError(string root, string step, string message, string? log)
    {
        try
        {
            Directory.CreateDirectory(root);
            var text = string.Join(Environment.NewLine,
                $"step={step}",
                $"message={message}",
                $"log={log ?? SetupLog.FilePath}") + Environment.NewLine;
            File.WriteAllText(Path.Combine(root, "setup-error.txt"), text);
        }
        catch { }
    }
}

/// <summary>持有已打开的事件句柄（SignalShown 之后无需再触发，仅防句柄被回收）。</summary>
internal sealed class EventHandleHolder : IDisposable
{
    private readonly EventWaitHandle _handle;

    public EventHandleHolder(string name)
        => _handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);

    public void Set() => _handle.Set();
    public void Dispose() => _handle.Dispose();
}

/// <summary>无 Avalonia 时的最小错误框（入口失败、没有安装信息等）。</summary>
internal static class SetupMessageBox
{
    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void Error(string caption, string message)
        => MessageBoxW(IntPtr.Zero, message, caption, MB_OK | MB_ICONERROR);
}
