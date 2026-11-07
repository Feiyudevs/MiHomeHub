using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using MiHome.Services;
using MiHome.ViewModels;
using MiHome.Views;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace MiHome;

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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 升级/换目录后 Run 键里可能还是旧 exe 路径
            StartupRegistration.Sync();

            // 静默启动只创建窗口句柄供托盘使用，不构建完整主界面。
            var silent = !AppSettingsStore.ShowMainWindowOnStartup;
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
            if (window.DataContext is MainViewModel mainVm) mainVm.DesktopWidgetsEnabled = Services.DesktopHost.Enabled;
            // 窗口打开后立即回收启动期临时对象和原生渲染资源，降低基础常驻内存
            window.Opened += (_, _) => Services.MemoryTrimmer.TrimForce();
            desktop.MainWindow.AddHandler(Thumb.DragStartedEvent, OnThumbDragStarted,
                RoutingStrategies.Bubble, handledEventsToo: true);
            desktop.MainWindow.AddHandler(Thumb.DragCompletedEvent, OnThumbDragCompleted,
                RoutingStrategies.Bubble, handledEventsToo: true);
            SetupTray(window, desktop, silent);

            // 单实例：重复启动的进程会发信号过来，弹出主界面
            Services.SingleInstance.BeginWatch(() => ShowMainWindow(window));

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

    private void SetupTray(MainWindow main, IClassicDesktopStyleApplicationLifetime desktop, bool silent)
    {
        _trayMenu = new TrayMenuWindow((MainViewModel)main.DataContext!);
        _trayMenu.ShowMainRequested += () => ShowMainWindow(main);
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

        desktop.Exit += (_, _) => _trayIcon?.Dispose();
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

    // 重新拉起一个实例后再退出当前进程；重启后是否弹主界面看「启动后显示主界面」
    private static void RestartApplication(MainWindow main)
    {
        // 先放掉单实例互斥体再拉新进程：否则新进程会判定已有实例在跑、弹一下旧窗口就退出
        Services.SingleInstance.Release();
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        main.ExitApplication();
    }
}
