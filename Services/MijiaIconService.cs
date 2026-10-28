using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace MijiaPanel.Services;

/// <summary>
/// 设备真机图：米家云的设备列表接口不带图标字段，按 model 到米家产品库取 realIcon 产品图。
/// realIcon 绝大多数托管在 iotweb-user-center（少数在 iotweb-product-center），且带签名参数，
/// 必须整段 URL 才能通过 CDN 校验；无该字段的型号返回 null，卡片回退到矢量图标。
/// </summary>
public static class MijiaIconService
{
    private const string ProductApi = "https://home.mi.com/cgi-op/api/v1/baike/v2/product?model=";

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

    public static Task<Bitmap?> GetAsync(string model) =>
        string.IsNullOrEmpty(model)
            ? Task.FromResult<Bitmap?>(null)
            : Cache.GetOrAdd(model, LoadAsync);

    private static async Task<Bitmap?> LoadAsync(string model)
    {
        try
        {
            var json = await Http.GetStringAsync(ProductApi + Uri.EscapeDataString(model));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("realIcon", out var icon) ||
                icon.ValueKind != JsonValueKind.String) return null;

            var url = icon.GetString() ?? "";
            if (string.IsNullOrEmpty(url)) return null;

            // realIcon 就是产品实物图，不能按 CDN 域名过滤（绝大多数在 iotweb-user-center）。
            // 也不能裁掉 ? 后的签名参数：该 CDN 校验签名，去签名会 403，必须整段 URL 下载。
            var bytes = await Http.GetByteArrayAsync(url);
            return new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            return null;
        }
    }
}
