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

    /// <summary>
    /// 退出登录时释放全部已缓存的真机图（Skia 原生像素），并把内存缓存清空，
    /// 让 GC 把这部分常驻内存收回来。磁盘缓存保留，下次登录仍秒出。
    /// 进行中的加载任务（Task 尚未结束）不阻塞等待，其位图随后随任务一起被 GC 回收。
    /// </summary>
    public static void Reset()
    {
        foreach (var task in Cache.Values)
        {
            if (task.IsCompleted)
            {
                try { task.Result?.Dispose(); } catch { }
            }
        }
        Cache.Clear();
    }

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
            // 不裁切、不抠图、不去白描边，也不预缩放。
            // 1) 合法预乘（硬抠图边缘缩放时才不会闪脏色）
            // 2) 修正 DPI 到 96，让 DIP 尺寸 = 像素数
            // 缩放到显示尺寸交给界面层 Image 的 MediumQuality 插值（Skia 双线性 + mipmap
            // 预滤波）；不要改成 HighQuality——那是固定 4×4 核三次采样，480→48 缩小 10 倍时
            // 欠采样，白描边会断成锯齿点（见 MainWindow.axaml DevPhoto）。
            var w = src.PixelSize.Width;
            var h = src.PixelSize.Height;
            if (w <= 0 || h <= 0) return src;

            var needPremul = src.AlphaFormat != AlphaFormat.Premul;
            var needDpi = Math.Abs(src.Dpi.X - OutputDpi.X) >= 0.5 ||
                          Math.Abs(src.Dpi.Y - OutputDpi.Y) >= 0.5;
            if (!needPremul && !needDpi) return src;

            var srcStride = w * 4;
            var px = new byte[srcStride * h];
            var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
            try
            {
                src.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), srcStride * h, srcStride);
            }
            finally
            {
                handle.Free();
            }

            if (needPremul)
                NormalizePremulInPlace(px, px.Length, src.AlphaFormat == AlphaFormat.Unpremul);

            var dst = new WriteableBitmap(new PixelSize(w, h), OutputDpi,
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = dst.Lock())
            {
                for (var y = 0; y < h; y++)
                    Marshal.Copy(px, y * srcStride, fb.Address + y * fb.RowBytes, srcStride);
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
