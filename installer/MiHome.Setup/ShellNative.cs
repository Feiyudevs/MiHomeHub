// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MiHome.Setup;

/// <summary>
/// Win32 进程启动：CREATE_SUSPENDED 建进程 → 允许前台 → ResumeThread。
/// 避免安装窗口以「后台启动」姿态出现（不闪任务栏、不抢焦点失败）。
/// </summary>
internal static class ShellNative
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_TIMEOUT = 0x00000102;

    /// <summary>由 CreateProcess 打开的子进程句柄（调用方负责 Close）。</summary>
    internal readonly struct StartedProcess
    {
        public int Pid { get; init; }
        public IntPtr Handle { get; init; }
    }

    /// <summary>挂起启动并立即恢复；返回子进程 PID 与进程句柄。</summary>
    public static StartedProcess StartSuspended(string exePath, string arguments, string workingDirectory)
    {
        var si = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            lpDesktop = null,
        };
        var commandLine = new StringBuilder(Quote(exePath) + " " + arguments);
        ProcessInformation pi;

        var ok = CreateProcessW(
            exePath,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
            IntPtr.Zero,
            workingDirectory,
            ref si,
            out pi);

        if (!ok)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动安装界面进程");

        try
        {
            // 父进程（安装器）把自己的前台权限让给子进程
            try { AllowSetForegroundWindow(pi.dwProcessId); } catch { }
            var resumed = ResumeThread(pi.hThread);
            if (resumed == unchecked((uint)-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "恢复安装界面进程失败");

            return new StartedProcess { Pid = pi.dwProcessId, Handle = pi.hProcess };
        }
        finally
        {
            CloseHandle(pi.hThread);
        }
    }

    /// <summary>等待子进程结束并取退出码；超时返回 null（调用方自行 Terminate）。</summary>
    public static int? WaitForExit(StartedProcess process, int timeoutMs)
    {
        var result = WaitForSingleObject(process.Handle, (uint)timeoutMs);
        if (result == WAIT_TIMEOUT) return null;
        if (result != WAIT_OBJECT_0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "等待安装界面进程失败");
        if (!GetExitCodeProcess(process.Handle, out var code))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读取安装界面退出码失败");
        return unchecked((int)code);
    }

    public static void Terminate(StartedProcess process)
    {
        try { TerminateProcess(process.Handle, 3); } catch { }
    }

    public static void Close(StartedProcess process)
    {
        try { if (process.Handle != IntPtr.Zero) CloseHandle(process.Handle); } catch { }
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            if (ch == '"') sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>错误消息框（外壳无 UI 框架）。</summary>
internal static class ShellMessageBox
{
    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void Error(string message)
        => MessageBoxW(IntPtr.Zero, message, "MiHome · 安装程序", MB_OK | MB_ICONERROR);
}
