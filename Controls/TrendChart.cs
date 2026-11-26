// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MiHomeHub.Controls;

/// <summary>
/// 首页趋势图：极简线图（平滑曲线 + 渐隐面积 + 末端光点）或柱状图（用电逐小时）。
///
/// 只负责画，不管数据从哪来：<see cref="Values"/> 传进来一串定长序列，NaN 表示这一格没有采样，
/// 曲线会把前后两个有值的点照样用曲线连过去（断档不收笔），柱子则跳过空格。
/// 纵轴按传入的有效值自动取值域（留 12% 余量），这张卡只比较自己 24 小时内的形状，不需要跨卡同轴。
/// 鼠标悬停时画出竖向指示线与读数气泡，标出该位置的时间和数值。
/// </summary>
public class TrendChart : Control
{
    private const double PadL = 2, PadR = 7, PadT = 3, PadB = 1;

    /// <summary>横向辅助线的透明度：只在图上留个位置感，不跟曲线抢眼。</summary>
    private const double GridOpacity = 0.22;

    private int _hoverIndex = -1;

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

    /// <summary>悬停气泡的底色 / 字色，由主题传入。</summary>
    public static readonly StyledProperty<IBrush?> TipBackgroundProperty =
        AvaloniaProperty.Register<TrendChart, IBrush?>(nameof(TipBackground));

    public static readonly StyledProperty<IBrush?> TipForegroundProperty =
        AvaloniaProperty.Register<TrendChart, IBrush?>(nameof(TipForeground));

    static TrendChart()
    {
        AffectsRender<TrendChart>(ValuesProperty, StrokeProperty, FillProperty, GridBrushProperty,
            IsBarsProperty, HighlightIndexProperty, ShowEndDotProperty, ThicknessProperty,
            UnitTextProperty, TimeStepMinutesProperty, TipBackgroundProperty, TipForegroundProperty,
            IsDailyBarsProperty);
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
        var values = Values;
        if (values is null || values.Count < 2 || width < 8 || height < 8) return;
        if (Stroke is not { } stroke) return;

        DrawGrid(context, width, height, IsBars ? 1 : 2);

        if (IsBars) DrawBars(context, values, stroke, width, height);
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

    /// <summary>横轴标签：曲线按「最后一格 = 现在」倒推，柱子就是整点。</summary>
    private string TimeLabel(int idx, int count)
    {
        // 柱子按天的历史模式：最后一格是今天，往前一天一格
        if (IsBars && IsDailyBars)
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
