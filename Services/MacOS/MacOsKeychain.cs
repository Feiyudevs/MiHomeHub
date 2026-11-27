// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// macOS 钥匙串（通用密码）读写：Security.framework 的 SecKeychain* 系列。
/// 用来存登录凭证：auth.json 的密文只落在当前 macOS 用户的登录钥匙串里，
/// 别的用户 / 别的机器拿不到。对应 Windows 侧的 DPAPI（同样是「只有当前系统用户能解密」）。
/// <para>
/// 为什么用这一组老 API 而不是 SecItemAdd/SecItemCopyMatching：
/// SecItem 系列要求调用方有 application-identifier / keychain-access-groups 权限，
/// 本项目是自签（ad-hoc）分发，没有这些权限，实测 SecItemAdd 直接返回 errSecParam(-50)；
/// 而 SecKeychain* 这组写的是用户登录钥匙串，自签应用照样能用，实测正常。
/// 将来如果拿到正式开发者签名再迁到 SecItem 系列。
/// </para>
/// <para>
/// <b>踩过的坑（别再改错）</b>：SecKeychainItemFreeContent 的真实签名是
/// <c>OSStatus SecKeychainItemFreeContent(SecKeychainAttributeList *attrList, void *data)</c>，
/// 是<b>两个</b>参数。少写一个参数会让原生层把随机值当 attrList，
/// 里面 CFHash(NULL) 直接 abort——表现为「扫码登录后进程立刻闪退」。
/// </para>
/// <para>
/// 所有调用都包了 try/catch 且失败只返回 false/null：第一次访问系统可能会弹钥匙串授权框，
/// 用户拒绝或非交互环境下会直接失败——那种情况要静默降级（调用方回退明文），
/// 不能因为存不进钥匙串就登不上号。
/// </para>
/// <para>
/// <b>另一个必须记住的坑</b>：钥匙串条目是「上一版程序」写进去的，
/// 而本项目是自签（ad-hoc）分发——每次重新编译签名都变了，
/// macOS 会把新程序当成「另一个应用」，读取时要求用户点确认。
/// 没有人点确认时 SecKeychainFindGenericPassword 不会返回，
/// 而是挂在 mach_msg 上等 securityd 回话，把调用线程整个卡死
/// （实测：启动线程卡在这里，应用既没有界面也退不掉）。
/// 所以钥匙串访问一律放后台线程并带超时，超时按「取不到」处理，
/// 绝不因为钥匙串把启动卡住。
/// </para>
/// </summary>
internal static class MacOsKeychain
{
    /// <summary>钥匙串条目的服务名：对应「本应用专用」的命名空间，避免和别的应用撞名。</summary>
    public const string ServiceName = "com.mihomehub.app";

    /// <summary>存放登录凭证的条目名（对应 auth.json）。</summary>
    public const string AccountAuth = "auth.json";

    /// <summary>读操作的超时上限：超了就当没读到，调用方回退明文。</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>写/删操作的超时上限。</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 本进程内是否已判定「钥匙串取不到」（超时或被拒）。
    /// 判定一次就不再重试，免得每次启动都白等一个超时。
    /// </summary>
    private static bool _unavailable;

    /// <summary>写入 / 覆盖一条通用密码；成功返回 true，失败返回 false（不抛异常）。</summary>
    public static bool TryStore(string account, byte[] secret)
    {
        // 非 macOS 平台没有 Security.framework，直接挡掉（免得 DllNotFoundException）
        if (!OperatingSystem.IsMacOS()) return false;
        if (string.IsNullOrEmpty(account) || secret is null || secret.Length == 0) return false;
        var result = RunWithTimeout(() => NativeTryStore(account, secret), false, WriteTimeout);
        if (!result.Ok) _unavailable = true;
        return result.Value;
    }

