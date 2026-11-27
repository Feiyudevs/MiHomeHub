// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MiHomeHub.Controls;
using MiHomeHub.Services;
using MiHomeHub.ViewModels;
using MiHomeHub.Widgets;

namespace MiHomeHub.Views;

/// <summary>
/// 一张贴桌面的数据卡：无标题栏，整窗即卡；背后挂 Zonex 的 <see cref="GlassBackdropHost"/>
/// 提供 20px 圆角原生磨砂与投影，卡窗本身只画半透明底色与内容。
///
/// 与设备分区共用 <see cref="DesktopHost"/> 的注册表、显隐、多屏落位与外观广播；
/// 拖动/缩放/右键菜单都在本类里，卡面布局见 DataCardView。
/// </summary>
public partial class DataCardWindow : Window, IDesktopWindow, IWidgetWindow
{
    private readonly DataCardVm _vm;

    private nint _hwnd;
    /// <summary>与设备分区共用同一套窗体外观（20px 圆角 + 原生磨砂 + 投影）。</summary>
    private readonly DesktopGlassFrame _frame;
    private DispatcherTimer? _guard;
    private DispatcherTimer? _chromeTimer;
    private DispatcherTimer? _pickHoverTimer;
    private int _guardTicks;
    private bool _closing;

    // 指针是否压在卡面上的原生判断：桌面卡贴在最底层，弹窗关掉后 :pointerover 可能停在错误状态，
    // 用光标坐标兜底，保证「移进卡片就露面、移出就收起」不依赖 Avalonia 的悬停链。
    private bool? _pickHover;

    // WndProc 子类：坐标照改、Z 序不动；顺手把玻璃宿主同帧推过去
    private DesktopNative.WndProcDelegate? _wndProcDelegate;
    private nint _originalWndProc;
    private nint _wndProcPtr;

    // 拖动 / 右下角缩放
    private bool _dragging;
    private bool _resizing;
    private PixelPoint _dragOrigin;
    private (int X, int Y) _dragCursor;
    private WidgetResizeSession? _resizeSession;

    public DataCardWindow(DataCardVm vm)
    {
        InitializeComponent();
        // 窗体外壳统一走 DesktopGlassFrame：设备分区也用同一份，圆角/磨砂/投影不会再各调一套。
        // 必须在设 Width/Height 之前建好：尺寸回调里会同步玻璃宿主。
        _frame = new DesktopGlassFrame(this, Card, () => Hwnd, "data-card", BringToFrontTemporarily);

        _vm = vm;
        DataContext = vm;
        Width = Math.Max(1, vm.Width);
        Height = Math.Max(1, vm.Height);

        // 先包右下手柄，再挂拖动事件：手柄是纯视觉，命中判定由 WidgetResize 的几何热区负责
        WidgetResize.AttachGrip(this, Card, vm.DesignSize);
        Card.AddHandler(PointerPressedEvent, OnSurfacePressed, RoutingStrategies.Tunnel, true);
        Card.AddHandler(PointerMovedEvent, OnSurfaceMoved, RoutingStrategies.Tunnel, true);
        Card.AddHandler(PointerReleasedEvent, OnSurfaceReleased, RoutingStrategies.Tunnel, true);

        vm.PropertyChanged += OnVmPropertyChanged;
        Closed += OnClosed;
        DesktopLog.Write($"[data-card] window constructed metric={vm.MetricKey} size={Width:0}×{Height:0}");

        _pickHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pickHoverTimer.Tick += (_, _) => SyncPickHover();
        _pickHoverTimer.Start();
    }

    public DataCardVm ViewModel => _vm;

    public Window Window => this;

    public WidgetResizeMode ResizeMode => WidgetResizeMode.Reflow;

    public nint Handle => Hwnd;

    public bool IsExiled { get; private set; }

    private nint Hwnd => _hwnd != nint.Zero ? _hwnd : TryGetPlatformHandle()?.Handle ?? nint.Zero;

    /// <summary>先落到保存位置再显示，避免在 (0,0) 闪一下。</summary>
    public override void Show()
    {
        ApplyModelPosition();
        _vm.Refresh();
        base.Show();
    }

    private void ApplyModelPosition()
    {
        var model = _vm.Model;
        var saved = new PixelPoint((int)Math.Round(model.X), (int)Math.Round(model.Y));
        var height = HeightDip();

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
    }

