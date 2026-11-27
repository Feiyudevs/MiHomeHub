// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiHomeHub.Models;

namespace MiHomeHub.Services.CameraMedia;

/// <summary>
/// 内置画面链路自检（只读）：释放内置画面源 → 起进程 → 用显卡硬解取帧 → 落一张 BMP。
/// 用法：MiHomeHub.exe --camera-live-probe [did] [帧数]
/// 不启界面、不改设备状态，输出只含帧尺寸、帧率、耗时，不含令牌。
/// </summary>
public static class CameraLiveProbe
{
    public static async Task RunAsync(string? did, int frames)
    {
        var devices = DeviceCacheStore.Load() ?? new List<MijiaDevice>();
        var device = did != null
            ? devices.FirstOrDefault(d => d.Did == did)
            : devices.FirstOrDefault(d => d.IsCamera);

        if (device == null)
        {
            Console.WriteLine("没有找到摄像头设备：先在程序里同步一次设备列表，或直接用 --camera-live-probe <did>。");
            return;
        }

        Console.WriteLine($"设备：{device.Name} / {device.Model} / {device.LocalIp}");
        Console.WriteLine($"显卡 HEVC 硬解：{(CameraHevcDecoder.CanUseHardware() ? "支持" : "不支持")}；HEVC 组件：{(CameraHevcDecoder.CanUseSoftwareMft() ? "已装" : "未装")}");

        var warmUrl = Environment.GetEnvironmentVariable("CAM_PROBE_URL");
        if (!string.IsNullOrEmpty(warmUrl))
        {
            DecodeOnMta(warmUrl, device.Did, frames);
            return;
        }

        var ensure = await CameraGo2Rtc.EnsureAsync(device).ConfigureAwait(false);
        Console.WriteLine($"内置画面源：{(ensure.ok ? "就绪" : "失败")} {ensure.baseUrl} {ensure.error}");
        if (!ensure.ok) return;

        var url = $"{ensure.baseUrl}/api/stream.mp4?src={Uri.EscapeDataString(device.Did)}";
        // Media Foundation 的阻塞调用必须在 MTA 线程上跑：主线程是 STA，会卡在消息泵上。
        var thread = new Thread(() => DecodeOnMta(url, device.Did, frames));
        thread.SetApartmentState(ApartmentState.MTA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        CameraGo2Rtc.Stop();
    }

    private static void DecodeOnMta(string url, string did, int frames)
    {
        using var decoder = new CameraHevcDecoder(url);
        if (!decoder.Open(out var openError))
        {
            Console.WriteLine($"打开解码失败：{openError}");
            CameraGo2Rtc.Stop();
            return;
        }

        var w = decoder.Width;
        var h = decoder.Height;
        Console.WriteLine($"帧尺寸：{w}x{h}");

        var bgra = new byte[w * h * 4];
        var outPath = Path.Combine(AppPaths.Dir, "camera", "live-probe.bmp");
        var sw = Stopwatch.StartNew();
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var got = 0;
        string? saved = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(frames, 10) / 2.0 + 10));
        while (got < frames && !cts.IsCancellationRequested)
        {
            if (!decoder.TryReadFrame(bgra, cts.Token)) break;
            got++;
            if (got == 3 || saved == null && got >= frames / 2)
            {
                saved = WriteBmp(outPath, w, h, bgra);
            }
        }

        sw.Stop();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu0;
        Console.WriteLine($"取帧：{got} 帧 / {sw.ElapsedMilliseconds}ms，CPU {cpu.TotalMilliseconds:F0}ms，{(got > 0 ? sw.Elapsed.TotalMilliseconds / got : 0):F2}ms/帧");
        if (saved != null) Console.WriteLine($"样张：{saved}");

