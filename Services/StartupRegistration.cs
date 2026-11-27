// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Text;
using MiHomeHub.Services.MacOS;

namespace MiHomeHub.Services;

/// <summary>
/// 开机启动 = 当前用户 Run 键（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）；
/// macOS 上对应当前用户的 LaunchAgent（~/Library/LaunchAgents/com.mihomehub.app.plist）。
/// Windows 侧直接调 advapi32，避免为注册表再引一个 NuGet 包（net10.0 非 windows TFM 不带 Registry）。
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MiHomeHub";

    private static readonly IntPtr HKeyCurrentUser = new(unchecked((int)0x80000001));
    private const int KeyQueryValue = 0x0001;
    private const int KeySetValue = 0x0002;
    private const int RegSz = 1;
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;

    private static string? Command => Environment.ProcessPath is { } exe ? $"\"{exe}\"" : null;

    /// <summary>macOS 用的可执行文件路径：LaunchAgent 的 plist 里是参数数组，不能带引号。</summary>
    private static string? ExecutablePath => Environment.ProcessPath;

    public static bool IsEnabled()
    {
        // macOS：开机启动 = LaunchAgent（~/Library/LaunchAgents/com.mihomehub.app.plist）
        if (OperatingSystem.IsMacOS())
            return ExecutablePath is { } exe && MacOsLaunchAgent.IsEnabled(exe);
        return Command is { } cmd && string.Equals(Read(), cmd, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>写入 / 删除启动项，返回是否成功。</summary>
    public static bool Apply(bool enabled)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (ExecutablePath is not { } exe) return false;
            var ok = MacOsLaunchAgent.Apply(exe, enabled);
            _lastError = MacOsLaunchAgent.LastError;
            return ok;
        }
        var cmd = Command;
        if (cmd is null) return false;
        return enabled ? Write(cmd) : Delete();
    }

    /// <summary>启动时对齐一次：升级或换目录后旧的 exe 路径会失效。</summary>
    /// <remarks>
    /// 这一步是启动期做的事，**任何异常都不能带崩应用**：
    /// 以前 macOS 上写不进 plist 时 UnauthorizedAccessException 会一路抛到
    /// App.OnFrameworkInitializationCompleted，进程 SIGABRT，用户连窗口都看不到。
    /// 现在这里兜住异常并记进 <see cref="LastError"/>，界面如实显示原因。
    /// </remarks>
    public static void Sync()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (ExecutablePath is not { } exe) return;
                MacOsLaunchAgent.Sync(exe, AppSettingsStore.AutoStart);
                _lastError = MacOsLaunchAgent.LastError;
                return;
            }
            if (!AppSettingsStore.AutoStart) return;
            var cmd = Command;
            if (cmd is null || string.Equals(Read(), cmd, StringComparison.OrdinalIgnoreCase)) return;
            Write(cmd);
        }
        catch (Exception ex)
        {
            _lastError = $"开机启动项对齐失败（{ex.Message}）";
            DesktopLog.Write($"[startup] 启动项对齐失败（不影响应用启动）：{_lastError}");
        }
    }

    /// <summary>最近一次开机启动注册失败的原因（成功时为空）。</summary>
    public static string? LastError => _lastError;

    private static string? _lastError;

    private static string? Read()
    {
        if (RegOpenKeyExW(HKeyCurrentUser, RunKey, 0, KeyQueryValue, out var key) != ErrorSuccess) return null;
        try
        {
            uint size = 0;
            if (RegQueryValueExW(key, ValueName, IntPtr.Zero, out _, null, ref size) != ErrorSuccess
                || size < 4) return null;

            var buf = new byte[size];
            if (RegQueryValueExW(key, ValueName, IntPtr.Zero, out _, buf, ref size) != ErrorSuccess) return null;
            return Encoding.Unicode.GetString(buf, 0, Math.Max(0, (int)size - 2)); // 去掉结尾 \0
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static bool Write(string value)
    {
        if (RegCreateKeyExW(HKeyCurrentUser, RunKey, 0, null, 0, KeySetValue, IntPtr.Zero,
                out var key, out _) != ErrorSuccess) return false;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(value + "\0");
            return RegSetValueExW(key, ValueName, 0, RegSz, bytes, (uint)bytes.Length) == ErrorSuccess;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static bool Delete()
    {
        if (RegOpenKeyExW(HKeyCurrentUser, RunKey, 0, KeySetValue, out var key) != ErrorSuccess) return true;
        try
        {
            var rc = RegDeleteValueW(key, ValueName);
            return rc == ErrorSuccess || rc == ErrorFileNotFound;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, int ulOptions, int samDesired,
        out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegCreateKeyExW(IntPtr hKey, string lpSubKey, int reserved, string? lpClass,
        int dwOptions, int samDesired, IntPtr lpSecurityAttributes, out IntPtr phkResult, out int lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved,
        out int lpType, byte[]? lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegSetValueExW(IntPtr hKey, string lpValueName, int reserved, int dwType,
        byte[] lpData, uint cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegDeleteValueW(IntPtr hKey, string lpValueName);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegCloseKey(IntPtr hKey);
}
