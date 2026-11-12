// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MiHome.Services;

/// <summary>统一加载应用窗口图标：多尺寸 ico + avares 路径，避免任务栏空白</summary>
public static class WindowIconHelper
{
    private static WindowIcon? _cached;

    public static WindowIcon AppIcon =>
        _cached ??= Load();

    private static WindowIcon Load()
    {
        using var stream = AssetLoader.Open(new Uri("avares://MiHome/Assets/logo.ico"));
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
        if (!window.ShowInTaskbar)
        {
            window.Icon = AppIcon;
            return;
        }

        window.ShowInTaskbar = false;
        window.Icon = AppIcon;

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
