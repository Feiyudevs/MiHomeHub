// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using MiHomeHub.Services;
using MiHomeHub.Services.MacOS;
using MiHomeHub.ViewModels;
using MiHomeHub.Widgets;

namespace MiHomeHub.Views;

/// <summary>
/// 分区独立顶层小窗：贴桌面（owner=Progman）+ 压底 + NOACTIVATE。
/// 窗体外观完全复用数据卡那一套（<see cref="DesktopGlassFrame"/>：20px 大圆角、原生磨砂与投影，
/// 原生玻璃不可用时回退 8px + 窗口级亚克力）。
/// 位置/尺寸由 View 里的拖动与右缘拉伸改，落位后写回模型持久化。
/// </summary>
public partial class DesktopGroupWindow : Window, IDesktopWindow
{
    /// <summary>
    /// 一行的推算高度（DIP）：设备卡 MinHeight 74 + 卡片外边距 8×2。
    /// 只在宫格还没布局过（空分区加第一台）时兜底，正常情况用实测的卡片高度。
    /// </summary>
    private const double CardRowDip = 90;

    /// <summary>宫格之外的固定开销（DIP）：标题栏 38 + 卡片描边 2 + 内容底部留白 6。</summary>
    private const double ChromeDip = 46;

    private readonly DesktopZoneVm _vm;

    /// <summary>和数据卡完全共用的窗体外观：20px 大圆角 + 原生磨砂 + 投影。</summary>
    private readonly DesktopGlassFrame _frame;

    /// <summary>设备分区才有「加设备就加宽一列」那套推算，媒体分区走不到。</summary>
    private DesktopGroupVm? _group;

    private nint _hwnd;
    private DispatcherTimer? _guard;
    private int _guardTicks;
    private DispatcherTimer? _chromeTimer;
    private DispatcherTimer? _settleTimer;
    private int _settlePasses;

    // WndProc 子类：压掉一切非本意的 Z 序变化（Avalonia 写位置/尺寸的 SetWindowPos 不带 NOZORDER）
    private DesktopNative.WndProcDelegate? _wndProcDelegate;
    private nint _originalWndProc;
    private nint _wndProcPtr;

    // ===== 折叠 / 展开高度动画 =====
    // 折叠后只剩标题栏 + 卡片 1px 描边：44 + 2。
    private const double CollapsedChromeHeight = 46;
    // 高度动画时长与帧间隔：easeOutCubic，180ms 收放更顺滑，不会像瞬间跳变。
    private const int HeightAnimationMs = 180;
    private const int HeightAnimationTickMs = 15;

    private DispatcherTimer? _heightTimer;
    private double _heightFrom;
    private double _heightTo;
    private double _heightElapsed;
    private int _heightRequest;

    // 拖动 / 拉伸状态
    private bool _dragging;
    private bool _resizing;
    private PixelPoint _dragOrigin;
    private (int X, int Y) _dragCursor;
    private double _resizeStartWidth;
    private int _resizeStartX;
    private bool _resizeFromLeft;

    public DesktopGroupWindow(DesktopZoneVm vm)
    {
        InitializeComponent();
        // 和数据卡共用同一套窗体外观；先于尺寸赋值建好，尺寸回调里才能同步玻璃宿主
        _frame = new DesktopGlassFrame(this, Root.CardSurface, () => Hwnd, "desktop-zone", OnOwnerRestored);

        _vm = vm;
        _group = vm as DesktopGroupVm;
        DataContext = vm;
        Width = vm.Width;
        // 空调分区按内容高自适应（与媒体分区一致），不参与「加一列就加宽」那套推算
        Root.Host = this;

        vm.PropertyChanged += OnVmPropertyChanged;
        if (_group != null)
        {
            _group.Devices.CollectionChanged += OnDevicesChanged;
            _group.Scenes.CollectionChanged += OnDevicesChanged;
        }
        Closed += OnClosed;
    }

    public DesktopZoneVm ViewModel => _vm;

    public Window Window => this;

    /// <summary>正在拖动 / 拉伸：此时禁止悬停自动展开与移出自动收起，对齐 Zonex 的交互保护。</summary>
    public bool IsManipulating => _dragging || _resizing;

    /// <summary>窗口句柄（贴桌面层级操作时使用）。</summary>
    public nint Handle => Hwnd;

    private nint Hwnd => _hwnd != nint.Zero ? _hwnd : TryGetPlatformHandle()?.Handle ?? nint.Zero;

    /// <summary>先落到保存的位置再显示，避免在 (0,0) 闪一下。</summary>
    public override void Show()
    {
        ApplyModelPosition();
        base.Show();
    }

    /// <summary>当前只是家屏缺席时的临时落位（模型坐标没动，等那块屏回来还要落回原位）。</summary>
    public bool IsExiled { get; private set; }

