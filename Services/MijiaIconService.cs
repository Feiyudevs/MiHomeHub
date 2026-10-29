using System;
using System.Buffers;
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
/// 磁盘存的是接口原图，显示前按不透明范围裁掉画布空白并缩到卡片够用的尺寸（见 CropToContent）。
/// </summary>
public static class MijiaIconService
{
    private const string ProductApi = "https://home.mi.com/cgi-op/api/v1/baike/v2/product?model=";

    /// <summary>常驻位图的最长边：图标框只有 48/56px，128px 留了 2 倍缩放余量。</summary>
    private const int MaxEdge = 128;

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
        var dst = CropToContent(src);
        // 裁切/缩放出另一张小位图后，原图就该放手：Skia 的解码像素在原生内存里，
        // 托管侧的 Bitmap 包装只有几十字节，光看堆完全发现不了它占了多少。
        if (!ReferenceEquals(dst, src)) src.Dispose();
        return dst;
    }

    /// <summary>
    /// 产品图各家画布留白不一（实测内容只占画布 60%–99%），直接铺满卡片就会有的大有的小。
    /// 这里按 alpha 不透明范围裁出实物外框，补回 6% 透明边距，再缩到 <see cref="MaxEdge"/> 以内常驻：
    /// 图标框只有 48/56px，留 128px 已够 2 倍缩放，全分辨率原图没必要一直握着。
    /// </summary>
    private static Bitmap CropToContent(Bitmap src)
    {
        try
        {
            var w = src.PixelSize.Width;
            var h = src.PixelSize.Height;
            var fmt = src.Format;
            // 两种 32 位格式的 alpha 都在每像素末字节，裁切按字节整块搬，不需要换算颜色顺序
            if (w == 0 || h == 0 || (fmt != PixelFormat.Bgra8888 && fmt != PixelFormat.Rgba8888)) return src;

            var stride = w * 4;
            var len = stride * h;
            // 整幅缓冲只有这一步用得上：租用，免得每个型号都往大对象堆扔一张全图（GC 不压缩 LOH，
            // 这些一次性大块会一直占着提交内存不还给系统）
            var px = ArrayPool<byte>.Shared.Rent(len);
            try
            {
                var srcHandle = GCHandle.Alloc(px, GCHandleType.Pinned);
                try
                {
                    src.CopyPixels(new PixelRect(0, 0, w, h), srcHandle.AddrOfPinnedObject(), len, stride);
                }
                finally
                {
                    srcHandle.Free();
                }

                var minX = w;
                var minY = h;
                var maxX = -1;
                var maxY = -1;
                for (var y = 0; y < h; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < w; x++)
                    {
                        if (px[row + x * 4 + 3] <= 32) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }

                if (maxX < minX) return src;

                var pad = (int)Math.Round(Math.Max(maxX - minX + 1, maxY - minY + 1) * 0.06);
                var x0 = Math.Max(0, minX - pad);
                var y0 = Math.Max(0, minY - pad);
                var cropW = Math.Min(w, maxX + 1 + pad) - x0;
                var cropH = Math.Min(h, maxY + 1 + pad) - y0;

                var scale = Math.Min(1d, (double)MaxEdge / Math.Max(cropW, cropH));
                var outW = (int)Math.Round(cropW * scale);
                var outH = (int)Math.Round(cropH * scale);
                if (outW == w && outH == h) return src;

                var outStride = outW * 4;
                var outPx = new byte[outStride * outH];
                // 盒式降采样：目标像素取源块均值。位图是预乘 alpha，各通道直接平均正好合法。
                for (var y = 0; y < outH; y++)
                {
                    var sy0 = y * cropH / outH;
                    var sy1 = (y + 1) * cropH / outH;
                    for (var x = 0; x < outW; x++)
                    {
                        var sx0 = x * cropW / outW;
                        var sx1 = (x + 1) * cropW / outW;
                        int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                        for (var sy = sy0; sy < sy1; sy++)
                        {
                            var p = (y0 + sy) * stride + (x0 + sx0) * 4;
                            for (var sxx = sx0; sxx < sx1; sxx++, p += 4)
                            {
                                sumB += px[p];
                                sumG += px[p + 1];
                                sumR += px[p + 2];
                                sumA += px[p + 3];
                            }
                        }

                        var n = (sy1 - sy0) * (sx1 - sx0);
                        var o = (y * outW + x) * 4;
                        outPx[o] = (byte)((sumB + n / 2) / n);
                        outPx[o + 1] = (byte)((sumG + n / 2) / n);
                        outPx[o + 2] = (byte)((sumR + n / 2) / n);
                        outPx[o + 3] = (byte)((sumA + n / 2) / n);
                    }
                }

                // DPI 与像素同步缩，DIP 尺寸才保持等于裁切框；卡片里是 Stretch=Uniform，
                // 真正决定显示大小的是图标框，这里只是让 Bitmap.Size 仍然说得通。
                var dst = new WriteableBitmap(new PixelSize(outW, outH),
                    new Vector(src.Dpi.X * scale, src.Dpi.Y * scale), fmt, src.AlphaFormat);
                using (var fb = dst.Lock())
                {
                    for (var y = 0; y < outH; y++)
                        Marshal.Copy(outPx, y * outStride, fb.Address + y * fb.RowBytes, outStride);
                }

                return dst;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(px);
            }
        }
        catch
        {
            // 裁切失败就用原图，不能因为归一化挡住卡片
            return src;
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
