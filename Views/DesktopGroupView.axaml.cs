// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MiHome.Controls;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 分区卡片内容：标题栏（拖动 / 单击折叠 / 双击重命名）、动作区（置顶 · 折叠 · 添加设备 · 菜单）、设备宫格。
/// 贴桌面窗永远不激活，所以这里所有交互都必须能只靠鼠标完成；重命名需要的键盘输入
/// 交给独立浮层（<see cref="DesktopRenameWindow"/>），不动分区窗自身的激活状态。
/// 折叠与重命名的行为对齐参考项目 Zonex：单击标题切换折叠（名称区延迟一个双击时间，给双击改名让路），
/// 双击名称弹出改名浮层，回车提交 / Esc 取消 / 失焦提交。
/// </summary>
public partial class DesktopGroupView : UserControl
{
    /// <summary>按下后移动超过这个距离（DIP）才算拖动，否则算点击。</summary>
    private const double DragThreshold = 4;

    /// <summary>
    /// 分区宫格单卡最小宽度（DIP），与 DesktopGroupView.axaml 里 MinItemWidth="240" 同步改。
    /// 空分区时宫格还没参与布局，宿主窗口按这个值推算「加一列要加多宽」。
    /// </summary>
    public const double CardMinWidth = 240;

    private bool _pressed;
    private bool _moved;
    private bool _pressOnName;
    private Point _pressPoint;

    // 媒体分区没有标题栏，整张卡当拖动面（设备分区仍只认标题栏）
    private bool _bodyPressed;
    private bool _bodyMoved;
    private Point _bodyPoint;

    private IDisposable? _clickTimer;
    private DesktopZoneVm? _zone;
    private bool _contentAttached;

    // ===== 移入自动展开：悬停 300ms 展开，移出 250ms 后自动收起 =====
    private const int HoverExpandDelayMs = 300;
    private const int HoverCollapseDelayMs = 250;
    private const int HoverWatchdogMs = 80;
    /// <summary>悬停自动展开后的短窗口：这期间点标题栏不折叠，避免刚展开就被误点收起（对齐 Zonex）。</summary>
    private const int HoverExpandClickGuardMs = 600;
    private DispatcherTimer? _hoverExpandTimer;
    private DispatcherTimer? _hoverWatchdog;
    /// <summary>本次展开由悬停触发：只有它才允许移出后自动收起。</summary>
    private bool _expandedByHover;
    /// <summary>指针还在分区里时被手动折叠：抑制悬停展开，直到指针真正移出。</summary>
    private bool _hoverExpandSuppressed;
    private DateTime _pointerLeftAtUtc;
    /// <summary>上次由悬停自动展开的时刻，default = 无。</summary>
    private DateTime _hoverExpandedAtUtc;

    // 多个分区同时折叠/关闭时合并回收；静态回调不捕获被卸载的 View 或设备树。
    private static IDisposable? _contentReleaseTrim;

    public DesktopGroupWindow? Host { get; set; }

    /// <summary>
    /// 宫格面板：宿主窗口要按它的实测列数/卡片行高推算「再加一项后窗口会长多高」，
    /// 从而判断该向下加行还是向宽加列。设备宫格没内容（只有场景）时取场景宫格，两面卡的宽度与行高同档。
    /// 全空（宫格未参与布局）时为 null。
    /// </summary>
    public AdaptiveGridPanel? DeviceGrid =>
        this.GetVisualDescendants().OfType<AdaptiveGridPanel>().FirstOrDefault(g => g.Children.Count > 0)
        ?? this.GetVisualDescendants().OfType<AdaptiveGridPanel>().FirstOrDefault();

