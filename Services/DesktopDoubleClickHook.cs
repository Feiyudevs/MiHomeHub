// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using MiHomeHub.Services.MacOS;

namespace MiHomeHub.Services;

/// <summary>
/// 全局鼠标钩子：识别「双击桌面空白处」，用来显示 / 隐藏所有桌面分区卡片。
/// 钩子回调里只做命中判定，真正的显隐切换 Post 回 UI 线程执行；
/// 判定为桌面图标（SysListView32 命中到条目）或本程序桌面卡片时不触发。
/// </summary>
internal static class DesktopDoubleClickHook
{
    private static nint _hook;
    private static DesktopNative.LowLevelMouseProc? _proc;
    private static long _lastDownMs;
    private static int _lastDownX;
    private static int _lastDownY;
    private static long _lastFireMs;

    /// <summary>
    /// 在 UI 线程安装（WH_MOUSE_LL 的回调也投递到这条线程的消息循环）。
    /// **启动早期不要调用**：低层鼠标钩子会让全系统的鼠标事件先等本进程处理，
    /// 启动那几秒 UI 线程还没忙完，钩子早挂就等于整机鼠标卡顿；由 App 在界面空闲后调用。
    /// </summary>
    public static void Install()
    {
        // macOS 没有 WH_MOUSE_LL 这套全局钩子，换 CGEventTap 自己判双击
        if (OperatingSystem.IsMacOS())
        {
            InstallMacOs();
            return;
        }

        if (_hook != nint.Zero) return;
        try
        {
            _proc = OnMouse;
            _hook = DesktopNative.SetWindowsHookExW(DesktopNative.WH_MOUSE_LL, _proc, nint.Zero, 0);
        }
        catch
        {
            // 钩子装不上不影响程序其余功能，只是双击桌面不生效
            _hook = nint.Zero;
        }

        DesktopLog.Write($"desktop mouse hook install → {(_hook == nint.Zero ? "failed" : "ok")}");
    }

    /// <summary>
    /// macOS：不再监听双击 —— 改用 Carbon 全局快捷键（见 <c>App.InstallDesktopShortcut</c>）。
    /// 原因：CGEventTap 需要「辅助功能」授权，自签分发时授权会被系统重置；Carbon 热键免授权。
    /// </summary>
    private static void InstallMacOs()
    {
        DesktopLog.Write("desktop mac double-click watcher → disabled (改用 Carbon 全局快捷键，免辅助功能权限)");
    }

