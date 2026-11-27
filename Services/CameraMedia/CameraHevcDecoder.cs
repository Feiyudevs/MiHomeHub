// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MiHomeHub.Services.CameraMedia;

/// <summary>
/// 摄像机画面解码：自己拆 fMP4（<see cref="Fmp4LiveSource"/>），把每个 NAL 交给随包内嵌的
/// libde265 软解，取到 YUV 平面后在内存里转 BGRA。
/// 全程不落临时文件、不起第二个进程、不依赖 ffmpeg / 外置播放器 / 系统 HEVC 组件。
///
/// 之前走过的两条路都不可靠：MF 的 SourceReader 读实时 HTTP fMP4 只出两三帧就停；
/// 自己喂系统 HEVC 解码器 MFT 时，机器上枚举到的解码器一律不接受 HEVC 输入类型。
/// </summary>
public sealed class CameraHevcDecoder : IDisposable
{
    private const string Dll = "libde265-0";

    [DllImport(Dll)] private static extern IntPtr de265_new_decoder();
    [DllImport(Dll)] private static extern int de265_free_decoder(IntPtr dec);
    [DllImport(Dll)] private static extern int de265_start_worker_threads(IntPtr dec, int n);
    [DllImport(Dll)] private static extern int de265_push_NAL(IntPtr dec, byte[] data, int length, long pts, IntPtr user);
    [DllImport(Dll)] private static extern int de265_decode(IntPtr dec, out int more);
    [DllImport(Dll)] private static extern IntPtr de265_get_next_picture(IntPtr dec);
    [DllImport(Dll)] private static extern void de265_release_next_picture(IntPtr dec);
    [DllImport(Dll)] private static extern int de265_get_image_width(IntPtr img, int channel);
    [DllImport(Dll)] private static extern int de265_get_image_height(IntPtr img, int channel);
    [DllImport(Dll)] private static extern IntPtr de265_get_image_plane(IntPtr img, int channel, out int stride);
    [DllImport(Dll)] private static extern int de265_get_chroma_format(IntPtr img);
    [DllImport(Dll)] private static extern int de265_get_bits_per_pixel(IntPtr img, int channel);

    private readonly string _url;
    private Fmp4LiveSource? _source;
    private IntPtr _ctx;
    private int _width;
    private int _height;
    private bool _eof;
    private int _frameCount;
    private static readonly bool Debug = Environment.GetEnvironmentVariable("CAM_DEBUG") == "1";

    public CameraHevcDecoder(string url) => _url = url;

    public int Width => _width;
    public int Height => _height;
    public bool IsOpen => _ctx != IntPtr.Zero;

    /// <summary>当前走内置软解，不依赖显卡。</summary>
    public static bool CanUseHardware() => false;

    /// <summary>解码能力随包内置，不依赖系统组件。</summary>
    public static bool CanUseSoftwareMft() => true;

