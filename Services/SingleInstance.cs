// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Avalonia.Threading;

namespace MiHomeHub.Services;

/// <summary>
/// 单实例：保证只跑一个常驻进程。
/// 重复启动时唤起已有实例的主界面（托盘里收着也能弹出来），然后自己直接退出。
///
/// Windows 用命名互斥体 + 命名事件；macOS 的 .NET 不支持命名同步对象
/// （EventWaitHandle 在非 Windows 上创建命名对象会抛 PlatformNotSupportedException），
/// 改用「独占锁文件 + 信号文件轮询」实现同样的两件事。
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static RegisteredWaitHandle? _wait;

    // ---- Windows：命名内核对象 ----
    // "Local\" 是 Windows 的命名对象作用域前缀；macOS 不走这条路，不需要。
    private static readonly string MutexName = @"Local\MiHomeHub.SingleInstance";
    private static readonly string ShowEventName = @"Local\MiHomeHub.ShowMain";

    // ---- macOS：文件锁 + 信号文件 ----
    private static FileStream? _lockFile;
    private static Timer? _signalTimer;
    private static DateTime _lastSignalWrite = DateTime.MinValue;
    private static readonly object SignalLock = new();

    /// <summary>本进程启动时刻（类加载时取，尽量贴近进程真正的起点）。</summary>
    private static readonly DateTime ProcessStartUtc = DateTime.UtcNow;

    /// <summary>抢占互斥体并备好「弹主界面」事件。返回 false 表示已有实例在跑。</summary>
    public static bool TryAcquire()
    {
        if (!OperatingSystem.IsWindows()) return TryAcquireMac();

        _mutex = new Mutex(true, MutexName, out var acquired);
        if (!acquired) return false;
        // 句柄必须留着：被 GC 终结化会把命名事件对象一并销毁，第二个实例就通知不到了
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out _);
        return true;
    }

    /// <summary>第二个实例：通知已驻留进程弹主界面。</summary>
    public static void SignalShowMain()
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Dir);
                lock (SignalLock)
                {
                    File.WriteAllText(MacSignalPath, DateTime.UtcNow.Ticks.ToString());
                    _lastSignalWrite = DateTime.UtcNow;
                }
            }
            catch
            {
                // 首个实例还没就绪或写不进去：静默退出
            }
            return;
        }

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
        if (!OperatingSystem.IsWindows())
        {
            lock (SignalLock) _lastSignalWrite = ReadSignalWrite();
            _signalTimer = new Timer(_ =>
            {
                DateTime write;
                lock (SignalLock) write = ReadSignalWrite();
                if (write == _lastSignalWrite) return;
                _lastSignalWrite = write;
                Dispatcher.UIThread.Post(onShowMain);
            }, null, 500, 500);

            // 竞态兜底：第二个实例可能在我们装上监听器之前就写了信号文件
            // （用户双击 app、第一个实例还在启动），那时定时器看不到变化，窗口就永远不弹。
            // 这里补一刀：信号比本进程启动还新 = 有人在我们启动期间叫过我们 → 立刻弹主界面。
            var pending = ReadSignalWrite();
            if (pending > ProcessStartUtc)
            {
                DesktopLog.Write("single-instance: signal written during startup → show main window");
                Dispatcher.UIThread.Post(onShowMain);
            }
            return;
        }

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
        _signalTimer?.Dispose();
        _signalTimer = null;
        try { _mutex?.ReleaseMutex(); } catch { /* 非拥有线程调用会抛，忽略 */ }
        _mutex?.Dispose();
        _mutex = null;

        if (_lockFile != null)
        {
            try { _lockFile.Dispose(); } catch { /* 忽略 */ }
            _lockFile = null;
            try
            {
                if (File.Exists(MacLockPath)) File.Delete(MacLockPath);
            }
            catch
            {
                // 锁文件删不掉也不影响：下次启动时独占打开失败才会误判，先按正常退出处理
            }
        }
    }

    // ================================================================ macOS 实现

    private static string MacLockPath => Path.Combine(AppPaths.Dir, "single-instance.lock");
    private static string MacSignalPath => Path.Combine(AppPaths.Dir, "single-instance.signal");

    /// <summary>
    /// 独占打开锁文件即拿到「单实例」身份：FileShare.None 意味着别的进程再开同一个文件会失败。
    /// 进程退出时句柄自动释放，锁随之失效，不需要额外的清理逻辑。
    /// </summary>
    private static bool TryAcquireMac()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Dir);
            _lockFile = new FileStream(MacLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // 已被别的实例独占：说明确实在跑
            _lockFile = null;
            return false;
        }
        catch
        {
            // 目录不可写等异常：宁可放行，也不要把应用锁死启动不了
            _lockFile = null;
            return true;
        }
    }

    private static DateTime ReadSignalWrite()
    {
        try
        {
            if (!File.Exists(MacSignalPath)) return DateTime.MinValue;
            return File.GetLastWriteTimeUtc(MacSignalPath);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
