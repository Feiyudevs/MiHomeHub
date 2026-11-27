// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MiHomeHub.Services;
using MiHomeHub.Services.Setup;
using MiHomeHub.ViewModels;
using MiHomeHub.Views;
using System.Diagnostics;

namespace MiHomeHub;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// 拖滑块期间把滚动条钉成可见：指针拖出窗口会让 Window:pointerover 失效，把手会中途消失。
    /// 处理挂的是窗口，sender 是窗口本身，得沿 args.Source（真正被拖的 Thumb）往上找 ScrollBar
    private static void OnThumbDragStarted(object? sender, VectorEventArgs args) => KeepVisibleWhile(args.Source, true);
    private static void OnThumbDragCompleted(object? sender, VectorEventArgs args) => KeepVisibleWhile(args.Source, false);

    private static void KeepVisibleWhile(object? source, bool dragging)
    {
        var bar = (source as Visual)?.GetVisualAncestors().OfType<ScrollBar>().FirstOrDefault();
        if (bar is null) return;
        if (dragging) bar.SetValue(Visual.OpacityProperty, 1d);
        else bar.ClearValue(Visual.OpacityProperty);
    }


    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = ThemeStore.CurrentVariant;
        // 未处理异常的落盘兜底：没有它，macOS 上一次未捕获异常只会变成一条
        // 「Abort trap: 6」崩溃报告，栈里全是没符号的托管地址，事后查不出原因。
        InstallCrashLog();
        // 全局 ToolTip 形状：单行胶囊、换行改小圆角矩形（见 Controls/ToolTipShape.cs）
        Controls.ToolTipShape.Install();

        // 窗口尺寸自检（--self-test）：开一个真窗口跑「放大 / 还原」回归，不进正常启动流程
        if (Array.Exists(Environment.GetCommandLineArgs(),
                argument => string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.UIThread.Post(async () =>
            {
                var code = 2;
                try
                {
                    code = await Services.WindowSelfTest.RunAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[self-test] EXCEPTION: " + ex);
                }
                Environment.Exit(code);
            }, DispatcherPriority.Background);
            base.OnFrameworkInitializationCompleted();
            return;
        }
        // 卡片投影偏好可能在启动前就是关闭状态，先落到资源再建窗口，避免首帧闪一下投影
        CardShadowScope.Apply(AppSettingsStore.CardShadow);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 安装/卸载/预览是独立任务：只建安装窗口，不碰单实例之外的托盘、桌面组件和主界面。
            // （--silent 的无界面安装已在 Program.Main 里提前处理，不会走到这里）
            if (SetupEntry.Active)
            {
                desktop.MainWindow = new SetupWindow(SetupEntry.Options ?? new SetupOptions());
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // 升级/换目录后 Run 键里可能还是旧 exe 路径
            StartupRegistration.Sync();

            // 静默启动只创建窗口句柄供托盘使用，不构建完整主界面。
            // --show 是「立即重启」带过来的：主动重启必须看到主界面，否则用户会以为程序没起来。
            var forceShow = Array.Exists(Environment.GetCommandLineArgs(),
                argument => string.Equals(argument, "--show", StringComparison.OrdinalIgnoreCase));
            var silent = !AppSettingsStore.ShowMainWindowOnStartup && !forceShow;
            var window = new MainWindow(deferInitialContent: silent)
            {
                DataContext = new MainViewModel()
            };
            if (silent)
            {
                window.Opacity = 0;
                window.ShowInTaskbar = false;
            }
            desktop.MainWindow = window;

            // 系统级退出（macOS：Dock 图标右键 → 退出 / ⌘Q）走的是 lifetime 的 ShutdownRequested，
            // 随后窗口会被程序化关闭。默认的「关窗收进托盘」会把这次关闭 cancel 掉，
            // 于是系统收到的是「用户取消」(-128)，表现为点退出毫无反应、只能强杀。
            // 这里在真正退出前先放行，让关闭流程走完。
            desktop.ShutdownRequested += (_, _) => window.AllowSystemShutdown();

            // 桌面组件：读上次的分区布局，开着的分区直接贴回桌面（主窗口收起进托盘也照样显示）
            Services.DesktopHost.Initialize((MainViewModel)window.DataContext);
            if (window.DataContext is MainViewModel mainVm)
            {
                mainVm.DesktopWidgetsEnabled = Services.DesktopHost.Enabled;
                // 「关于」页更新要用：安装器替换目录前必须先真正退出本进程
                mainVm.RequestExit = window.ExitApplication;
            }
            // 可选匿名统计：默认关闭，只有用户在设置里开启后才会有网络请求
            Services.Telemetry.TelemetryService.Init();
            // 启动期不做阻塞式内存整理：窗口刚打开就做完整压缩 GC + 收缩工作集，页面会被系统
            // 立刻换出，紧接着首帧渲染又全部换入（实测启动后几秒硬缺页 300~700 次/秒），
            // 这段时间整机跟着发虚。延后到首屏稳定再回收，占用峰值只高一小会儿。
            Services.MemoryTrimmer.TrimDelayed(6000);
            desktop.MainWindow.AddHandler(Thumb.DragStartedEvent, OnThumbDragStarted,
                RoutingStrategies.Bubble, handledEventsToo: true);
            desktop.MainWindow.AddHandler(Thumb.DragCompletedEvent, OnThumbDragCompleted,
                RoutingStrategies.Bubble, handledEventsToo: true);
            SetupTray(window, desktop, silent);

            // 单实例：重复启动的进程会发信号过来，弹出主界面
            Services.SingleInstance.BeginWatch(() => ShowMainWindow(window));

            // macOS：点 Dock 图标要把收进菜单栏的主界面叫回来。
            // Windows 上点任务栏是系统管的（最小化会还原），macOS 没有对应机制：
            // 应用被激活而主窗口是收起状态时，把它显示出来。
            if (OperatingSystem.IsMacOS())
                StartMacActivationWatch(window);

            // macOS：点 Dock 图标 = 系统发 reopen 事件（Avalonia 的 lifetime 把它转成
            // Activated(ActivationKind.Reopen)）。窗口最小化后 IsVisible 仍是 true，
            // 所以上面那条「前台上升沿」的看门狗看不见它，点了 Dock 没反应就是这个原因。
            // 这里单独接住 reopen：原生 deminiaturize（窗口隐藏时后端不会推状态给 NSWindow）
            // + 显示 + 激活。
            //
            // 注意：ClassicDesktopStyleApplicationLifetime 并不实现 IActivatableLifetime
            // （反射实测：它的接口只有 IClassicDesktopStyleApplicationLifetime /
            // IControlledApplicationLifetime / IApplicationLifetime / IDisposable），
            // 所以 `desktop is IActivatableLifetime` 永远是 false，订阅从未发生 —— 这就是
            // 「点 Dock 没反应」的真正原因。承载事件的是 Avalonia.Native 的
            // MacOSActivatableLifetime，要通过 Application.TryGetFeature 拿。
            if (OperatingSystem.IsMacOS()
                && Avalonia.Application.Current?.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind != ActivationKind.Reopen) return;
                    Dispatcher.UIThread.Post(() => RestoreMainWindow(window));
                };

            // 全局低层输入钩子（鼠标双击桌面 / 全局快捷键）推迟到界面彻底空闲再装：
            // 钩子回调跑在本进程线程，系统会让每一次鼠标、按键都先等它返回；启动那几秒
            // UI 线程还在建窗口、贴桌面，钩子早挂一秒就等于整机输入卡一秒（用户感受就是
            // 「一启动电脑就卡一下」）。晚挂的代价只是快捷键在开头一两秒内不响应。
            Dispatcher.UIThread.Post(() => InstallInputHooks(window), DispatcherPriority.ApplicationIdle);

            // macOS：桌面分区卡片的显隐快捷键（Carbon 注册，不需要辅助功能权限）
            if (OperatingSystem.IsMacOS())
                InstallDesktopShortcut();


            // 预热默认字体：强制创建并缓存 GlyphTypeface，避免运行时每排版一次就新建一份
            // 导致 gen2 老生代累积大量字体对象（实测可达 100 MB+）
            try
            {
                var defaultFont = Resources["DefaultFontFamily"] as FontFamily;
                if (defaultFont is not null)
                    _ = new Typeface(defaultFont);
            }
            catch
            {
                // 字体预热失败不影响启动
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private TrayIconHost? _trayIcon;
    private TrayMenuWindow? _trayMenu;

    /// <summary>
    /// 托盘图标没建起来（macOS 上状态栏图标创建失败）。这时「关闭时收进托盘」会把应用
    /// 藏起来且无处找回，所以关窗按钮改成直接退出。
    /// </summary>
    internal static bool TrayUnavailable { get; private set; }

    /// <summary>界面空闲后安装两个低层输入钩子，并把真实的安装结果同步给「快捷键」页。</summary>
    private static void InstallInputHooks(MainWindow window)
    {
        Services.DesktopDoubleClickHook.Install();
        Services.GlobalHotkeyService.InstallHook();
        if (window.DataContext is MainViewModel vm) vm.SyncHotkeyHookState();
    }

    /// <summary>
    /// macOS：注册「显示 / 隐藏桌面分区卡片」的全局快捷键。
    ///
    /// <para>
    /// 桌面双击在 macOS 上做不了：监听双击要用 CGEventTap，那需要「辅助功能」授权，
    /// 而本项目自签分发、每次重编译签名都变 → 系统把新程序当成另一个应用 → 授权被重置。
    /// 改用 Carbon 的 <c>RegisterEventHotKey</c>：系统级全局热键，<b>不需要任何授权</b>，
    /// 组合固定（设置页可从预设里挑）。
    /// </para>
    /// </summary>
    private static void InstallDesktopShortcut()
    {
        Services.MacOS.MacOsCarbonHotkey.Pressed += combo =>
        {
            try
            {
                Services.DesktopLog.Write($"macOS 桌面组件快捷键触发 combo={combo}");
                Services.DesktopHost.ToggleFromTray();
            }
            catch (Exception ex)
            {
                Services.DesktopLog.Write($"桌面组件快捷键处理失败: {ex.GetType().Name}: {ex.Message}");
            }
        };

        var combo = Services.AppSettingsStore.DesktopToggleHotkey;
        if (string.IsNullOrWhiteSpace(combo)) return;
        if (!Services.MacOS.MacOsCarbonHotkey.TryRegister(combo))
            Services.DesktopLog.Write($"桌面组件快捷键注册失败 combo={combo}（可能已被其它程序占用）");
    }

    /// <summary>托盘菜单打开的时刻：这段时间内不做「激活就弹窗」，免得用户点菜单时窗口自己冒出来。</summary>
    private static DateTime _trayMenuOpenedAt = DateTime.MinValue;

    /// <summary>
    /// macOS：应用被激活（点 Dock 图标、切回本应用）时，把收进菜单栏的主界面叫回来。
    /// Windows 上这一步由系统任务栏负责，macOS 没有等价物，只能自己盯前台应用变化。
    /// </summary>
    private static void StartMacActivationWatch(MainWindow window)
    {
        // 启动瞬间应用自己就是前台，别把这个当成「用户点了 Dock」
        var wasFrontmost = true;
        var startedAt = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            var frontmost = Services.MacOS.MacOsForeground.IsForegroundProcess();
            var rising = frontmost && !wasFrontmost;
            wasFrontmost = frontmost;

            // 最小化这一档每拍都对一次账，不等「前台上升沿」：
            // 窗口最小化时应用本来就在前台，用户再点 Dock 图标不会产生上升沿，
            // 只等上升沿就永远等不到那次「点 Dock 唤回」。
            // 判据是原生窗口的状态：AppKit 点 Dock 时会自己 deminiaturize，
            // 而 Avalonia 的 WindowState 可能还停在 Minimized，两边对不上就恢复不了。
            if (window.WindowState == WindowState.Minimized)
            {
                try
                {
                    if (Services.MacOS.MacOsWindowInterop.IsMiniaturized(window)) return;
                    window.WindowState = WindowState.Normal;
                    if (!window.IsVisible) window.Show();
                    window.Activate();
                    Services.DesktopLog.Write("macOS activation → 原生窗口已还原，对齐 WindowState");
                }
                catch (Exception ex)
                {
                    Services.DesktopLog.Write($"[showmain] 还原对账失败: {ex.Message}");
                }
                return;
            }

            if (!rising) return;
            // 启动后 5 秒内的首次激活是启动本身带起来的，不当作用户操作
            if (DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(5)) return;
            // 刚点过托盘菜单：菜单交互也会让应用短暂上前台，这时不要弹窗
            if (DateTime.UtcNow - _trayMenuOpenedAt < TimeSpan.FromSeconds(3)) return;
            if (window.IsVisible) return;
            Services.DesktopLog.Write("macOS activation → show main window");
            ShowMainWindow(window);
        };
        timer.Start();
    }

    private void SetupTray(MainWindow main, IClassicDesktopStyleApplicationLifetime desktop, bool silent)
    {
        _trayMenu = new TrayMenuWindow((MainViewModel)main.DataContext!);
        _trayMenu.ShowMainRequested += () => ShowMainWindow(main);
        _trayMenu.ToggleDesktopWidgetsRequested += Services.DesktopHost.ToggleFromTray;
        _trayMenu.ToggleThemeRequested += () => ToggleTheme(main);
        _trayMenu.OpenSettingsRequested += () => OpenSettings(main);
        _trayMenu.RestartRequested += () => RestartApplication(main);
        _trayMenu.ExitRequested += main.ExitApplication;

        // 托盘消息挂在窗口句柄上，得等窗口真正创建出来
        void Start(object? sender, EventArgs e)
        {
            main.Opened -= Start;
            _trayIcon = TrayIconHost.Create(main, "米家 · 控制中心");
            if (_trayIcon is null)
            {
                // 托盘没建起来（macOS 上状态栏图标创建失败）：绝不能把窗口收进托盘，
                // 否则应用就藏起来再也找不回来了。改成关窗即退出。
                TrayUnavailable = true;
                DesktopLog.Write("tray icon unavailable → close button exits the app");
            }
            else
            {
                _trayIcon.LeftClicked += () => ShowMainWindow(main);
                _trayIcon.RightClicked += () => OpenTrayMenu(main);
                // 点「发现新版本」气泡：把主窗口叫出来，用户自己去「设置 → 关于」安装
                _trayIcon.BalloonClicked += () => ShowMainWindow(main);

                // macOS：状态栏图标点开的是原生菜单，交互全走这些命令
                _trayIcon.ShowMainRequested += () => ShowMainWindow(main);
                _trayIcon.ToggleDesktopWidgetsRequested += Services.DesktopHost.ToggleFromTray;
                _trayIcon.ToggleThemeRequested += () => ToggleTheme(main);
                _trayIcon.OpenSettingsRequested += () => OpenSettings(main);
                _trayIcon.RestartRequested += () => RestartApplication(main);
                _trayIcon.ExitRequested += main.ExitApplication;
            }

            // 启动自动检查发现新版本时走托盘气泡，不抢焦点（强制更新另有模态框）
            if (main.DataContext is MainViewModel vm)
            {
                vm.NotifyUser = (title, text) => _trayIcon?.Notify(title, text);
                // 托盘图标刚建好，这时才弹得出来：补提示上次检查缓存的「有新版本」
                vm.NotifyCachedUpdate();
            }

            if (silent)
            {
                // 恢复常态属性再收起：之后从托盘打开要正常出现在任务栏、不再透明
                main.Opacity = 1;
                main.ShowInTaskbar = true;
                main.Hide();
                main.CompleteSilentStartup();
                // 静默启动即收进托盘：立即回收内存，让开机自启的常驻进程体积最小
                Services.MemoryTrimmer.TrimReleasedUi();
            }
        }

        if (main.IsVisible) Start(null, EventArgs.Empty);
        else main.Opened += Start;

        desktop.Exit += (_, _) =>
        {
            _trayIcon?.Dispose();
            // 退出前把剩余活跃时长和队列发出去（最多等 3 秒，不拖慢关闭）
            Services.Telemetry.TelemetryService.Shutdown();
        };
    }

    private void OpenTrayMenu(MainWindow main)
    {
        if (_trayMenu is null) return;
        _trayMenuOpenedAt = DateTime.UtcNow;
        // 按托盘图标的真实位置定位；图标记不到时退回鼠标位置（比如收在溢出区里）
        var cursor = TrayMenuWindow.CursorPosition();
        var anchor = _trayIcon?.IconRect() is { } rect
            ? new PixelPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2)
            : cursor;
        var screens = main.Screens;
        _trayMenu.ToggleNearAnchor(screens.ScreenFromPoint(anchor) ?? screens.Primary, anchor);
    }

    /// <summary>
    /// 托盘菜单「设置」：把主窗口叫出来并直接进设置页。
    /// <para>
    /// 设置页是主界面内容树里的一个对话框（<c>SettingsDialogView</c>，绑定 <c>IsSettingsOpen</c>），
    /// 窗口收进托盘时整棵内容树会被拆掉，所以要先切标志位再显示：
    /// 显示时内容树重建，绑定的 <c>IsSettingsOpen</c> 已经是 true，弹窗直接就在。
    /// </para>
    /// </summary>
    private static void OpenSettings(MainWindow main)
    {
        if (main.DataContext is MainViewModel vm) vm.OpenSettingsCommand.Execute(null);
        ShowMainWindow(main);
    }

    private static void ShowMainWindow(MainWindow main)
    {
        Services.DesktopLog.Write(
            $"[showmain] before visible={main.IsVisible} state={main.WindowState} opacity={main.Opacity} size={main.Width}x{main.Height} pos={main.Position}");
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        if (!main.IsVisible) main.Show();
        main.Activate();
        Services.DesktopLog.Write($"[showmain] after visible={main.IsVisible} opacity={main.Opacity}");
    }

    /// <summary>
    /// 点 Dock 图标（macOS reopen）后把主窗口叫回来。
    /// <para>
    /// 比 <see cref="ShowMainWindow"/> 多一步：先对 NSWindow 发原生 <c>deminiaturize:</c>。
    /// 窗口被收进 Dock 时 NSWindow 已经 miniaturize，而 Avalonia 的 WindowState 只有在后端
    /// 收到对应通知时才会回落；窗口同时还处于隐藏态时这次回落压根不会发生，
    /// 于是只改 WindowState 会出现「点了 Dock，窗口闪一下又缩回去」。
    /// </para>
    /// </summary>
    private static void InstallCrashLog()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Services.CrashLog.Write("AppDomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services.CrashLog.Write("UnobservedTask", e.Exception);
            e.SetObserved();
        };

        try
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
                Services.CrashLog.Write("UI", e.Exception);
        }
        catch
        {
            // 老版本 Avalonia 没有这个事件，记 AppDomain 的那份就够了
        }
    }

    private static void RestoreMainWindow(MainWindow main)
    {
        try
        {
            if (OperatingSystem.IsMacOS() && main.WindowState == WindowState.Minimized)
            {
                Services.MacOS.MacOsWindowInterop.Deminiaturize(main);
                main.WindowState = WindowState.Normal;
            }
        }
        catch (Exception ex)
        {
            Services.DesktopLog.Write($"[showmain] deminiaturize 失败: {ex.Message}");
        }

        ShowMainWindow(main);
    }

    private static void ToggleTheme(MainWindow main)
    {
        if (main.DataContext is MainViewModel vm)
        {
            vm.IsDark = !vm.IsDark;
            return;
        }
        ThemeStore.Set(!ThemeStore.IsLight);
        Application.Current!.RequestedThemeVariant = ThemeStore.CurrentVariant;
    }

    // 重新拉起一个实例后再退出当前进程。
    // 「立即重启」要看到主界面，所以带 --show（否则偏好是静默启动时会收进托盘，像没重启）；
    // 托盘菜单的重启也走这里，同样回到主界面。
    internal static void RestartApplication(MainWindow main)
    {
        // 先放掉单实例互斥体再拉新进程：否则新进程会判定已有实例在跑、弹一下旧窗口就退出
        Services.SingleInstance.Release();
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe, "--show") { UseShellExecute = true });
        main.ExitApplication();
    }
}
