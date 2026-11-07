using System;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia.Threading;

namespace MiHome.Services;

/// <summary>
/// 单实例：命名互斥体保证只跑一个常驻进程。
/// 重复启动时唤起已有实例的主界面（托盘里收着也能弹出来），然后自己直接退出。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SingleInstance
{
    private const string MutexName = @"Local\MiHome.SingleInstance";
    private const string ShowEventName = @"Local\MiHome.ShowMain";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static RegisteredWaitHandle? _wait;

    /// <summary>抢占互斥体并备好「弹主界面」事件。返回 false 表示已有实例在跑。</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out var acquired);
        if (!acquired) return false;
        // 句柄必须留着：被 GC 终结化会把命名事件对象一并销毁，第二个实例就通知不到了
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out _);
        return true;
    }

    /// <summary>第二个实例：通知已驻留进程弹主界面。</summary>
    public static void SignalShowMain()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(ShowEventName);
            evt.Set();
        }
        catch
        {
            // 首个实例还没就绪或打开失败：静默退出
        }
    }

    /// <summary>首个实例：线程池常驻等信号，收到后回到 UI 线程弹主界面。</summary>
    public static void BeginWatch(Action onShowMain)
    {
        var evt = _showEvent ?? EventWaitHandle.OpenExisting(ShowEventName);
        _wait = ThreadPool.RegisterWaitForSingleObject(evt,
            (_, _) => Dispatcher.UIThread.Post(onShowMain),
            null, -1, executeOnlyOnce: false);
    }

    /// <summary>托盘「重启」用：先放掉互斥体再拉新进程，否则新进程会被本进程挡住直接退出。</summary>
    public static void Release()
    {
        _wait?.Unregister(null);
        _wait = null;
        try { _mutex?.ReleaseMutex(); } catch { /* 非拥有线程调用会抛，忽略 */ }
        _mutex?.Dispose();
        _mutex = null;
    }
}
