using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using MiHomeHub.Widgets;
using MiHomeHub.Widgets;

namespace MiHomeHub.Widgets;

/// <summary>
/// 桌面组件「右下角拖拽改尺寸」的唯一实现（移动窗口仍在各组件自己的
/// <c>OnSurfacePressed/Moved/Released</c> 里，因为那段要跟对齐辅助线耦合）。
///
/// 约定：
/// ① 热区 = 卡片右下角 <see cref="GripDip"/>×<see cref="GripDip"/> 的方块，几何判定、
///    不依赖任何子控件 —— 组件内部怎么排都不能把这个角挡住；
/// ② 尺寸存**实例 options** 的 <c>w</c>/<c>h</c>（DIP），随 widgets.json 一起落盘，
///    绝不写类型默认值（否则缩放一个实例会改掉整个类型的默认尺寸）；
/// ③ 缩放全程只改 <see cref="Window.Width"/>/<c>Height</c>；玻璃宿主由卡片 WndProc 的
///    <c>WM_WINDOWPOSCHANGING</c> 同帧跟随（不重采），松手后再做一次 <c>SyncGlass</c> 对账；
/// ④ 尺寸规范见 <see cref="WidgetGrid"/>：宽高只允许 1:1 / 2:1 / 1:2 三种形状，
///    格数只取 2 的幂（2/4/8/16），且落在基准格 <see cref="WidgetGrid.Unit"/>（50 DIP）的整数倍上；
///    范围锁在「设计格数 ×[0.5, 3]」（实际有效档位就是减半 / 加倍，最小 2 格 = 100 DIP；
///    个别组件可在 <see cref="WidgetGrid.Fit"/> 里传自己的 minCells 单独放宽到 1 格 = 50 DIP），
///    缩到画不下内容之前就停住，避免出现裁切一半的卡片。拖把手时**实时**归一化到合法形状，
///    松手落盘与读档回放走同一个 <see cref="WidgetGrid.Fit"/>，不会出现三种形状以外的尺寸。
/// </summary>
internal static class WidgetResize
{
    /// <summary>实例 options 里的宽度键（DIP，整数字符串）。</summary>
    public const string KeyWidth = "w";

    /// <summary>实例 options 里的高度键（DIP，整数字符串）。</summary>
    public const string KeyHeight = "h";

    /// <summary>
    /// 「智能布局补宽」标记（<c>wf=1</c>）：宽度是列内左右对齐算出来的，比格点宽出不超过一个呼吸位
    /// （见 <c>WidgetSmartLayout</c> 的 ②″）。读档时不能再被 <see cref="WidgetGrid.Fit"/> 拉回格点，
    /// 否则上面宽卡的宽度一重启就缩回去，又和下面并排的两张窄卡错开。
    /// 用户手动拖右下角缩放、或「重置组件大小」都会清掉这个标记（拖出来的尺寸本来就在格点上）。
    /// </summary>
    public const string KeyFillWidth = "wf";

    /// <summary>
    /// 右下角热区边长（DIP）。
    /// ⚠️ 热区**不能贴着窗口直角**：卡片圆角由 Border 的 <c>ICustomHitTest</c> 参与命中判定，
    /// 圆角弧线外面那一小块（默认 20 半径时最外侧约 6~9 DIP）点了 Avalonia 收不到事件，
    /// 把手贴在角上就会「看得见、拖不动」。所以整个热区往内让 <see cref="GripInsetDip"/>，见 <see cref="IsInGrip"/>。
    /// </summary>
    public const double GripDip = 20;

    // ───────────────── UiScale 空间转换 ─────────────────
    // 语义约定：options（widgets.json）里永远是**设计空间**的 DIP 值；
    // 窗口 Width/Height 永远是**实际空间**（设计 × UiScale.Factor）。
    // 这两个换算是仅有的两跳，别在别处再乘除。

    /// <summary>设计值 → 窗口实际值（乘全局 UI 缩放因子）。</summary>
    public static Size ToActual(Size design)
        => new(design.Width * UiScale.Factor, design.Height * UiScale.Factor);

