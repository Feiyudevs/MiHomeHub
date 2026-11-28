// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;

namespace MiHomeHub.Services;

/// <summary>
/// 未处理异常的落盘日志。
///
/// 没有它的时候，macOS 上一次未处理的托管异常只会变成一条
/// "Abort trap: 6" 的崩溃报告，栈里全是没符号的托管地址，
/// 事后根本查不出是什么错（用户报「点绿键就闪退」时就是这样）。
/// 这里把异常类型、消息和托管栈写到应用数据目录的 crash.log，
/// 下次出问题时一眼能看到原因。
/// </summary>
public static class CrashLog
{
    private static readonly string LogFile = Path.Combine(AppPaths.Dir, "crash.log");

    /// <summary>追加一条崩溃记录；写盘失败也不能把异常处理本身变成新的异常。</summary>
    public static void Write(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Dir);
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}\n{ex}\n";
            File.AppendAllText(LogFile, text);
        }
        catch
        {
            // 写不进去就算了，不能因为记日志把进程再搞崩一次
        }
    }
}
