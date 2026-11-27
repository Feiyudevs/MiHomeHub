// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiHomeHub.Models;

namespace MiHomeHub.Services.CameraMedia;

/// <summary>
/// 内置的本地画面源（go2rtc，MIT）。
///
/// 摄像头只肯把画面交给带小米 P2P 握手的一方，米家云端接口里没有视频数据，
/// 所以这里随程序带一份 go2rtc 做协议栈：首次用到时从程序资源里释放到数据目录，
/// 按当前账号和一个随机本机端口生成配置，起一个隐藏进程，用完即退。
/// 用户机器上不需要预装任何东西。
/// </summary>
public static class CameraGo2Rtc
{
    private const string ResourceName = "MiHomeHub.camera.go2rtc.exe";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private static readonly object Gate = new();
    private static Process? _process;
    private static int _apiPort;
    private static int _rtspPort;
    private static string _baseUrl = "";
    private static bool _exitHooked;
    private static IntPtr _job;

    /// <summary>要写进画面源配置的摄像头：did → 型号 / 局域网地址。</summary>
    private static readonly Dictionary<string, StreamEntry> Streams = new(StringComparer.Ordinal);

    /// <summary>当前进程的配置里已经有哪些 did；新来一台不在其中才需要重启画面源。</summary>
    private static readonly HashSet<string> Configured = new(StringComparer.Ordinal);

    private sealed class StreamEntry
    {
        public string Model { get; init; } = "";
        public string Ip { get; init; } = "";
    }

    /// <summary>内置版本号，改资源时同步改这里，旧 exe 会被替换。</summary>
    private const string Version = "1.9.15";

    public static string BaseUrl
    {
        get { lock (Gate) return _baseUrl; }
    }

    /// <summary>声音走 RTSP 那条路：只有它带音频轨（mp4 里只有视频）。</summary>
    public static string RtspUrl
    {
        get { lock (Gate) return _rtspPort > 0 ? $"rtsp://127.0.0.1:{_rtspPort}" : ""; }
    }

    public static bool IsRunning
    {
        get
        {
            lock (Gate)
            {
                try { return _process is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    /// <summary>
    /// 先把候选摄像头登记进配置清单（只记，不起进程）：
    /// 之后无论桌面卡先播还是详情页先播，都能一次带上所有流，
    /// 不会因为「这台不在配置里」把正在播的那一路重启掉。
    /// </summary>
    public static void Prime(IEnumerable<MijiaDevice> cameras)
    {
        lock (Gate)
        {
            foreach (var c in cameras)
            {
                if (string.IsNullOrEmpty(c.Did) || string.IsNullOrEmpty(c.LocalIp)) continue;
                Streams[c.Did] = new StreamEntry { Model = c.Model, Ip = c.LocalIp };
            }
        }
    }

    /// <summary>
    /// 确保画面源在跑，并把这台摄像机的流登记进去。返回可直接播放的 api 根地址。
    /// 失败时给出可读原因，界面直接显示。
    /// </summary>
    public static async Task<(bool ok, string baseUrl, string error)> EnsureAsync(
        MijiaDevice device, CancellationToken ct = default)
    {
        var host = MijiaAuth.LoadOrEmpty();
        var uid = host.Auth.UserId;
        var token = host.Auth.PassToken;
        var ip = device.LocalIp;

        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(token))
            return (false, "", "还没有米家登录态：先在设置里扫码登录一次，画面源要用它去局域网握手。");
        if (string.IsNullOrEmpty(ip))
            return (false, "", "这台摄像机没有局域网地址：先在米家里打开一次，让设备列表带上 localip。");

        string exe;
        try { exe = EnsureBinary(); }
        catch (Exception ex) { return (false, "", $"释放内置画面源失败：{ex.GetBaseException().Message}"); }

        lock (Gate)
        {
            Streams[device.Did] = new StreamEntry { Model = device.Model, Ip = ip };

            bool alive;
            try { alive = _process is { HasExited: false }; }
            catch { alive = false; }

            // 进程活着、但配置里没有这一路：只能按新清单重启一次（前面 Prime 过的摄像头已在配置里，不会走到这里）
            if (alive && !Configured.Contains(device.Did))
            {
                StopLocked();
                alive = false;
            }

            if (!alive)
            {
                try
                {
                    StartProcess(exe, uid, token);
                }
                catch (Exception ex)
                {
                    return (false, "", $"启动内置画面源失败：{ex.GetBaseException().Message}");
                }
            }
        }

        var baseUrl = BaseUrl;
        if (!await WaitReadyAsync(baseUrl, ct).ConfigureAwait(false))
            return (false, baseUrl, "内置画面源没有在 8 秒内就绪：端口可能被占用或被安全软件拦了，稍后重试。");

        return (true, baseUrl, "");
    }

    /// <summary>程序退出、或用户停止画面时收掉进程，不留在后台。</summary>
    public static void Stop()
    {
        lock (Gate) StopLocked();
    }

    private static void StopLocked()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程已经被系统回收，忽略
        }
        _process = null;
        _baseUrl = "";
        _apiPort = 0;
        _rtspPort = 0;
        Configured.Clear();
    }

    /// <summary>从程序资源里释放 go2rtc.exe，版本变了就覆盖。</summary>
    private static string EnsureBinary()
    {
        var dir = Path.Combine(AppPaths.Dir, "camera");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "go2rtc.exe");
        var stamp = Path.Combine(dir, "version.txt");

        var built = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : "";
        if (File.Exists(exe) && built == Version) return exe;

        var asm = typeof(CameraGo2Rtc).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException("程序里没有内置的画面源资源");
        using (var file = File.Create(exe))
        {
            stream.CopyTo(file);
        }
        File.WriteAllText(stamp, Version);
        return exe;
    }