    public DesktopGroupView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_contentAttached) BindZone(DataContext as DesktopZoneVm);
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _contentAttached = true;
        BindZone(DataContext as DesktopZoneVm);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseContent();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>窗口关闭时也显式摘掉 VM 监听和内容树，避免等待下一轮布局清理。</summary>
    internal void ReleaseContent()
    {
        _contentAttached = false;
        CancelPendingClick();
        StopHoverExpandTimer();
        StopHoverWatchdog();
        _expandedByHover = false;
        _hoverExpandSuppressed = false;
        _hoverExpandedAtUtc = default;
        BindZone(null);
    }

    private void BindZone(DesktopZoneVm? zone)
    {
        if (!ReferenceEquals(_zone, zone))
        {
            if (_zone is not null) _zone.PropertyChanged -= OnZonePropertyChanged;
            UnloadZoneContent();
            _zone = zone;
            if (_zone is not null) _zone.PropertyChanged += OnZonePropertyChanged;
        }
        UpdateZoneContent();
    }

    private void OnZonePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(DesktopZoneVm.ContentVisible) or nameof(DesktopZoneVm.Collapsed))
            UpdateZoneContent();
    }

    private void UpdateZoneContent()
    {
        // 媒体卡展开时仍由卡面承担拖动；折叠后留下同一标题壳，才能再次展开。
        Header.IsVisible = _zone is { } zone && (!zone.IsMedia || zone.Collapsed);
        if (_zone is not { ContentVisible: true } activeZone)
        {
            UnloadZoneContent();
            return;
        }
        if (ZoneContentHost.Child is not null) return;

        Control? content = activeZone switch
        {
            DesktopGroupVm => new DesktopDevicesPanel(),
            DesktopMediaVm => new DesktopMediaPanel(),
            _ => null
        };
        if (content is null) return;
        content.DataContext = activeZone;
        ZoneContentHost.Child = content;
    }

    private void UnloadZoneContent()
    {
        if (ZoneContentHost.Child is not { } content) return;

        var controls = content.GetVisualDescendants().OfType<Control>().Prepend(content).ToArray();
        ZoneContentHost.Child = null;
        foreach (var control in controls)
        {
            // 关闭独立 Popup，并清掉 ItemsControl 对设备/场景集合及生成容器的引用。
            control.ContextMenu?.Close();
            if (control is ItemsControl items) items.ItemsSource = null;
        }
        content.DataContext = null;

        // 保留既有 90ms 淡出时序；到这里才请求回收，旧子树不会被 GC 任务闭包保留。
        _contentReleaseTrim?.Dispose();
        _contentReleaseTrim = DispatcherTimer.RunOnce(static () =>
        {
            _contentReleaseTrim = null;
            MemoryTrimmer.TrimReleasedUi();
        }, TimeSpan.FromMilliseconds(250));
    }

    /// <summary>被「加号」重复唤出时闪一下，让用户看清分区在哪。</summary>
    public void Flash()
    {
        Card.Opacity = 0.35;
        Card.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() }
        };
        Dispatcher.UIThread.Post(() => Card.Opacity = 1, DispatcherPriority.Render);
    }

    // ================= 标题栏：拖动 / 点击折叠 / 双击改名 =================

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (DataContext is not DesktopZoneVm vm) return;

        // 重命名浮层开着：这一下只用来让浮层失焦提交，不要再折叠 / 拖动
        if (DesktopHost.IsRenaming(vm)) return;

        // 标题栏上的按钮（折叠 / ＋ / ⋯）各自处理，不进拖动
        if (IsOnHeaderButton(e.Source)) return;

        // 双击名称：进入内联重命名（先撤掉等待中的单击折叠，否则改名时窗口会先收起来）
        if (e.ClickCount >= 2 && IsPressOnTitleText(e))
        {
            CancelPendingClick();
            BeginRename();
            e.Handled = true;
            return;
        }

        _pressOnName = IsPressOnTitleText(e);
        _pressPoint = e.GetPosition(this);
        _moved = false;
        _pressed = true;

        // 不在按下时抢捕获：贴桌面窗是 NOACTIVATE，按下就 Capture 会让 PointerReleased 收不到，
        // 单击折叠就永远不触发。等真的拖动（过阈值）再捕获。
        e.Handled = true;
    }

    private void OnHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (!_moved)
        {
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressPoint.X) < DragThreshold && Math.Abs(p.Y - _pressPoint.Y) < DragThreshold)
                return;
            _moved = true;
            // 先记下光标与窗口原点，再捕获：之后每一帧按屏幕光标位移推窗口
            Host?.BeginDrag();
            if (sender is Control c) e.Pointer.Capture(c);
        }

        Host?.UpdateDrag();
        e.Handled = true;
    }

    private void OnHeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        // 先把状态取出来再放捕获：Capture(null) 会同步触发 PointerCaptureLost，
        // 那个处理器会把 _pressed 清掉，顺序错了这里就会以为「没按下过」而直接 return。
        var wasPressed = _pressed;
        var moved = _moved;
        _pressed = false;
        _moved = false;

        e.Pointer.Capture(null);
        if (!wasPressed) return;

        Host?.EndDrag();
        if (moved) return;   // 拖过就不算点击

        HandleTitlePureClick();
        e.Handled = true;
    }

    private void OnHeaderCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _pressed = false;
        Host?.EndDrag();
    }

    // ================= 卡面拖动（只给没有标题栏的媒体分区用） =================
    // 媒体卡整张就是拖动面：按下落在按钮上时按钮自己吃掉事件，落到卡面空白/文字上才算拖动。
    // 这里刻意不做「点击折叠 / 双击改名」—— 没有标题栏就没有这两个动作。

    /// <summary>鼠标移入分区：折叠时按设置安排自动展开，并交给 VM 补一拍设备状态。</summary>
    private void OnZonePointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is not DesktopZoneVm vm) return;

        if (AppSettingsStore.HoverExpandEnabled && vm.Collapsed && !_hoverExpandSuppressed)
            StartHoverExpandTimer();

        // 即使不自动展开，也要用看门狗在「指针移出」时解除手动折叠抑制
        StartHoverWatchdog();
        vm.OnPointerEnteredZone();
    }

    /// <summary>折叠分区被悬停满 300ms 后自动展开；划过不触发。</summary>
    private void StartHoverExpandTimer()
    {
        if (!AppSettingsStore.HoverExpandEnabled || _hoverExpandSuppressed) return;
        if (DataContext is not DesktopZoneVm { Collapsed: true }) return;
        // 拖动 / 拉伸分区时按在卡片上会触发 PointerEntered，300ms 后不能自己弹开
        if (Host?.IsManipulating == true)
        {
            StopHoverExpandTimer();
            return;
        }
        if (_hoverExpandTimer is { IsEnabled: true }) return;
        _hoverExpandTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverExpandDelayMs) };
        _hoverExpandTimer.Tick += OnHoverExpandTick;
        _hoverExpandTimer.Start();
    }

    private void StopHoverExpandTimer()
    {
        if (_hoverExpandTimer is not { } timer) return;
        timer.Stop();
        timer.Tick -= OnHoverExpandTick;
        _hoverExpandTimer = null;
    }

    private void OnHoverExpandTick(object? sender, EventArgs e)
    {
        StopHoverExpandTimer();
        // 双重保险：计时器排队后才开始的拖动，也不能在 tick 这一刻把它弹开
        if (Host?.IsManipulating == true) return;
        if (!AppSettingsStore.HoverExpandEnabled || _hoverExpandSuppressed) return;
        if (DataContext is not DesktopZoneVm { Collapsed: true } vm) return;
        if (!IsCursorInsideZone()) return;

        vm.Collapsed = false;
        _expandedByHover = true;
        _hoverExpandedAtUtc = DateTime.UtcNow;
        _pointerLeftAtUtc = default;
        StartHoverWatchdog();
    }

    /// <summary>
    /// 贴桌面窗是 NOACTIVATE，PointerExited 不一定可靠；
    /// 用 80ms 看门狗按系统光标位置判断指针是否还在分区窗口矩形内。
    /// </summary>
    private void StartHoverWatchdog()
    {
        if (!AppSettingsStore.HoverExpandEnabled) return;
        if (_hoverWatchdog is { IsEnabled: true }) return;
        _hoverWatchdog ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverWatchdogMs) };
        _hoverWatchdog.Tick += OnHoverWatchdogTick;
        _hoverWatchdog.Start();
    }

    private void StopHoverWatchdog()
    {
        if (_hoverWatchdog is not { } timer) return;
        timer.Stop();
        timer.Tick -= OnHoverWatchdogTick;
        _hoverWatchdog = null;
        _pointerLeftAtUtc = default;
    }

    private void OnHoverWatchdogTick(object? sender, EventArgs e)
    {
        if (!AppSettingsStore.HoverExpandEnabled || (!_expandedByHover && !_hoverExpandSuppressed))
        {
            StopHoverWatchdog();
            return;
        }

        // 分区菜单是贴在窗口外的独立浮层：光标移到菜单上不算移出，菜单关掉后再重新计收起延时
        if (DataContext is DesktopZoneVm zone && DesktopHost.IsMenuOpenFor(zone))
        {
            _pointerLeftAtUtc = default;
            return;
        }

        if (IsCursorInsideZone())
        {
            _pointerLeftAtUtc = default;
            return;
        }

        var now = DateTime.UtcNow;
        if (_pointerLeftAtUtc == default)
        {
            _pointerLeftAtUtc = now;
            return;
        }
        if ((now - _pointerLeftAtUtc).TotalMilliseconds < HoverCollapseDelayMs) return;

        // 正在拖动 / 拉伸：先别收起，等手松开后由下一轮看门狗再判
        if (Host?.IsManipulating == true)
        {
            _pointerLeftAtUtc = default;
            return;
        }

        // 指针已真正移出：解除手动折叠抑制，下次移入照常展开
        _hoverExpandSuppressed = false;
        StopHoverWatchdog();
        if (!_expandedByHover) return;

        _expandedByHover = false;
        _hoverExpandedAtUtc = default;
        if (DataContext is DesktopZoneVm { Collapsed: false } vm)
            vm.Collapsed = true;
    }

    /// <summary>指针是否落在本分区窗口矩形内（物理像素）。</summary>
    private bool IsCursorInsideZone()
    {
        var h = Host?.Handle ?? nint.Zero;
        if (h == nint.Zero) return false;
        if (!DesktopNative.GetCursorPos(out var pt)) return false;
        if (!DesktopNative.GetWindowRect(h, out var rect)) return false;
        return pt.X >= rect.Left && pt.X < rect.Right
               && pt.Y >= rect.Top && pt.Y < rect.Bottom;
    }

    /// <summary>
    /// 手动折叠且指针仍停在分区里：抑制悬停展开，避免 300ms 后自己又弹开。
    /// 抑制由看门狗在指针真正移出后解除。
    /// </summary>
    private void SuppressHoverExpandAfterManualCollapse()
    {
        _expandedByHover = false;
        _hoverExpandedAtUtc = default;
        _pointerLeftAtUtc = default;
        StopHoverExpandTimer();

        // 开关已关：看门狗不会跑，抑制标记必须当场清掉，
        // 否则以后重新开启「移入自动展开」时这块分区会一直不响应
        if (!AppSettingsStore.HoverExpandEnabled)
        {
            _hoverExpandSuppressed = false;
            StopHoverWatchdog();
            return;
        }

        if (IsCursorInsideZone())
        {
            _hoverExpandSuppressed = true;
            StartHoverWatchdog();
        }
        else
        {
            _hoverExpandSuppressed = false;
            StopHoverWatchdog();
        }
    }

    /// <summary>
    /// 悬停刚自动展开的 600ms 内，忽略标题栏 / 收起按钮的折叠点击：
    /// 鼠标移入即展开、紧接着点一下又把刚展开的分区关掉，体验上像误触。
    /// 只拦「折叠」方向，窗口过期或分区本来就是折叠态时不拦。
    /// </summary>
    private bool IsHoverExpandClickGuarded(DesktopZoneVm vm)
        => !vm.Collapsed
           && _hoverExpandedAtUtc != default
           && (DateTime.UtcNow - _hoverExpandedAtUtc).TotalMilliseconds < HoverExpandClickGuardMs;

    /// <summary>所有用户主动折叠入口共用这一条，保证抑制与点击保护不漏。</summary>
    private void ToggleCollapseFromUser(DesktopZoneVm vm)
    {
        if (IsHoverExpandClickGuarded(vm)) return;
        if (!vm.Collapsed) SuppressHoverExpandAfterManualCollapse();
        vm.Collapsed = !vm.Collapsed;
    }

    private void OnBodyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not DesktopZoneVm { IsMedia: true }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (IsOnHeaderButton(e.Source)) return;

        _bodyPressed = true;
        _bodyMoved = false;
        _bodyPoint = e.GetPosition(this);
        e.Handled = true;
    }

    private void OnBodyMoved(object? sender, PointerEventArgs e)
    {
        if (!_bodyPressed) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _bodyPressed = false;
            Host?.EndDrag();
            return;
        }

        if (!_bodyMoved)
        {
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _bodyPoint.X) < DragThreshold && Math.Abs(p.Y - _bodyPoint.Y) < DragThreshold)
                return;
            _bodyMoved = true;
            Host?.BeginDrag();
            if (sender is Control c) e.Pointer.Capture(c);
        }

        Host?.UpdateDrag();
        e.Handled = true;
    }

    private void OnBodyReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasPressed = _bodyPressed;
        _bodyPressed = false;
        _bodyMoved = false;
        e.Pointer.Capture(null);
        if (wasPressed) Host?.EndDrag();
    }

    private void OnBodyCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _bodyPressed = false;
        _bodyMoved = false;
        Host?.EndDrag();
    }

    /// <summary>标题纯点击 → 展开/收起。名称区延迟一个双击时间（双击可取消），空白区立即。</summary>
    private void HandleTitlePureClick()
    {
        if (DataContext is not DesktopZoneVm vm) return;
        if (DesktopHost.IsRenaming(vm)) return;

        if (_pressOnName) ScheduleToggle();
        else ToggleCollapseFromUser(vm);
    }

    private void ScheduleToggle()
    {
        CancelPendingClick();
        var ms = DesktopNative.GetDoubleClickTime();
        if (ms == 0) ms = 500;
        var target = DataContext as DesktopZoneVm;
        _clickTimer = DispatcherTimer.RunOnce(() =>
        {
            _clickTimer = null;
            if (target == null || DesktopHost.IsRenaming(target)) return;
            ToggleCollapseFromUser(target);
        }, TimeSpan.FromMilliseconds(ms));
    }

    private void CancelPendingClick()
    {
        _clickTimer?.Dispose();
        _clickTimer = null;
    }

    private void OnCollapseClick(object? sender, RoutedEventArgs e)
    {
        CancelPendingClick();
        if (DataContext is DesktopZoneVm vm) ToggleCollapseFromUser(vm);
    }

    /// <summary>标题栏「＋」：把设备加进这个分区（媒体分区没有这个按钮，换音箱走卡片里那一行）。</summary>
    private void OnAddDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopGroupVm g) g.AddDeviceCommand.Execute(null);
    }

    /// <summary>标题栏图钉：这块分区浮到所有窗口之上 / 放回桌面层（层级切换在宿主窗口里做）。</summary>
    private void OnPinClick(object? sender, RoutedEventArgs e)
    {
        CancelPendingClick();
        if (DataContext is DesktopZoneVm vm) vm.IsTopmost = !vm.IsTopmost;
    }

    private static bool IsOnHeaderButton(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
        {
            if (v is Button) return true;
            if (v is DesktopGroupView) break;
        }
        return false;
    }

    /// <summary>按下点是否真的落在标题文字上（按几何判定，不靠命中元素 —— 文字块不参与命中测试）。</summary>
    private bool IsPressOnTitleText(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(TitleNameHost);
        var t = TitleText.Bounds;
        var hit = TitleText.IsVisible && p.X >= t.X && p.X <= t.Right && p.Y >= t.Y && p.Y <= t.Bottom;
        return hit;
    }

    // ================= 重命名 =================

    /// <summary>
    /// 标题双击 / 菜单「重命名」：在名称原来的位置弹一个独立输入浮层。
    /// 分区窗绝不能为了拿键盘焦点激活自己 —— 它挂在 Progman 名下，激活会把整个桌面窗口集
    /// （包括其它桌面组件的窗口）抬到普通软件之上。
    /// </summary>
    public void BeginRename()
    {
        if (DataContext is not DesktopZoneVm vm || vm.IsMedia) return;
        CancelPendingClick();
        if (TitleAnchor() is { } anchor) DesktopHost.OpenRename(vm, anchor);
    }

    /// <summary>名称文字当前的屏幕矩形（物理像素）：浮层据此盖在原位。</summary>
    private PixelRect? TitleAnchor()
    {
        if (!TitleText.IsVisible || TitleText.Bounds.Width < 1) return null;

        var topLeft = TitleText.PointToScreen(new Point(0, 0));
        var bottomRight = TitleText.PointToScreen(
            new Point(TitleText.Bounds.Width, TitleText.Bounds.Height));

        var w = bottomRight.X - topLeft.X;
        var h = bottomRight.Y - topLeft.Y;
        if (w < 1 || h < 1) return null;

        return new PixelRect(topLeft, new PixelSize(w, h));
    }

    // ================= 左右缘宽度拉伸 =================

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Host?.BeginResize(fromLeft: false);
        if (sender is Control c) e.Pointer.Capture(c);
        e.Handled = true;
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Host?.UpdateResize();
        e.Handled = true;
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        Host?.EndResize();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnResizeCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Host?.EndResize();

    private void OnResizeLeftPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Host?.BeginResize(fromLeft: true);
        if (sender is Control c) e.Pointer.Capture(c);
        e.Handled = true;
    }

    private void OnResizeLeftMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Host?.UpdateResize();
        e.Handled = true;
    }

    private void OnResizeLeftReleased(object? sender, PointerReleasedEventArgs e)
    {
        Host?.EndResize();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnResizeLeftCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Host?.EndResize();

    // ================= 分区菜单 =================

    /// <summary>
    /// ⋯ 菜单：贴桌面窗拿不到键盘焦点，浮层（Flyout）在 HWND_BOTTOM 的宿主上会被压到普通窗口之下，
    /// 所以开一个独立的小窗。这里把按钮的「屏幕矩形」整个传过去，菜单才能判断下方够不够放。
    /// </summary>
    private void OnMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopZoneVm vm) return;
        if (sender is not Control anchor) return;

        var topLeft = anchor.PointToScreen(new Point(0, 0));
        var bottomRight = anchor.PointToScreen(new Point(anchor.Bounds.Width, anchor.Bounds.Height));
        var rect = new PixelRect(
            topLeft.X,
            topLeft.Y,
            Math.Max(1, (int)Math.Round((double)(bottomRight.X - topLeft.X))),
            Math.Max(1, (int)Math.Round((double)(bottomRight.Y - topLeft.Y))));

        DesktopHost.OpenGroupMenu(vm, rect);
    }
}
