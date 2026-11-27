// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// macOS 前台进程判定：Windows 版用 GetForegroundWindow + GetWindowThreadProcessId，
/// macOS 没有等价 API，这里走 AppKit 的 NSWorkspace.frontmostApplication 取前台进程号对比。
/// 只用 objc runtime 的消息发送，不引入任何原生工程。
/// </summary>
internal static class MacOsForeground
{
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_id(nint self, nint sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern int objc_msgSend_int(nint self, nint sel);

    /// <summary>当前进程是否为前台应用（等价 Windows 的 GetForegroundWindow 判定）。</summary>
    public static bool IsForegroundProcess()
    {
        try
        {
            var workspaceClass = objc_getClass("NSWorkspace");
            if (workspaceClass == IntPtr.Zero) return false;

            var shared = objc_msgSend_id(workspaceClass, sel_registerName("sharedWorkspace"));
            if (shared == IntPtr.Zero) return false;

            var app = objc_msgSend_id(shared, sel_registerName("frontmostApplication"));
            if (app == IntPtr.Zero) return false;

            var pid = objc_msgSend_int(app, sel_registerName("processIdentifier"));
            return pid == Environment.ProcessId;
        }
        catch
        {
            // 取不到就按「非前台」处理：统计口径保守，不影响主流程
            return false;
        }
    }
}
