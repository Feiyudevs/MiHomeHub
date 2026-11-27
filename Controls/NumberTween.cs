// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MiHomeHub.Controls;

/// <summary>
/// 数字读数的缓动：绑定的文字从旧值换到新值时，不直接跳过去，而是在原位把数字顺着滑到新值。
/// 只对「整段是一个数字」的文字生效（数据看板的大读数就是这种），遇到「—」「暂无」这类占位
/// 立刻交还控制权，下一次有数直接显示。
///
/// 用法：挂在 TextBlock 上，文字照常绑定：
/// <code>
/// &lt;TextBlock Classes="TrendValue" ctl:NumberTween.Enabled="True" Text="{Binding TempTrendValue}" /&gt;
/// </code>
/// 动画期间写回文字用 SetCurrentValue，不动绑定的值来源——下一次数据刷新仍会正常通知进来。
/// </summary>
public static class NumberTween
{
    /// <summary>数字从旧值滑到新值的时长：比图表过渡稍短，读数和曲线几乎同时落定。</summary>
    private const double DurationMs = 420;

    /// <summary>挂到 TextBlock 上：文字是数字时，变化走缓动。</summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<object, TextBlock, bool>("Enabled");

    private sealed class State
    {
        public bool Applying;
        public bool HasNumber;
        public double Current;
        public string Prefix = "";
        public string Suffix = "";
        public string TargetText = "";
        public double StartValue;
        public double EndValue;
        public int Decimals;
        public Stopwatch? Watch;
        public DispatcherTimer? Timer;
        public EventHandler<AvaloniaPropertyChangedEventArgs>? TextChanged;
    }

    private static readonly ConditionalWeakTable<TextBlock, State> States = new();

    static NumberTween()
    {
        // XAML 直接设 AvaloniaProperty，不保证调用 CLR setter，订阅必须跟随属性变更
        EnabledProperty.Changed.AddClassHandler<TextBlock>((block, change) =>
        {
            if (change.GetNewValue<bool>()) Attach(block);
            else Detach(block);
        });
    }

    public static bool GetEnabled(TextBlock element) => element.GetValue(EnabledProperty);

    public static void SetEnabled(TextBlock element, bool value) => element.SetValue(EnabledProperty, value);

    private static void Attach(TextBlock block)
    {
        if (States.TryGetValue(block, out _)) return;

        var state = new State();
        States.Add(block, state);

        // 先把当前文字读进来当起点：动画不应该从 0 开始，而是从屏幕上那个数接着走
        if (TryParse(block.Text, out var prefix, out var value, out var suffix, out var decimals))
        {
            state.HasNumber = true;
            state.Current = value;
            state.Prefix = prefix;
            state.Suffix = suffix;
            state.Decimals = decimals;
            state.TargetText = block.Text ?? "";
        }

        state.TextChanged = (_, e) =>
        {
            if (e.Property != TextBlock.TextProperty || state.Applying) return;
            HandleText(block, state, e.GetNewValue<string?>());
        };
        block.PropertyChanged += state.TextChanged;
    }

    private static void Detach(TextBlock block)
    {
        if (!States.TryGetValue(block, out var state)) return;

        block.PropertyChanged -= state.TextChanged!;
        state.Timer?.Stop();
        state.Watch = null;
        States.Remove(block);
    }

