using System;
using System.Collections.Specialized;
using System.ComponentModel;
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

    private readonly DesktopGroupVm _vm;

    private nint _hwnd;
    private DispatcherTimer? _guard;
    private int _guardTicks;
    private DispatcherTimer? _chromeTimer;
    private bool _renameFocus;

    // 拖动 / 拉伸状态
    private bool _dragging;
    private bool _resizing;
    private PixelPoint _dragOrigin;
    private (int X, int Y) _dragCursor;
    private double _resizeStartWidth;

    public DesktopGroupWindow(DesktopGroupVm vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Width = vm.Width;
        Root.Host = this;

        vm.PropertyChanged += OnVmPropertyChanged;
        vm.Devices.CollectionChanged += OnDevicesChanged;
        Closed += OnClosed;
    }

    public DesktopGroupVm ViewModel => _vm;

    /// <summary>窗口句柄（内联重命名时用来判断「点窗外」）。</summary>
    public nint Handle => Hwnd;

    private nint Hwnd => _hwnd != nint.Zero ? _hwnd : TryGetPlatformHandle()?.Handle ?? nint.Zero;

    /// <summary>
    /// 内联重命名：临时去掉 NOACTIVATE 才能拿到键盘焦点（否则输入框点了也打不了字），
    /// 提交后由 <see cref="EndRenameFocus"/> 收回。
    /// </summary>
    public void BeginRenameFocus()
    {
        var h = Hwnd;
        if (h == nint.Zero) return;
        _renameFocus = true;
        DesktopEmbed.SetNoActivate(h, false);
        DesktopNative.SetForegroundWindow(h);
        try { Activate(); } catch { }
    }

    public void EndRenameFocus()
    {
        _renameFocus = false;
        var h = Hwnd;
        if (h == nint.Zero) return;
        DesktopEmbed.SetNoActivate(h, true);
        DesktopEmbed.PinBottom(h);
    }

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

        // 「加设备」可能发生在窗口建好之前（主窗口加号 → 选已有分区，分区窗这时才新建），
        // 等第一次布局跑完（几何可信）再补一次同样的加列判断；已经放得下就什么都不做。
        Dispatcher.UIThread.Post(() => FitIntoDesktop(_vm.Devices.Count, _vm.Devices.Count), DispatcherPriority.Loaded);
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

        if (!_renameFocus) DesktopEmbed.EnsureNoActivate(h);
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
            DesktopEmbed.EnsureNoActivate(h);
            if (!_renameFocus && !_dragging && !_resizing) DesktopEmbed.PinBottom(h);
            if (++_guardTicks >= 24) _guard?.Stop();
        };
        _guard.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Devices.CollectionChanged -= OnDevicesChanged;
        _guard?.Stop();
        _chromeTimer?.Stop();
        if (_hwnd != nint.Zero) DesktopEmbed.ClearAcrylic(_hwnd);
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        // 事件是在下一次布局前同步派发的：此时 Height 还是「加这台之前」的高度，
        // 正好用来推算加完之后的底部会不会顶出桌面（删设备不收窄，尊重用户手拉的宽度）
        var added = e.NewItems?.Count ?? 1;
        FitIntoDesktop(_vm.Devices.Count, _vm.Devices.Count - added);
    }

    /// <summary>
    /// 加设备时优先「加宽一列」而不是继续向下长：分区常摆在桌面下缘，
    /// 往下多的那一行直接被桌沿（或任务栏）挡在屏幕外，用户勾完看不到新设备，只能自己拖宽。
    /// 列数由宫格宽度决定，所以这里是把窗口加宽到能多放一列，行数随之减下来。
    /// </summary>
    /// <param name="newCount">加完之后的设备数。</param>
    /// <param name="oldCount">当前窗口高度对应的设备数（同一批时即「只核对能不能放下」）。</param>
    private void FitIntoDesktop(int newCount, int oldCount)
    {
        if (newCount <= 0 || _vm.Collapsed) return;

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
        var oldRows = (int)Math.Ceiling(oldCount / (double)columns);
        var chrome = Math.Max(ChromeDip, Height - oldRows * rowHeight);

        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var scale = RenderScaling <= 0 ? 1 : RenderScaling;

        var rows = (int)Math.Ceiling(newCount / (double)columns);
        var width = Width;
        while (Position.Y + (chrome + rows * rowHeight) * scale > work.Bottom)
        {
            var nextRows = (int)Math.Ceiling(newCount / (double)(columns + 1));
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

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DesktopGroupVm.Width):
                if (Math.Abs(Width - _vm.Width) > 0.5) Width = _vm.Width;
                ScheduleChrome();
                break;
            case nameof(DesktopGroupVm.Collapsed):
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
