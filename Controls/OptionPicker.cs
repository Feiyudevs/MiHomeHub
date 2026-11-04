using System;
using System.Collections;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using MiHome.Models;

namespace MiHome.Controls;

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
            AttachWheelForwarder();
            // 先让浮层以闭合态挂上树，下一帧再翻到展开态，过渡才有得播
            Dispatcher.UIThread.Post(() => Panel.Classes.Add("open"), DispatcherPriority.Render);
        };
        Pop.Closed += (_, _) =>
        {
            Trigger.Classes.Remove("open");
            Panel.Classes.Remove("open");
            DetachWheelForwarder();
        };

        // 行按钮在 ItemTemplate 里，挂事件路由接一次比每条模板写 Click 稳
        Panel.AddHandler(Button.ClickEvent, OnItemClick);

        ActualThemeVariantChanged += (_, _) => SyncTheme();
        SyncTheme();

        Rebind();
    }

    private void SyncTheme()
    {
        if (ActualThemeVariant == ThemeVariant.Light) Panel.Classes.Add("light");
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
        // 触发器上的房间标注跟着选中项走，没有就不留空底板
        PreTxt.Text = meta ?? "";
        Pre.IsVisible = !string.IsNullOrEmpty(meta);
    }

    private void OnItemClick(object? sender, RoutedEventArgs args)
    {
        if (args.Source is Button { Tag: IPickOption option })
            SetCurrentValue(SelectedValueProperty, option.Value);
        Pop.IsOpen = false;
    }

    /// <summary>宿主窗口收起时（如托盘菜单）跟着关掉浮层，Popup 不会随窗口 Hide 一起消失。</summary>
    public void CloseDropDown() => Pop.IsOpen = false;

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