    /// <summary>窗口实际值 → 设计值（除全局 UI 缩放因子，落盘/比较设计值前用）。</summary>
    public static Size ToDesign(Size actual)
        => UiScale.Factor > 0
            ? new Size(actual.Width / UiScale.Factor, actual.Height / UiScale.Factor)
            : actual;

    private static Cursor? _gripCursor;
    private static Cursor? _arrowCursor;

    /// <summary>把手常态浓度（鼠标在卡片上，但没压到右下角热区）。</summary>
    private static readonly IBrush GripIdleBrush = new SolidColorBrush(Colors.White, 0.30);

    /// <summary>把手加亮浓度（鼠标压在热区里 = 「这里可以拖」）。</summary>
    private static readonly IBrush GripHotBrush = new SolidColorBrush(Colors.White, 0.85);

    /// <summary>正在进行中的缩放会话数：>0 时指针离开了卡片也不收把手（按着拖过界时会闪）。</summary>
    private static int _activeSessions;

    private static bool IsInteracting => _activeSessions > 0;

    /// <summary>缩放会话开始 / 结束（由 <see cref="WidgetResizeSession"/> 调用）。</summary>
    internal static void BeginSession() => _activeSessions++;

    /// <summary>缩放会话收尾：次数归零后按真实光标位置把把手显隐对账一次（拖过界松手时收起）。</summary>
    internal static void EndSession(Window win)
    {
        _activeSessions = Math.Max(0, _activeSessions - 1);
        SyncHover(win);
    }

    /// <summary>该实例的尺寸是否被右下角把手改过（右键菜单「重置组件大小」只在此时出现）。</summary>
    /// <remarks>
    /// 只被智能布局补过宽（<c>wf=1</c>、且没改高度）不算「用户改过尺寸」：那是布局为了列内边缘对齐
    /// 自己算出来的，菜单里不该冒出一个「重置组件大小」让用户去手动收拾。
    /// </remarks>
    public static bool HasCustomSize(IReadOnlyDictionary<string, string>? options)
    {
        if (options == null) return false;
        if (options.ContainsKey(KeyHeight)) return true;
        bool fill = options.TryGetValue(KeyFillWidth, out var f) && f == "1";
        return options.ContainsKey(KeyWidth) && !fill;
    }

    /// <summary>
    /// 把窗口尺寸恢复成组件设计尺寸，并摘掉实例里的 <c>w</c>/<c>h</c>（落盘由 WidgetManager 统一做）。
    /// 只动几何；玻璃对账 / 抬到卡片之上由调用方按自己的流程收尾。
    /// </summary>
    public static void ResetSize(Window win, Size design, IDictionary<string, string> options)
    {
        options.Remove(KeyWidth);
        options.Remove(KeyHeight);
        options.Remove(KeyFillWidth);

        var actual = ToActual(design);
        if (win.Width > 1 && Math.Abs(win.Width - actual.Width) > 0.5) win.Width = actual.Width;
        if (win.Height > 1 && Math.Abs(win.Height - actual.Height) > 0.5) win.Height = actual.Height;

        AppLog.Info("widget",
            $"[WidgetResize] 重置尺寸 → {design.Width:0}×{design.Height:0} (×{UiScale.Factor:0.00})");
    }

    /// <summary>
    /// 读实例尺寸：没存过 / 存坏了都回落到组件自己的设计尺寸。
    /// 返回值是**实际空间**（设计 × 因子），直接可赋给窗口 Width/Height。
    /// </summary>
    public static Size ReadSize(IReadOnlyDictionary<string, string>? options, Size design,
        Size? minCells = null)
    {
        if (options is null || design.Width < 1 || design.Height < 1) return ToActual(design);
        var want = new Size(
            Parse(options, KeyWidth, design.Width),
            Parse(options, KeyHeight, design.Height));
        var snapped = ClampDesign(want, design, minCells);
        // 智能布局补过宽的实例：宽度保留布局算出来的非格点值（高度仍按格点归一）
        if (options.TryGetValue(KeyFillWidth, out var fill) && fill == "1")
        {
            double maxW = design.Width * WidgetGrid.MaxScale;
            snapped = new Size(Math.Clamp(want.Width, WidgetGrid.Unit, maxW), snapped.Height);
        }
        return ToActual(snapped);
    }

