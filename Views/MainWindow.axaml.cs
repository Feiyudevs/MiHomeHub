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
using System.Threading.Tasks;
using System.Runtime;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

public partial class MainWindow : Window
{
    // 上次运行时保存的位置与大小，等到窗口显示后再生效
    private WindowPlacementStore.Placement? _savedPlacement;

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

    // 空闲自动回收：记录最近交互时间，窗口安静超阈值后整理内存
    private DateTimeOffset _lastInteraction = DateTimeOffset.UtcNow;
    private DispatcherTimer? _idleTrimTimer;
    private DispatcherTimer? _gpuReleaseTimer;
    private const double IdleTrimSeconds = 30;

    public MainWindow()
    {
        InitializeComponent();
        // 任务栏图标要在建任务栏按钮之前就位，否则会永久空白
        WindowIconHelper.Apply(this);
        _normalSize = new Size(Width, Height);
        RestorePlacement();

        // 点击搜索胶囊以外的任意位置 → 收起搜索
        AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);

        // DataContext 在窗口构造后由 App 注入，这里订阅属性变化来驱动抽屉滑入/滑出动画
        DataContextChanged += OnDataContextChanged;

        PositionChanged += (_, _) => QueueCaptureNormalBounds();
        SizeChanged += (_, _) => QueueCaptureNormalBounds();
        Opened += (_, _) => ApplySavedPlacement();
        // 拖动结束的写盘挂在定时器上，关闭时补一次，避免最后一次变化随静默期丢掉
        Closing += OnClosing;

