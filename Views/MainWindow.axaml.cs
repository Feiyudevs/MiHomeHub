// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Linq;
using System.Threading.Tasks;
using MiHomeHub.Services;
using MiHomeHub.Controls;
using MiHomeHub.Models;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

public partial class MainWindow : CustomWindow
{
    // 上次运行时保存的位置与大小，等到窗口显示后再生效
    private WindowPlacementStore.Placement? _savedPlacement;

    // 家屏：保存时窗口所在的那块显示器。它暂时不在了也不丢记录，临时落到别的屏只是借用。
    private string _homeSig = "";
    private PixelPoint _homePosition;
    private bool _hasHome;

    // true = 当前坐标只是家屏缺席时的临时落位，不能当成新位置写盘
    private bool _isExiled;
    private PixelPoint _exiledPosition;

    // 普通状态下的窗口边界，最大化 / 最小化时用它作为下次恢复的依据
    private PixelPoint _normalPosition;
    private Size _normalSize;
    private bool _hasNormalBounds;

    // 是否处于本窗口自己实现的「最大化」（正好铺满屏幕工作区）
    private bool _isMaximized;
    private PixelPoint _maximizedPosition;
    private Size _maximizedSize;

    // 拖动/缩放期间位置连续变化，等变化停下（视为拖动结束）才写盘
    private IDisposable? _settleTimer;
    // 普通态边界采集也去抖：拖动时每个像素都会触发 PositionChanged，逐帧采集等于每帧枚举一次显示器
    private IDisposable? _boundsCaptureTimer;

    private DispatcherTimer? _gpuReleaseTimer;

    /// <summary>图标选择窗同一时间只开一个，防止右键双击连开两层。</summary>
    private bool _iconPickerOpen;

    public MainWindow() : this(false) { }

    internal MainWindow(bool deferInitialContent)
    {
        _deferInitialContent = deferInitialContent;
        InitializeComponent();
        // 窗口矩形包含投影留白，最小尺寸同步加上，保证正文最小可用宽度不缩水
        // macOS 上不留白（EffectiveShadowMargin = 0），也就不需要补这段。
        if (!OperatingSystem.IsMacOS())
        {
            MinWidth += ShadowMargin * 2;
            MinHeight += ShadowMargin * 2;
        }
        RefreshSurfaceGeometry();
        // 任务栏图标要在建任务栏按钮之前就位，否则会永久空白
        WindowIconHelper.Apply(this);
        _normalSize = new Size(Width, Height);
        RestorePlacement();

        // 点击搜索胶囊以外的任意位置 → 收起搜索
        AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);

        // 点击小爱/音乐浮层与唤出胶囊以外的空白区域 → 收起浮层
        AddHandler(PointerPressedEvent, OnOverlayOutsidePressed, RoutingStrategies.Tunnel);

        // DataContext 在窗口构造后由 App 注入，这里订阅属性变化来驱动抽屉滑入/滑出动画
        DataContextChanged += OnDataContextChanged;

        PositionChanged += (_, _) => QueueCaptureNormalBounds();
        SizeChanged += (_, _) =>
        {
            EnforceSizeLimits();
            QueueCaptureNormalBounds();
        };
        Opened += (_, _) => ApplySavedPlacement();
        // 拖动结束的写盘挂在定时器上，关闭时补一次，避免最后一次变化随静默期丢掉
        Closing += OnClosing;

