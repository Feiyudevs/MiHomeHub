using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 分区「添加设备 / 场景」弹窗宿主：自绘卡片（标题栏可拖动、点遮罩关闭），
/// 正文按房间分组、场景单独一组，每组是网格卡片；勾选即时生效，没有「确定」二次提交。
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
        Title = $"添加设备 / 场景 · {vm.Name}";
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

    /// <summary>点分类胶囊：下面只留那一组（「全部房间」= 全部房间的设备），并把正文拉回顶部。</summary>
    private void OnRoomTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PickerTab tab }) return;
        _vm.SelectTab(tab);
        BodyScroll.Offset = new Vector(0, 0);
        e.Handled = true;
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
