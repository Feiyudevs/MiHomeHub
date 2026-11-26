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
using System.Runtime.Versioning;

namespace MiHomeHub;

[SupportedOSPlatform("windows")]
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
        // 全局 ToolTip 形状：单行胶囊、换行改小圆角矩形（见 Controls/ToolTipShape.cs）
        Controls.ToolTipShape.Install();
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

            // 全局低层输入钩子（鼠标双击桌面 / 全局快捷键）推迟到界面彻底空闲再装：
            // 钩子回调跑在本进程线程，系统会让每一次鼠标、按键都先等它返回；启动那几秒
            // UI 线程还在建窗口、贴桌面，钩子早挂一秒就等于整机输入卡一秒（用户感受就是
            // 「一启动电脑就卡一下」）。晚挂的代价只是快捷键在开头一两秒内不响应。
            Dispatcher.UIThread.Post(() => InstallInputHooks(window), DispatcherPriority.ApplicationIdle);

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

    /// <summary>界面空闲后安装两个低层输入钩子，并把真实的安装结果同步给「快捷键」页。</summary>
    private static void InstallInputHooks(MainWindow window)
    {
        Services.DesktopDoubleClickHook.Install();
        Services.GlobalHotkeyService.InstallHook();
        if (window.DataContext is MainViewModel vm) vm.SyncHotkeyHookState();
    }

    private void SetupTray(MainWindow main, IClassicDesktopStyleApplicationLifetime desktop, bool silent)
    {
        _trayMenu = new TrayMenuWindow((MainViewModel)main.DataContext!);
        _trayMenu.ShowMainRequested += () => ShowMainWindow(main);
        _trayMenu.ToggleDesktopWidgetsRequested += Services.DesktopHost.ToggleFromTray;
        _trayMenu.ToggleThemeRequested += () => ToggleTheme(main);
        _trayMenu.RestartRequested += () => RestartApplication(main);
        _trayMenu.ExitRequested += main.ExitApplication;

        // 托盘消息挂在窗口句柄上，得等窗口真正创建出来
        void Start(object? sender, EventArgs e)
        {
            main.Opened -= Start;
            _trayIcon = new TrayIconHost(main, "米家 · 控制中心");
            _trayIcon.LeftClicked += () => ShowMainWindow(main);
            _trayIcon.RightClicked += () => OpenTrayMenu(main);
            // 点「发现新版本」气泡：把主窗口叫出来，用户自己去「设置 → 关于」安装
            _trayIcon.BalloonClicked += () => ShowMainWindow(main);

            // 启动自动检查发现新版本时走托盘气泡，不抢焦点（强制更新另有模态框）
            if (main.DataContext is MainViewModel vm)
            {
                vm.NotifyUser = (title, text) => _trayIcon.Notify(title, text);
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
        // 按托盘图标的真实位置定位；图标记不到时退回鼠标位置（比如收在溢出区里）
        var cursor = TrayMenuWindow.CursorPosition();
        var anchor = _trayIcon?.IconRect() is { } rect
            ? new PixelPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2)
            : cursor;
        var screens = main.Screens;
        _trayMenu.ToggleNearAnchor(screens.ScreenFromPoint(anchor) ?? screens.Primary, anchor);
    }

    private static void ShowMainWindow(MainWindow main)
    {
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        if (!main.IsVisible) main.Show();
        main.Activate();
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