    /// <summary>
    /// 把任意尺寸归一化到合法形状（1:1 / 2:1 / 1:2）与合法范围（设计格数 ×[0.5, 3]，格数只取 2 的幂）。
    /// ⭐ 尺寸规范全部收在 <see cref="WidgetGrid.Fit"/>：拖把手、松手落盘、读档回放走同一份，
    /// 所以界面上的组件尺寸永远只在这三种形状里，智能布局装出来才是整齐的倍数关系。
    /// ⚠️ <paramref name="want"/> 与 <paramref name="design"/> 必须同处**实际空间**（窗口 DIP）。
    /// <paramref name="minCells"/> 是逐组件的缩放下限（格数），默认 null = 「设计 × 0.5」。
    /// </summary>
    public static Size Clamp(Size want, Size design, Size? minCells = null)
        => ToActual(WidgetGrid.Fit(ToDesign(want), ToDesign(design), minCells));

    /// <summary>设计空间的归一化（<see cref="WidgetGrid.Unit"/> 原格点）：读档回放 / 落盘前用。</summary>
    internal static Size ClampDesign(Size want, Size design, Size? minCells = null)
        => WidgetGrid.Fit(want, design, minCells);

    private static double Parse(IReadOnlyDictionary<string, string> options, string key, double fallback)
    {
        if (options.TryGetValue(key, out var raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)
            && v > 0)
            return v;
        return fallback;
    }

    /// <summary>
    /// 点是否落在右下角热区（<paramref name="p"/> 是相对窗口的 DIP 坐标）。
    ///
    /// 热区 = 距右边、下边各 <c>GripInsetDip</c> 起、边长 <see cref="GripDip"/> 的方块。
    /// inset 取卡片圆角半径的 0.34 倍（下限 7）：可命中区域的外角点 (w-i, h-i) 到圆心的距离
    /// (r-i)·√2 ≤ r，恒落在圆角内部（半径 20 时 18.7 &lt; 20），所以热区可以贴得很近 ——
    /// 这里的 inset 只用来「避开圆角外的死区」，不再额外多让，把手因此看起来就在右下角上。
    /// </summary>
    public static bool IsInGrip(Window win, Point p)
    {
        double w = win.Bounds.Width, h = win.Bounds.Height;
        if (w <= 1 || h <= 1) return false;

        double inset = GripInsetDip;
        double right = w - inset, bottom = h - inset;
        return p.X <= right && p.X >= right - GripDip
            && p.Y <= bottom && p.Y >= bottom - GripDip;
    }

    /// <summary>热区内缩量（DIP）：卡片圆角越大越往里让，保证热区整个落在可命中区域里。</summary>
    private static double GripInsetDip => Math.Max(7, GlassStyleService.WidgetCornerRadius * 0.34);

    /// <summary>
    /// 悬停反馈（只在非拖拽态调用）：
    /// ① 光标 —— 热区里给「右下角斜箭头」，其它地方恢复普通箭头；
    /// ② 把手 —— 鼠标在卡片上才显示（默认藏着，不抢内容视觉），压进热区时加亮。
    /// 拖拽中改光标会让光标在缩放/移动之间闪，所以这段只在悬停路径里走。
    /// </summary>
    public static void UpdateCursor(Window win, Point p)
    {
        bool grip = IsInGrip(win, p);
        var want = grip
            ? _gripCursor ??= new Cursor(StandardCursorType.BottomRightCorner)
            : _arrowCursor ??= new Cursor(StandardCursorType.Arrow);
        if (!ReferenceEquals(win.Cursor, want)) win.Cursor = want;

        if (GripOf(win) is { } marker)
        {
            if (!marker.IsVisible) marker.IsVisible = true;
            var brush = grip ? GripHotBrush : GripIdleBrush;
            if (!ReferenceEquals(marker.Stroke, brush)) marker.Stroke = brush;
        }
    }

    /// <summary>
    /// 按真实光标位置对账把手显隐：松手时指针可能已经在卡片外（拖过界），
    /// 这时不该留一个把手在屏上。缩放会话收尾时调用。
    /// </summary>
    public static void SyncHover(Window win)
    {
        if (IsInteracting) return;
        if (GripOf(win) is not { } marker) return;
        marker.IsVisible = IsCursorInside(win);
        if (!ReferenceEquals(marker.Stroke, GripIdleBrush)) marker.Stroke = GripIdleBrush;
    }

