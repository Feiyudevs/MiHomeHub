// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// macOS 全局光标位置（逻辑点，左上角原点，与 Avalonia 的 PixelPoint 同坐标系）。
///
/// Windows 版走 user32.GetCursorPos，给的是物理像素。
/// macOS 没有等价 API，但 CoreGraphics 的 CGEventGetLocation 给的正好就是「点」，
/// 而 Avalonia 的 macOS 后端（Window.Position / Screen.Bounds / ScreenFromPoint）也全用点，
/// 所以这里直接用点，不再按 backing scale 换算成物理像素——换算会让光标与窗口坐标差一个缩放倍数。
/// </summary>
internal static class MacOsCursor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreGraphics)]
    private static extern nint CGEventCreate(nint source);

    // CGEvent 没有独立的 Release 导出（新系统上 dlsym("CGEventRelease") 直接返回 NULL，
    // 用它 P/Invoke 会抛 EntryPointNotFoundException → TryGet 永远失败 → 拖边一直用兜底算法 → 抖动）。
    // CGEventRef 就是 CFTypeRef，用 CoreFoundation 的 CFRelease 释放即可。
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint handle);

    // CGEventGetLocation 是「返回结构体」，不是 out 参数：签名写错会让 16 字节的返回值走错 ABI。
    [DllImport(CoreGraphics)]
    private static extern CGPoint CGEventGetLocation(nint evt);

    /// <summary>取当前光标位置（逻辑点）；取不到返回 false。</summary>
    public static bool TryGet(out PixelPoint point)
    {
        point = default;
        try
        {
            var evt = CGEventCreate(IntPtr.Zero);
            if (evt == IntPtr.Zero) return false;
            try
            {
                var location = CGEventGetLocation(evt);
                point = new PixelPoint(
                    (int)Math.Round(location.X),
                    (int)Math.Round(location.Y));
                return true;
            }
            finally
            {
                CFRelease(evt);
            }
        }
        catch
        {
            return false;
        }
    }
}
