// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using Avalonia.Media;
using MiHomeHub.Services.MacOS;

namespace MiHomeHub.Services;

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

    /// <summary>
    /// explorer 起停时广播给所有顶层窗口的「桌面就绪」消息（开机启动正是撞在这段时序里）。
    /// <para>
    /// 改成惰性取值：注册窗口消息是一次 user32 调用，macOS 上没有 user32 ——
    /// 放在静态字段初始化里会让整个 DesktopEmbed 类型在 macOS 上第一次被碰到就抛
    /// TypeInitializationException。所以只在 Windows 上真正去注册，其它平台返回 0（不会被匹配到）。
    /// </para>
    /// </summary>
    public static uint ShellReadyMessage => _shellReadyMessage ??= OperatingSystem.IsWindows()
        ? DesktopNative.RegisterWindowMessageW("TaskbarCreated")
        : 0u;

    private static uint? _shellReadyMessage;

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
        // macOS 没有 HWND / Progman 这套概念：贴桌面靠 NSWindow.setLevel(kCGDesktopWindowLevel)，
        // 由分区窗那边（DesktopGroupWindow.AttachDesktopMac）调用 MacOsWindowInterop 完成。
        // 这里直接返回 false，绝不碰 DesktopNative（那些是 user32 导出，macOS 上会 DllNotFound）。
        if (OperatingSystem.IsMacOS()) return false;

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
        // macOS：贴壁纸层不是「压 Z 序」，而是把窗口 level 设成 kCGDesktopWindowLevel（见 MacOsWindowInterop）
        if (OperatingSystem.IsMacOS()) return;

        if (hwnd == nint.Zero) return;
        using (AllowZOrderChange())
            DesktopNative.SetWindowPos(hwnd, DesktopNative.HWND_BOTTOM, 0, 0, 0, 0,
                DesktopNative.SWP_NOSIZE | DesktopNative.SWP_NOMOVE | DesktopNative.SWP_NOACTIVATE
                | DesktopNative.SWP_NOOWNERZORDER);
    }

    /// <summary>确保 WS_EX_NOACTIVATE 在位（Avalonia 合成时会重置 ExStyle）。返回是否真的改过。</summary>
    public static bool EnsureNoActivate(nint hwnd)
    {
        // macOS：等价物是 ShowActivated=false + NSWindow 子类覆写 canBecomeKeyWindow（MacOsWindowInterop.MakeNonActivating），
        // 分区窗创建时就已经设好，这里无事可做
        if (OperatingSystem.IsMacOS()) return false;

        if (hwnd == nint.Zero) return false;
        var ex = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE);
        var next = ex | DesktopNative.WS_EX_NOACTIVATE | DesktopNative.WS_EX_TOOLWINDOW;
        next &= ~DesktopNative.WS_EX_APPWINDOW;
        if (next == ex) return false;
        DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE, (nint)next);
        return true;
    }

    /// <summary>
    /// 置顶（标题栏图钉）：先摘掉 Progman 从属 —— owner 链会把窗口钉在桌面层，
    /// 带着 WS_EX_TOPMOST 也抬不到普通软件之上；摘掉后再进 topmost 带。
    /// NOACTIVATE / TOOLWINDOW 保留：图钉只改层级，不抢焦点、不进 Alt+Tab。
    /// </summary>
    public static void PinTop(nint hwnd)
    {
        // macOS：置顶 = level 抬到 kCGFloatingWindowLevel（MacOsWindowInterop.SetFloatingLevel）
        if (OperatingSystem.IsMacOS()) return;

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

    /// <summary>补回 WS_EX_TOPMOST（Avalonia 合成会重置 ExStyle，掉一次就被普通窗口盖住）。返回是否真的改过。</summary>
    public static bool EnsureTopmost(nint hwnd)
    {
        // macOS：level 是窗口自己的属性，不会被系统重置；这里保持为空实现
        if (OperatingSystem.IsMacOS()) return false;

        if (hwnd == nint.Zero || !DesktopNative.IsWindow(hwnd)) return false;
        var ex = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE);
        if ((ex & DesktopNative.WS_EX_TOPMOST) != 0) return false;
        DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE,
            (nint)(ex | DesktopNative.WS_EX_TOPMOST | DesktopNative.WS_EX_NOACTIVATE
                       | DesktopNative.WS_EX_TOOLWINDOW));
        using (AllowZOrderChange())
            DesktopNative.SetWindowPos(hwnd, DesktopNative.HWND_TOPMOST, 0, 0, 0, 0,
                DesktopNative.SWP_NOSIZE | DesktopNative.SWP_NOMOVE | DesktopNative.SWP_NOACTIVATE
                | DesktopNative.SWP_NOSENDCHANGING);
        return true;
    }

    public static void StripNoRedirection(nint hwnd)
    {
        // macOS 没有 NOREDIRECTIONBITMAP 这回事（Avalonia 走的是自己的渲染路径）
        if (OperatingSystem.IsMacOS()) return;

        if (hwnd == nint.Zero) return;
        var ex = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE);
        if ((ex & DesktopNative.WS_EX_NOREDIRECTIONBITMAP) == 0) return;
        DesktopNative.SetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE, (nint)(ex & ~DesktopNative.WS_EX_NOREDIRECTIONBITMAP));
    }

    /// <summary>DWM Cloak：窗口仍是「已显示」，但合成层不画，改样式期间不闪。</summary>
    public static void SetCloaked(nint hwnd, bool cloaked)
    {
        // macOS：没有 DWM Cloak；改外观时靠 Avalonia 自己重绘，不存在「已显示但没画」的中间态
        if (OperatingSystem.IsMacOS()) return;

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
        // macOS：圆角不用窗口区域（RGN）裁，而是 XAML 里直接写 CornerRadius —— 交给 Avalonia 合成器画，
        // 角上不会漏出壁纸，也没有 Windows 那种「圆角自带系统阴影」的副作用
        if (OperatingSystem.IsMacOS()) return;

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
        // macOS 没有系统阴影这套东西：卡片阴影由 XAML 的 BoxShadow 自己画，形状可控
        if (OperatingSystem.IsMacOS()) return;

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
        // macOS：系统没有窗口圆角/描边概念，SystemDecorations=None 已经够了
        if (OperatingSystem.IsMacOS()) return;

        if (hwnd == nint.Zero) return;
        var corner = DesktopNative.DWMWCP_DONOTROUND;
        DesktopNative.DwmSetWindowAttribute(hwnd, DesktopNative.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        SuppressSystemShadow(hwnd);
    }

    /// <summary>
    /// 拖动 / 拉伸期间是否改用「降载」路径（摘掉磨砂与投影、宿主窗口原地不动，松手再补回来）。
    ///
    /// 桌面组件每跟手一帧都要把玻璃宿主 SetWindowPos 一次，而宿主带 HostBackdropBrush：
    /// 每挪一次 DWM 就丢掉旧采样、重采背景并把高斯模糊 + 两层投影重跑一遍。Win11 的合成链路
    /// 吃得下这份开销，Win10 上同样的链路就成了肉眼可见的发涩 / 掉帧（同为拖拽，普通窗口不带
    /// 这层玻璃，所以「拖别的没事」）。这里只在 Win10（无 DWMWCP_ROUND 的那一代合成器）降载。
    ///
    /// 环境变量 <c>MIHOMEHUB_DRAG_LITE=1/0</c> 可强制开关：Win11 上复现、或在 Win10 上关掉验证对照。
    /// </summary>
    public static bool DragLite
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("MIHOMEHUB_DRAG_LITE");
            if (env is "1" or "true" or "on") return true;
            if (env is "0" or "false" or "off") return false;
            return !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        }
    }

    /// <summary>
    /// 让 DWM 收角。这是「窗口级 ACCENT 磨砂 + 圆角」唯一有效的组合：
    /// RGN 能裁住 Avalonia 的内容，却裁不住 DWM 铺满窗口矩形的模糊（实测 rgnComplexity=3 仍在，
    /// 四角依旧是方的），只有 DWM 的窗口形状会连磨砂一起裁。
    /// 系统阴影另行用 SuppressSystemShadow 关掉；Win10（无 DWMWCP_ROUND）返回 false 由调用方回退 RGN。
    /// </summary>
    public static bool RoundByDwm(nint hwnd)
    {
        // macOS：没有 DWM 圆角；圆角由 XAML CornerRadius 负责，这里如实返回「没做」
        if (OperatingSystem.IsMacOS()) return false;

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
        // macOS 没有 ACCENT blur-behind：磨砂改用 Avalonia 的 TransparencyLevelHint = AcrylicBlur
        // （macOS 后端映射成 NSVisualEffectView 的 Blur 模式），色调仍由 XAML 里的 Background 决定。
        // 这里返回 false，表示「这一层没做」，调用方不要再指望它出效果。
        if (OperatingSystem.IsMacOS()) return false;

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
        // macOS：磨砂是 Avalonia 的 TransparencyLevelHint，撤掉靠把 hint 改回 Transparent（分区窗那边做）
        if (OperatingSystem.IsMacOS()) return;

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
        // macOS：Avalonia 每次内容变化都会自己 present，没有「已显示但屏上一像素没有」这个毛病
        if (OperatingSystem.IsMacOS()) return;

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

    /// <summary>
    /// 把刚生成的这一帧推出去，**不动窗口尺寸**。
    /// <para>
    /// 与 <see cref="ForcePresent"/> 的区别：那个靠「高度 -1px 再还原」拿一帧真 WM_SIZE，
    /// 开机 settle 阶段用户看不见，用来救「已显示但屏上一像素没有」；但把它用在拖滑杆这种
    /// 连续变更上，窗口每次都会真缩 1px 再弹回，内容跟着重排 —— 肉眼就是整块分区在抖。
    /// 这里改成标记 frame 无效 + 等 DWM 合成，几何不动，画面不跳。
    /// </para>
    /// </summary>
    public static void NudgePresent(nint hwnd)
    {
        // macOS：同上，Avalonia 自己管合成调度
        if (OperatingSystem.IsMacOS()) return;

        if (hwnd == nint.Zero || !DesktopNative.IsWindow(hwnd)) return;
        DesktopNative.RedrawWindow(hwnd, nint.Zero, nint.Zero,
            DesktopNative.RDW_INVALIDATE | DesktopNative.RDW_FRAME
            | DesktopNative.RDW_ALLCHILDREN | DesktopNative.RDW_UPDATENOW);
        try { DesktopNative.DwmFlush(); } catch { }
    }

    /// <summary>虚拟桌面矩形（多屏按逻辑坐标拼接）。</summary>
    public static (int X, int Y, int Width, int Height) VirtualScreen()
    {
        // macOS：没有 GetSystemMetrics，用 MacOsDisplay.All() 的各屏 Bounds 求并集
        // （MacOsDisplay 按 CGDisplay 的全局坐标给 Bounds，和 Avalonia 的屏幕坐标系一致）
        if (OperatingSystem.IsMacOS())
        {
            var screens = MacOsDisplay.All();
            if (screens.Count == 0) return (0, 0, 1, 1);

            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;
            foreach (var screen in screens)
            {
                var b = screen.Bounds;
                minX = Math.Min(minX, b.X);
                minY = Math.Min(minY, b.Y);
                maxX = Math.Max(maxX, b.X + b.Width);
                maxY = Math.Max(maxY, b.Y + b.Height);
            }

            return (minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
        }

        return (DesktopNative.GetSystemMetrics(DesktopNative.SM_XVIRTUALSCREEN),
            DesktopNative.GetSystemMetrics(DesktopNative.SM_YVIRTUALSCREEN),
            Math.Max(1, DesktopNative.GetSystemMetrics(DesktopNative.SM_CXVIRTUALSCREEN)),
            Math.Max(1, DesktopNative.GetSystemMetrics(DesktopNative.SM_CYVIRTUALSCREEN)));
    }
}
