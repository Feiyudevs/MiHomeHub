using System.Security.Cryptography;
using System.Text;

namespace MiHome.Services;

/// <summary>
/// 米家云端请求加密/签名（移植自 mijia-api miutils.py）
/// - gen_nonce: 随机 nonce
/// - signed_nonce: SHA256(base64decode(ssecurity) + base64decode(nonce))
/// - RC4 加密（先 discard 1024 字节）
/// - SHA1 签名
/// </summary>
public static class MijiaCrypto
{
    /// <summary>
    /// 参照 miutils.gen_nonce：8 字节随机 + int(millis/60000) 的「最小」大端编码。
    /// 时间戳占 4 字节共 12 字节；定长写成 8 字节（共 16）会被服务端判 auth error。
    /// </summary>
    public static string GenNonce()
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var b = RandomNumberGenerator.GetBytes(8);
        var part2 = ms / 60000;
        var n = (Convert.ToString(part2, 2).Length + 7) / 8;
        Array.Resize(ref b, 8 + n);
        for (var i = 0; i < n; i++)
            b[8 + i] = (byte)((part2 >> (8 * (n - 1 - i))) & 0xFF);
        return Convert.ToBase64String(b);
    }

    public static string SignedNonce(string ssecurityB64, string nonceB64)
    {
        using var sha = SHA256.Create();
        var ss = Convert.FromBase64String(ssecurityB64);
        var nc = Convert.FromBase64String(nonceB64);
        var h = sha.ComputeHash(ss.Concat(nc).ToArray());
        return Convert.ToBase64String(h);
    }

    private static string Sign(string method, string uri, string signedNonce, SortedDictionary<string, string> p)
    {
        var parts = new List<string> { method.ToUpper(), uri };
        foreach (var kv in p) parts.Add($"{kv.Key}={kv.Value}");
        parts.Add(signedNonce);
        var s = string.Join("&", parts);
        using var sha1 = SHA1.Create();
        return Convert.ToBase64String(sha1.ComputeHash(Encoding.UTF8.GetBytes(s)));
    }

    /// <summary>RC4 加密（先丢弃 1024 字节），返回 base64</summary>
    public static string Rc4Encrypt(string keyB64, string plainText)
    {
        var key = Convert.FromBase64String(keyB64);
        using var rc4 = new RC4(key);
        rc4.Discard(1024);
        var data = Encoding.UTF8.GetBytes(plainText);
        var enc = rc4.TransformFinalBlock(data, 0, data.Length);
        return Convert.ToBase64String(enc);
    }

    /// <summary>RC4 解密（先丢弃 1024 字节），返回 UTF8 字符串（可能是 gzip）</summary>
    public static byte[] Rc4Decrypt(string keyB64, string cipherB64)
    {
        var key = Convert.FromBase64String(keyB64);
        using var rc4 = new RC4(key);
        rc4.Discard(1024);
        var data = Convert.FromBase64String(cipherB64);
        return rc4.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>
    /// 生成最终 POST 参数：把 data 字段先包成 {data: json}，加 rc4_hash__ 签名，
    /// 再把所有 value 用 RC4 加密，最后加 signature/ssecurity/_nonce。
    /// </summary>
    public static Dictionary<string, string> BuildEncParams(
        string uri, string method, string ssecurity, string nonce, Dictionary<string, string> paramsPlain)
    {
        var signedNonce = SignedNonce(ssecurity, nonce);

        // 第一步：原始参数 + rc4_hash__
        var step1 = new SortedDictionary<string, string>(paramsPlain);
        step1["rc4_hash__"] = Sign(method, uri, signedNonce, step1);

        // 第二步：所有 value RC4 加密
        var step2 = new SortedDictionary<string, string>();
        foreach (var kv in step1)
            step2[kv.Key] = Rc4Encrypt(signedNonce, kv.Value);

        // 第三步：加最终 signature / ssecurity / _nonce
        var result = new Dictionary<string, string>
        {
            ["signature"] = Sign(method, uri, signedNonce, step2),
            ["ssecurity"] = ssecurity,
            ["_nonce"] = nonce
        };
        foreach (var kv in step2) result[kv.Key] = kv.Value;
        return result;
    }
}

/// <summary>简易 RC4 实现（.NET 内置无 RC4）</summary>
public sealed class RC4 : IDisposable
{
    private readonly byte[] _s = new byte[256];
    private int _i, _j;

    public RC4(byte[] key)
    {
        for (var i = 0; i < 256; i++) _s[i] = (byte)i;
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + _s[i] + key[i % key.Length]) & 0xFF;
            (_s[i], _s[j]) = (_s[j], _s[i]);
        }
        _i = 0; _j = 0;
    }

    public void Discard(int n)
    {
        var buf = new byte[n];
        Transform(buf, 0, n);
    }

    public void Transform(byte[] data, int offset, int count)
    {
        for (var k = 0; k < count; k++)
        {
            _i = (_i + 1) & 0xFF;
            _j = (_j + _s[_i]) & 0xFF;
            (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
            data[offset + k] ^= _s[(_s[_i] + _s[_j]) & 0xFF];
        }
    }

    public byte[] TransformFinalBlock(byte[] data, int offset, int count)
    {
        var outBuf = new byte[count];
        Buffer.BlockCopy(data, offset, outBuf, 0, count);
        Transform(outBuf, 0, count);
        return outBuf;
    }

    public void Dispose() { }
}
