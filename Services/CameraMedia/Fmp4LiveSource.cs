// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

namespace MiHomeHub.Services.CameraMedia;

/// <summary>
/// 把内置画面源吐出的实时 fMP4（HEVC）在线拆开：先拿到 hvcC 参数集，之后一帧一个采样。
/// 之所以自己拆而不是交给 Media Foundation 的 SourceReader：MF 读实时 HTTP fMP4 会卡住，
/// 字节在流、帧却迟迟不来（实测解码 0.3fps 且画面错乱）。自己拆只依赖 HTTP 流本身。
/// </summary>
public sealed class Fmp4LiveSource : IDisposable
{
    public readonly record struct Packet(byte[] Data, long Dts, long Duration, bool Key);

    private const int MaxBox = 32 * 1024 * 1024;

    private HttpClient? _http;
    private HttpResponseMessage? _response;
    private Stream? _stream;
    private byte[] _buf = new byte[1 << 16];
    private int _len;
    private long _base = 0;
    private bool _stopped;
    private long _dts;
    private static readonly bool Debug = Environment.GetEnvironmentVariable("CAM_DEBUG") == "1";
    private int _boxCount;

    private readonly List<Packet> _queue = new();
    private (long start, List<(long off, int size, bool key, long dur)> items)? _pending;

    private readonly List<int> _frameSampleSizes = new();

    public byte[]? Extradata { get; private set; }
    public int NalLengthSize { get; private set; } = 4;
    public byte[] ParameterSets { get; private set; } = Array.Empty<byte>();
    public int Timescale { get; private set; } = 90000;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public bool Open(string url, out string error)
    {
        error = "";
        try
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var resp = _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            _response = resp;
            _stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }

        // 读到 moov 为止，此前的 styp/sidx/ftyp 直接跳过。
        while (Extradata == null)
        {
            if (!NextBox(out var type, out _, out var size, out var payload, out var payloadSize))
            {
                error = "画面源提前结束，没拿到参数集";
                return false;
            }

            if (type == "moov") ParseMoov(payload, payloadSize);
            Drop(size);
        }

