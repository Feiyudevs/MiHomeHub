// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MiHomeHub.Models;

namespace MiHomeHub.Controls;

/// <summary>
/// 自绘下拉选择：触发器显示当前项，点开是玻璃浮层列表，选中项只上柔色底不改边框。
/// Options 给一组 <see cref="OptionItem"/>，SelectedValue 双向绑到调用方的状态上。
/// </summary>
public partial class OptionPicker : UserControl
{
    public static readonly StyledProperty<IEnumerable?> OptionsProperty =
        AvaloniaProperty.Register<OptionPicker, IEnumerable?>(nameof(Options));

    public static readonly StyledProperty<object?> SelectedValueProperty =
        AvaloniaProperty.Register<OptionPicker, object?>(nameof(SelectedValue),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>小号档：触发器和浮层一起收紧，嵌在信息卡角上这类窄处用。</summary>
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<OptionPicker, bool>(nameof(Compact));

    /// <summary>胶囊形触发器：24 高配 12 圆角，左右半圆，用在卡片角上的小控件。</summary>
    public static readonly StyledProperty<bool> PillProperty =
        AvaloniaProperty.Register<OptionPicker, bool>(nameof(Pill));

    /// <summary>触发按钮上是否显示房间胶囊。默认显示；趋势卡这类窄胶囊只留设备名，房间留在展开列表里。</summary>
    public static readonly StyledProperty<bool> ShowMetaProperty =
        AvaloniaProperty.Register<OptionPicker, bool>(nameof(ShowMeta), defaultValue: true);

    /// <summary>浮层是否置顶。桌面数据卡贴在桌面最底层，普通浮层会被桌面图标层压住、点不到选项；
    /// 只有这类宿主需要打开，普通页面保持默认（不压在其他应用上）。</summary>
    public static readonly StyledProperty<bool> TopmostPopupProperty =
        AvaloniaProperty.Register<OptionPicker, bool>(nameof(TopmostPopup));

    /// <summary>玻璃档：桌面组件上的下拉。触发器与浮层的悬停 / 选中一律换成白色带透明度，
    /// 压在用户自选的彩色卡面上不发灰，也不把品牌紫糊在上面。</summary>
    public static readonly StyledProperty<bool> GlassProperty =
        AvaloniaProperty.Register<OptionPicker, bool>(nameof(Glass));

    public bool Glass
    {
        get => GetValue(GlassProperty);
        set => SetValue(GlassProperty, value);
    }

    public bool Pill
    {
        get => GetValue(PillProperty);
        set => SetValue(PillProperty, value);
    }

    public bool ShowMeta
    {
        get => GetValue(ShowMetaProperty);
        set => SetValue(ShowMetaProperty, value);
    }

    public bool TopmostPopup
    {
        get => GetValue(TopmostPopupProperty);
        set => SetValue(TopmostPopupProperty, value);
    }

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public IEnumerable? Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public object? SelectedValue
    {
        get => GetValue(SelectedValueProperty);
        set => SetValue(SelectedValueProperty, value);
    }

    static OptionPicker()
    {
        OptionsProperty.Changed.AddClassHandler<OptionPicker>((x, _) => x.Rebind());
        SelectedValueProperty.Changed.AddClassHandler<OptionPicker>((x, _) => x.SyncSelection());
        CompactProperty.Changed.AddClassHandler<OptionPicker>((x, _) => x.SyncCompact());
        PillProperty.Changed.AddClassHandler<OptionPicker>((x, _) => x.SyncPill());
        ShowMetaProperty.Changed.AddClassHandler<OptionPicker>((x, _) => x.SyncSelection());
        TopmostPopupProperty.Changed.AddClassHandler<OptionPicker>((x, _) => x.SyncPopupTopmost());
        GlassProperty.Changed.AddClassHandler<OptionPicker>((x, _) => x.SyncGlass());
    }

    public OptionPicker()
    {
        InitializeComponent();

        Pop.PlacementTarget = Trigger;
        Pop.Topmost = TopmostPopup;
        // 贴近窗口底部时（如小爱语音浮层）自动翻到触发点上方，避免列表伸出屏幕
        Pop.PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideY
            | PopupPositionerConstraintAdjustment.FlipY;
        Trigger.Click += (_, _) => Pop.IsOpen = !Pop.IsOpen;

        Pop.Opened += (_, _) =>
        {
            // 上一轮关浮层后可能还替触发器占着「露面」状态，浮层重新打开就先把这个占位收回，
            // 免得指针一离开卡片，那份占位的收尾逻辑把刚开出来的浮层又合回去
            ClearRevealHold();
            Panel.MinWidth = Trigger.Bounds.Width;
            Trigger.Classes.Add("open");
            // 控件自己也挂一个 .open：宿主（如首页趋势卡）靠它把「悬停才露面的触发器」在浮层开着时留住
            Classes.Set("open", true);
            SyncCardPickOpen(true);
            AttachWheelForwarder();
            AttachPressProbe();
            Diag($"open op={Opacity:F2}");
            // 先让浮层以闭合态挂上树，下一帧再翻到展开态，过渡才有得播
            Dispatcher.UIThread.Post(() =>
            {
                Panel.Classes.Add("open");
                UpdateScrollPadding();
                WriteLayoutDiag();
            }, DispatcherPriority.Render);
        };
        Pop.Closed += (_, _) =>
        {
            Trigger.Classes.Remove("open");
            Panel.Classes.Remove("open");
            DetachWheelForwarder();
            DetachPressProbe();
            HoldRevealUntilPointerLeaves();
            StartDiagSampling();
        };

        // 行按钮在 ItemTemplate 里，挂事件路由接一次比每条模板写 Click 稳
        Panel.AddHandler(Button.ClickEvent, OnItemClick);
        Panel.AddHandler(PointerPressedEvent, OnItemPointerPressed, RoutingStrategies.Tunnel, true);

        ActualThemeVariantChanged += (_, _) => SyncTheme();
        SyncTheme();
        SyncCompact();
        SyncPill();
        SyncGlass();

        // 出现 / 消失纵向滚动条时，再决定要不要给右侧房间胶囊让出 14px
        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)
                UpdateScrollPadding();
        };

