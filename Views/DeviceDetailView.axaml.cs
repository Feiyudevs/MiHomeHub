// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia.Controls;

namespace MiHome.Views;

/// <summary>
/// 设备详情正文（电源/开关/音量/耗材/窗帘/灯光/空调/高级设置/设备信息）。
/// 右侧抽屉与桌面分区的控制弹窗共用这一个组件，避免两份内容各自漂移。
/// 数据挂在 MainViewModel 的 DrawerDevice 上，命令也是主视图模型那一套。
/// </summary>
public partial class DeviceDetailView : UserControl
{
    public DeviceDetailView()
    {
        InitializeComponent();
    }
}
