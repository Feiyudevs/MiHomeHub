using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 分区独立顶层小窗：贴桌面（owner=Progman）+ 压底 + NOACTIVATE + 窗口级亚克力。
/// 位置/尺寸由 View 里的拖动与右缘拉伸改，落位后写回模型持久化。
/// </summary>
public partial class DesktopGroupWindow : Window
{
    /// <summary>卡片圆角（DIP）。必须与 DWM 圆角半径一致（8），否则角部会露出系统裁切的硬边。</summary>
    public const int Corner = 8;

    /// <summary>
    /// 一行的推算高度（DIP）：设备卡 MinHeight 74 + 卡片外边距 8×2。
    /// 只在宫格还没布局过（空分区加第一台）时兜底，正常情况用实测的卡片高度。
    /// </summary>
    private const double CardRowDip = 90;

    /// <summary>宫格之外的固定开销（DIP）：标题栏 38 + 卡片描边 2 + 内容底部留白 6。</summary>
    private const double ChromeDip = 46;

    private readonly DesktopZoneVm _vm;

    /// <summary>设备分区才有「加设备就加宽一列」那套推算，媒体分区走不到。</summary>
    private DesktopGroupVm? _group;

    private nint _hwnd;
    private DispatcherTimer? _guard;
    private int _guardTicks;
    private DispatcherTimer? _chromeTimer;

    // WndProc 子类：压掉一切非本意的 Z 序变化（Avalonia 写位置/尺寸的 SetWindowPos 不带 NOZORDER）
    private DesktopNative.WndProcDelegate? _wndProcDelegate;
    private nint _originalWndProc;
    private nint _wndProcPtr;

    // 拖动 / 拉伸状态
    private bool _dragging;
    private bool _resizing;
    private PixelPoint _dragOrigin;
    private (int X, int Y) _dragCursor;
    private double _resizeStartWidth;

