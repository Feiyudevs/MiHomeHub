// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MiHomeHub.Services.CameraMedia;

/// <summary>
/// 摄像头声音：走内置画面源的 RTSP 那条路（只有它带音频）。
///
/// 画面走的是 mp4，里头只有一条视频轨；声音是单独一条 PCMA/16000 轨，
/// mp4 装不下（要转 AAC 才能塞进去，那就要 ffmpeg）。所以这里自己当一次 RTSP 客户端：
/// DESCRIBE 拿 SDP、SETUP 要音频轨（RTP over TCP 交织）、PLAY 后在一条线程上收包，
/// 把 G.711 A-law 解成 16k 单声道 PCM 交给播放器。全程只连本机的内置画面源。
/// </summary>
public sealed class CameraAudioSource : IDisposable
{
    public const int SampleRate = 16000;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Thread? _reader;
    private volatile bool _running;
    private string _session = "";
    private static readonly bool Debug = Environment.GetEnvironmentVariable("CAM_DEBUG") == "1";

    /// <summary>解好的 PCM（16k / 16bit / 单声道小端）从这里出来。</summary>
    public event Action<byte[]>? Pcm;

    public bool IsRunning => _running;

    /// <summary>连上并开始收包；失败时给出可读原因，画面照常播。</summary>
    public bool Start(string rtspUrl, string streamName, out string error)
    {
        error = "";
        try
        {
            var uri = rtspUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(streamName);
            var hostPort = new Uri(rtspUrl);
            _tcp = new TcpClient();
            _tcp.Connect(hostPort.Host, hostPort.Port);
            _tcp.NoDelay = true;
            _stream = _tcp.GetStream();

            if (Debug) Console.WriteLine($"[aud] DESCRIBE {uri}");
            var describe = Send($"DESCRIBE {uri} RTSP/1.0\r\nCSeq: 1\r\nAccept: application/sdp\r\n\r\n");
            if (Debug) Console.WriteLine($"[aud] desc: {describe[..Math.Min(40, describe.Length)]}");
            if (!describe.StartsWith("RTSP/1.0 200", StringComparison.Ordinal))
            {
                error = "画面源不接受 RTSP 请求";
                return false;
            }

            var sdp = describe;
            var audioIndex = sdp.IndexOf("m=audio", StringComparison.Ordinal);
            if (audioIndex < 0)
            {
                error = "这台摄像机没有音频轨";
                return false;
            }

            var control = "trackID=1";
            var ctrlAt = sdp.IndexOf("a=control:", audioIndex, StringComparison.Ordinal);
            if (ctrlAt > 0)
            {
                var lineEnd = sdp.IndexOf('\n', ctrlAt);
                var value = sdp[(ctrlAt + "a=control:".Length)..(lineEnd < 0 ? sdp.Length : lineEnd)].Trim();
                if (value.Length > 0 && value != "*") control = value;
            }

            if (Debug) Console.WriteLine($"[aud] control={control} sdp<<{sdp.Replace("\r", " ").Replace("\n", " | ")}>>");
            Thread.Sleep(120);
            var audioUri = uri + "/" + control;
            if (Debug) Console.WriteLine($"[aud] SETUP {audioUri}");
            var setup = Send($"SETUP {audioUri} RTSP/1.0\r\nCSeq: 2\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n\r\n");
            if (Debug) Console.WriteLine($"[aud] setup: {setup[..Math.Min(40, setup.Length)]} {ReadHeaderValue(setup, "Transport")}");
            if (!setup.StartsWith("RTSP/1.0 200", StringComparison.Ordinal))
            {
                error = "画面源拒绝了音频轨（SETUP 失败）";
                return false;
            }

            _session = ReadHeaderValue(setup, "Session");
            if (_session.Length == 0)
            {
                error = "画面源没有给出会话标识";
                return false;
            }
            var sessionId = _session.Split(';')[0].Trim();

            if (Debug) Console.WriteLine($"[aud] PLAY session={sessionId}");
            var play = Send($"PLAY {uri} RTSP/1.0\r\nCSeq: 3\r\nSession: {sessionId}\r\nRange: npt=0.000-\r\n\r\n");
            if (Debug) Console.WriteLine($"[aud] play: {play[..Math.Min(40, play.Length)]}");
            if (!play.StartsWith("RTSP/1.0 200", StringComparison.Ordinal))
            {
                error = "画面源拒绝了播放请求（PLAY 失败）";
                return false;
            }

            _running = true;
            _reader = new Thread(() => ReadLoop(sessionId))
            {
                IsBackground = true,
                Name = "CameraAudio",
            };
            _reader.Start();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Dispose();
            return false;
        }
    }

