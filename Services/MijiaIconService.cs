using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MiHome.Services;

/// <summary>
/// 设备真机图：米家云的设备列表接口不带图标字段，按 model 到米家产品库取 realIcon 产品图。
/// realIcon 绝大多数托管在 iotweb-user-center（少数在 iotweb-product-center），且带签名参数，
/// 必须整段 URL 才能通过 CDN 校验；无该字段的型号返回 null，卡片回退到矢量图标。
/// 结果同时写入 %APPDATA%\Feiyu\MiHome\icons\ 磁盘缓存：命中期内直接读盘，过期后对比 URL
/// 再决定是否重新下载，产品图更新时能自动换新。
/// 磁盘与内存都用接口原图，不做裁切/抠图（白描边等瑕疵原样保留）；仅在源 DPI≠96 时重包，防止图标显示过小。
/// </summary>
public static class MijiaIconService
{
    private const string ProductApi = "https://home.mi.com/cgi-op/api/v1/baike/v2/product?model=";

    /// <summary>
    /// 常驻最长边：卡片 48px、抽屉 56px，按 3 倍高分屏 ≈ 168px。
    /// 加载时用三角滤波把 480px 原图真实缩到该尺寸（写入 WriteableBitmap），不是靠 UI 再拉大原图。
    /// </summary>
    private const int MaxEdge = 168;

    /// <summary>输出位图统一 96 DPI：Size（DIP）= 像素数，不再继承源图 pHYs（常是 72 或被当成 2835）。</summary>
    private static readonly Vector OutputDpi = new(96, 96);

    /// <summary>磁盘缓存有效期内不打产品库；过期后若 CDN 路径未变则续期，变了才重下。</summary>
    private static readonly TimeSpan DiskTtl = TimeSpan.FromDays(7);

    private static readonly string CacheDir = Path.Combine(AppPaths.Dir, "icons");
    private static readonly string IndexPath = Path.Combine(CacheDir, "index.json");

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static MijiaIconService() =>
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");

    /// <summary>同一型号只查一次（GetOrAdd 的工厂返回 Task，天然合并了并发重复请求）。</summary>
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed class IndexEntry
    {
        public string Url { get; set; } = "";
        public long SavedAt { get; set; }
        public string File { get; set; } = "";
    }

    private sealed class IndexRoot
    {
        public Dictionary<string, IndexEntry> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object IndexLock = new();
    private static IndexRoot? _index;

    public static Task<Bitmap?> GetAsync(string model) =>
        string.IsNullOrEmpty(model)
            ? Task.FromResult<Bitmap?>(null)
            : Cache.GetOrAdd(model, LoadAsync);

    private static async Task<Bitmap?> LoadAsync(string model)
    {
        try
        {
            var cached = TryReadDisk(model, out var entry);
            if (entry != null && DateTime.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(entry.SavedAt) < DiskTtl)
                return cached;

            var json = await Http.GetStringAsync(ProductApi + Uri.EscapeDataString(model));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("realIcon", out var icon) ||
                icon.ValueKind != JsonValueKind.String)
            {
                // 产品库没有该型号：有过期磁盘图就继续用，避免闪一下再变回矢量图标
                return cached;
            }

            var url = icon.GetString() ?? "";
            if (string.IsNullOrEmpty(url)) return cached;

            // realIcon 带 CDN 签名，每次请求 query 都会变；只比 path 判断是否换图
            if (cached != null && entry != null && UrlPath(entry.Url) == UrlPath(url))
            {
                TouchIndex(model, url, entry.File);
                return cached;
            }

            // realIcon 就是产品实物图，不能按 CDN 域名过滤（绝大多数在 iotweb-user-center）。
            // 也不能裁掉 ? 后的签名参数：该 CDN 校验签名，去签名会 403，必须整段 URL 下载。
            var bytes = await Http.GetByteArrayAsync(url);
            WriteDisk(model, url, bytes);
            return FromBytes(bytes);
        }
        catch
        {
            // 网络失败时尽量用磁盘图，不挡卡片
            return TryReadDisk(model, out _);
        }
    }

    private static string UrlPath(string url)
    {
        var i = url.IndexOf('?');
        return i < 0 ? url : url[..i];
    }

