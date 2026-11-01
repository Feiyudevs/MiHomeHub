using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 分区「添加设备」弹窗宿主：自绘卡片（标题栏可拖动、点遮罩关闭），
/// 正文按设备类型分组、每组是网格卡片；勾选即时生效，没有「确定」二次提交。
/// </summary>
public partial class DesktopPickerWindow : Window
{
    private readonly DesktopPickerVm _vm = new();

    public DesktopPickerWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.CloseRequested = Close;
        Opened += (_, _) => FocusSearch();
    }

    public DesktopGroupVm? Target => _vm.Target;

    public void Bind(DesktopGroupVm vm)
    {
        _vm.Bind(vm);
        Title = $"添加设备 · {vm.Name}";
    }

    /// <summary>打开就把光标放进搜索框：用户在长列表里几乎总是先搜。</summary>
    private void FocusSearch()
    {
        Dispatcher.UIThread.Post(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // 标题栏空白处按下才拖；右侧关闭按钮自己消费了事件
        BeginMoveDrag(e);
        e.Handled = true;
    }

    /// <summary>点房间胶囊：切高亮 + 把正文滚到对应分组（「全部房间」回顶部）。</summary>
    private void OnRoomTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PickerTab tab }) return;
        var index = _vm.ActivateTab(tab);
        // 分组容器要等布局跑完才有坐标，延后一帧再算（首次打开、刚改过搜索词都适用）
        Dispatcher.UIThread.Post(() => ScrollToGroup(index), DispatcherPriority.Loaded);
        e.Handled = true;
    }

    private void ScrollToGroup(int index)
    {
        if (index < 0)
        {
            BodyScroll.Offset = new Vector(0, 0);   // 「全部房间」
            return;
        }

        if (GroupList.ContainerFromIndex(index) is not Control container) return;

        var point = container.TranslatePoint(new Point(0, 0), BodyScroll);
        if (point is null) return;

        // TranslatePoint 给的是视口内坐标，内容坐标 = 当前偏移 + 视口坐标
        var target = BodyScroll.Offset.Y + point.Value.Y - 2;
        var max = Math.Max(0, BodyScroll.Extent.Height - BodyScroll.Viewport.Height);
        BodyScroll.Offset = new Vector(0, Math.Clamp(target, 0, max));
    }

    private void OnMaskPressed(object? sender, PointerPressedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }
}
