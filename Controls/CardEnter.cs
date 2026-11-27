// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MiHomeHub.Controls;

/// <summary>
/// 非列表卡片的入场动画：卡片自己或它所在的整块从看不见变成看得见时，按同排可见顺序依次淡入。
/// 首页「数据看板」这类直接摆进面板的卡片用它；设备/场景列表仍由 <see cref="RecyclingItemsControl"/> 负责。
///
/// 用法：
/// <code>
/// &lt;ContentControl ctl:CardEnter.Enabled="True" ... /&gt;
/// </code>
/// 只挂在卡片容器（ContentControl）上，不要挂到卡片模板里的 Border：容器没有别处设置的
/// RenderTransform，不会和卡片自身的悬停效果打架。
/// </summary>
public static class CardEnter
{
    /// <summary>挂到卡片容器上：容器出现时播一次入场动画。</summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<object, Control, bool>("Enabled");

    private sealed class WatchState
    {
        public bool WasVisible;
        public EventHandler<AvaloniaPropertyChangedEventArgs>? VisibilityChanged;
        public EventHandler<VisualTreeAttachmentEventArgs>? Attached;
        public EventHandler<VisualTreeAttachmentEventArgs>? Detached;
        public readonly List<AvaloniaObject> Observed = new();
    }

    private static readonly ConditionalWeakTable<Control, WatchState> Watches = new();

    static CardEnter()
    {
        // XAML 直接设 AvaloniaProperty，不保证调用 CLR setter，订阅必须跟随属性变更。
        EnabledProperty.Changed.AddClassHandler<Control>((card, change) =>
        {
            if (change.GetNewValue<bool>()) StartWatching(card);
            else StopWatching(card);
        });
    }

    public static bool GetEnabled(Control element) => element.GetValue(EnabledProperty);

    public static void SetEnabled(Control element, bool value) => element.SetValue(EnabledProperty, value);

    private static void StartWatching(Control card)
    {
        if (Watches.TryGetValue(card, out _)) return;

        var state = new WatchState();
        Watches.Add(card, state);

        // 自己的显隐翻转（编辑弹窗里单独打开一张卡）也算一次出现，单张不排队，点完立刻浮现
        state.VisibilityChanged = (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty) Sync(card, state, stagger: false);
        };
        // 整块（例如「数据看板」随首拉骨架换成真卡）翻转时，祖先的 IsVisible 才是真正的信号：
        // 卡片自己的 IsVisible 一直为 true，不会发出通知。这一批按同排可见顺序依次入场。
        state.Attached = (_, _) =>
        {
            ObserveAncestors(card, state);
            Sync(card, state, stagger: true);
        };
        state.Detached = (_, _) =>
        {
            UnobserveAncestors(state);
            state.WasVisible = false;
        };

        card.PropertyChanged += state.VisibilityChanged;
        card.AttachedToVisualTree += state.Attached;
        card.DetachedFromVisualTree += state.Detached;

        // 属性是在已上屏的卡片上补挂的：先补齐祖先订阅，再按当前状态补一次
        if (card.GetVisualRoot() is not null)
        {
            ObserveAncestors(card, state);
            Sync(card, state, stagger: true);
        }
    }

    private static void StopWatching(Control card)
    {
        if (!Watches.TryGetValue(card, out var state)) return;

        card.PropertyChanged -= state.VisibilityChanged!;
        card.AttachedToVisualTree -= state.Attached!;
        card.DetachedFromVisualTree -= state.Detached!;
        UnobserveAncestors(state);
        Watches.Remove(card);
    }

    private static void ObserveAncestors(Control card, WatchState state)
    {
        UnobserveAncestors(state);
        foreach (var ancestor in card.GetVisualAncestors())
        {
            ancestor.PropertyChanged += state.VisibilityChanged!;
            state.Observed.Add(ancestor);
        }
    }

    private static void UnobserveAncestors(WatchState state)
    {
        foreach (var ancestor in state.Observed) ancestor.PropertyChanged -= state.VisibilityChanged!;
        state.Observed.Clear();
    }

    /// <summary>
    /// 只有「从看不见变成看得见」才播：卡片被收起、整块被切走都只更新状态，
    /// 重新回到首页 / 重新打开时才从右侧移入。
    /// </summary>
    private static void Sync(Control card, WatchState state, bool stagger)
    {
        var visible = IsVisibleNow(card);
        if (!visible)
        {
            state.WasVisible = false;
            return;
        }
        if (state.WasVisible) return;

        state.WasVisible = true;
        // 数据看板的卡片是整块出现的，用比列表更慢、更轻的上浮档，别像被一下子推到位
        CardReveal.Play(card, stagger ? CardReveal.DelayFor(OrderIn(card)) : 0, soft: true);
    }

    /// <summary>卡片真正上屏：挂在可视化树上，且整条祖先链都没有被收起。</summary>
    private static bool IsVisibleNow(Control card)
    {
        if (card.GetVisualRoot() is null) return false;

        Visual? current = card;
        while (current is not null)
        {
            if (!current.IsVisible) return false;
            current = current.GetVisualParent();
        }
        return true;
    }

    /// <summary>同一排里第几张可见的卡片：整块出现时 0、1、2… 依次错开。
    /// 按卡片在面板里的实际位置从上到下、从左到右数，用户拖动换序后动画顺序也跟着新位置走；
    /// 刚挂上树还没量出位置时（Bounds 全为 0），退回 Children 的先后顺序。</summary>
    private static int OrderIn(Control card)
    {
        if (card.GetVisualParent() is not Panel panel) return 0;

        var siblings = panel.Children
            .OfType<Control>()
            .Where(child => GetEnabled(child) && IsVisibleNow(child))
            .OrderBy(child => child.Bounds.Y)
            .ThenBy(child => child.Bounds.X)
            .ToList();

        var index = siblings.IndexOf(card);
        return index < 0 ? 0 : index;
    }
}
