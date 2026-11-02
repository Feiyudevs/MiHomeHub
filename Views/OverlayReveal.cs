using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MiHome.Views;

/// <summary>
/// 标题栏胶囊浮层的开合动画：打开淡入轻落、关闭反向收起后再隐藏，
/// 避免收起过程中残留的浮层挡住下面的点击。小爱与音乐两个浮层共用。
/// </summary>
internal static class OverlayReveal
{
    /// <summary>
    /// 驱动一个带 TranslateTransform 的浮层开关。
    /// <paramref name="isStillOpen"/> 在收起动画结束时回读 VM 状态：
    /// 动画期间用户又点开了，就保持可见。
    /// </summary>
    public static void Run(Border panel, bool open, Func<bool> isStillOpen)
    {
        if (panel.RenderTransform is not TranslateTransform tx) return;

        var duration = TimeSpan.FromMilliseconds(open ? 240 : 160);
        Easing ease = open ? new CubicEaseOut() : new CubicEaseIn();
        var fromY = open ? -8d : tx.Y;
        var fromOpacity = open ? 0d : panel.Opacity;

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
            Setters = { new Setter(TranslateTransform.YProperty, open ? 0d : -8d) }
        });

        _ = Fade(fromOpacity, open ? 1d : 0d, duration, ease).RunAsync(panel);
        _ = slide.RunAsync(panel).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                if (!open && !isStillOpen()) panel.IsVisible = false;
                tx.Y = 0;
            }));
    }

    /// <summary>构造一个透明度关键帧动画（0% 起、100% 止，FillMode.Forward 保持终值）。</summary>
    private static Animation Fade(double from, double to, TimeSpan duration, Easing ease)
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
