using System;
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
/// 分区卡片内容：标题栏（拖动 / 单击折叠 / 双击重命名）、动作区（折叠 · 添加设备 · 菜单）、设备宫格。
/// 贴桌面窗永远不激活，所以这里所有交互都必须能只靠鼠标完成。
/// 折叠与重命名的行为对齐参考项目 Zonex：单击标题切换折叠（名称区延迟一个双击时间，给双击改名让路），
/// 双击名称进入内联输入框，回车提交 / Esc 取消 / 失焦提交 / 点窗外提交。
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

    private IDisposable? _clickTimer;
    private IDisposable? _dismissArm;
    private DispatcherTimer? _dismissTimer;
    private bool _renaming;
    private bool _renameEnding;

    public DesktopGroupWindow? Host { get; set; }

    /// <summary>
    /// 设备宫格面板：宿主窗口要按它的实测列数/卡片行高推算「再加一台设备后窗口会长多高」，
    /// 从而判断该向下加行还是向宽加列。空分区（宫格未参与布局）时为 null。
    /// </summary>
    public AdaptiveGridPanel? DeviceGrid =>
        this.GetVisualDescendants().OfType<AdaptiveGridPanel>().FirstOrDefault();

    public DesktopGroupView()
    {
        InitializeComponent();

        // 卡片其它位置按下也要结束内联重命名：贴桌面窗是 NOACTIVATE，LostFocus 不可靠，
        // 在隧道阶段统一兜住（编辑框自身的按下不算）。
        AddHandler(PointerPressedEvent, OnRootPressedTunnel, RoutingStrategies.Tunnel);
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
        if (DataContext is not DesktopGroupVm vm) return;

        // 内联重命名中：点在输入框外先提交，本次按下不再触发拖动/折叠
        if (_renaming)
        {
            if (!IsRenameBox(e.Source))
            {
                CommitRename();
                e.Handled = true;
            }
            return;
        }

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

    /// <summary>标题纯点击 → 展开/收起。名称区延迟一个双击时间（双击可取消），空白区立即。</summary>
    private void HandleTitlePureClick()
    {
        if (_renaming) return;
        if (DataContext is not DesktopGroupVm vm) return;

        if (_pressOnName) ScheduleToggle();
        else
        {
            vm.Collapsed = !vm.Collapsed;
        }
    }

    private void ScheduleToggle()
    {
        CancelPendingClick();
        var ms = DesktopNative.GetDoubleClickTime();
        if (ms == 0) ms = 500;
        _clickTimer = DispatcherTimer.RunOnce(() =>
        {
            _clickTimer = null;
            if (!_renaming && DataContext is DesktopGroupVm vm) vm.Collapsed = !vm.Collapsed;
        }, TimeSpan.FromMilliseconds(ms));
    }

    private void CancelPendingClick()
    {
        _clickTimer?.Dispose();
        _clickTimer = null;
    }

    private void OnCollapseClick(object? sender, RoutedEventArgs e)
    {
        // 正在重命名时：只提交改名，绝不再触发折叠（点折叠箭头也等同于在分区上点了一下）
        if (_renaming) { CommitRename(); return; }
        CancelPendingClick();
        if (DataContext is DesktopGroupVm vm)
        {
            vm.Collapsed = !vm.Collapsed;
        }
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

    /// <summary>按下点是否落在标题文字（或改名输入框）上。</summary>
    private bool IsOnTitleText(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, TitleText) || ReferenceEquals(v, TitleEditBox)) return true;
            if (ReferenceEquals(v, TitleNameHost)) return true;
            if (v is DesktopGroupView) break;
        }
        return false;
    }

    private bool IsRenameBox(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, TitleEditBox)) return true;
            if (v is DesktopGroupView) break;
        }
        return false;
    }

    // ================= 内联重命名 =================

    public void BeginRename()
    {
        if (_renaming || DataContext is not DesktopGroupVm vm) return;

        _renaming = true;
        _pressed = false;
        Host?.EndDrag();
        // 贴桌面窗是 NOACTIVATE，收不到键盘；改名期间临时放开，结束再收回
        Host?.BeginRenameFocus();

        TitleText.IsVisible = false;
        TitleEditBox.Text = vm.Name;
        TitleEditBox.IsVisible = true;

        TitleEditBox.KeyDown -= OnRenameKeyDown;
        TitleEditBox.KeyDown += OnRenameKeyDown;
        TitleEditBox.LostFocus -= OnRenameLostFocus;
        TitleEditBox.LostFocus += OnRenameLostFocus;

        // 等一帧再聚焦：此时输入框刚参与布局，直接 Focus 会被吞掉
        Dispatcher.UIThread.Post(() =>
        {
            if (!_renaming) return;
            TitleEditBox.Focus();
            TitleEditBox.SelectAll();
        }, DispatcherPriority.Input);

        StartDismissPoll();
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_renaming) return;
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_renaming || _renameEnding) return;
        Dispatcher.UIThread.Post(() => { if (_renaming) CommitRename(); }, DispatcherPriority.Background);
    }

    private void CommitRename()
    {
        if (!_renaming || _renameEnding) return;

        string? applied = null;
        var name = TitleEditBox.Text?.Trim() ?? "";
        if (DataContext is DesktopGroupVm vm && name.Length > 0 && name != vm.Name)
        {
            vm.Name = name.Length > 24 ? name[..24] : name;
            applied = vm.Name;
        }

        EndRenameUi();
    }

    private void CancelRename()
    {
        if (!_renaming || _renameEnding) return;
        EndRenameUi();
    }

    private void EndRenameUi()
    {
        if (_renameEnding) return;
        _renameEnding = true;
        try
        {
            _renaming = false;
            StopDismissPoll();

            TitleEditBox.KeyDown -= OnRenameKeyDown;
            TitleEditBox.LostFocus -= OnRenameLostFocus;
            TitleEditBox.IsVisible = false;
            TitleText.IsVisible = true;

            Host?.EndRenameFocus();
        }
        finally
        {
            _renameEnding = false;
        }
    }

    /// <summary>点分区窗外的空白：完成改名。窗口常不抢前台，只能靠轮询鼠标键 + 窗口矩形判断。</summary>
    private void StartDismissPoll()
    {
        StopDismissPoll();
        // 先躲开本次双击的第二次按下，免得刚进入编辑就被判成「点了窗外」
        _dismissArm = DispatcherTimer.RunOnce(() =>
        {
            _dismissArm = null;
            if (!_renaming) return;
            // 40ms 一轮：GetAsyncKeyState 只反映「此刻是否按下」，太慢会把快手的短按漏掉
            _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _dismissTimer.Tick += (_, _) => PollDismiss();
            _dismissTimer.Start();
        }, TimeSpan.FromMilliseconds(180));
    }

    private void StopDismissPoll()
    {
        _dismissArm?.Dispose();
        _dismissArm = null;
        _dismissTimer?.Stop();
        _dismissTimer = null;
    }

    private void PollDismiss()
    {
        if (!_renaming || _renameEnding) return;

        var left = (DesktopNative.GetAsyncKeyState(0x01) & 0x8000) != 0;
        var right = (DesktopNative.GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (!left && !right) return;

        var h = Host?.Handle ?? nint.Zero;
        if (h == nint.Zero) return;
        if (!DesktopNative.GetCursorPos(out var pt) || !DesktopNative.GetWindowRect(h, out var rect)) return;

        const int pad = 4;
        var inside = pt.X >= rect.Left - pad && pt.X <= rect.Right + pad
                     && pt.Y >= rect.Top - pad && pt.Y <= rect.Bottom + pad;
        if (!inside) CommitRename();
    }

    private void OnRootPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (!_renaming || _renameEnding) return;
        if (IsRenameBox(e.Source)) return;
        // 标题栏上的按下必须交给 OnHeaderPressed：那边会「提交改名 + 本次不再折叠」。
        // 如果这里先提交了，_renaming 变 false，冒泡到 OnHeaderPressed 时就当成普通点击，
        // 松手后会再切一次折叠 —— 这正是「改名时点标题会触发收起」的原因。
        if (IsInHeader(e.Source)) return;
        if (IsOnHeaderButton(e.Source)) return;
        CommitRename();
    }

    /// <summary>按下点是否落在标题栏里（含标题栏上的按钮）。</summary>
    private bool IsInHeader(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, Header)) return true;
            if (v is DesktopGroupView) break;
        }
        return false;
    }

    // ================= 右缘宽度拉伸 =================

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Host?.BeginResize();
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

    // ================= 分区菜单 =================

    /// <summary>
    /// ⋯ 菜单：贴桌面窗拿不到键盘焦点，浮层（Flyout）在 HWND_BOTTOM 的宿主上会被压到普通窗口之下，
    /// 所以开一个独立的小窗。这里把按钮的「屏幕矩形」整个传过去，菜单才能判断下方够不够放。
    /// </summary>
    private void OnMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopGroupVm vm) return;
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
