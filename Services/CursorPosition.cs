// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using MiHomeHub.Services.MacOS;

namespace MiHomeHub.Services;

/// <summary>
/// 屏幕全局光标位置（物理像素，左上角原点）。
/// Windows 走 user32，macOS 走 CoreGraphics 的事件源位置。
/// </summary>
internal static class CursorPosition
{
    /// <summary>取当前光标位置；取不到返回 false。</summary>
    public static bool TryGet(out PixelPoint point)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!DesktopNative.GetCursorPos(out var native))
            {
                point = default;
                return false;
            }
            point = new PixelPoint(native.X, native.Y);
            return true;
        }

        if (OperatingSystem.IsMacOS())
            return MacOsCursor.TryGet(out point);

        point = default;
        return false;
    }
}
