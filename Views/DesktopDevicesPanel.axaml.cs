// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia.Controls;
using MiHome.Controls;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>桌面设备分区的内容：设备宫格 + 场景宫格 + 空态。样式与主窗口那张设备卡同源，只在这里覆盖成玻璃底。</summary>
public partial class DesktopDevicesPanel : UserControl
{
    private readonly CardReorderDrag _deviceDrag;
    private readonly CardReorderDrag _sceneDrag;

    public DesktopDevicesPanel()
    {
        InitializeComponent();

        // 两块宫格各自一套拖拽排序：只在同一面里换顺序，设备不会拖进场景那面
        _deviceDrag = new CardReorderDrag(DeviceItems, DeviceDropBar, DeviceDropFrame, MoveDevice);
        _sceneDrag = new CardReorderDrag(SceneItems, SceneDropBar, SceneDropFrame, MoveScene);
    }

    private void MoveDevice(int from, int to)
    {
        if (DataContext is DesktopGroupVm vm) vm.MoveDevice(from, to);
    }

    private void MoveScene(int from, int to)
    {
        if (DataContext is DesktopGroupVm vm) vm.MoveScene(from, to);
    }
}
