// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia.Controls;
using Avalonia.Input;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

/// <summary>
/// 设备详情正文（电源/开关/音量/耗材/窗帘/灯光/空调/高级设置/设备信息）。
/// 右侧抽屉与桌面分区的控制弹窗共用这一个组件，避免两份内容各自漂移。
/// 数据挂在 MainViewModel 的 DrawerDevice 上，命令也是主视图模型那一套。
/// 数值步进与手输都在 ctl:NumberStepper 里，这一层不再单独接管指针和焦点。
/// </summary>
public partial class DeviceDetailView : UserControl
{
    public DeviceDetailView()
    {
        InitializeComponent();
    }

    /// <summary>双击画面直接放大：正在播才响应，没播时双击不替用户开流。</summary>
    private void OnCameraStageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsCameraLive) return;
        vm.OpenCameraPlayerWindowCommand.Execute(null);
    }
}
