using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

    public DesktopZoneVm? Target { get; private set; }

    public void Bind(DesktopZoneVm vm, PixelRect anchor)
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

        // 落点规则（下方优先、放不下翻上方、右缘对齐按钮）与音量条、选音箱浮层完全一样，统一走 AnchorTo
        PopupPlacement.AnchorTo(this, _anchor);
        Activate();

        // 首帧先画在「收起」状态，等渲染循环跑起来再加 .open，过渡才有起点
        Dispatcher.UIThread.Post(() => Card.Classes.Add("open"), DispatcherPriority.Render);
    }

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopZoneVm vm) return;
        if (sender is not Button { Tag: string tag }) return;

        switch (tag)
        {
            case "add":
                Close();
                if (vm is DesktopGroupVm g) g.AddDeviceCommand.Execute(null);
                break;
            case "rename":
                Close();
                vm.RenameCommand.Execute(null);
                break;
            case "addzone":
                Close();
                DesktopHost.CreateBlankGroup(vm);
                break;
            case "appearance":
                Close();
                vm.OpenAppearanceCommand.Execute(null);
                break;
            case "dissolve":
                vm.DissolveCommand.Execute(null);
                // 第一次点只是进入确认态（文案变了），菜单留着让用户再点一次；
                // 真正执行完（分区已收起 / 媒体分区已关闭）才关掉。
                if (!vm.IsAwaitingDangerConfirm) Close();
                break;
        }
    }
}
