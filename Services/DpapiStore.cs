// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using System.Text;

namespace MiHome.Services;

/// <summary>
/// Windows DPAPI（当前用户作用域）文本加解密。
/// 用于 auth.json：登录凭证不以明文落盘，仅当前 Windows 用户可解密。
/// 换 Windows 账户或换机后无法解密，需重新扫码登录。
/// </summary>
internal static class DpapiStore
{
    private const string Prefix = "dpapi:";
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>内容是否由本工具加密（带 dpapi: 前缀）。</summary>
    public static bool IsProtected(string? text)
        => text is not null && text.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>加密为 "dpapi:&lt;Base64&gt;"；非 Windows 或调用失败返回 null（调用方回退明文）。</summary>
    public static string? Protect(string plain)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var input = ToBlob(Encoding.UTF8.GetBytes(plain));
            try
            {
                if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out var output))
                    return null;
                return Prefix + Convert.ToBase64String(TakeAndFree(ref output));
            }
            finally { FreeBlob(ref input); }
        }
        catch { return null; }
    }

    /// <summary>解密 "dpapi:&lt;Base64&gt;"；前缀不符或解密失败返回 null。</summary>
    public static string? Unprotect(string protectedText)
    {
        if (!IsProtected(protectedText)) return null;
        try
        {
            var raw = Convert.FromBase64String(protectedText[Prefix.Length..]);
            var input = ToBlob(raw);
            try
            {
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out var output))
                    return null;
                return Encoding.UTF8.GetString(TakeAndFree(ref output));
            }
            finally { FreeBlob(ref input); }
        }
        catch { return null; }
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = ptr };
    }

    private static byte[] TakeAndFree(ref DATA_BLOB blob)
    {
        try
        {
            var bytes = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
            return bytes;
        }
        finally { FreeBlob(ref blob, localFree: true); }
    }

    private static void FreeBlob(ref DATA_BLOB blob, bool localFree = false)
    {
        if (blob.pbData == IntPtr.Zero) return;
        if (localFree) LocalFree(blob.pbData);
        else Marshal.FreeHGlobal(blob.pbData);
        blob.pbData = IntPtr.Zero;
        blob.cbData = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
