using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MiHome.Services;

namespace MiHome.Controls;

/// <summary>
/// 自定义窗体基类：无边框 + 大圆角 + 自绘描边 / 投影（文档路线 B，不含毛玻璃与系统投影）。
///
/// 分工：
/// 1. 本类负责「窗口级」——透明分层窗、DWM 退出非客户区、清 Win32 frame 样式位、状态变化后持续自愈，
///    以及把影子留白区的鼠标命中改成「圆角卡片边缘缩放 / 留白穿透」。
/// 2. 派生窗体负责「内容级」——三层 Border：
///    投影层（兄弟 Border，BoxShadow，IsHitTestVisible=False）、
///    外形层（Background + BorderThickness 描边 + CornerRadius，⚠️ 不要开 ClipToBounds：
///    圆角裁剪是 1 位掩膜不带抗锯齿，会把描边和圆角边切成硬台阶）、
///    裁剪层（CornerRadius 比外形层小一个描边宽度，ClipToBounds 只裁子内容，台阶落在同色背景上看不见）。
///    派生窗在 <see cref="OnSurfaceGeometryChanged"/> 里把三层的 Margin / CornerRadius 跟着
///    <see cref="EffectiveShadowMargin"/> / <see cref="EffectiveCornerRadius"/> 更新。
///
/// 最大化（含自行实现的「铺满工作区」）时留白与圆角必须一起归零，否则四角会露出桌面。
/// </summary>
public class CustomWindow : Window
{
    private nint _hwnd;
    private nint _oldWndProc;
    private nint _wndProcPtr;
    private DesktopNative.WndProcDelegate? _wndProcDelegate;   // ★ 必须字段持有，GC 后就是野指针
    private DispatcherTimer? _watchdog;
    private readonly List<IDisposable> _delayedRepairs = new();
    private bool _repairing;
    private bool _surfaceMaximized;
    private PixelRect? _lastNormalOuter;

    /// <summary>卡片圆角（DIP）。必须与派生窗 Border.CornerRadius 用同一个数。</summary>
    public double SurfaceCornerRadius { get; set; } = 20d;

    /// <summary>窗口内留给投影 / 描边的透明留白（DIP）。0 表示不留白（也等于不要自绘投影）。</summary>
    public double ShadowMargin { get; set; } = 34d;

    /// <summary>卡片边缘触发缩放的带宽（DIP），落在留白区里，不抢正文点击。</summary>
    public double ResizeBand { get; set; } = 8d;

    /// <summary>是否处于最大化（系统 WindowState.Maximized 或派生窗自己实现的铺满工作区）。</summary>
    public bool IsSurfaceMaximized => _surfaceMaximized || WindowState == WindowState.Maximized;

    /// <summary>当前生效的圆角：最大化归零。</summary>
    public double EffectiveCornerRadius => IsSurfaceMaximized ? 0d : SurfaceCornerRadius;

    /// <summary>当前生效的留白：最大化归零。</summary>
    public double EffectiveShadowMargin => IsSurfaceMaximized ? 0d : ShadowMargin;

    public CustomWindow()
    {
        // 圆角 / 投影全部自绘，系统只需要保持「透明画布」。
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        // 透明背景下 ClearType 的子像素会算错（文字出现红蓝彩边），统一走灰度抗锯齿。
        RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);