        return Extradata is { Length: > 0 };
    }

    /// <summary>取下一个采样，流断了返回 false。</summary>
    public bool TryReadPacket(out Packet packet)
    {
        while (true)
        {
            if (_queue.Count > 0)
            {
                packet = _queue[0];
                _queue.RemoveAt(0);
                return true;
            }

            if (_stopped) { packet = default; return false; }

            if (!NextBox(out var type, out var start, out var size, out var payload, out var payloadSize))
            {
                packet = default;
                return false;
            }

            if (type == "moof") ParseMoof(start, payload, payloadSize);
            else if (type == "mdat") ExtractSamples(payload, payloadSize);

            Drop(size);
        }
    }

    // ── 字节窗口 ─────────────────────────────────────────────

    /// <summary>读满一个完整 box，返回类型、起始绝对偏移、总长、负载偏移与长度。</summary>
    private bool NextBox(out string type, out long start, out long size, out int payload, out int payloadSize)
    {
        type = "";
        start = _base;
        size = 0;
        payload = 0;
        payloadSize = 0;

        if (!Ensure(8)) return false;

        start = _base;
        var rawSize = ReadU32(0);
        type = Ascii(4, 4);

        var header = 8;
        long boxSize = rawSize;
        if (rawSize == 1)
        {
            if (!Ensure(16)) return false;
            boxSize = (long)ReadU64(8);
            header = 16;
        }
        else if (rawSize == 0)
        {
            // 到流末尾为止，实时流不该出现，按错误处理
            return false;
        }

        if (boxSize < header || boxSize > MaxBox) return false;
        if (!Ensure((int)boxSize)) return false;

        if (Debug && _boxCount < 20) Console.WriteLine($"[src] box {type} size={boxSize} @{start}");
        _boxCount++;

        size = boxSize;
        payload = header;
        payloadSize = (int)boxSize - header;
        return true;
    }

    private bool Ensure(int count)
    {
        while (_len < count)
        {
            Compact(count);
            var chunk = (_buf.Length - _len) >= 4096 ? _buf.Length - _len : Math.Max(4096, count - _len + 4096);
            if (chunk > _buf.Length - _len) Array.Resize(ref _buf, _len + chunk);

            int read;
            try
            {
                read = _stream!.Read(_buf, _len, _buf.Length - _len);
            }
            catch
            {
                _stopped = true;
                return false;
            }

            if (read <= 0)
            {
                _stopped = true;
                return false;
            }

            _len += read;
        }
        return true;
    }

    /// <summary>丢掉已处理的前 n 个字节。</summary>
    private void Drop(long n)
    {
        if (n >= _len) { _len = 0; }
        else
        {
            Buffer.BlockCopy(_buf, (int)n, _buf, 0, _len - (int)n);
            _len -= (int)n;
        }
        _base += n;
    }

    private void Compact(int keep)
    {
        // 窗口太大时先腾出空间，避免无谓扩容
        if (_len + 65536 < _buf.Length) return;
        var cap = Math.Max(keep + 65536, _len + 65536);
        if (cap > _buf.Length) Array.Resize(ref _buf, cap);
    }

    // ── box 解析 ─────────────────────────────────────────────

    private void ParseMoov(int payload, int size)
    {
        if (Debug) Console.WriteLine("[src] moov children");
        DumpChildren(payload, payload + size);
        var end = payload + size;
        foreach (var (type, ps, psize) in Children(payload, end))
        {
            if (type != "trak") continue;
            var sub = Children(ps, ps + psize);
            int? mdia = null;
            foreach (var (t2, p2, s2) in sub)
            {
                if (t2 == "mdia") { mdia = p2; ParseMdia(p2, p2 + s2); }
            }
            if (Extradata != null) return;
        }
    }

    private void ParseMdia(int start, int end)
    {
        foreach (var (type, ps, psize) in Children(start, end))
        {
            if (type == "mdhd")
            {
                var version = _buf[ps];
                var t = version == 1 ? ReadU32(ps + 20) : ReadU32(ps + 12);
                if (t > 0) Timescale = (int)t;
            }
            else if (type == "minf")
            {
                foreach (var (t2, p2, s2) in Children(ps, ps + psize))
                {
                    if (t2 != "stbl") continue;
                    foreach (var (t3, p3, s3) in Children(p2, p2 + s2))
                    {
                        if (t3 == "stsd") ParseStsd(p3, p3 + s3);
                    }
                }
            }
        }
    }

    private void ParseStsd(int start, int end)
    {
        if (Debug) Console.WriteLine($"[src] stsd {end - start}B");
        // version/flags(4) + entry_count(4)
        var p = start + 8;
        while (p + 8 <= end)
        {
            var entrySize = (int)ReadU32(p);
            if (entrySize < 8 || p + entrySize > end) return;

            // 视觉采样条目固定字段有 78 字节，子 box 位置不固定，直接在条目里扫 hvcC
            for (var q = p + 8; q + 8 <= p + entrySize; q++)
            {
                if (Ascii(q + 4, 4) != "hvcC") continue;
                var cfgSize = (int)ReadU32(q);
                if (cfgSize < 8 || q + cfgSize > p + entrySize) continue;

                Extradata = new byte[cfgSize - 8];
                Buffer.BlockCopy(_buf, q + 8, Extradata, 0, cfgSize - 8);
                ParseHvcC(Extradata);
                if (Debug) Console.WriteLine($"[src] extradata hvcC {Extradata.Length}B nal={NalLengthSize} 参数集={ParameterSets.Length}B");
                return;
            }

            p += entrySize;
        }
    }

    private void ParseMoof(long moofStart, int payload, int size)
    {
        var items = new List<(long off, int size, bool key, long dur)>();
        long baseData = moofStart;
        var defaultDur = 0;
        var defaultSize = 0;
        var defaultFlags = 0;

        foreach (var (type, ps, psize) in Children(payload, payload + size))
        {
            if (type != "traf") continue;

            long trafBase = baseData;
            var samples = new List<(int size, bool key, long dur)>();
            int? dataOffset = null;

            foreach (var (t2, p2, s2) in Children(ps, ps + psize))
            {
                switch (t2)
                {
                    case "tfhd":
                    {
                        var flags = ReadFlags(p2);
                        var track = (int)ReadU32(p2 + 4);
                        if (track != 1 && _frameSampleSizes.Count > 0) { /* 仍按视频轨处理 */ }
                        var q = p2 + 8;
                        if ((flags & 0x01) != 0) { trafBase = (long)ReadU64(q); q += 8; }
                        if ((flags & 0x02) != 0) q += 4;
                        if ((flags & 0x08) != 0) { defaultDur = (int)ReadU32(q); q += 4; }
                        if ((flags & 0x10) != 0) { defaultSize = (int)ReadU32(q); q += 4; }
                        if ((flags & 0x20) != 0) { defaultFlags = (int)ReadU32(q); q += 4; }
                        break;
                    }
                    case "trun":
                    {
                        var flags = ReadFlags(p2);
                        var count = (int)ReadU32(p2 + 4);
                        var q = p2 + 8;
                        if ((flags & 0x01) != 0) { dataOffset = (int)ReadU32(q); q += 4; }
                        if ((flags & 0x04) != 0) { defaultFlags = (int)ReadU32(q); q += 4; }

                        for (var i = 0; i < count; i++)
                        {
                            var dur = defaultDur;
                            var sz = defaultSize;
                            var fl = defaultFlags;
                            if ((flags & 0x100) != 0) { dur = (int)ReadU32(q); q += 4; }
                            if ((flags & 0x200) != 0) { sz = (int)ReadU32(q); q += 4; }
                            if ((flags & 0x400) != 0) { fl = (int)ReadU32(q); q += 4; }
                            if ((flags & 0x800) != 0) q += 4;
                            samples.Add((sz, (fl & 0x00010000) == 0, dur));
                        }
                        break;
                    }
                    case "tfdt":
                    {
                        var version = _buf[p2];
                        var t = version == 1 && p2 + 12 <= _buf.Length ? (long)ReadU64(p2 + 4) : (long)ReadU32(p2 + 4);
                        if (t > 0 && _dts == 0) _dts = t;
                        break;
                    }
                }
            }

            if (samples.Count == 0) continue;
            var dataStart = trafBase + (dataOffset ?? 0);
            var cursor = dataStart;
            foreach (var (sz, key, dur) in samples)
            {
                items.Add((cursor, sz, key, dur));
                cursor += sz;
            }
        }

        if (items.Count > 0) _pending = (moofStart, items);
    }

    private void ExtractSamples(int payload, int size)
    {
        if (_pending == null) return;
        var (_, items) = _pending.Value;
        _pending = null;

        foreach (var (off, sz, key, dur) in items)
        {
            var index = off - _base;
            if (index < 0 || index + sz > _len) continue;
            if (sz <= 0) continue;

            var data = new byte[sz];
            Buffer.BlockCopy(_buf, (int)index, data, 0, sz);
            var annexB = ToAnnexB(data, key);
            _queue.Add(new Packet(annexB, _dts, dur > 0 ? dur : 0, key));
            _dts += dur > 0 ? dur : 0;
            if (_dts == 0) _dts = 1;
        }
    }

    /// <summary>把 fMP4 的长度前缀 NAL 改成 Annex B（起始码），关键帧前面补参数集。</summary>
    private byte[] ToAnnexB(byte[] sample, bool key)
    {
        using var ms = new MemoryStream(sample.Length + 256);
        if (key && ParameterSets.Length > 0)
        {
            ms.Write(ParameterSets, 0, ParameterSets.Length);
        }

        var p = 0;
        while (p + NalLengthSize <= sample.Length)
        {
            var len = 0;
            for (var i = 0; i < NalLengthSize; i++) len = (len << 8) | sample[p + i];
            p += NalLengthSize;
            if (len <= 0 || p + len > sample.Length) break;
            ms.Write(StartCode, 0, StartCode.Length);
            ms.Write(sample, p, len);
            p += len;
        }

        return ms.ToArray();
    }

    private static readonly byte[] StartCode = { 0, 0, 0, 1 };

    /// <summary>从 hvcC 里取 NAL 长度字节数与 VPS/SPS/PPS，拼成可前置的 Annex B 段。</summary>
    private void ParseHvcC(byte[] hvcC)
    {
        try
        {
            if (hvcC.Length < 23) return;
            NalLengthSize = (hvcC[21] & 0x03) + 1;

            var p = 22;
            var arrays = hvcC[p++];
            var sets = new List<byte>();
            for (var i = 0; i < arrays; i++)
            {
                var nalType = hvcC[p++] & 0x3F;
                var count = (hvcC[p] << 8) | hvcC[p + 1]; p += 2;
                for (var j = 0; j < count; j++)
                {
                    var len = (hvcC[p] << 8) | hvcC[p + 1]; p += 2;
                    if (p + len > hvcC.Length) return;
                    if (nalType is 32 or 33 or 34)
                    {
                        sets.AddRange(StartCode);
                        for (var k = 0; k < len; k++) sets.Add(hvcC[p + k]);
                    }
                    p += len;
                }
            }
            ParameterSets = sets.ToArray();
        }
        catch
        {
            // 参数集不规范就当没有，关键帧前面不补
        }
    }

    // ── 工具 ────────────────────────────────────────────────

    private IEnumerable<(string type, int start, int size)> Children(int start, int end)
    {
        var p = start;
        while (p + 8 <= end)
        {
            var sz = (int)ReadU32(p);
            if (sz < 8 || p + sz > end) yield break;
            yield return (Ascii(p + 4, 4), p + 8, sz - 8);
            p += sz;
        }
    }

    private void DumpChildren(int start, int end)
    {
        foreach (var (type, ps, psize) in Children(start, end))
            if (Debug) Console.WriteLine($"[src]   child {type} {psize}B");
    }

    private uint ReadFlags(int p) => (uint)(_buf[p + 1] << 16 | _buf[p + 2] << 8 | _buf[p + 3]);

    private uint ReadU32(int p) =>
        (uint)(_buf[p] << 24 | _buf[p + 1] << 16 | _buf[p + 2] << 8 | _buf[p + 3]);

    private ulong ReadU64(int p) => ((ulong)ReadU32(p) << 32) | ReadU32(p + 4);

    private string Ascii(int p, int n) => System.Text.Encoding.ASCII.GetString(_buf, p, n);

    public void Dispose()
    {
        _stopped = true;
        try { _stream?.Dispose(); } catch { }
        try { _response?.Dispose(); } catch { }
        try { _http?.Dispose(); } catch { }
        _stream = null;
        _http = null;
    }
}
