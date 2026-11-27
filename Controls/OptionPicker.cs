// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Controls.Primitives.PopupPositioning;
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
    }

    public OptionPicker()
    {
        InitializeComponent();

        Pop.PlacementTarget = Trigger;
        // 贴近窗口底部时（如小爱语音浮层）自动翻到触发点上方，避免列表伸出屏幕
        Pop.PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideY
            | PopupPositionerConstraintAdjustment.FlipY;
        Trigger.Click += (_, _) => Pop.IsOpen = !Pop.IsOpen;

        Pop.Opened += (_, _) =>
        {
            Panel.MinWidth = Trigger.Bounds.Width;
            Trigger.Classes.Add("open");
            // 控件自己也挂一个 .open：宿主（如首页趋势卡）靠它把「悬停才露面的触发器」在浮层开着时留住
            Classes.Set("open", true);
            SyncCardPickOpen(true);
            AttachWheelForwarder();
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
            Classes.Set("open", false);
            SyncCardPickOpen(false);
            DetachWheelForwarder();
        };

        // 行按钮在 ItemTemplate 里，挂事件路由接一次比每条模板写 Click 稳
        Panel.AddHandler(Button.ClickEvent, OnItemClick);

        ActualThemeVariantChanged += (_, _) => SyncTheme();
        SyncTheme();
        SyncCompact();
        SyncPill();

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

    private void SyncTheme()
    {        if (ActualThemeVariant == ThemeVariant.Light) Panel.Classes.Add("light");
        else Panel.Classes.Remove("light");
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
        if (args.Source is Button { Tag: IPickOption option })
            SetCurrentValue(SelectedValueProperty, option.Value);
        Pop.IsOpen = false;
    }

    /// <summary>宿主窗口收起时（如托盘菜单）跟着关掉浮层，Popup 不会随窗口 Hide 一起消失。</summary>
    public void CloseDropDown() => Pop.IsOpen = false;

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
