// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MiHome.Models;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

public partial class MainWindow
{
    private Control? _mainContentRoot;
    private INameScope? _mainContentScope;
    private MainViewModel? _observedMainViewModel;
    private DispatcherTimer? _releasedContentTrimTimer;
    private bool _deferInitialContent;
    private bool _presentingHiddenFrame;
    private bool _changingMainContent;

    private bool IsMainContentLoaded => _mainContentRoot is not null;
    private T? FindMainControl<T>(string name) where T : Control => _mainContentScope?.Find<T>(name);

    // 不保存旧树中的命名控件；隐藏后只保留当前模板scope的空引用。
    private Border SearchBox => FindMainControl<Border>("SearchBox")!;
    private TextBox SearchInput => FindMainControl<TextBox>("SearchInput")!;
    private Button XiaoAiBtn => FindMainControl<Button>("XiaoAiBtn")!;
    private Button MusicBtn => FindMainControl<Button>("MusicBtn")!;
    private Button HomeChipBtn => FindMainControl<Button>("HomeChipBtn")!;
    private Border? DeviceMask => FindMainControl<Border>("DeviceMask");
    private Border? DeviceMaskLight => FindMainControl<Border>("DeviceMaskLight");
    private DeviceDrawerView? DeviceDrawerView => FindMainControl<DeviceDrawerView>("DeviceDrawerView");
    private VoicePanelView? VoicePanelView => FindMainControl<VoicePanelView>("VoicePanelView");
    private MusicPanelView? MusicPanelView => FindMainControl<MusicPanelView>("MusicPanelView");
    private HomePanelView? HomePanelView => FindMainControl<HomePanelView>("HomePanelView");

    public override void Show()
    {
        if (!_deferInitialContent && !_presentingHiddenFrame && WindowState != WindowState.Minimized)
            EnsureMainContent();
        base.Show();
    }

    internal void CompleteSilentStartup() => _deferInitialContent = false;

    private void UpdateMainContentLifetime()
    {
        if (_changingMainContent) return;
        if (!IsVisible || WindowState == WindowState.Minimized)
            ReleaseMainContent();
        else if (!_deferInitialContent && !_presentingHiddenFrame)
            EnsureMainContent();
    }