    public bool Open(out string error)
    {
        error = "";
        try
        {
            if (!CameraNative.Ensure())
            {
                error = "内置解码库落盘失败";
                return false;
            }

            var source = new Fmp4LiveSource();
            if (!source.Open(_url, out var srcError))
            {
                error = "读取画面流失败：" + srcError;
                source.Dispose();
                return false;
            }
            _source = source;

            var size = HvcC.ReadSize(source.Extradata!);
            _width = size.width;
            _height = size.height;

            _ctx = de265_new_decoder();
            if (_ctx == IntPtr.Zero)
            {
                error = "内置解码器初始化失败";
                return false;
            }
            de265_start_worker_threads(_ctx, 2);
            if (Debug) Console.WriteLine($"[dec] libde265 就绪 {_width}x{_height}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// 取一帧，转成 BGRA 写进 <paramref name="bgra"/>（调用方按 w*h*4 分配）。
    /// 返回 false 表示流结束或已被取消。
    /// </summary>
    public unsafe bool TryReadFrame(byte[] bgra, CancellationToken ct)
    {
        if (_ctx == IntPtr.Zero || _source == null) return false;

        while (!ct.IsCancellationRequested)
        {
            if (TakePicture(bgra)) return true;
            if (_eof) return false;

            if (!_source.TryReadPacket(out var packet))
            {
                _eof = true;
                continue;
            }

            FeedAnnexB(packet.Data);
        }

        return false;
    }

    /// <summary>把一帧的 Annex B 数据拆成单个 NAL 推进解码器，再跑一次解码。</summary>
    private void FeedAnnexB(byte[] data)
    {
        var p = 0;
        while (p + 3 <= data.Length)
        {
            if (!(data[p] == 0 && data[p + 1] == 0 && data[p + 2] == 1))
            {
                p++;
                continue;
            }

            p += 3;
            if (p < data.Length && data[p] == 0) p++; // 四字节起始码多出来的那个 0
            var start = p;
            if (start >= data.Length) break;

            var next = start;
            while (next + 3 <= data.Length)
            {
                if (data[next] == 0 && data[next + 1] == 0 && data[next + 2] == 1) break;
                next++;
            }

            var len = Math.Min(next, data.Length) - start;
            if (len > 0)
            {
                // push 只认裸 NAL，必须从 start 拷出来，不能把整包从 0 位置传进去
                var nal = new byte[len];
                Buffer.BlockCopy(data, start, nal, 0, len);
                de265_push_NAL(_ctx, nal, len, 0, IntPtr.Zero);
            }
            p = next;
        }

        // 一个访问单元推完后跑解码，直到解码器说“还要更多输入”
        for (var guard = 0; guard < 64; guard++)
        {
            var err = de265_decode(_ctx, out var more);
            // 8 = DE265_ERROR_WAITING_FOR_INPUT_DATA，属于正常“还得再喂数据”
            if (err == 8) break;
            if (err != 0)
            {
                if (Debug) Console.WriteLine($"[dec] decode err {err}");
                break;
            }
            if (more == 0) break;
        }
    }

    /// <summary>取一张已经解好的画面并转换；没有就返回 false。</summary>
    private unsafe bool TakePicture(byte[] bgra)
    {
        var img = de265_get_next_picture(_ctx);
        if (img == IntPtr.Zero) return false;

        try
        {
            var w = de265_get_image_width(img, 0);
            var h = de265_get_image_height(img, 0);
            if (w <= 0 || h <= 0) return false;
            if (de265_get_chroma_format(img) != 1 || de265_get_bits_per_pixel(img, 0) != 8) return false;

            if (w != _width || h != _height) { _width = w; _height = h; }

            var yPlane = de265_get_image_plane(img, 0, out var yStride);
            var uPlane = de265_get_image_plane(img, 1, out var uStride);
            var vPlane = de265_get_image_plane(img, 2, out var vStride);
            if (yPlane == IntPtr.Zero || uPlane == IntPtr.Zero || vPlane == IntPtr.Zero) return false;

            ConvertYuv420((byte*)yPlane, yStride, (byte*)uPlane, uStride, (byte*)vPlane, vStride, bgra, w, h);
            if (Debug && _frameCount < 5)
                Console.WriteLine($"[dec] frame {_frameCount} {w}x{h} y={yStride} u={uStride} v={vStride}");
            _frameCount++;
            return true;
        }
        finally
        {
            de265_release_next_picture(_ctx);
        }
    }

    /// <summary>BT.709 limited range 的 4:2:0 8bit → BGRA，按行并行。</summary>
    private static unsafe void ConvertYuv420(byte* yPlane, int yStride, byte* uPlane, int uStride,
        byte* vPlane, int vStride, byte[] bgra, int w, int h)
    {
        var dstStride = w * 4;
        fixed (byte* dst = bgra)
        {
            var d0 = dst;
            Parallel.For(0, h, y =>
            {
                var rowY = yPlane + y * yStride;
                var rowU = uPlane + (y >> 1) * uStride;
                var rowV = vPlane + (y >> 1) * vStride;
                var rowD = d0 + y * dstStride;
                for (var x = 0; x < w; x++)
                {
                    var yy = rowY[x] - 16;
                    var u = rowU[x >> 1] - 128;
                    var v = rowV[x >> 1] - 128;
                    var r = Clip((298 * yy + 409 * v + 128) >> 8);
                    var g = Clip((298 * yy - 100 * u - 208 * v + 128) >> 8);
                    var b = Clip((298 * yy + 516 * u + 128) >> 8);
                    var o = x * 4;
                    rowD[o] = (byte)b;
                    rowD[o + 1] = (byte)g;
                    rowD[o + 2] = (byte)r;
                    rowD[o + 3] = 255;
                }
            });
        }
    }

    private static int Clip(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    public void Dispose()
    {
        try { _source?.Dispose(); } catch { }
        try { if (_ctx != IntPtr.Zero) de265_free_decoder(_ctx); } catch { }
        _source = null;
        _ctx = IntPtr.Zero;
    }
}

/// <summary>从 hvcC 参数集里读 SPS，拿初始画面尺寸（最终以真正解出来的画面为准）。</summary>
internal static class HvcC
{
    public static (int width, int height) ReadSize(byte[] hvcC)
    {
        try
        {
            if (hvcC.Length < 23) return (1920, 1088);

            var p = 22;
            var arrays = hvcC[p++];
            for (var i = 0; i < arrays; i++)
            {
                var nalType = hvcC[p++] & 0x3F;
                var count = ReadU16(hvcC, p); p += 2;
                for (var j = 0; j < count; j++)
                {
                    var len = ReadU16(hvcC, p); p += 2;
                    if (nalType == 33 /* SPS */ && len > 0 && p + len <= hvcC.Length)
                        return ParseSps(hvcC, p, len);
                    p += len;
                }
            }
        }
        catch
        {
            // 参数集不规范时退回默认
        }
        return (1920, 1088);
    }

    private static (int width, int height) ParseSps(byte[] data, int offset, int length)
    {
        var bits = new BitReader(data, offset, length);
        bits.Skip(16);                        // NAL 头
        bits.Skip(4);                         // sps_video_parameter_set_id
        var maxSubLayers = bits.ReadUe();
        bits.Skip(1);                         // sps_temporal_id_nesting_flag
        bits.Skip(96);                        // profile_tier_level 通用部分
        if (maxSubLayers > 0) return (1920, 1088);
        bits.ReadUe();                        // sps_seq_parameter_set_id
        var chroma = bits.ReadUe();
        if (chroma == 3) bits.Skip(1);
        var w = bits.ReadUe();
        var h = bits.ReadUe();
        if (bits.ReadFlag())
        {
            var left = bits.ReadUe();
            var right = bits.ReadUe();
            var top = bits.ReadUe();
            var bottom = bits.ReadUe();
            var subW = chroma is 1 or 2 ? 2 : 1;
            var subH = chroma == 1 ? 2 : 1;
            w -= (left + right) * subW;
            h -= (top + bottom) * subH;
        }
        return (w, h);
    }

    private static int ReadU16(byte[] b, int p) => (b[p] << 8) | b[p + 1];

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _pos;

        public BitReader(byte[] data, int offset, int length)
        {
            _data = data;
            _pos = offset * 8;
            _end = (offset + length) * 8;
        }

        public bool ReadFlag() => ReadBit() != 0;

        public int ReadBit()
        {
            if (_pos >= _end) throw new InvalidOperationException("参数集越界");
            var bit = (_data[_pos >> 3] >> (7 - (_pos & 7))) & 1;
            _pos++;
            return bit;
        }

        public void Skip(int n) => _pos += n;

        public int ReadUe()
        {
            var zeros = 0;
            while (ReadBit() == 0)
            {
                if (++zeros > 16) throw new InvalidOperationException("参数集异常");
            }
            var value = 1;
            for (var i = 0; i < zeros; i++) value = (value << 1) | ReadBit();
            return value - 1;
        }
    }
}
