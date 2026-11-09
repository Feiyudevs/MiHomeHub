using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 托盘菜单：透明无边框窗口承载自绘卡片，比系统原生托盘菜单更贴近应用视觉。
/// 阴影靠窗口透明边距留白，边距需要大于阴影偏移 + 扩散，否则光晕会被窗口裁掉。
/// </summary>
public partial class TrayMenuWindow : Window
{
    private const double ShadowMargin = 20;

    // 有音箱时多出的媒体块高度（下拉 32 + 间距 + 三键 34 + 上下留白 + 分隔线）
    private const double MediaBlockHeight = 94;
    // 基础高度含顶部「米家控制中心」标题行（26 + 与下一项的 1px 间距）
    private const double BaseHeight = 259;

    private readonly MainViewModel _vm;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _slide = new();

    // 定位锚点：媒体区在打开过程中才出现/收起时要重排窗口，得拿原锚点再贴一次
    private Screen? _screen;
    private PixelPoint _anchor;

    // 点托盘图标时窗口会先失活隐藏，紧接着才收到 Clicked；用时间戳区分「关闭」和「重新打开」
    private long _hiddenAt;

    public event Action? ShowMainRequested;
    public event Action? ToggleThemeRequested;
    public event Action? RestartRequested;
    public event Action? ExitRequested;

    public TrayMenuWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        Card.RenderTransform = new TransformGroup { Children = { _scale, _slide } };
        Card.RenderTransformOrigin = new RelativePoint(1, 1, RelativeUnit.Relative);

        var ease = new CubicEaseOut();
        Card.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(140), Easing = ease }
        };
        _scale.Transitions = new Transitions
        {
            new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = ease },
            new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = ease }
        };
        _slide.Transitions = new Transitions
        {
            new DoubleTransition { Property = TranslateTransform.YProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = ease }
        };
        Card.Opacity = 0;
        Deactivated += (_, _) => Dismiss();
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>设备/音箱数据在菜单开着时到货：媒体区从骨架换成真控件，或家里其实没音箱要收掉，窗口高度跟着重排。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.TrayMediaSlot)) return;
        if (!IsVisible || _screen is null) return;
        // 同步收尾可能在线程池回来，窗口尺寸/位置只能在 UI 线程改
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnViewModelPropertyChanged(sender, e));
            return;
        }

        var height = _vm.TrayMediaSlot ? BaseHeight + MediaBlockHeight : BaseHeight;
        if (Math.Abs(height - Height) < 0.5) return;
        Height = height;
        PlaceNear(_screen, _anchor);
    }

    /// <summary>贴着托盘图标弹出；已经打开时再次触发就是收起。</summary>
    public void ToggleNearAnchor(Screen? screen, PixelPoint anchor)
    {
        if (IsVisible)
        {
            Dismiss();
            return;
        }
        if (Environment.TickCount64 - _hiddenAt < 280)
        {
            _hiddenAt = 0;
            return;
        }
        Open(screen, anchor);
    }

    private void Open(Screen? screen, PixelPoint anchor)
    {
        if (screen is null) return;
        // 媒体区（含加载骨架）在不在，高度要先定下来再定位
        _screen = screen;
        _anchor = anchor;
        Height = _vm.TrayMediaSlot ? BaseHeight + MediaBlockHeight : BaseHeight;
        RefreshThemeEntry();
        PlaceNear(screen, anchor);
        Show();
        Activate();
        AnimateIn();
    }

    private void RefreshThemeEntry()
    {
        var light = ThemeStore.IsLight;
        ThemeText.Text = light ? "切换深色主题" : "切换浅色主题";
        SunIcon.IsVisible = !light;
        MoonIcon.IsVisible = light;
    }

    private void AnimateIn()
    {
        Card.Opacity = 0;
        _scale.ScaleX = _scale.ScaleY = 0.94;
        _slide.Y = 8;
        Dispatcher.UIThread.Post(() =>
        {
            Card.Opacity = 1;
            _scale.ScaleX = _scale.ScaleY = 1;
            _slide.Y = 0;
        }, DispatcherPriority.Render);
    }

    private void Dismiss()
    {
        if (!IsVisible) return;
        // 窗口 Hide 不会带走 Popup：下拉开着时先把它一起收起，否则会留在桌面上
        SpeakerPicker.CloseDropDown();
        Hide();
        _hiddenAt = Environment.TickCount64;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Dismiss();
    }

    private void OnShowMainClick(object? sender, RoutedEventArgs e)
    {
        Dismiss();
        ShowMainRequested?.Invoke();
    }

    private void OnToggleThemeClick(object? sender, RoutedEventArgs e)
    {
        Dismiss();
        ToggleThemeRequested?.Invoke();
    }

    private void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        Dismiss();
        RestartRequested?.Invoke();
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        Dismiss();
        ExitRequested?.Invoke();
    }

    /// <summary>
    /// 贴着任务栏朝向展开：底栏时卡片落在托盘上方、右边缘靠近图标；
    /// 任务栏在其它边时按对应方向避开。最后统一收进工作区内。
    /// </summary>
    private void PlaceNear(Screen? screen, PixelPoint anchor)
    {
        if (screen is null) return;

        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var work = screen.WorkingArea;

        int cardW = (int)Math.Round((Width - ShadowMargin * 2) * scaling);
        int cardH = (int)Math.Round((Height - ShadowMargin * 2) * scaling);
        int margin = (int)Math.Round(ShadowMargin * scaling);
        int gap = (int)Math.Round(10 * scaling);

        int x, y;
        var origin = new RelativePoint(1, 1, RelativeUnit.Relative);

        if (anchor.X < work.X)
        {
            x = work.X + gap;
            y = anchor.Y - cardH / 2;
            origin = new RelativePoint(0, 0.5, RelativeUnit.Relative);
        }
        else if (anchor.X >= work.Right)
        {
            x = work.Right - gap - cardW;
            y = anchor.Y - cardH / 2;
            origin = new RelativePoint(1, 0.5, RelativeUnit.Relative);
        }
        else if (anchor.Y < work.Y)
        {
            x = anchor.X + (int)Math.Round(16 * scaling) - cardW;
            y = work.Y + gap;
            origin = new RelativePoint(1, 0, RelativeUnit.Relative);
        }
        else
        {
            var bottom = anchor.Y >= work.Bottom ? work.Bottom - gap : anchor.Y - gap;
            x = anchor.X + (int)Math.Round(16 * scaling) - cardW;
            y = bottom - cardH;
        }

        x = Math.Clamp(x, work.X + gap, Math.Max(work.X + gap, work.Right - gap - cardW));
        y = Math.Clamp(y, work.Y + gap, Math.Max(work.Y + gap, work.Bottom - gap - cardH));

        Card.RenderTransformOrigin = origin;
        Position = new PixelPoint(x - margin, y - margin);
    }

    public static PixelPoint CursorPosition()
    {
        GetCursorPos(out var p);
        return new PixelPoint(p.X, p.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
}