    /// <summary>屏幕光标是否落在本窗矩形内（物理像素，和 GetWindowRect 同系）。</summary>
    private static bool IsCursorInside(Window win)
    {
        var hwnd = win.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (hwnd == nint.Zero) return false;
        if (!Win32.GetCursorPos(out var cp)) return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        return cp.X >= r.Left && cp.X < r.Right && cp.Y >= r.Top && cp.Y < r.Bottom;
    }

    /// <summary>从窗口内容里取回把手（<see cref="AttachGrip"/> 把它包在 <c>Border.Child</c> 的 Panel 里）。</summary>
    private static Avalonia.Controls.Shapes.Path? GripOf(Window win)
        => win.Content is Border { Child: Panel { Tag: GripTag } host }
            ? host.Children.OfType<Avalonia.Controls.Shapes.Path>().LastOrDefault()
            : null;

    /// <summary>
    /// 在卡片内容之上叠一个右下角把手（三条斜线）：纯视觉提示，<c>IsHitTestVisible=false</c>，
    /// 命中判定始终走 <see cref="IsInGrip"/> 的几何判定，免得被某个子控件把点击吃掉。
    ///
    /// 做法是把 <c>Border.Child</c> 包进一个 Panel（原内容 + 把手），不改任何 XAML：
    /// 组件窗口结构各不相同，逐个改 XAML 容易漏，包一层对所有组件一致。
    ///
    /// 这里是**缩放语义的唯一分发点**（<see cref="IWidgetWindow.ResizeMode"/>）：
    /// · <see cref="WidgetResizeMode.Reflow"/>（默认）→ 原样挂把手，窗口变大 = 画布变大、显示更多内容；
    /// · <see cref="WidgetResizeMode.Scale"/> → 额外把内容钉进「设计尺寸画布」并套
    ///   <c>Viewbox</c>，窗口变大 = 整块 UI（字号/间距/图标）同比放大。
    ///
    /// ⭐ 默认<b>隐藏</b>，鼠标移入卡片才显示、移出即收 —— 常驻显示会让每个组件角落都多三道线，
    /// 静态观感太吵；用户去抓右下角时它自己会出来（压进热区还会加亮）。
    /// </summary>
    /// <param name="win">组件窗口（用来读 <see cref="IWidgetWindow.ResizeMode"/>）。</param>
    /// <param name="card">卡片根 Border（<c>win.Content</c>）。</param>
    /// <param name="design">组件设计尺寸（DIP）：Scale 语义下作为「设计画布」的固定大小。</param>
    public static void AttachGrip(Window win, Border card, Size design)
    {
        if (card.Child is null || card.Child is Panel { Tag: GripTag }) return;

        Control content = card.Child;

        // ① 缩放语义 = Scale：内容钉死在设计画布上，再由 Viewbox 整体等比缩放。
        //    ⚠️ 必须先 card.Child = null 再建画布：内容还挂在 Border 上时直接
        //    new Grid { Children = { content } } 会抛 "The Control already has a parent."
        if (win is IWidgetWindow { ResizeMode: WidgetResizeMode.Scale })
        {
            card.Child = null;
            content = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.Both,
                Child = new Grid
                {
                    Width = design.Width,
                    Height = design.Height,
                    Children = { content },
                },
            };
        }

        // ② UiScale：内容再包一层 LayoutTransformControl（等比放大，Avalonia 无 WPF 式 LayoutTransform 属性）。
        // 卡片本体（win.Content 的 Border）不动 —— GripOf / IsInGrip 的窗口空间几何判定不受影响；
        // 把手在 transform 外，保持设计尺寸的视觉。
        if (UiScale.Active)
        {
            // ⚠️ 必须先 card.Child = null 再包：原内容还挂在 Border 上时直接
            //    new LayoutTransformControl { Child = content } 会抛
            //    "The Control already has a parent."，组件恢复整批失败。
            card.Child = null;
            content = new Avalonia.Controls.LayoutTransformControl
            {
                LayoutTransform = new ScaleTransform(UiScale.Factor, UiScale.Factor),
                Child = content,
            };
        }