    /// <summary>
    /// 家屏在 → 用模型里的坐标；家屏被关掉/拔掉 → 按它在家屏里的相对位置等比搬到还在用的屏。
    /// 模型坐标只在用户拖动、拉伸之后才改写，所以临时落点不会顶掉原位。
    /// </summary>
    private void ApplyModelPosition()
    {
        var model = _vm.Model;
        var saved = new PixelPoint((int)Math.Round(model.X), (int)Math.Round(model.Y));
        var height = HeightDip();

        // 老数据没记签名：坐标还落在某块屏上就认那块屏为家，都对不上才算借用
        var home = MonitorLayout.FindBySignature(model.ScreenSig)
                   ?? (model.ScreenSig.Length == 0 ? MonitorLayout.Holding(saved, Width, height) : null);

        if (home is { } liveHome)
        {
            IsExiled = false;
            Position = MonitorLayout.PlaceOnHome(saved, Width, height, liveHome);
            return;
        }

        var target = MonitorLayout.Fallback();
        if (target is null)
        {
            ClampIntoVirtualDesktop(saved);
            return;
        }

        IsExiled = true;
        Position = MonitorLayout.MoveOnto(saved, Width, height,
            MonitorLayout.BoundsOf(model.ScreenSig), target.Value);
        DesktopLog.Write($"[monitor] 分区「{model.Name}」借用 {model.ScreenSig} → {target.Value.Signature} pos={Position}");
    }

    /// <summary>一块屏都枚举不到时的兜底：按虚拟桌面夹住，别把分区丢在屏幕外。</summary>
    private void ClampIntoVirtualDesktop(PixelPoint saved)
    {
        var (vx, vy, vw, vh) = DesktopEmbed.VirtualScreen();
        var x = (int)Math.Clamp(saved.X, vx + 8, vx + Math.Max(8, vw - 80));
        var y = (int)Math.Clamp(saved.Y, vy + 8, vy + Math.Max(8, vh - 60));
        Position = new PixelPoint(x, y);
    }

    /// <summary>分区高度由内容撑开，布局跑完前拿不到：按折叠/展开的典型高度估，只用于夹边与等比搬移。</summary>
    private double HeightDip()
    {
        var scale = RenderScaling > 0 ? RenderScaling : 1;
        if (Bounds.Height > 1) return Bounds.Height / scale;
        return _vm.Collapsed ? 40 : 240;
    }

    /// <summary>屏幕变化后重新落位：家屏回来就回原位，家屏又没了就重新借用。</summary>
    public void ReapplyPlacement()
    {
        if (_dragging || _resizing) return;
        ApplyModelPosition();
    }

    /// <summary>眼下这块屏的签名，给新建的分区安家时用。</summary>
    public string CurrentScreenSignature() => MonitorLayout.SignatureAt(Position, Width, HeightDip());

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        AttachDesktop();

        // 高度由内容撑开，Show 之前只能估：布局跑完后按真实尺寸再夹一次，别露出屏幕外
        Dispatcher.UIThread.Post(ReapplyPlacement, DispatcherPriority.Loaded);

