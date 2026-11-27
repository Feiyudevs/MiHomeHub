// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using MiHomeHub.Models;

namespace MiHomeHub.Services;

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
    /// 允许完全无底色、只留磨砂模糊，壁纸最透；上限放到 1.0，拉满就是一块实心底。
    /// </para>
    /// <para>
    /// 这里是**唯一**的对比度来源：文字上不叠任何阴影。曾经按 DeskBox 的思路给 TextBlock
    /// 挂过 DropShadowEffect，Avalonia 在 12.5px 小字上会糊成一圈灰雾，中等亮度底上整片发脏；
    /// 而 DeskBox 之所以「看不出阴影」，是因为它的背景是 60~145 的暗底，黑影落在上面本就不可见。
    /// </para>
    /// </summary>
    public const double MinDensity = 0.0;

    /// <summary>满浓度上限：拉到头就是实心底色，通透感完全让位给可读性。</summary>
    private const double MaxDensity = 1.0;

    /// <summary>默认浓度：DeskBox 那一档的等效观感（配合 tint 后约 0.4）。</summary>
    public const double DefaultDensity = 0.34;

    /// <summary>
    /// 关掉毛玻璃后的浓度：**不透明**，不是「近实心」。
    /// <para>
    /// 之前按滑杆映射到 0.80~1.0，浓度拉在七八十的人点「关毛玻璃」只从 0.81 变成 0.96，
    /// 深色壁纸底下几乎看不出差别，主观上就是「关了跟没关一样」。玻璃关掉本就该是
    /// 一张实心卡片：alpha 直接给满，墙纸一点不透，模糊也不再画 —— 开关一眼可见。
    /// </para>
    /// <para>
    /// 代价是浓度滑杆只在毛玻璃开启时生效；界面上同步把滑杆置灰并写明，不做「拖了没反应」的假控件。
    /// </para>
    /// </summary>
    private const double OpaqueDensity = 1.0;

    /// <summary>
    /// 纸白档的浓度下限。白色的坑在「半透」：34% 的白叠在深色壁纸上出来是一块脏灰，
    /// 还会把壁纸的紫红光透上来染成脏粉。所以白底整体抬高 —— 滑杆最左也是干净的浅灰纸，拉满是近白。
    /// 留 0.20 的跨度，拖起来仍然看得出深浅，不至于又变成「拖了没反应」。
    /// </summary>
    private const double PaperBase = 0.78;

    /// <summary>纸白档的浓度上限（拉满时的近白）。</summary>
    private const double PaperMax = 0.985;

    /// <summary>
    /// 分区卡片底色：浓度直接就是 alpha，用户滑杆在 [MinDensity, MaxDensity] 之间调。
    /// 底色只压暗不加亮，透出来的是壁纸的明暗与模糊，这是「毛玻璃」能成立的前提。
    /// </summary>
    public static IBrush CardBrush(DesktopAppearance look)
    {
        var baseColor = TintColor(look);
        var a = (byte)Math.Clamp(Math.Round(255 * CardDensity(look)), 0, 255);
        return new SolidColorBrush(Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));
    }

    /// <summary>
    /// 底色不透明度：毛玻璃开 = 滑杆原值（夹在 [MinDensity, MaxDensity] 内保通透）；
    /// 毛玻璃关 = 实心底色（<see cref="OpaqueDensity"/>），纸白档同理，开关之间差别必须一眼看到。
    /// </summary>
    public static double CardDensity(DesktopAppearance look)
    {
        if (!look.Glass) return OpaqueDensity;

        var density = Math.Clamp(look.ClampedOpacity, MinDensity, MaxDensity);
        var t = density / MaxDensity;

        if (IsLight(look)) return PaperBase + (PaperMax - PaperBase) * t;
        return density;
    }

    /// <summary>悬停抬亮一档的底色（图标格、标题栏按钮用）；低浓度下也要看得见。</summary>
    public static IBrush HoverBrush(DesktopAppearance look)
    {
        // 浅底上「抬亮」根本看不出来，悬停改成压一档灰，深色文字上才有反馈。
        if (IsLight(look))
        {
            var paperAlpha = (byte)Math.Clamp(Math.Round(255 * (0.05 + 0.06 * look.ClampedOpacity)), 0, 255);
            return new SolidColorBrush(Color.FromArgb(paperAlpha, Dark.R, Dark.G, Dark.B));
        }

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
    /// <para>
    /// 例外是「纸白」这一档（白色预设）：它反而要往上提到近白，文字也跟着切深色，
    /// 见 <see cref="IsLight"/> 与 DesktopGroupWindow 里的主题切换。
    /// </para>
    /// </summary>
    public static Color TintColor(DesktopAppearance look)
    {
        var c = BaseColor(look);
        var luma = Luma(c);

        if (IsLight(look))
        {
            var lift = Math.Clamp((LightTargetLuma - luma) / Math.Max(0.01, 1.0 - luma), 0, 1);
            return Blend(c, Paper, lift);
        }

        if (luma <= TargetLuma) return c;

        var t = Math.Clamp((luma - TargetLuma) / Math.Max(0.01, luma - Luma(Dark)), 0, 1);
        return Blend(c, Dark, t);
    }

    /// <summary>浅底亮度门槛。</summary>
    private const double LightThreshold = 0.72;

    /// <summary>
    /// 浅底还要求「接近中性」：亮绿色 #81F467 亮度虽高，但它是彩色底，
    /// 该走压暗那套（深底白字）；只有白/灰这种低饱和的才切「纸白 + 深字」。
    /// </summary>
    private const double LightMaxChroma = 0.18;

    /// <summary>浅底的最低亮度：白色预设也要留住一点纸感，不刺眼。</summary>
    private const double LightTargetLuma = 0.90;

    /// <summary>浅色底的上提目标色（带一点冷的纸白）。</summary>
    private static readonly Color Paper = Color.Parse("#F4F6FA");

    /// <summary>
    /// 当前外观是否走「纸白」一套：只认用户亲手挑的近中性浅色（白色预设 / 手输白灰）。
    /// 跟随主题时不参与 —— 分区窗固定深色这件事是有意为之，不因为主程序切浅色就翻过来。
    /// </summary>
    public static bool IsLight(DesktopAppearance look)
        => !string.IsNullOrWhiteSpace(look.BgColor) && TryParse(look.BgColor, out var c) && IsPaper(c);

    private static bool IsPaper(Color c)
        => Luma(c) >= LightThreshold && Chroma(c) <= LightMaxChroma;

    /// <summary>色度（最大通道差）：白灰≈0，彩色底接近 0.5 以上。</summary>
    private static double Chroma(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
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

    /// <summary>卡片描边：深色底用淡白线，纸白底用淡黑线，1px 只负责把卡片从壁纸上勾出来。</summary>
    public static Color CardBorderColor(DesktopAppearance look)
        => IsLight(look)
            ? Color.FromArgb(30, 0, 0, 0)
            : Color.FromArgb(42, 255, 255, 255);

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
