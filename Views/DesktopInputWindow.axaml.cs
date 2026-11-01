using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>通用输入弹窗：可激活的普通窗口，输入框能正常拿到键盘焦点。</summary>
public partial class DesktopInputWindow : Window
{
    private readonly DesktopInputVm _vm = new();

    public DesktopInputWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.CloseRequested = Close;
        Opened += (_, _) => FocusInput();
    }

    public void Bind(string header, string initial, Action<string> apply)
    {
        _vm.Header = header;
        _vm.Value = initial;
        _vm.Apply = apply;
    }

    private void FocusInput()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Input.Focus();
            Input.SelectAll();
        }, DispatcherPriority.Input);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _vm.ConfirmCommand.Execute(null);
            e.Handled = true;
        }
    }
}
