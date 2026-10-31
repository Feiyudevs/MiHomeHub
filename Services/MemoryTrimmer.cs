using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MiHome.Services;

/// <summary>
/// 内存整理工具：在窗口收进托盘等空闲时机回收托管内存并收缩工作集，
/// 对常驻托盘类应用可有效降低任务管理器中的常驻内存占用。
/// 参考 Feido 项目「贴底隐藏时的内存优化」实现。
/// </summary>
public static class MemoryTrimmer
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static DateTimeOffset _lastTrim = DateTimeOffset.MinValue;

    /// <summary>
    /// 回收内存：压缩 LOH、执行完整 GC，并请示操作系统收缩工作集。
    /// 内置 10 秒节流，避免频繁触发。
    /// </summary>
    public static void Trim()
    {
        // 节流：10 秒内只执行一次
        if ((DateTimeOffset.UtcNow - _lastTrim).TotalSeconds < 10) return;
        DoTrim();
    }

    /// <summary>
    /// 强制回收内存：忽略节流限制，用于窗口隐藏等关键时机。
    /// 在后台线程异步执行，不阻塞调用方（如关闭/收起窗口）。
    /// </summary>
    public static void TrimForce()
    {
        DoTrim(blocking: false);
    }

    /// <summary>
    /// 阻塞式强制回收：GC 同步完成后再返回，用于启动隐藏等 UI 不可见的场景。
    /// </summary>
    public static void TrimBlocking()
    {
        DoTrim(blocking: true);
    }

    /// <summary>
    /// 延迟整理内存：等待 UI 渲染/数据加载稳定后再回收，避免收缩工作集卡掉动画帧。
    /// </summary>
    public static void TrimDelayed(int delayMilliseconds)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMilliseconds);
                Trim();
            }
            catch
            {
                // 后台任务，忽略异常
            }
        });
    }

    private static void DoTrim(bool blocking = false)
    {
        _lastTrim = DateTimeOffset.UtcNow;

        // 在后台线程执行，避免阻塞 UI（阻塞式除外，由调用方确保处于空闲态）
        if (!blocking)
        {
            Task.Run(() => RunTrim(blocking: false));
            return;
        }
        RunTrim(blocking: true);
    }

    private static void RunTrim(bool blocking)
    {
        try
        {
            Console.WriteLine($"[MemoryTrimmer] 开始回收 blocking={blocking}");

            // 第一轮：压缩 LOH + 完整 GC
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: blocking, compacting: true);
            GC.WaitForPendingFinalizers();

            // 第二轮：回收 Finalizer 释放的对象，再次压缩 LOH
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: blocking, compacting: true);
            GC.WaitForPendingFinalizers();

            // 让 Windows 回收物理内存页（任务管理器中立即下降）
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));

            var process = System.Diagnostics.Process.GetCurrentProcess();
            var workingSetMb = process.WorkingSet64 / 1024 / 1024;
            var privateMemoryMb = process.PrivateMemorySize64 / 1024 / 1024;
            Console.WriteLine(
                $"[MemoryTrimmer] 回收完成, 托管堆={GC.GetTotalMemory(false) / 1024 / 1024}MB, " +
                $"工作集={workingSetMb}MB, 私有内存={privateMemoryMb}MB");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MemoryTrimmer] 内存整理失败: {ex.Message}");
        }
    }
}