        // 可见且非最小化时，启用滚停回收和空闲检查；隐藏到后台时停止定时器。
        InitIdleMemoryTrimmer();
        Closed += (_, _) =>
        {
            _releasedContentTrimTimer?.Stop();
            DisplayWatcher.Changed -= OnDisplayChanged;
            if (_observedMainViewModel is not null)
            {
                _observedMainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
                _observedMainViewModel.IconEditorRequested -= OnIconEditorRequested;
            }
            _observedMainViewModel = null;
        };
    }

    // 托盘「退出」才真正结束进程；点关闭按钮只是收进托盘
    private bool _allowExit;

    public void ExitApplication()
    {
        _allowExit = true;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
            // 兜底：万一某个窗口的关闭处理又把退出拦住了，进程也得真的结束，
            // 否则用户在 Dock 里点「退出」看着像没反应，只能强杀。
            DispatcherTimer.RunOnce(() => Environment.Exit(0), TimeSpan.FromMilliseconds(2500));
        }
    }

    /// <summary>
    /// 允许系统级退出（macOS Dock 退出 / ⌘Q / lifetime 关闭）把窗口真正关掉。
    /// 不放行的话，「关窗收进托盘」会把这次程序化关闭 cancel 掉，
    /// 系统收到「用户取消」(-128)，用户看着像点退出没反应。
    /// </summary>
    internal void AllowSystemShutdown()
    {
        _allowExit = true;
        _memoryTrimmersClosed = true;
        StopAutomaticMemoryTrimmers();
        _gpuReleaseTimer?.Stop();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        DesktopLog.Write($"[closing] programmatic={e.IsProgrammatic} allowExit={_allowExit}");
        // 去抖中的采集先落地，否则最后 120ms 内的挪动会丢
        _boundsCaptureTimer?.Dispose();
        _boundsCaptureTimer = null;
        CaptureNormalBounds();
        Persist();
        if (_allowExit)
        {
            _memoryTrimmersClosed = true;
            StopAutomaticMemoryTrimmers();
            _gpuReleaseTimer?.Stop();
            return;
        }
        // 系统级的「退出」（macOS：Dock 图标右键 → 退出；也包括程序自己 Shutdown）走的是
        // 程序化关闭，不该被「关窗收进托盘」这条用户交互规则拦下来：
        // 拦下来等于应用拒绝退出，用户只能强杀进程。
        if (e.IsProgrammatic)
        {
            _allowExit = true;
            _memoryTrimmersClosed = true;
            StopAutomaticMemoryTrimmers();
            _gpuReleaseTimer?.Stop();
            return;
        }
        // 关闭即退出：托盘菜单窗口还挂在 lifetime 上，只关主窗不会结束进程，统一走退出
        if (!AppSettingsStore.CloseToTray || MiHomeHub.App.TrayUnavailable)
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(ExitApplication);
            return;
        }
        e.Cancel = true;
        Hide();
        Vm?.PausePolling();
        // 收进托盘后回收内存：窗口已隐藏，立即收缩工作集，降低常驻占用
        RequestReleasedContentTrim();
        // 安静几秒后无感回收 GPU 纹理（见下方 ScheduleGpuResourceRelease）
        ScheduleGpuResourceRelease();
    }

    // ======== 隐藏后进托盘的 GPU 纹理释放（参考 Feido ReleaseRenderCacheAsync） ========
    /// <summary>
    /// 安排 GPU 纹理释放：窗口收进托盘（已 Hide、不可见）后等几秒，无感回收 Avalonia 合成纹理。
    /// 通过临时 Opacity=0 + ShowInTaskbar=false 地 Show 一帧再 Hide，用户完全无感，
    /// 但能强制合成器释放该窗口的 GPU 纹理。需真机验证显存是否回落、有无副作用；
    /// 若无效或有害，注释掉 OnClosing 中对本方法的调用即可。
    /// </summary>
    private void ScheduleGpuResourceRelease()
    {
        _gpuReleaseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gpuReleaseTimer.Tick -= OnGpuReleaseTick;
        _gpuReleaseTimer.Tick += OnGpuReleaseTick;
        _gpuReleaseTimer.Stop();
        _gpuReleaseTimer.Start();
    }

    private void OnGpuReleaseTick(object? sender, EventArgs e)
    {
        _gpuReleaseTimer?.Stop();
        // 仅在确已收进托盘（不可见）时做一次无感回收
        if (IsVisible) return;
        var prevOpacity = Opacity;
        var prevTaskbar = ShowInTaskbar;
        _presentingHiddenFrame = true;
        try
        {
            Opacity = 0;
            ShowInTaskbar = false;
            Show();
            InvalidateVisual();
            Hide();
        }
        catch
        {
            // 无感回收失败不影响托盘常驻
        }
        finally
        {
            if (IsVisible) Hide();
            Opacity = prevOpacity;
            ShowInTaskbar = prevTaskbar;
            _presentingHiddenFrame = false;
        }
    }

    /// <summary>尺寸可在显示前设置；位置需等窗口真正显示后再设，见 <see cref="ApplySavedPlacement"/>。</summary>
    private void RestorePlacement()
    {
        _savedPlacement = WindowPlacementStore.Load();
        if (_savedPlacement is not { } saved) return;

        // 读回来的尺寸也要过一遍上限：历史布局里可能存着当年跑飞的值，
        // 直接喂给平台会被 AppKit 裁一轮再被 Avalonia 放大回去。
        Width = WindowSizeLimits.ClampWidth(saved.Width, MinWidth);
        Height = WindowSizeLimits.ClampHeight(saved.Height, MinHeight);
        _normalSize = new Size(Width, Height);
    }

    private void ApplySavedPlacement()
    {
        DisplayWatcher.Attach(this);
        DisplayWatcher.Changed -= OnDisplayChanged;
        DisplayWatcher.Changed += OnDisplayChanged;

        if (_savedPlacement is { } saved)
        {
            _homeSig = saved.ScreenSig;
            _homePosition = new PixelPoint(saved.X, saved.Y);
            _hasHome = true;
        }

        PlaceByHomeScreen();
        CaptureNormalBounds();
    }

    /// <summary>
    /// 家屏在 → 用保存的坐标；家屏被关掉/拔掉 → 等比搬到还在用的那块屏，
    /// 保存的坐标与签名都不动，等这块屏重新插上或下次开机回来时再落回原位。
    /// </summary>
    private void PlaceByHomeScreen()
    {
        if (!_hasHome) return;

        var saved = _homePosition;
        var home = MonitorLayout.FindBySignature(_homeSig)
                   // 老数据没记签名：坐标仍落在某块屏上，就当场把那块屏认作家
                   ?? (_homeSig.Length == 0 ? MonitorLayout.Holding(saved, Width, Height) : null);

        if (home is { } liveHome)
        {
            _isExiled = false;
            MoveTo(MonitorLayout.PlaceOnHome(saved, Width, Height, liveHome));
            return;
        }

        var target = MonitorLayout.Fallback();
        if (target is null) return;
        _isExiled = true;
        MoveTo(MonitorLayout.MoveOnto(saved, Width, Height, MonitorLayout.BoundsOf(_homeSig), target.Value));
        DesktopLog.Write($"[monitor] 主窗借用 {_homeSig} → {target.Value.Signature} pos={_exiledPosition}");
    }

    /// <summary>屏幕变化：家屏回来了就落回原位，家屏没了就搬到备用屏；最大化时按当前屏重铺工作区。</summary>
    private void OnDisplayChanged()
    {
        if (!_hasHome) return;
        if (_isMaximized)
        {
            MaximizeToWorkArea();
            return;
        }
        PlaceByHomeScreen();
        Persist();
    }

    /// <summary>记下落点再改坐标：CaptureNormalBounds 靠这个值区分「程序搬的」和「用户拖的」。</summary>
    private void MoveTo(PixelPoint position)
    {
        _exiledPosition = position;
        if (Position != position) Position = position;
    }

    /// <summary>
    /// 系统最大化会把无边框窗口撑到工作区之外（右侧、底部溢出屏幕导致内容被裁切），
    /// 所以改为自己算边界：正好铺满当前屏幕的工作区。
    /// </summary>
    private void MaximizeToWorkArea()
    {
        var screen = Screens.ScreenFromWindow(this)
                     ?? Screens.ScreenFromPoint(Position)
                     ?? Screens.Primary;
        if (screen is null) return;

        var work = screen.WorkingArea;
        // 工作区为空（屏幕对象刚失效 / 显示器热插拔的瞬态）时不能往下走：
        // 一旦把尺寸设成 0，窗口会塌成 0×0，随后平台按最小尺寸回弹，
        // 每弹一次就把最小尺寸加到窗口上（实测 1668 → 2648 → 3628 … 一路涨到几万），
        // 最后 backing store 分配失败把进程带走 —— 也就是「点绿键进全屏就闪退」。
        if (work.Width <= 0 || work.Height <= 0)
        {
            CrashLog.Write("window-maximize", new InvalidOperationException(
                $"工作区无效，已放弃放大：{work}"));
            return;
        }
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;

        // 还有没落盘的普通边界就补上，最大化本身不记录；此后到退出最大化前不再写
        if (_settleTimer is not null) Persist();
        _isMaximized = true;
        // 先把系统外框压干净，再算边界：否则 Width 会被按「客户区 + frame」换算，外层溢出工作区
        ApplyWindowChromeNow();
        // 先撤掉投影留白与圆角，再铺满工作区；否则窗口矩形里会留一圈透明边、四角露出桌面
        SetSurfaceMaximized(true);
        _maximizedPosition = work.Position;
        var maxW = WindowSizeLimits.ClampWidth(work.Width / scaling, MinWidth);
        var maxH = WindowSizeLimits.ClampHeight(work.Height / scaling, MinHeight);
        _maximizedSize = new Size(maxW, maxH);
        Position = _maximizedPosition;
        Width = _maximizedSize.Width;
        Height = _maximizedSize.Height;
        // 外层矩形再按物理像素钉死一次：正好铺满工作区，不多不少
        SetSurfaceBounds(work);
        // 平台把窗口帧读回来时偶尔会把物理像素当逻辑点（尺寸凭空放大）。
        // 下一拍核对一次，尺寸不对就钉回工作区，避免用户看到「窗口突然撑爆」。
        Dispatcher.UIThread.Post(ReassertMaximizedBounds, DispatcherPriority.Background);
    }

    /// <summary>放大后复核尺寸：被平台读歪了就钉回工作区尺寸。</summary>
    private void ReassertMaximizedBounds()
    {
        if (!_isMaximized || _maximizedSize.Width <= 0) return;
        if (Math.Abs(Width - _maximizedSize.Width) <= 2 && Math.Abs(Height - _maximizedSize.Height) <= 2) return;
        var w = WindowSizeLimits.ClampWidth(_maximizedSize.Width, MinWidth);
        var h = WindowSizeLimits.ClampHeight(_maximizedSize.Height, MinHeight);
        CrashLog.Write("window-maximize", new InvalidOperationException(
            $"放大后尺寸被读歪，已钉回：{Width}x{Height} → {w}x{h}"));
        Width = w;
        Height = h;
    }

    /// <summary>
    /// 窗口是否还停在铺满工作区的那圈边界上。系统还原、贴边分屏等不经过本窗口的退出方式
    /// 只会改变几何，所以以几何为准，避免 _isMaximized 卡住后普通边界再也不更新。
    /// 2 DIP 容差是设备像素取整误差。
    /// </summary>
    private bool IsOnMaximizedBounds() =>
        Position == _maximizedPosition
        && Math.Abs(Width - _maximizedSize.Width) <= 2
        && Math.Abs(Height - _maximizedSize.Height) <= 2;

    private void RestoreFromMaximize()
    {
        // 同上：先压干净外框，避免 Width/Height 被按「客户区 + frame」换算成溢出的外层矩形
        ApplyWindowChromeNow();
        _isMaximized = false;
        if (_hasNormalBounds)
        {
            // 还原用的普通边界同样要夹上限：它可能来自当年跑飞的那次拖拽
            var restoreW = WindowSizeLimits.ClampWidth(_normalSize.Width, MinWidth);
            var restoreH = WindowSizeLimits.ClampHeight(_normalSize.Height, MinHeight);
            Position = _normalPosition;
            Width = restoreW;
            Height = restoreH;
            // macOS 的落位单位是逻辑点（Screen.Scaling 恒为 1），拿不到屏时也不能退回
            // 窗口的 RenderScaling（Retina 上是 2），否则尺寸会被放大一倍。
            var scale = Screens.ScreenFromPoint(_normalPosition)?.Scaling
                        ?? (OperatingSystem.IsMacOS() ? 1 : RenderScaling);
            if (scale <= 0) scale = 1;
            SetSurfaceBounds(new PixelRect(_normalPosition, new PixelSize(
                (int)Math.Round(_normalSize.Width * scale),
                (int)Math.Round(_normalSize.Height * scale))));
        }
        // 尺寸先回到普通边界再放回留白，避免还原瞬间卡片被工作区裁一圈
        SetSurfaceMaximized(false);
        // 同样复核一次：还原后尺寸被平台读歪的话钉回普通边界
        if (_hasNormalBounds)
            Dispatcher.UIThread.Post(() =>
            {
                if (_isMaximized) return;
                        var w = WindowSizeLimits.ClampWidth(_normalSize.Width, MinWidth);
                var h = WindowSizeLimits.ClampHeight(_normalSize.Height, MinHeight);
                if (Math.Abs(Width - w) <= 2 && Math.Abs(Height - h) <= 2) return;
                Width = w;
                Height = h;
            }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 把内容级圆角 / 投影留白对齐到 <see cref="CustomWindow"/> 的当前生效值。
    /// 两层 Border 必须同步：投影层与裁切层分开，投影才不会被 ClipToBounds 裁成硬边。
    /// </summary>
    protected override void OnSurfaceGeometryChanged()
    {
        var margin = new Thickness(EffectiveShadowMargin);
        var radius = new CornerRadius(EffectiveCornerRadius);
        // 内容裁剪层比外轮廓小一个描边宽度：1 位裁剪掩膜的硬台阶落在描边内侧的同色背景上，看不见。
        var innerRadius = new CornerRadius(System.Math.Max(0d, EffectiveCornerRadius - 1d));

        SurfaceShadow.Margin = margin;
        SurfaceShadow.CornerRadius = radius;
        SurfaceShadow.IsVisible = EffectiveShadowMargin > 0;

        RootSurface.Margin = margin;
        RootSurface.CornerRadius = radius;
        SurfaceClip.CornerRadius = innerRadius;
    }

    private IBrush? _resizePageMask;
    private bool _resizeLite;

    /// <summary>
    /// 拖边框改尺寸时整窗每帧都要重绘，先卸掉两处最贵的全窗特效：
    /// 24px 模糊的窗口投影、页面顶部的渐变 OpacityMask（Skia 会为它建一整块离屏图层再合成）。
    /// 松手立刻装回去，静止观感不变。纯移动不触发（移动不重排内容，卸了反而白闪一下）。
    /// </summary>
    protected override void OnResizeInteractionChanged(bool resizing)
    {
        if (resizing)
        {
            if (_resizeLite) return;
            _resizeLite = true;
            SurfaceShadow.IsVisible = false;
            if (PageCarousel is { } liteCarousel)
            {
                _resizePageMask = liteCarousel.OpacityMask;
                liteCarousel.OpacityMask = null;
            }
            return;
        }

        if (_resizeLite)
        {
            _resizeLite = false;
            SurfaceShadow.IsVisible = EffectiveShadowMargin > 0;
            if (PageCarousel is { } carousel)
                carousel.OpacityMask = _resizePageMask;
            _resizePageMask = null;
        }
        // 纯移动也会走到这里：拖动期间的分帧事件被跳过，松手后必须补记最终落位，否则下次启动回旧位置。
        QueueCaptureNormalBounds();
    }

    /// <summary>系统（Win+↑、贴边）发起的最大化同样会溢出屏幕，转成自己实现的铺满工作区。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == WindowStateProperty)
        {
            UpdateMainContentLifetime();
            UpdateAutomaticMemoryTrimmerActivity();
        }
        // 收进托盘（Hide）后不再轮询；从托盘 Show 回来要恢复。
        // 之前只处理了「最小化」这条路径，Hide→Show 时 WindowState 没有变化，轮询会一直停着。
        if (change.Property == IsVisibleProperty)
        {
            // GPU 纹理回收会临时 Show/Hide 一帧，不能把它当成用户唤出/收起
            if (_presentingHiddenFrame) return;
            if (IsVisible && WindowState != WindowState.Minimized) Vm?.ResumePolling();
            else if (!IsVisible) Vm?.PausePolling();
        }
        if (change.Property == WindowStateProperty)
        {
            // 最小化到任务栏：窗口已不可见，立即收缩工作集，降低常驻占用
            if (WindowState == WindowState.Minimized)
            {
                Vm?.PausePolling();
                RequestReleasedContentTrim();
                return;
            }
            // 从最小化恢复：重启状态轮询
            Vm?.ResumePolling();
            if (WindowState != WindowState.Maximized) return;

            if (OperatingSystem.IsMacOS())
            {
                // macOS：自己把窗口帧铺到工作区后，系统会认为窗口已经「zoom」，
                // Avalonia 随即把 WindowState 报成 Maximized。
                // 这时绝不能反手设回 Normal —— 原生窗口会被 unzoom 成 0×0，
                // 接着平台按最小尺寸回弹，每回弹一次就把最小尺寸加到窗口上
                // （实测 1668 → 2648 → 3628 … 一路涨到上万），
                // 最后 backing store 分配失败，进程直接闪退（用户报「点绿键进全屏就闪退」）。
                // 这里只认下这个状态：铺满工作区本来就是我们想要的效果。
                Dispatcher.UIThread.Post(() =>
                {
                    if (WindowState != WindowState.Maximized) return;
                    if (!_isMaximized)
                    {
                        // 只有当窗口确实铺满了某块屏的工作区时才认下这个「最大化」状态；
                        // 否则是还原之后平台残留的旧状态，认下会把窗口又拽回去铺满。
                        var zoomed = false;
                        foreach (var s in Screens.All)
                        {
                            var work = s.WorkingArea;
                            if (work.Width <= 0 || work.Height <= 0) continue;
                            if (Math.Abs(Width - work.Width) <= 2 && Math.Abs(Height - work.Height) <= 2)
                            {
                                zoomed = true;
                                break;
                            }
                        }
                        if (!zoomed) return;
                        CaptureNormalBounds();
                        _isMaximized = true;
                        _maximizedPosition = Position;
                        _maximizedSize = new Size(Width, Height);
                    }
                    ReassertMaximizedBounds();
                }, DispatcherPriority.Background);
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (WindowState != WindowState.Maximized) return;
                WindowState = WindowState.Normal;
                MaximizeToWorkArea();
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// 尺寸的兜底闸门：任何路径（拖边、平台回读、还原）把窗口撑得超过上限时，
    /// 立刻拉回上限。macOS 上超大的窗口帧会被 AppKit 裁一轮、再被 Avalonia 当成逻辑点读回来，
    /// 一来一回就放大一轮，最后 backing store 分配失败把进程带走（用户报「点绿键进全屏就闪退」）。
    /// </summary>
    private void EnforceSizeLimits()
    {
        if (double.IsNaN(Width) || double.IsNaN(Height)) return;
        if (Width <= WindowSizeLimits.MaxExtent && Height <= WindowSizeLimits.MaxExtent) return;

        var w = WindowSizeLimits.ClampWidth(Width, MinWidth);
        var h = WindowSizeLimits.ClampHeight(Height, MinHeight);
        CrashLog.Write("window-size", new InvalidOperationException(
            $"窗口尺寸超出上限，已拉回：{Width}x{Height} → {w}x{h}"));
        Width = w;
        Height = h;
    }

    /// <summary>
    /// 尺寸 / 位置变化的采集不逐帧做：去抖到变化停下 120ms 再算一次。
    /// 拖动时每帧都会触发事件，逐次采集会让 UI 线程在每一帧枚举一遍显示器（EnumDisplayMonitors + DPI 查询）。
    /// 最大化的正常路径由 <see cref="ToggleMaximize"/> 同步采集，不依赖这里。
    /// </summary>
    private void QueueCaptureNormalBounds()
    {
        if (IsInSizeMove) return;
        _boundsCaptureTimer?.Dispose();
        _boundsCaptureTimer = DispatcherTimer.RunOnce(() =>
        {
            _boundsCaptureTimer = null;
            CaptureNormalBounds();
        }, TimeSpan.FromMilliseconds(120));
    }

    private void CaptureNormalBounds()
    {
        if (WindowState != WindowState.Normal) return;
        if (_isMaximized && IsOnMaximizedBounds()) return;
        _isMaximized = false;
        if (double.IsNaN(Width) || Width <= 0 || double.IsNaN(Height) || Height <= 0) return;
        // 离谱的尺寸不进「普通边界」：否则它会被还原时原样写回平台，继续滚雪球
        if (!WindowSizeLimits.IsSane(Width, Height)) return;
        // 借用别的屏期间用户自己挪了窗口 = 重新安家，此后这个位置就是它的家，照旧写盘
        if (_isExiled && Position != _exiledPosition) _isExiled = false;
        _normalPosition = Position;
        _normalSize = new Size(Width, Height);
        _hasNormalBounds = true;
        if (!_isExiled)
        {
            _homePosition = _normalPosition;
            _homeSig = MonitorLayout.SignatureAt(_normalPosition, Width, Height);
            _hasHome = true;
        }
        QueuePersist();
    }

    /// <summary>位置/尺寸变化不停写盘，只在下一次变化前静默 300ms（拖动结束）时写一次。</summary>
    private void QueuePersist()
    {
        _settleTimer?.Dispose();
        _settleTimer = DispatcherTimer.RunOnce(() =>
        {
            _settleTimer = null;
            Persist();
        }, TimeSpan.FromMilliseconds(300));
    }

    private void Persist()
    {
        _settleTimer?.Dispose();
        _settleTimer = null;
        if (_isMaximized || !_hasNormalBounds) return;
        // 借用别的屏期间写的仍是家屏坐标，尺寸按当下的记
        WindowPlacementStore.Save(_homePosition.X, _homePosition.Y,
            _normalSize.Width, _normalSize.Height, _homeSig);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (_isMaximized) RestoreFromMaximize(); // 拖动已最大化的窗口时先还原
        BeginMoveDrag(e);
    }

    private void OnMinClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // ---------- macOS 红绿灯 ----------
    // 红：把窗口收起来但应用继续活着（原生 mac 的语义：点红点只是关掉这一扇窗，
    // 菜单栏图标还在，程序不退出）。这里直接 Hide + 停轮询，不走 Close：
    // Close 会触发 OnClosing 的「关闭时收进托盘」分支，行为取决于那个设置；
    // 而原生红点就是「隐藏这一扇窗」，与设置无关。
    private void OnMacCloseClick(object? sender, RoutedEventArgs e)
    {
        Hide();
        Vm?.PausePolling();
        RequestReleasedContentTrim();
        ScheduleGpuResourceRelease();
    }

    // 黄：最小化到 Dock。macOS 上 WindowState=Minimized 会真的把 NSWindow
    // 收进 Dock（原生 miniaturize），点 Dock 图标能还原。
    private void OnMacMinClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // 绿：铺满工作区。Avalonia 的 macOS 后端把 WindowState.FullScreen 只当状态位，
    // 不改窗口尺寸（实测 bounds 不变），所以这里用「铺满工作区」这一档，
    // 行为上等同原生 mac 里「不支持全屏的窗口按绿键放大」。
    private void OnMacZoomClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    internal void ToggleMaximize()
    {
        if (_isMaximized)
        {
            RestoreFromMaximize();
            return;
        }
        CaptureNormalBounds();
        MaximizeToWorkArea();
    }

    private void OnGotoHome(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.SelectedNavIndex = 0;
    }

    private void OnGotoScenes(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.SelectedNavIndex = 2;
    }

    private void OnGotoHotkeys(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.SelectedNavIndex = 3;
    }

    private void OnGotoSettings(object? sender, RoutedEventArgs e)
    {
        Vm?.OpenSettingsCommand.Execute(null);
    }

    private void OnGotoRooms(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.SelectedNavIndex = 1;
    }

    private bool _searchOpen;

    // 事件从窗口往下隧穿，子控件把点击标为已处理也照样收到
    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_searchOpen || SearchBox.IsPointerOver) return;
        SetSearchOpen(false);
    }

    /// <summary>
    /// 点击浮层与唤出胶囊以外的空白区域收起标题栏浮层（小爱 / 音乐）。
    /// 音箱下拉是独立 Popup（TopLevel 不是本窗口），在其中的选择不算“点外部”。
    /// </summary>
    private void OnOverlayOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!vm.IsVoiceOpen && !vm.IsMusicOpen && !vm.IsHomeOpen) return;
        if (e.Source is not Visual src) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (TopLevel.GetTopLevel(src) != this) return;
        if (IsWithin(src, HomeChipBtn) || IsWithin(src, HomePanelView)) return;
        if (IsWithin(src, XiaoAiBtn) || IsWithin(src, VoicePanelView)) return;
        if (IsWithin(src, MusicBtn) || IsWithin(src, MusicPanelView)) return;
        vm.IsVoiceOpen = false;
        vm.IsMusicOpen = false;
        vm.IsHomeOpen = false;
    }

    private static bool IsWithin(Visual src, Visual? container)
    {
        if (container == null) return false;
        for (Visual? v = src; v != null; v = v.GetVisualParent())
            if (v == container) return true;
        return false;
    }

    private void OnSearchBoxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (_searchOpen)
        {
            SearchInput.Focus();
            return;
        }
        e.Handled = true; // 展开动作不能顺带触发标题栏拖动
        SetSearchOpen(true);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true; // 不让窗口级 Escape 顺手关掉其他浮层
        SetSearchOpen(false);
    }

    /// <summary>清空钮：只清关键词，输入框保持展开（方便立刻改词重搜）。</summary>
    private void OnSearchClearClick(object? sender, RoutedEventArgs e)
    {
        if (FindMainControl<TextBox>("SearchInput") is not { } input) return;
        input.Text = string.Empty;
        input.Focus();
    }

    private void OnSearchLostFocus(object? sender, RoutedEventArgs e)
    {
        // 焦点还在搜索胶囊上（点击胶囊内空白）或还有关键词时保持展开
        Dispatcher.UIThread.Post(() =>
        {
            if (!_searchOpen) return;
            if (SearchInput.IsKeyboardFocusWithin || SearchBox.IsPointerOver) return;
            if (!string.IsNullOrEmpty(SearchInput.Text)) return;
            SetSearchOpen(false);
        }, DispatcherPriority.Background);
    }

    private void SetSearchOpen(bool open)
    {
        if (_searchOpen == open) return;
        _searchOpen = open;
        SearchBox.Classes.Set("open", open);
        if (open)
        {
            SearchInput.Focus();
            return;
        }
        // 收起即清空关键词，避免看不见的过滤还在生效
        SearchInput.Text = string.Empty;
        if (SearchInput.IsKeyboardFocusWithin) SearchBox.Focus();
    }

    private void OnMaskClick(object? sender, PointerReleasedEventArgs e) => Vm?.CloseOverlaysCommand.Execute(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // 搜索框里的按键不能被当成快捷键，但 Escape 仍用于关闭抽屉。
        bool typing = e.Source is TextBox;
        if (e.Key == Key.Escape)
        {
            Vm?.CloseOverlaysCommand.Execute(null);
            return;
        }
        if (typing || e.KeyModifiers != KeyModifiers.None) return;

        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1: if (Vm != null) Vm.SelectedNavIndex = 0; break;
            case Key.D2 or Key.NumPad2: if (Vm != null) Vm.SelectedNavIndex = 1; break;
            case Key.D3 or Key.NumPad3: if (Vm != null) Vm.SelectedNavIndex = 2; break;
            case Key.D4 or Key.NumPad4: Vm?.OpenSettingsCommand.Execute(null); break;
            case Key.T: Vm?.ToggleThemeCommand.Execute(null); break;
        }
    }

    // ================= 设备详情抽屉：右→左滑入动画 + 遮罩淡入淡出 =================

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_observedMainViewModel is not null)
        {
            _observedMainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
            _observedMainViewModel.IconEditorRequested -= OnIconEditorRequested;
        }
        _observedMainViewModel = DataContext as MainViewModel;
        if (_observedMainViewModel is not null)
        {
            _observedMainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
            _observedMainViewModel.IconEditorRequested += OnIconEditorRequested;
        }
        if (_mainContentRoot is not null) _mainContentRoot.DataContext = DataContext;
    }

    /// <summary>房间 / 场景要换图标时弹出选择窗；解析与落盘都在弹窗视图模型里完成。</summary>
    private async void OnIconEditorRequested(object? sender, CustomIconTarget target)
    {
        // 场景详情已经独立成窗时，弹窗要挂在那个窗上，主窗口不抢（否则会把主程序拽到前面）
        if (target.FromEditor &&
            _observedMainViewModel is { IsSceneEditorOpen: true, IsSceneEditorInline: false })
            return;

        if (_iconPickerOpen) return;
        _iconPickerOpen = true;
        try
        {
            var window = new IconPickerWindow(target);
            await window.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Vm?.Log("[IconPicker] 打开图标选择窗失败：" + ex.Message);
        }
        finally
        {
            _iconPickerOpen = false;
        }
    }

    /// <summary>
    /// 小爱浮层左沿对齐唤出的胶囊按钮；靠右放不下时夹在窗口右边距内。
    /// </summary>
    private void AlignVoicePanel()
    {
        var panel = VoicePanelView;
        if (CapsulePanelLeft(XiaoAiBtn, panel, panel?.CardWidth ?? 344) is double x) panel?.AlignToButton(x);
    }

    /// <summary>音乐浮层同上：两个浮层同宽同手感，共用一套夹取。</summary>
    private void AlignMusicPanel()
    {
        var panel = MusicPanelView;
        if (CapsulePanelLeft(MusicBtn, panel, panel?.CardWidth ?? 344) is double x) panel?.AlignToButton(x);
    }

    /// <summary>我的家庭浮层同上：左沿对齐家庭胶囊，同宽同位。</summary>
    private void AlignHomePanel()
    {
        var panel = HomePanelView;
        if (CapsulePanelLeft(HomeChipBtn, panel, panel?.CardWidth ?? 344) is double x) panel?.AlignToButton(x);
    }

    /// <summary>
    /// 把卡片左沿落到锚点按钮左沿。<paramref name="host"/> 是铺满整窗的宿主 UserControl
    /// （所以只能拿它当坐标系，宽度必须用 <paramref name="cardWidth"/>，不能用 host.Bounds.Width）。
    /// </summary>
    private double? CapsulePanelLeft(Control? anchor, Control? host, double cardWidth)
    {
        if (anchor == null || host == null) return null;
        var pt = anchor.TranslatePoint(new Point(0, 0), host);
        if (pt is null) return null;

        const double edge = 12;
        // Bounds 是含投影留白的窗口矩形，正文可见宽度要减掉两侧留白
        var visibleWidth = Bounds.Width - EffectiveShadowMargin * 2;
        var maxLeft = visibleWidth - edge - cardWidth;
        return Math.Clamp(pt.Value.X, edge, Math.Max(edge, maxLeft));
    }

    /// <summary>
    /// 抽屉从右侧滑入（X 正方向出屏），两层遮罩做淡入淡出动画（打开淡入、关闭淡出）。
    /// 动画用完整关键帧（0%→100%）保证可靠；关闭动画结束后再统一隐藏，避免遮罩残留挡点击。
    /// </summary>
    private void AnimateDeviceDrawer(bool open)
    {
        var mask = DeviceMask;
        var maskLight = DeviceMaskLight;
        if (mask == null || maskLight == null) return;

        var duration = TimeSpan.FromMilliseconds(open ? 300 : 230);
        var ease = new CubicEaseOut();

        mask.IsVisible = true;
        maskLight.IsVisible = true;

        var maskAnim = FadeAnimation(open ? 0 : 1, open ? 1 : 0, duration, ease);
        var maskLightAnim = FadeAnimation(open ? 0 : 1, open ? 1 : 0, duration, ease);

        _ = maskAnim.RunAsync(mask).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                if (!open)
                {
                    mask.IsVisible = false;
                    maskLight.IsVisible = false;
                }
            }));
        _ = maskLightAnim.RunAsync(maskLight);

        // 抽屉本身的滑入 / 滑出动画交给子控件处理
        DeviceDrawerView?.Animate(open);
    }

    /// <summary>构造一个透明度关键帧动画（0% 起、100% 止，FillMode.Forward 保持终值）。</summary>
    private static Animation FadeAnimation(double from, double to, TimeSpan duration, Easing ease)
    {
        var anim = new Animation
        {
            Duration = duration,
            Easing = ease,
            FillMode = FillMode.Forward
        };
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Setter(OpacityProperty, from) }
        });
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Setter(OpacityProperty, to) }
        });
        return anim;
    }
}
