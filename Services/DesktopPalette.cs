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

    /// <summary>分区卡片底色：毛玻璃 → 薄纱，非毛玻璃 → 厚底；两者都乘背景透明度。</summary>
    public static IBrush CardBrush(DesktopAppearance look)
    {
        var baseColor = BaseColor(look);
        var factor = (look.Glass ? 0.46 : 0.94) * look.ClampedOpacity;
        var a = (byte)Math.Clamp(Math.Round(255 * factor), 0, 255);
        return new SolidColorBrush(Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));
    }

    /// <summary>悬停抬亮一档的底色（图标格、标题栏按钮用）。</summary>
    public static IBrush HoverBrush(DesktopAppearance look)
    {
        var c = BaseColor(look);
        var a = (byte)Math.Clamp(Math.Round(255 * 0.10 * look.ClampedOpacity), 0, 255);
        return new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
    }

    /// <summary>磨砂染色强度：透明度越低越透。</summary>
    public static double TintOpacity(DesktopAppearance look) => 0.20 * look.ClampedOpacity;

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
