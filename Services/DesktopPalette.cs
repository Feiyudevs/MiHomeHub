// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>
/// 桌面分区配色：底色来自「自定义色」或深色主题卡片色，Alpha 由背景透明度缩放。
/// 毛玻璃开时底色只留一层薄纱（模糊由窗口级亚克力承担），关时由底色本身撑观感。
/// <para>
/// 分区窗固定走深色（<c>DesktopGroupWindow.RequestedThemeVariant="Dark"</c>），不跟随主程序主题，
/// 所以这里取资源时一律按 <see cref="ThemeVariant.Dark"/>，不再读 ActualThemeVariant。
/// </para>
/// </summary>
internal static class DesktopPalette
{
    /// <summary>默认底色（主题资源取不到时的兜底，与深色 CardBrush 一致）。</summary>
    private static readonly Color Fallback = Color.Parse("#1A1F2C");

    public static Color BaseColor(DesktopAppearance look)
    {
        if (!string.IsNullOrWhiteSpace(look.BgColor) && TryParse(look.BgColor, out var custom))
            return custom;

        var app = Application.Current;
        if (app != null && app.TryGetResource("CardBrush", ThemeVariant.Dark, out var res) && res is ISolidColorBrush b)
            return b.Color;

        return Fallback;
    }

    /// <summary>
    /// 玻璃态浓度（= 直接当 alpha 用）的下限与上限。
    /// <para>
    /// 屏幕上的最终浓度 = 底色 alpha 再叠一层 DWM tint（<see cref="TintOpacity"/>，两者近似相乘）。
    /// 下限 0：文字可读性已由灰度抗锯齿（RenderOptions.TextRenderingMode="Antialias"）兜底，
    /// 允许完全无底色、只留磨砂模糊，壁纸最透；上限收在 0.72，再高就是一块实心板。
    /// </para>
    /// <para>
    /// 这里是**唯一**的对比度来源：文字上不叠任何阴影。曾经按 DeskBox 的思路给 TextBlock
    /// 挂过 DropShadowEffect，Avalonia 在 12.5px 小字上会糊成一圈灰雾，中等亮度底上整片发脏；
    /// 而 DeskBox 之所以「看不出阴影」，是因为它的背景是 60~145 的暗底，黑影落在上面本就不可见。
    /// </para>
    /// </summary>
    public const double MinDensity = 0.0;

    /// <summary>满浓度上限：再往上就是一块实心板，通透感彻底消失。</summary>
    private const double MaxDensity = 0.72;

    /// <summary>默认浓度：DeskBox 那一档的等效观感（配合 tint 后约 0.4）。</summary>
    public const double DefaultDensity = 0.34;

    /// <summary>关掉毛玻璃时的实心底（没有模糊兜底，得靠浓度撑）。</summary>
    private const double OpaqueDensity = 0.92;

    /// <summary>
    /// 分区卡片底色：浓度直接就是 alpha，用户滑杆在 [MinDensity, MaxDensity] 之间调。
    /// 底色只压暗不加亮，透出来的是壁纸的明暗与模糊，这是「毛玻璃」能成立的前提。
    /// </summary>
    public static IBrush CardBrush(DesktopAppearance look)
    {
        var baseColor = TintColor(look);
        var density = look.Glass
            ? Math.Clamp(look.ClampedOpacity, MinDensity, MaxDensity)
            : OpaqueDensity;
        var a = (byte)Math.Clamp(Math.Round(255 * density), 0, 255);
        return new SolidColorBrush(Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));
    }

    /// <summary>悬停抬亮一档的底色（图标格、标题栏按钮用）；低浓度下也要看得见。</summary>
    public static IBrush HoverBrush(DesktopAppearance look)
    {
        var c = BaseColor(look);
        var a = (byte)Math.Clamp(Math.Round(255 * (0.07 + 0.07 * look.ClampedOpacity)), 0, 255);
        return new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
    }

    /// <summary>
    /// 磨砂染色强度（DWM ACCENT 的 tint）：只补一点点，别把模糊层本身压黑 ——
    /// 通不通透全看这一层还剩多少亮度差。
    /// <para>
    /// 原来 0.14 + 0.12×浓度，滑杆 38% 时是 0.186；这一层和 <see cref="CardBrush"/> 的 alpha
    /// 是相乘叠加的，两层一起把壁纸压成一片平灰（实测卡内 93~112）。改成 0.10×浓度后，
    /// tint 跟着浓度走：0% 时完全无染色（纯磨砂），拖满时才明显。
    /// </para>
    /// </summary>
    public static double TintOpacity(DesktopAppearance look)
        => 0.10 * look.ClampedOpacity;

    /// <summary>
    /// 实际参与着色的底色：自定义色只贡献色相，亮度统一压到 <see cref="TargetLuma"/> 以下。
    /// 分区固定深色主题、文字是近白色 —— 浅底 + 白字等于没有文字
    /// （用户挑的紫色 #7043ED 亮度 0.35，直接铺上去白字只有 3.2:1，压暗后回到 4.7:1）。
    /// </summary>
    public static Color TintColor(DesktopAppearance look)
    {
        var c = BaseColor(look);
        var luma = Luma(c);
        if (luma <= TargetLuma) return c;

        var t = Math.Clamp((luma - TargetLuma) / Math.Max(0.01, luma - Luma(Dark)), 0, 1);
        return Blend(c, Dark, t);
    }

    /// <summary>自定义底色的亮度上限：与深色主题 CardBrush #1A1F2C（亮度 0.12）同档。</summary>
    private const double TargetLuma = 0.14;

    private static double Luma(Color c)
        => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>压暗目标色，与深色主题 CardBrush 同色相。</summary>
    private static readonly Color Dark = Color.Parse("#0C1018");

    private static Color Blend(Color from, Color to, double t)
    {
        byte Mix(byte a, byte b) => (byte)Math.Clamp(Math.Round(a + (b - a) * t), 0, 255);
        return Color.FromArgb(from.A, Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    /// <summary>描边颜色：分区固定深色底，所以恒用淡白描边。</summary>
    public static Color BorderColor() => Color.FromArgb(26, 255, 255, 255);

    private static bool TryParse(string text, out Color color)
    {
        color = default;
        var s = text.Trim();
        if (s.Length == 0) return false;
        if (!s.StartsWith('#')) s = "#" + s;
        try
        {
            if (s.Length == 7) s = "#FF" + s[1..];
            else if (s.Length == 4) s = $"#FF{s[1]}{s[1]}{s[2]}{s[2]}{s[3]}{s[3]}";
            if (s.Length != 9) return false;
            color = Color.Parse(s);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
