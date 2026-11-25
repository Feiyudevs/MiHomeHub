// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;

namespace MiHomeHub.Services.Setup;

/// <summary>
/// 更新用的无界面安装：与 <see cref="Views.SetupWindow"/> 走同一套 <see cref="SetupService"/> 事务，
/// 只是不建窗口、不注册 Avalonia。入口在 <c>Program.Main</c>，早于任何 UI 初始化。
/// 成功后拉起新版本退出；失败弹一个最小错误框（此时用户的应用已经关闭，没有别的反馈通道）。
/// </summary>
internal static class SilentSetup
{
    public static int Run(SetupOptions options)
    {
        SetupLog.Info("silent-setup", "start", $"temp={options.TempDir}");

        SetupResult result;
        try
        {
            var service = new SetupService(options);
            result = service.InstallAsync(desktopShortcut: false, telemetryOptIn: true, progress: null).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            SetupLog.Fail("silent-setup", ex);
            SetupMessageBox.Error("MiHomeHub 更新失败",
                "安装未完成：" + ex.Message + "\n\n诊断日志：" + SetupLog.FilePath);
            return 1;
        }

        if (!result.Success)
        {
            SetupLog.Fail("silent-setup",
                new SetupFailureException(result.Step ?? "silent-setup", result.Error ?? "未知错误"),
                path: result.Root, details: result.Hint);
            SetupMessageBox.Error("MiHomeHub 更新失败",
                $"{(result.Step is { } step ? step + "：" : string.Empty)}{result.Error}"
                + (result.Hint is { } hint ? "\n\n" + hint : string.Empty)
                + "\n\n诊断日志：" + SetupLog.FilePath);
            return 1;
        }

        try
        {
            SetupProcess.Launch(SetupPaths.AppExe(result.Root));
        }
        catch (Exception ex)
        {
            SetupLog.Fail("silent-launch", ex, path: result.Root);
            return 1;
        }

        SetupLog.Info("silent-setup", "ok", $"root={result.Root} version={result.Version}");
        return 0;
    }
}
