// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MiHome.Controls;

/// <summary>
/// 设备卡右上角「更多」按钮的行为：点击时打开所在设备卡的 ContextMenu（与右键同一份菜单）。
/// 卡片模板在 Themes/Templates.axaml 里、没有代码后置，所以用附加属性接管点击：
/// 用法 <c>ctl:MenuOpener.OpenOnClick="True"</c>，点击时沿视觉树向上找第一个挂了
/// ContextMenu 的控件（即设备卡 Button 本身）并把菜单钉在它上面打开。
///
/// v2：从 Button 扩展到任意 Control，支持 Border 等轻量元素作为「更多」触发按钮，
/// 减少每张卡实例化一个完整 Button 控件的开销。
/// </summary>
public static class MenuOpener
{
    public static readonly AttachedProperty<bool> OpenOnClickProperty =
        AvaloniaProperty.RegisterAttached<object, Control, bool>("OpenOnClick");

    static MenuOpener()
    {
        OpenOnClickProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            if (control is Button button)
            {
                button.Click -= OnButtonClick;
                if (change.GetNewValue<bool>()) button.Click += OnButtonClick;
            }
            else
            {
                control.PointerReleased -= OnPointerReleased;
                if (change.GetNewValue<bool>()) control.PointerReleased += OnPointerReleased;
            }
        });
    }

    public static void SetOpenOnClick(Control element, bool value) => element.SetValue(OpenOnClickProperty, value);

    public static bool GetOpenOnClick(Control element) => element.GetValue(OpenOnClickProperty);

    private static void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && OpenMenu(control)) e.Handled = true;
    }

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control origin) return;
        // 只响应左键，右键走 ContextMenu 默认行为
        if (e.InitialPressMouseButton != MouseButton.Left) return;

        if (OpenMenu(origin)) e.Handled = true;
    }

    private static bool OpenMenu(Control origin)
    {
        // 模板尚未实例化菜单时，也能找到卡片宿主。
        var host = HasMenu(origin) ? origin : origin.GetVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(HasMenu);

        if (host == null) return false;
        if (LazyContextMenu.GetOrCreateMenu(host) is not { } menu)
            return false;

        if (menu.IsOpen) menu.Close();
        else menu.Open(host);
        return true;
    }

    private static bool HasMenu(Control control) =>
        control.ContextMenu != null || LazyContextMenu.GetMenuTemplate(control) != null;
}