    private static Bitmap FromBytes(byte[] bytes)
    {
        var src = new Bitmap(new MemoryStream(bytes));
        try
        {
            // 不裁切、不抠图、不去白描边。只做两件事：
            // 1) 合法预乘（硬抠图边缘缩放时才不会闪脏色）
            // 2) 整图高质量缩到 MaxEdge，把 0/255 硬边的锯齿磨成半透明过渡
            var w = src.PixelSize.Width;
            var h = src.PixelSize.Height;
            if (w <= 0 || h <= 0) return src;

            var scale = Math.Min(1d, (double)MaxEdge / Math.Max(w, h));
            var outW = Math.Max(1, (int)Math.Round(w * scale));
            var outH = Math.Max(1, (int)Math.Round(h * scale));
            var needCopy = outW != w || outH != h ||
                           Math.Abs(src.Dpi.X - OutputDpi.X) >= 0.5 ||
                           Math.Abs(src.Dpi.Y - OutputDpi.Y) >= 0.5;
            if (!needCopy) return src;

            var srcStride = w * 4;
            var srcPx = new byte[srcStride * h];
            var handle = GCHandle.Alloc(srcPx, GCHandleType.Pinned);
            try
            {
                src.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), srcStride * h, srcStride);
            }
            finally
            {
                handle.Free();
            }

            NormalizePremulInPlace(srcPx, srcStride * h, src.AlphaFormat == AlphaFormat.Unpremul);

            byte[] outPx;
            if (outW == w && outH == h)
            {
                outPx = srcPx;
            }
            else
            {
                outPx = new byte[outW * 4 * outH];
                TriangleResize(srcPx, w, h, outPx, outW, outH);
            }

            var dst = new WriteableBitmap(new PixelSize(outW, outH), OutputDpi,
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = dst.Lock())
            {
                var outStride = outW * 4;
                for (var y = 0; y < outH; y++)
                    Marshal.Copy(outPx, y * outStride, fb.Address + y * fb.RowBytes, outStride);
            }

