// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MiHomeHub.Controls;

/// <summary>
/// 首页趋势图：极简线图（平滑曲线 + 渐隐面积 + 末端光点）或柱状图（用电逐小时）。
///
/// 只负责画，不管数据从哪来：<see cref="Values"/> 传进来一串定长序列，NaN 表示这一格没有采样，
/// 曲线会把前后两个有值的点照样用曲线连过去（断档不收笔），柱子则跳过空格。
/// 纵轴按传入的有效值自动取值域（留 12% 余量），这张卡只比较自己 24 小时内的形状，不需要跨卡同轴。
/// 鼠标悬停时画出竖向指示线与读数气泡，标出该位置的时间和数值。
///
/// 数据换新（新采样、换范围、换设备）时不直接跳过去：把旧的一串按新长度重采样成起点，
/// 用短促的缓动逐帧插值到新值，曲线和柱子会从原来的形状柔和地长成新形状。
/// </summary>
public class TrendChart : Control
{
    private const double PadL = 2, PadR = 7, PadT = 3, PadB = 1;

    /// <summary>横向辅助线的透明度：只在图上留个位置感，不跟曲线抢眼。</summary>
    private const double GridOpacity = 0.22;

    /// <summary>新旧序列之间的过渡时长：足够看清形状在变，又不会让读数看起来过时。</summary>
    private const double TransitionMs = 460;

    private int _hoverIndex = -1;

    /// <summary>当前真正画出来的一串（过渡中是插值结果）；null 时退回 <see cref="Values"/>。</summary>
    private IReadOnlyList<double>? _renderValues;

    private double[]? _fromValues;
    private double[]? _toValues;
    private double[]? _liveValues;
    private DispatcherTimer? _transitionTimer;
    private Stopwatch? _transitionWatch;

    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<TrendChart, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>线/柱的主色。</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<TrendChart, IBrush?>(nameof(Stroke));

    /// <summary>曲线下方的渐隐填充；为空则只画线。</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<TrendChart, IBrush?>(nameof(Fill));

    /// <summary>水平参考线颜色；为空不画网格。</summary>
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<TrendChart, IBrush?>(nameof(GridBrush));

    /// <summary>true 画柱状（用电），false 画曲线。</summary>
    public static readonly StyledProperty<bool> IsBarsProperty =
        AvaloniaProperty.Register<TrendChart, bool>(nameof(IsBars));

    /// <summary>柱状模式下每根柱子是一天（横轴标日期），而不是一个小时（标 HH:00）。</summary>
    public static readonly StyledProperty<bool> IsDailyBarsProperty =
        AvaloniaProperty.Register<TrendChart, bool>(nameof(IsDailyBars));

    /// <summary>
    /// 点阵模式：每次测量画一个点、不连线。称重这类离散记录用它——
    /// 两天之间没有称重，用曲线连过去等于编了一段没发生过的变化。
    /// </summary>
    public static readonly StyledProperty<bool> IsDotsProperty =
        AvaloniaProperty.Register<TrendChart, bool>(nameof(IsDots));

    /// <summary>点阵背后的区间带：取这一串有效值的最低到最高，用主色淡淡铺一层。</summary>
    public static readonly StyledProperty<bool> ShowBandProperty =
        AvaloniaProperty.Register<TrendChart, bool>(nameof(ShowBand));

    /// <summary>均值虚线：给点阵补一条「这段时间平均」的横线。</summary>
    public static readonly StyledProperty<bool> ShowMeanLineProperty =
        AvaloniaProperty.Register<TrendChart, bool>(nameof(ShowMeanLine));

    /// <summary>
    /// 外部给的参照基准（例如「上月同期日均」），画成一条横虚线；NaN 表示不画。
    /// 柱状模式下按 0 基线换算，曲线模式按当前值域换算。
    /// </summary>
    public static readonly StyledProperty<double> BaselineProperty =
        AvaloniaProperty.Register<TrendChart, double>(nameof(Baseline), double.NaN);

    /// <summary>柱状模式下高亮的那一格（当前小时）；-1 表示不高亮。</summary>
    public static readonly StyledProperty<int> HighlightIndexProperty =
        AvaloniaProperty.Register<TrendChart, int>(nameof(HighlightIndex), -1);

    /// <summary>线末端是否画当前读数光点。</summary>
    public static readonly StyledProperty<bool> ShowEndDotProperty =
        AvaloniaProperty.Register<TrendChart, bool>(nameof(ShowEndDot), true);

