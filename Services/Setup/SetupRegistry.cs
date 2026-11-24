// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MiHome.Services.Setup;

internal sealed record SetupRegistryValue(string Name, int Type, byte[] Data);

/// <summary>
/// 安装器需要的最小注册表封装（HKCU）：直接调 advapi32，和主程序的 StartupRegistration 同一路线，
/// 不额外引入 Microsoft.Win32.Registry。只处理安装/卸载用到的字符串与 DWORD。
/// </summary>
internal static class SetupRegistry
{
    private static readonly IntPtr HKeyCurrentUser = new(unchecked((int)0x80000001));

    private const int KeyRead = 0x20019;
    private const int KeyWrite = 0x20006;
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int RegSz = 1;
    private const int RegExpandSz = 2;
    private const int RegBinary = 3;
    private const int RegDword = 4;

    public static bool KeyExists(string subKey)
    {
        var rc = RegOpenKeyExW(HKeyCurrentUser, subKey, 0, KeyRead, out var key);
        if (rc != ErrorSuccess) return false;
        RegCloseKey(key);
        return true;
    }

    public static string? ReadString(string subKey, string name)
    {
        var rc = RegOpenKeyExW(HKeyCurrentUser, subKey, 0, KeyRead, out var key);
        if (rc != ErrorSuccess) return null;
        try
        {
            uint size = 0;
            if (RegQueryValueExW(key, name, IntPtr.Zero, out var type, null, ref size) != ErrorSuccess || size < 2)
                return null;
            if (type != RegSz && type != RegExpandSz) return null;

            var buffer = new byte[size];
            if (RegQueryValueExW(key, name, IntPtr.Zero, out _, buffer, ref size) != ErrorSuccess) return null;
            var text = Encoding.Unicode.GetString(buffer, 0, Math.Max(0, (int)size - 2));
            return text;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    public static int? ReadDword(string subKey, string name)
    {
        var rc = RegOpenKeyExW(HKeyCurrentUser, subKey, 0, KeyRead, out var key);
        if (rc != ErrorSuccess) return null;
        try
        {
            var buffer = new byte[4];
            uint size = (uint)buffer.Length;
            if (RegQueryValueExW(key, name, IntPtr.Zero, out var type, buffer, ref size) != ErrorSuccess
                || type != RegDword || size != 4) return null;
            return BitConverter.ToInt32(buffer, 0);
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    public static List<SetupRegistryValue> ReadValues(string subKey)
    {
        var values = new List<SetupRegistryValue>();
        var rc = RegOpenKeyExW(HKeyCurrentUser, subKey, 0, KeyRead, out var key);
        if (rc != ErrorSuccess) return values;
        try
        {
            var nameBuffer = new StringBuilder(512);
            var dataBuffer = new byte[64 * 1024];
            for (uint index = 0; ; index++)
            {
                uint nameSize = (uint)nameBuffer.Capacity;
                uint dataSize = (uint)dataBuffer.Length;
                var result = RegEnumValueW(key, index, nameBuffer, ref nameSize, IntPtr.Zero,
                    out var type, dataBuffer, ref dataSize);
                if (result == 259) break; // ERROR_NO_MORE_ITEMS
                if (result != ErrorSuccess) break;

                var data = new byte[dataSize];
                Array.Copy(dataBuffer, data, (int)dataSize);
                values.Add(new SetupRegistryValue(nameBuffer.ToString(), type, data));
            }
        }
        finally
        {
            RegCloseKey(key);
        }
        return values;
    }

    public static void SetString(string subKey, string name, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + "\0");
        WriteRaw(subKey, name, RegSz, bytes);
    }

    public static void SetDword(string subKey, string name, int value)
    {
        WriteRaw(subKey, name, RegDword, BitConverter.GetBytes(value));
    }

    public static void WriteRaw(string subKey, string name, int type, byte[] data)
    {
        var rc = RegCreateKeyExW(HKeyCurrentUser, subKey, 0, null, 0, KeyWrite, IntPtr.Zero,
            out var key, out _);
        if (rc != ErrorSuccess)
            throw new Win32Exception(rc, "无法创建注册表键：" + subKey);
        try
        {
            var valueRc = RegSetValueExW(key, name, 0, type, data, (uint)data.Length);
            if (valueRc != ErrorSuccess)
                throw new Win32Exception(valueRc, "写入注册表值失败：" + name);
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    /// <summary>删除一个无子键的键（安装器只用于卸载登记键）。键不存在视为成功。</summary>
    public static void DeleteKey(string subKey)
    {
        RegDeleteKeyW(HKeyCurrentUser, subKey);
    }

    public static void DeleteValue(string subKey, string name)
    {
        var rc = RegOpenKeyExW(HKeyCurrentUser, subKey, 0, KeyWrite, out var key);
        if (rc != ErrorSuccess) return;
        try
        {
            var valueRc = RegDeleteValueW(key, name);
            if (valueRc != ErrorSuccess && valueRc != ErrorFileNotFound)
                throw new Win32Exception(valueRc, "删除注册表值失败：" + name);
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
    private static extern int RegEnumValueW(IntPtr hKey, uint dwIndex, StringBuilder lpValueName,
        ref uint lpcchValueName, IntPtr lpReserved, out int lpType, byte[] lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegDeleteKeyW(IntPtr hKey, string lpSubKey);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegDeleteValueW(IntPtr hKey, string lpValueName);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegCloseKey(IntPtr hKey);
}
