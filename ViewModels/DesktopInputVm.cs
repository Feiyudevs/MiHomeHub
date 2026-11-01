using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MiHome.ViewModels;

/// <summary>通用文本输入弹窗（分区重命名等）：只有一个输入框 + 确定/取消。</summary>
public partial class DesktopInputVm : ObservableObject
{
    [ObservableProperty] private string _header = "重命名";
    [ObservableProperty] private string _value = "";

    public Action? CloseRequested { get; set; }

    /// <summary>确认后的回调，由宿主传入。</summary>
    public Action<string>? Apply { get; set; }

    [RelayCommand]
    private void Confirm()
    {
        Apply?.Invoke(Value);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