    /// <summary>线宽（DIP）。</summary>
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<TrendChart, double>(nameof(Thickness), 1.8);

    /// <summary>悬停气泡里的数值单位（℃ / % / W）。</summary>
    public static readonly StyledProperty<string> UnitTextProperty =
        AvaloniaProperty.Register<TrendChart, string>(nameof(UnitText), "");

    /// <summary>曲线横轴每格多少分钟，用来把悬停位置换算成时间；柱状模式按整点算，忽略它。</summary>
    public static readonly StyledProperty<double> TimeStepMinutesProperty =
        AvaloniaProperty.Register<TrendChart, double>(nameof(TimeStepMinutes), 5);

    /// <summary>
    /// 直接给每个点配一段横轴文字（体重按真实称重日期），给值时优先于按格倒推的时间。
    /// 只影响悬停气泡，不画横轴刻度。
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> XLabelsProperty =
        AvaloniaProperty.Register<TrendChart, IReadOnlyList<string>?>(nameof(XLabels));

    /// <summary>悬停气泡的底色 / 字色，由主题传入。</summary>
    public static readonly StyledProperty<IBrush?> TipBackgroundProperty =
        AvaloniaProperty.Register<TrendChart, IBrush?>(nameof(TipBackground));

    public static readonly StyledProperty<IBrush?> TipForegroundProperty =
        AvaloniaProperty.Register<TrendChart, IBrush?>(nameof(TipForeground));

    static TrendChart()
    {
        AffectsRender<TrendChart>(ValuesProperty, StrokeProperty, FillProperty, GridBrushProperty,
            IsBarsProperty, HighlightIndexProperty, ShowEndDotProperty, ThicknessProperty,
            UnitTextProperty, TimeStepMinutesProperty, XLabelsProperty, TipBackgroundProperty, TipForegroundProperty,
            IsDailyBarsProperty, IsDotsProperty, ShowBandProperty, ShowMeanLineProperty, BaselineProperty);
    }

