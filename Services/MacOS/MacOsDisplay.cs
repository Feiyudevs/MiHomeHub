// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using MiHomeHub.Services;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// macOS 显示器枚举（CoreGraphics）。
/// 对应 Windows 侧的 EnumDisplayMonitors：拿边界、是否主屏、缩放，
/// 给 MonitorLayout 算「家屏签名」和落位用。
/// </summary>
internal static class MacOsDisplay
{
    /// <summary>一次最多枚举多少块屏（实际能接的屏远少于这个数）。</summary>
    private const uint MaxDisplays = 32;

    /// <summary>上次记下的枚举结果，用来判断「屏配置变了」才写日志（All() 调用很频繁）。</summary>
    private static string _lastLayout = "";

    /// <summary>当前接在系统上的显示器；枚举不到时返回空表，调用方按「没有屏信息」处理。</summary>
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        if (!OperatingSystem.IsMacOS()) return list;
        try
        {
            var displays = new uint[MaxDisplays];

            // 在线列表（所有连着的屏，含当前没窗口的副屏）优先；
            // 活动列表只给「有窗口或主屏」的屏，多屏机器上常只剩主屏 → 落位会算错。
            uint count;
            var rc = CGGetOnlineDisplayList(MaxDisplays, displays, out count);
            if (rc != 0 || count == 0)
            {
                displays = new uint[MaxDisplays];
                rc = CGGetActiveDisplayList(MaxDisplays, displays, out count);
                if (rc == 0 && count > 0)
                    DesktopLog.Write($"[display] online list unavailable, fell back to active count={count}");
            }

            // 还是一块都没有（进程刚起、还没接上窗口服务器）→ 至少把主屏带上。
            if (count == 0)
            {
                var main = CGMainDisplayID();
                if (main != 0)
                {
                    displays[0] = main;
                    count = 1;
                    DesktopLog.Write("[display] fallback to CGMainDisplayID");
                }
            }

            for (var i = 0; i < count; i++)
            {
                var display = displays[i];
                try
                {
                    var bounds = CGDisplayBounds(display);
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                    {
                        DesktopLog.Write($"[display] id={display} skipped, bounds=({bounds.X:0},{bounds.Y:0} {bounds.Width:0}x{bounds.Height:0})");
                        continue;
                    }

                    // 坐标单位：macOS 上一律用「逻辑点」，不是物理像素。
                    // Avalonia 的 macOS 后端（Screen.Bounds / WorkingArea / Window.Position）
                    // 全都是逻辑点，Screen.Scaling 恒为 1；MonitorLayout 的落位算式要跟它对齐，
                    // 否则 Retina 屏上卡片会差一个缩放倍数（实测：Position 设 1400 → 窗口落在 1400 点）。
                    // Windows 侧是物理像素，两边单位不同但各自内部自洽。
                    var rect = new PixelRect(
                        (int)Math.Round(bounds.X),
                        (int)Math.Round(bounds.Y),
                        Math.Max(1, (int)Math.Round(bounds.Width)),
                        Math.Max(1, (int)Math.Round(bounds.Height)));

                    list.Add(new MonitorInfo(
                        DeviceNameOf(display, i),
                        rect,
                        rect, // 工作区：macOS 没有任务栏那套工作区概念，整个显示区域就是工作区（近似）
                        CGDisplayIsMain(display),
                        1)); // 缩放：1 点 = 1 DIP，与 Avalonia 的 Screen.Scaling 一致
                }
                catch (Exception ex)
                {
                    DesktopLog.Write($"[display] id={display} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // 屏配置变了才写一次日志（All() 在拖动/落位时被反复调用）
            var layout = string.Join("; ", list.Select(m => $"{m.DeviceName}@{m.Bounds}"));
            if (layout != _lastLayout)
            {
                _lastLayout = layout;
                DesktopLog.Write($"[display] {list.Count} screen(s): {layout}");
            }
        }
        catch
        {
            // 互操作失败只影响这次的落位判断，窗口仍按保存的坐标显示
        }
        return list;
    }

    /// <summary>
    /// 显示器标识：用公开的单位号/厂商/型号/序列号拼一个稳定串（同一块屏插拔前后不变）。
    /// 注：CGDisplayCreateUUIDFromDisplayID 是私有符号，公开头文件里没有，运行时找不到入口点，
    /// 所以这里不用它，改用公开的 CGDisplayUnitNumber 等。
    /// </summary>
    private static string DeviceNameOf(uint display, int index)
    {
        try
        {
            var unit = CGDisplayUnitNumber(display);
            var serial = CGDisplaySerialNumber(display);
            var vendor = CGDisplayVendorNumber(display);
            var model = CGDisplayModelNumber(display);
            if (unit == 0 && serial == 0 && vendor == 0 && model == 0) return $"Display{index + 1}";
            return $"d{unit}-v{vendor}-m{model}-s{serial}";
        }
        catch
        {
            // 拿不到名字就用序号，不影响落位
        }
        return $"Display{index + 1}";
    }

    /// <summary>CoreGraphics 的矩形（4 个 double）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, uint[] activeDisplays, out uint displayCount);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGGetOnlineDisplayList(uint maxDisplays, uint[] activeDisplays, out uint displayCount);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern uint CGMainDisplayID();

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern CGRect CGDisplayBounds(uint display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CGDisplayIsMain(uint display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern uint CGDisplayUnitNumber(uint display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern uint CGDisplaySerialNumber(uint display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern uint CGDisplayVendorNumber(uint display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern uint CGDisplayModelNumber(uint display);

}
