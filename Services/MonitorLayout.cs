// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;

namespace MiHome.Services;

/// <summary>
/// 多屏落位：给每块显示器算一个签名（设备名 + 边界），窗口与桌面分区的位置记录里连签名一起存。
/// 家屏（保存时所处的屏）在 → 回到原坐标；家屏被关掉 → 按它在家屏里的相对位置等比搬到还在用的屏，
/// 存着的坐标不动，等这块屏重新插上或下次开机回来时原样落回。
/// <para>
/// 走 Win32 枚举而不是 Avalonia 的 Screen：分区窗在 Show 之前、主窗收进托盘之后都要能查屏，
/// 那两种时序下窗口还没有可用的 Screens 上下文。
/// </para>
/// </summary>
internal static class MonitorLayout
{
    /// <summary>夹进工作区时离屏幕边缘的留白（物理像素）。</summary>
    private const int EdgePx = 8;

    /// <summary>「还算看得见」的下限：横向要够摸标题栏拖动，纵向要够露出标题栏。</summary>
    private const int MinVisibleWidth = 160;
    private const int MinVisibleHeight = 48;

    private static readonly DesktopNative.EnumMonitorsProc EnumProc = Collect;

    /// <summary>当前接在系统上的显示器；枚举不到时返回空表，调用方按「没有屏信息」处理。</summary>
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        var gate = GCHandle.Alloc(list);
        try
        {
            DesktopNative.EnumDisplayMonitors(nint.Zero, nint.Zero, EnumProc, GCHandle.ToIntPtr(gate));
        }
        catch
        {
            // 互操作失败只影响这次的落位判断，窗口仍按保存的坐标显示
        }
        finally
        {
            gate.Free();
        }
        return list;
    }

    private static bool Collect(nint hMonitor, nint hdc, nint lprcMonitor, nint lParam)
    {
        try
        {
            if (GCHandle.FromIntPtr(lParam).Target is not List<MonitorInfo> list) return true;
            var info = new DesktopNative.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<DesktopNative.MONITORINFOEX>(),
                DeviceName = ""
            };
            if (!DesktopNative.GetMonitorInfo(hMonitor, ref info)) return true;

            list.Add(new MonitorInfo(
                info.DeviceName ?? "",
                ToRect(info.Monitor),
                ToRect(info.Work),
                (info.Flags & DesktopNative.MONITORINFOF_PRIMARY) != 0,
                ScalingOf(hMonitor)));
        }
        catch
        {
            // 单块屏读不出来就跳过，不能让异常越过原生回调栈
        }
        return true;
    }

    private static PixelRect ToRect(DesktopNative.RECT r)
        => new(r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));

    private static double ScalingOf(nint hMonitor)
    {
        try
        {
            if (DesktopNative.GetDpiForMonitor(hMonitor, DesktopNative.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
                && dpiX >= 96)
                return dpiX / 96.0;
        }
        catch
        {
            // 老系统没有 shcore：按 100% 处理
        }
        return 1;
    }

    /// <summary>签名里的设备名；换插口后这个名字会变，靠边界还能认出同一块屏。</summary>
    public static string NameOf(string signature)
    {
        var i = signature.LastIndexOf('|');
        return i < 0 ? signature : signature[..i];
    }

    /// <summary>签名里存的家屏边界；没有签名（老数据）时拿不到，只能退回夹取。</summary>
    public static PixelRect? BoundsOf(string signature)
    {
        var i = signature.LastIndexOf('|');
        if (i < 0 || i + 1 >= signature.Length) return null;
        var parts = signature[(i + 1)..].Split(',');
        if (parts.Length != 4) return null;
        if (!int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)
            || !int.TryParse(parts[2], out var w) || !int.TryParse(parts[3], out var h)) return null;
        return w <= 0 || h <= 0 ? null : new PixelRect(x, y, w, h);
    }

    /// <summary>
    /// 按签名找回那块屏：设备名 + 边界全等 → 设备名唯一相同（改过屏的排列位置）→ 边界唯一相同（换过插口）。
    /// 都对不上说明这块屏暂时不在了，调用方改走临时借用。
    /// </summary>
    public static MonitorInfo? FindBySignature(string signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        var monitors = All();

        foreach (var m in monitors)
            if (m.Signature == signature) return m;

        var name = NameOf(signature);
        var sameName = Unique(monitors, m => m.DeviceName == name);
        if (sameName != null) return sameName;

        var bounds = BoundsOf(signature);
        return bounds is { } b ? Unique(monitors, m => m.Bounds == b) : null;
    }

    private static MonitorInfo? Unique(List<MonitorInfo> monitors, Func<MonitorInfo, bool> match)
    {
        MonitorInfo? found = null;
        var hits = 0;
        foreach (var m in monitors)
        {
            if (!match(m)) continue;
            found = m;
            hits++;
        }
        // 两块屏条件相同时认不出是哪块，宁可当作找不到
        return hits == 1 ? found : null;
    }

    /// <summary>临时借用哪块屏：主屏优先，没有主屏标记就取面积最大的那块。</summary>
    public static MonitorInfo? Fallback()
    {
        var monitors = All();
        MonitorInfo? best = null;
        foreach (var m in monitors)
        {
            if (m.IsPrimary) return m;
            if (best is null || Area(m.Bounds) > Area(best.Value.Bounds)) best = m;
        }
        return best;
    }

    private static long Area(PixelRect r) => (long)r.Width * r.Height;

    /// <summary>
    /// 宽 widthDip × 高 heightDip 的窗口左上角落在 saved 时实际待在哪块屏上。
    /// 先看窗口中心落在谁的边界里（贴着边缘探出去也算在这块屏上），都对不上再要求至少露出能拖的标题栏。
    /// </summary>
    public static MonitorInfo? Holding(PixelPoint saved, double widthDip, double heightDip)
    {
        var monitors = All();
        foreach (var m in monitors)
        {
            var size = Pixels(widthDip, heightDip, m);
            var center = new PixelPoint(saved.X + size.Width / 2, saved.Y + size.Height / 2);
            if (m.Bounds.Contains(center)) return m;
        }
        foreach (var m in monitors)
            if (EnoughOn(m.WorkArea, new PixelRect(saved, Pixels(widthDip, heightDip, m)))) return m;

        return null;
    }

    /// <summary>窗口当前所在屏的签名，作为它的「家」；查不到屏时返回空串。</summary>
    public static string SignatureAt(PixelPoint position, double widthDip, double heightDip)
        => Holding(position, widthDip, heightDip)?.Signature ?? "";

    /// <summary>
    /// 家屏在位时的落点：只要还和这块屏有交集就照用保存的坐标（贴着边缘探出的摆放要保住），
    /// 完全被挤出这块屏（改了分辨率或排列）才夹回它的工作区。
    /// </summary>
    public static PixelPoint PlaceOnHome(PixelPoint saved, double widthDip, double heightDip, MonitorInfo home)
    {
        var rect = new PixelRect(saved, Pixels(widthDip, heightDip, home));
        return home.Bounds.Intersects(rect) ? saved : MoveOnto(saved, widthDip, heightDip, null, home);
    }

    private static bool EnoughOn(PixelRect work, PixelRect rect)
        => Math.Min(rect.Right, work.Right) - Math.Max(rect.X, work.X) >= MinVisibleWidth
        && Math.Min(rect.Bottom, work.Bottom) - Math.Max(rect.Y, work.Y) >= MinVisibleHeight;

    /// <summary>DIP 尺寸按该屏缩放换算成物理像素（Position 是物理像素，Width/Height 是 DIP）。</summary>
    public static PixelSize Pixels(double widthDip, double heightDip, MonitorInfo m)
    {
        var scale = m.Scaling > 0 ? m.Scaling : 1;
        return new PixelSize(Math.Max(1, (int)Math.Round(widthDip * scale)),
                             Math.Max(1, (int)Math.Round(heightDip * scale)));
    }

    /// <summary>
    /// 把保存的坐标搬到目标屏的工作区内。给了家屏边界就按它在家屏里的相对位置等比平移
    /// （一块屏上的几个组件一起搬到备用屏时保持原有分布，不会全挤在同一边缘），没给就只做夹取。
    /// </summary>
    public static PixelPoint MoveOnto(PixelPoint saved, double widthDip, double heightDip,
        PixelRect? homeBounds, MonitorInfo target)
    {
        var size = Pixels(widthDip, heightDip, target);
        var work = target.WorkArea;

        var x = saved.X;
        var y = saved.Y;
        if (homeBounds is { } home)
        {
            var fx = Fraction(saved.X - home.X, home.Width - size.Width);
            var fy = Fraction(saved.Y - home.Y, home.Height - size.Height);
            if (fx >= 0) x = work.X + (int)Math.Round(fx * Math.Max(0, work.Width - size.Width));
            if (fy >= 0) y = work.Y + (int)Math.Round(fy * Math.Max(0, work.Height - size.Height));
        }

        return new PixelPoint(ClampAxis(x, work.X, work.Right, size.Width),
                              ClampAxis(y, work.Y, work.Bottom, size.Height));
    }

    /// <summary>在家屏可用空间里的占比；窗口比家屏还宽（没有等比余地）时返回 -1，表示这条轴只做夹取。</summary>
    private static double Fraction(int offset, int free) => free <= 0 ? -1 : (double)offset / free;

    private static int ClampAxis(int value, int start, int end, int size)
    {
        var low = start + EdgePx;
        return Math.Clamp(value, low, Math.Max(low, end - size - EdgePx));
    }
}

/// <summary>一块物理显示器：边界、工作区、是否主屏、有效缩放。</summary>
internal readonly struct MonitorInfo
{
    public MonitorInfo(string deviceName, PixelRect bounds, PixelRect workArea, bool isPrimary, double scaling)
    {
        DeviceName = deviceName;
        Bounds = bounds;
        WorkArea = workArea;
        IsPrimary = isPrimary;
        Scaling = scaling;
    }

    public string DeviceName { get; }
    public PixelRect Bounds { get; }
    public PixelRect WorkArea { get; }
    public bool IsPrimary { get; }
    public double Scaling { get; }

    /// <summary>身份签名：设备名认「哪块屏」，边界用来区分同型号的两块屏。</summary>
    public string Signature => $"{DeviceName}|{Bounds.X},{Bounds.Y},{Bounds.Width},{Bounds.Height}";
}