    /// <summary>读出一条通用密码；读不到（没有这条 / 被拒 / 超时）返回 null。</summary>
    public static byte[]? TryLoad(string account)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        if (string.IsNullOrEmpty(account)) return null;
        if (_unavailable) return null;
        var result = RunWithTimeout<byte[]?>(() => NativeTryLoad(account), null, ReadTimeout);
        if (!result.Ok)
        {
            _unavailable = true;
            DesktopLog.Write("[keychain] 读取不可用（超时或被拒），本进程内不再重试");
        }
        return result.Value;
    }

    /// <summary>删掉一条通用密码；本来就没有也算成功。</summary>
    public static void Delete(string account)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (string.IsNullOrEmpty(account)) return;
        var result = RunWithTimeout(() => NativeDelete(account), true, WriteTimeout);
        if (!result.Ok) _unavailable = true;
    }

    /// <summary>
    /// 在后台线程跑一次 Security.framework 调用，并带超时。
    /// 拿不到就返回 fallback，绝不把调用线程挂在 securityd 的 mach_msg 上。
    /// </summary>
    private static (T Value, bool Ok) RunWithTimeout<T>(Func<T> action, T fallback, TimeSpan timeout)
    {
        Task<T>? task = null;
        try
        {
            task = Task.Run(action);
            if (task.Wait(timeout)) return (task.Result, true);
        }
        catch (Exception)
        {
            // 原生调用抛了：按「取不到」处理。
            return (fallback, false);
        }
        // 超时：后台线程继续挂着等 securityd，进程退出时自然结束。
        return (fallback, false);
    }

    private static bool NativeTryStore(string account, byte[] secret)
    {
        try
        {
            var service = Encoding.UTF8.GetBytes(ServiceName);
            var name = Encoding.UTF8.GetBytes(account);

            // 同一个 service+account 只允许存在一条：先删旧的再写新的（SecKeychainAdd 对重复条目会直接报错）
            DeleteExisting(service, name);

            var status = SecKeychainAddGenericPassword(
                IntPtr.Zero,                 // 默认登录钥匙串
                (uint)service.Length, service,
                (uint)name.Length, name,
                (uint)secret.Length, secret,
                out var itemRef);
            if (itemRef != IntPtr.Zero)
            {
                RelaxAccess(itemRef);   // 允许任何程序读取，免去下次启动弹授权框
                CFRelease(itemRef);     // 条目本身留在钥匙串里，只放掉我们这个引用
            }
            return status == ErrorSuccess;
        }
        catch
        {
            // 钥匙串被拒 / 沙盒限制 / 签名问题：按「没存上」处理，由调用方降级
            return false;
        }
    }

    private static byte[]? NativeTryLoad(string account)
    {
        try
        {
            var service = Encoding.UTF8.GetBytes(ServiceName);
            var name = Encoding.UTF8.GetBytes(account);

            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)service.Length, service,
                (uint)name.Length, name,
                out var length, out var data, out var itemRef);
            if (status != ErrorSuccess || data == IntPtr.Zero) return null;
            try
            {
                if (length == 0) return null;
                var buffer = new byte[length];
                Marshal.Copy(data, buffer, 0, (int)length);
                return buffer;
            }
            finally
            {
                // 密码内容是系统分配的一块内存，读完必须还回去，否则会一直泄漏。
                // 注意签名有两个参数：attrList 传 NULL，data 传内容指针。
                SecKeychainItemFreeContent(IntPtr.Zero, data);
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool NativeDelete(string account)
    {
        try
        {
            var service = Encoding.UTF8.GetBytes(ServiceName);
            var name = Encoding.UTF8.GetBytes(account);
            DeleteExisting(service, name);
            return true;
        }
        catch
        {
            // 删不掉就留着，调用方不依赖删除结果
            return false;
        }
    }

    /// <summary>
    /// 把条目的访问控制改成「任何程序都能读，不用弹窗确认」。
    /// 默认 ACL 只认「写这条的那份签名」，而自签分发每次重新编译签名都变，
    /// 下次读就会变成「另一个应用要访问」→ 弹授权框 → 没人点就永远挂着。
    /// 这套 API 在新系统上已废弃，能用就用，失败也不影响主流程。
    /// </summary>
    private static void RelaxAccess(IntPtr itemRef)
    {
        try
        {
            if (SecAccessCreate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var access) == ErrorSuccess && access != IntPtr.Zero)
            {
                try
                {
                    SecKeychainItemSetAccess(itemRef, access);
                }
                finally
                {
                    CFRelease(access);
                }
            }
        }
        catch
        {
            // 老系统没有这组 API：忽略，读取超时兜底照样生效。
        }
    }

    /// <summary>删掉同 service+account 的旧条目；没有旧条目也算成功。</summary>
    private static void DeleteExisting(byte[] service, byte[] account)
    {
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)service.Length, service,
            (uint)account.Length, account,
            out _, out var data, out var itemRef);
        if (status != ErrorSuccess) return; // 没有旧条目，不用删
        if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
        if (itemRef != IntPtr.Zero) SecKeychainItemDelete(itemRef);
    }

    private const int ErrorSuccess = 0;

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLen, byte[] service,
        uint accountLen, byte[] account, uint passwordLen, byte[] password, out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLen, byte[] service,
        uint accountLen, byte[] account, out uint passwordLen, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    /// <summary>
    /// 释放 SecKeychainFindGenericPassword 返回的内容。
    /// 原生签名是 (SecKeychainAttributeList *attrList, void *data)——两个参数，少写一个会 abort。
    /// </summary>
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    /// <summary>创建访问控制对象；trustedList 传 NULL 表示「任何程序都能访问，不用确认」。</summary>
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecAccessCreate(IntPtr descriptor, IntPtr trustedList, IntPtr trustedAppList, out IntPtr accessRef);

    /// <summary>给条目设置访问控制。</summary>
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemSetAccess(IntPtr itemRef, IntPtr accessRef);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);
}