    private void EnsureMainContent()
    {
        if (_mainContentRoot is not null || _changingMainContent || _memoryTrimmersClosed) return;
        _releasedContentTrimTimer?.Stop();
        if (!Resources.TryGetResource("MainWindowUiTemplate", ActualThemeVariant, out var resource)
            || resource is not DataTemplate template) return;
        _changingMainContent = true;
        try
        {
            var built = TemplateContent.Load(template.Content)
                ?? throw new InvalidOperationException("Main window content could not be constructed.");
            var root = built.Result;
            root.DataContext = DataContext;
            _mainContentRoot = root;
            _mainContentScope = built.NameScope;
            SurfaceContentHost.Content = root;
            WireNavSlider();
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_mainContentRoot, root) || Vm is not { } vm) return;
                if (vm.IsDrawerOpen) AnimateDeviceDrawer(true);
                if (vm.IsHomeOpen) { AlignHomePanel(); HomePanelView?.Animate(true); }
                if (vm.IsVoiceOpen) { AlignVoicePanel(); VoicePanelView?.Animate(true); }
                if (vm.IsMusicOpen) { AlignMusicPanel(); MusicPanelView?.Animate(true); }
            }, DispatcherPriority.Loaded);
        }
        finally { _changingMainContent = false; }
    }

    private void ReleaseMainContent()
    {
        if (_mainContentRoot is not { } root || _changingMainContent) return;
        _changingMainContent = true;
        try
        {
            _searchOpen = false;
            if (FocusManager?.GetFocusedElement() is Avalonia.Visual focused && IsWithin(focused, root))
                FocusManager.ClearFocus();
            // Popup拥有独立顶层；先关闭，避免PlacementTarget把旧主界面保活。
            foreach (var control in root.GetVisualDescendants().OfType<Control>().Prepend(root).ToArray())
            {
                if (control.ContextMenu is { IsOpen: true } menu) menu.Close();
                control.ContextFlyout?.Hide();
                if (control is ComboBox combo) combo.IsDropDownOpen = false;
                if (control is Button button) button.Flyout?.Hide();
            }
            _mainContentRoot = null;
            _mainContentScope = null;
            SurfaceContentHost.Content = null;
            UnwireNavSlider();
            if (Vm is { } vm)
            {
                vm.CloseOverlaysCommand.Execute(null);
                vm.SearchText = string.Empty;
            }
            foreach (var items in root.GetVisualDescendants().OfType<ItemsControl>().ToArray())
                items.ItemsSource = null;
            root.DataContext = null;
            RequestReleasedContentTrim();
        }
        finally { _changingMainContent = false; }
    }

    private void RequestReleasedContentTrim()
    {
        if (_memoryTrimmersClosed) return;
        // 抽屉/遮罩最多约300ms才释放动画引用，等它们结束后再收旧树。
        _releasedContentTrimTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _releasedContentTrimTimer.Tick -= OnReleasedContentTrim;
        _releasedContentTrimTimer.Tick += OnReleasedContentTrim;
        _releasedContentTrimTimer.Stop();
        _releasedContentTrimTimer.Start();
    }

    private void OnReleasedContentTrim(object? sender, EventArgs e)
    {
        _releasedContentTrimTimer?.Stop();
        if (!IsMainContentLoaded) MemoryTrimmer.TrimReleasedUi();
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsMainContentLoaded) return;
        // 切页（含键盘 1/2/3）：滑块滑过去，同时让新页里的列表卡片逐张移入
        if (e.PropertyName == nameof(MainViewModel.SelectedNavIndex))
        {
            Dispatcher.UIThread.Post(() =>
            {
                SyncNavSlider(true);
                PlayPageEnter(Vm?.SelectedNavIndex ?? 0);
            }, DispatcherPriority.Background);
            return;
        }
        if (e.PropertyName is not (nameof(MainViewModel.IsDrawerOpen)
            or nameof(MainViewModel.IsVoiceOpen) or nameof(MainViewModel.IsMusicOpen)
            or nameof(MainViewModel.IsHomeOpen))) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsMainContentLoaded || Vm is not { } vm) return;
            if (e.PropertyName == nameof(MainViewModel.IsDrawerOpen)) AnimateDeviceDrawer(vm.IsDrawerOpen);
            if (e.PropertyName == nameof(MainViewModel.IsHomeOpen))
            {
                if (vm.IsHomeOpen) AlignHomePanel();
                HomePanelView?.Animate(vm.IsHomeOpen);
            }
            if (e.PropertyName == nameof(MainViewModel.IsVoiceOpen))
            {
                if (vm.IsVoiceOpen) AlignVoicePanel();
                VoicePanelView?.Animate(vm.IsVoiceOpen);
            }
            if (e.PropertyName == nameof(MainViewModel.IsMusicOpen))
            {
                if (vm.IsMusicOpen) AlignMusicPanel();
                MusicPanelView?.Animate(vm.IsMusicOpen);
            }
        });
    }

    // ================= 侧栏选中滑块 =================
    // 一块共享的浅紫底随选中行上下位移（位移/宽高过渡写在 Border.NavSlider.ready 样式里）。
    // 滑块铺在导航行下层：位置按当前 .on 行的实际边界算，行本身底色透明（见 ControlStyles 的 NavRow.on）。
    private Control? _navSliderHost;
    private Canvas? _navSliderLayer;
    private Border? _navSliderIndicator;
    private ScrollViewer? _navRoomsScroll;
    private Rect? _navSliderRect;
    private bool _navSliderReady;

    private void WireNavSlider()
    {
        _navSliderLayer = FindMainControl<Canvas>("NavSliderLayer");
        _navSliderIndicator = FindMainControl<Border>("NavSlider");
        _navRoomsScroll = FindMainControl<ScrollViewer>("NavRoomsScroll");
        if (_navSliderLayer?.Parent is not Control host) return;
        _navSliderHost = host;
        host.LayoutUpdated += OnNavSliderLayoutUpdated;
        // 点房间时 SelectedNavIndex 可能一直是 1，属性不变，靠点击补一次；
        // Button 会在自己的 PointerReleased 里把事件标记已处理，所以这里必须 handledEventsToo: true 才能收到
        host.AddHandler(PointerReleasedEvent, OnNavHostPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        if (_navRoomsScroll is { } scroll) scroll.ScrollChanged += OnNavRoomsScrollChanged;
        Dispatcher.UIThread.Post(() => SyncNavSlider(false), DispatcherPriority.Loaded);
    }

    private void UnwireNavSlider()
    {
        if (_navSliderHost is { } host)
        {
            host.LayoutUpdated -= OnNavSliderLayoutUpdated;
            host.RemoveHandler(PointerReleasedEvent, OnNavHostPointerReleased);
        }
        if (_navRoomsScroll is { } scroll) scroll.ScrollChanged -= OnNavRoomsScrollChanged;
        _navSliderHost = null;
        _navSliderLayer = null;
        _navSliderIndicator = null;
        _navRoomsScroll = null;
        _navSliderRect = null;
        _navSliderReady = false;
        _pendingEnterIndex = null;
    }

    private void OnNavSliderLayoutUpdated(object? sender, EventArgs e)
    {
        SyncNavSlider(false);
        // 房间切换后卡片容器可能还没实例化，挂起等这一轮布局完再补动画
        if (_pendingEnterIndex is { } index) PlayPageEnter(index);
    }

    private void OnNavRoomsScrollChanged(object? sender, ScrollChangedEventArgs e) => SyncNavSlider(false);

    private void OnNavHostPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var isRoomClick = IsRoomRowSource(e.Source);
        Dispatcher.UIThread.Post(() =>
        {
            SyncNavSlider(true);
            // 已经停在房间页时再点别的房间，SelectedNavIndex 不变，这里补一次卡片入场
            if (isRoomClick) PlayPageEnter(Vm?.SelectedNavIndex ?? 1);
        }, DispatcherPriority.Background);
    }

    /// <summary>把滑块对到当前选中行；animate=false 用于布局/滚动这类跟随刷新（不走过渡，避免拖影）。</summary>
    private void SyncNavSlider(bool animate)
    {
        if (_navSliderLayer is not { } layer || _navSliderIndicator is not { } indicator) return;

        var row = FindSelectedNavRow(layer);
        if (row is null || !IsNavRowFullyVisible(row, layer)
            || row.TranslatePoint(new Point(0, 0), layer) is not { } origin)
        {
            _navSliderRect = null;
            indicator.IsVisible = false;
            return;
        }

        // 行模板里的底色 Border 内缩 10,3：滑块贴住这块底，而不是整行外框
        var rect = new Rect(origin.X + 10, origin.Y + 3,
            Math.Max(0, row.Bounds.Width - 20), Math.Max(0, row.Bounds.Height - 6));
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            _navSliderRect = null;
            indicator.IsVisible = false;
            return;
        }
        if (_navSliderRect == rect && indicator.IsVisible) return;

        _navSliderRect = rect;
        if (!animate) indicator.Classes.Set("ready", false);
        Canvas.SetLeft(indicator, rect.X);
        Canvas.SetTop(indicator, rect.Y);
        indicator.Width = rect.Width;
        indicator.Height = rect.Height;
        indicator.IsVisible = true;

        // 首帧/跟随刷新：先落位，再挂上过渡，保证后续切行才做位移动画
        if (!_navSliderReady || !animate)
        {
            _navSliderReady = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_navSliderIndicator, indicator)) indicator.Classes.Set("ready", true);
            }, DispatcherPriority.Render);
        }
    }

    private static Button? FindSelectedNavRow(Visual layer) =>
        layer.Parent is not Visual host
            ? null
            : host.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains("NavRow") && b.Classes.Contains("on"));

    /// <summary>选中的房间被滚出房间列表视口时藏起滑块，避免它跑到标题/顶部行上。</summary>
    private bool IsNavRowFullyVisible(Button row, Visual layer)
    {
        if (_navRoomsScroll is not { } scroll) return true;
        if (!row.GetVisualAncestors().Contains(scroll)) return true;
        if (scroll.TranslatePoint(new Point(0, 0), layer) is not { } viewport) return false;
        if (row.TranslatePoint(new Point(0, 0), layer) is not { } origin) return false;
        return origin.Y >= viewport.Y - 0.5
            && origin.Y + row.Bounds.Height <= viewport.Y + scroll.Bounds.Height + 0.5;
    }

    private static bool IsRoomRowSource(object? source) =>
        source is Visual visual
        && visual.GetSelfAndVisualAncestors().OfType<Button>()
            .Any(b => b.DataContext is RoomChip);

    // ================= 切页入场：列表卡片逐张移入 =================
    private Carousel? PageCarousel => FindMainControl<Carousel>("PageCarousel");

    // 房间切换后卡片容器可能还没实例化，先记下页索引，等下一轮 LayoutUpdated 再播
    private int? _pendingEnterIndex;

    /// <summary>
    /// 切页/切房间后让该页各列表里的卡片依次从右侧向左移入并淡入（最多错开 10 张，避免长列表拖太久）。
    /// 位移挂在卡片的 item 容器上：容器没有别处设置的 RenderTransform，不会和卡片自身的悬停抬起冲突。
    /// </summary>
    private void PlayPageEnter(int index)
    {
        if (PageCarousel?.ContainerFromIndex(index) is not Control page) return;

        var seen = new HashSet<Control>();
        var order = 0;
        var unrealized = false;
        foreach (var items in page.GetVisualDescendants().OfType<ItemsControl>())
        {
            var count = items.ItemCount;
            for (var i = 0; i < count; i++)
            {
                if (items.ContainerFromIndex(i) is not Control card)
                {
                    unrealized = true;
                    continue;
                }
                if (!card.IsVisible) continue;
                if (!seen.Add(card)) continue;
                PlayCardEnter(card, Math.Min(order++, 10) * 30);
            }
        }

        // 一张都没拿到但列表里有数据：说明容器还没实例化，挂起，别让动画落空
        _pendingEnterIndex = seen.Count == 0 && unrealized ? index : null;
    }

    private static void PlayCardEnter(Control card, int delayMs)
    {
        // 关键帧不能直接写 RenderTransform（Avalonia 11 没有该动画器），
        // 固定挂一个 TranslateTransform 只动它的 X，与骨架动画同一套写法。
        card.RenderTransform = new TranslateTransform(20, 0);
        card.Opacity = 0;

        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(240),
            Delay = TimeSpan.FromMilliseconds(delayMs),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
        };
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters =
            {
                new Setter(OpacityProperty, 0d),
                new Setter(TranslateTransform.XProperty, 20d),
            }
        });
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters =
            {
                new Setter(OpacityProperty, 1d),
                new Setter(TranslateTransform.XProperty, 0d),
            }
        });
        _ = anim.RunAsync(card);
    }

}