        CameraGo2Rtc.Stop();
    }

    /// <summary>声音自检：走 RTSP 收 PCMA，解成 PCM 落一段 WAV，顺带打出包数、时长和音量。</summary>
    public static async Task RunAudioAsync(string? did, int seconds)
    {
        var devices = DeviceCacheStore.Load() ?? new List<MijiaDevice>();
        var device = did != null
            ? devices.FirstOrDefault(d => d.Did == did)
            : devices.FirstOrDefault(d => d.IsCamera);
        if (device == null)
        {
            Console.WriteLine("没有找到摄像头设备。");
            return;
        }

        var overrideRtsp = Environment.GetEnvironmentVariable("CAM_RTSP_URL");
        var keepVideo = false;
        string mp4Url = "";
        if (string.IsNullOrEmpty(overrideRtsp))
        {
            var ensure = await CameraGo2Rtc.EnsureAsync(device).ConfigureAwait(false);
            Console.WriteLine($"内置画面源：{(ensure.ok ? "就绪" : "失败")} {ensure.error}");
            if (!ensure.ok) return;
            mp4Url = $"{ensure.baseUrl}/api/stream.mp4?src={Uri.EscapeDataString(device.Did)}";
            keepVideo = true;
        }

        var rtsp = string.IsNullOrEmpty(overrideRtsp) ? CameraGo2Rtc.RtspUrl : overrideRtsp.TrimEnd('/');
        Console.WriteLine($"RTSP：{rtsp}");

        // 声音要先有画面消费者把流真跑起来，否则画面源会在 DESCRIBE 之后把连接掐掉
        using var videoCts = new CancellationTokenSource();
        Task? feeder = null;
        var videoReady = new System.Threading.ManualResetEventSlim(false);
        if (keepVideo)
        {
            feeder = Task.Run(() =>
            {
                using var dec = new CameraHevcDecoder(mp4Url);
                if (!dec.Open(out var err))
                {
                    Console.WriteLine($"视频没起来：{err}");
                    return;
                }
                var buf = new byte[dec.Width * dec.Height * 4];
                while (!videoCts.IsCancellationRequested)
                {
                    if (!dec.TryReadFrame(buf, videoCts.Token)) break;
                }
            });
            videoReady.Wait(TimeSpan.FromSeconds(45));
            Console.WriteLine("视频已出帧：" + videoReady.IsSet);
        }

        var source = new CameraAudioSource();
        var chunks = new System.Collections.Generic.List<byte[]>();
        var packets = 0;
        source.Pcm += pcm => { packets++; chunks.Add(pcm); };
        var started = false;
        var error = "";
        for (var attempt = 0; attempt < 4 && !started; attempt++)
        {
            if (attempt > 0) Thread.Sleep(1500);
            var candidate = new CameraAudioSource();
            candidate.Pcm += pcm => { packets++; chunks.Add(pcm); };
            if (candidate.Start(rtsp, device.Did, out error))
            {
                started = true;
                source = candidate;
                break;
            }
            Console.WriteLine($"第 {attempt + 1} 次：{error}");
            candidate.Dispose();
        }
        if (!started)
        {
            Console.WriteLine("音频启动失败。");
            CameraGo2Rtc.Stop();
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds))).ConfigureAwait(false);
        source.Dispose();
        videoCts.Cancel();
        try { feeder?.Wait(2000); } catch { }

        var total = 0;
        double sumSq = 0;
        long count = 0;
        var all = new MemoryStream();
        foreach (var chunk in chunks)
        {
            all.Write(chunk, 0, chunk.Length);
            total += chunk.Length;
            for (var i = 0; i + 1 < chunk.Length; i += 2)
            {
                var sample = (short)(chunk[i] | (chunk[i + 1] << 8));
                sumSq += (double)sample * sample;
                count++;
            }
        }

        var rms = count > 0 ? Math.Sqrt(sumSq / count) : 0;
        var duration = (double)total / 2 / CameraAudioSource.SampleRate;
        Console.WriteLine($"音频：{packets} 包 / {seconds}s，PCM {total} 字节 ≈ {duration:F2}s，RMS {rms:F0}");

        var path = Path.Combine(AppPaths.Dir, "camera", "audio-probe.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pcm = all.ToArray();
        using (var fs = File.Create(path))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + pcm.Length);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            bw.Write(16);
            bw.Write((ushort)1);
            bw.Write((ushort)1);
            bw.Write(CameraAudioSource.SampleRate);
            bw.Write(CameraAudioSource.SampleRate * 2);
            bw.Write((ushort)2);
            bw.Write((ushort)16);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(pcm.Length);
            bw.Write(pcm);
        }
        Console.WriteLine($"样张：{path}");

        CameraGo2Rtc.Stop();
    }
    private static string WriteBmp(string path, int w, int h, byte[] bgra)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        var size = 54 + w * h * 3;
        bw.Write((ushort)0x4D42);
        bw.Write(size);
        bw.Write(0);
        bw.Write(54);
        bw.Write(40);
        bw.Write(w);
        bw.Write(h);
        bw.Write((ushort)1);
        bw.Write((ushort)24);
        bw.Write(0);
        bw.Write(w * h * 3);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);
        for (var y = h - 1; y >= 0; y--)
        {
            var row = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var o = row + x * 4;
                bw.Write(bgra[o]);
                bw.Write(bgra[o + 1]);
                bw.Write(bgra[o + 2]);
            }
        }
        return path;
    }
}


