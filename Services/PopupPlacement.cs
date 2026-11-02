using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace MiHome.Services;

/// <summary>
/// 弹窗定位。
///  - <see cref="CenterOnScreen"/>：在「鼠标所在的那块屏」的工作区居中弹出。
///  - <see cref="PlaceAtCursor"/>：在鼠标点击处弹出（右下偏移一点，别被光标压住），并夹进所在屏工作区。
/// 参考 Zonex 的 DesktopContextMenuWindow.ShowAt：Position 是物理像素，Width/Height 是 DIP，
/// 唯一正确的换算是 px = dip × 屏缩放。
/// </summary>
internal static class PopupPlacement
{
    /// <summary>光标与弹窗边缘的间距（DIP），留出光标本身，避免正好压在标题栏上。</summary>
    private const int GapDip = 12;

    /// <summary>距屏幕工作区边缘的最小留白（DIP）。</summary>
    private const int EdgeDip = 8;

    /// <summary>锚定浮层（分区菜单 / 音量条 / 选音箱）与触发按钮之间的缝隙（DIP）。</summary>
    private const int AnchorGapDip = 4;

    /// <summary>
    /// 把浮层贴到触发按钮的矩形下方：下方放不下才翻到上方，水平默认右缘对齐按钮右缘（菜单从按钮左侧展开）。
    /// 贴桌面的分区窗是 HWND_BOTTOM + NOACTIVATE，Avalonia 的 Flyout / Popup 会被压到普通窗口之下，
    /// 所以菜单、音量条、选音箱都只能各开一个独立小窗，落点统一走这里。
    /// </summary>
    public static void AnchorTo(Window win, PixelRect anchor, bool rightAligned = true)
    {
        // SizeToContent 的窗口在布局跑完前 ClientSize 还是 0，算出来的高度没有意义
        win.UpdateLayout();

        var scale = win.RenderScaling <= 0 ? 1 : win.RenderScaling;
        var dipW = win.ClientSize.Width > 1 ? win.ClientSize.Width : win.Width;
        var dipH = win.ClientSize.Height > 1 ? win.ClientSize.Height : win.Bounds.Height;
        var w = (int)Math.Ceiling(Math.Max(1, dipW) * scale);
        var h = (int)Math.Ceiling(Math.Max(1, dipH) * scale);

        var screens = ScreensOf(win);
        var screen = screens?.ScreenFromPoint(anchor.TopLeft)
                     ?? screens?.ScreenFromPoint(anchor.BottomRight)
                     ?? screens?.Primary;
        var wa = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var gap = (int)Math.Round(AnchorGapDip * scale);
        var edge = (int)Math.Round(EdgeDip * scale);

        // 垂直：默认下方，下方放不下翻到上方，两边都放不下就夹在工作区里
        var below = anchor.Bottom + gap;
        var above = anchor.Y - h - gap;
        int y;
        if (below + h <= wa.Bottom - edge) y = below;
        else if (above >= wa.Y + edge) y = above;
        else y = Math.Clamp(below, wa.Y + edge, Math.Max(wa.Y + edge, wa.Bottom - h - edge));

        // 水平：右缘对齐（音量条与菜单都是靠左展开的窄条），再夹进工作区
        var x = rightAligned ? anchor.Right - w : anchor.X;
        x = Math.Clamp(x, wa.X + edge, Math.Max(wa.X + edge, wa.Right - w - edge));

        win.Position = new PixelPoint(x, y);
        DesktopLog.Write($"[popup] {win.GetType().Name} anchor={anchor} size={w}x{h} scale={scale:F2} wa={wa} → ({x},{y})");
    }