    private static void StartProcess(string exe, string uid, string token)
    {
        var dir = Path.GetDirectoryName(exe)!;
        var api = FreePort();
        var rtsp = FreePort();
        var webrtc = FreePort();

        var yaml = new StringBuilder();
        yaml.Append("api:\n  listen: \"127.0.0.1:").Append(api).Append("\"\n");
        yaml.Append("rtsp:\n  listen: \"127.0.0.1:").Append(rtsp).Append("\"\n");
        yaml.Append("webrtc:\n  listen: \"127.0.0.1:").Append(webrtc).Append("\"\n");
        yaml.Append("  candidates:\n    - \"127.0.0.1:").Append(webrtc).Append("\"\n");
        yaml.Append("xiaomi:\n  ").Append(uid).Append(": ").Append(token).Append('\n');

        // 一次把登记过的摄像头全写进去：桌面监控卡与详情页各看一台时也不用重启画面源
        Configured.Clear();
        if (Streams.Count == 0)
        {
            yaml.Append("streams: {}\n");
        }
        else
        {
            yaml.Append("streams:\n");
            foreach (var (did, entry) in Streams)
            {
                yaml.Append("  ").Append(did).Append(": \"xiaomi://")
                    .Append(uid).Append(":cn@").Append(entry.Ip)
                    .Append("?did=").Append(did)
                    .Append("&model=").Append(entry.Model).Append("\"\n");
                Configured.Add(did);
            }
        }

        var configPath = Path.Combine(dir, "go2rtc.yaml");
        File.WriteAllText(configPath, yaml.ToString(), new UTF8Encoding(false));
        TryRestrict(configPath);

        var logPath = Path.Combine(dir, "go2rtc.log");
        if (File.Exists(logPath))
        {
            try
            {
                var info = new FileInfo(logPath);
                if (info.Length > 2 * 1024 * 1024) File.Delete(logPath);
            }
            catch
            {
                // 日志清不掉不影响启动
            }
        }

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-config");
        psi.ArgumentList.Add(configPath);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("启动画面源进程失败");
        // 上一个实例可能还没放掉日志文件：写不进去也不能挡住启动
        StreamWriter? log = null;
        try { log = new StreamWriter(logPath, append: true) { AutoFlush = true }; }
        catch { log = null; }
        if (log != null)
        {
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => { try { log?.Dispose(); } catch { } };

        if (!_exitHooked)
        {
            _exitHooked = true;
            // 主进程无论怎么退出，内置画面源都跟着走，不给用户留后台进程。
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Stop();
        }

        // 进程被强杀时 ProcessExit 不会执行，用 Job Object 兜住：句柄一关，子进程跟着结束。
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new JobObjectExtendedLimitInformation();
                info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                SetInformationJobObject(_job, JobObjectExtendedLimitInformationClass, ref info,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>());
            }
            if (_job != IntPtr.Zero) AssignProcessToJobObject(_job, proc.Handle);
        }
        catch
        {
            // 加不进 Job 时还有 ProcessExit 兜底，不影响播放
        }

        _process = proc;
        _apiPort = api;
        _rtspPort = rtsp;
        _baseUrl = $"http://127.0.0.1:{api}";
    }

    private static async Task<bool> WaitReadyAsync(string baseUrl, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var resp = await Http.GetAsync(baseUrl + "/api/streams", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch
            {
                // 还没起来，继续等
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>配置里带着会话令牌，只给当前账户读写。</summary>
    private static void TryRestrict(string path)
    {
        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user == null) return;
            var info = new FileInfo(path);
            var acl = info.GetAccessControl();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Read | FileSystemRights.Write,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        catch
        {
            // 收紧失败不阻塞播放
        }
    }

    /// <summary>
    /// 本机能不能解 HEVC：Media Foundation 要有一个已注册的解码器，Windows 上就是那个 HEVC 组件；
    /// 显卡支持硬解只代表能加速，替代不了解码器本身（组件没装时 SourceReader 会找不到解码器）。
    /// </summary>
    public static bool HasHevcDecoder()
    {
        try
        {
            return HevcSupportProbe.IsAvailable();
        }
        catch
        {
            return false;
        }
    }

    // ===== Job Object：父进程死掉时子进程一起结束 =====
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass,
        ref JobObjectExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

/// <summary>只做一件事：问系统有没有可用的 HEVC 解码器，不解帧。</summary>
internal static class HevcSupportProbe
{
    public static bool IsAvailable()
    {
        // Windows 上的 HEVC 解码能力来自那个系统组件（OEM 机器常预装，零售机要装一次）。
        // 显卡支持硬解只影响快慢，不构成「能解」：组件没装时 SourceReader 直接找不到解码器。
        return CameraHevcDecoder.CanUseSoftwareMft();
    }
}