        if (_group != null)
            Dispatcher.UIThread.Post(
                () => FitIntoDesktop(_group.Devices.Count, _group.Scenes.Count,
                                     _group.Devices.Count, _group.Scenes.Count),
                DispatcherPriority.Loaded);
    }

    private void AttachDesktop()
    {
        // macOS 没有 HWND / Progman / DWM 这一套：贴桌面改成 NSWindow 的 level + collectionBehavior，
        // 整条 Windows 路径（WndProc 子类、cloak、RGN 圆角、玻璃宿主）一概不适用
        if (OperatingSystem.IsMacOS())
        {
            AttachDesktopMac();
            return;
        }

        _hwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (_hwnd == nint.Zero) return;

        // 调试口子：MIHOMEHUB_DESKTOP_DEBUG=1 时不贴桌面、改为置顶普通窗口，
        // 方便在其它窗口之上肉眼核对卡片排版/圆角/磨砂（正式运行时环境变量不存在，走正常贴桌面路径）。
        if (DebugMode)
        {
            Topmost = true;
            DesktopEmbed.KillSystemFrame(_hwnd);
            _frame.EnsureHost();
            ApplyChrome();
            return;
        }

        // 必须先装：之后每一次坐标/尺寸写入都不准动 Z 序
        InstallWndProc();

        // 贴桌面期间全程 cloak：改样式/打磨砂都会闪，用户看不到中间态。
        // 顺序和数据卡一致：先压底再摆玻璃宿主，解除 cloak 前把磨砂和首帧都准备好。
        DesktopEmbed.SetCloaked(_hwnd, true);
        try
        {
            DesktopEmbed.KillSystemFrame(_hwnd);
            DesktopEmbed.Attach(_hwnd);
            _frame.EnsureHost();
            ApplyChrome();

            DesktopEmbed.PinBottom(_hwnd);
            if (_frame.UsesNativeGlass) _frame.SeatBehind();
            DesktopEmbed.ForcePresent(_hwnd);
        }
        finally
        {
            DesktopEmbed.SetCloaked(_hwnd, false);
        }

        if (_frame.UsesNativeGlass)
        {
            _frame.PrimeForShow();
            if (!_frame.Sync())
            {
                // 刚解除 cloak 时 DWM 属性可能还没同步，等到下一帧再复查一次
                Dispatcher.UIThread.Post(() =>
                {
                    if (_frame.UsesNativeGlass && !_frame.Sync()) _frame.FallbackToAcrylic("attach-deferred");
                }, DispatcherPriority.Render);
            }
            else
            {
                _frame.RepinBehindOwner();
            }
        }

        // 上次退出前钉住的分区：开机直接回到置顶态
        if (_vm.IsTopmost)
        {
            Topmost = true;
            DesktopEmbed.PinTop(_hwnd);
            _frame.RepinBehindOwner();
        }

        StartGuard();
        StartMaterialSettle();
    }

    /// <summary>
    /// macOS 版贴桌面。Windows 那套「owner=Progman + 压底 + NOACTIVATE + DWM 圆角/玻璃宿主」
    /// 在 macOS 上没有对应物，逐项换成 AppKit 的等价手段：
    /// <list type="bullet">
    /// <item><b>不抢焦点</b>：ShowActivated=false + 窗口子类覆写 canBecomeKeyWindow
    /// （等价 Windows 的 WS_EX_NOACTIVATE）—— 点卡片不会把用户正在用的窗口顶掉。</item>
    /// <item><b>无边框</b>：SystemDecorations=None（XAML 里已设，这里只兜底）。</item>
    /// <item><b>磨砂</b>：自建的 NSVisualEffectView（BehindWindow + HUDWindow + Active），
    /// Avalonia 自带的 AcrylicBlur 在 macOS 上不生效（那层 effect view 恒 hidden=1）。</item>
    /// <item><b>贴桌面层</b>：NSWindow.setLevel(kCGDesktopIconWindowLevel + 1) ——
    /// 访达桌面窗铺满整块屏幕且层级更低，卡片停在壁纸层会被它整个盖住，点不动也拖不动。</item>
    /// <item><b>跟着所有桌面空间、不进 Cmd+Tab</b>：setCollectionBehavior(Stationary|CanJoinAllSpaces|IgnoresCycle)。</item>
    /// <item><b>标题栏图钉（置顶）</b>：改用 kCGFloatingWindowLevel 抬出桌面卡片层。</item>
    /// </list>
    /// 拿不到 NSWindow 指针时 MacOsWindowInterop 内部会安静跳过：卡片退化成普通浮窗，程序不崩。
    /// </summary>
    private void AttachDesktopMac()
    {
        // 不抢焦点
        ShowActivated = false;

        // 无边框（XAML 已设时不重复写，省一次属性变更）
        if (SystemDecorations != Avalonia.Controls.SystemDecorations.None)
            SystemDecorations = Avalonia.Controls.SystemDecorations.None;

        // 调试口子：macOS 上也允许「不贴桌面、置顶普通窗口」，方便肉眼核对排版
        if (DebugMode)
        {
            Topmost = true;
            ApplyChromeMac();
            return;
        }

        // 磨砂 / 主题
        ApplyChromeMac();

        // 贴桌面 + 桌面空间行为
        MacOsWindowInterop.SetCollectionBehavior(this);
        // 抬到桌面卡片层：访达桌面窗（kCGDesktopIconWindowLevel）铺满整块屏幕，
        // 停在壁纸层会被它整个盖住 → 卡片点不动、拖不动；
        // 与访达同层也不够 —— 主窗口一最小化，系统激活访达桌面，访达窗口就排到卡片前面。
        // 卡片层比访达高一层、比普通窗口低，问题彻底消失。
        MacOsWindowInterop.SetDesktopCardLevel(this);
        // 原生窗口背景改成透明 + 非不透明，否则自绘圆角四周会露出黑边。
        MacOsWindowInterop.MakeTransparent(this);
        // 毛玻璃层 + 圆角裁剪（对应 Windows 的 DWM 圆角 / 亚克力）。
        RefreshDesktopBlur();
        // 点卡片不抢焦点（对应 Windows 的 WS_EX_NOACTIVATE）：
        // ShowActivated=false 只管「显示时不要激活」，点击时照样会把 App 拉到前台。
        MacOsWindowInterop.MakeNonActivating(this);

        // 上次退出前钉住的分区：开机直接回到浮层
        if (_vm.IsTopmost)
            MacOsWindowInterop.SetFloatingLevel(this);
    }

    /// <summary>
    /// macOS 版外观：
    /// <list type="bullet">
    /// <item>磨砂 = 自建的 NSVisualEffectView（BehindWindow + HUDWindow + Active），
    /// 插在 contentView 最底层，卡片底色叠在模糊上 —— 和 Windows 版 DWM 亚克力的关系一致。</item>
    /// <item>浅色底色预设时整块分区切浅色主题，否则白底上仍是近白字。</item>
    /// </list>
    /// 圆角不在这里处理：macOS 上由 contentView 的图层裁剪收口（对应 Windows 的 DWM 圆角），
    /// 阴影仍由 XAML 的 BoxShadow 画。
    /// </summary>
    private void ApplyChromeMac()
    {
        var look = DesktopHost.Look;

        // 底色是浅色（白色预设）时整块切浅色主题：文字、图标、描边全跟着主题资源走深色版
        var variant = DesktopPalette.IsLight(look) ? ThemeVariant.Light : ThemeVariant.Dark;
        if (RequestedThemeVariant != variant) RequestedThemeVariant = variant;

        // 窗口本身保持透明：底色交给 XAML 的 CardBrush，模糊交给下面自建的效果层。
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        MacOsWindowInterop.MakeTransparent(this);
        RefreshDesktopBlur();
    }

    /// <summary>
    /// 重新贴一遍毛玻璃效果层（开/关、尺寸变了都要重贴）。
    /// 尺寸用的是 ClientSize（Avalonia 在 macOS 上给的是「点」，与 NSSize 同单位，不用再乘 RenderScaling）。
    /// </summary>
    private void RefreshDesktopBlur()
    {
        var look = DesktopHost.Look;
        // ClientSize 在布局跑完前可能是 0（刚打开时），退回窗口自身的宽高。
        var size = ClientSize;
        var width = size.Width > 1 ? size.Width : Width;
        var height = size.Height > 1 ? size.Height : Height;
        var ok = MacOsWindowInterop.SetDesktopBlur(this, look.Glass, MacCorner, width, height);
        DesktopLog.Write($"[chrome] macOS 毛玻璃 glass={look.Glass} ok={ok} size={width:F0}x{height:F0}");
    }

    /// <summary>「显示桌面」还原后补一次层级与磨砂：DWM 会丢掉内容表面，宿主也要重新贴回卡下。</summary>
    private void OnOwnerRestored()
    {
        var h = Hwnd;
        if (h == nint.Zero || !IsVisible) return;
        if (_vm.IsTopmost) DesktopEmbed.EnsureTopmost(h);
        else DesktopEmbed.PinBottom(h);
        _frame.RepinBehindOwner();
        DesktopEmbed.ForcePresent(h);
    }

    /// <summary>macOS 卡片圆角（DIP）。Windows 那套 20px 大圆角由玻璃宿主负责，macOS 自绘收口用 8。</summary>
    private const int MacCorner = 8;

    private static readonly bool DebugMode =
        Environment.GetEnvironmentVariable("MIHOMEHUB_DESKTOP_DEBUG") == "1";

    /// <summary>重打圆角 + 磨砂 + 桌面层级（外观变更、尺寸变化后调用）。Windows 走玻璃宿主，macOS 走自建效果层。</summary>
    public void ApplyChrome()
    {
        // macOS：没有 DWM 圆角 / 玻璃宿主那一套，改用自建的 NSVisualEffectView 磨砂层
        if (OperatingSystem.IsMacOS())
        {
            ApplyChromeMac();
            return;
        }

        _frame.ApplyChrome();
    }

    /// <summary>
    /// 外观（背景浓度 / 毛玻璃 / 底色）改动后立即作用到这块分区：
    /// 1) 让 Avalonia 内容重画一帧 —— 底色是从 VM 换进来的画刷，必须真落到画面上；
    /// 2) 让玻璃外壳按新参数重打磨砂 / 投影，再把这一帧推出去等合成；
    /// 全程**不动窗口几何**，否则拖动时整块分区会跟着 1px 抖动。
    /// </summary>
    public void ReapplyAppearance()
    {
        InvalidateVisual();
        Root.InvalidateVisual();

        // macOS：磨砂是 NSVisualEffectView 自己跟着内容重算的，没有「快照过期」这回事
        if (OperatingSystem.IsMacOS())
        {
            ApplyChromeMac();
            MacOsWindowInterop.Redraw(this);
            return;
        }

        var h = Hwnd;
        if (h == nint.Zero || !IsVisible || _dragging || _resizing) return;
        ApplyChrome();
        _frame.Sync();
        DesktopEmbed.NudgePresent(h);
        DesktopLog.Write($"[chrome] 重打磨砂 reason=appearance hwnd={h}");
    }

    /// <summary>
    /// 重取一次磨砂材质：原生宿主按「窗口被 invalidate」取样，
    /// 开机那段时序里第一次取样时桌面还没合成完，材质会停在动画帧上，
    /// 重打外壳 + 1px 尺寸抖动 = 强制重新取样。
    /// </summary>
    private void RefreshMaterial(string reason)
    {
        // macOS：重新写一次材质 hint + 让窗口重画就够了
        if (OperatingSystem.IsMacOS())
        {
            ApplyChromeMac();
            MacOsWindowInterop.Redraw(this);
            return;
        }

        var h = Hwnd;
        if (h == nint.Zero || !IsVisible || _dragging || _resizing) return;
        ApplyChrome();
        _frame.Sync();
        DesktopEmbed.ForcePresent(h);
        DesktopLog.Write($"[chrome] 重打磨砂 reason={reason} hwnd={h}");
    }

    /// <summary>挂上桌面后再补打两轮：开机启动时 DWM 的首次取样往往还是空的。</summary>
    private void StartMaterialSettle()
    {
        _settleTimer?.Stop();
        _settlePasses = 0;
        _settleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _settleTimer.Tick += (_, _) =>
        {
            if (++_settlePasses > 2)
            {
                _settleTimer?.Stop();
                _settleTimer = null;
                return;
            }
            RefreshMaterial($"settle{_settlePasses}");
        };
        _settleTimer.Start();
    }

    /// <summary>
    /// 开机/切层后补打一小段时间：Avalonia 合成会反复重置 ExStyle，
    /// NOACTIVATE 掉了点一下就把整个桌面窗口集抬到前台，TOPMOST 掉了置顶的分区又会被盖住。
    /// </summary>
    private void StartGuard()
    {
        // macOS：不用补 ExStyle —— NSWindow 的 level 不会被系统重置，也就没有「掉了又被盖住」的问题
        if (OperatingSystem.IsMacOS()) return;

        _guard?.Stop();
        _guardTicks = 0;
        _guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _guard.Tick += (_, _) =>
        {
            var h = Hwnd;
            if (h == nint.Zero || !DesktopNative.IsWindowVisible(h))
            {
                _guard?.Stop();
                return;
            }
            EnsureWndProc();
            // 只在 ExStyle 真被重置时补层级。无条件每 60ms 压一次底，
            // 玻璃宿主会被反复挤到卡下面再重钉，DWM 每次都重采背景 —— 看起来就是背景连闪。
            var exChanged = DesktopEmbed.EnsureNoActivate(h);
            if (_vm.IsTopmost)
            {
                DesktopEmbed.EnsureTopmost(h);
                if (exChanged) _frame.RepinBehindOwner();
            }
            else if (!_dragging && !_resizing && exChanged)
            {
                DesktopEmbed.PinBottom(h);
                _frame.RepinBehindOwner();
                if (_frame.UsesNativeGlass && !_frame.Sync()) _frame.FallbackToAcrylic("guard");
            }

            if (++_guardTicks < 24) return;

            // 桌面态补打 1.4s 就够了；置顶态不能停表 —— Avalonia 随时可能重写 ExStyle，
            // 停表后置顶位一旦被覆盖就再没人补，分区会悄无声息掉回普通窗口后面。降频继续守着。
            if (!_vm.IsTopmost)
            {
                _guard?.Stop();
                return;
            }
            if (_guardTicks == 24 && _guard != null)
                _guard.Interval = TimeSpan.FromMilliseconds(500);
        };
        _guard.Start();
    }

    /// <summary>
    /// 给分区窗装 WndProc 子类：
    /// 1. WM_WINDOWPOSCHANGING 的 WINDOWPOS 补 SWP_NOZORDER | SWP_NOOWNERZORDER —— 坐标照改、层级不动。
    ///    WS_EX_NOACTIVATE 只挡激活，挡不住 Avalonia 写位置/尺寸时那次 SetWindowPos 把窗口顶到
    ///    非置顶层最上面（连带整条 Progman 桌面带，Zonex 卡片也会跟着盖住前台软件）。
    ///    只补 SWP_NOZORDER 不够：owner=Progman 的窗口还会被系统按 owner 链连带重排，
    ///    必须同时 SWP_NOOWNERZORDER 才能整组不动。
    /// 2. WM_MOUSEACTIVATE 直接回 MA_NOACTIVATE —— 兜住 ExStyle 被合成重置的瞬间。
    /// </summary>
    private void InstallWndProc()
    {
        // macOS 没有 WndProc 可子类化（也没有 user32），这层压 Z 序的机制整块不存在
        if (!OperatingSystem.IsWindows()) return;

        if (_hwnd == nint.Zero || _wndProcDelegate != null) return;

        _wndProcDelegate = WndProc;
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = DesktopNative.GetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC);
        DesktopNative.SetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC, _wndProcPtr);
    }

    /// <summary>Avalonia 若重装过 WndProc，原样接回去继续压 Z 序，避免链到自己无限递归。</summary>
    private void EnsureWndProc()
    {
        // macOS 没有 WndProc 可子类化（也没有 user32），这层压 Z 序的机制整块不存在
        if (!OperatingSystem.IsWindows()) return;

        if (_hwnd == nint.Zero || _wndProcDelegate == null) return;
        var current = DesktopNative.GetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC);
        if (current == _wndProcPtr || current == nint.Zero) return;
        _originalWndProc = current;
        DesktopNative.SetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC, _wndProcPtr);
    }

    private nint WndProc(nint hWnd, uint uMsg, nint wParam, nint lParam)
    {
        if (uMsg == DesktopNative.WM_MOUSEACTIVATE)
            return new nint(DesktopNative.MA_NOACTIVATE);

        // 系统 NC 一律不画：分区内容是 20px 大圆角，系统那层边框/标题会叠成双弧
        if (uMsg == 0x0085 /* WM_NCPAINT */) return nint.Zero;
        if (uMsg == 0x0086 /* WM_NCACTIVATE */) return (nint)1;

        // Win+D 拒绝最小化：桌面组件不是普通应用窗口
        if (uMsg == 0x0112 /* WM_SYSCOMMAND */
            && ((int)wParam & 0xFFF0) == 0xF020 /* SC_MINIMIZE */)
            return nint.Zero;

        // 桌面重合成（透明效果开关 / 分辨率或壁纸变化 / explorer 起停）会把窗口材质打回
        // 系统 fallback 的实心灰底，这几条广播就是「该重取磨砂了」的信号
        if (uMsg == DesktopNative.WM_DWMCOMPOSITIONCHANGED
            || uMsg == DesktopNative.WM_DISPLAYCHANGE
            || uMsg == DesktopEmbed.ShellReadyMessage)
            Dispatcher.UIThread.Post(() => RefreshMaterial($"msg:0x{uMsg:X}"));

        // 拔插屏幕、以及 explorer（任务栏）就绪：让落位逻辑重新判定家屏在不在。
        // 分区常驻，这两条广播比主窗的屏幕事件更可靠。
        if (uMsg == DesktopNative.WM_DISPLAYCHANGE || uMsg == DesktopEmbed.ShellReadyMessage)
            Dispatcher.UIThread.Post(DisplayWatcher.Notify);

        // 坐标照改、层级不动；玻璃宿主同帧跟住卡窗，避免拖动时糊影晚一帧
        if (uMsg == DesktopNative.WM_WINDOWPOSCHANGING)
        {
            _frame.FollowOwnerMove(lParam);
            if (!DesktopEmbed.ZOrderChangeAllowed)
            {
                var wp = Marshal.PtrToStructure<DesktopNative.WINDOWPOS>(lParam);
                var changed = false;
                if (_vm.IsTopmost)
                {
                    if (wp.hwndInsertAfter != DesktopNative.HWND_TOPMOST)
                    {
                        wp.hwndInsertAfter = DesktopNative.HWND_TOPMOST;
                        changed = true;
                    }
                    if ((wp.flags & DesktopNative.SWP_NOZORDER) != 0)
                    {
                        wp.flags &= ~DesktopNative.SWP_NOZORDER;
                        changed = true;
                    }
                }
                else
                {
                    var flags = wp.flags | DesktopNative.SWP_NOZORDER | DesktopNative.SWP_NOOWNERZORDER;
                    if (flags != wp.flags)
                    {
                        wp.flags = flags;
                        changed = true;
                    }
                }
                if (changed) Marshal.StructureToPtr(wp, lParam, false);
            }
        }

        // 宿主必须紧贴卡窗正下方：Avalonia 或系统重排后玻璃可能翻到内容上面
        if (uMsg == DesktopNative.WM_WINDOWPOSCHANGED || uMsg == 0x0005 /* WM_SIZE */)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_dragging || _resizing) return;
                _frame.Sync();
                _frame.RepinBehindOwner();
            }, DispatcherPriority.Render);
        }

        return _originalWndProc != nint.Zero
            ? DesktopNative.CallWindowProcW(_originalWndProc, hWnd, uMsg, wParam, lParam)
            : DesktopNative.DefWindowProcW(hWnd, uMsg, wParam, lParam);
    }

    private void RestoreWndProc()
    {
        // macOS 没有 WndProc 可子类化（也没有 user32），这层压 Z 序的机制整块不存在
        if (!OperatingSystem.IsWindows()) return;

        if (_hwnd != nint.Zero && _originalWndProc != nint.Zero && _wndProcDelegate != null)
        {
            var current = DesktopNative.GetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC);
            if (current == _wndProcPtr)
                DesktopNative.SetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC, _originalWndProc);
        }
        _originalWndProc = nint.Zero;
        _wndProcPtr = nint.Zero;
        _wndProcDelegate = null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        if (_group != null)
        {
            _group.Devices.CollectionChanged -= OnDevicesChanged;
            _group.Scenes.CollectionChanged -= OnDevicesChanged;
        }
        Root.ReleaseContent();
        Root.Host = null;
        _guard?.Stop();
        _chromeTimer?.Stop();
        _settleTimer?.Stop();
        StopHeightAnimation();
        RestoreWndProc();
        try { _frame.Dispose(); } catch { }
        if (_hwnd != nint.Zero) DesktopEmbed.ClearAcrylic(_hwnd);
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        // 拖拽排序是「摘一张再插回」：条目数没变，不能按新增内容去放宽窗口
        if (_group?.IsReordering == true) return;
        // 事件是在下一次布局前同步派发的：此时 Height 还是「加这些之前」的高度，
        // 正好用来推算加完之后的底部会不会顶出桌面（删内容不收窄，尊重用户手拉的宽度）
        var added = e.NewItems?.Count ?? 1;
        var devices = _group?.Devices.Count ?? 0;
        var scenes = _group?.Scenes.Count ?? 0;
        var sceneBatch = ReferenceEquals(sender, _group?.Scenes);
        FitIntoDesktop(devices, scenes,
            sceneBatch ? devices : devices - added,
            sceneBatch ? scenes - added : scenes);
    }

    /// <summary>
    /// 加内容时优先「加宽一列」而不是继续向下长：分区常摆在桌面下缘，
    /// 往下多的那一行直接被桌沿（或任务栏）挡在屏幕外，用户勾完看不到新东西，只能自己拖宽。
    /// 列数由宫格宽度决定，所以这里是把窗口加宽到能多放一列，行数随之减下来。
    /// </summary>
    /// <param name="newDevices">加完之后的设备数。</param>
    /// <param name="newScenes">加完之后的场景数；场景另起一面宫格，行数各自取整再相加。</param>
    /// <param name="oldDevices">当前窗口高度对应的设备数（同一批时即「只核对能不能放下」）。</param>
    /// <param name="oldScenes">当前窗口高度对应的场景数。</param>
    private void FitIntoDesktop(int newDevices, int newScenes, int oldDevices, int oldScenes)
    {
        if (newDevices + newScenes <= 0 || _vm.Collapsed) return;

        var grid = Root.DeviceGrid;
        var minItem = grid?.MinItemWidth ?? DesktopGroupView.CardMinWidth;
        var spacing = grid?.Spacing ?? 0;

        // 列数/行高优先取实测值：宫格未参与布局（空分区）时退回「一列 + 推算行高」
        var columns = grid != null && grid.Bounds.Width > 1
            ? Math.Max(1, (int)Math.Floor((grid.Bounds.Width + spacing) / (minItem + spacing)))
            : 1;
        var rowHeight = grid != null && grid.Children.Count > 0 && grid.Children[0].Bounds.Height > 1
            ? grid.Children[0].Bounds.Height
            : CardRowDip;
        var oldRows = Rows(oldDevices, oldScenes, columns);
        var chrome = Math.Max(ChromeDip, Height - oldRows * rowHeight);

        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var scale = RenderScaling <= 0 ? 1 : RenderScaling;

        var rows = Rows(newDevices, newScenes, columns);
        var width = Width;
        while (Position.Y + (chrome + rows * rowHeight) * scale > work.Bottom)
        {
            var nextRows = Rows(newDevices, newScenes, columns + 1);
            // 加一列却减不了行（例如只多出一台），加宽没有意义
            if (nextRows >= rows) break;
            var next = width + minItem + spacing;
            if (next > Models.DesktopGroupModel.MaxWidth) break;
            // 往右长会把右缘顶出这块屏，同样看不见，宁可不加
            if (Position.X + next * scale > work.Right) break;
            columns++;
            rows = nextRows;
            width = next;
        }

        if (Math.Abs(width - Width) > 0.5) _vm.Width = Math.Round(width);
    }

    /// <summary>设备与场景各占一面宫格：行数是两段分别取整再相加，不是合起来的总数除列数。</summary>
    private static int Rows(int devices, int scenes, int columns)
        => (int)Math.Ceiling(devices / (double)columns) + (int)Math.Ceiling(scenes / (double)columns);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DesktopZoneVm.Width):
                if (Math.Abs(Width - _vm.Width) > 0.5) Width = _vm.Width;
                ScheduleChrome();
                break;
            case nameof(DesktopZoneVm.Collapsed):
                BeginHeightAnimation();
                ScheduleChrome();
                break;
            case nameof(DesktopZoneVm.ContentVisible):
                // 展开时内容先挂回树，SizeToContent=Height 会在下一帧把窗口直接撑到全高，
                // 抢在高度动画前面就成了「瞬间弹开」。先在 ContentVisible 这一拍把高度锁在折叠高度，
                // 紧接着的 Collapsed 回调再从 46 逐帧长到自然高度，全程平滑无跳变。
                if (!_vm.Collapsed && SizeToContent != SizeToContent.Manual)
                {
                    SizeToContent = SizeToContent.Manual;
                    Height = CollapsedChromeHeight;
                }
                ScheduleChrome();
                break;
            case nameof(DesktopZoneVm.IsTopmost):
                ApplyTopmost(_vm.IsTopmost);
                break;
        }
    }

    // ================= 高度展开 / 收起动画 =================

    /// <summary>
    /// 折叠 / 展开：把窗口从当前高度缓出到目标高度。
    /// 展开时先维持折叠高度，等内容挂上后量一次自然高度再开始长高，
    /// 这样既不会先闪成全高，也不会把内容压扁（窗口只做裁剪，内容按原尺寸淡入）。
    /// </summary>
    private void BeginHeightAnimation()
    {
        var request = ++_heightRequest;
        var expanding = !_vm.Collapsed;

        // 展开：起点就是刚锁定的折叠高度（不读 Bounds —— 内容挂上后它可能已被撑到全高，
        // 那样动画会从全高走到全高，看着仍像瞬间弹开）。
        // 收起 / 中途反向：从窗口当前实际高度续接，不回跳。
        var start = expanding
            ? (double.IsNaN(Height) ? CollapsedChromeHeight : Height)
            : (Bounds.Height > 1
                ? Bounds.Height
                : (double.IsNaN(Height) ? CollapsedChromeHeight : Height));

        // 动画期间由本类接管高度；展开落定后再交回 SizeToContent 自动高度
        SizeToContent = SizeToContent.Manual;
        Height = Math.Max(CollapsedChromeHeight, start);

        if (expanding)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (request != _heightRequest || _vm.Collapsed) return;
                StartHeightTween(MeasureNaturalHeight());
            }, DispatcherPriority.Loaded);
            return;
        }

        StartHeightTween(CollapsedChromeHeight);
    }

    /// <summary>
    /// 量出展开后的自然高度：给根视图无限高度，Grid 里的星号行在无限约束下按 Auto 处理，
    /// 量到的就是「标题栏 + 当前内容」应有的高度。
    /// </summary>
    private double MeasureNaturalHeight()
    {
        Root.Measure(new Size(Width, double.PositiveInfinity));
        return Math.Max(CollapsedChromeHeight, Root.DesiredSize.Height);
    }

    private void StartHeightTween(double target)
    {
        _heightFrom = Height;
        _heightTo = Math.Max(CollapsedChromeHeight, target);
        _heightElapsed = 0;

        if (Math.Abs(_heightTo - _heightFrom) < 0.5)
        {
            Height = _heightTo;
            FinishHeightAnimation();
            return;
        }

        if (_heightTimer == null)
        {
            _heightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HeightAnimationTickMs) };
            _heightTimer.Tick += OnHeightAnimationTick;
        }
        if (!_heightTimer.IsEnabled) _heightTimer.Start();
    }

    private void OnHeightAnimationTick(object? sender, EventArgs e)
    {
        _heightElapsed += HeightAnimationTickMs;
        var t = Math.Min(1.0, _heightElapsed / (double)HeightAnimationMs);
        var eased = 1 - Math.Pow(1 - t, 3); // easeOutCubic
        Height = _heightFrom + (_heightTo - _heightFrom) * eased;

        if (t < 1) return;
        FinishHeightAnimation();
    }

    private void FinishHeightAnimation()
    {
        StopHeightAnimation();
        Height = _heightTo;

        // 展开落定后交回自动高度，之后往分区里加设备仍能随内容长高；
        // 折叠保持 Manual，等 VM 卸载内容时不会先弹回全高。
        if (!_vm.Collapsed)
        {
            SizeToContent = SizeToContent.Height;
            Height = double.NaN;
        }

        ScheduleChrome();
    }

    private void StopHeightAnimation()
    {
        if (_heightTimer is not { } timer) return;
        timer.Stop();
        timer.Tick -= OnHeightAnimationTick;
        _heightTimer = null;
    }

    /// <summary>
    /// 标题栏图钉：在「贴桌面」和「浮在所有窗口之上」之间切换。
    /// 置顶要摘掉 Progman 从属，取消置顶要重新挂回去并压底 —— 两条路径都会改窗口样式，
    /// 所以全程 cloak，跟开机贴桌面同一套做法。
    /// </summary>
    private void ApplyTopmost(bool on)
    {
        // macOS：置顶 = level 抬到 kCGFloatingWindowLevel，取消钉住回桌面卡片层
        if (OperatingSystem.IsMacOS())
        {
            Topmost = on;
            if (on) MacOsWindowInterop.SetFloatingLevel(this);
            else MacOsWindowInterop.SetDesktopCardLevel(this);
            return;
        }

        var h = Hwnd;
        if (h == nint.Zero) return;

        DesktopEmbed.SetCloaked(h, true);
        try
        {
            // Avalonia 自己那份 Topmost 也要跟着改：它写位置时按这个值决定 SetWindowPos 的插入位置，
            // 只改原生样式会让两边状态打架
            Topmost = on;
            if (on) DesktopEmbed.PinTop(h);
            else
            {
                DesktopEmbed.Attach(h);
                ApplyChrome();
                DesktopEmbed.PinBottom(h);
            }
        }
        finally
        {
            DesktopEmbed.SetCloaked(h, false);
        }

        if (_frame.UsesNativeGlass)
        {
            _frame.Sync();
            _frame.PrimeForShow();
            _frame.RepinBehindOwner();
        }

        DesktopEmbed.ForcePresent(h);
        StartGuard();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _frame.Sync();
        ScheduleChrome();
    }

    /// <summary>尺寸/布局变化后延迟重打一次：拖动拉伸时逐帧重裁 Region 会闪。</summary>
    private void ScheduleChrome()
    {
        _chromeTimer?.Stop();
        _chromeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _chromeTimer.Tick += (_, _) =>
        {
            _chromeTimer?.Stop();
            _chromeTimer = null;
            ApplyChrome();
        };
        _chromeTimer.Start();
    }

    // ================= 拖动 / 拉伸 =================
    // 坐标一律取系统光标（屏幕像素）：贴桌面窗拖动时指针相对窗口的位置不变，
    // 用窗口内坐标算位移会自己抵消掉，只有屏幕坐标才推得动。

    public void BeginDrag()
    {
        if (!DesktopNative.GetCursorPos(out var p)) return;
        _dragging = true;
        _dragCursor = (p.X, p.Y);
        _dragOrigin = Position;
        // Win10 上先摘掉磨砂/投影，拖动期间只有分区窗跟手（松手在 EndDrag 补回来）
        _frame.BeginDragLite();
    }

    public void UpdateDrag()
    {
        if (!_dragging || !DesktopNative.GetCursorPos(out var p)) return;
        Position = new PixelPoint(_dragOrigin.X + (p.X - _dragCursor.X),
            _dragOrigin.Y + (p.Y - _dragCursor.Y));
    }

    public void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _frame.EndDragLite();
        SavePosition();
    }

    public void BeginResize(bool fromLeft = false)
    {
        if (!DesktopNative.GetCursorPos(out var p)) return;
        _resizing = true;
        _resizeFromLeft = fromLeft;
        _dragCursor = (p.X, p.Y);
        _resizeStartWidth = Width;
        _resizeStartX = Position.X;
        _frame.BeginDragLite();
    }

    public void UpdateResize()
    {
        if (!_resizing || !CursorPosition.TryGet(out var p)) return;
        // Windows：光标是物理像素、Width 是 DIP，要除以缩放比；Position 同样是物理像素，所以回写时再乘回去。
        // macOS：光标（CGEventGetLocation）、Width、Position 全是「点」，同一个单位，换算会把速度搞错一倍。
        var scale = OperatingSystem.IsMacOS() ? 1 : (RenderScaling <= 0 ? 1 : RenderScaling);

        if (!_resizeFromLeft)
        {
            var delta = (p.X - _dragCursor.X) / scale;
            var next = Math.Clamp(_resizeStartWidth + delta,
                Models.DesktopGroupModel.MinWidth, Models.DesktopGroupModel.MaxWidth);
            if (Math.Abs(Width - next) < 0.5) return;
            Width = next;
            return;
        }

        // 左缘拉伸：右边缘钉住，宽度变化量反推 X；不允许把左缘拖出这块屏
        var (vx, _, _, _) = DesktopEmbed.VirtualScreen();
        var maxWidth = Math.Max(Models.DesktopGroupModel.MinWidth,
            Math.Min(Models.DesktopGroupModel.MaxWidth,
                     _resizeStartWidth + (_resizeStartX - vx) / scale));
        var deltaLeft = (p.X - _dragCursor.X) / scale;
        var width = Math.Clamp(_resizeStartWidth - deltaLeft,
            Models.DesktopGroupModel.MinWidth, maxWidth);
        var x = _resizeStartX + (int)Math.Round((_resizeStartWidth - width) * scale);
        if (x != Position.X) Position = new PixelPoint(x, Position.Y);
        if (Math.Abs(Width - width) >= 0.5) Width = width;
    }

    public void EndResize()
    {
        if (!_resizing) return;
        _resizing = false;
        _vm.Width = Math.Round(Width);
        _frame.EndDragLite();
        ApplyChrome();
        SavePosition();
    }

    private void SavePosition()
    {
        _vm.Model.X = Position.X;
        _vm.Model.Y = Position.Y;
        // 用户主动落点就是新的家：记下当下所在屏，之后那块屏临时不在才有借用与回落的依据
        var sig = CurrentScreenSignature();
        if (sig.Length > 0) _vm.Model.ScreenSig = sig;
        IsExiled = false;
        DesktopHost.PersistGroup(_vm);
    }

    /// <summary>已存在的分区被再次「加号」唤出时闪一下，提示它在哪。</summary>
    public void BringToFrontTemporarily() => Root.Flash();

    /// <summary>从菜单「重命名」触发：复用标题栏的内联编辑（与双击名称同一套逻辑）。</summary>
    public void BeginRename() => Root.BeginRename();
}
