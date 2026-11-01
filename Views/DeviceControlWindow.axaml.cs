using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 桌面分区里的设备控制弹窗宿主。
/// 内容是弹窗基类（遮罩 + 居中卡片）里的设备控制面板，数据仍挂在主视图模型上，
/// 所以开关/滑杆的命令与主窗口抽屉完全共用一套。
/// 出现位置跟随「点它的那块屏幕」，卡片标题栏可拖动窗口。
/// </summary>
public partial class DeviceControlWindow : Window
{
    private readonly MainViewModel _vm;

    public DeviceControlWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) => vm.PropertyChanged -= OnVmPropertyChanged;
    }

    /// <summary>
    /// 在鼠标点击处弹出（多屏时按光标所在的那块屏）；光标取不到时退回「点它的分区所在的屏幕居中」。
    /// 已经在显示时只挪位置，不重建，避免闪一下。
    /// </summary>
    public void ShowFor(Window? anchor)
    {
        // 先摆位再 Show：反过来的话窗口会先在默认位置闪一帧再跳过去
        // 参考点用「点它的分区窗」位置，保证落在分区所在的那块屏；取不到再用光标
        if (!PopupPlacement.CenterOnScreen(this, Width, Height, anchor?.Position)) PlaceOn(anchor);
        if (!IsVisible) Show();
        Activate();
    }

    private void PlaceOn(Window? anchor)
    {
        var screen = (anchor != null ? Screens.ScreenFromWindow(anchor) : null)
                     ?? (IsVisible ? Screens.ScreenFromPoint(Position) : null)
                     ?? Screens.Primary;
        if (screen == null) return;

        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        var w = (int)Math.Ceiling(Width * scale);
        var h = (int)Math.Ceiling(Height * scale);

        var wa = screen.WorkingArea;
        var x = wa.X + Math.Max(0, (wa.Width - w) / 2);
        var y = wa.Y + Math.Max(0, (wa.Height - h) / 2);
        Position = new PixelPoint(x, y);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDevicePanelOpen) && !_vm.IsDevicePanelOpen)
            Close();
    }
}
