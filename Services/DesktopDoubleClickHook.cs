using System;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;

namespace MiHome.Services;

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

    /// <summary>在 UI 线程安装（WH_MOUSE_LL 的回调也投递到这条线程的消息循环）。</summary>
    public static void Install()
    {
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

        var root = DesktopNative.GetAncestor(hit, DesktopNative.GA_ROOT);
        var cls = ClassName(root == nint.Zero ? hit : root);
        if (cls is not ("Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32"))
            return false;

        // 桌面 ListView 铺满整块屏，图标与空白都会命中 SysListView32：
        // 用 LVM_HITTEST 问一下条目，命中图标就不算「空白」。
        if (string.Equals(cls, "SysListView32", StringComparison.Ordinal))
            return !HitTestDesktopItem(hit, pt);

        return true;
    }

    private static string ClassName(nint h)
    {
        var sb = new StringBuilder(64);
        return DesktopNative.GetClassNameW(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>
    /// 跨进程 LVM_HITTEST：指针参数必须落在 Explorer 的地址空间里，
    /// 否则 comctl32 会往本进程地址写，轻则拿不到结果、重则让 Explorer 崩掉。
    /// </summary>
    private static bool HitTestDesktopItem(nint hListView, DesktopNative.POINT screenPt)
    {
        var pid = 0u;
        DesktopNative.GetWindowThreadProcessId(hListView, out pid);
        if (pid == 0) return false;

        var proc = DesktopNative.OpenProcess(
            DesktopNative.PROCESS_VM_OPERATION | DesktopNative.PROCESS_VM_READ | DesktopNative.PROCESS_VM_WRITE,
            false, pid);
        if (proc == nint.Zero) return false;

        var size = Marshal.SizeOf<DesktopNative.LVHITTESTINFO>();
        var remote = DesktopNative.VirtualAllocEx(proc, nint.Zero, (nuint)size,
            DesktopNative.MEM_COMMIT | DesktopNative.MEM_RESERVE, DesktopNative.PAGE_READWRITE);
        var local = Marshal.AllocHGlobal(size);
        try
        {
            if (remote == nint.Zero) return false;

            var client = screenPt;
            DesktopNative.MapWindowPoints(nint.Zero, hListView, ref client, 1);

            var info = new DesktopNative.LVHITTESTINFO { pt = client, flags = 0, iItem = -1 };
            Marshal.StructureToPtr(info, local, false);
            if (!DesktopNative.WriteProcessMemory(proc, remote, local, (nuint)size, out _))
                return false;

            if (DesktopNative.SendMessageTimeoutW(hListView, DesktopNative.LVM_HITTEST, nint.Zero, remote,
                    DesktopNative.SMTO_ABORTIFHUNG, 50, out _) == nint.Zero)
                return false;

            if (!DesktopNative.ReadProcessMemory(proc, remote, local, (nuint)size, out _))
                return false;

            info = Marshal.PtrToStructure<DesktopNative.LVHITTESTINFO>(local);
            return (info.flags & DesktopNative.LVHT_ONITEM) != 0 && info.iItem >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(local);
            if (remote != nint.Zero) DesktopNative.VirtualFreeEx(proc, remote, 0, DesktopNative.MEM_RELEASE);
            DesktopNative.CloseHandle(proc);
        }
    }
}
