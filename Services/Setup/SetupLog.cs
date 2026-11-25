// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MiHomeHub.Services.Setup;

/// <summary>
/// 安装/卸载诊断日志（外壳与主程序共用）：<c>%TEMP%\MiHomeHub-setup.log</c>，
/// 全局互斥体串行写入，单行制表符分隔，超 64 KB 从头部截断。
/// 所有写日志失败都静默吞掉——诊断不能遮盖原始故障。
/// </summary>
internal static class SetupLog
{
    private const long MaxBytes = 64 * 1024;

    public static string FilePath => Path.Combine(Path.GetTempPath(), SetupProtocol.LogFileName);

    public static void Info(string step, string result = "ok", string? message = null, Exception? ex = null,
        string? path = null, string? details = null)
        => Write(step, result, ex, path, message, details);

    public static void Fail(string step, Exception ex, string? path = null, string? message = null, string? details = null)
        => Write(step, "failed", ex, path, message, details);

    private static void Write(string step, string result, Exception? ex, string? path, string? message, string? details)
    {
        try
        {
            var line = string.Join('\t',
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                $"pid={Environment.ProcessId}",
                $"step={step}",
                $"result={result}",
                "exception=" + (ex?.GetType().Name ?? string.Empty),
                "path=" + (path ?? string.Empty),
                "message=" + oneLine(message ?? ex?.Message),
                "inner=" + oneLine(ex?.InnerException?.Message),
                "stack=" + oneLine(ex?.StackTrace),
                "details=" + oneLine(details));

            bool owned = false;
            try
            {
                using var mutex = new Mutex(false, SetupProtocol.LogMutexName);
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { owned = true; }

                try
                {
                    TrimIfNeeded(FilePath);
                    File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false));
                }
                finally
                {
                    if (owned) { try { mutex.ReleaseMutex(); } catch { } }
                }
            }
            catch
            {
                try { File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false)); } catch { }
            }
        }
        catch
        {
            // 日志失败不得影响安装
        }
    }

    /// <summary>日志里是否出现过某一步（外壳的重试判定依据）。</summary>
    public static bool ContainsStep(string step)
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            return File.ReadLines(FilePath).Any(l => l.Contains($"\tstep={step}\t", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private static void TrimIfNeeded(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            if (new FileInfo(file).Length <= MaxBytes) return;
            var lines = File.ReadAllLines(file);
            var keep = lines.Skip(Math.Max(0, lines.Length - 400)).ToArray();
            File.WriteAllLines(file, keep, new UTF8Encoding(false));
        }
        catch { }
    }

    private static string oneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