    /// <summary>序列换新时开一段过渡；卡片里后长出来的图也淡入，不整块突然亮起。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValuesProperty)
        {
            BeginTransition(change.GetOldValue<IReadOnlyList<double>?>(), change.GetNewValue<IReadOnlyList<double>?>());
        }
        else if (change.Property == Visual.IsVisibleProperty && change.GetNewValue<bool>())
        {
            BeginFadeIn();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // 离开页面就把没跑完的过渡收口到终值，回来时看到的是最新数据而不是半截
        FinishTransition();
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public bool IsBars
    {
        get => GetValue(IsBarsProperty);
        set => SetValue(IsBarsProperty, value);
    }

    public bool IsDailyBars
    {
        get => GetValue(IsDailyBarsProperty);
        set => SetValue(IsDailyBarsProperty, value);
    }

    public bool IsDots
    {
        get => GetValue(IsDotsProperty);
        set => SetValue(IsDotsProperty, value);
    }

    public bool ShowBand
    {
        get => GetValue(ShowBandProperty);
        set => SetValue(ShowBandProperty, value);
    }

    public bool ShowMeanLine
    {
        get => GetValue(ShowMeanLineProperty);
        set => SetValue(ShowMeanLineProperty, value);
    }

    public double Baseline
    {
        get => GetValue(BaselineProperty);
        set => SetValue(BaselineProperty, value);
    }

    public int HighlightIndex
    {
        get => GetValue(HighlightIndexProperty);
        set => SetValue(HighlightIndexProperty, value);
    }

    public bool ShowEndDot
    {
        get => GetValue(ShowEndDotProperty);
        set => SetValue(ShowEndDotProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public string UnitText
    {
        get => GetValue(UnitTextProperty);
        set => SetValue(UnitTextProperty, value);
    }

    public double TimeStepMinutes
    {
        get => GetValue(TimeStepMinutesProperty);
        set => SetValue(TimeStepMinutesProperty, value);
    }

    public IReadOnlyList<string>? XLabels
    {
        get => GetValue(XLabelsProperty);
        set => SetValue(XLabelsProperty, value);
    }

    public IBrush? TipBackground
    {
        get => GetValue(TipBackgroundProperty);
        set => SetValue(TipBackgroundProperty, value);
    }

    public IBrush? TipForeground
    {
        get => GetValue(TipForegroundProperty);
        set => SetValue(TipForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var values = _renderValues ?? Values;
        if (values is null || values.Count < 2 || width < 8 || height < 8) return;
        if (Stroke is not { } stroke) return;

        DrawGrid(context, width, height, IsBars ? 1 : 2);
        DrawBaseline(context, values, stroke, width, height);

        if (IsDots) DrawDots(context, values, stroke, width, height);
        else if (IsBars) DrawBars(context, values, stroke, width, height);
        else DrawCurve(context, values, stroke, width, height);

        if (_hoverIndex >= 0) DrawHover(context, values, stroke, width, height);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var values = Values;
        if (values is null || values.Count < 2) return;
        var plotW = Bounds.Width - PadL - PadR;
        if (plotW <= 0) return;

        var p = e.GetPosition(this);
        var idx = (int)Math.Round((p.X - PadL) / plotW * (values.Count - 1));
        idx = Math.Clamp(idx, 0, values.Count - 1);
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            InvalidateVisual();
        }
    }

    /// <summary>卡片里后长出来的曲线淡入：不透明度 0→1，不整块突然亮起。</summary>
    private void BeginFadeIn()
    {
        Opacity = 0;
        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
        };
        fade.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Setter(Visual.OpacityProperty, 0d) }
        });
        fade.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Setter(Visual.OpacityProperty, 1d) }
        });
        _ = fade.RunAsync(this);
    }

    /// <summary>
    /// 新序列到了：以「当前画面上那一串」为起点，重采样到新长度后逐帧插值过去。
    /// 起点用重采样而不是要求等长——换范围、换设备时点数会差很多，这样也能顺着过渡。
    /// </summary>
    private void BeginTransition(IReadOnlyList<double>? oldValues, IReadOnlyList<double>? newValues)
    {
        var basis = _renderValues ?? oldValues;

        // 新数据为空 / 点太少：没东西可画，直接换掉
        if (newValues is null || newValues.Count < 2)
        {
            ResetTransition();
            _renderValues = newValues;
            return;
        }

        // 第一次上数据没有「从哪来」：直接落下，卡片的入场动画会盖住这一下
        if (basis is null || basis.Count < 2 || !HasAnyValid(basis))
        {
            ResetTransition();
            _renderValues = newValues;
            return;
        }

        var target = Snapshot(newValues);
        var from = ResampleTo(basis, target.Length);

        // 数据没变（定时刷新的常态）：不播动画，免得每隔几秒轻轻抖一下
        if (ValuesEqual(from, target))
        {
            ResetTransition();
            _renderValues = newValues;
            return;
        }

        _fromValues = from;
        _toValues = target;
        _liveValues = new double[target.Length];
        Array.Copy(from, _liveValues, from.Length);
        _renderValues = _liveValues;
        _transitionWatch = Stopwatch.StartNew();

        if (_transitionTimer is null)
        {
            _transitionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Render, (_, _) => TickTransition());
        }
        _transitionTimer.Start();
        InvalidateVisual();
    }

    private void TickTransition()
    {
        if (_transitionWatch is null || _fromValues is null || _toValues is null || _liveValues is null)
        {
            StopTimer();
            return;
        }

        var raw = Math.Min(1.0, _transitionWatch.Elapsed.TotalMilliseconds / TransitionMs);
        // 前段快、后段绵长：先让形状从旧态快速脱离，再慢慢落到新值，收尾不突然停住
        var eased = 1 - Math.Pow(1 - raw, 4);

        var count = _liveValues.Length;
        for (var i = 0; i < count; i++)
        {
            var a = _fromValues[i];
            var b = _toValues[i];
            if (double.IsNaN(a) || double.IsNaN(b))
            {
                _liveValues[i] = b;
                continue;
            }
            _liveValues[i] = a + (b - a) * eased;
        }

        InvalidateVisual();
        if (raw >= 1) FinishTransition();
    }

    /// <summary>把过渡收口到终值：不留下半截曲线，也不会让旧序列一直压在画面上。</summary>
    private void FinishTransition()
    {
        if (_toValues is not null)
        {
            _liveValues = null;
            _renderValues = _toValues;
        }
        ResetTransition();
    }

    /// <summary>丢掉过渡状态但不改当前画面：数据直接换掉、或没有过渡可跑时用。</summary>
    private void ResetTransition()
    {
        _fromValues = null;
        _toValues = null;
        _liveValues = null;
        _transitionWatch = null;
        StopTimer();
    }

    private void StopTimer() => _transitionTimer?.Stop();

    private static double[] Snapshot(IReadOnlyList<double> values)
    {
        var result = new double[values.Count];
        for (var i = 0; i < values.Count; i++) result[i] = values[i];
        return result;
    }

    /// <summary>把旧序列按相对位置线性重采样到新长度；断档（NaN）尽量用相邻的有效值接上。</summary>
    private static double[] ResampleTo(IReadOnlyList<double> source, int count)
    {
        var result = new double[count];
        if (count == 0) return result;
        if (source.Count == 0)
        {
            Array.Fill(result, double.NaN);
            return result;
        }
        if (source.Count == 1)
        {
            Array.Fill(result, source[0]);
            return result;
        }

        for (var i = 0; i < count; i++)
        {
            var pos = (double)i * (source.Count - 1) / (count - 1);
            var low = (int)Math.Floor(pos);
            var high = Math.Min(source.Count - 1, low + 1);
            var fraction = pos - low;

            var a = source[low];
            var b = source[high];
            if (double.IsNaN(a)) result[i] = b;
            else if (double.IsNaN(b)) result[i] = a;
            else result[i] = a + (b - a) * fraction;
        }
        return result;
    }

    private static bool ValuesEqual(double[] a, double[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (double.IsNaN(a[i]) && double.IsNaN(b[i])) continue;
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static bool HasAnyValid(IReadOnlyList<double> values)
    {
        foreach (var v in values)
            if (!double.IsNaN(v)) return true;
        return false;
    }

    private void DrawGrid(DrawingContext context, double width, double height, int lines)
    {
        if (GridBrush is not { } grid) return;
        var pen = new Pen(grid, 1);
        // 辅助线只留个位置感：压到两成出头，不让它跟曲线和柱子抢眼
        using var _ = context.PushOpacity(GridOpacity);
        for (var i = 1; i <= lines; i++)
        {
            var y = Math.Round(height * i / (lines + 1)) + 0.5;
            context.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
    }

    private void DrawCurve(DrawingContext context, IReadOnlyList<double> values, IBrush stroke,
        double width, double height)
    {
        var (min, max) = Range(values);
        if (double.IsNaN(min)) return;
        Pad(ref min, ref max);

        const double padL = PadL, padR = PadR, padT = PadT, padB = PadB;
        var plotW = width - padL - padR;
        var plotH = height - padT - padB;

        double X(int i) => padL + plotW * i / (values.Count - 1);
        double Y(double v) => padT + plotH * (1 - (v - min) / (max - min));

        var pen = new Pen(stroke, Thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var bottom = padT + plotH;

        // 有值的点按顺序串成一条：中间断档既不收笔、也不硬拉折线，照样按曲线连过去
        var runs = new List<List<Point>>();
        var run = new List<Point>();
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (double.IsNaN(v))
            {
                if (run.Count > 0)
                {
                    runs.Add(run);
                    run = new List<Point>();
                }
                continue;
            }
            run.Add(new Point(X(i), Y(v)));
        }
        if (run.Count > 0) runs.Add(run);
        if (runs.Count == 0) return;

        // 平滑只在段内做（末端不动），再把各段接成一条，跨断档的那一段也交给贝塞尔去画
        var shape = new List<Point>(values.Count);
        foreach (var r in runs) shape.AddRange(Soften(r));

        if (Fill is { } fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(shape[0], true);
                AppendSmooth(ctx, shape);
                ctx.LineTo(new Point(shape[^1].X, bottom));
                ctx.LineTo(new Point(shape[0].X, bottom));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(fill, null, area);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(shape[0], false);
            AppendSmooth(ctx, shape);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, line);

        if (!ShowEndDot) return;
        // 末端当前读数：外圈淡光 + 实心圆点，落屏后是这条线的「现在」
        var end = shape[^1];
        if (TryColor(stroke) is { } color)
        {
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(36, color.R, color.G, color.B)),
                null, end, 6.5, 6.5);
        }
        context.DrawEllipse(stroke, null, end, 3, 3);
    }

    private void DrawBars(DrawingContext context, IReadOnlyList<double> values, IBrush stroke,
        double width, double height)
    {
        var (_, max) = Range(values);
        if (double.IsNaN(max) || max <= 0) return;

        var gap = 3.0;
        var barW = (width - gap * (values.Count - 1)) / values.Count;
        if (barW <= 0) return;

        var baseBrush = TryColor(stroke) is { } c
            ? new SolidColorBrush(Color.FromArgb(82, c.R, c.G, c.B))
            : stroke;
        var focus = HighlightIndex;
        var plotH = height - 1;

        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (double.IsNaN(v) || v <= 0) continue;
            var barH = Math.Max(2, plotH * v / max);
            var x = i * (barW + gap);
            var top = height - barH;
            var r = Math.Min(3.0, Math.Min(barW / 2, barH / 2));

            // 柱顶两角做成小圆角，底边保持平齐贴着基线
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(x, height), true);
                ctx.LineTo(new Point(x, top + r));
                ctx.QuadraticBezierTo(new Point(x, top), new Point(x + r, top));
                ctx.LineTo(new Point(x + barW - r, top));
                ctx.QuadraticBezierTo(new Point(x + barW, top), new Point(x + barW, top + r));
                ctx.LineTo(new Point(x + barW, height));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(i == focus ? stroke : baseBrush, null, geo);
        }
    }

    /// <summary>
    /// 参照基准线（例如「上月同期日均」）：一条横虚线，回答「比它高还是低」。
    /// 柱子从 0 起算，所以柱状模式按 0 到最大值换算；曲线按当前值域换算。
    /// </summary>
    private void DrawBaseline(DrawingContext context, IReadOnlyList<double> values, IBrush stroke,
        double width, double height)
    {
        var value = Baseline;
        if (double.IsNaN(value)) return;

        double y;
        if (IsBars)
        {
            var (_, max) = Range(values);
            if (double.IsNaN(max) || max <= 0 || value <= 0) return;
            y = height - Math.Min(1, value / max) * (height - 1);
        }
        else
        {
            var (min, max) = Range(values);
            if (double.IsNaN(min)) return;
            Pad(ref min, ref max);
            var plotH = height - PadT - PadB;
            y = PadT + plotH * (1 - (value - min) / (max - min));
        }

        y = Math.Round(y) + 0.5;
        var brush = TryColor(stroke) is { } c
            ? new SolidColorBrush(Color.FromArgb(96, c.R, c.G, c.B))
            : GridBrush ?? stroke;
        var pen = new Pen(brush, 1, dashStyle: new DashStyle(new double[] { 4, 3 }, 0));
        context.DrawLine(pen, new Point(PadL, y), new Point(width - PadR, y));
    }

    /// <summary>
    /// 点阵：每次测量画一个点。背后可以铺一条「最低到最高」的区间带、画一条均值虚线，
    /// 但绝不把点连成线——没称重的日子没有数据，连起来就是一段编出来的变化。
    /// </summary>
    private void DrawDots(DrawingContext context, IReadOnlyList<double> values, IBrush stroke,
        double width, double height)
    {
        var (rawMin, rawMax) = Range(values);
        if (double.IsNaN(rawMin)) return;

        var min = rawMin;
        var max = rawMax;
        Pad(ref min, ref max);

        var plotW = width - PadL - PadR;
        var plotH = height - PadT - PadB;
        if (plotW <= 0 || plotH <= 0) return;

        double X(int i) => PadL + plotW * i / (values.Count - 1);
        double Y(double v) => PadT + plotH * (1 - (v - min) / (max - min));

        var color = TryColor(stroke);

        // 区间带：这一串读数落在的范围，只铺一层底色，不跟点抢眼
        if (ShowBand && color is { } bandColor && rawMax - rawMin > 1e-9)
        {
            var top = Y(rawMax);
            var bottom = Y(rawMin);
            var bandH = Math.Max(1, bottom - top);
            var rect = new Rect(PadL - 1, top, plotW + 2, bandH);
            FillRoundRect(context,
                new SolidColorBrush(Color.FromArgb(30, bandColor.R, bandColor.G, bandColor.B)), rect,
                Math.Min(5.0, bandH / 2));
        }

        // 均值虚线：这段时间的平均水平，压得比点更淡
        if (ShowMeanLine && color is { } meanColor)
        {
            var mean = Mean(values);
            if (!double.IsNaN(mean))
            {
                var y = Math.Round(Y(mean)) + 0.5;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(72, meanColor.R, meanColor.G, meanColor.B)),
                    1, dashStyle: new DashStyle(new double[] { 4, 3 }, 0));
                context.DrawLine(pen, new Point(PadL, y), new Point(width - PadR, y));
            }
        }

        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (double.IsNaN(v)) continue;
            var p = new Point(X(i), Y(v));
            if (color is { } glow)
                context.DrawEllipse(new SolidColorBrush(Color.FromArgb(30, glow.R, glow.G, glow.B)),
                    null, p, 6, 6);
            context.DrawEllipse(stroke, null, p, 3, 3);
        }
    }

    /// <summary>1-2-1 权重的轻平滑：只让线条更圆润，起点终点不动，也不改原始数据。</summary>
    private static List<Point> Soften(IReadOnlyList<Point> pts)
    {
        if (pts.Count < 3) return new List<Point>(pts);
        var res = new List<Point>(pts.Count) { pts[0] };
        for (var i = 1; i < pts.Count - 1; i++)
        {
            res.Add(new Point(
                (pts[i - 1].X + 2 * pts[i].X + pts[i + 1].X) / 4,
                (pts[i - 1].Y + 2 * pts[i].Y + pts[i + 1].Y) / 4));
        }
        res.Add(pts[^1]);
        return res;
    }

    /// <summary>
    /// Catmull-Rom 转三次贝塞尔：视觉上是顺滑的曲线，又不会像样条那样在极值处过冲。
    /// 断档会让相邻两点横向拉得很开，手柄长度因此限在一段的三分之一出头，
    /// 免得跨空白时鼓出一个明显的大包——正常密集采样时这个上限不会生效。
    /// </summary>
    private static void AppendSmooth(StreamGeometryContext ctx, IReadOnlyList<Point> pts)
    {
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var p0 = i > 0 ? pts[i - 1] : pts[i];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = i + 2 < pts.Count ? pts[i + 2] : p2;

            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);

            var limit = Distance(p1, p2) * 0.45;
            c1 = ClampHandle(p1, c1, limit);
            c2 = ClampHandle(p2, c2, limit);

            ctx.CubicBezierTo(c1, c2, p2);
        }
    }

    /// <summary>把贝塞尔手柄缩短到 <paramref name="limit"/> 以内，方向不变。</summary>
    private static Point ClampHandle(Point origin, Point handle, double limit)
    {
        var dx = handle.X - origin.X;
        var dy = handle.Y - origin.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= limit || len < 1e-6) return handle;

        var k = limit / len;
        return new Point(origin.X + dx * k, origin.Y + dy * k);
    }

    private static double Distance(Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>悬停读数：竖向虚线 + 落点光点 + 一枚跟着位置走的小气泡（时间 + 数值）。</summary>
    private void DrawHover(DrawingContext context, IReadOnlyList<double> values, IBrush stroke,
        double width, double height)
    {
        var idx = _hoverIndex;
        if (idx < 0 || idx >= values.Count) return;
        var plotW = width - PadL - PadR;
        var plotH = height - PadT - PadB;
        if (plotW <= 0 || plotH <= 0) return;

        var x = PadL + plotW * idx / (values.Count - 1);
        var v = values[idx];
        var y = double.NaN;

        if (IsBars)
        {
            var (_, max) = Range(values);
            if (!double.IsNaN(max) && max > 0 && !double.IsNaN(v) && v > 0)
                y = height - Math.Max(2, (height - 1) * v / max);
        }
        else
        {
            var (min, max) = Range(values);
            if (!double.IsNaN(min))
            {
                Pad(ref min, ref max);
                if (!double.IsNaN(v)) y = PadT + plotH * (1 - (v - min) / (max - min));
            }
        }

        var guide = GridBrush ?? stroke;
        var pen = new Pen(guide, 1, dashStyle: new DashStyle(new double[] { 3, 3 }, 0));
        context.DrawLine(pen, new Point(x, PadT), new Point(x, PadT + plotH));

        if (!double.IsNaN(y))
        {
            if (TryColor(stroke) is { } c)
                context.DrawEllipse(new SolidColorBrush(Color.FromArgb(45, c.R, c.G, c.B)),
                    null, new Point(x, y), 5.5, 5.5);
            context.DrawEllipse(stroke, null, new Point(x, y), 2.6, 2.6);
        }

        var text = double.IsNaN(v)
            ? $"{TimeLabel(idx, values.Count)}  无采样"
            : $"{TimeLabel(idx, values.Count)}  {ValueLabel(v)}";
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 10.5, TipForeground ?? Brushes.White);

        var tw = ft.Width + 14;
        var th = ft.Height + 8;
        var left = Math.Clamp(x - tw / 2, 0, Math.Max(0, width - tw));
        var top = double.IsNaN(y) ? PadT : y - th - 8;
        if (top < 0) top = (double.IsNaN(y) ? PadT : y + 8);
        top = Math.Clamp(top, 0, Math.Max(0, height - th));

        var rect = new Rect(left, top, tw, th);
        FillRoundRect(context, TipBackground ?? DefaultTipBg, rect, 6);
        context.DrawText(ft, new Point(left + 7, top + 4));
    }

    /// <summary>横轴标签：给了每个点的文字就直接用（体重按真实称重日期），否则按天/整点/最后一格倒推。</summary>
    private string TimeLabel(int idx, int count)
    {
        if (XLabels is { Count: > 0 } labels && idx >= 0 && idx < labels.Count
            && !string.IsNullOrEmpty(labels[idx]))
            return labels[idx];
        // 按天的历史：最后一格是今天，往前一天一格
        if (IsDailyBars)
            return $"{DateTime.Today.AddDays(idx - (count - 1)):M/d}";
        if (IsBars) return $"{idx:00}:00";
        var minutes = Math.Max(0, TimeStepMinutes) * (count - 1 - idx);
        var t = DateTime.Now.AddMinutes(-minutes);
        return $"{t:HH:mm}";
    }

    private string ValueLabel(double v)
    {
        var unit = UnitText ?? "";
        if (unit == "W")
            return v >= 1000 ? $"{v / 1000:0.##} kW" : $"{v:0} W";
        if (unit == "%") return $"{v:0}{unit}";
        return $"{v:0.#}{unit}";
    }

    private static void FillRoundRect(DrawingContext ctx, IBrush brush, Rect rect, double r)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(rect.X + r, rect.Y), true);
            g.LineTo(new Point(rect.Right - r, rect.Y));
            g.QuadraticBezierTo(new Point(rect.Right, rect.Y), new Point(rect.Right, rect.Y + r));
            g.LineTo(new Point(rect.Right, rect.Bottom - r));
            g.QuadraticBezierTo(new Point(rect.Right, rect.Bottom), new Point(rect.Right - r, rect.Bottom));
            g.LineTo(new Point(rect.X + r, rect.Bottom));
            g.QuadraticBezierTo(new Point(rect.X, rect.Bottom), new Point(rect.X, rect.Bottom - r));
            g.LineTo(new Point(rect.X, rect.Y + r));
            g.QuadraticBezierTo(new Point(rect.X, rect.Y), new Point(rect.X + r, rect.Y));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, geo);
    }

    private static readonly IBrush DefaultTipBg = new SolidColorBrush(Color.FromArgb(235, 22, 26, 36));

    private static (double Min, double Max) Range(IReadOnlyList<double> values)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var v in values)
        {
            if (double.IsNaN(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return min == double.MaxValue ? (double.NaN, double.NaN) : (min, max);
    }

    /// <summary>有效值的平均；一个有效值都没有时返回 NaN。</summary>
    private static double Mean(IReadOnlyList<double> values)
    {
        var sum = 0.0;
        var count = 0;
        foreach (var v in values)
        {
            if (double.IsNaN(v)) continue;
            sum += v;
            count++;
        }
        return count == 0 ? double.NaN : sum / count;
    }

    private static void Pad(ref double min, ref double max)
    {
        var range = max - min;
        if (range < 1e-6)
        {
            // 一整天读数不动（比如恒定 24℃）：给一条平线留出上下空间，别贴边
            var mid = (max + min) / 2;
            min = mid - (Math.Abs(mid) > 0 ? Math.Abs(mid) * 0.08 : 1);
            max = mid + (Math.Abs(mid) > 0 ? Math.Abs(mid) * 0.08 : 1);
            range = max - min;
        }
        var pad = range * 0.12;
        min -= pad;
        max += pad;
    }

    private static Color? TryColor(IBrush? brush) => brush switch
    {
        ISolidColorBrush solid => solid.Color,
        IGradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops[0].Color,
        _ => null
    };
}
