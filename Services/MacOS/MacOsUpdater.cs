// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// macOS 端的更新安装：下载到的产物是 <c>.dmg</c>（不是 Windows 的 .exe 安装器），
/// 这里负责挂载镜像、把新的 MiHomeHub.app 换到当前运行位置，再重新拉起应用。
/// 没有就地替换的权限时（应用装在只读位置、或不是 .app 方式运行）退化为
/// 「打开镜像让用户手动拖拽」，不硬失败。
/// </summary>
internal static class MacOsUpdater
{
    /// <summary>从当前进程路径反推 .app 包目录；便携运行（bin/ 下的裸可执行文件）返回 false。</summary>
    public static bool TryGetRunningBundle(out string bundlePath)
    {
        bundlePath = string.Empty;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var macOsDir = Path.GetDirectoryName(exe);           // …/MiHomeHub.app/Contents/MacOS
            if (macOsDir is null) return false;
            var contents = Path.GetDirectoryName(macOsDir);      // …/MiHomeHub.app/Contents
            if (contents is null ||
                !string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal))
                return false;
            var bundle = Path.GetDirectoryName(contents);         // …/MiHomeHub.app
            if (bundle is null || !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return false;

            bundlePath = bundle;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>挂载 dmg 并把新版本换到当前位置；返回给用户看的一句话结果。</summary>
    public static string InstallFromDmg(string dmgPath)
    {
        if (!File.Exists(dmgPath))
            throw new FileNotFoundException("安装包不存在", dmgPath);

        var mount = MountDmg(dmgPath);
        if (mount is null)
        {
            OpenDmg(dmgPath);
            return "无法挂载磁盘映像，已打开镜像，请手动把 MiHomeHub 拖进「应用程序」";
        }

        try
        {
            var newApp = Directory.GetDirectories(mount, "*.app").FirstOrDefault();
            if (newApp is null)
            {
                OpenDmg(dmgPath);
                return "镜像里没有找到 .app，已打开镜像，请手动把 MiHomeHub 拖进「应用程序」";
            }

            if (!TryGetRunningBundle(out var bundle))
            {
                OpenDmg(dmgPath);
                return "当前不是 .app 方式运行，已打开镜像，请手动把 MiHomeHub 拖进「应用程序」";
            }

            var parent = Path.GetDirectoryName(bundle);
            if (parent is null || !IsWritable(parent))
            {
                OpenDmg(dmgPath);
                return "没有写入当前安装目录的权限，已打开镜像，请手动把 MiHomeHub 拖进「应用程序」";
            }

            // 正在运行的 app 不能就地覆盖（可执行文件被占用），先把旧的挪开再放新的。
            var backup = bundle + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            if (string.IsNullOrEmpty(Run("/bin/mv", Quote(bundle) + " " + Quote(backup))))
            {
                OpenDmg(dmgPath);
                return "替换旧版本失败，已打开镜像，请手动把 MiHomeHub 拖进「应用程序」";
            }

            if (string.IsNullOrEmpty(Run("/usr/bin/ditto", Quote(newApp) + " " + Quote(bundle))))
            {
                Run("/bin/mv", Quote(backup) + " " + Quote(bundle));   // 回滚到旧版本
                OpenDmg(dmgPath);
                return "写入新版本失败，已回滚到旧版本；镜像已打开，请手动安装";
            }

            ScheduleCleanup(backup);
            ScheduleRelaunch(bundle);
            return "正在替换为新版本并重新启动…";
        }
        finally
        {
            Detach(mount);
        }
    }

    // ─────────────────────────────── 内部实现 ───────────────────────────────

    private static string? MountDmg(string dmgPath)
    {
        var output = Run("/usr/bin/hdiutil", "attach -nobrowse -readonly -mountrandom " + Quote(dmgPath));
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var last = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (last is not null && last.StartsWith("/Volumes/", StringComparison.Ordinal))
                return last;
        }
        return null;
    }

    private static void Detach(string mount) => Run("/usr/bin/hdiutil", "detach " + Quote(mount));

    private static void OpenDmg(string dmgPath) => Run("/usr/bin/open", Quote(dmgPath));

    /// <summary>目录是否可写（能创建临时文件才算）。</summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".mihomehub-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>旧副本延后删除：正在运行的进程还占着里面的可执行文件。</summary>
    private static void ScheduleCleanup(string backup)
    {
        Spawn("/bin/sh", "-c " + Quote("/bin/sleep 60; /bin/rm -rf " + Quote(backup)));
    }

    /// <summary>等旧进程退出后再拉起新版本（替换时旧进程还在跑）。</summary>
    private static void ScheduleRelaunch(string bundle)
    {
        var pid = Environment.ProcessId;
        var script = $"/bin/sh -c {Quote($@"while /bin/kill -0 {pid} 2>/dev/null; do /bin/sleep 0.5; done; /usr/bin/open {Quote(bundle)}")}";
        Spawn("/bin/sh", script);
    }

    private static void Spawn(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.Dispose();
        }
        catch
        {
            // 拉起失败不影响主流程：用户还能手动打开新版本
        }
    }

    /// <summary>执行外部命令；成功返回 stdout，失败返回空串。</summary>
    private static string Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) return "";

            var stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(180_000)) return "";
            return p.ExitCode == 0 ? stdout : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>shell 引号：路径里有空格/引号时不至于把命令拆坏。</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