        var grip = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 0,9 L 9,0 M 0,5 L 5,0 M 0,1 L 1,0"),
            Stroke = GripIdleBrush,
            StrokeThickness = 1.2,
            StrokeLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, GripInsetDip - 1, GripInsetDip - 1),
            IsHitTestVisible = false,
            IsVisible = false,
        };

        var content2 = content;
        card.Child = null;
        card.Child = new Panel { Tag = GripTag, Children = { content2, grip } };

        card.PointerEntered += (_, _) => grip.IsVisible = true;
        card.PointerExited += (_, _) =>
        {
            // 按着拖过界时指针已经在卡片外了，但把手要留着（否则拖一半就闪没了）
            if (IsInteracting) return;
            grip.IsVisible = false;
        };
    }

    /// <summary>
    /// 界面缩放热切换（不重启）：把**已经建好**的组件窗口换到新因子。
    ///
    /// 两件事：
    /// ① 内容缩放层 —— 建窗时因子 = 1 的实例根本没有 LayoutTransformControl，
    ///    这里现场补一层（先把内容从 Panel 里摘出来再挂，绝不「已有父级还认父」）；
    /// ② 窗口尺寸 —— 窗口里存的是**实际值**，先 ÷ 旧因子还原设计值，再 × 新因子。
    ///    设计值不变，所以 widgets.json 不用改，换回来也不会漂移。
    /// </summary>
    /// <returns>确实动过这个窗口（调用方还要做落位钳制 / 玻璃对账）</returns>
    public static bool ApplyUiScale(Window win, double oldFactor, double newFactor)
    {
        if (win.Content is not Border card || card.Child is not Panel { Tag: GripTag } host)
            return false;
        if (oldFactor <= 0 || newFactor <= 0) return false;

        var scale = new ScaleTransform(newFactor, newFactor);
        var ltc = host.Children.OfType<LayoutTransformControl>().FirstOrDefault();
        if (ltc != null)
        {
            // 换**新实例**而不是改 ScaleX：LayoutTransformControl 只监听属性变更，
            // 直接改 transform 内部字段不会触发重新测量（内容会保持旧大小）。
            ltc.LayoutTransform = scale;
        }
        else if (Math.Abs(newFactor - 1.0) >= 0.001 && host.Children.Count > 0)
        {
            // 建窗时因子 = 1 → 没有缩放层。先摘父级（Panel.Children.Remove）再挂，否则抛
            // "The Control already has a parent."（这正是「改分辨率后组件全没」的老坑）。
            var content = host.Children[0];
            host.Children.RemoveAt(0);
            host.Children.Insert(0, new LayoutTransformControl
            {
                LayoutTransform = scale,
                Child = content,
            });
        }

        double w = win.Width > 1 ? win.Width : 0;
        double h = win.Height > 1 ? win.Height : 0;
        if (w <= 1 || h <= 1) return ltc != null;
        double designW = w / oldFactor;
        double designH = h / oldFactor;
        double actualW = designW * newFactor;
        double actualH = designH * newFactor;
        if (Math.Abs(win.Width - actualW) > 0.5) win.Width = actualW;
        if (Math.Abs(win.Height - actualH) > 0.5) win.Height = actualH;
        return true;
    }

    private const string GripTag = "widget-grip";
}

/// <summary>
/// 一次右下角缩放的会话：按下时记「起始鼠标位置 + 起始尺寸」，移动时按光标位移换算 DIP 尺寸，
/// 松手时交给调用方落盘。与拖拽移动互斥（按下时二选一）。
///
/// ⭐ 缩放**不再**吸附其它组件的邻边：宽高只吸附到基准格点并按
/// 1:1 / 2:1 / 1:2 归一（<see cref="WidgetGrid.Fit"/>）。邻边吸附会让尺寸落成
/// 「62 的倍数之外」的值，破坏倍数关系 —— 整齐优先。左上角被把手锁住，所以只可能改宽高。
///
/// ⭐ 尺寸写的是 <see cref="Window.Width"/>/<c>Height</c>（DIP），Windows 侧是 SetWindowPos，
/// 卡片 WndProc 在 <c>WM_WINDOWPOSCHANGING</c> 里已经把新矩形同帧推给玻璃宿主，
/// 所以拖动过程中不会出现「卡片变大、玻璃还停在旧矩形」的错位。
/// </summary>
internal sealed class WidgetResizeSession
{
    private readonly Window _win;
    private readonly Size _design;
    private readonly Size? _minCells;
    private readonly Action _live;
    private readonly PixelPoint _startMouse;
    private readonly double _startW;
    private readonly double _startH;
    private readonly double _scale;