    private static nint OnMouse(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && AppSettingsStore.DoubleClickDesktopToggles)
            {
                var data = Marshal.PtrToStructure<DesktopNative.MSLLHOOKSTRUCT>(lParam);
                var msg = (int)wParam;
                if (msg == DesktopNative.WM_LBUTTONDBLCLK)
                {
                    if (IsDesktopBlank(data.pt)) TryFire();
                    _lastDownMs = 0;
                }
                else if (msg == DesktopNative.WM_LBUTTONDOWN)
                {
                    if (IsDesktopBlank(data.pt))
                    {
                        long now = Environment.TickCount64;
                        uint maxMs = DesktopNative.GetDoubleClickTime();
                        if (maxMs == 0) maxMs = 500;
                        int rx = Math.Max(DesktopNative.GetSystemMetrics(DesktopNative.SM_CXDOUBLECLK), 32);
                        int ry = Math.Max(DesktopNative.GetSystemMetrics(DesktopNative.SM_CYDOUBLECLK), 32);
                        bool second = _lastDownMs != 0
                                      && now - _lastDownMs <= maxMs
                                      && Math.Abs(data.pt.X - _lastDownX) <= rx
                                      && Math.Abs(data.pt.Y - _lastDownY) <= ry;
                        if (second)
                        {
                            TryFire();
                        }
                        else
                        {
                            _lastDownMs = now;
                            _lastDownX = data.pt.X;
                            _lastDownY = data.pt.Y;
                        }
                    }
                    else
                    {
                        _lastDownMs = 0;
                    }
                }
            }
        }
        catch
        {
            // 钩子回调里绝不能抛异常，否则系统会静默卸载钩子
        }

        return DesktopNative.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>系统 DBLCLK 与第二次 DOWN 可能同时到达，350ms 内只切一次。</summary>
    private static void TryFire()
    {
        long now = Environment.TickCount64;
        if (now - _lastFireMs < 350) return;
        _lastFireMs = now;
        _lastDownMs = 0;
        Dispatcher.UIThread.Post(DesktopHost.ToggleVisibilityFromDesktop, DispatcherPriority.Input);
    }

    /// <summary>光标是否落在真正的桌面空白处（排除本程序卡片、桌面图标和其它软件的窗口）。</summary>
    private static bool IsDesktopBlank(DesktopNative.POINT pt)
    {
        if (DesktopHost.IsPointOnDesktopWidget(pt.X, pt.Y)) return false;

        var hit = DesktopNative.WindowFromPoint(pt);
        if (hit == nint.Zero) return false;

        // 先确认这块屏幕归桌面外壳（Progman / WorkerW）所有，
        // 别的程序窗口（含资源管理器文件夹窗口）一律不算桌面空白。
        var root = DesktopNative.GetAncestor(hit, DesktopNative.GA_ROOT);
        var rootCls = ClassName(root == nint.Zero ? hit : root);
        if (rootCls is not ("Progman" or "WorkerW" or "SHELLDLL_DefView"))
            return false;

        // 桌面图标都画在 SysListView32 里，这块列表铺满整屏：
        // 命中列表本身时要问一下条目，命中图标就不算空白。
        var list = FindDesktopListView(hit);
        if (list == nint.Zero) return true;
        return HitTestDesktopItem(list, pt) == IconHit.Blank;
    }

    /// <summary>从命中窗口往上找桌面图标列表（SysListView32）；命中窗口本身就是列表时直接返回它。</summary>
    private static nint FindDesktopListView(nint h)
    {
        for (var i = 0; i < 8 && h != nint.Zero; i++)
        {
            if (string.Equals(ClassName(h), "SysListView32", StringComparison.Ordinal)) return h;
            h = DesktopNative.GetAncestor(h, DesktopNative.GA_PARENT);
        }
        return nint.Zero;
    }

    private static string ClassName(nint h)
    {
        var sb = new StringBuilder(64);
        return DesktopNative.GetClassNameW(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private enum IconHit
    {
        Blank,
        Icon,
        /// <summary>问不到结果（线程忙 / 超时 / 没权限）：按「不是空白」处理，宁可漏切一次也不误隐藏。</summary>
        Unknown
    }

    /// <summary>
    /// 跨进程 LVM_HITTEST：指针参数必须落在 Explorer 的地址空间里，
    /// 否则 comctl32 会往本进程地址写，轻则拿不到结果、重则让 Explorer 崩掉。
    /// </summary>
    private static IconHit HitTestDesktopItem(nint hListView, DesktopNative.POINT screenPt)
    {
        var pid = 0u;
        DesktopNative.GetWindowThreadProcessId(hListView, out pid);
        if (pid == 0) return IconHit.Unknown;

        var proc = DesktopNative.OpenProcess(
            DesktopNative.PROCESS_VM_OPERATION | DesktopNative.PROCESS_VM_READ | DesktopNative.PROCESS_VM_WRITE,
            false, pid);
        if (proc == nint.Zero) return IconHit.Unknown;

        const int sentinel = -2; // 合法条目号从 0 起，-2 只可能来自「没回填」
        var size = Marshal.SizeOf<DesktopNative.LVHITTESTINFO>();
        var remote = DesktopNative.VirtualAllocEx(proc, nint.Zero, (nuint)size,
            DesktopNative.MEM_COMMIT | DesktopNative.MEM_RESERVE, DesktopNative.PAGE_READWRITE);
        var local = Marshal.AllocHGlobal(size);
        var sent = false;
        try
        {
            if (remote == nint.Zero) return IconHit.Unknown;

            var client = screenPt;
            DesktopNative.MapWindowPoints(nint.Zero, hListView, ref client, 1);

            var info = new DesktopNative.LVHITTESTINFO { pt = client, flags = 0, iItem = sentinel };
            Marshal.StructureToPtr(info, local, false);
            if (!DesktopNative.WriteProcessMemory(proc, remote, local, (nuint)size, out _))
                return IconHit.Unknown;

            // 返回值是命中的条目号，命中第一条（0）时也是 0，
            // 所以只能看回填结果，不能用返回值当成败。
            sent = DesktopNative.SendMessageTimeoutW(hListView, DesktopNative.LVM_HITTEST, nint.Zero, remote,
                DesktopNative.SMTO_ABORTIFHUNG, 50, out _) != nint.Zero;
            if (!sent) return IconHit.Unknown;

            if (!DesktopNative.ReadProcessMemory(proc, remote, local, (nuint)size, out _))
                return IconHit.Unknown;

            info = Marshal.PtrToStructure<DesktopNative.LVHITTESTINFO>(local);
            if (info.iItem == sentinel) return IconHit.Unknown;
            return (info.flags & DesktopNative.LVHT_ONITEM) != 0 && info.iItem >= 0 ? IconHit.Icon : IconHit.Blank;
        }
        catch
        {
            return IconHit.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(local);
            // 超时后 Explorer 可能还在处理这条消息，这时释放远程缓冲会写到已释放的地址：留着，稍后再收。
            if (sent)
            {
                if (remote != nint.Zero) DesktopNative.VirtualFreeEx(proc, remote, 0, DesktopNative.MEM_RELEASE);
                DesktopNative.CloseHandle(proc);
            }
            else
            {
                _orphanRemotes.Add((proc, remote, Environment.TickCount64));
            }
            ReleaseOrphans();
        }
    }

    /// <summary>超时那几次分出去的远程缓冲 / 进程句柄，等 Explorer 处理完消息后再回收（UI 线程独占，无需加锁）。</summary>
    private static readonly System.Collections.Generic.List<(nint Proc, nint Remote, long AtMs)> _orphanRemotes = new();

    private static void ReleaseOrphans()
    {
        long now = Environment.TickCount64;
        for (var i = _orphanRemotes.Count - 1; i >= 0; i--)
        {
            var orphan = _orphanRemotes[i];
            if (now - orphan.AtMs < 2000) continue;
            _orphanRemotes.RemoveAt(i);
            if (orphan.Remote != nint.Zero)
                DesktopNative.VirtualFreeEx(orphan.Proc, orphan.Remote, 0, DesktopNative.MEM_RELEASE);
            if (orphan.Proc != nint.Zero) DesktopNative.CloseHandle(orphan.Proc);
        }
    }
}
