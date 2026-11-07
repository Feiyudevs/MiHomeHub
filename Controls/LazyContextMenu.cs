using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;

namespace MiHome.Controls;

/// <summary>
/// 延迟创建 ContextMenu：平时不实例化菜单对象，右键、菜单键或「更多」点击时才构建。
/// 设备卡片有 200+ 张时，每张静态内嵌一个 ContextMenu 对象（含 MenuItem + Template + Path）
/// 会常驻大量冗余菜单实例；改成右键时才创建后，只有用户实际右键的那几张才会分配菜单内存。
///
/// 用法：
/// <code>
/// &lt;Button ctl:LazyContextMenu.MenuTemplate="{StaticResource MyMenuTemplate}" .../&gt;
/// </code>
/// 其中 MyMenuTemplate 是一个 DataTemplate，根元素为 ContextMenu。
/// </summary>
public static class LazyContextMenu
{
    public static readonly AttachedProperty<IDataTemplate?> MenuTemplateProperty =
        AvaloniaProperty.RegisterAttached<object, Control, IDataTemplate?>("MenuTemplate");

    private static readonly AttachedProperty<ContextMenu?> CreatedMenuProperty =
        AvaloniaProperty.RegisterAttached<object, Control, ContextMenu?>("CreatedMenu");

    static LazyContextMenu()
    {
        // XAML 可直接设置 AvaloniaProperty，不保证调用 CLR setter，订阅必须跟随属性变更。
        MenuTemplateProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            control.RemoveHandler(Control.ContextRequestedEvent, OnContextRequested);
            control.DataContextChanged -= OnDataContextChanged;
            ClearCreatedMenu(control);
            if (change.GetNewValue<IDataTemplate?>() != null)
            {
                control.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
                control.DataContextChanged += OnDataContextChanged;
            }
        });
    }

    public static void SetMenuTemplate(Control element, IDataTemplate? value) =>
        element.SetValue(MenuTemplateProperty, value);

    public static IDataTemplate? GetMenuTemplate(Control element) => element.GetValue(MenuTemplateProperty);

    /// <summary>所有打开入口共用；挂到宿主后，Popup 保留通向 Window 的逻辑父链。</summary>
    public static ContextMenu? GetOrCreateMenu(Control host)
    {
        if (host.ContextMenu is { } existing)
        {
            if (ReferenceEquals(existing, host.GetValue(CreatedMenuProperty)))
                existing.DataContext = host.DataContext;
            return existing;
        }

        if (GetMenuTemplate(host)?.Build(host.DataContext) is not ContextMenu menu) return null;
        menu.DataContext = host.DataContext;
        host.SetValue(CreatedMenuProperty, menu);
        host.ContextMenu = menu;
        return menu;
    }

    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control || e.Handled) return;
        if (GetOrCreateMenu(control) is not { } menu) return;
        // 键盘请求没有指针位置，贴着卡片打开，避免菜单出现在旧鼠标位置。
        var placement = menu.Placement;
        try
        {
            if (!e.TryGetPosition(control, out _))
                menu.SetCurrentValue(ContextMenu.PlacementProperty, PlacementMode.Bottom);
            menu.Open(control);
        }
        finally
        {
            menu.SetCurrentValue(ContextMenu.PlacementProperty, placement);
        }
        e.Handled = true;
    }

    private static void ClearCreatedMenu(Control control)
    {
        if (control.GetValue(CreatedMenuProperty) is not { } menu) return;
        menu.Close();
        if (ReferenceEquals(control.ContextMenu, menu)) control.ContextMenu = null;
        menu.DataContext = null;
        control.ClearValue(CreatedMenuProperty);
    }

    private static void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (sender is Control control) ClearCreatedMenu(control);
    }
}
