using System;
using System.Diagnostics;
using Avalonia.Media;

namespace MiHome.Services;

/// <summary>
/// 桌面层级贴合（对齐 NoFences / Zonex 的做法）：
/// 1. WS_EX_TOOLWINDOW：不进 Alt+Tab / 任务栏
/// 2. WS_EX_NOACTIVATE：分区是桌面层，点击绝不激活 —— 否则激活会把整个 Progman 窗口集抬到普通应用之上
/// 3. GWL_HWNDPARENT → Progman：贴住桌面，Win+D / 显示桌面时一起让开
/// 4. HWND_BOTTOM：永远压在普通窗口之下，桌面图标之上
/// 5. 圆角靠 SetWindowRgn（DWM/合成层裁不到，RGN 是唯一稳的路径）
/// </summary>
internal static class DesktopEmbed
{
    // 分区窗自身 WndProc 会把所有 WM_WINDOWPOSCHANGING 的 Z 序改动压掉，
    // 只有本库主动压底（Attach/PinBottom）期间放行 —— SetWindowPos 是同步调用，WndProc 同线程，计数足够。
    [ThreadStatic] private static int _zOrderAllowDepth;

    /// <summary>explorer 起停时广播给所有顶层窗口的「桌面就绪」消息（开机启动正是撞在这段时序里）。</summary>
    public static readonly uint ShellReadyMessage = DesktopNative.RegisterWindowMessageW("TaskbarCreated");

    public static bool ZOrderChangeAllowed => _zOrderAllowDepth > 0;

    private static IDisposable AllowZOrderChange() => new ZOrderScope();

    private sealed class ZOrderScope : IDisposable
    {
        public ZOrderScope() => _zOrderAllowDepth++;
        public void Dispose() => _zOrderAllowDepth--;
    }

