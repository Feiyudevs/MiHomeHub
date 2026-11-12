// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows.Input;
using Avalonia.Media;

namespace MiHome.ViewModels;

/// <summary>
/// 设备卡的宿主契约：主窗口列表与桌面分区共用同一张设备卡（DeviceCardTemplate），
/// 卡片不关心自己贴在哪，只把动作交给所在宿主实现：
/// 主窗口点卡片展开详情抽屉，桌面分区点卡片弹出控制弹窗。
/// 右键菜单同样由宿主给（文案 / 图标 / 动作），主窗口是「添加到桌面组件」，分区是「从分区移除」。
/// </summary>
public interface IDeviceCardHost
{
    /// <summary>点整张卡片的动作，参数为设备。</summary>
    ICommand OpenCardCommand { get; }

    /// <summary>拨卡片右侧开关的动作，参数为设备。</summary>
    ICommand ToggleSwitchCommand { get; }

    /// <summary>卡片右键菜单的动作，参数为设备；为 null 时不显示右键菜单。</summary>
    ICommand? CardMenuCommand { get; }

    /// <summary>卡片右键菜单的文案：「添加到桌面组件」/「从分区移除」。</summary>
    string CardMenuText { get; }

    /// <summary>卡片右键菜单的图标（24 网格线性几何），取 <see cref="CardMenuIcons"/> 里的常量。</summary>
    Geometry CardMenuIcon { get; }
}
