// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace MiHome.Services.Setup;

public enum SetupMode
{
    Install,
    Uninstall,
}

/// <summary>安装器解析后的运行参数（协议见 <see cref="SetupProtocol"/>）。</summary>
public sealed class SetupOptions
{
    public SetupMode Mode { get; init; } = SetupMode.Install;
    public string PackagePath { get; init; } = string.Empty;
    public string TempDir { get; init; } = string.Empty;
    public string? SelfTestRoot { get; init; }
    public string? ShownEvent { get; init; }
    public string? ReadyEvent { get; init; }
    public int LauncherPid { get; init; }

    /// <summary>更新时的无界面安装：不显示安装窗口，直接跑完事务后拉起新版本。</summary>
    public bool Silent { get; init; }

    /// <summary>界面预览：走假进度，绝不写文件/注册表/快捷方式。</summary>
    public bool Preview { get; init; }
    /// <summary>预览变体：install / update / reinstall / uninstall / error。</summary>
    public string PreviewVariant { get; init; } = "install";

    public bool IsSelfTest => !string.IsNullOrEmpty(SelfTestRoot);
}

internal sealed record SetupProgress(double Percent, string Step);

internal sealed record SetupPreview(
    string Root,
    string? InstalledVersion,
    string? PackageVersion,
    bool IsUpgrade);

internal sealed class SetupResult
{
    public bool Success { get; init; }
    public string Root { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? PreviousVersion { get; init; }
    public string? Error { get; init; }
    public string? Hint { get; init; }
    public string? Step { get; init; }
    public bool WasRunning { get; init; }
    public string LogPath => SetupLog.FilePath;
}

/// <summary>
/// 安装事务：校验 → 复制到 app.new → 停旧进程 → 目录轮转 → 登记集成 → 清理 app.previous。
/// 任一步失败按逆序回滚（含登记现场），并通过日志记录可定位的步骤。
/// </summary>
internal sealed class SetupService
{
    private readonly SetupOptions _options;
    private readonly SetupIntegration _integration;

    public SetupService(SetupOptions options)
    {
        _options = options;
        _integration = SetupIntegration.Create(options.SelfTestRoot);
    }

    /// <summary>安装界面里手动选定的目录；仅安装态使用，升级/卸载仍走登记位置。</summary>
    public string? TargetRootOverride { get; set; }

    public string Root
    {
        get
        {
            if (_options.SelfTestRoot is not null)
                return SetupFiles.Normalize(_options.SelfTestRoot);
            if (!string.IsNullOrWhiteSpace(TargetRootOverride))
                return SetupFiles.Normalize(TargetRootOverride);
            return _options.Preview
                ? SetupFiles.Normalize(SetupPaths.DefaultRoot)
                : SetupPaths.ResolveTargetRoot();
        }
    }

    public SetupPreview Preview()
    {
        if (_options.Preview) return BuildPreview();

        var root = _options.Mode == SetupMode.Uninstall
            ? (SetupPaths.FindInstalledRoot() ?? Root)
            : Root;

        var installed = SetupFiles.VersionOf(SetupPaths.AppExe(root));
        var package = SetupFiles.VersionOf(Path.Combine(_options.TempDir, SetupProtocol.ExeName));
        var upgrade = installed is not null && package is not null
                      && SetupFiles.CompareVersions(package, installed) > 0;
        return new SetupPreview(root, installed, package, upgrade);
    }

    /// <summary>预览用的假现场：不读注册表、不读安装目录，固定展示默认安装位置。</summary>
    private SetupPreview BuildPreview()
    {
        var root = Root;
        var current = SetupFiles.VersionOf(Environment.ProcessPath) ?? "1.0.0";
        if (_options.Mode == SetupMode.Uninstall)
            return new SetupPreview(root, current, null, false);

        return _options.PreviewVariant switch
        {
            "update" => new SetupPreview(root, "0.9.0", current, true),
            "reinstall" => new SetupPreview(root, current, current, false),
            _ => new SetupPreview(root, null, current, false),
        };
    }

    public Task<SetupResult> InstallAsync(bool desktopShortcut, IProgress<SetupProgress>? progress)
        => _options.Preview
            ? PreviewInstallAsync(progress)
            : Task.Run(() => Install(desktopShortcut, progress));

    public Task<SetupResult> UninstallAsync(bool removeData, IProgress<SetupProgress>? progress)
        => _options.Preview
            ? PreviewUninstallAsync(removeData, progress)
            : Task.Run(() => Uninstall(removeData, progress));

