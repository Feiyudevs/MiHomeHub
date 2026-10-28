using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MijiaPanel.Models;

namespace MijiaPanel.Services;

/// <summary>
/// 米家云端 API 客户端（api.mijia.tech/app）
/// 所有请求走 BuildEncParams 加密签名。
/// </summary>
public class MijiaClient
{
    private const string ApiBase = "https://api.mijia.tech/app";
    private const string SceneListUri = "/appgateway/miot/appsceneservice/AppSceneService/GetSimpleSceneList";
    private const string RunSceneUri = "/appgateway/miot/appsceneservice/AppSceneService/NewRunScene";
    private readonly MijiaAuth _auth;
    private readonly HttpClient _http;
    private List<MijiaHome> _homes = new();

    public MijiaClient(MijiaAuth auth)
    {
        _auth = auth;
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
        })
        {
            // 默认超时 100s，单个请求卡住（DNS 慢/丢包/服务端排队）会把整条链路拖死
            Timeout = TimeSpan.FromSeconds(20)
        };
        ApplyCookies();
    }

    private void ApplyCookies()
    {
        var a = _auth.Auth;
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("User-Agent", a.UA);
        _http.DefaultRequestHeaders.Add("accept-encoding", "identity");
        _http.DefaultRequestHeaders.Add("miot-accept-encoding", "GZIP");
        _http.DefaultRequestHeaders.Add("miot-encrypt-algorithm", "ENCRYPT-RC4");
        _http.DefaultRequestHeaders.Add("x-xiaomi-protocal-flag-cli", "PROTOCAL-HTTP2");
        var cookie = $"cUserId={a.CUserId}; yetAnotherServiceToken={a.ServiceToken}; serviceToken={a.ServiceToken}; " +
                     $"channel=MI_APP_STORE; countryCode=CN; PassportDeviceId={a.DeviceId}; locale=zh_CN";
        _http.DefaultRequestHeaders.Add("Cookie", cookie);
    }

    private async Task<JsonElement> RequestAsync(string uri, object dataObj)
    {
        var dataJson = JsonSerializer.Serialize(dataObj);
        var nonce = MijiaCrypto.GenNonce();
        var plain = new Dictionary<string, string> { ["data"] = dataJson };
        var form = MijiaCrypto.BuildEncParams(uri, "POST", _auth.Auth.SSecurity, nonce, plain);

        var content = new FormUrlEncodedContent(form);
        var resp = await _http.PostAsync(ApiBase + uri, content);
        var body = await resp.Content.ReadAsStringAsync();

        JsonElement ret;
        try
        {
            ret = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch
        {
            // 加密响应：RC4 解密（可能 gzip）
            var signedNonce = MijiaCrypto.SignedNonce(_auth.Auth.SSecurity, nonce);
            var dec = MijiaCrypto.Rc4Decrypt(signedNonce, body.Trim());
            try
            {
                var s = Encoding.UTF8.GetString(dec);
                ret = JsonSerializer.Deserialize<JsonElement>(s);
            }
            catch
            {
                // gzip
                using var ms = new MemoryStream(dec);
                using var gz = new GZipStream(ms, CompressionMode.Decompress);
                using var sr = new StreamReader(gz, Encoding.UTF8);
                var s = await sr.ReadToEndAsync();
                ret = JsonSerializer.Deserialize<JsonElement>(s);
            }
        }

        if (ret.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() != 0)
        {
            var msg = ret.TryGetProperty("message", out var m) ? m.GetString() : "未知错误";
            throw new Exception($"米家 API 错误 {c}: {msg}");
        }
        return ret.TryGetProperty("result", out var r) ? r : ret;
    }

    public async Task<List<MijiaHome>> GetHomesAsync()
    {
        var r = await RequestAsync("/v2/homeroom/gethome_merged", new
        {
            fg = true, fetch_share = true, fetch_share_dev = true,
            fetch_cariot = true, limit = 300, app_ver = 7, plat_form = 0
        });
        _homes = r.GetProperty("homelist").Deserialize<List<MijiaHome>>() ?? new();
        return _homes;
    }

    public async Task<List<MijiaDevice>> GetDevicesAsync(MijiaHome home)
    {
        var r = await RequestAsync("/home/home_device_list", new
        {
            home_owner = home.Uid,
            home_id = long.Parse(home.Id),
            limit = 200,
            start_did = "",
            get_split_device = true,
            support_smart_home = true,
            get_cariot_device = true,
            get_third_device = true
        });
        var list = r.TryGetProperty("device_info", out var d)
            ? d.Deserialize<List<MijiaDevice>>() ?? new()
            : new List<MijiaDevice>();
        foreach (var x in list) x.HomeId = home.Id;
        AssignRooms(home, list);
        return list;
    }

    public async Task<List<MijiaDevice>> GetAllDevicesAsync()
    {
        var homes = await GetHomesAsync();
        // 多家庭串行拉会让耗时按家庭数叠加，这里并发
        var tasks = homes.Select(async h =>
        {
            try { return await GetDevicesAsync(h); } catch { return new List<MijiaDevice>(); }
        });
        var lists = await Task.WhenAll(tasks);
        return lists.SelectMany(x => x).ToList();
    }

    /// <summary>
    /// 房间归属只能从 homelist 的 roomlist[].dids 反查（device_info 的 extra 里没有房间）。
    /// 两边的 did 都可能带 / 不带 ".s2" 这类拆分后缀，所以完整 did 和去后缀的 base did 都建索引。
    /// </summary>
    private static void AssignRooms(MijiaHome home, List<MijiaDevice> devices)
    {
        var full = new Dictionary<string, string>();
        var baseId = new Dictionary<string, string>();
        foreach (var room in home.Rooms)
        {
            var name = string.IsNullOrEmpty(room.Name) ? "未命名房间" : room.Name;
            foreach (var did in room.Dids)
            {
                full[did] = name;
                var key = did.Split('.')[0];
                baseId.TryAdd(key, name);
            }
        }
        foreach (var d in devices)
        {
            if (full.TryGetValue(d.Did, out var n) || baseId.TryGetValue(d.Did.Split('.')[0], out n))
                d.RoomName = n;
        }
    }

    /// <summary>手动场景列表（所有家庭）。</summary>
    public async Task<List<MijiaScene>> GetScenesAsync()
    {
        var homes = _homes.Count > 0 ? _homes : await GetHomesAsync();
        var tasks = homes.Select(async h =>
        {
            var list = new List<MijiaScene>();
            var r = await RequestAsync(SceneListUri, new
            {
                app_version = 12, get_type = 2, home_id = h.Id, owner_uid = h.Uid
            });
            if (r.TryGetProperty("manual_scene_info_list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in arr.Deserialize<List<MijiaScene>>() ?? new())
                {
                    s.HomeId = h.Id;
                    list.Add(s);
                }
            }
            return list;
        });
        var lists = await Task.WhenAll(tasks);
        return lists.SelectMany(x => x).ToList();
    }

    public async Task RunSceneAsync(MijiaScene scene)
    {
        var owner = _homes.Find(h => h.Id == scene.HomeId)?.Uid ?? 0;
        await RequestAsync(RunSceneUri, new
        {
            scene_id = scene.SceneId, scene_type = 2, phone_id = "null",
            home_id = scene.HomeId, owner_uid = owner
        });
    }

    /// <summary>读属性：did + siid + piid</summary>
    public async Task<JsonElement> GetPropAsync(string did, int siid, int piid)
    {
        var r = await RequestAsync("/miotspec/prop/get", new
        {
            @params = new[] { new { did, siid, piid } },
            datasource = 1
        });
        return r[0];
    }

    /// <summary>
    /// 批量读开关状态。一次 /miotspec/prop/get 的每条 params 各自带 siid/piid，
    /// 所以开关(piid=1)和窗帘(piid=3)能混在同一批里，不再分两轮串行；批次之间也并发。
    /// 灯/开关/插座/空调读 piid=1 的 bool on；窗帘的 piid=1 只写不可读，改读 piid=3 的位置百分比。
    /// </summary>
    public async Task<Dictionary<string, bool>> GetOnStatesAsync(IReadOnlyList<MijiaDevice> devices)
    {
        var targets = devices.Where(d => d.Control != MijiaControl.None).ToList();
        var batches = new List<Task<Dictionary<string, bool>>>();
        for (var start = 0; start < targets.Count; start += 40)
        {
            batches.Add(ReadStatesChunkAsync(targets.Skip(start).Take(40).ToList()));
        }

        var map = new Dictionary<string, bool>();
        foreach (var part in await Task.WhenAll(batches))
            foreach (var kv in part) map[kv.Key] = kv.Value;
        return map;
    }

    private async Task<Dictionary<string, bool>> ReadStatesChunkAsync(List<MijiaDevice> chunk)
    {
        var map = new Dictionary<string, bool>();
        var controls = new Dictionary<string, MijiaControl>();
        foreach (var d in chunk) controls[d.Did] = d.Control;

        var query = chunk
            .Select(d => new { did = d.Did, siid = 2, piid = d.Control == MijiaControl.Curtain ? 3 : 1 })
            .ToArray();
        var r = await RequestAsync("/miotspec/prop/get", new { @params = query, datasource = 1 });
        if (r.ValueKind != JsonValueKind.Array) return map;

        foreach (var item in r.EnumerateArray())
        {
            if (!item.TryGetProperty("did", out var dd) || dd.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("value", out var v))
                continue;

            var did = dd.GetString()!;
            var isCurtain = controls.TryGetValue(did, out var c) && c == MijiaControl.Curtain;
            map[did] = isCurtain
                ? v.ValueKind == JsonValueKind.Number && v.GetDouble() > 0
                : v.ValueKind == JsonValueKind.True;
        }
        return map;
    }

    /// <summary>
    /// 写属性：did + siid + piid + value。
    /// 云端按条目回 code，0/1 是成功，负数才是被设备拒绝（-704xxx 是不支持/越界/只读）。
    /// </summary>
    public async Task SetPropAsync(string did, int siid, int piid, object value)
    {
        var r = await RequestAsync("/miotspec/prop/set", new
        {
            @params = new[] { new { did, siid, piid, value } }
        });
        if (r.ValueKind != JsonValueKind.Array) return;
        foreach (var item in r.EnumerateArray())
        {
            var code = item.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32() : 0;
            if (code < 0) throw new Exception($"设备拒绝了指令（code {code}）");
        }
    }

    /// <summary>
    /// 便捷：开关灯。
    /// 灯标准服务：siid=2, piid=1 是 on。
    /// </summary>
    public async Task ToggleLightAsync(string did, bool on)
    {
        await SetPropAsync(did, 2, 1, on);
    }

    /// <summary>窗帘：motor-control 只写，1=打开、2=关闭（0 是暂停）。</summary>
    public async Task SetCurtainAsync(string did, bool open)
    {
        await SetPropAsync(did, 2, 1, open ? 1 : 2);
    }

    /// <summary>读灯开关状态</summary>
    public async Task<bool> GetLightOnAsync(string did)
    {
        try
        {
            var r = await GetPropAsync(did, 2, 1);
            return r.TryGetProperty("value", out var v) && v.GetBoolean();
        }
        catch { return false; }
    }
}
