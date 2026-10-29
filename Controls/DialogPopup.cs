using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
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

    public static readonly StyledProperty<double> CardMaxHeightProperty =
        AvaloniaProperty.Register<DialogPopup, double>(nameof(CardMaxHeight), 640);

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<DialogPopup, ICommand?>(nameof(CloseCommand));

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

    public double CardMaxHeight
    {
        get => GetValue(CardMaxHeightProperty);
        set => SetValue(CardMaxHeightProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Source is Control { Classes: not null } source && source.Classes.Contains(MaskClass))
            Close();
    }

    /// <summary>走宿主给的关闭命令，让遮罩点击与右上角按钮落在同一个出口。</summary>
    public void Close() => CloseCommand?.Execute(null);
}