    // ────────────────────────────────── 界面预览（零副作用） ──────────────────────────────────

    private async Task<SetupResult> PreviewInstallAsync(IProgress<SetupProgress>? progress)
    {
        var root = Root;
        var current = SetupFiles.VersionOf(Environment.ProcessPath) ?? "1.0.0";
        var preview = Preview();

        SetupLog.Info("preview-install", "start", $"variant={_options.PreviewVariant} root={root}");
        Report(progress, 0.02, "正在检查安装环境…");
        await Task.Delay(350);
        Report(progress, 0.06, "正在备份登记信息…");
        await Task.Delay(250);
        Report(progress, 0.08, "正在复制程序文件…");
        foreach (var percent in new[] { 0.18, 0.29, 0.40, 0.50 })
        {
            await Task.Delay(260);
            Report(progress, percent, "正在复制程序文件…");
        }

        if (_options.PreviewVariant == "error")
        {
            await Task.Delay(450);
            var failure = new SetupFailureException("复制程序文件",
                "预览模式：模拟安装失败，系统未发生任何改动。",
                fixHint: "这是错误态预览；点击“重试”会再次模拟失败。");
            SetupLog.Fail(failure.Step, failure, path: root);
            return new SetupResult
            {
                Success = false,
                Root = root,
                Error = failure.Message,
                Hint = failure.FixHint,
                Step = failure.Step,
            };
        }

        Report(progress, 0.55, "正在关闭正在运行的程序…");
        await Task.Delay(300);
        Report(progress, 0.62, "正在切换版本目录…");
        await Task.Delay(300);
        Report(progress, 0.78, "正在登记卸载信息与快捷方式…");
        await Task.Delay(350);
        Report(progress, 0.94, "正在清理旧版本…");
        await Task.Delay(200);
        Report(progress, 1.0, "安装完成（预览）");
        SetupLog.Info("preview-install", "ok", root);

        return new SetupResult
        {
            Success = true,
            Root = root,
            Version = current,
            PreviousVersion = preview.InstalledVersion,
            WasRunning = false,
        };
    }

    private async Task<SetupResult> PreviewUninstallAsync(bool removeData, IProgress<SetupProgress>? progress)
    {
        var root = Root;
        SetupLog.Info("preview-uninstall", "start", $"removeData={removeData} root={root}");

        Report(progress, 0.15, "正在关闭正在运行的程序…");
        await Task.Delay(350);
        Report(progress, 0.35, "正在清理卸载登记与快捷方式…");
        await Task.Delay(350);
        Report(progress, 0.55, "正在删除程序文件…");
        await Task.Delay(450);
        if (removeData)
        {
            Report(progress, 0.8, "正在删除应用数据…");
            await Task.Delay(300);
        }
        Report(progress, 1.0, "卸载完成（预览）");
        SetupLog.Info("preview-uninstall", "ok", root);

        return new SetupResult { Success = true, Root = root };
    }

    // ────────────────────────────────── 安装 ──────────────────────────────────

