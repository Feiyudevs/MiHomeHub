using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MiHome.Services;

/// <summary>
/// 显示器变化通知：插拔屏幕、改分辨率会连着来好几次，合并成稳定后的一次再广播。
/// 事件源挂在常驻窗口的屏幕集合上 —— 主窗收进托盘只是隐藏，进程还在，通知照旧进来。
/// </summary>
internal static class DisplayWatcher
{
    /// <summary>屏幕数量或布局稳定后的变化通知。</summary>
    public static event Action? Changed;

    private const int SettleMs = 500;

    private static Screens? _source;
    private static readonly EventHandler OnScreensChanged = (_, _) => Debounce();
    private static IDisposable? _settle;

    /// <summary>用任一常驻窗口挂上屏幕变化事件；重复调用只会挂一次。</summary>
    public static void Attach(Window anchor)
    {
        var screens = anchor.Screens;
        if (screens == null || ReferenceEquals(_source, screens)) return;

        if (_source != null) _source.Changed -= OnScreensChanged;
        _source = screens;
        screens.Changed += OnScreensChanged;
    }

    /// <summary>
    /// 系统广播的显示变化（WM_DISPLAYCHANGE）也走这里：Avalonia 的屏幕事件是另一条来源，
    /// 两条都接上，拔插屏幕时谁先到都只合并成一次通知。
    /// </summary>
    public static void Notify() => Debounce();

    private static void Debounce()
    {
        _settle?.Dispose();
        _settle = DispatcherTimer.RunOnce(() =>
        {
            _settle = null;
            Changed?.Invoke();
        }, TimeSpan.FromMilliseconds(SettleMs));
    }
}
