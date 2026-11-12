// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia.Media;

namespace MiHome.ViewModels;

/// <summary>
/// 设备卡右键菜单的图标（24 网格线性图标，与 Themes/Icons.axaml 的 GeoPlus / GeoMin 同形状）。
/// 由宿主按动作挑一个交给 XAML：这样「＋ / －」只用一个 Path 绑 Data，
/// 不用在模板里写两条路径 + 互斥的可见性开关。
/// </summary>
internal static class CardMenuIcons
{
    /// <summary>＋：加入动作（添加到桌面组件）。</summary>
    public static readonly Geometry Add = StreamGeometry.Parse("M12,5 V19 M5,12 H19");

    /// <summary>－：移除动作（从分区移除）。</summary>
    public static readonly Geometry Remove = StreamGeometry.Parse("M5,12 H19");
}
