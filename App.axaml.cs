using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MiHome.Services;
using MiHome.ViewModels;
using MiHome.Views;
using System.Runtime.CompilerServices;

namespace MiHome;

public partial class App : Application
{
    // 一格滚轮的行进距离与动画时长：距离大一点、动画短一点，滚起来更跟手
    private const double WheelStep = 220;
    private const int WheelDurationMs = 110;
    private static readonly ConditionalWeakTable<ScrollViewer, WheelScroll> WheelScrolls = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// 滚轮改为缓动动画滚动；拖拽滑块、键盘滚动仍走原生逻辑
    private static void OnPointerWheel(object? sender, PointerWheelEventArgs args)
    {
        if (args.Handled || args.Delta.Y == 0) return;
        var source = args.Source as Visual;
        var sv = source as ScrollViewer
                 ?? source?.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (sv is null) return;

        var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        var from = sv.Offset;
        var to = Math.Clamp(from.Y - args.Delta.Y * WheelStep, 0, max);
        if (Math.Abs(to - from.Y) < 0.5) return;

        if (!WheelScrolls.TryGetValue(sv, out var scroll))
            WheelScrolls.Add(sv, scroll = new WheelScroll(sv));

        scroll.Start(from.Y, to);
        args.Handled = true;
    }

    /// 拖滑块期间把滚动条钉成可见：指针拖出窗口会让 Window:pointerover 失效，把手会中途淡掉。
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

    private sealed class WheelScroll
    {
        private static readonly Easing Ease = new CubicEaseOut();
        private readonly ScrollViewer sv;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(8) };
        private double from, to, x;
        private long startMs;

        public WheelScroll(ScrollViewer sv)
        {
            this.sv = sv;
            timer.Tick += (_, _) => Tick();
        }

        public void Start(double fromY, double toY)
        {
            from = fromY;
            to = toY;
            x = sv.Offset.X;
            startMs = Environment.TickCount64;
            timer.Start();
        }

        private void Tick()
        {
            var t = (Environment.TickCount64 - startMs) / (double)WheelDurationMs;
            if (t >= 1)
            {
                sv.Offset = new Vector(x, to);
                timer.Stop();
                return;
            }

            sv.Offset = new Vector(x, from + (to - from) * Ease.Ease(t));
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = ThemeStore.CurrentVariant;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 升级/换目录后 Run 键里可能还是旧 exe 路径
            StartupRegistration.Sync();

            // 开机自启（Run 键带 --startup）：窗口只用来先创建句柄，托盘就绪后立刻收起
            var silent = desktop.Args?.Contains("--startup", StringComparer.OrdinalIgnoreCase) == true;
            var window = new MainWindow
            {
                DataContext = new MainViewModel()
            };
            if (silent)
            {
                window.Opacity = 0;
                window.ShowInTaskbar = false;
            }
            desktop.MainWindow = window;
            desktop.MainWindow.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            desktop.MainWindow.AddHandler(Thumb.DragStartedEvent, OnThumbDragStarted,
                RoutingStrategies.Bubble, handledEventsToo: true);
            desktop.MainWindow.AddHandler(Thumb.DragCompletedEvent, OnThumbDragCompleted,
                RoutingStrategies.Bubble, handledEventsToo: true);
            SetupTray(window, desktop, silent);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private TrayIconHost? _trayIcon;
    private TrayMenuWindow? _trayMenu;

    private void SetupTray(MainWindow main, IClassicDesktopStyleApplicationLifetime desktop, bool silent)
    {
        _trayMenu = new TrayMenuWindow();
        _trayMenu.ShowMainRequested += () => ShowMainWindow(main);
        _trayMenu.ToggleThemeRequested += () => ToggleTheme(main);
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
        if (!main.IsVisible) main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
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
}