            return dst;
        }
        catch
        {
            return src;
        }
    }

    /// <summary>合法预乘：a=0 清零；Unpremul×a；Premul 把通道钳到 ≤a。不删像素、不改轮廓语义。</summary>
    private static void NormalizePremulInPlace(byte[] px, int len, bool fromUnpremul)
    {
        for (var i = 0; i < len; i += 4)
        {
            var a = px[i + 3];
            if (a == 0)
            {
                px[i] = px[i + 1] = px[i + 2] = 0;
                continue;
            }

            if (fromUnpremul)
            {
                px[i] = (byte)(px[i] * a / 255);
                px[i + 1] = (byte)(px[i + 1] * a / 255);
                px[i + 2] = (byte)(px[i + 2] * a / 255);
            }
            else
            {
                if (px[i] > a) px[i] = a;
                if (px[i + 1] > a) px[i + 1] = a;
                if (px[i + 2] > a) px[i + 2] = a;
            }
        }
    }

    /// <summary>可分离三角滤波缩放（整图，预乘空间）：硬 alpha 边缩下去会变软，去掉阶梯锯齿。</summary>
    private static void TriangleResize(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
    {
        var ratioX = (double)sw / dw;
        var ratioY = (double)sh / dh;
        var radiusX = Math.Max(1d, ratioX);
        var radiusY = Math.Max(1d, ratioY);
        var tmp = new byte[dw * 4 * sh];

        for (var y = 0; y < sh; y++)
        {
            var row = y * sw * 4;
            var o = y * dw * 4;
            for (var x = 0; x < dw; x++)
            {
                var center = (x + 0.5) * ratioX - 0.5;
                var left = (int)Math.Floor(center - radiusX + 1);
                var right = (int)Math.Ceiling(center + radiusX - 1);
                if (left < 0) left = 0;
                if (right > sw - 1) right = sw - 1;
                double wSum = 0, b = 0, g = 0, r = 0, a = 0;
                for (var sx = left; sx <= right; sx++)
                {
                    var t = Math.Abs(sx - center) / radiusX;
                    var w = t >= 1 ? 0 : 1 - t;
                    if (w <= 0) continue;
                    var p = row + sx * 4;
                    wSum += w;
                    b += src[p] * w;
                    g += src[p + 1] * w;
                    r += src[p + 2] * w;
                    a += src[p + 3] * w;
                }

                if (wSum <= 0)
                {
                    var pc = row + Math.Clamp((int)Math.Round(center), 0, sw - 1) * 4;
                    tmp[o + x * 4] = src[pc];
                    tmp[o + x * 4 + 1] = src[pc + 1];
                    tmp[o + x * 4 + 2] = src[pc + 2];
                    tmp[o + x * 4 + 3] = src[pc + 3];
                }
                else
                {
                    var inv = 1 / wSum;
                    tmp[o + x * 4] = (byte)Math.Clamp((int)(b * inv + 0.5), 0, 255);
                    tmp[o + x * 4 + 1] = (byte)Math.Clamp((int)(g * inv + 0.5), 0, 255);
                    tmp[o + x * 4 + 2] = (byte)Math.Clamp((int)(r * inv + 0.5), 0, 255);
                    tmp[o + x * 4 + 3] = (byte)Math.Clamp((int)(a * inv + 0.5), 0, 255);
                }
            }
        }

        for (var y = 0; y < dh; y++)
        {
            var center = (y + 0.5) * ratioY - 0.5;
            var top = (int)Math.Floor(center - radiusY + 1);
            var bottom = (int)Math.Ceiling(center + radiusY - 1);
            if (top < 0) top = 0;
            if (bottom > sh - 1) bottom = sh - 1;
            var o = y * dw * 4;
            for (var x = 0; x < dw; x++)
            {
                double wSum = 0, b = 0, g = 0, r = 0, a = 0;
                for (var sy = top; sy <= bottom; sy++)
                {
                    var t = Math.Abs(sy - center) / radiusY;
                    var w = t >= 1 ? 0 : 1 - t;
                    if (w <= 0) continue;
                    var p = sy * dw * 4 + x * 4;
                    wSum += w;
                    b += tmp[p] * w;
                    g += tmp[p + 1] * w;
                    r += tmp[p + 2] * w;
                    a += tmp[p + 3] * w;
                }

                if (wSum <= 0)
                {
                    var sy = Math.Clamp((int)Math.Round(center), 0, sh - 1);
                    var pc = sy * dw * 4 + x * 4;
                    dst[o + x * 4] = tmp[pc];
                    dst[o + x * 4 + 1] = tmp[pc + 1];
                    dst[o + x * 4 + 2] = tmp[pc + 2];
                    dst[o + x * 4 + 3] = tmp[pc + 3];
                }
                else
                {
                    var inv = 1 / wSum;
                    dst[o + x * 4] = (byte)Math.Clamp((int)(b * inv + 0.5), 0, 255);
                    dst[o + x * 4 + 1] = (byte)Math.Clamp((int)(g * inv + 0.5), 0, 255);
                    dst[o + x * 4 + 2] = (byte)Math.Clamp((int)(r * inv + 0.5), 0, 255);
                    dst[o + x * 4 + 3] = (byte)Math.Clamp((int)(a * inv + 0.5), 0, 255);
                }
            }
        }
    }

    private static string FileNameFor(string model)
    {
        var safe = new StringBuilder(model.Length);
        foreach (var ch in model)
            safe.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        var sha = SHA1.HashData(Encoding.UTF8.GetBytes(model.ToLowerInvariant()));
        return Convert.ToHexString(sha)[..16] + "_" + safe + ".bin";
    }

    private static IndexRoot LoadIndex()
    {
        lock (IndexLock)
        {
            if (_index != null) return _index;
            try
            {
                if (File.Exists(IndexPath))
                    _index = JsonSerializer.Deserialize<IndexRoot>(File.ReadAllText(IndexPath)) ?? new IndexRoot();
                else
                    _index = new IndexRoot();
            }
            catch
            {
                _index = new IndexRoot();
            }
            return _index;
        }
    }

    private static void SaveIndex()
    {
        lock (IndexLock)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(IndexPath, JsonSerializer.Serialize(_index ?? new IndexRoot()));
            }
            catch
            {
                // 写索引失败不影响本次运行
            }
        }
    }

    private static Bitmap? TryReadDisk(string model, out IndexEntry? entry)
    {
        entry = null;
        try
        {
            var idx = LoadIndex();
            if (!idx.Models.TryGetValue(model, out entry) || string.IsNullOrEmpty(entry.File)) return null;
            var path = Path.Combine(CacheDir, entry.File);
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return bytes.Length == 0 ? null : FromBytes(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteDisk(string model, string url, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var file = FileNameFor(model);
            File.WriteAllBytes(Path.Combine(CacheDir, file), bytes);
            var idx = LoadIndex();
            lock (IndexLock)
            {
                idx.Models[model] = new IndexEntry
                {
                    Url = url,
                    File = file,
                    SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
            SaveIndex();
        }
        catch
        {
            // 落盘失败只影响下次启动，本次内存结果仍可用
        }
    }

    private static void TouchIndex(string model, string url, string file)
    {
        var idx = LoadIndex();
        lock (IndexLock)
        {
            if (idx.Models.TryGetValue(model, out var e)) e.SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            else idx.Models[model] = new IndexEntry
            {
                Url = url,
                File = file,
                SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        SaveIndex();
    }
}
