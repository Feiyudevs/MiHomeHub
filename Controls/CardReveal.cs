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
/// 「数据看板」这类整块出现的卡片用 <paramref name="soft"/> 那一档：位移更短、时间更长、
/// 缓动换成前面收得很快、末尾拖得很长的曲线，看起来像轻轻托起来，而不是被推到位。
/// 位移挂在 item 容器的 RenderTransform 上 —— 容器没有别处设置的 RenderTransform，
/// 不会和卡片自身的悬停抬起冲突。
/// </summary>
internal static class CardReveal
{
    private const int StepMs = 30;
    private const int MaxStep = 10;
    private const double EnterMs = 240;

    /// <summary>柔和档：上浮 14、总时长 420ms。</summary>
    private const double SoftEnterMs = 420;
    private const double SoftOffsetY = 14;

    /// <summary>第 index 张卡片的入场延迟：0、30、60…300ms，之后一律 300ms。</summary>
    public static int DelayFor(int index) => Math.Min(Math.Max(index, 0), MaxStep) * StepMs;

    /// <summary>
    /// 先把容器压成全透明再播动画。调用点必须落在卡片上屏之前（容器准备 / 布局完成后的渲染前），
    /// 否则会看到「卡片先整块亮一帧、再被拉回透明重放」的闪屏。
    /// 延迟写进关键帧时间线（前两个关键帧数值相同＝原地等）而不是 Animation.Delay：
    /// 这样动画第一拍就接管了不透明度，回收池里带着上一次终值的容器也不会漏亮一帧。
    /// </summary>
    /// <param name="soft">true：上浮 + 长缓动的柔和档（数据看板）；false：原来的右侧轻推（列表）。</param>
    public static void Play(Control card, int delayMs, bool soft = false)
    {
        var delay = Math.Max(0, delayMs);
        var enterMs = soft ? SoftEnterMs : EnterMs;

        // 位移轴也跟着换：柔和档是「从下面托起来」，列表还是从右侧推入
        var offsetX = soft ? 0 : 20;
        var offsetY = soft ? SoftOffsetY : 0;
        var offsetProperty = soft ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        var offsetStart = soft ? SoftOffsetY : 20d;

        card.RenderTransform = new TranslateTransform(offsetX, offsetY);
        card.Opacity = 0;

        var total = TimeSpan.FromMilliseconds(delay + enterMs);
        var anim = new Animation
        {
            Duration = total,
            // 柔和档用「前段快、后段绵长」的曲线：4/5 的时间就走完大半位移，剩下慢慢落定
            Easing = soft ? new SplineEasing(0.16, 1, 0.3, 1) : new CubicEaseOut(),
            FillMode = FillMode.Forward,
        };
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters =
            {
                new Setter(Visual.OpacityProperty, 0d),
                new Setter(offsetProperty, offsetStart),
            }
        });
        if (delay > 0)
        {
            anim.Children.Add(new KeyFrame
            {
                Cue = new Cue(delay / (delay + enterMs)),
                Setters =
                {
                    new Setter(Visual.OpacityProperty, 0d),
                    new Setter(offsetProperty, offsetStart),
                }
            });
        }
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters =
            {
                new Setter(Visual.OpacityProperty, 1d),
                new Setter(offsetProperty, 0d),
            }
        });
        _ = anim.RunAsync(card);
    }
}
