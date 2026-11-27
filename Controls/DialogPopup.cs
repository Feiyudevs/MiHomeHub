// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MiHomeHub.Controls;

/// <summary>
/// 弹窗基类：遮罩 + 居中圆角卡片（浅描边 + 投影）+ 标题栏 + 可滚动正文。
/// 调用方只提供正文内容和 Title/Subtitle，显隐挂 IsOpen，关闭挂 CloseCommand。
/// </summary>
public class DialogPopup : ContentControl
{
    /// <summary>遮罩在模板里的类名，点它即请求关闭。</summary>
    public const string MaskClass = "Mask";

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<DialogPopup, bool>(nameof(IsOpen));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DialogPopup, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<DialogPopup, string?>(nameof(Subtitle));

    public static readonly StyledProperty<double> CardWidthProperty =
        AvaloniaProperty.Register<DialogPopup, double>(nameof(CardWidth), 760);

    /// <summary>卡片固定高度（双击 0/NaN 则按内容自适应，受 CardMaxHeight 限制）。设置页用固定值避免切 Tab 时尺寸跳动。</summary>
    public static readonly StyledProperty<double> CardHeightProperty =
        AvaloniaProperty.Register<DialogPopup, double>(nameof(CardHeight), double.NaN);

    public static readonly StyledProperty<double> CardMaxHeightProperty =
        AvaloniaProperty.Register<DialogPopup, double>(nameof(CardMaxHeight), 640);

    /// <summary>正文滚动区外边距（左,上,右,下）。默认 28,18,6,26；设置页等想让内容贴底时把下边距设 0。</summary>
    public static readonly StyledProperty<Thickness> ContentMarginProperty =
        AvaloniaProperty.Register<DialogPopup, Thickness>(nameof(ContentMargin), new Thickness(28, 18, 6, 26));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<DialogPopup, ICommand?>(nameof(CloseCommand));

    /// <summary>标题栏右上角、关闭钮左边的动作区（放一两个动作钮）。整块靠右排，为空时不影响关闭钮的位置。</summary>
    public static readonly StyledProperty<object?> HeaderActionsProperty =
        AvaloniaProperty.Register<DialogPopup, object?>(nameof(HeaderActions));

    /// <summary>
    /// 正文滚动区之外的底部常驻区（保存 / 取消这类操作钮）。
    /// 正文再长也只在中间滚动，底部操作始终可见；为空时整行不占位。
    /// </summary>
    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<DialogPopup, object?>(nameof(Footer));

