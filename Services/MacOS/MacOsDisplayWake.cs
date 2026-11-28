// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// macOS 显示器唤醒助手。
///
/// Avalonia.Native 的渲染计时器由 CVDisplayLinkCreateWithActiveCGDisplays 驱动，
/// 而这个 API 只在「存在活动显示器」时才成功：显示器进睡眠/待机时它返回 -6661，
/// Avalonia 平台初始化会直接抛
/// “Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661”，
/// 应用在窗口还没创建之前就退出——用户看到的就是「双击 app 完全没有反应，
/// 也没有任何报错」，连菜单栏图标都没建出来，自然也没法右键退出。
///
/// 这里在初始化 Avalonia 之前先向系统声明一次「用户正在使用本机」，
/// 系统会顺手把显示器唤醒，CVDisplayLink 随后就能建起来。
/// 断言只保留十几秒（覆盖启动 + 首帧），随后释放，避免把屏幕一直点亮。
/// </summary>
internal static class MacOsDisplayWake
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>kIOPMAssertionTypeUserActive。</summary>
    private const uint AssertionTypeUserActive = 0x00000020;

    private const uint Utf8 = 0x08000100;

    [DllImport(IOKit, EntryPoint = "IOPMAssertionDeclareUserActivity")]
    private static extern int DeclareUserActivity(IntPtr reason, uint assertionType, out uint assertionId);

    [DllImport(IOKit, EntryPoint = "IOPMAssertionRelease")]
    private static extern int ReleaseAssertion(uint assertionId);

    [DllImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, byte[] cString, uint encoding);

    [DllImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static extern void CFRelease(IntPtr handle);

    private static uint _assertionId;

    /// <summary>声明一次用户活动，把显示器唤醒。返回是否拿到断言。</summary>
    public static bool BeginStartupWake()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (_assertionId != 0) return true;
        try
        {
            var bytes = Encoding.UTF8.GetBytes("MiHomeHub 正在启动界面\0");
            var reason = CFStringCreateWithCString(IntPtr.Zero, bytes, Utf8);
            if (reason == IntPtr.Zero) return false;
            try
            {
                if (DeclareUserActivity(reason, AssertionTypeUserActive, out var id) != 0 || id == 0) return false;
                _assertionId = id;
                return true;
            }
            finally
            {
                CFRelease(reason);
            }
        }
        catch (Exception)
        {
            // 老系统没这个符号就算了：最坏情况还是渲染计时器起不来，由重试兜底。
            return false;
        }
    }

    /// <summary>释放唤醒断言，让系统回到正常的休眠策略。</summary>
    public static void EndStartupWake()
    {
        var id = _assertionId;
        if (id == 0) return;
        _assertionId = 0;
        try
        {
            ReleaseAssertion(id);
        }
        catch (Exception)
        {
            // 释放失败无所谓，进程退出后断言自动失效。
        }
    }
}