    /// <summary>收 RTP 交织包：'$' + 通道 + 2 字节长度 + 数据；只认 PCMA 的载荷。</summary>
    private void ReadLoop(string sessionId)
    {
        var buf = new byte[4096];
        var acc = 0;
        while (_running)
        {
            int n;
            try
            {
                n = _stream!.Read(buf, acc, buf.Length - acc);
            }
            catch
            {
                break;
            }
            if (n <= 0) break;
            acc += n;

            while (acc >= 4 && buf[0] == 0x24 /* '$' */)
            {
                var length = (buf[2] << 8) | buf[3];
                if (acc < 4 + length) break;

                if (length >= 12)
                {
                    var payload = DecodeRtp(buf, 4, length);
                    if (payload != null) Pcm?.Invoke(payload);
                }

                Buffer.BlockCopy(buf, 4 + length, buf, 0, acc - 4 - length);
                acc -= 4 + length;
            }

            // 缓冲里剩的不是交织包（多半是 RTSP 的保活响应），直接清掉重新对齐
            if (acc > buf.Length - 1) acc = 0;
        }
    }

    private static byte[]? DecodeRtp(byte[] buf, int offset, int length)
    {
        var version = buf[offset] >> 6;
        if (version != 2) return null;

        var padding = (buf[offset] & 0x20) != 0;
        var extension = (buf[offset] & 0x10) != 0;
        var csrcCount = buf[offset] & 0x0F;
        var payloadType = buf[offset + 1] & 0x7F;
        if (payloadType != 97 && payloadType != 8) return null; // 97 = PCMA/16000，8 = PCMA/8000

        var header = 12 + csrcCount * 4;
        if (extension)
        {
            if (length < header + 4) return null;
            var extWords = (buf[offset + header + 2] << 8) | buf[offset + header + 3];
            header += 4 + extWords * 4;
        }
        if (length <= header) return null;

        var payloadLength = length - header;
        if (padding)
        {
            var pad = buf[offset + length - 1];
            payloadLength -= pad;
        }
        if (payloadLength <= 0) return null;

        var pcm = new byte[payloadLength * 2];
        for (var i = 0; i < payloadLength; i++)
        {
            var sample = ALawToLinear((byte)(buf[offset + header + i] ^ 0x55));
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return pcm;
    }

    /// <summary>G.711 A-law → 16bit 线性。</summary>
    private static int ALawToLinear(byte a)
    {
        var t = (a & 0x0F) << 4;
        var seg = (a & 0x70) >> 4;
        t = seg switch
        {
            0 => t + 8,
            _ => (t + 0x108) << (seg - 1),
        };
        return (a & 0x80) != 0 ? t : -t;
    }

    private string Send(string request)
    {
        try
        {
            _stream!.Write(Encoding.ASCII.GetBytes(request));
        }
        catch (Exception ex)
        {
            throw new IOException("写 RTSP 请求失败：" + ex.Message + " / " + request.Split('\r')[0], ex);
        }

        try
        {
            return ReadResponse();
        }
        catch (Exception ex)
        {
            throw new IOException("读 RTSP 响应失败：" + ex.Message + " / " + request.Split('\r')[0], ex);
        }
    }

    /// <summary>读一段 RTSP 文本响应（含 Content-Length 指定的正文）。</summary>
    private string ReadResponse()
    {
        var sb = new StringBuilder();
        var buf = new byte[1024];
        var needBody = -1;
        while (true)
        {
            var n = _stream!.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));

            var headerEnd = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd >= 0)
            {
                if (needBody < 0)
                {
                    needBody = 0;
                    foreach (var line in sb.ToString()[..headerEnd].Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            needBody = int.TryParse(line[15..].Trim(), out var v) ? v : 0;
                    }
                }

                // 正文够了就立刻收手：多读一次会把下一条请求的响应吃掉，甚至会等到对面把连接关掉
                if (sb.Length - headerEnd - 4 >= needBody) break;
            }
        }
        return sb.ToString();
    }

    private static string ReadHeaderValue(string response, string name)
    {
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) continue;
            return trimmed[(name.Length + 1)..].Trim();
        }
        return "";
    }

    public void Stop()
    {
        _running = false;
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
        try { if (_reader is { IsAlive: true }) _reader.Join(300); } catch { }
        _reader = null;

        // 让画面源及时释放这条会话
        try
        {
            if (_session.Length > 0)
            {
                _session = "";
            }
        }
        catch
        {
            // 收尾失败无所谓
        }
    }

    public void Dispose() => Stop();
}