    /// <summary>
    /// 标题栏按住可拖动，默认开启，不用每个弹窗再单独写一次。
    /// 独立弹窗窗体（整窗就是这张卡片）拖的是窗口；嵌在主窗口里的弹窗拖的是卡片本身，
    /// 主窗口不动。铺满父窗口、尺寸跟着宿主对齐的遮罩型窗体（触发条件 / 添加动作这类）
    /// 仍要显式关掉，拖了会和遮罩错位。
    /// </summary>
    public static readonly StyledProperty<bool> CanDragProperty =
        AvaloniaProperty.Register<DialogPopup, bool>(nameof(CanDrag), true);

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public double CardWidth
    {
        get => GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    public double CardHeight
    {
        get => GetValue(CardHeightProperty);
        set => SetValue(CardHeightProperty, value);
    }

    public double CardMaxHeight
    {
        get => GetValue(CardMaxHeightProperty);
        set => SetValue(CardMaxHeightProperty, value);
    }

    public Thickness ContentMargin
    {
        get => GetValue(ContentMarginProperty);
        set => SetValue(ContentMarginProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public object? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public bool CanDrag
    {
        get => GetValue(CanDragProperty);
        set => SetValue(CanDragProperty, value);
    }

    static DialogPopup()
    {
        // 运行中改了 CanDrag 就顺手把标题栏的手型光标换过来
        CanDragProperty.Changed.AddClassHandler<DialogPopup>((popup, _) => popup.UpdateHeaderCursor());
    }

    /// <summary>模板里的标题栏，拖动与手型光标都挂在它上面。</summary>
    private Control? _header;

    /// <summary>模板里的卡片本体：嵌在主窗口里时拖的是它，不是窗口。</summary>
    private Border? _card;

    /// <summary>拖卡片时记下按下位置和当时的位移，移动量按差值算，不会越拖越飘。</summary>
    private Point _pressPoint;
    private Vector _pressOffset;
    private bool _draggingCard;

    /// <summary>
    /// 宿主是不是主窗口：主窗口里的弹窗（设置、数据看板编辑、场景编辑…）只拖卡片自己，
    /// 卡片跟着遮罩一起居中摆着，拖它不该把整个软件挪走；
    /// 其余都是独立弹窗窗体（整窗就是这张卡片），拖标题栏等于拖窗口。
    /// </summary>
    private static bool IsMainWindowHost(Window win) =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        && ReferenceEquals(win, desktop.MainWindow);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (e.NameScope.Find<Border>("DialogCard") is { } card)
            _card = card;

        // 模板里的标题栏（Panel 名为 DialogHeader）负责拖动
        if (e.NameScope.Find<Control>("DialogHeader") is { } header)
        {
            _header = header;
            header.PointerPressed -= OnHeaderPressed;
            header.PointerPressed += OnHeaderPressed;
            header.PointerMoved -= OnHeaderMoved;
            header.PointerMoved += OnHeaderMoved;
            header.PointerReleased -= OnHeaderReleased;
            header.PointerReleased += OnHeaderReleased;
            header.PointerCaptureLost -= OnHeaderCaptureLost;
            header.PointerCaptureLost += OnHeaderCaptureLost;
            UpdateHeaderCursor();
        }
    }

    private void UpdateHeaderCursor()
    {
        if (_header is null) return;
        _header.Cursor = CanDrag ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanDrag) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (TopLevel.GetTopLevel(this) is not Window win) return;

        // 标题栏上压着按钮（关闭 / 动作钮）时让按钮自己吃这次点击，不要变成拖动
        if (e.Source is Visual source &&
            (source is Button || source.GetVisualAncestors().OfType<Button>().Any())) return;

        if (IsMainWindowHost(win))
        {
            if (_card is null) return;
            _draggingCard = true;
            _pressPoint = e.GetPosition(this);
            _pressOffset = CurrentCardOffset();
            e.Pointer.Capture(_header);
        }
        else
        {
            win.BeginMoveDrag(e);
        }

        e.Handled = true;
    }

    private void OnHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingCard || _card is null) return;

        var offset = ClampCardOffset(_pressOffset + (e.GetPosition(this) - _pressPoint));
        _card.RenderTransform = new TranslateTransform(offset.X, offset.Y);
        e.Handled = true;
    }

    private void OnHeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_draggingCard) return;
        _draggingCard = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnHeaderCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _draggingCard = false;

    private Vector CurrentCardOffset() =>
        _card?.RenderTransform is TranslateTransform t ? new Vector(t.X, t.Y) : default;

    /// <summary>卡片拖出去也不能离开弹窗外壳：左沿不出左边、右沿不出右边，上下同理。</summary>
    private Vector ClampCardOffset(Vector offset)
    {
        if (_card is null) return offset;

        var card = _card.Bounds;
        var area = Bounds;

        var x = offset.X;
        var y = offset.Y;
        if (card.Width <= area.Width) x = Math.Clamp(x, -card.X, area.Width - card.Right);
        if (card.Height <= area.Height) y = Math.Clamp(y, -card.Y, area.Height - card.Bottom);
        return new Vector(x, y);
    }

    private void ResetCardOffset()
    {
        _draggingCard = false;
        if (_card is not null) _card.RenderTransform = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsOpenProperty && change.GetNewValue<bool>() is false)
        {
            // 关掉就把位移清掉，下次打开仍是居中的位置
            ResetCardOffset();
        }
        else if (change.Property == BoundsProperty && _card is not null && CurrentCardOffset() != default)
        {
            // 窗口被改尺寸时把卡片收回到弹窗范围内
            var offset = ClampCardOffset(CurrentCardOffset());
            _card.RenderTransform = new TranslateTransform(offset.X, offset.Y);
        }
    }

    /// <summary>
    /// 点遮罩关闭。判定放在按下而不是抬起：拖动标题栏移动窗口时，抬起的那一刻光标可能已经在遮罩上，
    /// 用抬起判定会把「拖完松手」误当成「点了遮罩」，窗口直接被关掉。
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Source is Control { Classes: not null } source && source.Classes.Contains(MaskClass))
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>走宿主给的关闭命令，让遮罩点击与右上角按钮落在同一个出口。</summary>
    public void Close() => CloseCommand?.Execute(null);
}