        // shell 激活路径也可能让系统把样式算回来，补一次。
        Activated += (_, _) => Dispatcher.UIThread.Post(RepairNow);
    }

    /// <summary>派生窗自行实现最大化时调用；与系统 WindowState 合并判断，重复调用是空操作。</summary>
    protected void SetSurfaceMaximized(bool maximized)
    {
        if (_surfaceMaximized == maximized) return;
        _surfaceMaximized = maximized;
        RefreshSurfaceGeometry();
        ScheduleRepair();
    }

    /// <summary>让派生窗重算内容级留白 / 圆角（构造完成、状态变化、最大化切换后调用）。</summary>
    protected void RefreshSurfaceGeometry() => OnSurfaceGeometryChanged();

    /// <summary>
    /// 立即把系统外框压干净。改窗口几何前先调一次：Avalonia 的 Width/Height 是客户区尺寸，
    /// 样式位一时没压干净的话会被自动加上 frame，外层矩形就会比工作区大一圈。
    /// </summary>
    protected void ApplyWindowChromeNow() => RepairNow();

    /// <summary>
    /// 按物理像素精确铺放窗口（自定义最大化 / 还原用）。无系统非客户区时客户区 == 外层矩形，
    /// 直接钉死外层才能保证「正好铺满工作区」，不受 Avalonia 客户区换算影响。
    /// </summary>
    protected void SetSurfaceBounds(PixelRect bounds)
    {
        if (_hwnd == nint.Zero) return;
        DesktopNative.SetWindowPos(_hwnd, nint.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            DesktopNative.SWP_NOZORDER | DesktopNative.SWP_NOOWNERZORDER | DesktopNative.SWP_NOACTIVATE);
    }

    /// <summary>派生窗在这里把 Border 的 Margin / CornerRadius 对齐到 Effective* 值。</summary>
    protected virtual void OnSurfaceGeometryChanged() { }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        AttachChrome();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
        {
            var previous = change.GetOldValue<WindowState>();
            RefreshSurfaceGeometry();
            ScheduleRepair();
            // 必须先 ScheduleRepair（它会清掉所有延时任务），再挂恢复钉框。
            if (previous == WindowState.Minimized && WindowState == WindowState.Normal)
                ScheduleRestorePin();
        }
        else if (change.Property == IsVisibleProperty && IsVisible)
        {
            Dispatcher.UIThread.Post(RepairNow);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachChrome();
        base.OnClosed(e);
    }

    private void AttachChrome()
    {
        if (_hwnd == nint.Zero)
            _hwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (_hwnd == nint.Zero) return;

        InstallWndProc();
        WindowChrome.EnsureClean(_hwnd, CanResize);
        StartWatchdog();
        RefreshSurfaceGeometry();
    }

    private void DetachChrome()
    {
        _watchdog?.Stop();
        _watchdog = null;

        foreach (var repair in _delayedRepairs) repair.Dispose();
        _delayedRepairs.Clear();

        // 先摘链再放委托引用（顺序反了窗口下次收消息就是野指针）。
        RestoreWndProc();
        _wndProcDelegate = null;
        _hwnd = nint.Zero;
    }

    /// <summary>
    /// 状态变化后多档兜底：0ms（下一轮 Dispatcher，Avalonia 重算样式正好在这之后）+ 60/300/900ms。
    /// 这里绝不 cloak —— 「藏一下窗口」本身就是一次可见闪烁。
    /// </summary>
    private void ScheduleRepair()
    {
        if (_hwnd == nint.Zero) return;

        Dispatcher.UIThread.Post(RepairNow, DispatcherPriority.Background);

        foreach (var repair in _delayedRepairs) repair.Dispose();
        _delayedRepairs.Clear();
        foreach (var delay in new[] { 60, 300, 900 })
            _delayedRepairs.Add(DispatcherTimer.RunOnce(RepairNow, TimeSpan.FromMilliseconds(delay)));
    }

    private void StartWatchdog()
    {
        _watchdog ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _watchdog.Tick -= OnWatchdogTick;
        _watchdog.Tick += OnWatchdogTick;
        _watchdog.Start();
    }

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        EnsureWndProc();   // Avalonia 可能在状态变化时重装 WndProc，重新接上
        RepairNow();
    }

    private void RepairNow()
    {
        if (_hwnd == nint.Zero || _repairing) return;
        if (!DesktopNative.IsWindow(_hwnd)) return;
        if (!WindowChrome.NeedsRepair(_hwnd, CanResize)) return; // 全绿 → 空操作

        _repairing = true;
        try { WindowChrome.EnsureClean(_hwnd, CanResize); }
        finally { _repairing = false; }
    }

    private void InstallWndProc()
    {
        if (_hwnd == nint.Zero || _wndProcDelegate != null) return;

        _wndProcDelegate = OnNativeMessage;
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _oldWndProc = DesktopNative.GetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC);
        DesktopNative.SetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC, _wndProcPtr);
    }

    /// <summary>Avalonia 若重装过 WndProc，原样接回去；避免链到自己无限递归。</summary>
    private void EnsureWndProc()
    {
        if (_hwnd == nint.Zero || _wndProcDelegate == null) return;
        var current = DesktopNative.GetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC);
        if (current == _wndProcPtr || current == nint.Zero) return;
        _oldWndProc = current;
        DesktopNative.SetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC, _wndProcPtr);
    }

    private void RestoreWndProc()
    {
        if (_hwnd != nint.Zero && _oldWndProc != nint.Zero && _wndProcDelegate != null)
        {
            var current = DesktopNative.GetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC);
            if (current == _wndProcPtr)
                DesktopNative.SetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC, _oldWndProc);
        }
        _oldWndProc = nint.Zero;
        _wndProcPtr = nint.Zero;
    }

    private nint OnNativeMessage(nint hWnd, uint uMsg, nint wParam, nint lParam)
    {
        // 从最小化恢复的瞬间，系统/Avalonia 提交的是「客户区 + 7px 缩放边」的放大矩形，
        // 放行后再去纠正就会看到窗口先弹大、再缩回（抖动一下）。
        // 窗口此刻还处在最小化态，直接在 WM_WINDOWPOSCHANGING 里把矩形改成记忆里的普通态矩形。
        // 首次显示同理：Avalonia 会把 Width/Height 按 WS_THICKFRAME 换算成外层（每次都多加 14），
        // 这里按声明尺寸纠回去，否则启动一次胖一圈。
        if (uMsg == DesktopNative.WM_WINDOWPOSCHANGING && !IsSurfaceMaximized)
        {
            var pos = Marshal.PtrToStructure<DesktopNative.WINDOWPOS>(lParam);
            if (DesktopNative.IsIconic(hWnd) && _lastNormalOuter is { } restore)
            {
                pos.x = restore.X;
                pos.y = restore.Y;
                pos.cx = restore.Width;
                pos.cy = restore.Height;
                pos.flags &= ~(DesktopNative.SWP_NOSIZE | DesktopNative.SWP_NOMOVE);
                Marshal.StructureToPtr(pos, lParam, false);
            }
            else if (_lastNormalOuter is { } known && pos.cx > 0 && pos.cy > 0)
            {
                // 「记忆尺寸 + 缩放边」就是 Avalonia 换算后的结果，改回记忆尺寸即可；
                // 这条不依赖窗口是否还在最小化态，托盘唤起那条路径（先置 Normal 再 Show）也走它。
                var pad = FramePad(hWnd);
                if (pos.cx == known.Width + pad.X && pos.cy == known.Height + pad.Y)
                {
                    pos.cx = known.Width;
                    pos.cy = known.Height;
                    pos.flags &= ~DesktopNative.SWP_NOSIZE;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
            }
            else if (_lastNormalOuter is null)
            {
                var wantedCx = Math.Round(Width * DpiScale(hWnd));
                var wantedCy = Math.Round(Height * DpiScale(hWnd));
                if (wantedCx > 0 && wantedCy > 0 && (pos.cx != wantedCx || pos.cy != wantedCy))
                {
                    pos.cx = (int)wantedCx;
                    pos.cy = (int)wantedCy;
                    pos.flags &= ~DesktopNative.SWP_NOSIZE;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
            }
        }

        if (uMsg == DesktopNative.WM_NCHITTEST)
        {
            if (HitTest(hWnd, lParam) is int hit)
                return new nint(hit);
        }

        var result = _oldWndProc != nint.Zero
            ? DesktopNative.CallWindowProcW(_oldWndProc, hWnd, uMsg, wParam, lParam)
            : DesktopNative.DefWindowProcW(hWnd, uMsg, wParam, lParam);

        // 修完再交回给 Avalonia / 系统：Avalonia 自己的 WM_SIZE 处理也会重算样式位，
        // 顺序反了等于我们清完它又加回来。这里放在消息处理之后，保证我们是「最后一步」。
        // SetWindowPos(FRAMECHANGED) 只修样式，修完下轮检查全绿，不会递归。
        if (uMsg == DesktopNative.WM_SIZE || uMsg == DesktopNative.WM_WINDOWPOSCHANGED)
        {
            if (!_repairing)
            {
                _repairing = true;
                try { WindowChrome.EnsureClean(hWnd, CanResize); }
                finally { _repairing = false; }
            }
            RememberNormalOuter(hWnd);
        }

        return result;
    }

    /// <summary>记下普通态（非最大化、非最小化）的外层矩形，供从最小化恢复时钉回去。</summary>
    private void RememberNormalOuter(nint hwnd)
    {
        if (_surfaceMaximized || WindowState != WindowState.Normal) return;
        if (!DesktopNative.GetWindowRect(hwnd, out var rect)) return;
        // 最小化瞬间可能先收到一个 -32000 的矩形，别把它当成普通态尺寸记下来。
        if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return;
        if (rect.Left <= -10000 || rect.Top <= -10000) return;
        _lastNormalOuter = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>
    /// 从最小化恢复时，Avalonia 按「记下的尺寸 + WS_THICKFRAME 的缩放边」重算外层矩形。
    /// 这个缩放边即使我们关掉非客户区绘制、清掉 CAPTION/BORDER，系统照样留 7px，
    /// 于是每恢复一次外层就胖 14px，反复最小化会一直变大。
    /// 这里用最小化前记下的外层矩形钉回去，尺寸保持不变。
    /// </summary>
    private void ScheduleRestorePin()
    {
        // 自定义最大化时最小化再恢复，交给派生窗自己的「铺满工作区」逻辑，别把窗口钉回普通尺寸。
        if (_hwnd == nint.Zero || IsSurfaceMaximized || _lastNormalOuter is not { } rect) return;

        Dispatcher.UIThread.Post(() => SetSurfaceBounds(rect), DispatcherPriority.Background);
        // 晚一档再钉一次：Avalonia 可能在自己的状态恢复流程里又改一次尺寸。
        _delayedRepairs.Add(DispatcherTimer.RunOnce(() => SetSurfaceBounds(rect), TimeSpan.FromMilliseconds(160)));
    }

    private static double DpiScale(nint hwnd)
    {
        var dpi = DesktopNative.GetDpiForWindow(hwnd);
        return dpi >= 96 ? dpi / 96.0 : 1.0;
    }

    /// <summary>
    /// 当前样式下「客户区 → 外层」要多出的像素。清掉 CAPTION/BORDER 后仍保留 WS_THICKFRAME（缩放依赖它），
    /// 系统照样按每边 7px 的缩放边换算，96 DPI 下合计 14。
    /// </summary>
    private static (int X, int Y) FramePad(nint hwnd)
    {
        var style = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_STYLE);
        var exStyle = (uint)DesktopNative.GetWindowLongPtr(hwnd, DesktopNative.GWL_EXSTYLE);
        var r = new DesktopNative.RECT { Left = 0, Top = 0, Right = 1, Bottom = 1 };
        if (!DesktopNative.AdjustWindowRectEx(ref r, style, false, exStyle)) return (0, 0);
        return (r.Right - r.Left - 1, r.Bottom - r.Top - 1);
    }

    /// <summary>
    /// 命中测试：卡片内 → 默认（HTCLIENT）；卡片边缘带宽内 → 缩放；影子留白其余区域 → 穿透（HTTRANSPARENT）。
    /// 最大化（留白为 0）时整窗都交给客户端，不做任何改写。
    /// </summary>
    private int? HitTest(nint hwnd, nint lParam)
    {
        var margin = EffectiveShadowMargin;
        if (margin <= 0d || ResizeBand <= 0d) return null;

        // 卡片是放在客户区里的，客户区相对外层还差一圈缩放边（WS_THICKFRAME 的 7px），
        // 所以命中区要从客户区原点算，不能直接用窗口外框。
        if (!DesktopNative.GetClientRect(hwnd, out var client)) return null;
        var origin = new DesktopNative.POINT();
        if (!DesktopNative.ClientToScreen(hwnd, ref origin)) return null;

        var scale = DpiScale(hwnd);

        var l = (long)lParam;
        var x = unchecked((short)(l & 0xFFFF));
        var y = unchecked((short)((l >> 16) & 0xFFFF));

        var m = margin * scale;
        var band = ResizeBand * scale;

        var left = origin.X + m;
        var top = origin.Y + m;
        var right = origin.X + client.Right - m;
        var bottom = origin.Y + client.Bottom - m;

        var nearLeft = x >= left - band && x <= left + band;
        var nearRight = x >= right - band && x <= right + band;
        var nearTop = y >= top - band && y <= top + band;
        var nearBottom = y >= bottom - band && y <= bottom + band;

        if (nearLeft && nearTop) return DesktopNative.HTTOPLEFT;
        if (nearRight && nearTop) return DesktopNative.HTTOPRIGHT;
        if (nearLeft && nearBottom) return DesktopNative.HTBOTTOMLEFT;
        if (nearRight && nearBottom) return DesktopNative.HTBOTTOMRIGHT;
        if (nearLeft) return DesktopNative.HTLEFT;
        if (nearRight) return DesktopNative.HTRIGHT;
        if (nearTop) return DesktopNative.HTTOP;
        if (nearBottom) return DesktopNative.HTBOTTOM;

        var insideCard = x > left + band && x < right - band && y > top + band && y < bottom - band;
        return insideCard ? null : DesktopNative.HTTRANSPARENT;
    }
}
