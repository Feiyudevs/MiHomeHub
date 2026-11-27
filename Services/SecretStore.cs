// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Text;
using MiHomeHub.Services.MacOS;

namespace MiHomeHub.Services;

/// <summary>
/// 登录凭证的落盘保护：Windows 用 DPAPI（当前用户作用域），macOS 用系统钥匙串。
/// 落盘只写一个标记串，真正的密文交给系统保管；解不开时调用方按「未登录」处理。
/// <para>
/// 为什么不在 Windows 侧直接沿用 DpapiStore：两边的密文存放位置完全不同（DPAPI 是本地加密 blob，
/// 钥匙串是系统保管的条目），调用方不该关心平台差异，所以在这里做一层分派。
/// </para>
/// </summary>
internal static class SecretStore
{
    /// <summary>macOS 标记前缀；后面的版本号留给将来换存储格式时区分。</summary>
    private const string KeychainPrefix = "keychain:";

    /// <summary>内容是不是本工具保护过的（带对应平台的标记前缀）。</summary>
    public static bool IsProtected(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (OperatingSystem.IsWindows()) return DpapiStore.IsProtected(text);
        if (OperatingSystem.IsMacOS()) return text.StartsWith(KeychainPrefix, StringComparison.Ordinal);
        return false;
    }

    /// <summary>
    /// 返回要落盘的密文标记；本机保护不可用时返回 null。
    /// <para>
    /// 返回 null 表示「这次保护没成功」，调用方据此回退明文落盘——这是刻意的降级：
    /// 存不进系统密钥库时只是少了一层保护，不能连带把用户踢下线。
    /// </para>
    /// </summary>
    public static string? Protect(string plain)
    {
        if (OperatingSystem.IsWindows()) return DpapiStore.Protect(plain);
        if (!OperatingSystem.IsMacOS()) return null;
        if (plain is null) return null;
        try
        {
            // 整段 auth.json 的明文以 UTF-8 存进钥匙串，磁盘上只留一个标记
            var secret = Encoding.UTF8.GetBytes(plain);
            if (!MacOsKeychain.TryStore(MacOsKeychain.AccountAuth, secret)) return null;
            return KeychainPrefix + "1";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解密；前缀不符 / 系统里读不到 / 解密失败都返回 null。</summary>
    public static string? Unprotect(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (OperatingSystem.IsWindows()) return DpapiStore.Unprotect(text);
        if (!OperatingSystem.IsMacOS() || !text.StartsWith(KeychainPrefix, StringComparison.Ordinal)) return null;
        try
        {
            var secret = MacOsKeychain.TryLoad(MacOsKeychain.AccountAuth);
            if (secret is null || secret.Length == 0) return null;
            return Encoding.UTF8.GetString(secret);
        }
        catch
        {
            return null;
        }
    }
}
