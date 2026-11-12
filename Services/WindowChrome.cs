// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace MiHome.Services;

/// <summary>
/// 独立顶层窗口的「无系统外框」窗口 chrome（自定义窗体路线 B：大圆角 + 全自绘轮廓，不做毛玻璃）。
///
/// 两条线必须同时守住，缺一条系统窗体就会在窗口状态变化后叠回自绘圆角上：
/// 1. DWM：DONOTROUND + NCRENDERING_POLICY=DISABLED + BORDER_COLOR=NONE（管「DWM 画不画」）；
/// 2. 样式位：清 CAPTION/BORDER/DLGFRAME 与 EX 的 DLGMODALFRAME/CLIENTEDGE（管「Win32 画不画」）。
/// Avalonia 在最小化 / 还原等状态变化后会重算窗口样式，把 frame 位算回来，所以调用方需要持续自愈
/// （见 <see cref="NeedsRepair"/> + Controls.CustomWindow 的看门狗）。
///
/// ⚠️ DWMNCRP_DISABLED 是 1，不是 0（0 = USEWINDOWSTYLE，等于没关）。
/// ⚠️ FRAMECHANGED 会重算非客户区并重置描边色，所以 BORDER_COLOR=NONE 必须最后补。
/// </summary>
internal static class WindowChrome
{
    /// <summary>Win32 负责画标题栏 / 细边框 / 粗边框的样式位（DWM 属性管不到它们）。</summary>
    public const uint SystemFrameStyleMask =
        DesktopNative.WS_CAPTION | DesktopNative.WS_BORDER | DesktopNative.WS_DLGFRAME;

    /// <summary>
    /// Win32 负责画各种窗口边缘的扩展样式位。
    /// ⚠️ 不含 WS_EX_WINDOWEDGE：它是 WS_THICKFRAME 的附属位，系统在非客户区重算时会自己补回来
    /// （实测无错误、写不进去），而它只在系统绘制非客户区时才有意义 —— 本路线已把 NCRENDERING 关掉，
    /// 留着它不会有任何可见边框。把它算进「需要修」只会让看门狗永远判脏、每 500ms 空转一次 FRAMECHANGED。
    /// </summary>
    public const uint SystemFrameExStyleMask =
        DesktopNative.WS_EX_DLGMODALFRAME | DesktopNative.WS_EX_CLIENTEDGE;

    /// <summary>是否还有需要压下去的「系统窗体」痕迹；全绿时返回 false，调用方可直接空转。</summary>
    public static bool NeedsRepair(nint hwnd, bool resizable)
    {
        if (hwnd == nint.Zero || !DesktopNative.IsWindow(hwnd)) return false;

        var style = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_STYLE);
        var ex = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE);

        if ((style & SystemFrameStyleMask) != 0) return true;
        if ((ex & SystemFrameExStyleMask) != 0) return true;

        // 缩放依赖 WS_THICKFRAME；它本身不画 frame，只是 SC_SIZE 的前置条件。
        var hasThickFrame = (style & DesktopNative.WS_THICKFRAME) != 0;
        if (resizable != hasThickFrame) return true;

        var ncRendering = 0;
        if (DesktopNative.DwmGetWindowAttribute(hwnd, DesktopNative.DWMWA_NCRENDERING_ENABLED,
                out ncRendering, sizeof(int)) >= 0 && ncRendering != 0)
            return true;

        return false;
    }

    /// <summary>
    /// 把窗口压回「无系统外框」。幂等：状态已经正确时只会重写 DWM 属性，不会触发 FRAMECHANGED。
    /// 必须与 <see cref="NeedsRepair"/> 搭配调用，后者为 false 时不要进这里，避免高频空转。
    /// </summary>
    public static void EnsureClean(nint hwnd, bool resizable)
    {
        if (hwnd == nint.Zero || !DesktopNative.IsWindow(hwnd)) return;

        var style = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_STYLE);
        var ex = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE);

        var nextStyle = style & ~SystemFrameStyleMask;
        if (resizable) nextStyle |= DesktopNative.WS_THICKFRAME;
        else nextStyle &= ~DesktopNative.WS_THICKFRAME;
        // WS_SYSMENU / MINIMIZEBOX / MAXIMIZEBOX 保留：它们不画 frame，只管 Alt+Space 系统菜单与任务栏行为。

        var styleChanged = nextStyle != style;
        if (styleChanged)
            DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_STYLE, (nint)nextStyle);

        var nextEx = ex & ~SystemFrameExStyleMask;
        var exChanged = nextEx != ex;
        if (exChanged)
            DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE, (nint)nextEx);

        // DWM 线：圆角交给我们画，非客户区不渲染，过渡动画关掉（否则最大化/还原会看到 DWM 快照缩放）。
        var corner = DesktopNative.DWMWCP_DONOTROUND;
        DesktopNative.DwmSetWindowAttribute(hwnd, DesktopNative.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var ncPolicy = DesktopNative.DWMNCRP_DISABLED;
        DesktopNative.DwmSetWindowAttribute(hwnd, DesktopNative.DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(int));
        var transitionsDisabled = 1;
        DesktopNative.DwmSetWindowAttribute(hwnd, DesktopNative.DWMWA_TRANSITIONS_FORCEDISABLED,
            ref transitionsDisabled, sizeof(int));

        // 改过样式位必须让系统重算非客户区；这一步会把描边色重置回系统默认，所以描边最后补。
        if (styleChanged || exChanged)
            DesktopNative.SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0,
                DesktopNative.SWP_NOMOVE | DesktopNative.SWP_NOSIZE | DesktopNative.SWP_NOZORDER
                | DesktopNative.SWP_NOOWNERZORDER | DesktopNative.SWP_NOACTIVATE
                | DesktopNative.SWP_FRAMECHANGED);

        var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DesktopNative.DwmSetWindowAttribute(hwnd, DesktopNative.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }
}