    private static void HandleText(TextBlock block, State state, string? text)
    {
        if (text is null) return;

        if (!TryParse(text, out var prefix, out var target, out var suffix, out var decimals))
        {
            // 「—」「正在采样」这类不是数字：停掉过渡并忘掉旧值，下一次有数直接显示
            state.Timer?.Stop();
            state.Watch = null;
            state.HasNumber = false;
            state.TargetText = text;
            return;
        }

        state.Prefix = prefix;
        state.Suffix = suffix;
        state.Decimals = decimals;
        state.TargetText = text;

        // 首次出现或刚离开占位：直接落值，卡片的入场动画会盖住这一下
        if (!state.HasNumber)
        {
            state.HasNumber = true;
            state.Current = target;
            return;
        }

        if (Math.Abs(target - state.Current) < 1e-9) return;

        state.StartValue = state.Current;
        state.EndValue = target;
        state.Watch = Stopwatch.StartNew();

        // 绑定刚把新值推上来，这一帧会先画出终值；先把显示拉回起点，动画从屏幕上原来的数接着走
        state.Applying = true;
        block.SetCurrentValue(TextBlock.TextProperty,
            state.Prefix + FormatValue(state.StartValue, state.Decimals) + state.Suffix);
        state.Applying = false;

        if (state.Timer is null)
        {
            state.Timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Render, (_, _) => Tick(block, state));
        }
        state.Timer.Start();
    }

    private static void Tick(TextBlock block, State state)
    {
        if (state.Watch is null) return;

        // 页面切走 / 卡片收起时直接收口，不要回来时还在半路上
        if (!block.IsVisible || block.GetVisualRoot() is null)
        {
            state.Current = state.EndValue;
            state.Watch = null;
            state.Timer?.Stop();
            state.Applying = true;
            block.SetCurrentValue(TextBlock.TextProperty, state.TargetText);
            state.Applying = false;
            return;
        }

        var raw = Math.Min(1.0, state.Watch.Elapsed.TotalMilliseconds / DurationMs);
        // 三次缓出：起步就把大部分差值走掉，最后一段慢慢贴住目标
        var eased = 1 - Math.Pow(1 - raw, 3);
        var value = state.StartValue + (state.EndValue - state.StartValue) * eased;
        state.Current = value;

        var text = state.Prefix + FormatValue(value, state.Decimals) + state.Suffix;
        state.Applying = true;
        block.SetCurrentValue(TextBlock.TextProperty, text);
        state.Applying = false;

        if (raw >= 1)
        {
            state.Applying = true;
            block.SetCurrentValue(TextBlock.TextProperty, state.TargetText);
            state.Applying = false;
            state.Current = state.EndValue;
            state.Watch = null;
            state.Timer?.Stop();
        }
    }

    /// <summary>固定小数位输出，去掉末尾多余的 0（目标文字本来就不带），避免过渡中出现 1.200 这种写法。</summary>
    private static string FormatValue(double value, int decimals)
    {
        var text = value.ToString("F" + decimals, CultureInfo.CurrentCulture);
        if (decimals <= 0) return text;

        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var dot = text.LastIndexOf(separator, StringComparison.Ordinal);
        if (dot < 0) return text;

        var end = text.Length - 1;
        while (end > dot && text[end] == '0') end--;
        return end == dot ? text[..dot] : text[..(end + 1)];
    }

    /// <summary>
    /// 从一段文字里挑出第一个数字，前后原样留下（大读数目前是纯数字，这里兼容「+1.2℃」这类写法）。
    /// 小数位按文字里实际写出的位数记，最多 3 位。
    /// </summary>
    private static bool TryParse(string? text, out string prefix, out double value, out string suffix, out int decimals)
    {
        prefix = "";
        suffix = "";
        value = 0;
        decimals = 0;
        if (string.IsNullOrEmpty(text)) return false;

        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsDigit(c))
            {
                start = i > 0 && (text[i - 1] == '-' || text[i - 1] == '+') ? i - 1 : i;
                break;
            }
            if ((c == '-' || c == '+') && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                start = i;
                break;
            }
        }
        if (start < 0) return false;

        var end = start;
        if (text[end] == '-' || text[end] == '+') end++;
        while (end < text.Length && char.IsDigit(text[end])) end++;

        var fraction = 0;
        if (end < text.Length && (text[end] == '.' || text[end] == ','))
        {
            var probe = end + 1;
            while (probe < text.Length && char.IsDigit(text[probe])) probe++;
            fraction = probe - (end + 1);
            if (fraction > 0) end = probe;
            else fraction = 0;
        }

        prefix = text[..start];
        suffix = text[end..];

        var number = text[start..end];
        if (CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator == ",")
            number = number.Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;

        decimals = Math.Min(fraction, 3);
        return true;
    }
}