        // 空闲自动回收：窗口开着但用户不操作时，定期整理回收缩工作集
        InitIdleMemoryTrimmer();
    }

    // ======== 特效测试开关：定位内存占用用 ========
    // 点击标题栏「特效」按钮：关闭所有 BoxShadow + 隐藏环境光晕，并强制 GC；
    // 再点恢复。用于对比开关前后进程内存，判断阴影/光晕是否是内存大头。
    private bool _fxOff;
    private readonly Dictionary<Border, BoxShadows> _savedShadows = new();

    private void OnToggleFxClick(object? sender, RoutedEventArgs e)
    {
        _fxOff = !_fxOff;
        if (_fxOff)
            DisableEffects();
        else
            RestoreEffects();

        // 虚拟化滚动新 realize 的卡片会重新带上样式阴影，延迟再兜底扫两轮
        Dispatcher.UIThread.Post(ReapplyIfOff, DispatcherPriority.Background);
        Dispatcher.UIThread.Post(ReapplyIfOff, DispatcherPriority.Background);

        ForceGc();

        ToolTip.SetTip(FxBadge, _fxOff
            ? "特效已关闭（阴影/光晕），点击恢复"
            : "测试：关闭所有阴影/光晕并强制回收内存（再点恢复）");
        FxBadge.Opacity = _fxOff ? 0.45 : 1.0;
    }

    private void ReapplyIfOff()
    {
        if (_fxOff) DisableEffects();
    }

    private void DisableEffects()
    {
        foreach (var border in this.GetVisualDescendants().OfType<Border>())
        {
            var shadow = border.BoxShadow;
            if (shadow.Count == 0)
                continue;
            if (!_savedShadows.ContainsKey(border))
                _savedShadows[border] = shadow;
            border.BoxShadow = default;
        }

        if (AmbientGlow is not null)
            AmbientGlow.IsVisible = false;
    }

    private void RestoreEffects()
    {
        foreach (var kv in _savedShadows)
            kv.Key.BoxShadow = kv.Value;
        _savedShadows.Clear();

        if (AmbientGlow is not null)
            AmbientGlow.IsVisible = true;
    }

    private static void ForceGc()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    // 托盘「退出」才真正结束进程；点关闭按钮只是收进托盘
    private bool _allowExit;

    public void ExitApplication()
    {
        _allowExit = true;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        Persist();
        if (_allowExit) return;
        // 关闭即退出：托盘菜单窗口还挂在 lifetime 上，只关主窗不会结束进程，统一走退出
        if (!AppSettingsStore.CloseToTray)
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(ExitApplication);
            return;
        }
        e.Cancel = true;
        Hide();
        // 收进托盘后回收内存：窗口已隐藏，立即收缩工作集，降低常驻占用
        Services.MemoryTrimmer.TrimForce();
        // 安静几秒后无感回收 GPU 纹理（见下方 ScheduleGpuResourceRelease）
        ScheduleGpuResourceRelease();
    }

    // ======== 空闲自动回收（参考 Feido InitIdleMemoryTrimmer） ========
    /// <summary>
    /// 空闲自动回收内存：hover、点按、滚轮、按键等交互会触发 JIT 与临时对象分配，
    /// 使内存逐步攀升且 GC 不会主动回收；窗口内无交互超过阈值后自动整理，让占用回落。
    /// </summary>
    private void InitIdleMemoryTrimmer()
    {
        void MarkInteraction(object? s, RoutedEventArgs e) => _lastInteraction = DateTimeOffset.UtcNow;
        AddHandler(InputElement.PointerMovedEvent, MarkInteraction, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerPressedEvent, MarkInteraction, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerWheelChangedEvent, MarkInteraction, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyDownEvent, MarkInteraction, RoutingStrategies.Tunnel);

        _idleTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _idleTrimTimer.Tick += (s, e) =>
        {
            var idleSeconds = (DateTimeOffset.UtcNow - _lastInteraction).TotalSeconds;
            if (idleSeconds >= IdleTrimSeconds && IsVisible)
                Services.MemoryTrimmer.Trim();
        };
        _idleTrimTimer.Start();
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
        try
        {
            var prevOpacity = Opacity;
            var prevTaskbar = ShowInTaskbar;
            Opacity = 0;
            ShowInTaskbar = false;
            Show();
            InvalidateVisual();
            Hide();
            Opacity = prevOpacity;
            ShowInTaskbar = prevTaskbar;
        }
        catch
        {
            // 无感回收失败不影响托盘常驻
        }
    }

    /// <summary>尺寸可在显示前设置；位置需等窗口真正显示后再设，见 <see cref="ApplySavedPlacement"/>。</summary>
    private void RestorePlacement()
    {
        _savedPlacement = WindowPlacementStore.Load();
        if (_savedPlacement is not { } saved) return;

        Width = Math.Max(saved.Width, MinWidth);
        Height = Math.Max(saved.Height, MinHeight);
        _normalSize = new Size(Width, Height);
    }

    private void ApplySavedPlacement()
    {
        if (_savedPlacement is { } saved)
        {
            var position = new PixelPoint(saved.X, saved.Y);
            // 显示器变更后旧坐标可能已在屏幕外，此时只沿用尺寸
            if (IsVisibleOnSomeScreen(position)) Position = position;
        }
        CaptureNormalBounds();
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
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;

        // 还有没落盘的普通边界就补上，最大化本身不记录；此后到退出最大化前不再写
        if (_settleTimer is not null) Persist();
        _isMaximized = true;
        _maximizedPosition = work.Position;
        _maximizedSize = new Size(work.Width / scaling, work.Height / scaling);
        Position = _maximizedPosition;
        Width = _maximizedSize.Width;
        Height = _maximizedSize.Height;
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
        _isMaximized = false;
        if (!_hasNormalBounds) return;
        Position = _normalPosition;
        Width = _normalSize.Width;
        Height = _normalSize.Height;
    }

    /// <summary>系统（Win+↑、贴边）发起的最大化同样会溢出屏幕，转成自己实现的铺满工作区。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            // 最小化到任务栏：窗口已不可见，立即收缩工作集，降低常驻占用
            if (WindowState == WindowState.Minimized)
            {
                Services.MemoryTrimmer.TrimForce();
                return;
            }
            if (WindowState != WindowState.Maximized) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (WindowState != WindowState.Maximized) return;
                WindowState = WindowState.Normal;
                MaximizeToWorkArea();
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>判断窗口左上角落在该坐标时，是否仍有足够区域（标题栏）处于某块屏幕的工作区内。</summary>
    private bool IsVisibleOnSomeScreen(PixelPoint position)
    {
        var scaling = Screens.ScreenFromPoint(position)?.Scaling ?? Screens.Primary?.Scaling ?? 1;
        var rect = new PixelRect(position, PixelSize.FromSize(_normalSize, scaling));
        foreach (var screen in Screens.All)
        {
            var area = screen.WorkingArea;
            int overlapX = Math.Min(rect.Right, area.Right) - Math.Max(rect.X, area.X);
            int overlapY = Math.Min(rect.Bottom, area.Bottom) - Math.Max(rect.Y, area.Y);
            if (overlapX >= 160 && overlapY >= 48) return true;
        }
        return false;
    }

    /// <summary>系统最大化时尺寸变化早于 WindowState 更新，延后一拍再取，避免把最大化边界记成普通边界。</summary>
    private void QueueCaptureNormalBounds() =>
        Dispatcher.UIThread.Post(CaptureNormalBounds, DispatcherPriority.Background);

    private void CaptureNormalBounds()
    {
        if (WindowState != WindowState.Normal) return;
        if (_isMaximized && IsOnMaximizedBounds()) return;
        _isMaximized = false;
        if (double.IsNaN(Width) || Width <= 0 || double.IsNaN(Height) || Height <= 0) return;
        _normalPosition = Position;
        _normalSize = new Size(Width, Height);
        _hasNormalBounds = true;
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
        WindowPlacementStore.Save(_normalPosition.X, _normalPosition.Y,
            _normalSize.Width, _normalSize.Height);
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

    private void ToggleMaximize()
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
        if (DataContext is MainViewModel vm)
        {
            // 抽屉可见性不再走 XAML 绑定，改由动画驱动：打开先显示再滑入，关闭先滑出再隐藏
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsDrawerOpen))
                    Dispatcher.UIThread.Post(() => AnimateDeviceDrawer(vm.IsDrawerOpen));
                if (args.PropertyName == nameof(MainViewModel.IsVoiceOpen))
                {
                    if (vm.IsVoiceOpen) AlignVoicePanel();
                    Dispatcher.UIThread.Post(() => VoicePanelView?.Animate(vm.IsVoiceOpen));
                }
            };
        }
    }

    /// <summary>
    /// 小爱浮层左沿对齐唤出的胶囊按钮；靠右放不下时夹在窗口右边距内。
    /// </summary>
    private void AlignVoicePanel()
    {
        if (VoicePanelView == null || XiaoAiBtn == null) return;
        var pt = XiaoAiBtn.TranslatePoint(new Point(0, 0), VoicePanelView);
        if (pt is null) return;

        const double panelWidth = 344;
        const double edge = 12;
        var maxLeft = Bounds.Width - edge - panelWidth;
        VoicePanelView.AlignToButton(Math.Clamp(pt.Value.X, edge, Math.Max(edge, maxLeft)));
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
