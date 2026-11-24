// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace MiHome.Services.Setup;

/// <summary>安装事务中可带步骤名的失败（不含 MiHome 依赖，外壳同源编译）。</summary>
internal sealed class SetupFailureException : Exception
{
    public string Step { get; }
    public string? FixHint { get; }

    public SetupFailureException(string step, string message, Exception? inner = null, string? fixHint = null)
        : base(message, inner)
    {
        Step = step;
        FixHint = fixHint;
    }
}

/// <summary>
/// 安装/卸载涉及的全部文件操作。任何复制/删除/移动之前必须过 <see cref="Check"/>：
/// 沿祖先链逐级检查 ReparsePoint，防止符号链接/junction 把递归操作带出目标目录。
/// </summary>
internal static class SetupFiles
{
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        while (full.Length > root.Length && (full.EndsWith('\\') || full.EndsWith('/')))
            full = full[..^1];
        return full;
    }

    /// <summary>全路径归一化 + 祖先链 ReparsePoint 校验。缺失的叶子不算错（创建场景）。</summary>
    public static string Check(string path)
    {
        var full = Normalize(path);
        var current = full;
        while (true)
        {
            try
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    var attrs = File.GetAttributes(current);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                        throw new SetupFailureException("路径校验", $"路径是链接/联接点，拒绝操作：{current}");
                }
            }
            catch (SetupFailureException) { throw; }
            catch { /* 无权限读取属性时继续向上：真正写入会给出明确错误 */ }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
        return full;
    }

    public static bool IsUnder(string child, string parent)
    {
        var c = Normalize(child);
        var p = Normalize(parent);
        if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return true;
        return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>复制目录（递归）。<paramref name="progress"/> 参数为 0..1。</summary>
    public static void CopyDirectory(string source, string dest, Action<double>? progress = null)
    {
        source = Check(source);
        dest = Check(dest);
        if (!Directory.Exists(source))
            throw new SetupFailureException("复制程序", $"安装源不存在：{source}");

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        Directory.CreateDirectory(dest);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            progress?.Invoke(files.Count == 0 ? 1 : (i + 1) / (double)files.Count);
        }
        progress?.Invoke(1);
    }

    public static void DeleteDirectory(string path)
    {
        var full = Check(path);
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }

    public static void MoveDirectory(string from, string to)
    {
        var src = Check(from);
        var dst = Check(to);
        if (!Directory.Exists(src))
            throw new SetupFailureException("移动目录", $"源目录不存在：{src}");
        if (Directory.Exists(dst))
            throw new SetupFailureException("移动目录", $"目标目录已存在：{dst}");
        Directory.Move(src, dst);
    }

    public static void CopyFile(string from, string to)
    {
        var src = Check(from);
        var dst = Check(to);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
    }

    public static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>杀软实时扫描可能短暂锁住刚解压的 exe，轮询到可读为止。</summary>
    public static bool WaitReadable(string file, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (File.Exists(file))
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length > 0) return true;
                }
            }
            catch { /* 被占用，继续等 */ }
            Thread.Sleep(120);
        }
        return File.Exists(file);
    }

    /// <summary>取文件版本（三段）。读不到返回 null。</summary>
    public static string? VersionOf(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var text = info.FileVersion ?? info.ProductVersion;
            return NormalizeVersion(text);
        }
        catch
        {
            return null;
        }
    }

    public static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = version.Trim().Split('.');
        if (parts.Length >= 3) return $"{parts[0]}.{parts[1]}.{parts[2]}";
        return version.Trim();
    }

    public static int CompareVersions(string? a, string? b)
    {
        var va = Parse(a);
        var vb = Parse(b);
        for (var i = 0; i < Math.Max(va.Length, vb.Length); i++)
        {
            var x = i < va.Length ? va[i] : 0;
            var y = i < vb.Length ? vb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static int[] Parse(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return new[] { 0, 0, 0, 0 };
        var parts = v.Trim().Split('.');
        var result = new int[Math.Max(3, parts.Length)];
        for (var i = 0; i < parts.Length && i < result.Length; i++)
            int.TryParse(parts[i], out result[i]);
        return result;
    }
}

/// <summary>临时产物上的排他租约：持有者崩溃后自动可清理，活事务不会被别的实例误删。</summary>
internal sealed class SetupLease : IDisposable
{
    private FileStream? _stream;
    public string Path { get; }

    private SetupLease(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public static SetupLease? TryAcquire(string targetPath, string content = "")
    {
        var leasePath = targetPath + SetupProtocol.LeaseSuffix;
        try
        {
            var stream = new FileStream(leasePath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.DeleteOnClose);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
                writer.Write(content);
            return new SetupLease(leasePath, stream);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }
}
