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
/// 手感基准：点击后第一帧就要「亮」，位移只做 6px 的落定，
/// 整体压在 150ms 内结束 —— 再长就会被感知成迟钝。
/// </summary>
internal static class OverlayReveal
{
    // ── 时长：打开=淡入快、位移稍长；关闭=两段都短，绝不拖住下一次点击 ──
    private static readonly TimeSpan OpenSlide = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan OpenFade = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CloseSlide = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan CloseFade = TimeSpan.FromMilliseconds(90);

    /// <summary>收起终点：负值让卡片往标题栏方向退，视觉上像「缩回胶囊」。</summary>
    private const double HiddenOffset = -6d;

    /// <summary>
    /// 驱动一个带 TranslateTransform 的浮层开关。
    /// <paramref name="isStillOpen"/> 在收起动画结束时回读 VM 状态：
    /// 动画期间用户又点开了，就保持可见。
    /// </summary>
    public static void Run(Border panel, bool open, Func<bool> isStillOpen)
    {
        if (panel.RenderTransform is not TranslateTransform tx) return;

        var fromY = open ? HiddenOffset : tx.Y;
        var toY = open ? 0d : HiddenOffset;
        var fromOpacity = open ? 0d : panel.Opacity;
        var toOpacity = open ? 1d : 0d;

        panel.IsVisible = true;
        tx.Y = fromY;
        // 打开时先压到全透明，否则布局后的第一帧会以满不透明度闪一下再被动画拉回 0，
        // 观感就是「先闪、再淡入」的顿挫。
        if (open) panel.Opacity = 0d;

        // 打开用 QuinticEaseOut：起步极快、收尾顺滑，卡片几乎立刻到位；
        // 关闭用 CubicEaseIn：越走越快，干脆利落。
        var slide = new Animation
        {
            Duration = open ? OpenSlide : CloseSlide,
            Easing = open ? new QuinticEaseOut() : new CubicEaseIn(),
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

        // 透明度独立跑一条更短的曲线：先「亮」起来，剩下的位移只做轻微落定，
        // 这样即使位移还在走，用户也已经能看清并点击卡片了。
        _ = Fade(fromOpacity, toOpacity, open ? OpenFade : CloseFade,
                 open ? new CubicEaseOut() : new CubicEaseIn()).RunAsync(panel);

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
