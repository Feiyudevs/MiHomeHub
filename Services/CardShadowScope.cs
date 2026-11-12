// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Media;

namespace MiHome.Services;

/// <summary>
/// 卡片投影的运行期开关：卡片样式统一引用 CardShadow / CardShadowHover 两个资源，
/// 这里整体替换资源值即可让所有窗口（主界面、设置、抽屉、桌面组件）同步生效。
/// </summary>
public static class CardShadowScope
{
    public const string Key = "CardShadow";
    public const string HoverKey = "CardShadowHover";

    private static readonly BoxShadows Normal =
        BoxShadows.Parse("0 1 2 0 #050F172A, 0 5 16 0 #080F172A");

    private static readonly BoxShadows Hover =
        BoxShadows.Parse("0 3 8 0 #0D0F172A, 0 10 24 0 #140F172A");

    /// <summary>关闭时用空投影，而不是把控件上的 BoxShadow 置空（动态资源替换后仍可回切）。</summary>
    private static readonly BoxShadows None = default;

    public static void Apply(bool show)
    {
        if (Application.Current is not { } app) return;
        app.Resources[Key] = show ? Normal : None;
        app.Resources[HoverKey] = show ? Hover : None;
    }
}
