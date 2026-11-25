// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace MiHomeHub.Services.Setup;

/// <summary>安装前后的进程协同：关闭安装在目标目录里的 MiHomeHub 实例，完成后启动新版本。</summary>
internal static class SetupProcess
{
    public static List<Process> FindRunning(string root)
    {
        var list = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!string.Equals(p.ProcessName, "MiHomeHub", StringComparison.OrdinalIgnoreCase))
                {
                    p.Dispose();
                    continue;
                }

                var path = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && SetupFiles.IsUnder(path, root))
                    list.Add(p);
                else
                    p.Dispose();
            }
            catch
            {
                // 32/64 位或权限不足读不到路径：不认定为本安装目录的实例
                p.Dispose();
            }
        }
        return list;
    }

    /// <summary>关闭目标目录里的实例。MiHomeHub 默认关闭即收进托盘，所以先试 WM_CLOSE，等不到再强杀。</summary>
    public static bool StopRunning(string root, int timeoutMs = 8000)
    {
        var running = FindRunning(root);
        if (running.Count == 0) return false;

        SetupLog.Info("stop-running", "ok", $"count={running.Count}");
        foreach (var p in running)
        {
            try { p.CloseMainWindow(); }
            catch (Exception ex) { SetupLog.Fail("stop-running-close", ex, details: $"pid={p.Id}"); }
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (running.All(HasExited)) break;
            Thread.Sleep(200);
        }

        foreach (var p in running)
        {
            try
            {
                if (HasExited(p)) continue;
                SetupLog.Info("stop-running", "kill", $"pid={p.Id}");
                p.Kill(entireProcessTree: false);
                p.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                SetupLog.Fail("stop-running-kill", ex, details: $"pid={p.Id}");
            }
            finally
            {
                p.Dispose();
            }
        }
        return true;
    }

    public static void Launch(string exe)
    {
        if (!File.Exists(exe))
            throw new SetupFailureException("启动程序", $"程序不存在：{exe}");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true,
        };
        Process.Start(psi);
        SetupLog.Info("launch-app", "ok", exe);
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch { return true; }
    }
}
