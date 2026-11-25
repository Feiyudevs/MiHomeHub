// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace MiHomeHub.Controls;

/// <summary>
/// 全局 ToolTip 形状策略：只有「单行」才收成胶囊（全圆角），一旦换行（长文本换行或内容自带换行）
/// 就退回小圆角矩形，避免多行气泡被 999 圆角削成两头半圆的大胶囊。
///
/// 判定不看高度（字号 / 行高会变），而是用 <see cref="TextLayout"/> 按当前字体实测文本宽度：
/// 未超过内容可用宽度且不含换行符 → 单行。复杂内容（非字符串）一律按多行处理。
///
/// 用法：App.OnFrameworkInitializationCompleted 里调用一次 <see cref="Install"/>。
/// 全局 ToolTip 的默认圆角仍由 Themes/ToolTipStyles.axaml 给（单行胶囊值），这里只在运行期覆盖。
/// </summary>
public static class ToolTipShape
{
    /// 单行：全圆，两头半圆。
    public const double PillRadius = 999;

    /// 多行：小圆角矩形。
    public const double RectRadius = 10;

    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        ToolTip.BoundsProperty.Changed.AddClassHandler<ToolTip>((tip, _) => Apply(tip));
        ToolTip.ContentProperty.Changed.AddClassHandler<ToolTip>((tip, _) => Apply(tip));
        ToolTip.IsOpenProperty.Changed.AddClassHandler<ToolTip>((tip, _) => Apply(tip));
        ToolTip.FontSizeProperty.Changed.AddClassHandler<ToolTip>((tip, _) => Apply(tip));
        ToolTip.MaxWidthProperty.Changed.AddClassHandler<ToolTip>((tip, _) => Apply(tip));
    }

    private static void Apply(ToolTip tip)
    {
        var radius = IsSingleLine(tip) ? PillRadius : RectRadius;
        if (Math.Abs(tip.CornerRadius.TopLeft - radius) < 0.01) return;
        tip.CornerRadius = new CornerRadius(radius);
    }

    private static bool IsSingleLine(ToolTip tip)
    {
        if (tip.Content is not string text || text.Length == 0) return false;
        if (text.Contains('\n') || text.Contains('\r')) return false;

        var available = MaxTextWidth(tip);
        if (available <= 0) return false;

        var typeface = new Typeface(tip.FontFamily, tip.FontStyle, tip.FontWeight);
        var layout = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, tip.FontSize, Brushes.Black);
        return layout.Width <= available + 0.5;
    }

    /// 气泡内文字真正能用的宽度 = MaxWidth - 左右内边距 - 左右边框。
    private static double MaxTextWidth(ToolTip tip)
    {
        var max = tip.MaxWidth;
        if (double.IsNaN(max) || double.IsInfinity(max) || max <= 0) max = 260;
        var padding = tip.Padding;
        var border = tip.BorderThickness;
        return max - padding.Left - padding.Right - border.Left - border.Right;
    }
}
