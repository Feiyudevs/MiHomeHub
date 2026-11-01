using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 分区「⋯」菜单：独立小窗。
/// 贴桌面的分区窗是 HWND_BOTTOM + NOACTIVATE，用 Flyout / Popup 会被压到普通窗口之下，
/// 所以这里按参考项目的做法开一个自己的顶层菜单窗（置顶 + 失焦即收）。
/// 位置规则：默认贴在按钮下方、右缘对齐按钮右缘；下方放不下才翻到上方。
/// </summary>
public partial class DesktopMenuWindow : Window
{
    /// <summary>按钮在屏幕上的矩形（物理像素）。</summary>
    private PixelRect _anchor;

    private DateTime _openedAt;

    public DesktopMenuWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated;
    }

    public DesktopGroupVm? Target { get; private set; }

    public void Bind(DesktopGroupVm vm, PixelRect anchor)
    {
        Target = vm;
        DataContext = vm;
        _anchor = anchor;

        // 先给个「按钮正下方」的粗略落点，避免窗口先在屏幕左上角闪一帧
        Position = new PixelPoint(anchor.X, anchor.Bottom);
    }

    /// <summary>
    /// 宿主分区窗是 NOACTIVATE，Show 之后系统可能立刻补一条 WA_INACTIVE（窗口还没真激活就失活），
    /// 直接关会把菜单刚开就吞掉 —— 开场 300ms 内的失活一律忽略。
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        var age = (DateTime.UtcNow - _openedAt).TotalMilliseconds;
        DesktopLog.Write($"[menu] Deactivated age={age:F0}ms");
        if (age < 300) return;
        Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _openedAt = DateTime.UtcNow;

        // SizeToContent=Height：不先跑一次布局，ClientSize 还是 0，算出来的高度就没有意义
        UpdateLayout();

        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        var dipW = ClientSize.Width > 1 ? ClientSize.Width : Width;
        var dipH = ClientSize.Height > 1 ? ClientSize.Height : Bounds.Height;
        var w = (int)Math.Ceiling(Math.Max(1, dipW) * scale);
        var h = (int)Math.Ceiling(Math.Max(1, dipH) * scale);

        var screen = Screens.ScreenFromPoint(_anchor.TopLeft)
                     ?? Screens.ScreenFromPoint(_anchor.BottomRight)
                     ?? Screens.Primary;
        var wa = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        const int gap = 4;
        const int edge = 8;

        // 垂直：默认下方，下方放不下再翻到按钮上方，两边都放不下就在工作区内夹住
        var below = _anchor.Bottom + gap;
        var above = _anchor.Y - h - gap;
        int y;
        if (below + h <= wa.Bottom - edge) y = below;
        else if (above >= wa.Y + edge) y = above;
        else y = Math.Clamp(below, wa.Y + edge, Math.Max(wa.Y + edge, wa.Bottom - h - edge));

        // 水平：右缘对齐按钮右缘（菜单从按钮左侧展开），再夹进工作区
        var x = _anchor.Right - w;
        x = Math.Clamp(x, wa.X + edge, Math.Max(wa.X + edge, wa.Right - w - edge));

        Position = new PixelPoint(x, y);
        DesktopLog.Write($"[menu] anchor={_anchor} size={w}x{h} scale={scale:F2} wa={wa} → ({x},{y})");
        Activate();
    }

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopGroupVm vm) return;
        if (sender is not Button { Tag: string tag }) return;

        switch (tag)
        {
            case "add":
                Close();
                vm.AddDeviceCommand.Execute(null);
                break;
            case "rename":
                Close();
                vm.RenameCommand.Execute(null);
                break;
            case "appearance":
                Close();
                vm.OpenAppearanceCommand.Execute(null);
                break;
            case "dissolve":
                vm.DissolveCommand.Execute(null);
                // 第一次点只是进入确认态（文案变了），菜单留着让用户再点一次
                if (vm.DissolveLabel == "解散分区") Close();
                break;
        }
    }
}