    private Size _size;

    private WidgetResizeSession(Window win, Size design, Action live, Size? minCells)
    {
        _win = win;
        _design = design;
        _minCells = minCells;
        _live = live;
        _startMouse = Win32.GetCursorPos(out var cp) ? new PixelPoint(cp.X, cp.Y) : default;
        // 窗口与拖拽全程在**实际空间**（设计 × UiScale 因子）进行
        var designActual = WidgetResize.ToActual(design);
        _startW = win.Width > 1 ? win.Width : designActual.Width;
        _startH = win.Height > 1 ? win.Height : designActual.Height;
        _size = new Size(_startW, _startH);
        double scale = win.RenderScaling;
        _scale = scale > 0 ? scale : 1.0;
        WidgetResize.BeginSession();
    }

    public static WidgetResizeSession Begin(Window win, Size design, Action live, Size? minCells = null)
        => new(win, design, live, minCells);

    /// <summary>当前尺寸（DIP），松手时落盘用。</summary>
    public Size Size => _size;

    /// <summary>指针移动：按光标位移改窗口尺寸（物理像素位移 ÷ DPI 缩放 = DIP 位移，实际空间）。</summary>
    public void Apply()
    {
        if (!Win32.GetCursorPos(out var cp)) return;

        double wantW = _startW + (cp.X - _startMouse.X) / _scale;
        double wantH = _startH + (cp.Y - _startMouse.Y) / _scale;
        // 只吸附格点 + 归一形状（1:1 / 2:1 / 1:2）；不再吸附邻边，避免出现格点之外的尺寸
        var next = WidgetResize.Clamp(new Size(wantW, wantH), WidgetResize.ToActual(_design), _minCells);

        if (Math.Abs(next.Width - _size.Width) < 0.5 && Math.Abs(next.Height - _size.Height) < 0.5) return;

        _size = next;
        if (Math.Abs(_win.Width - next.Width) > 0.5) _win.Width = next.Width;
        if (Math.Abs(_win.Height - next.Height) > 0.5) _win.Height = next.Height;
        try { _live(); } catch { }
    }

    /// <summary>把最终尺寸写进实例 options（落盘由 WidgetManager.ScheduleSave 统一做）。</summary>
    public void Commit(IDictionary<string, string> options)
    {
        // 手动拖出来的尺寸一律回格点：上一轮智能布局补宽的非格点宽度到此作废
        options.Remove(WidgetResize.KeyFillWidth);
        // 实际空间 → 设计空间再归一落盘：widgets.json 里永远是 Unit 格点 + 1:1 / 2:1 / 1:2 的设计值
        var designSize = WidgetResize.ClampDesign(WidgetResize.ToDesign(_size), _design, _minCells);

        // 缩回设计尺寸就把键摘掉：存档里不留「等于默认值」的冗余项
        if (Math.Abs(designSize.Width - _design.Width) < 0.5) options.Remove(WidgetResize.KeyWidth);
        else options[WidgetResize.KeyWidth] = ((int)Math.Round(designSize.Width)).ToString();

        if (Math.Abs(designSize.Height - _design.Height) < 0.5) options.Remove(WidgetResize.KeyHeight);
        else options[WidgetResize.KeyHeight] = ((int)Math.Round(designSize.Height)).ToString();

        AppLog.Info("widget",
            $"[WidgetResize] 尺寸 {_size.Width:0}×{_size.Height:0}（设计 {designSize.Width:0}×{designSize.Height:0}）");

        // 收尾：拖拽期间可能残留的辅助线 / 会话状态清干净，再按真实光标位置对账把手显隐
        WidgetAlignGuide.End();
        // 松手时光标可能已经在卡片外（拖过界）：按真实位置对账，别留一个把手在屏上
        WidgetResize.EndSession(_win);
    }
}

