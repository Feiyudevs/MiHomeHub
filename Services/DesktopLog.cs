// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;

namespace MiHome.Services;

/// <summary>
/// 贴桌面层的调试日志：只有设了 MIHOME_DESKTOP_DEBUG=1 才写盘。
/// 分区窗是 HWND_BOTTOM + NOACTIVATE 的特殊窗口，很多问题（点击没到、菜单被系统失活吞掉）
/// 在普通日志里看不到，只能在关键节点落一行。
/// </summary>
internal static class DesktopLog
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("MIHOME_DESKTOP_DEBUG") == "1";

    private static readonly string File = Path.Combine(AppPaths.Dir, "desktop-debug.log");

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            Directory.CreateDirectory(AppPaths.Dir);
            System.IO.File.AppendAllText(File, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能影响交互
        }
    }
}
