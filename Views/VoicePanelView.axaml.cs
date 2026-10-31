using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 小爱语音指令浮层：由标题栏胶囊唤出。
/// 浮层可见性由动画驱动（打开淡入轻落 / 关闭收起后隐藏），开合动画随本控件走。
/// </summary>
public partial class VoicePanelView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public VoicePanelView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // 若初始就是打开状态则直接落位（无动画）
        if (Vm is { IsVoiceOpen: true })
        {
            VoicePanel.IsVisible = true;
            VoicePanel.Opacity = 1;
        }
    }

    private void OnVoiceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; // 回车直接发送，不插入换行
        Vm?.SendVoiceCommand.Execute(null);
    }

    /// <summary>
    /// 浮层开合：打开时淡入并从标题栏下方轻落，关闭时反向收起后再隐藏，
    /// 避免收起过程中残留的浮层挡住下面的点击。
    /// </summary>
    public void Animate(bool open)
    {
        var panel = VoicePanel;
        if (panel.RenderTransform is not TranslateTransform tx) return;

        var duration = TimeSpan.FromMilliseconds(open ? 240 : 160);
        Easing ease = open ? new CubicEaseOut() : new CubicEaseIn();
        var fromY = open ? -8d : tx.Y;
        var toY = open ? 0d : -8d;
        var fromOpacity = open ? 0d : panel.Opacity;
        var toOpacity = open ? 1d : 0d;

        panel.IsVisible = true;
        tx.Y = fromY;

        var slide = new Animation
        {
            Duration = duration,
            Easing = ease,
            FillMode = FillMode.Forward
        };
        slide.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Setter(TranslateTransform.YProperty, fromY) }
        });
        slide.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Setter(TranslateTransform.YProperty, toY) }
        });

        _ = FadeAnimation(fromOpacity, toOpacity, duration, ease).RunAsync(panel);
        _ = slide.RunAsync(panel).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                // 收起动画结束后若用户又打开了，就保持可见（以 VM 的当前状态为准）
                if (!open && Vm?.IsVoiceOpen != true) panel.IsVisible = false;
                tx.Y = 0;
            }));
    }

    /// <summary>构造一个透明度关键帧动画（0% 起、100% 止，FillMode.Forward 保持终值）。</summary>
    private static Animation FadeAnimation(double from, double to, TimeSpan duration, Easing ease)
    {
        var anim = new Animation
        {
            Duration = duration,
            Easing = ease,
            FillMode = FillMode.Forward
        };
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Setter(Visual.OpacityProperty, from) }
        });
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Setter(Visual.OpacityProperty, to) }
        });
        return anim;
    }
}
