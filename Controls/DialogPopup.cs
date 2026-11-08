using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MiHome.Controls;

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

    /// <summary>标题栏是否可拖动宿主窗口（弹窗宿主是自绘无边框窗时用）。</summary>
    public static readonly StyledProperty<bool> CanDragProperty =
        AvaloniaProperty.Register<DialogPopup, bool>(nameof(CanDrag));

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

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // 模板里的标题栏（Panel 名为 DialogHeader）负责拖动
        if (e.NameScope.Find<Control>("DialogHeader") is { } header)
        {
            header.PointerPressed -= OnHeaderPressed;
            header.PointerPressed += OnHeaderPressed;
            header.Cursor = CanDrag ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
        }
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanDrag) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (TopLevel.GetTopLevel(this) is not Window win) return;

        win.BeginMoveDrag(e);
        e.Handled = true;
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