    private void ClampIntoVirtualDesktop(PixelPoint saved)
    {
        var (vx, vy, vw, vh) = DesktopEmbed.VirtualScreen();
        var x = (int)Math.Clamp(saved.X, vx + 8, vx + Math.Max(8, vw - 80));
        var y = (int)Math.Clamp(saved.Y, vy + 8, vy + Math.Max(8, vh - 60));
        Position = new PixelPoint(x, y);
    }

    private double HeightDip()
    {
        var scale = RenderScaling > 0 ? RenderScaling : 1;
        if (_vm.Height > 1) return _vm.Height;
        if (Bounds.Height > 1) return Bounds.Height / scale;
        return _vm.DesignSize.Height;
    }

    public void ReapplyPlacement()
    {
        if (_dragging || _resizing) return;
        ApplyModelPosition();
    }

    public string CurrentScreenSignature() => MonitorLayout.SignatureAt(Position, Width, HeightDip());

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        DesktopLog.Write($"[data-card] OnOpened metric={_vm.MetricKey} pos={Position} size={Width:0}×{Height:0}");
        try
        {
            AttachDesktop();
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"[data-card] attach failed metric={_vm.MetricKey}：{ex}");
            try { _frame.FallbackToAcrylic("attach-failed"); } catch { }
        }
        Dispatcher.UIThread.Post(ReapplyPlacement, DispatcherPriority.Loaded);
    }

    private static readonly bool DebugMode =
        Environment.GetEnvironmentVariable("MIHOMEHUB_DESKTOP_DEBUG") == "1";

    private void AttachDesktop()
    {
        _hwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
        DesktopLog.Write($"[data-card] attach hwnd=0x{_hwnd:X} glassAvail={GlassBackdropHost.Available}");
        if (_hwnd == nint.Zero) return;

        if (DebugMode)
        {
            // 调试口子：不做桌面层，只把窗置顶，方便核对卡面排版与玻璃（正式运行不走这里）
            Topmost = true;
            DesktopEmbed.KillSystemFrame(_hwnd);
            _frame.EnsureHost();
            ApplyChrome();
            return;
        }

        InstallWndProc();

        DesktopEmbed.SetCloaked(_hwnd, true);
        try
        {
            DesktopEmbed.KillSystemFrame(_hwnd);
            DesktopEmbed.Attach(_hwnd);
            _frame.EnsureHost();
            ApplyChrome();

            // 先把卡压到底，再摆玻璃宿主：宿主的锚点就是「卡正下方那张窗」，
            // 顺序反过来的话压底会把卡挤到宿主下面，宿主随后得重钉一次 —— 那一次必定重采背景，
            // 卡已经上屏了，重采当场就是背景闪一下。
            DesktopEmbed.PinBottom(_hwnd);

            // 卡还 cloak 着的时候先把玻璃宿主摆到卡的矩形、采好背景，再把内容压住。
            // 之后解除 cloak + 开内容在同一拍里发生，磨砂和卡面一起上屏；
            // 否则就是「卡先亮一帧（没磨砂）→ 下一帧才糊上」，看起来闪一下。
            if (_frame.UsesNativeGlass) _frame.SeatBehind();

            // 强推一帧（缩 1px 再还原）放在 cloak 期间做：用户看不到这两下，但 Avalonia
            // 确实收到了一次真 WM_SIZE，解除 cloak 时首帧已经画好 —— 不会出现「卡窗先亮成透明洞」。
            DesktopEmbed.ForcePresent(_hwnd);
        }
        finally
        {
            // Z 序已在上面压好，这里只解除 cloak，不再写任何几何
            DesktopEmbed.SetCloaked(_hwnd, false);
        }

        if (_frame.UsesNativeGlass)
        {
            _frame.PrimeForShow();
            if (!_frame.Sync())
            {
                // 刚解除 cloak 时 DWM 属性可能还没同步，先别急着判死：等到下一帧再复查一次
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

        // 首帧已在 cloak 期间推好，这里只开内容、不再动几何
        StartGuard();
    }

    /// <summary>重打圆角 / 描边 / 主题与材质：外观设置改动、尺寸变化、系统重合成后都会调用。</summary>
    public void ApplyChrome() => _frame.ApplyChrome();

    private void ReapplyMaterial(string reason)
    {
        if (_closing || !IsVisible || _dragging || _resizing) return;
        ApplyChrome();
        DesktopEmbed.ForcePresent(Hwnd);
        DesktopLog.Write($"[data-card] 重打材质 reason={reason} hwnd={Hwnd}");
    }

    /// <summary>开机/切层后补打一小段时间：NOACTIVATE 与压底可能被 Avalonia 合成重置。</summary>
    private void StartGuard()
    {
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
            // 只在 Avalonia 真的把 ExStyle 重置了才补压底。每次压底都会把卡挤到玻璃宿主下面，
            // 宿主就得跟着重新落位、DWM 跟着重采一次背景；连补 24 次，看到的就是卡片背景连闪几下。
            var exChanged = DesktopEmbed.EnsureNoActivate(h);
            if (!_dragging && !_resizing && exChanged)
            {
                DesktopEmbed.PinBottom(h);
                _frame.RepinBehindOwner();
                if (_frame.UsesNativeGlass && !_frame.Sync()) _frame.FallbackToAcrylic("guard");
            }

            if (++_guardTicks >= 24)
            {
                _guard?.Stop();
                _guard = null;
            }
        };
        _guard.Start();
    }

    private void InstallWndProc()
    {
        if (_hwnd == nint.Zero || _wndProcDelegate != null) return;

        _wndProcDelegate = WndProc;
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = DesktopNative.GetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC);
        DesktopNative.SetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC, _wndProcPtr);
    }

    private void EnsureWndProc()
    {
        if (_hwnd == nint.Zero || _wndProcDelegate == null) return;
        var current = DesktopNative.GetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC);
        if (current == _wndProcPtr || current == nint.Zero) return;
        _originalWndProc = current;
        DesktopNative.SetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC, _wndProcPtr);
    }

    private nint WndProc(nint hWnd, uint uMsg, nint wParam, nint lParam)
    {
        // 点击绝不激活：卡窗是桌面层的一部分，激活会把整条 Progman owned 窗口组抬到普通软件之上
        if (uMsg == DesktopNative.WM_MOUSEACTIVATE)
            return new nint(DesktopNative.MA_NOACTIVATE);

        // 系统 NC 一律不画，否则会和卡面 20px 圆角叠成双边框
        if (uMsg == 0x0085 /* WM_NCPAINT */) return nint.Zero;
        if (uMsg == 0x0086 /* WM_NCACTIVATE */) return (nint)1;

        // Win+D 拒绝最小化：桌面卡不是普通应用窗口
        if (uMsg == 0x0112 /* WM_SYSCOMMAND */
            && ((int)wParam & 0xFFF0) == 0xF020 /* SC_MINIMIZE */)
            return nint.Zero;

        // 坐标照改、层级不动：Avalonia 写位置/尺寸时那次 SetWindowPos 会把卡片顶到桌面带最上面
        if (uMsg == DesktopNative.WM_WINDOWPOSCHANGING && lParam != nint.Zero)
        {
            _frame.FollowOwnerMove(lParam);
            if (!DesktopEmbed.ZOrderChangeAllowed)
            {
                var wp = Marshal.PtrToStructure<DesktopNative.WINDOWPOS>(lParam);
                wp.flags |= DesktopNative.SWP_NOZORDER | DesktopNative.SWP_NOOWNERZORDER;
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }

        // 宿主必须紧贴卡窗正下方：Avalonia 或系统重排后玻璃可能翻到卡内容上面
        if (uMsg == DesktopNative.WM_WINDOWPOSCHANGED || uMsg == 0x0005 /* WM_SIZE */)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _frame.Sync();
                if (!_dragging && !_resizing) _frame.RepinBehindOwner();
            }, DispatcherPriority.Render);
        }

        if (uMsg == DesktopNative.WM_DWMCOMPOSITIONCHANGED
            || uMsg == DesktopNative.WM_DISPLAYCHANGE
            || uMsg == DesktopEmbed.ShellReadyMessage)
            Dispatcher.UIThread.Post(() => ReapplyMaterial($"msg:0x{uMsg:X}"));

        if (uMsg == DesktopNative.WM_DISPLAYCHANGE || uMsg == DesktopEmbed.ShellReadyMessage)
            Dispatcher.UIThread.Post(DisplayWatcher.Notify);

        return _originalWndProc != nint.Zero
            ? DesktopNative.CallWindowProcW(_originalWndProc, hWnd, uMsg, wParam, lParam)
            : DesktopNative.DefWindowProcW(hWnd, uMsg, wParam, lParam);
    }

    private void RestoreWndProc()
    {
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

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataCardVm.Width) && Math.Abs(Width - _vm.Width) > 0.5)
            Width = _vm.Width;
        else if (e.PropertyName == nameof(DataCardVm.Height) && Math.Abs(Height - _vm.Height) > 0.5)
            Height = _vm.Height;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _frame.Sync();

        // 退回亚克力时圆角 Region 必须随尺寸重裁；原生玻璃只跟随矩形，不必重打
        if (!_frame.UsesNativeGlass && !_closing)
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
    }

    public void ReapplyAppearance()
    {
        InvalidateVisual();
        Card.InvalidateVisual();
        ApplyChrome();
        _frame.Sync();
        DesktopEmbed.NudgePresent(Hwnd);
    }

    /// <summary>被再次唤出时轻微推一帧，让用户看到卡片还在；不抢焦点。</summary>
    public void BringToFrontTemporarily()
    {
        var h = Hwnd;
        if (h == nint.Zero) return;
        DesktopEmbed.PinBottom(h);
        _frame.RepinBehindOwner();
        DesktopEmbed.NudgePresent(h);
    }

    /// <summary>数据卡没有名称浮层，右键菜单也不提供重命名。</summary>
    public void BeginRename() { }

    // ================= 拖动 / 缩放 =================

    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_closing) return;
        var point = e.GetCurrentPoint(this);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            e.Handled = true;
            if (DesktopNative.GetCursorPos(out var p))
                DesktopHost.OpenCardMenu(_vm, new PixelRect(p.X, p.Y, 1, 1));
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        // 桌面卡贴在最底层，下拉按钮自己的 Click 会被本窗的拖动命中链吃掉：
        // 先在卡面坐标里判断落点是否在某个下拉上，命中就直接开合，且不再进入拖动。
        if (PickerAt(e.GetPosition(this)) is { } picker)
        {
            picker.ToggleDropDown();
            e.Handled = true;
            return;
        }
        // 右上角「⋯」同样按几何矩形兜底：命中最深元素退化成整张卡时，
        // 按钮自己的 Click 根本收不到，点下去会变成拖窗。
        if (MoreButtonAt(e.GetPosition(this)) is { } more)
        {
            OpenCardMenuFrom(more);
            e.Handled = true;
            return;
        }
        // 卡面上的时间和来源下拉是独立控件：先让它拿到点击，别把整张卡当成拖动面。
        if (IsInteractiveSource(e.Source as Visual)) return;

        if (WidgetResize.IsInGrip(this, e.GetPosition(this)))
            BeginResize();
        else
            BeginDrag();

        e.Pointer.Capture(Card);
        e.Handled = true;
    }

    /// <summary>指针是否压在卡面上：用原生光标与窗口矩形对账，不读可能失真的 <c>:pointerover</c>。</summary>
    private void SyncPickHover()
    {
        if (_closing) return;

        var inside = false;
        if (IsVisible)
        {
            var h = Hwnd;
            if (h != nint.Zero
                && DesktopNative.GetCursorPos(out var cursor)
                && DesktopNative.GetWindowRect(h, out var rect))
            {
                inside = cursor.X >= rect.Left && cursor.X < rect.Right
                    && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
            }
        }

        if (_pickHover == inside) return;
        _pickHover = inside;
        Card.Classes.Set("pick-hover", inside);
    }

    /// <summary>卡面坐标命中的下拉：按每个下拉的可见矩形算，不依赖 PointerEvent.Source。
    /// 桌面卡从 Progman 子窗接事件，命中最深元素有时会退化成整张卡。</summary>
    private OptionPicker? PickerAt(Point point)
    {
        foreach (var picker in Card.GetVisualDescendants().OfType<OptionPicker>())
        {
            if (!picker.IsVisible) continue;
            var origin = picker.TranslatePoint(new Point(0, 0), this);
            if (origin is null) continue;
            if (new Rect(origin.Value, picker.Bounds.Size).Contains(point)) return picker;
        }
        return null;
    }

    /// <summary>卡面坐标命中的「⋯」菜单键：和下拉同一套几何判定，大小卡各一枚，
    /// 藏起来的那一枚 Bounds 为零，不会误命中。</summary>
    private Button? MoreButtonAt(Point point)
    {
        foreach (var button in Card.GetVisualDescendants().OfType<Button>())
        {
            if (!button.Classes.Contains("CardMore") || !button.IsEffectivelyVisible) continue;
            var origin = button.TranslatePoint(new Point(0, 0), this);
            if (origin is null) continue;
            if (new Rect(origin.Value, button.Bounds.Size).Contains(point)) return button;
        }
        return null;
    }

    /// <summary>把菜单钉到「⋯」按钮上：菜单贴按钮下沿、右缘对齐。</summary>
    private void OpenCardMenuFrom(Control anchor)
    {
        var topLeft = anchor.PointToScreen(new Point(0, 0));
        var bottomRight = anchor.PointToScreen(new Point(anchor.Bounds.Width, anchor.Bounds.Height));
        DesktopHost.OpenCardMenu(_vm, new PixelRect(
            topLeft.X, topLeft.Y,
            Math.Max(1, bottomRight.X - topLeft.X),
            Math.Max(1, bottomRight.Y - topLeft.Y)));
    }

    /// <summary>点击落在 OptionPicker / 按钮上时返回 true；否则整张卡仍是拖动面。</summary>
    private static bool IsInteractiveSource(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is OptionPicker or Button or ToggleButton) return true;
            if (visual is DataCardWindow) break;
        }
        return false;
    }

    private void OnSurfaceMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging)
        {
            UpdateDrag();
            e.Handled = true;
            return;
        }
        if (_resizing)
        {
            _resizeSession?.Apply();
            e.Handled = true;
            return;
        }
        WidgetResize.UpdateCursor(this, e.GetPosition(this));
        WidgetResize.SyncHover(this);
    }

    private void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging) EndDrag();
        if (_resizing) EndResize();
        e.Pointer.Capture(null);
        WidgetResize.SyncHover(this);
    }

    public void BeginDrag()
    {
        if (!DesktopNative.GetCursorPos(out var p)) return;
        _dragging = true;
        _dragCursor = (p.X, p.Y);
        _dragOrigin = Position;
        // Win10 上先摘掉磨砂/投影，拖动期间只有卡窗跟手（松手在 EndDrag 补回来）
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

    public void BeginResize()
    {
        if (_resizing) return;
        _resizing = true;
        _frame.BeginDragLite();
        _resizeSession = WidgetResizeSession.Begin(this, _vm.DesignSize, () =>
        {
            _vm.Width = Width;
            _vm.Height = Height;
            _frame.Sync();
        }, _vm.MinCells);
    }

    public void EndResize()
    {
        if (!_resizing) return;
        _resizing = false;

        // Commit 只负责把手柄会话收尾与尺寸归一化（写进临时字典后丢弃）；
        // 真值直接落在模型的 Width/Height 上，desktop.json 不引入 widgets.json 那套 w/h 键
        _resizeSession?.Commit(new Dictionary<string, string>());
        _resizeSession = null;

        _vm.Width = Math.Round(Width);
        _vm.Height = Math.Round(Height);
        _frame.EndDragLite();
        ApplyChrome();
        SavePosition();
    }

    private void SavePosition()
    {
        _vm.Model.X = Position.X;
        _vm.Model.Y = Position.Y;
        var sig = CurrentScreenSignature();
        if (sig.Length > 0) _vm.Model.ScreenSig = sig;
        IsExiled = false;
        DesktopHost.PersistGroup(_vm);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _guard?.Stop();
        _guard = null;
        _pickHoverTimer?.Stop();
        _pickHoverTimer = null;
        _chromeTimer?.Stop();
        _chromeTimer = null;
        RestoreWndProc();

        try { _frame.Dispose(); } catch { }

        if (_hwnd != nint.Zero)
        {
            try { DesktopEmbed.ClearAcrylic(_hwnd); } catch { }
        }
    }
}
