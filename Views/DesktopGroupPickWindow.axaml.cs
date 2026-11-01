using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MiHome.Models;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 「添加到桌面组件」选择分区弹窗：列出已有分区点一下即加入，底部可输入名称新建分区。
/// 与通用输入弹窗一样是可激活的普通窗口，输入框能正常拿到键盘焦点。
/// </summary>
public partial class DesktopGroupPickWindow : Window
{
    private readonly DesktopGroupPickVm _vm = new();

    public DesktopGroupPickWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.CloseRequested = Close;
        Opened += (_, _) => FocusName();
    }

    public void Bind(MijiaDevice device) => _vm.Bind(device);

    /// <summary>打开就把光标放进新分区名输入框：直接回车就能建组加入。</summary>
    private void FocusName()
    {
        Dispatcher.UIThread.Post(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>点分区行：加入该分区（DataTemplate 里的命令绑定在编译期拿不到，走 Tag + 代码后置）。</summary>
    private void OnGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GroupPickItem item })
            _vm.PickCommand.Execute(item);
        e.Handled = true;
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        _vm.CreateCommand.Execute(null);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // 焦点在「新建并添加」按钮上时由按钮自己处理，这里不再重复触发，避免建出两个分区
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Button) return;
            _vm.CreateCommand.Execute(null);
            e.Handled = true;
        }
    }
}