    private SetupResult Install(bool desktopShortcut, IProgress<SetupProgress>? progress)
    {
        var root = Root;
        string? installedVersion = null;
        var wasRunning = false;
        FileStream? setupLock = null;
        SetupSnapshot? snapshot = null;

        var newMoved = false;
        var oldCreated = false;
        var previousCreated = false;

        try
        {
            Report(progress, 0.02, "正在检查安装环境…");
            SetupPaths.ValidateTargetRoot(root);
            installedVersion = SetupFiles.VersionOf(SetupPaths.AppExe(root));

            setupLock = AcquireLock(root);

            var appDir = SetupPaths.AppDir(root);
            var appNew = Path.Combine(root, SetupProtocol.NewDirName);
            var appOld = Path.Combine(root, SetupProtocol.OldDirName);
            var appPrevious = Path.Combine(root, SetupProtocol.PreviousDirName);
            if (Directory.Exists(appNew) || Directory.Exists(appPrevious))
                throw new SetupFailureException("检查安装环境",
                    "发现上次安装未完成的残留（app.new / app.previous）",
                    fixHint: "请手动删除后再重试");

            var source = SetupFiles.Check(_options.TempDir);
            if (!File.Exists(Path.Combine(source, SetupProtocol.ExeName)))
                throw new SetupFailureException("检查安装包", $"安装包不完整：缺少 {SetupProtocol.ExeName}");
            if (SetupFiles.IsUnder(source, appDir))
                throw new SetupFailureException("检查安装包", "安装临时目录不能位于程序目录内");

            Report(progress, 0.06, "正在备份登记信息…");
            snapshot = _integration.Capture();

            Report(progress, 0.08, "正在复制程序文件…");
            SetupFiles.CopyDirectory(source, appNew, p => Report(progress, 0.08 + p * 0.42, "正在复制程序文件…"));
            if (!File.Exists(Path.Combine(appNew, SetupProtocol.ExeName)))
                throw new SetupFailureException("复制程序文件", "复制后未找到主程序");

            Report(progress, 0.55, "正在关闭正在运行的程序…");
            wasRunning = SetupProcess.StopRunning(root);

            Report(progress, 0.62, "正在切换版本目录…");
            if (Directory.Exists(appOld))
            {
                SetupFiles.MoveDirectory(appOld, appPrevious);
                previousCreated = true;
            }
            if (Directory.Exists(appDir))
            {
                SetupFiles.MoveDirectory(appDir, appOld);
                oldCreated = true;
            }
            SetupFiles.MoveDirectory(appNew, appDir);
            newMoved = true;

            Report(progress, 0.78, "正在登记卸载信息与快捷方式…");
            var version = SetupFiles.VersionOf(SetupPaths.AppExe(root)) ?? "0.0.0";
            // 静默更新沿用现有桌面快捷方式状态：不能因为更新把用户建过的快捷方式删掉
            var keepDesktop = desktopShortcut
                              || (_options.Silent && File.Exists(_integration.DesktopLnkPath));
            var info = new SetupApplyInfo(root, version, "Feiyu", DesktopShortcut: keepDesktop);
            RunSta(() => _integration.Apply(info));

            Report(progress, 0.94, "正在清理旧版本…");
            try
            {
                if (Directory.Exists(appPrevious)) SetupFiles.DeleteDirectory(appPrevious);
                // 卸载入口已合并进主程序，清掉旧版留在安装根的独立卸载器
                TryDeleteFile(SetupPaths.RootUninstaller(root));
            }
            catch (Exception ex)
            {
                SetupLog.Fail("cleanup-previous", ex, path: appPrevious);
            }

            Report(progress, 1.0, "安装完成");
            SetupLog.Info("install-finish", "ok", root);
            return new SetupResult
            {
                Success = true,
                Root = root,
                Version = version,
                PreviousVersion = installedVersion,
                WasRunning = wasRunning,
            };
        }
        catch (Exception ex)
        {
            var failure = Normalize(ex, "安装");
            SetupLog.Fail(failure.Step, ex, path: root);

            try
            {
                if (snapshot is not null)
                    RunSta(() => _integration.Restore(snapshot));
            }
            catch (Exception rollbackEx)
            {
                SetupLog.Fail("rollback-integration", rollbackEx, path: root);
            }

            try
            {
                var appDir = SetupPaths.AppDir(root);
                var appNew = Path.Combine(root, SetupProtocol.NewDirName);
                var appOld = Path.Combine(root, SetupProtocol.OldDirName);
                var appPrevious = Path.Combine(root, SetupProtocol.PreviousDirName);

                if (newMoved && Directory.Exists(appDir))
                    SetupFiles.MoveDirectory(appDir, appNew);
                if (oldCreated && Directory.Exists(appOld))
                    SetupFiles.MoveDirectory(appOld, appDir);
                if (previousCreated && Directory.Exists(appPrevious))
                    SetupFiles.MoveDirectory(appPrevious, appOld);
                if (Directory.Exists(appNew))
                    SetupFiles.DeleteDirectory(appNew);
            }
            catch (Exception rollbackEx)
            {
                SetupLog.Fail("rollback-files", rollbackEx, path: root);
                failure = new SetupFailureException(failure.Step,
                    failure.Message + "（回滚未完成，需手动处理安装目录）", rollbackEx, failure.FixHint);
            }

            if (wasRunning)
            {
                try { SetupProcess.Launch(SetupPaths.AppExe(root)); }
                catch (Exception restartEx) { SetupLog.Fail("relaunch-old", restartEx, path: root); }
            }

            return new SetupResult
            {
                Success = false,
                Root = root,
                Version = installedVersion,
                Error = failure.Message,
                Hint = failure.FixHint,
                Step = failure.Step,
            };
        }
        finally
        {
            setupLock?.Dispose();
        }
    }