    /// <summary>把窗口贴到桌面层；返回是否成功拿到 Progman。</summary>
    public static bool Attach(nint hwnd)
    {
        if (hwnd == nint.Zero || !DesktopNative.IsWindow(hwnd)) return false;
        try
        {
            var ex = (nint)(uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE);
            var exStyle = (uint)ex | DesktopNative.WS_EX_TOOLWINDOW | DesktopNative.WS_EX_NOACTIVATE;
            exStyle &= ~(DesktopNative.WS_EX_TOPMOST
                         | DesktopNative.WS_EX_APPWINDOW
                         | DesktopNative.WS_EX_DLGMODALFRAME
                         | DesktopNative.WS_EX_NOREDIRECTIONBITMAP);
            DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE, (nint)exStyle);

            var st = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_STYLE);
            st &= ~(DesktopNative.WS_CAPTION | DesktopNative.WS_THICKFRAME | DesktopNative.WS_SYSMENU
                    | DesktopNative.WS_MINIMIZEBOX | DesktopNative.WS_MAXIMIZEBOX
                    | DesktopNative.WS_BORDER | DesktopNative.WS_DLGFRAME);
            DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_STYLE, (nint)st);

            var progman = DesktopNative.FindWindowW("Progman", null);
            if (progman != nint.Zero)
                DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_HWNDPARENT, progman);

            using (AllowZOrderChange())
                DesktopNative.SetWindowPos(hwnd, DesktopNative.HWND_BOTTOM, 0, 0, 0, 0,
                    DesktopNative.SWP_NOSIZE | DesktopNative.SWP_NOMOVE | DesktopNative.SWP_NOACTIVATE
                    | DesktopNative.SWP_SHOWWINDOW | DesktopNative.SWP_NOSENDCHANGING
                    | DesktopNative.SWP_NOOWNERZORDER);

            // 圆角由 RGN 承担：先把 Avalonia 合成会反复加回的 NOREDIRECTIONBITMAP 清掉
            StripNoRedirection(hwnd);
            return progman != nint.Zero;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[desktop] Attach 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>压回桌面底（不改尺寸/位置/激活态）。</summary>
    public static void PinBottom(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        using (AllowZOrderChange())
            DesktopNative.SetWindowPos(hwnd, DesktopNative.HWND_BOTTOM, 0, 0, 0, 0,
                DesktopNative.SWP_NOSIZE | DesktopNative.SWP_NOMOVE | DesktopNative.SWP_NOACTIVATE
                | DesktopNative.SWP_NOOWNERZORDER);
    }

    /// <summary>确保 WS_EX_NOACTIVATE 在位（Avalonia 合成时会重置 ExStyle）。</summary>
    public static void EnsureNoActivate(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        var ex = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE);
        var next = ex | DesktopNative.WS_EX_NOACTIVATE | DesktopNative.WS_EX_TOOLWINDOW;
        next &= ~DesktopNative.WS_EX_APPWINDOW;
        if (next != ex)
            DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE, (nint)next);
    }

    /// <summary>
    /// 置顶（标题栏图钉）：先摘掉 Progman 从属 —— owner 链会把窗口钉在桌面层，
    /// 带着 WS_EX_TOPMOST 也抬不到普通软件之上；摘掉后再进 topmost 带。
    /// NOACTIVATE / TOOLWINDOW 保留：图钉只改层级，不抢焦点、不进 Alt+Tab。
    /// </summary>
    public static void PinTop(nint hwnd)
    {
        if (hwnd == nint.Zero || !DesktopNative.IsWindow(hwnd)) return;

        DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_HWNDPARENT, nint.Zero);

        var ex = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE);
        DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE,
            (nint)(ex | DesktopNative.WS_EX_TOPMOST | DesktopNative.WS_EX_NOACTIVATE
                       | DesktopNative.WS_EX_TOOLWINDOW));

        // 改过 owner 必须再走一次 SetWindowPos，层级链才会真正重挂
        using (AllowZOrderChange())
            DesktopNative.SetWindowPos(hwnd, DesktopNative.HWND_TOPMOST, 0, 0, 0, 0,
                DesktopNative.SWP_NOSIZE | DesktopNative.SWP_NOMOVE | DesktopNative.SWP_NOACTIVATE
                | DesktopNative.SWP_FRAMECHANGED | DesktopNative.SWP_NOSENDCHANGING);
    }

    /// <summary>补回 WS_EX_TOPMOST（Avalonia 合成会重置 ExStyle，掉一次就被普通窗口盖住）。</summary>
    public static void EnsureTopmost(nint hwnd)
    {
        if (hwnd == nint.Zero || !DesktopNative.IsWindow(hwnd)) return;
        var ex = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE);
        if ((ex & DesktopNative.WS_EX_TOPMOST) != 0) return;
        DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE,
            (nint)(ex | DesktopNative.WS_EX_TOPMOST | DesktopNative.WS_EX_NOACTIVATE
                       | DesktopNative.WS_EX_TOOLWINDOW));
        using (AllowZOrderChange())
            DesktopNative.SetWindowPos(hwnd, DesktopNative.HWND_TOPMOST, 0, 0, 0, 0,
                DesktopNative.SWP_NOSIZE | DesktopNative.SWP_NOMOVE | DesktopNative.SWP_NOACTIVATE
                | DesktopNative.SWP_NOSENDCHANGING);
    }

    public static void StripNoRedirection(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        var ex = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE);
        if ((ex & DesktopNative.WS_EX_NOREDIRECTIONBITMAP) == 0) return;
        DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE, (nint)(ex & ~DesktopNative.WS_EX_NOREDIRECTIONBITMAP));
    }

    /// <summary>DWM Cloak：窗口仍是「已显示」，但合成层不画，改样式期间不闪。</summary>
    public static void SetCloaked(nint hwnd, bool cloaked)
    {
        if (hwnd == nint.Zero) return;
        var v = cloaked ? 1 : 0;
        try { DesktopNative.DwmSetWindowAttribute(hwnd, DesktopNative.DWMWA_CLOAK, ref v, sizeof(int)); }
        catch { }
    }

    /// <summary>
    /// 圆角裁切：radiusDip ≤ 0 表示恢复整窗矩形。
    /// 这是分区窗的主路径 —— RGN 能连 ACCENT 磨砂一起裁掉（角部直接露壁纸），
    /// 而 DWM 圆角（ROUND + thickframe）虽然也裁得住，但必然带一层约 20px 的系统阴影，桌面小部件上很显脏。
    /// </summary>
    public static void RoundCorners(nint hwnd, int radiusDip)
    {
        if (hwnd == nint.Zero || !DesktopNative.IsWindow(hwnd)) return;
        StripNoRedirection(hwnd);

        if (radiusDip <= 0)
        {
            DesktopNative.SetWindowRgn(hwnd, nint.Zero, false);
            return;
        }

        if (!DesktopNative.GetWindowRect(hwnd, out var r)) return;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;

        // CornerRadius 是 DIP，Region 要物理像素
        double scale = 1.0;
        try
        {
            var dpi = DesktopNative.GetDpiForWindow(hwnd);
            if (dpi >= 96) scale = dpi / 96.0;
        }
        catch { }

        var rad = Math.Max(2, (int)Math.Round(radiusDip * scale));
        rad = Math.Min(rad, Math.Min(w, h) / 2);

        var rgn = DesktopNative.CreateRoundRectRgn(0, 0, w + 1, h + 1, rad * 2, rad * 2);
        if (rgn == nint.Zero) return;
        // 成功后 Region 所有权归系统，不能再 DeleteObject
        if (DesktopNative.SetWindowRgn(hwnd, rgn, true) == 0)
            DesktopNative.DeleteObject(rgn);
    }

    /// <summary>关闭 Win11 的系统阴影 / 描边（ROUND 状态渲染过的阴影会粘滞，这里先把阴影侧全部关死）。</summary>
    public static void SuppressSystemShadow(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        var nc = DesktopNative.DWMNCRP_DISABLED;
        DesktopNative.DwmSetWindowAttribute(hwnd, DesktopNative.DWMWA_NCRENDERING_POLICY, ref nc, sizeof(int));
        var none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DesktopNative.DwmSetWindowAttribute(hwnd, DesktopNative.DWMWA_BORDER_COLOR, ref none, sizeof(int));
        var margins = default(DesktopNative.MARGINS);
        DesktopNative.DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>关闭 Win11 的系统圆角/描边（会跟 RGN 打架，露出一圈亮边）。</summary>
    public static void KillSystemFrame(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        var corner = DesktopNative.DWMWCP_DONOTROUND;
        DesktopNative.DwmSetWindowAttribute(hwnd, DesktopNative.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        SuppressSystemShadow(hwnd);
    }

    /// <summary>
    /// 让 DWM 收角。这是「窗口级 ACCENT 磨砂 + 圆角」唯一有效的组合：
    /// RGN 能裁住 Avalonia 的内容，却裁不住 DWM 铺满窗口矩形的模糊（实测 rgnComplexity=3 仍在，
    /// 四角依旧是方的），只有 DWM 的窗口形状会连磨砂一起裁。
    /// 系统阴影另行用 SuppressSystemShadow 关掉；Win10（无 DWMWCP_ROUND）返回 false 由调用方回退 RGN。
    /// </summary>
    public static bool RoundByDwm(nint hwnd)
    {
        if (hwnd == nint.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        var corner = DesktopNative.DWMWCP_ROUND;
        return DesktopNative.DwmSetWindowAttribute(hwnd, DesktopNative.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref corner, sizeof(int)) == 0;
    }

    /// <summary>
    /// 窗口级亚克力磨砂（ACCENT blur-behind）。与 Avalonia 合成模式无关，
    /// 是分层透明窗上唯一可靠的「毛玻璃」路径。
    /// </summary>
    public static bool ApplyAcrylic(nint hwnd, Color tint, double tintOpacity)
    {
        if (hwnd == nint.Zero) return false;
        try
        {
            // GradientColor 是 AABBGGRR（与 0xAARRGGBB 相反）
            var a = (byte)Math.Clamp((int)Math.Round(255 * tintOpacity), 0, 255);
            var color = (uint)((a << 24) | (tint.B << 16) | (tint.G << 8) | tint.R);
            return ApplyAccent(hwnd, DesktopNative.ACCENT_ENABLE_ACRYLICBLURBEHIND, color)
                   || ApplyAccent(hwnd, DesktopNative.ACCENT_ENABLE_BLURBEHIND, color);
        }
        catch
        {
            return false;
        }
    }

    public static void ClearAcrylic(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        try { ApplyAccent(hwnd, 0, 0); } catch { }
    }

    private static bool ApplyAccent(nint hwnd, int state, uint gradient)
    {
        var accent = new DesktopNative.AccentPolicy
        {
            AccentState = state,
            AccentFlags = 0,
            GradientColor = unchecked((int)gradient)
        };

        var size = System.Runtime.InteropServices.Marshal.SizeOf<DesktopNative.AccentPolicy>();
        var data = new DesktopNative.WindowCompositionAttributeData
        {
            Attribute = DesktopNative.WCA_ACCENT_POLICY,
            SizeOfData = size,
            Data = System.Runtime.InteropServices.Marshal.AllocHGlobal(size)
        };
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(accent, data.Data, false);
            return DesktopNative.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(data.Data);
        }
    }

    /// <summary>
    /// 主动要一帧：贴桌面窗用 IsVisible 隐藏后重新显示，Avalonia 认为内容没变就不再 present，
    /// 于是 Win32 查什么都是「已显示」、屏上却一像素没有。1px 抖动拿一次真 WM_SIZE。
    /// </summary>
    public static void ForcePresent(nint hwnd)
    {
        if (hwnd == nint.Zero || !DesktopNative.IsWindow(hwnd)) return;
        DesktopNative.InvalidateRect(hwnd, nint.Zero, false);
        if (!DesktopNative.GetWindowRect(hwnd, out var r)) return;
        var w = Math.Max(1, r.Right - r.Left);
        var h = Math.Max(1, r.Bottom - r.Top);
        const uint flags = DesktopNative.SWP_NOMOVE | DesktopNative.SWP_NOZORDER
                           | DesktopNative.SWP_NOACTIVATE | DesktopNative.SWP_NOSENDCHANGING;
        DesktopNative.SetWindowPos(hwnd, nint.Zero, 0, 0, w, Math.Max(1, h - 1), flags);
        DesktopNative.SetWindowPos(hwnd, nint.Zero, 0, 0, w, h, flags);
    }

    /// <summary>虚拟桌面矩形（多屏按逻辑坐标拼接）。</summary>
    public static (int X, int Y, int Width, int Height) VirtualScreen()
        => (DesktopNative.GetSystemMetrics(DesktopNative.SM_XVIRTUALSCREEN),
            DesktopNative.GetSystemMetrics(DesktopNative.SM_YVIRTUALSCREEN),
            Math.Max(1, DesktopNative.GetSystemMetrics(DesktopNative.SM_CXVIRTUALSCREEN)),
            Math.Max(1, DesktopNative.GetSystemMetrics(DesktopNative.SM_CYVIRTUALSCREEN)));
}
