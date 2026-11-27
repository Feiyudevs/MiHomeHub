// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MiHomeHub.Services;

/// <summary>统一加载应用窗口图标：多尺寸 ico + avares 路径，避免任务栏空白</summary>
public static class WindowIconHelper
{
    private static WindowIcon? _cached;

    public static WindowIcon AppIcon =>
        _cached ??= Load();

    private static WindowIcon Load()
    {
        // macOS 的 Skia 解 .ico 不稳（多尺寸 ico 里容易挑错帧），改用同源的 PNG；
        // Windows 侧维持多尺寸 ico，任务栏缩放效果更好。
        var uri = OperatingSystem.IsMacOS()
            ? new Uri("avares://MiHomeHub/Assets/logo-256.png")
            : new Uri("avares://MiHomeHub/Assets/logo.ico");
        using var stream = AssetLoader.Open(uri);
        return new WindowIcon(stream);
    }

    /// <summary>
    /// 给窗口套上应用图标。调用前必须先把 ShowInTaskbar 设成最终值。
    ///
    /// 任务栏按钮在窗口句柄创建那一刻就取好了图标，而图标要等句柄建好才下发得到，
    /// 顺序差一步按钮就永久空白；换图标句柄、重发 WM_SETICON 都刷不动它，只能重建按钮。
    /// 所以先压住任务栏不建按钮，等窗口显示完再放行，此时按钮取到的就是应用图标。
    /// 延后是必须的：Opened 里同步放行仍会拿到空按钮。
    /// </summary>
    public static void Apply(Window window)
    {
        window.Icon = AppIcon;

        // macOS 没有任务栏，也不需要「延后放行」这套时序（那是 Windows 任务栏按钮的坑）。
        if (OperatingSystem.IsMacOS() || !window.ShowInTaskbar) return;

        window.ShowInTaskbar = false;

        var revealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        window.Opened += (_, _) =>
        {
            revealTimer.Tick += (_, _) =>
            {
                revealTimer.Stop();
                window.ShowInTaskbar = true;
            };
            revealTimer.Start();
        };
    }
}
