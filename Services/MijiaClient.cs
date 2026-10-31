using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MiHome.Models;

namespace MiHome.Services;

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

    public async Task<List<MijiaHome>> GetHomesAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _homes.Count > 0) return _homes;
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
        var all = lists.SelectMany(x => x).ToList();

        // 别人共享给我的设备不在任何家庭列表里，单独拉一次并按 did 去重合并
        try
        {
            var seen = all.Select(d => d.Did).ToHashSet();
            foreach (var d in await GetSharedDevicesAsync())
                if (seen.Add(d.Did)) all.Add(d);
        }
        catch
        {
            // 共享设备是增量能力，拉取失败不影响自有设备
        }
        return all;
    }

    /// <summary>
    /// 共享设备列表：接口把所有「有 owner 的设备」返回来，过滤 owner 为空的条目；
    /// 归属统一标成 shared，避免被某个家庭的切换误删。
    /// </summary>
    public async Task<List<MijiaDevice>> GetSharedDevicesAsync()
    {
        var r = await RequestAsync("/v2/home/device_list_page", new
        {
            ssid = "<unknown ssid>",
            bssid = "02:00:00:00:00:00",
            getVirtualModel = true,
            getHuamiDevices = 1,
            get_split_device = true,
            support_smart_home = true,
            get_cariot_device = true,
            get_third_device = true,
            get_phone_device = true,
            get_miwear_device = true
        });
        if (!r.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<MijiaDevice>();

        var list = new List<MijiaDevice>();
        foreach (var item in arr.EnumerateArray())
        {
            if (!item.TryGetProperty("owner", out var owner)) continue;
            var shared = owner.ValueKind switch
            {
                JsonValueKind.Object or JsonValueKind.Array => true,
                JsonValueKind.String => !string.IsNullOrEmpty(owner.GetString()),
                JsonValueKind.Number => owner.GetDouble() != 0,
                JsonValueKind.True => true,
                _ => false
            };
            if (!shared) continue;
            var d = item.Deserialize<MijiaDevice>();
            if (d == null || string.IsNullOrEmpty(d.Did)) continue;
            d.HomeId = "shared";
            if (string.IsNullOrWhiteSpace(d.RoomName) || d.RoomName == "未分配房间")
                d.RoomName = "共享设备";
            list.Add(d);
        }
        return list;
    }

    /// <summary>当前已缓存的家庭列表；先调 GetHomesAsync / GetAllDevicesAsync 才有内容。</summary>
    public IReadOnlyList<MijiaHome> Homes => _homes;

    /// <summary>按 id 取家庭；找不到返回 null。</summary>
    public MijiaHome? FindHome(string id) => _homes.FirstOrDefault(h => h.Id == id);

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

    /// <summary>拉取设备 MIoT 规格（services[]，含各属性 siid/piid/format/unit/range 或 value-list），用于核对属性定义。</summary>
    public async Task<JsonElement> GetSpecAsync(string did)
    {
        return await RequestAsync("/miotspec/device/getspec", new
        {
            did, force = false, language = "zh_CN"
        });
    }

    /// <summary>
    /// 一次读同一设备的多个属性。返回 siid*1000+piid → 原始值（bool / int / double / string，或 null）。
    /// 用组合键避免不同服务出现相同 piid 时互相覆盖（如空调模式 2/2 与风速 3/2）。
    /// 单条失败（设备不支持该属性、离线、越界）云端会带负 code，这里逐条跳过，不回抛。
    /// </summary>
    public async Task<Dictionary<int, object?>> GetPropsAsync(string did, params (int siid, int piid)[] props)
    {
        var map = new Dictionary<int, object?>();
        if (props.Length == 0) return map;
        var r = await RequestAsync("/miotspec/prop/get", new
        {
            @params = props.Select(p => new { did, siid = p.siid, piid = p.piid }).ToArray(),
            datasource = 1
        });
        if (r.ValueKind != JsonValueKind.Array) return map;

        foreach (var item in r.EnumerateArray())
        {
            if (!item.TryGetProperty("siid", out var sp) || sp.ValueKind != JsonValueKind.Number) continue;
            if (!item.TryGetProperty("piid", out var pp) || pp.ValueKind != JsonValueKind.Number) continue;
            var key = sp.GetInt32() * 1000 + pp.GetInt32();
            if (!item.TryGetProperty("value", out var v)) { map[key] = null; continue; }
            map[key] = v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => v.TryGetInt32(out var i) ? (object)i : v.GetDouble(),
                JsonValueKind.String => v.GetString(),
                _ => null
            };
        }
        return map;
    }

    /// <summary>灯详情：亮度 + 色温。</summary>
    public sealed record LightState(int Brightness, int ColorTemp);

    public async Task<LightState?> GetLightStateAsync(string did)
    {
        var m = await GetPropsAsync(did, (2, 2), (2, 3));
        if (m.Count == 0) return null;
        return new LightState(ToInt(m.GetValueOrDefault(2 * 1000 + 2), 100), ToInt(m.GetValueOrDefault(2 * 1000 + 3), 4000));
    }

    /// <summary>空调详情：模式 + 目标温度 + 风速（按设备规格的 siid/piid 读取）。</summary>
    public sealed record AcState(int Mode, double TargetTemp, int FanLevel);

    public async Task<AcState?> GetAcStateAsync(string did, Models.AcControlSchema schema)
    {
        var m = await GetPropsAsync(did,
            (schema.ModeSiid, schema.ModePiid),
            (schema.TempSiid, schema.TempPiid),
            (schema.FanSiid, schema.FanPiid));
        if (m.Count == 0) return null;
        return new AcState(
            ToInt(m.GetValueOrDefault(schema.ModeSiid * 1000 + schema.ModePiid), 0),
            ToDouble(m.GetValueOrDefault(schema.TempSiid * 1000 + schema.TempPiid), 26),
            ToInt(m.GetValueOrDefault(schema.FanSiid * 1000 + schema.FanPiid), 0));
    }

    private static int ToInt(object? v, int dflt) => v switch
    {
        int i => i,
        double d => (int)d,
        long l => (int)l,
        string s when int.TryParse(s, out var i) => i,
        _ => dflt
    };

    private static double ToDouble(object? v, double dflt) => v switch
    {
        double d => d,
        int i => i,
        long l => l,
        string s when double.TryParse(s, out var d) => d,
        _ => dflt
    };

    /// <summary>
    /// 跨设备批量读属性（轮询用）：一次请求最多 40 条，返回 (did,siid,piid) → 值。
    /// 单条失败（不支持/离线）云端会带负 code，这里跳过，不打断整批。
    /// </summary>
    public async Task<Dictionary<(string Did, int Siid, int Piid), object?>> ReadPropsAsync(
        IReadOnlyList<(string Did, int Siid, int Piid)> props)
    {
        var map = new Dictionary<(string, int, int), object?>();
        for (var start = 0; start < props.Count; start += 40)
        {
            var chunk = props.Skip(start).Take(40).ToList();
            var r = await RequestAsync("/miotspec/prop/get", new
            {
                @params = chunk.Select(p => new { did = p.Did, siid = p.Siid, piid = p.Piid }).ToArray(),
                datasource = 1
            });
            if (r.ValueKind != JsonValueKind.Array) continue;

            foreach (var item in r.EnumerateArray())
            {
                if (!item.TryGetProperty("did", out var dd) || dd.ValueKind != JsonValueKind.String) continue;
                if (!item.TryGetProperty("siid", out var sp) || sp.ValueKind != JsonValueKind.Number) continue;
                if (!item.TryGetProperty("piid", out var pp) || pp.ValueKind != JsonValueKind.Number) continue;
                var key = (dd.GetString()!, sp.GetInt32(), pp.GetInt32());
                map[key] = item.TryGetProperty("value", out var v) ? ReadValue(v) : null;
            }
        }
        return map;
    }

    /// <summary>执行规格动作：米家 action 接口的 params 是单个对象，不是 prop/set 那样的数组。</summary>
    public Task RunActionAsync(string did, int siid, int aiid) =>
        RunActionWithParamsAsync(did, siid, aiid, null);

    /// <summary>
    /// 带入参的规格动作（小爱文本指令等）：入参按位置数组放在 in 里，
    /// 例如 execute-text-directive 的 in = ["打开客厅灯", false]。
    /// </summary>
    public async Task RunActionWithParamsAsync(string did, int siid, int aiid, object? input)
    {
        var payload = input == null
            ? (object)new { did, siid, aiid }
            : new { did, siid, aiid, @in = input };
        await RequestAsync("/miotspec/action", new { @params = payload });
    }

    /// <summary>音箱音量 0–100（speaker 服务 volume 属性）。</summary>
    public async Task SetSpeakerVolumeAsync(string did, int siid, int piid, int volume)
    {
        await SetPropAsync(did, siid, piid, Math.Clamp(volume, 0, 100));
    }

    /// <summary>
    /// 小爱文本指令：execute-text-directive 的入参是 [文本, 静默执行]，
    /// 少传第二个参数云端会直接判定入参不合法，音箱不会有任何反应。
    /// </summary>
    public async Task SendTextDirectiveAsync(string did, int siid, int aiid, string text)
        => await RunActionWithParamsAsync(did, siid, aiid, new object[] { text, false });

    private static object? ReadValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.TryGetInt32(out var i) ? i : v.GetDouble(),
        JsonValueKind.String => v.GetString(),
        _ => null
    };

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
            if (code < 0)
            {
                var msg = item.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new Exception(string.IsNullOrEmpty(msg)
                    ? $"设备拒绝了指令 siid={siid} piid={piid}（code {code}）"
                    : $"设备拒绝了指令 siid={siid} piid={piid}（code {code} {msg}）");
            }
        }
    }

    /// <summary>bool 开关下发：优先用规格解析出的 siid/piid，未解析时回落 siid=2/piid=1。</summary>
    public async Task SetPowerAsync(MijiaDevice device, bool on)
    {
        await SetPropAsync(device.TargetDid, device.PowerSiid, device.PowerPiid, on);
    }

    /// <summary>多路开关的一路下发；虚拟子设备同样打父 did + 通道 siid。</summary>
    public async Task SetChannelAsync(SwitchChannelState channel, bool on)
    {
        if (channel.Device is not { } device) return;
        await SetPropAsync(device.TargetDid, channel.Siid, channel.Piid, on);
    }

    /// <summary>灯亮度 1–100（light 服务 piid=2）。</summary>
    public async Task SetLightBrightnessAsync(string did, int brightness)
    {
        await SetPropAsync(did, 2, 2, Math.Clamp(brightness, 1, 100));
    }

    /// <summary>灯色温，单位 K（light 服务 piid=3，常见 2700–6500）。</summary>
    public async Task SetLightColorTempAsync(string did, int kelvin)
    {
        await SetPropAsync(did, 2, 3, Math.Clamp(kelvin, 1000, 10000));
    }

    /// <summary>空调运行模式（按设备规格的 siid/piid，取值来自该型号 value-list）。</summary>
    public async Task SetAcModeAsync(string did, int siid, int piid, int mode)
    {
        await SetPropAsync(did, siid, piid, mode);
    }

    /// <summary>空调目标温度 ℃（按设备规格的 siid/piid，范围与步进取自 value-range）。</summary>
    public async Task SetAcTargetTempAsync(string did, int siid, int piid, double temp, Models.AcControlSchema schema)
    {
        var rounded = Math.Round(Math.Clamp(temp, schema.TempMin, schema.TempMax) / schema.TempStep) * schema.TempStep;
        await SetPropAsync(did, siid, piid, rounded);
    }

    /// <summary>空调风速档位（按设备规格的 siid/piid，取值来自该型号 value-list）。</summary>
    public async Task SetAcFanLevelAsync(string did, int siid, int piid, int level)
    {
        await SetPropAsync(did, siid, piid, level);
    }

    // ===== 窗帘电机：motor-control 只写（siid=2 / piid=1），0 停 1 开 2 关 =====
    public const int CurtainPause = 0;
    public const int CurtainOpen = 1;
    public const int CurtainClose = 2;

    /// <summary>下发窗帘电机指令：0=暂停、1=打开、2=关闭。</summary>
    public async Task SetCurtainMotorAsync(string did, int motor)
    {
        if (motor is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(motor), motor, "窗帘电机指令仅支持 0/1/2");
        await SetPropAsync(did, 2, 1, motor);
    }

    /// <summary>兼容卡片快捷开关：true 打开、false 关闭（不发暂停）。</summary>
    public Task SetCurtainAsync(string did, bool open) =>
        SetCurtainMotorAsync(did, open ? CurtainOpen : CurtainClose);

    /// <summary>读窗帘当前开合度 0–100（siid=2 / piid=3，只读位置百分比）。</summary>
    public async Task<int?> GetCurtainPositionAsync(string did)
    {
        try
        {
            var m = await GetPropsAsync(did, (2, 3));
            if (!m.TryGetValue(2 * 1000 + 3, out var v) || v == null) return null;
            return Math.Clamp(ToInt(v, 0), 0, 100);
        }
        catch
        {
            return null;
        }
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
