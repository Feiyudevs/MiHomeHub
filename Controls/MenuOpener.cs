using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MiHome.Controls;

/// <summary>
/// 设备卡右上角「更多」按钮的行为：点击时打开所在设备卡的 ContextMenu（与右键同一份菜单）。
/// 卡片模板在 Themes/Templates.axaml 里、没有代码后置，所以用附加属性接管点击：
/// 用法 <c>ctl:MenuOpener.OpenOnClick="True"</c>，点击时沿视觉树向上找第一个挂了
/// ContextMenu 的控件（即设备卡 Button 本身）并把菜单钉在它上面打开。
/// </summary>
public static class MenuOpener
{
    public static readonly AttachedProperty<bool> OpenOnClickProperty =
        AvaloniaProperty.RegisterAttached<object, Button, bool>("OpenOnClick");

    /// <summary>XAML 里设值即挂接 Click（松开即解挂），不需要监听属性变更。</summary>
    public static void SetOpenOnClick(Button element, bool value)
    {
        element.SetValue(OpenOnClickProperty, value);
        if (value) element.Click += OnClick;
        else element.Click -= OnClick;
    }

    public static bool GetOpenOnClick(Button element) => element.GetValue(OpenOnClickProperty);

    private static void OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control origin) return;

        // 先看自己，再沿视觉树向上找第一个挂了右键菜单的祖先（设备卡）。
        // 菜单默认 Placement=Pointer，打开位置就是当前指针位置（「更多」按钮处）。
        var host = origin.ContextMenu as ContextMenu ?? origin.GetVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(ancestor => ancestor.ContextMenu is ContextMenu);

        if (host?.ContextMenu is not ContextMenu menu) return;

        if (menu.IsOpen) menu.Close();
        else menu.Open(host);
        e.Handled = true;
    }
}