    // ────────────────────────────────── 卸载 ──────────────────────────────────

    private SetupResult Uninstall(bool removeData, IProgress<SetupProgress>? progress)
    {
        var root = _options.SelfTestRoot is not null
            ? SetupFiles.Normalize(_options.SelfTestRoot)
            : SetupPaths.FindInstalledRoot() ?? string.Empty;

        FileStream? setupLock = null;
        try
        {
            if (string.IsNullOrEmpty(root))
                throw new SetupFailureException("定位安装目录", "没有找到本程序的安装信息",
                    fixHint: "请删除程序目录后清理“设置 → 应用”中的残留条目");

            var appDir = SetupPaths.AppDir(root);
            if (SetupFiles.IsUnder(AppContext.BaseDirectory, appDir))
                throw new SetupFailureException("准备卸载", "不能从程序目录内部执行卸载",
                    fixHint: "请运行安装目录下的卸载程序");

            setupLock = AcquireLock(root);

            Report(progress, 0.15, "正在关闭正在运行的程序…");
            SetupProcess.StopRunning(root);

            Report(progress, 0.35, "正在清理卸载登记与快捷方式…");
            RunSta(() => _integration.Remove());

            Report(progress, 0.55, "正在删除程序文件…");
            foreach (var name in new[]
                     {
                         SetupProtocol.AppDirName, SetupProtocol.OldDirName,
                         SetupProtocol.NewDirName, SetupProtocol.PreviousDirName,
                     })
            {
                var dir = Path.Combine(root, name);
                try
                {
                    if (Directory.Exists(dir)) SetupFiles.DeleteDirectory(dir);
                }
                catch (Exception ex)
                {
                    SetupLog.Fail("uninstall-delete", ex, path: dir);
                }
            }

            TryDeleteFile(SetupPaths.RootUninstaller(root));

            if (removeData && _options.SelfTestRoot is null)
            {
                Report(progress, 0.8, "正在删除应用数据…");
                try
                {
                    if (Directory.Exists(SetupPaths.DataRoot))
                        SetupFiles.DeleteDirectory(SetupPaths.DataRoot);
                }
                catch (Exception ex)
                {
                    SetupLog.Fail("uninstall-data", ex, path: SetupPaths.DataRoot);
                }
            }

            // 先释放安装锁再删锁文件，否则句柄还开着，根目录永远删不掉
            setupLock?.Dispose();
            setupLock = null;
            TryDeleteFile(Path.Combine(root, SetupProtocol.LockFileName));

            // 目录里可能还有用户自己放的文件，只在空目录时删
            try
            {
                if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0)
                    Directory.Delete(root);
            }
            catch (Exception ex)
            {
                SetupLog.Fail("uninstall-root", ex, path: root);
            }

            Report(progress, 1.0, "卸载完成");
            SetupLog.Info("uninstall-finish", "ok", root);
            return new SetupResult { Success = true, Root = root };
        }
        catch (Exception ex)
        {
            var failure = Normalize(ex, "卸载");
            SetupLog.Fail(failure.Step, ex, path: root);
            return new SetupResult
            {
                Success = false,
                Root = root,
                Error = failure.Message,
                Hint = failure.FixHint,
                Step = failure.Step,
            };
        }
        finally
        {
            setupLock?.Dispose();
        }
    }

    // ────────────────────────────────── 工具 ──────────────────────────────────

    private static FileStream AcquireLock(string root)
    {
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, SetupProtocol.LockFileName);
        try
        {
            return new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new SetupFailureException("获取安装锁", "另一个安装或卸载程序正在运行，请稍后重试", ex);
        }
    }

    private static SetupFailureException Normalize(Exception ex, string defaultStep)
    {
        if (ex is SetupFailureException failure) return failure;
        if (ex is UnauthorizedAccessException)
            return new SetupFailureException(defaultStep, "没有权限操作安装目录", ex, "请检查该目录的权限或换一个位置");
        return new SetupFailureException(defaultStep, ex.Message, ex,
            "请查看诊断日志后重试；若文件被杀毒软件占用，请暂时放行本程序");
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Report(IProgress<SetupProgress>? progress, double percent, string step)
    {
        SetupLog.Info("progress", "ok", message: $"{percent:P0} {step}");
        progress?.Report(new SetupProgress(percent, step));
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { SetupLog.Fail("delete-file", ex, path: path); }
    }
}