    public DesktopGroupWindow(DesktopZoneVm vm)
    {
        InitializeComponent();
        _vm = vm;
        _group = vm as DesktopGroupVm;
        DataContext = vm;
        Width = vm.Width;
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

    /// <summary>窗口句柄（贴桌面层级操作时使用）。</summary>
    public nint Handle => Hwnd;

    private nint Hwnd => _hwnd != nint.Zero ? _hwnd : TryGetPlatformHandle()?.Handle ?? nint.Zero;

    /// <summary>先落到保存的位置再显示，避免在 (0,0) 闪一下。</summary>
    public override void Show()
    {
        ApplyModelPosition();
        base.Show();
    }

    private void ApplyModelPosition()
    {
        var (vx, vy, vw, vh) = DesktopEmbed.VirtualScreen();
        var x = (int)Math.Clamp(_vm.Model.X, vx + 8, vx + Math.Max(8, vw - 80));
        var y = (int)Math.Clamp(_vm.Model.Y, vy + 8, vy + Math.Max(8, vh - 60));
        Position = new PixelPoint(x, y);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        AttachDesktop();

        if (_group != null)
            Dispatcher.UIThread.Post(
                () => FitIntoDesktop(_group.Devices.Count, _group.Scenes.Count,
                                     _group.Devices.Count, _group.Scenes.Count),
                DispatcherPriority.Loaded);
    }

    private void AttachDesktop()
    {
        _hwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (_hwnd == nint.Zero) return;

        // 调试口子：MIHOME_DESKTOP_DEBUG=1 时不贴桌面、改为置顶普通窗口，
        // 方便在其它窗口之上肉眼核对卡片排版/圆角/磨砂（正式运行时环境变量不存在，走正常贴桌面路径）。
        if (DebugMode)
        {
            Topmost = true;
            DesktopEmbed.KillSystemFrame(_hwnd);
            ApplyChrome();
            return;
        }

        // 必须先装：之后每一次坐标/尺寸写入都不准动 Z 序
        InstallWndProc();

        // 贴桌面期间全程 cloak：改样式/打磨砂都会闪，用户看不到中间态
        DesktopEmbed.SetCloaked(_hwnd, true);
        try
        {
            DesktopEmbed.KillSystemFrame(_hwnd);
            DesktopEmbed.Attach(_hwnd);
            ApplyChrome();
        }
        finally
        {
            DesktopEmbed.SetCloaked(_hwnd, false);
        }

        DesktopEmbed.PinBottom(_hwnd);
        DesktopEmbed.ForcePresent(_hwnd);
        StartGuard();
    }

    private static readonly bool DebugMode =
        Environment.GetEnvironmentVariable("MIHOME_DESKTOP_DEBUG") == "1";

    /// <summary>重打圆角 + 磨砂 + 桌面层级（外观变更、尺寸变化后调用）。</summary>
    public void ApplyChrome()
    {
        var h = Hwnd;
        if (h == nint.Zero) return;

        DesktopEmbed.EnsureNoActivate(h);
        DesktopEmbed.StripNoRedirection(h);

        // 材质先打
        var look = DesktopHost.Look;
        if (look.Glass)
            DesktopEmbed.ApplyAcrylic(h, DesktopPalette.BaseColor(look), DesktopPalette.TintOpacity(look));
        else
            DesktopEmbed.ClearAcrylic(h);

        // 形状最后打（改 NC/材质会重建合成 backdrop，先打会被冲掉）：
        // 圆角必须交给 DWM —— 窗口级 ACCENT 磨砂是 DWM 铺满整个窗口矩形的，SetWindowRgn 只能裁
        // Avalonia 的内容，裁不住那层模糊（实测 RGN 明明设上了，四角依旧方），
        // 只有 DWM 的窗口形状会连磨砂一起收角。
        // 代价是 DWM 会给圆角窗口补一层很淡的系统阴影（约 8px），比 thickframe 那套 20px 干净得多；
        // NC 渲染/描边/边框仍在 SuppressSystemShadow 里关死，不留灰边。
        DesktopEmbed.SuppressSystemShadow(h);
        if (!DesktopEmbed.RoundByDwm(h))
            DesktopEmbed.RoundCorners(h, Corner);   // Win10 无 DWMWCP_ROUND：回退 RGN（磨砂角部会方，accept）
        else
            DesktopEmbed.RoundCorners(h, 0);        // DWM 收角成功：Region 归零，避免双弧
    }

    /// <summary>Avalonia 合成会反复重置 ExStyle，NOACTIVATE 掉了就会点一下把整个桌面窗口集抬到前台。</summary>
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
            DesktopEmbed.EnsureNoActivate(h);
            if (!_dragging && !_resizing) DesktopEmbed.PinBottom(h);
            if (++_guardTicks >= 24) _guard?.Stop();
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
        if (_hwnd == nint.Zero || _wndProcDelegate != null) return;

        _wndProcDelegate = WndProc;
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = DesktopNative.GetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC);
        DesktopNative.SetWindowLongPtr(_hwnd, DesktopNative.GWL_WNDPROC, _wndProcPtr);
    }

    /// <summary>Avalonia 若重装过 WndProc，原样接回去继续压 Z 序，避免链到自己无限递归。</summary>
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
        if (uMsg == DesktopNative.WM_MOUSEACTIVATE)
            return new nint(DesktopNative.MA_NOACTIVATE);

        if (uMsg == DesktopNative.WM_WINDOWPOSCHANGING && !DesktopEmbed.ZOrderChangeAllowed)
        {
            var wp = Marshal.PtrToStructure<DesktopNative.WINDOWPOS>(lParam);
            var next = wp.flags | DesktopNative.SWP_NOZORDER | DesktopNative.SWP_NOOWNERZORDER;
            if (next != wp.flags)
            {
                wp.flags = next;
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }

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

    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        if (_group != null)
        {
            _group.Devices.CollectionChanged -= OnDevicesChanged;
            _group.Scenes.CollectionChanged -= OnDevicesChanged;
        }
        _guard?.Stop();
        _chromeTimer?.Stop();
        RestoreWndProc();
        if (_hwnd != nint.Zero) DesktopEmbed.ClearAcrylic(_hwnd);
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
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
                ScheduleChrome();
                break;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
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
        SavePosition();
    }

    public void BeginResize()
    {
        if (!DesktopNative.GetCursorPos(out var p)) return;
        _resizing = true;
        _dragCursor = (p.X, p.Y);
        _resizeStartWidth = Width;
    }

    public void UpdateResize()
    {
        if (!_resizing || !DesktopNative.GetCursorPos(out var p)) return;
        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        var delta = (p.X - _dragCursor.X) / scale;
        var next = Math.Clamp(_resizeStartWidth + delta,
            Models.DesktopGroupModel.MinWidth, Models.DesktopGroupModel.MaxWidth);
        if (Math.Abs(Width - next) < 0.5) return;
        Width = next;
    }

    public void EndResize()
    {
        if (!_resizing) return;
        _resizing = false;
        _vm.Width = Math.Round(Width);
        SavePosition();
    }

    private void SavePosition()
    {
        _vm.Model.X = Position.X;
        _vm.Model.Y = Position.Y;
        DesktopHost.PersistGroup(_vm);
    }

    /// <summary>已存在的分区被再次「加号」唤出时闪一下，提示它在哪。</summary>
    public void BringToFrontTemporarily() => Root.Flash();

    /// <summary>从菜单「重命名」触发：复用标题栏的内联编辑（与双击名称同一套逻辑）。</summary>
    public void BeginRename() => Root.BeginRename();
}