        Rebind();
    }

    /// <summary>
    /// overlay 滚动条画在视口右缘：只有真的出现纵向滚动条时，内容右侧才留 14px 给房间胶囊；
    /// 内容不够高、没有滚动条时不留这道空白，避免卡片右侧空一截。
    /// 底部始终留 3px 缓冲：滚到底后最后一行不贴卡片底缘，圆角高亮条不会被边框 / 圆角裁掉。
    /// </summary>
    private void UpdateScrollPadding()
    {
        var hasBar = Scroller.Extent.Height > Scroller.Viewport.Height + 0.5;
        List.Margin = new Thickness(0, 0, hasBar ? 14 : 0, 3);
    }

    /// <summary>临时布局诊断：定位列表滚不到底时 extent / viewport / 各行高的偏差。</summary>
    private void WriteLayoutDiag()
    {
        try
        {
            var panel = List.ItemsPanelRoot as Panel;
            var heights = panel?.Children.Select(c => c.Bounds.Height) ?? Enumerable.Empty<double>();
            var line = $"extent={Scroller.Extent.Height:F1} viewport={Scroller.Viewport.Height:F1} " +
                       $"listH={List.Bounds.Height:F1} scrollerH={Scroller.Bounds.Height:F1} " +
                       $"panelH={Panel.Bounds.Height:F1} items={panel?.Children.Count ?? 0} " +
                       $"heights=[{string.Join(",", heights.Select(h => h.ToString("F1")))}]";
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "optpicker-diag.log"), line + "\n");
        }
        catch { }
    }

    /// <summary>
    /// 小号档的类要挂在触发器与浮层上，而不是挂在控件自己身上：
    /// 控件自己的 Styles 只覆盖子树，选择器以自身类型打头（如 OptionPicker.compact）匹配不到自己。
    /// 关键尺寸同时写成局部值——局部值一定压过样式，不指望选择器链一路都对得上。
    /// </summary>
    private void SyncCompact()
    {
        Trigger.Classes.Set("mini", Compact);
        Panel.Classes.Set("mini", Compact);

        if (!Compact) return;

        Trigger.MinWidth = 0;
        Trigger.Height = 24;
        Trigger.Padding = new Thickness(8, 0, 6, 0);
        Cur.FontSize = 10.5;
        RoomTx.FontSize = 9.5;
        Chev.Margin = new Thickness(6, 0, 0, 0);
        // 行内的字号靠继承：浮层挂 11，列表项自己不再写死字号
        Panel.SetValue(TextBlock.FontSizeProperty, 11d);
        Panel.Padding = new Thickness(4);
    }

    /// <summary>胶囊形：类挂在触发器上，圆角在模板里的 Border 上，只能靠样式改。</summary>
    private void SyncPill()
    {
        Trigger.Classes.Set("pill", Pill);
        if (!Pill) return;

        // 样式里的 /template/ 选择器万一没命中（模板还没套上时），这里直接给本地值兜底
        foreach (var border in Trigger.GetVisualDescendants().OfType<Border>())
            if (border.Name is "Bd" or "Hov") border.CornerRadius = new CornerRadius(12);
    }

    /// <summary>
    /// 玻璃档的类同样挂在触发器和浮层上：悬停底、选中底、箭头浓淡都在这两处生效，
    /// 挂控件自身匹配不到自己的样式（同 Compact / Pill 的理由）。
    /// </summary>
    private void SyncGlass()
    {
        Trigger.Classes.Set("glass", Glass);
        Panel.Classes.Set("glass", Glass);
    }

    private void SyncTheme()
    {        if (ActualThemeVariant == ThemeVariant.Light) Panel.Classes.Add("light");
        else Panel.Classes.Remove("light");
    }

    private void SyncPopupTopmost()
    {
        if (Pop is not null) Pop.Topmost = TopmostPopup;
    }

    private void Rebind()
    {
        List.ItemsSource = Options;
        SyncSelection();
    }

    private void SyncSelection()
    {
        string? current = null;
        string? meta = null;
        if (Options is IEnumerable items)
        {
            foreach (var raw in items)
            {
                if (raw is not IPickOption option) continue;
                option.IsSelected = Equals(option.Value, SelectedValue);
                if (option.IsSelected)
                {
                    current = option.Label;
                    meta = option.Meta;
                }
            }
        }

        Cur.Text = current ?? "选择";
        // 触发器上的房间标注跟着选中项走，排在名称后面；趋势卡可关掉，房间只留在展开列表里
        RoomTx.Text = meta ?? "";
        Room.IsVisible = ShowMeta && !string.IsNullOrEmpty(meta);
    }

    private void OnItemClick(object? sender, RoutedEventArgs args)
    {
        // Click 的 Source 在不同模板下可能是文字/内容层，不一定是按钮本身；
        // 往上找带 IPickOption 的那个按钮，避免菜单关了却没写回选中值。
        if (ItemButtonOf(args.Source as Visual) is { Tag: IPickOption option })
            SetCurrentValue(SelectedValueProperty, option.Value);
        Pop.IsOpen = false;
    }

    /// <summary>按下就落选：桌面浮层压在桌面上时，按钮的 Click 偶发收不到；
    /// 直接按指针命中的行提交，菜单随即可关闭，不依赖按钮的点击语义。</summary>
    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Panel).Properties.IsLeftButtonPressed) return;
        if (ItemButtonOf(e.Source as Visual) is not { Tag: IPickOption option }) return;

        SetCurrentValue(SelectedValueProperty, option.Value);
        Pop.IsOpen = false;
        e.Handled = true;
    }

    private static Button? ItemButtonOf(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button { Tag: IPickOption }) return (Button)visual;
            // 浮层的根是 TopLevel：往上走到这里就说明已经出了本控件所在的模板。
            if (visual is TopLevel) break;
        }
        return null;
    }

    /// <summary>宿主窗口收起时（如托盘菜单）跟着关掉浮层，Popup 不会随窗口 Hide 一起消失。</summary>
    public void CloseDropDown()
    {
        Pop.IsOpen = false;
        // 宿主窗口都要收起来了，不再替触发器占位，直接交还悬停状态
        ClearRevealHold();
        DropReveal();
    }

    /// <summary>桌面卡是贴桌面的特殊窗口，按钮自己的 Click 常被窗口层的拖动命中判定吃掉；
    /// 由宿主在命中下拉区域时直接开合，绕开这条丢失的点击链。</summary>
    public void ToggleDropDown() => Pop.IsOpen = !Pop.IsOpen;

    /// <summary>浮层开着期间记下的按下点（顶层坐标），用来判断「收起它的那一下点在哪儿」。</summary>
    private Point? _hostPressPoint;

    private DateTime _hostPressAt;

    private TopLevel? _pressRoot;

    private void AttachPressProbe()
    {
        DetachPressProbe();
        _pressRoot = TopLevel.GetTopLevel(this);
        _pressRoot?.AddHandler(PointerPressedEvent, OnHostPointerPressed, RoutingStrategies.Tunnel, true);
    }

    private void DetachPressProbe()
    {
        // 只摘监听：按下点还要留给关浮层时判断点在哪儿
        _pressRoot?.RemoveHandler(PointerPressedEvent, OnHostPointerPressed);
    }

    private void OnHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_pressRoot is null) return;
        _hostPressPoint = e.GetPosition(_pressRoot);
        _hostPressAt = DateTime.UtcNow;
    }

    /// <summary>关浮层后仍替触发器占着「露面」状态的宿主（等指针离开它再交还）。</summary>
    private Control? _revealHold;

    /// <summary>占位期间挂指针移动兜底的顶层。</summary>
    private TopLevel? _revealHoldRoot;

    /// <summary>
    /// 关掉浮层后，把「触发器保持露面」的类留到指针真的离开宿主为止，而不是当场摘。
    /// <para>
    /// 点空白取消的那一下先被 LightDismiss 的透明层吃掉：光标明明还压在卡片上，
    /// 卡片这一帧的 <c>:pointerover</c> 却是空的（悬停挂在那层透明层上），要等下一次重画
    /// 才算回卡片。若关浮层时当场摘类，两枚胶囊会先淡出、下一帧再淡回 —— 就是那一下「闪」。
    /// </para>
    /// <para>
    /// 所以判断不看悬停状态（刚关时它是过期的），只看坐标：收起它的那一下点在宿主卡片上，
    /// 就把类留着，交给 <c>PointerExited</c> / 后续指针移动的位置来收尾；点在卡片外则照常淡出。
    /// </para>
    /// </summary>
    private void HoldRevealUntilPointerLeaves()
    {
        var host = FindRevealHost();
        var press = _hostPressPoint;
        var fresh = press is not null && (DateTime.UtcNow - _hostPressAt).TotalMilliseconds < 800;

        if (host is null || !fresh || !HitHost(host, press!.Value, _pressRoot))
        {
            Diag($"drop host={(host is null ? "null" : "ok")} press={(press is null ? "-" : "ok")} fresh={fresh}");
            DropReveal();
            return;
        }

        ClearRevealHold();
        _revealHold = host;
        host.PointerExited += OnRevealHostPointerExited;
        _revealHoldRoot = TopLevel.GetTopLevel(host);
        _revealHoldRoot?.AddHandler(PointerMovedEvent, OnRevealHostPointerMoved, RoutingStrategies.Tunnel, true);
        Diag($"hold size={host.Bounds.Size}");
    }

    private void OnRevealHostPointerMoved(object? sender, PointerEventArgs e)
    {
        var host = _revealHold;
        if (host is null) return;

        var root = _revealHoldRoot ?? TopLevel.GetTopLevel(host);
        if (root is null) return;

        if (!HitHost(host, e.GetPosition(root), root)) ReleaseRevealHold("move-out");
    }

    private void OnRevealHostPointerExited(object? sender, PointerEventArgs e) => ReleaseRevealHold("host-exit");

    /// <summary>指针已不在宿主的卡片上：占位到期，把「露面」类摘掉，两枚胶囊正常淡出。</summary>
    private void ReleaseRevealHold(string reason)
    {
        ClearRevealHold();
        Diag($"release {reason}");
        DropReveal();
    }

    /// <summary>只拆占位用的监听，不动已经挂上的类（浮层重开、窗口收起时用）。</summary>
    private void ClearRevealHold()
    {
        if (_revealHold is not null)
            _revealHold.PointerExited -= OnRevealHostPointerExited;
        _revealHoldRoot?.RemoveHandler(PointerMovedEvent, OnRevealHostPointerMoved);
        _revealHold = null;
        _revealHoldRoot = null;
    }

    private void DropReveal()
    {
        Classes.Set("open", false);
        SyncCardPickOpen(false);
    }

    /// <summary>点（顶层坐标）是否落在宿主矩形里；宿主可能在滚动区 / 变换里，用 TranslatePoint 换算。</summary>
    private static bool HitHost(Control host, Point point, TopLevel? root)
    {
        root ??= TopLevel.GetTopLevel(host);
        if (root is null) return false;

        var origin = host.TranslatePoint(new Point(0, 0), root);
        if (origin is null) return false;
        return new Rect(origin.Value, host.Bounds.Size).Contains(point);
    }

    /// <summary>
    /// 谁在替触发器挡住「平时不露面」：首页趋势卡是整张 Border（两个下拉要一起留在场上），
    /// 桌面数据卡是同级的 UserControl。规律一样：先找挂着 TrendCard 的 Border，
    /// 找不到就退回最近的 UserControl 祖先。
    /// </summary>
    private Control? FindRevealHost()
    {
        var ancestors = this.GetVisualAncestors().ToList();
        Control? card = ancestors.OfType<Border>().FirstOrDefault(b => b.Classes.Contains("TrendCard"));
        if (card is not null) return card;
        return ancestors.OfType<UserControl>().FirstOrDefault();
    }

    // ---------- 临时诊断：定位「点空白收起时胶囊闪一下」（定位完删掉） ----------
    private DispatcherTimer? _diagTimer;
    private DateTime _diagStart;

    private void Diag(string text)
    {
        try
        {
            var card = this.GetVisualAncestors().OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("TrendCard"));
            var ops = card is null
                ? "-"
                : string.Join("|", card.GetVisualDescendants().OfType<OptionPicker>()
                    .Select(p => $"{p.Opacity:F2}{(p.Classes.Contains("open") ? "*" : "")}"));
            var line = $"[{DateTime.Now:mm:ss.fff}] {text} op={Opacity:F2} ops=[{ops}] " +
                       $"card={(card is null ? "-" : string.Join("|", card.Classes))} " +
                       $"cardOver={(card?.IsPointerOver.ToString() ?? "-")}";
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "pick-reveal.log"), line + "\n");
        }
        catch { }
    }

    private void StartDiagSampling()
    {
        _diagTimer?.Stop();
        _diagStart = DateTime.UtcNow;
        _diagTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            var ms = (DateTime.UtcNow - _diagStart).TotalMilliseconds;
            Diag($"t={ms:F0}ms");
            if (ms > 420) _diagTimer?.Stop();
        });
        _diagTimer.Start();
    }

    /// <summary>
    /// 首页趋势卡同一张卡上有两个下拉（设备来源 + 时间范围）。浮层一开，卡片就收不到 :pointerover，
    /// 旁边那个触发器会跟着消失；这里给整张卡挂一个 .pick-open，让两个触发器一起留在场上。
    /// 关掉时先看卡里还有没有别的浮层开着，最后一个小号才摘类。
    /// </summary>
    private void SyncCardPickOpen(bool open)
    {
        var card = this.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("TrendCard"));
        if (card is null) return;

        if (open)
        {
            card.Classes.Add("pick-open");
            return;
        }

        if (card.GetVisualDescendants().OfType<OptionPicker>()
                .Any(p => !ReferenceEquals(p, this) && p.Classes.Contains("open")))
            return;
        card.Classes.Remove("pick-open");
    }

    /// <summary>浮层打开期间挂到所在窗口上的滚轮拦截宿主。</summary>
    private TopLevel? _wheelHost;

    /// <summary>
    /// Windows 上 Popup 收不到滚轮（AvaloniaUI/Avalonia#16646）：滚轮按光标坐标命中的是浮层底下那层，
    /// 于是滚下拉时背后的内容（侧边栏、设备列表）跟着滚。这里在窗口的隧道阶段先拦一道：
    /// 光标压在浮层卡片上就把滚轮量转给浮层列表，并标记 Handled 挡住背后。
    /// </summary>
    private void AttachWheelForwarder()
    {
        if (_wheelHost != null) return;
        _wheelHost = TopLevel.GetTopLevel(this);
        _wheelHost?.AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Tunnel, true);
    }

    private void DetachWheelForwarder()
    {
        if (_wheelHost == null) return;
        _wheelHost.RemoveHandler(PointerWheelChangedEvent, OnPreviewWheel);
        _wheelHost = null;
    }

    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        var host = TopLevel.GetTopLevel(Panel);
        if (!Pop.IsOpen || _wheelHost == null || host == null) return;

        // 光标的屏幕像素坐标 vs 浮层卡片的屏幕像素矩形（各自从所在 TopLevel 换算，跨窗口也成立）
        var cursor = _wheelHost.PointToScreen(e.GetPosition(_wheelHost));
        var origin = host.PointToScreen(Panel.Bounds.Position);
        var scale = host.RenderScaling;
        var card = new Rect(origin.X, origin.Y, Panel.Bounds.Width * scale, Panel.Bounds.Height * scale);
        if (!card.Contains(new Point(cursor.X, cursor.Y))) return;

        // 一步滚一格选项；到顶/到底就地夹住，不把空白滚出来
        var offset = Scroller.Offset;
        var y = Math.Clamp(offset.Y - e.Delta.Y * 34, 0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height));
        Scroller.Offset = new Vector(offset.X, y);
        e.Handled = true;
    }
}
