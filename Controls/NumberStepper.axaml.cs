// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MiHomeHub.Controls;

/// <summary>
/// 胶囊步进器：− 数值 单位 +。
/// 左右钮按 <see cref="Step"/> 一档加减并夹在 [Minimum, Maximum] 里；
/// 双击数值换成只吃数字的输入框（单位单独标在框外），回车 / 点空白处 / 失焦都算提交，
/// 提交时统一校准（贴到步进档位、夹回量程），Esc 放弃本次输入。
/// 详情页的色温量程、场景编辑的动作数值与延时、添加动作弹窗共用这一个控件——
/// 外观和交互只此一份，各处不再各自抄一遍，也就不会再出现「这里能双击、那里不能」。
/// </summary>
public partial class NumberStepper : UserControl
{
    /// <summary>当前值（使用处一律写 Mode=TwoWay：加减与手输都要写回宿主）。</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<NumberStepper, double>(nameof(Value), 0d);

    /// <summary>下界（含）。</summary>
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<NumberStepper, double>(nameof(Minimum), 0d);

    /// <summary>上界（含）。</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<NumberStepper, double>(nameof(Maximum), 100d);

    /// <summary>一档的步长（同时用于手输校准：输入值贴到最近的整数档）。</summary>
    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<NumberStepper, double>(nameof(Step), 1d);

    /// <summary>单位（K / 秒 / % / ℃ …）。空串则不显示。</summary>
    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<NumberStepper, string>(nameof(Unit), "");

    /// <summary>数值区宽度，切到手输框时两者同宽，胶囊不跳。</summary>
    public static readonly StyledProperty<double> ValueWidthProperty =
        AvaloniaProperty.Register<NumberStepper, double>(nameof(ValueWidth), 52d);

    public static readonly StyledProperty<double> ValueFontSizeProperty =
        AvaloniaProperty.Register<NumberStepper, double>(nameof(ValueFontSize), 13d);

    public static readonly StyledProperty<FontWeight> ValueFontWeightProperty =
        AvaloniaProperty.Register<NumberStepper, FontWeight>(nameof(ValueFontWeight), FontWeight.SemiBold);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public double ValueWidth
    {
        get => GetValue(ValueWidthProperty);
        set => SetValue(ValueWidthProperty, value);
    }

    public double ValueFontSize
    {
        get => GetValue(ValueFontSizeProperty);
        set => SetValue(ValueFontSizeProperty, value);
    }

    public FontWeight ValueFontWeight
    {
        get => GetValue(ValueFontWeightProperty);
        set => SetValue(ValueFontWeightProperty, value);
    }

    /// <summary>手输状态：框已接管数值区，正在等提交。</summary>
    private bool _editing;

    /// <summary>手输期间挂在顶层上的「点空白即提交」监听，收尾时一定摘掉。</summary>
    private TopLevel? _top;
    private EventHandler<PointerPressedEventArgs>? _outsideHandler;

    public NumberStepper()
    {
        InitializeComponent();
        SyncVisuals();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty
            || change.Property == UnitProperty
            || change.Property == ValueWidthProperty
            || change.Property == ValueFontSizeProperty
            || change.Property == ValueFontWeightProperty)
        {
            SyncVisuals();
        }
    }

    /// <summary>把属性铺到文字上：值、单位、宽度与字号（输入框跟着用同一套，切换时不跳）。</summary>
    private void SyncVisuals()
    {
        if (ValText == null || ValBox == null || UnitText == null) return;

        ValText.Text = Format(Value);
        ValText.Width = ValueWidth;
        ValText.FontSize = ValueFontSize;
        ValText.FontWeight = ValueFontWeight;

        ValBox.Width = ValueWidth;
        ValBox.FontSize = ValueFontSize;
        ValBox.FontWeight = ValueFontWeight;

        var unit = Unit ?? "";
        UnitText.Text = unit;
        UnitText.IsVisible = unit.Length > 0;
    }

    private static string Format(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private void OnStepDown(object? sender, RoutedEventArgs e) => StepBy(-1);

    private void OnStepUp(object? sender, RoutedEventArgs e) => StepBy(1);

    /// <summary>按一档加减：先贴到步进档位再夹量程，和手输走同一套校准。</summary>
    private void StepBy(int dir)
    {
        var step = Step > 0 ? Step : 1;
        var next = Math.Round((Value + dir * step) / step) * step;
        Value = Calibrate(next);
    }

    /// <summary>贴到步进档位、夹进量程；步长 ≥ 1 时保持整数（26 不能变成 26.0 被设备拒收）。</summary>
    private double Calibrate(double raw)
    {
        var lo = Math.Min(Minimum, Maximum);
        var hi = Math.Max(Minimum, Maximum);
        var step = Step > 0 ? Step : 1;
        var snapped = Math.Round(raw / step) * step;
        snapped = Math.Clamp(snapped, lo, hi);
        return step >= 1 ? Math.Round(snapped) : Math.Round(snapped, 2);
    }

    // ===== 双击手输 =====

    private void OnValueDoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        BeginEdit();
    }

    private void BeginEdit()
    {
        if (_editing) return;
        _editing = true;

        ValBox.Text = Format(Value);
        ValText.IsVisible = false;
        ValBox.IsVisible = true;

        // 点框外的任何地方都算输入完成：空白区不可聚焦，光靠 LostFocus 等不到
        _top = TopLevel.GetTopLevel(this);
        if (_top != null)
        {
            _outsideHandler = OnTopPointerPressed;
            _top.AddHandler(PointerPressedEvent, _outsideHandler, RoutingStrategies.Tunnel);
        }

        // 框刚刚由隐藏转可见，立刻 Focus 会落在旧焦点上，推一帧再抢
        Dispatcher.UIThread.Post(() =>
        {
            if (!_editing) return;
            ValBox.Focus();
            ValBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void OnTopPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_editing) return;
        if (e.Source is Visual v && (ReferenceEquals(v, ValBox) || ValBox.IsVisualAncestorOf(v))) return;
        EndEdit(commit: true);
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                EndEdit(commit: true);
                e.Handled = true;
                break;
            case Key.Escape:
                EndEdit(commit: false);
                e.Handled = true;
                break;
        }
    }

    private void OnBoxLostFocus(object? sender, RoutedEventArgs e) => EndEdit(commit: true);

    /// <summary>
    /// 收尾：提交就把框里的数字校准后写回，取消则保持原值；然后恢复文字。
    /// 先落 _editing 再隐藏，避免隐藏引发的失焦又进来一次。
    /// </summary>
    private void EndEdit(bool commit)
    {
        if (!_editing) return;
        _editing = false;

        var typed = ParseNumber(ValBox.Text);
        if (commit && typed.HasValue) Value = Calibrate(typed.Value);

        DetachOutsideHandler();

        ValBox.IsVisible = false;
        ValText.IsVisible = true;
        SyncVisuals();
    }

    private void DetachOutsideHandler()
    {
        if (_top != null && _outsideHandler != null)
            _top.RemoveHandler(PointerPressedEvent, _outsideHandler);
        _top = null;
        _outsideHandler = null;
    }

    /// <summary>框里只认数字：字母、单位、空格一律剔掉（粘进来「2700K」也照样读成 2700）。</summary>
    private static double? ParseNumber(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsDigit(c) || c == '.') sb.Append(c);
        }

        var digits = sb.ToString();
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EndEdit(commit: true);
        base.OnDetachedFromVisualTree(e);
    }
}
