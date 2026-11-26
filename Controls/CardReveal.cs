// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace MiHomeHub.Controls;

/// <summary>
/// 列表卡片的入场动画：从右侧 20px 移入并淡入，同一列表第 N 张延后 30ms（最多错开 10 张）。
/// 位移挂在 item 容器的 RenderTransform 上 —— 容器没有别处设置的 RenderTransform，
/// 不会和卡片自身的悬停抬起冲突。
/// </summary>
internal static class CardReveal
{
    private const int StepMs = 30;
    private const int MaxStep = 10;
    private const double EnterMs = 240;

    /// <summary>第 index 张卡片的入场延迟：0、30、60…300ms，之后一律 300ms。</summary>
    public static int DelayFor(int index) => Math.Min(Math.Max(index, 0), MaxStep) * StepMs;

    /// <summary>
    /// 先把容器压成全透明再播动画。调用点必须落在卡片上屏之前（容器准备 / 布局完成后的渲染前），
    /// 否则会看到「卡片先整块亮一帧、再被拉回透明重放」的闪屏。
    /// 延迟写进关键帧时间线（前两个关键帧数值相同＝原地等）而不是 Animation.Delay：
    /// 这样动画第一拍就接管了不透明度，回收池里带着上一次终值的容器也不会漏亮一帧。
    /// </summary>
    public static void Play(Control card, int delayMs)
    {
        var delay = Math.Max(0, delayMs);
        card.RenderTransform = new TranslateTransform(20, 0);
        card.Opacity = 0;

        var total = TimeSpan.FromMilliseconds(delay + EnterMs);
        var anim = new Animation
        {
            Duration = total,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
        };
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters =
            {
                new Setter(Visual.OpacityProperty, 0d),
                new Setter(TranslateTransform.XProperty, 20d),
            }
        });
        if (delay > 0)
        {
            anim.Children.Add(new KeyFrame
            {
                Cue = new Cue(delay / (delay + EnterMs)),
                Setters =
                {
                    new Setter(Visual.OpacityProperty, 0d),
                    new Setter(TranslateTransform.XProperty, 20d),
                }
            });
        }
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters =
            {
                new Setter(Visual.OpacityProperty, 1d),
                new Setter(TranslateTransform.XProperty, 0d),
            }
        });
        _ = anim.RunAsync(card);
    }
}
