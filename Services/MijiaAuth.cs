// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>
/// 米家扫码登录流程（移植自 mijia-api QRlogin）
/// 1. GET serviceLogin → 拿 location（登录页 URL）
/// 2. GET loginUrl → 拿 loginUrl（二维码内容）+ lp（长轮询地址）
/// 3. UI 显示二维码，用户用米家 App 扫
/// 4. GET lp（120s 长轮询）→ 拿 ssecurity/passToken/userId/cUserId/location
/// 5. GET location → Cookie 里拿到 serviceToken
/// </summary>
public class MijiaAuth
{
    private const string ServiceLoginUrl = "https://account.xiaomi.com/pass/serviceLogin?_json=true&sid=mijia&_locale=zh_CN";
    private const string LoginUrlBase = "https://account.xiaomi.com/longPolling/loginUrl";

    /// <summary>扫码流程专用：需要 CookieContainer 串起 serviceLogin → lp → 回调。</summary>
    private static HttpClient NewClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip
    })
    {
        // lp 是 120s 长轮询，默认 100s 超时会在等待期间被掐断
        Timeout = TimeSpan.FromSeconds(150)
    };

    /// <summary>
    /// 换 serviceToken 专用：UseCookies=false，只带我们拼的 passport Cookie。
    /// 共享 CookieContainer 会把上次登录的 serviceToken/过期会话掺进下次 serviceLogin，
    /// 表现为退出后再一键登录必失败、只能改扫码。
    /// </summary>
    private static HttpClient NewRefreshClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private HttpClient _http = NewClient();
    public AuthData Auth { get; private set; } = new();

    /// <summary>丢弃扫码会话的 Cookie，避免退出/下次登录带上旧 Cookie。</summary>
    private void ResetHttp()
    {
        try { _http.Dispose(); } catch { }
        _http = NewClient();
    }

    private static readonly string AuthPath = Path.Combine(AppPaths.Dir, "auth.json");

    public static MijiaAuth LoadOrEmpty()
    {
        var a = new MijiaAuth();
        try
        {
            if (!File.Exists(AuthPath)) return a;

            var text = File.ReadAllText(AuthPath);
            var legacyPlaintext = false;

            if (DpapiStore.IsProtected(text))
            {
                // DPAPI 加密文件：解密失败（换 Windows 账户 / 换机）按未登录处理
                var json = DpapiStore.Unprotect(text);
                if (json is null) return a;
                a.Auth = JsonSerializer.Deserialize<AuthData>(json) ?? new AuthData();
            }
            else
            {
                a.Auth = JsonSerializer.Deserialize<AuthData>(text) ?? new AuthData();
                legacyPlaintext = true;
            }

            // 旧版本明文 auth.json：读入后立即改写为 DPAPI 加密，完成一次性迁移
            if (legacyPlaintext
                && (!string.IsNullOrEmpty(a.Auth.ServiceToken) || !string.IsNullOrEmpty(a.Auth.PassToken)))
                a.Save();
        }
        catch { }
        return a;
    }

    public bool HasValidAuth()
    {
        return !string.IsNullOrEmpty(Auth.ServiceToken)
            && !string.IsNullOrEmpty(Auth.SSecurity)
            && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < Auth.ExpireTime;
    }

    /// <summary>是否还留着 passToken，可走一键登录、无需重新扫码。</summary>
    public bool CanQuickLogin() => !string.IsNullOrEmpty(Auth.PassToken);

    /// <summary>
    /// 退出登录：清掉会话凭证（serviceToken / ssecurity），保留 passToken 与设备标识，
    /// 下次可一键换新 serviceToken，不必再扫码。
    /// </summary>
    public void Logout()
    {
        Auth.ServiceToken = "";
        Auth.SSecurity = "";
        Auth.ExpireTime = 0;
        Save();
        ResetHttp();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(AuthPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(Auth, new JsonSerializerOptions { WriteIndented = true });
        // DPAPI 加密落盘（仅当前 Windows 用户可解密）；加密不可用时回退明文，保证登录不中断
        File.WriteAllText(AuthPath, DpapiStore.Protect(json) ?? json);
    }

    private string Ua => string.IsNullOrEmpty(Auth.UA)
        ? (Auth.UA = $"Android-15-11.0.701-Xiaomi-23046RP50C-OS2.0-" +
                    $"{RandomHex(40)}-CN-{RandomHex(32)}-{RandomHex(32)}-SmartHome-MI_APP_STORE-{RandomHex(40)}|{RandomHex(40)}|{Auth.PassO}-64")
        : Auth.UA;

    private static string RandomHex(int n)
    {
        const string c = "0123456789abcdef";
        var b = new char[n];
        var r = Random.Shared;
        for (var i = 0; i < n; i++) b[i] = c[r.Next(c.Length)];
        return new string(b);
    }

    private static string RandomBase64(int n)
    {
        const string c = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_-";
        var b = new char[n];
        var r = Random.Shared;
        for (var i = 0; i < n; i++) b[i] = c[r.Next(c.Length)];
        return new string(b);
    }

    private void EnsureDeviceId()
    {
        if (string.IsNullOrEmpty(Auth.DeviceId)) Auth.DeviceId = RandomBase64(16);
        if (string.IsNullOrEmpty(Auth.PassO)) Auth.PassO = RandomHex(16);
    }

    /// <summary>装上 passport 侧身份 Cookie（serviceLogin 认这套）。</summary>
    private void UsePassportCookies()
    {
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("User-Agent", Ua);
        _http.DefaultRequestHeaders.Add("Cookie",
            $"deviceId={Auth.DeviceId};pass_o={Auth.PassO};passToken={Auth.PassToken};" +
            $"userId={Auth.UserId};cUserId={Auth.CUserId};uLocale=zh_CN;");
    }

    /// <summary>最近一次 RefreshServiceTokenAsync 失败原因（成功时为 null）。</summary>
    public string? LastRefreshError { get; private set; }

    /// <summary>
    /// 用已存的 passToken 换「与该服务配对」的 ssecurity + serviceToken，无需重新扫码。
    /// 必须用 serviceLogin 响应里的 ssecurity 去配它自己 location 下发的 serviceToken；
    /// 用扫码长轮询响应里的 ssecurity 会被判 code=4 auth error。
    /// 每次独立建连，不复用扫码会话的 CookieContainer。
    /// </summary>
    public async Task<bool> RefreshServiceTokenAsync(CancellationToken ct = default)
    {
        LastRefreshError = null;
        EnsureDeviceId();
        using var http = NewRefreshClient();
        http.DefaultRequestHeaders.Add("User-Agent", Ua);
        http.DefaultRequestHeaders.Add("Cookie",
            $"deviceId={Auth.DeviceId};pass_o={Auth.PassO};passToken={Auth.PassToken};" +
            $"userId={Auth.UserId};cUserId={Auth.CUserId};uLocale=zh_CN;");

        try
        {
            var resp = await http.GetAsync(ServiceLoginUrl, ct);
            var text = (await resp.Content.ReadAsStringAsync(ct)).Replace("&&&START&&&", "");
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var c) && c.GetInt32() != 0)
            {
                LastRefreshError = $"serviceLogin code={c.GetInt32()} {Text(root, "description")}";
                return false;
            }
            var ssecurity = Text(root, "ssecurity");
            var location = Text(root, "location");
            if (string.IsNullOrEmpty(ssecurity) || string.IsNullOrEmpty(location))
            {
                LastRefreshError = "serviceLogin 未返回 ssecurity/location";
                return false;
            }

            var locResp = await http.GetAsync(location, ct);
            if ((int)locResp.StatusCode != 200)
            {
                LastRefreshError = $"location HTTP {(int)locResp.StatusCode}";
                return false;
            }
            var token = "";
            if (locResp.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var setCookie in cookies)
                {
                    var kv = setCookie.Split(';')[0];
                    var idx = kv.IndexOf('=');
                    if (idx <= 0) continue;
                    if (kv[..idx] == "serviceToken") token = kv[(idx + 1)..];
                }
            }
            if (string.IsNullOrEmpty(token))
            {
                LastRefreshError = "location 未下发 serviceToken";
                return false;
            }

            Auth.SSecurity = ssecurity;
            Auth.ServiceToken = token;
            // 换到的新会话重新计 30 天，否则过期时间一直停在扫码那一刻，
            // 明明能自动续期，启动却被判成未登录
            Auth.ExpireTime = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
            Save();
            return true;
        }
        catch (OperationCanceledException)
        {
            LastRefreshError = "已取消";
            return false;
        }
        catch (Exception ex)
        {
            LastRefreshError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>第一步：获取扫码所需数据（loginUrl 用作二维码内容，lp 用于长轮询）</summary>
    public async Task<(string loginUrl, string lp)?> GetQrLoginDataAsync(CancellationToken ct = default)
    {
        // 每次扫码都从干净 Cookie 会话开始，避免上次登录/长轮询残留干扰
        ResetHttp();
        EnsureDeviceId();
        UsePassportCookies();

        // 1) serviceLogin 拿 location
        var resp = await _http.GetAsync(ServiceLoginUrl, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        text = text.Replace("&&&START&&&", "");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("location", out var locEl)) return null;
        var location = locEl.GetString()!;

        // 2) 解析 location 的 query，拼到 loginUrl
        var uri = new Uri(location);
        var qs = HttpUtility.ParseQueryString(uri.Query);
        var qb = new StringBuilder();
        qb.Append(LoginUrlBase).Append("?");
        foreach (var k in qs.AllKeys)
        {
            if (k == null) continue;
            qb.Append(Uri.EscapeDataString(k)).Append("=").Append(Uri.EscapeDataString(qs[k] ?? "")).Append("&");
        }
        qb.Append("theme=&bizDeviceType=&_hasLogo=false&_qrsize=240&_dc=").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var resp2 = await _http.GetAsync(qb.ToString(), ct);
        var text2 = await resp2.Content.ReadAsStringAsync(ct);
        text2 = text2.Replace("&&&START&&&", "");
        using var doc2 = JsonDocument.Parse(text2);
        var root2 = doc2.RootElement;
        if (root2.TryGetProperty("code", out var c) && c.GetInt32() != 0) return null;
        var loginUrl = root2.GetProperty("loginUrl").GetString()!;
        var lp = root2.GetProperty("lp").GetString()!;
        return (loginUrl, lp);
    }

    /// <summary>最近一次 WaitForScanAsync 失败原因（成功或无响应时为 null）。</summary>
    public string? LastScanError { get; private set; }

    /// <summary>取字段文本：小米部分字段（userId 等）是 Number，GetString() 会抛类型异常。</summary>
    private static string Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return "";
        return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText();
    }

    /// <summary>第二步：长轮询等待用户扫码（最长 120s）。成功后填充 Auth 并保存。取消登录时传入的 ct 会立刻打断。</summary>
    public async Task<bool> WaitForScanAsync(string lp, CancellationToken ct = default)
    {
        LastScanError = null;
        var raw = "";
        // 复用 _http：lp 与回调都依赖第一步登录页下发的 Cookie 会话
        try
        {
            var resp = await _http.GetAsync(lp, ct);
            raw = await resp.Content.ReadAsStringAsync(ct);
            ct.ThrowIfCancellationRequested();
            var text = raw.Replace("&&&START&&&", "");
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var c) && c.GetInt32() != 0)
            {
                LastScanError = $"lp 返回 code={c.GetInt32()} {Text(root, "description")}";
                return false;
            }

            Auth.SSecurity = Text(root, "ssecurity");
            Auth.UserId = Text(root, "userId");
            Auth.CUserId = Text(root, "cUserId");
            Auth.PassToken = Text(root, "passToken");

            // 第三步：GET location，从 Set-Cookie 里拿 serviceToken
            var callback = Text(root, "location");
            var cbResp = await _http.GetAsync(callback, ct);
            if (cbResp.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var setCookie in cookies)
                {
                    var kv = setCookie.Split(';')[0];
                    var idx = kv.IndexOf('=');
                    if (idx <= 0) continue;
                    var k = kv[..idx];
                    var v = kv[(idx + 1)..];
                    if (k == "serviceToken") Auth.ServiceToken = v;
                }
            }

            Auth.ExpireTime = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
            Save();
            if (string.IsNullOrEmpty(Auth.ServiceToken))
                LastScanError = $"回调 {(int)cbResp.StatusCode} 未下发 serviceToken";
            return !string.IsNullOrEmpty(Auth.ServiceToken);
        }
        catch (OperationCanceledException)
        {
            LastScanError = "已取消";
            return false;
        }
        catch (Exception ex)
        {
            var preview = raw.Length > 160 ? raw[..160] : raw;
            LastScanError = $"{ex.GetType().Name}: {ex.Message} | lp={preview}";
            return false;
        }
    }
}