    /// <summary>
    /// 在「当前屏幕」工作区居中弹出。<paramref name="widthDip"/>/<paramref name="heightDip"/> 传窗口真实尺寸
    /// （Show 之前拿不到 Bounds，用 XAML 里声明的常量最稳）。
    /// <para>
    /// 挑屏顺序：显式参考点 → 鼠标位置 → 主屏。**不能**用 ScreenFromWindow：弹窗是复用实例，
    /// 第二次打开时它还在上次的位置上，会一直把新弹窗摆回旧屏。
    /// </para>
    /// </summary>
    public static bool CenterOnScreen(Window win, double widthDip, double heightDip, PixelPoint? at = null)
    {
        var screens = ScreensOf(win);
        if (screens == null) return false;

        var reference = at ?? CursorPoint() ?? WindowPoint(win);
        var screen = (reference != null ? screens.ScreenFromPoint(reference.Value) : null)
                     ?? screens.Primary;
        if (screen == null) return false;

        var scale = screen.Scaling > 0 ? screen.Scaling : win.RenderScaling;
        if (scale <= 0) scale = 1;

        var w = (int)Math.Ceiling(Math.Max(1, widthDip) * scale);
        var h = (int)Math.Ceiling(Math.Max(1, heightDip) * scale);
        var wa = screen.WorkingArea;
        var edge = (int)Math.Round(EdgeDip * scale);

        // 居中；窗口比工作区还大时贴左上，不居中（否则两侧都被挤出屏幕，标题栏都摸不到）
        var x = w >= wa.Width ? wa.X : wa.X + (wa.Width - w) / 2;
        var y = h >= wa.Height ? wa.Y : wa.Y + (wa.Height - h) / 2;

        x = Math.Clamp(x, wa.X, Math.Max(wa.X, wa.Right - w));
        y = Math.Clamp(y, wa.Y, Math.Max(wa.Y, wa.Bottom - h));

        win.Position = new PixelPoint(x, y);
        DesktopLog.Write($"[popup] {win.GetType().Name} center ref={reference} screen={wa} size={w}x{h} scale={scale:F2} → ({x},{y})");
        return true;
    }

    /// <summary>
    /// 把窗口摆到鼠标处。多屏时按光标所在屏；右下放不下翻到左上方，再夹住。
    /// </summary>
    public static bool PlaceAtCursor(Window win, double widthDip, double heightDip)
    {
        var point = CursorPoint();
        if (point == null) return false;

        var screens = ScreensOf(win);
        if (screens == null) return false;

        var screen = screens.ScreenFromPoint(point.Value) ?? screens.Primary;
        if (screen == null) return false;

        var scale = screen.Scaling > 0 ? screen.Scaling : win.RenderScaling;
        if (scale <= 0) scale = 1;

        var w = (int)Math.Ceiling(Math.Max(1, widthDip) * scale);
        var h = (int)Math.Ceiling(Math.Max(1, heightDip) * scale);
        var gap = (int)Math.Round(GapDip * scale);
        var edge = (int)Math.Round(EdgeDip * scale);

        var wa = screen.WorkingArea;

        // 默认落在光标右下方；右边/下面放不下就翻到左上方，两边都不够就夹进工作区
        var x = point.Value.X + gap;
        if (x + w > wa.Right - edge) x = point.Value.X - gap - w;

        var y = point.Value.Y + gap;
        if (y + h > wa.Bottom - edge) y = point.Value.Y - gap - h;

        x = Math.Clamp(x, wa.X + edge, Math.Max(wa.X + edge, wa.Right - w - edge));
        y = Math.Clamp(y, wa.Y + edge, Math.Max(wa.Y + edge, wa.Bottom - h - edge));

        win.Position = new PixelPoint(x, y);
        DesktopLog.Write($"[popup] {win.GetType().Name} cursor={point} size={w}x{h} scale={scale:F2} wa={wa} → ({x},{y})");
        return true;
    }

    private static PixelPoint? CursorPoint()
        => DesktopNative.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null;

    /// <summary>光标取不到时的兜底：主窗口所在位置。</summary>
    private static PixelPoint? WindowPoint(Window win)
    {
        if (win.IsVisible) return win.Position;
        return MainWindowPoint();
    }

    private static PixelPoint? MainWindowPoint()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } main)
            return main.Position;
        return null;
    }

    /// <summary>Show 之前窗口自己的 Screens 还没就绪：先退到主窗口，再退到任意一个已开窗（托盘后主窗可能已关）。</summary>
    private static Screens? ScreensOf(Window win)
    {
        if (win.Screens != null) return win.Screens;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow?.Screens is { } mainScreens) return mainScreens;

            foreach (var w in desktop.Windows)
            {
                if (w.Screens is { } s) return s;
            }
        }

        return null;
    }
}
