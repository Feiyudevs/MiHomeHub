// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MiHomeHub.Models;
using MiHomeHub.Services.Telemetry;

namespace MiHomeHub.Services;

/// <summary>
/// 米家云端 API 客户端（api.mijia.tech/app）
/// 所有请求走 BuildEncParams 加密签名。
/// </summary>
public class MijiaClient : IDisposable
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

    /// <summary>当前登录的小米账号 ID（体脂秤等 eco 接口要拿它当 uid）。</summary>
    public string UserId => _auth.Auth.UserId;

    internal async Task<JsonElement> RequestRawAsync(string uri, object dataObj) =>
        await RequestRawAsync(uri, dataObj, ApiBase);

    /// <summary>
    /// 走「米家生态」接口（<c>/eco/...</c>）：体脂秤的成员与历史记录都挂在这条路上，
    /// 复用同一套签名请求，返回完整信封（code / message / result），由调用方判 code。
    /// </summary>
    public async Task<JsonElement> EcoRequestAsync(string uri, object dataObj) =>
        await RequestRawAsync(uri, dataObj);

    /// <summary>
    /// 底层请求：返回完整响应信封（含 code / message / result），不在这里抛业务错误。
    /// 诊断探针需要看到原始 code 与字段结构，业务层走 <see cref="RequestAsync"/>。
    /// </summary>
    internal async Task<JsonElement> RequestRawAsync(string uri, object dataObj, string baseUrl)
    {
        var (bytes, nonce) = await PostSignedAsync(uri, dataObj, baseUrl);

        JsonElement ret;
        try
        {
            ret = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            // 加密响应：RC4 解密（可能 gzip）
            var signedNonce = MijiaCrypto.SignedNonce(_auth.Auth.SSecurity, nonce);
            var dec = MijiaCrypto.Rc4Decrypt(signedNonce, Encoding.UTF8.GetString(bytes).Trim());
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

        return ret;
    }

    /// <summary>签名并 POST，返回原始响应字节与本次 nonce（解密要用同一个 nonce 推 signedNonce）。</summary>
    internal async Task<(byte[] Bytes, string Nonce)> PostSignedAsync(string uri, object dataObj, string? baseUrl = null)
    {
        var dataJson = JsonSerializer.Serialize(dataObj);
        var nonce = MijiaCrypto.GenNonce();
        var plain = new Dictionary<string, string> { ["data"] = dataJson };
        var form = MijiaCrypto.BuildEncParams(uri, "POST", _auth.Auth.SSecurity, nonce, plain);
        var content = new FormUrlEncodedContent(form);
        var resp = await _http.PostAsync((baseUrl ?? ApiBase) + uri, content);
        return (await resp.Content.ReadAsByteArrayAsync(), nonce);
    }

    /// <summary>业务请求：剥掉外层信封，code≠0 直接抛。</summary>
    private async Task<JsonElement> RequestAsync(string uri, object dataObj)
    {
        var ret = await RequestRawAsync(uri, dataObj);
        if (ret.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() != 0)
        {
            var msg = ret.TryGetProperty("message", out var m) ? m.GetString() : "未知错误";
            throw new Exception($"米家 API 错误 {c.GetInt32()}: {msg}");
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

        // 设备组（灯组等多设备虚拟设备）的成员 did 只出现在原始响应的
        // extra.member_list.member_ship 里，DTO 不映射，这里补上：
        // 场景里的组动作（type=4）要靠它把「1 个设备组」展开成真实设备数。
        // 同一轮把设备自己的封面图（devPicInfo.logo / icon）也补上：型号产品图查不到时靠它。
        if (r.TryGetProperty("device_info", out var rawArr) && rawArr.ValueKind == JsonValueKind.Array)
        {
            var byDid = new Dictionary<string, MijiaDevice>(StringComparer.Ordinal);
            foreach (var x in list)
                if (!string.IsNullOrEmpty(x.Did)) byDid[x.Did] = x;

            foreach (var el in rawArr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var did = el.TryGetProperty("did", out var de) ? de.ToString() : "";
                var dev = byDid.TryGetValue(did, out var found) ? found : null;
                if (dev != null)
                    dev.CustomIconUrl = ExtractDeviceArtwork(el) ?? dev.CustomIconUrl;

                if (!did.StartsWith("group.", StringComparison.OrdinalIgnoreCase)) continue;
                if (dev == null) continue;
                if (!el.TryGetProperty("extra", out var extra) || extra.ValueKind != JsonValueKind.Object) continue;
                if (!extra.TryGetProperty("member_list", out var ml) || ml.ValueKind != JsonValueKind.Object) continue;
                if (!ml.TryGetProperty("member_ship", out var ms) || ms.ValueKind != JsonValueKind.Object) continue;

                dev.GroupMembers.Clear();
                foreach (var member in ms.EnumerateObject())
                    if (!string.IsNullOrEmpty(member.Name)) dev.GroupMembers.Add(member.Name);
            }
        }
        foreach (var x in list) x.HomeId = home.Id;

        // 米家自带的「更换图标」目录：设备列表里的 subclass_id 选中项，指向目录里的一张图。
        // 只有真的选了图标的设备才会带非 0 id，正常情况下一个家庭一次请求就够。
        var missing = list.Where(item => string.IsNullOrEmpty(item.CustomIconUrl)).ToList();
        if (missing.Any(item => TrySubclassId(item.SubclassId) is not null))
        {
            var icons = await GetSubclassIconsAsync();
            foreach (var item in missing)
            {
                var id = TrySubclassId(item.SubclassId);
                if (id is uint key && icons.TryGetValue(key, out var url))
                    item.CustomIconUrl = url;
            }
        }

        AssignRooms(home, list);
        return list;
    }

    private Dictionary<uint, string>? _subclassIcons;
    private DateTime _subclassIconsAt;

    /// <summary>米家 App 内置的替换图标目录（id → 图片），短时缓存，失败不重试。</summary>
    private async Task<Dictionary<uint, string>> GetSubclassIconsAsync()
    {
        if (_subclassIcons != null && DateTime.UtcNow - _subclassIconsAt < TimeSpan.FromMinutes(10))
            return _subclassIcons;

        var icons = new Dictionary<uint, string>();
        try
        {
            var r = await RequestAsync("/v2/device/switch_subclass", new { });
            if (r.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in r.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    if (!row.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
                    if (!row.TryGetProperty("proxy_category_icon", out var icon) || icon.ValueKind != JsonValueKind.String) continue;
                    if (id.TryGetUInt32(out var key))
                    {
                        var url = NormalizeArtworkUrl(icon.GetString());
                        if (url != null) icons[key] = url;
                    }
                }
            }
        }
        catch
        {
            // 只是可选封面，目录接口失败不影响设备列表
        }
        _subclassIcons = icons;
        _subclassIconsAt = DateTime.UtcNow;
        return icons;
    }

    private static uint? TrySubclassId(string? raw) =>
        uint.TryParse(raw, out var id) && id > 0 ? id : null;

    /// <summary>
    /// 设备自己的封面图：米家 App 里设备图标优先取 devPicInfo.logo（用户上传的封面），
    /// 其次才是设备列表的 icon 字段。两者可能是相对路径，补成云 CDN 的绝对地址。
    /// </summary>
    private static string? ExtractDeviceArtwork(JsonElement raw)
    {
        var logo = "";
        if (raw.TryGetProperty("devPicInfo", out var pic))
        {
            if (pic.ValueKind == JsonValueKind.Object)
                logo = ArtworkString(pic, "logo") ?? "";
            else if (pic.ValueKind == JsonValueKind.String)
            {
                var text = pic.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                            logo = ArtworkString(doc.RootElement, "logo") ?? "";
                    }
                    catch
                    {
                        // devPicInfo 也可能直接就是一段 URL，不是 JSON
                    }
                    if (string.IsNullOrEmpty(logo)) logo = text;
                }
            }
        }

        var url = NormalizeArtworkUrl(logo);
        if (url != null) return url;

        // 设备列表里下发设备图的字段名不只一个版本，按优先级都试一遍
        foreach (var name in new[] { "icon", "icon_real", "deviceIcon", "iconUrl" })
        {
            if (raw.TryGetProperty(name, out var icon) && icon.ValueKind == JsonValueKind.String)
            {
                url = NormalizeArtworkUrl(icon.GetString());
                if (url != null) return url;
            }
        }
        return null;
    }

    private static string? ArtworkString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NormalizeArtworkUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var url = raw.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        else if (url.StartsWith('/')) url = "https://cdn.fds.api.xiaomi.com" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // 也见过不带前导斜杠的 CDN 相对路径（iotweb-product-center/xxx.png）
            if (!url.Contains('/') || !url.Contains('.')) return null;
            url = "https://cdn.fds.api.xiaomi.com/" + url.TrimStart('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return null;
        }
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? url : null;
    }

    /// <summary>
    /// 探针专用：设备列表的原始响应。DTO 只映射了常用字段，设备组（did 形如 group.xxx）
    /// 的成员信息如果存在，只会出现在原始响应里。
    /// </summary>
    public async Task<JsonElement> GetRawHomeDeviceListAsync(MijiaHome home)
        => await RequestAsync("/home/home_device_list", new
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

    /// <summary>
    /// 探针专用：新版设备列表接口（米家 Android 走的也是这一条），字段比旧接口全，
    /// 设备图标的字段名/层级以这里返回的为准。
    /// </summary>
    public async Task<JsonElement> GetRawHomeDeviceListV2Async(MijiaHome home)
        => await RequestAsync("/v2/home/home_device_list", new
        {
            home_owner = home.Uid,
            home_id = long.Parse(home.Id),
            limit = 300,
            get_split_device = true,
            support_smart_home = true,
            get_cariot_device = true,
            get_third_device = true
        });

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
            d.CustomIconUrl = ExtractDeviceArtwork(item) ?? d.CustomIconUrl;
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
        var sw = Stopwatch.StartNew();
        try
        {
            await RequestAsync(RunSceneUri, new
            {
                scene_id = scene.SceneId, scene_type = 2, phone_id = "null",
                home_id = scene.HomeId, owner_uid = owner
            });
            TelemetryService.Track("scene_run", "执行场景", new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["trigger"] = "manual",
                ["ms"] = (int)Math.Min(sw.ElapsedMilliseconds, 60000),
            });
        }
        catch
        {
            TelemetryService.Track("scene_run", "执行场景", new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["trigger"] = "manual",
                ["ms"] = (int)Math.Min(sw.ElapsedMilliseconds, 60000),
            });
            throw;
        }
    }

    /// <summary>
    /// 只执行场景里落到指定设备的那几条属性动作：逐条 set_prop 下发，完全不碰云端场景定义。
    /// 用于「我只想开这盏灯，不想整套场景跑一遍」——比改米家场景安全得多，也不受场景里其他设备离线影响。
    /// 返回实际下发条数；tts / 执行其他场景 / 设备组命令这类无法单拆的动作会跳过。
    /// </summary>
    public async Task<(int Applied, int Skipped)> RunSceneOnDeviceAsync(MijiaScene scene, string did)
    {
        var actions = scene.ActionsForDevice(did);
        if (actions.Count == 0) return (0, 0);

        var applied = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var action in actions)
        {
            var writes = action.Writes.Where(w => MijiaScene.SameDid(w.Did ?? action.TargetDid, did)).ToList();
            if (writes.Count == 0)
            {
                // tts 播报、跳转其他场景、设备组命令：参数无法照搬到单设备，跳过并计数
                skipped++;
                continue;
            }

            foreach (var w in writes)
            {
                var value = w.Raw is { } raw ? ReadValue(raw) : null;
                if (value == null)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    await SetPropAsync(w.Did ?? did, w.Siid, w.Piid, value);
                    applied++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{w.Describe()}（{ex.Message}）");
                }
            }
        }

        if (applied == 0 && errors.Count > 0)
            throw new Exception(string.Join("；", errors));

        if (applied == 0 && skipped > 0)
            throw new Exception("这条场景对当前设备只有语音/跳转类动作，无法单独下发");

        return (applied, skipped);
    }

    // ===== 场景 / 自动化全量（2026-11 探针实测结论） =====
    // 两个接口必须一起拿，各缺一半：
    //   GetSimpleSceneList(app_version=12,get_type=2)
    //     → manual_scene_info_list / auto_scene_info_list 分类准确（12 手动 + N 自动化），
    //       且每条已带 scene_action（设备/动作/属性值）与 enable；
    //       但自动化只有 first_trigger，拿不到完整且/或条件与生效时段。
    //   GetSceneList(app_version=12,get_type=2)
    //     → scene_info_list 是全集详情（scene_trigger / scene_condition / scene_action / timewindow / room_id），
    //       但它的 type 字段语义混乱（手动自动化混在一起），且不带 get_type 参数会漏条目。
    // 所以：分类听 Simple，详情听 SceneList，按 scene_id 合并。

    private const string SceneFullListUri = "/appgateway/miot/appsceneservice/AppSceneService/GetSceneList";
    private const string SceneStatusUri = "/appgateway/miot/appsceneservice/AppSceneService/SetSceneStatus";

    /// <summary>编辑 / 新建场景（2026-11 实锤：整条对象原样回传，scene_id="0" 表示新建）。</summary>
    private const string SceneEditUri = "/appgateway/miot/appsceneservice/AppSceneService/Edit";

    /// <summary>删除场景（入参 {scene_id}，实测可用）。</summary>
    private const string SceneDeleteUri = "/appgateway/miot/appsceneservice/AppSceneService/Delete";

    /// <summary>
    /// 只取「场景/自动化 → 启停态」的轻量同步：每个家庭一次 GetSimpleSceneList（简化版，带 enable）。
    /// 用于本应用在某处改了开关、或用户在手机 App 上改了之后把界面状态对齐，
    /// 不重建列表、不拉动作详情，网络开销远小于 GetSceneBundleAsync。
    /// </summary>
    public async Task<Dictionary<string, bool>> GetSceneEnableMapAsync()
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        var homes = _homes.Count > 0 ? _homes : await GetHomesAsync();

        foreach (var home in homes)
        {
            var raw = await TryRawAsync(SceneListUri, new
            {
                app_version = 12, get_type = 2, home_id = home.Id, owner_uid = home.Uid
            });
            if (Unwrap(raw) is not { } result) continue;

            foreach (var listName in new[] { "manual_scene_info_list", "auto_scene_info_list" })
            {
                foreach (var s in ReadSceneList(result, listName))
                {
                    if (!string.IsNullOrEmpty(s.SceneId)) map[s.SceneId] = s.Enable;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// 启停自动化 / 场景。2026-11 探针实测：SetSceneStatus 幂等写（原值写回）返回 code=0、result=true，
    /// 复核状态未变，说明它就是启停入口；用户实测手机米家 App 同步变化。
    /// 入参同时带 enable(bool) 与 status(1/0)：接口只认其中一个，两个给一致值最稳。
    /// </summary>
    public async Task SetSceneEnabledAsync(MijiaScene scene, bool enable)
    {
        var owner = _homes.Find(h => h.Id == scene.HomeId)?.Uid ?? 0;
        var r = await RequestAsync(SceneStatusUri, new
        {
            app_version = 12,
            scene_id = scene.SceneId,
            enable,
            status = enable ? 1 : 0,
            home_id = scene.HomeId,
            owner_uid = owner
        });
        if (r.ValueKind == JsonValueKind.False)
            throw new Exception("云端未接受这次启停操作");
    }

    /// <summary>
    /// 保存（编辑或新建）场景。整条原始对象原样回传，只做必要字段覆盖，避免漏字段被服务端拒绝。
    /// <paramref name="mutate"/> 就地修改报文（改名称、改动作值等）。
    /// 返回服务端确认的 scene_id（新建时是新 id）。
    /// </summary>
    public async Task<string> SaveSceneAsync(MijiaScene scene, Action<System.Text.Json.Nodes.JsonObject>? mutate = null)
    {
        var body = BuildEditPayload(scene);
        mutate?.Invoke(body);
        return await PostEditAsync(body, scene.SceneId);
    }

    /// <summary>
    /// 以场景为模板新建一条副本。副本默认关闭，避免复制完成瞬间就被触发。
    /// </summary>
    public async Task<string> DuplicateSceneAsync(MijiaScene scene, string newName)
    {
        var body = BuildEditPayload(scene);
        body["scene_id"] = "0";
        body["name"] = newName;
        body["enable"] = false;
        body["enable_push"] = false;
        body["common_use"] = false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        body["create_time"] = now;
        body["update_time"] = now;
        return await PostEditAsync(body, "");
    }

    /// <summary>删除场景 / 自动化。实测入参只要 scene_id + 家庭信息。</summary>
    public async Task DeleteSceneAsync(MijiaScene scene)
    {
        var owner = _homes.Find(h => h.Id == scene.HomeId)?.Uid ?? 0;
        await RequestAsync(SceneDeleteUri, new
        {
            app_version = 12,
            scene_id = scene.SceneId,
            home_id = scene.HomeId,
            owner_uid = owner
        });
    }

    /// <summary>
    /// 构造编辑报文：以原始 JSON 为底（编辑接口要求整条对象结构完整），
    /// 再补 app_version / home_id / owner_uid 这几个接口级字段。
    /// </summary>
    private System.Text.Json.Nodes.JsonObject BuildEditPayload(MijiaScene scene)
    {
        if (scene.Raw == null)
            throw new Exception("这条场景没有保留原始报文，请刷新后重试");

        var body = (System.Text.Json.Nodes.JsonObject)scene.Raw.DeepClone();
        var owner = _homes.Find(h => h.Id == scene.HomeId)?.Uid
                    ?? (long.TryParse(body["uid"]?.ToString(), out var u) ? u : 0);

        body["app_version"] = 12;
        body["home_id"] = scene.HomeId;
        body["owner_uid"] = owner;
        if (body["uid"] == null) body["uid"] = owner.ToString();
        return body;
    }

    private async Task<string> PostEditAsync(System.Text.Json.Nodes.JsonObject body, string fallbackId)
    {
        var r = await RequestAsync(SceneEditUri, body);
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("scene_id", out var sid))
            return sid.ToString();
        return fallbackId;
    }

    /// <summary>
    /// 拉当前所有家庭的场景 + 自动化，已合并详情并按 <see cref="MijiaScene.IsAutomation"/> 分类。
    /// 详情接口失败时降级为简化列表（仍有动作与启用态，只是没有完整触发条件）。
    /// </summary>
    public async Task<List<MijiaScene>> GetSceneBundleAsync()
    {
        var homes = _homes.Count > 0 ? _homes : await GetHomesAsync();
        var tasks = homes.Select(LoadHomeScenesAsync);
        var lists = await Task.WhenAll(tasks);
        return lists.SelectMany(x => x).ToList();
    }

    private async Task<List<MijiaScene>> LoadHomeScenesAsync(MijiaHome home)
    {
        var simpleTask = TryRawAsync(SceneListUri, new
        {
            app_version = 12, get_type = 2, home_id = home.Id, owner_uid = home.Uid
        });
        var fullTask = TryRawAsync(SceneFullListUri, new
        {
            app_version = 12, get_type = 2, home_id = home.Id, owner_uid = home.Uid
        });
        await Task.WhenAll(simpleTask, fullTask);

        var manual = new List<MijiaScene>();
        var autos = new List<MijiaScene>();
        if (Unwrap(simpleTask.Result) is { } simple)
        {
            manual = ReadSceneList(simple, "manual_scene_info_list");
            autos = ReadSceneList(simple, "auto_scene_info_list");
        }

        var detail = new Dictionary<string, MijiaScene>(StringComparer.Ordinal);
        if (Unwrap(fullTask.Result) is { } full)
        {
            foreach (var d in ReadSceneList(full, "scene_info_list"))
                if (!string.IsNullOrEmpty(d.SceneId)) detail[d.SceneId] = d;
        }

        var result = new List<MijiaScene>(manual.Count + autos.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(MijiaScene item, bool isAutomation)
        {
            if (string.IsNullOrEmpty(item.SceneId) || !seen.Add(item.SceneId)) return;
            item.HomeId = home.Id;
            item.IsAutomation = isAutomation;
            result.Add(item);
        }

        // 分类以简化列表为准，详情用来补字段（详情对象本身带动作与触发条件）
        foreach (var s in manual) Add(detail.TryGetValue(s.SceneId, out var d) ? d : s, false);
        foreach (var s in autos) Add(detail.TryGetValue(s.SceneId, out var d) ? d : s, true);

        // 详情里多出来的（简化列表没覆盖到的）按有无触发条件归类
        foreach (var d in detail.Values)
        {
            if (seen.Contains(d.SceneId)) continue;
            Add(d, d.Triggers.Count > 0);
        }

        return result;
    }

    /// <summary>剥掉 {code,message,result} 外壳；接口直接返回结果时原样给出。</summary>
    private static JsonElement? Unwrap(JsonElement? raw)
    {
        if (raw is not { } r) return null;
        if (r.ValueKind != JsonValueKind.Object) return r;
        return r.TryGetProperty("result", out var res) ? res : r;
    }

    private static List<MijiaScene> ReadSceneList(JsonElement result, string arrayName)
    {
        var list = new List<MijiaScene>();
        if (result.ValueKind != JsonValueKind.Object) return list;
        if (!result.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            MijiaScene? s = null;
            try { s = el.Deserialize<MijiaScene>(); } catch { }
            if (s == null || string.IsNullOrEmpty(s.SceneId)) continue;
            // 留一份原始 JSON：编辑接口要整条回传，缺字段会被服务端拒绝
            try { s.Raw = System.Text.Json.Nodes.JsonNode.Parse(el.GetRawText())?.AsObject(); } catch { }
            list.Add(s);
        }
        return list;
    }

    /// <summary>取原始响应，任何异常都返回 null（用于可降级的辅助接口）。</summary>
    private async Task<JsonElement?> TryRawAsync(string uri, object dataObj)
    {
        try
        {
            return await RequestRawAsync(uri, dataObj);
        }
        catch
        {
            return null;
        }
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
    /// 米家扩展程序（插件）元数据：返回 latest_info.download_url / 版本等。
    /// 产品配置里没有图标的型号（如 togo.light.t1），米家列表图实际来自插件包，需要靠这里定位。
    /// </summary>
    public async Task<JsonElement> FetchPluginAsync(string model, int apiVersion = 10117)
    {
        var plugins = new[] { new { model } };
        return await RequestAsync("/v2/plugin/fetch_plugin", new
        {
            is_car_mihome = false,
            latest_req = new
            {
                api_version = apiVersion,
                app_platform = "Android",
                region = "CN",
                plugins,
                package_type = ""
            },
            backup_req = new
            {
                plugins,
                api_level = 111,
                app_platform = "phone"
            }
        });
    }

    /// <summary>产品配置原始查询：按型号问云端要指定字段，一次最多 100 个型号（诊断/取图共用）。</summary>
    public async Task<JsonElement> QueryProductConfigAsync(IEnumerable<string> models, params string[] keys)
    {
        var batch = models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        return await RequestAsync("/v2/productconfig/single_pull", new { models = batch, keys });
    }

    /// <summary>
    /// 产品拟物图：米家 App 自己用的型号 → 图标来源。`/v2/productconfig/single_pull`
    /// 返回 [{model,name,icon_real}]，覆盖百科产品库查不到、但开发者已经上传拟物图的新型号
    /// （trytogo 这类三方灯具就属于这种）；百科接口只作为兜底。
    /// 一次最多 100 个型号，整批打包成一个签名请求，避免几十张卡片各打一次云。
    /// </summary>
    public async Task<Dictionary<string, string>> GetProductIconUrlsAsync(IEnumerable<string> models)
    {
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var start = 0; start < pending.Count; start += 100)
        {
            var batch = pending.Skip(start).Take(100).ToArray();
            JsonElement r;
            try
            {
                r = await QueryProductConfigAsync(batch, "model", "name", "icon_real");
            }
            catch
            {
                // 图标是装饰性数据，接口失败不能拖垮设备列表
                continue;
            }
            if (r.ValueKind != JsonValueKind.Array) continue;

            foreach (var row in r.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!row.TryGetProperty("model", out var m) || m.ValueKind != JsonValueKind.String) continue;
                if (!row.TryGetProperty("icon_real", out var icon) || icon.ValueKind != JsonValueKind.String) continue;
                var model = m.GetString();
                var url = icon.GetString();
                if (!string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(url))
                    urls[model.Trim()] = url.Trim();
            }
        }
        return urls;
    }

    /// <summary>单个型号的拟物图地址，探针用；正常加载走批量接口。</summary>
    public async Task<string?> GetProductIconUrlAsync(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var urls = await GetProductIconUrlsAsync(new[] { model });
        return urls.TryGetValue(model.Trim(), out var url) ? url : null;
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

    /// <summary>灯详情：亮度 + 色温（+ 预设 mode，型号没有这条属性或没读到为 null）。</summary>
    public sealed record LightState(int? Brightness, int? ColorTemp, int? Mode);

    /// <summary>读灯当前值；siid/piid 由规格解析出的 LightSchema 决定，未解析时回落 light 服务 2/2、2/3。</summary>
    public async Task<LightState?> GetLightStateAsync(string did, Models.LightControlSchema? schema = null)
    {
        var bSiid = schema is { HasBrightness: true } s1 ? s1.BrightSiid : 2;
        var bPiid = schema is { HasBrightness: true } s2 ? s2.BrightPiid : 2;
        var cSiid = schema is { HasColorTemp: true } s3 ? s3.CtSiid : 2;
        var cPiid = schema is { HasColorTemp: true } s4 ? s4.CtPiid : 3;

        var props = new List<(int, int)> { (bSiid, bPiid), (cSiid, cPiid) };
        if (schema is { ModePiid: > 0 } s5) props.Add((s5.ModeSiid, s5.ModePiid));

        var m = await GetPropsAsync(did, props.ToArray());
        if (m.Count == 0) return null;

        var bright = m.TryGetValue(bSiid * 1000 + bPiid, out var bv) && bv != null ? ToInt(bv, 0) : (int?)null;
        var ct = m.TryGetValue(cSiid * 1000 + cPiid, out var cv) && cv != null ? ToInt(cv, 0) : (int?)null;
        var mode = schema is { ModePiid: > 0 } s6 &&
                   m.TryGetValue(s6.ModeSiid * 1000 + s6.ModePiid, out var mv) && mv != null
            ? ToInt(mv, 0)
            : (int?)null;

        return new LightState(bright, ct, mode);
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
    ///
    /// 注意：外层 code=0 只代表云端收下了请求，设备是否真的执行要看 result 里每条的 code
    /// （<see cref="ThrowIfDeviceRejected"/>）。老固件不支持这条动作时外层照样回 0，
    /// 以前不检查就会「界面提示已发送、音箱毫无反应」。
    /// </summary>
    public async Task RunActionWithParamsAsync(string did, int siid, int aiid, object? input)
    {
        var payload = input == null
            ? (object)new { did, siid, aiid }
            : new { did, siid, aiid, @in = input };
        var r = await RequestAsync("/miotspec/action", new { @params = payload });
        ThrowIfDeviceRejected(r, siid, aiid);
    }

    /// <summary>动作结果逐条看 code：负数才是设备/固件拒绝（-704xxx 不支持/越界），0/1 视为已受理。</summary>
    private static void ThrowIfDeviceRejected(JsonElement result, int siid, int aiid)
    {
        void Check(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) return;
            if (!item.TryGetProperty("code", out var c) || c.ValueKind != JsonValueKind.Number) return;
            var code = c.GetInt32();
            if (code >= 0) return;
            var msg = item.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            throw new Exception(string.IsNullOrEmpty(msg)
                ? $"设备拒绝了动作 siid={siid} aiid={aiid}（code {code}）"
                : $"设备拒绝了动作 siid={siid} aiid={aiid}（code {code} {msg}）");
        }

        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray()) Check(item);
        }
        else Check(result);
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
    /// 云端按条目回 code，0/1 是成功，负数才是被设备拒绝（-704xxx 是不支持/越界，-400 只读）。
    /// </summary>
    public async Task SetPropAsync(string did, int siid, int piid, object value)
    {
        var r = await RequestAsync("/miotspec/prop/set", new
        {
            @params = new[] { new { did, siid, piid, value } }
        });
        // 单条写的 result 是对象、批量写才是数组（云端两种都出现过）。以前只认数组，
        // 对象回包一律当成成功返回，条目里的负 code 全被吞掉——界面记「已下发」而设备毫无动作。
        ThrowIfPropRejected(r, did, siid, piid);
    }

    /// <summary>属性写结果逐条看 code：对象/数组都解析，负数才抛（0/1 是已受理）。</summary>
    private static void ThrowIfPropRejected(JsonElement result, string did, int siid, int piid)
    {
        void Check(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) return;
            if (!item.TryGetProperty("code", out var c) || c.ValueKind != JsonValueKind.Number) return;
            var code = c.GetInt32();
            if (code >= 0) return;
            var msg = item.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            throw new Exception(string.IsNullOrEmpty(msg)
                ? $"设备拒绝了指令 {did} siid={siid} piid={piid}（code {code}）"
                : $"设备拒绝了指令 {did} siid={siid} piid={piid}（code {code} {msg}）");
        }

        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray()) Check(item);
        }
        else Check(result);
    }

    /// <summary>bool 开关下发：优先用规格解析出的 siid/piid，未解析时回落 siid=2/piid=1。</summary>
    public async Task SetPowerAsync(MijiaDevice device, bool on)
    {
        // 统计只记设备类别与成败，不记 did / 设备名 / 房间
        var kind = DeviceKind(device.SpecType);
        var sw = Stopwatch.StartNew();
        try
        {
            await SetPropAsync(device.TargetDid, device.PowerSiid, device.PowerPiid, on);
            // 成功分支量最大：同一类别/动作在会话内合批，一条事件代表 _merge_n 次操作；失败仍逐条上报
            TelemetryService.TrackRollup("device_control", "开关设备", $"{kind}|{(on ? "on" : "off")}|ok",
                new Dictionary<string, object?>
                {
                    ["kind"] = kind,
                    ["action"] = on ? "on" : "off",
                    ["ok"] = true,
                },
                (int)Math.Min(sw.ElapsedMilliseconds, 60000));
        }
        catch
        {
            TelemetryService.Track("device_control", "开关设备", new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["action"] = on ? "on" : "off",
                ["ok"] = false,
                ["ms"] = (int)Math.Min(sw.ElapsedMilliseconds, 60000),
            });
            throw;
        }
    }

    /// <summary>从 MIoT 规格串里取设备大类（light / air-conditioner / curtain …），解析不出就 other。</summary>
    private static string DeviceKind(string? specType)
    {
        if (string.IsNullOrWhiteSpace(specType)) return "other";
        var parts = specType.Split(':');
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i] == "device" && parts[i + 1].Length > 0) return parts[i + 1];
        }
        return "other";
    }

    /// <summary>多路开关的一路下发；虚拟子设备同样打父 did + 通道 siid。</summary>
    public async Task SetChannelAsync(SwitchChannelState channel, bool on)
    {
        if (channel.Device is not { } device) return;
        await SetPropAsync(device.TargetDid, channel.Siid, channel.Piid, on);
    }

    /// <summary>灯亮度：按规格的 siid/piid 与值域下发；未解析规格时回落 light 服务 2/2、1–100。</summary>
    public async Task SetLightBrightnessAsync(string did, int brightness, Models.LightControlSchema? schema = null)
    {
        var ls = schema is { HasBrightness: true } ? schema : null;
        await SetPropAsync(did, ls?.BrightSiid ?? 2, ls?.BrightPiid ?? 2,
            SnapToRange(brightness, ls?.BrightMin ?? 1, ls?.BrightMax ?? 100, ls?.BrightStep ?? 1));
    }

    /// <summary>
    /// 灯色温，单位 K：siid/piid 按设备规格，量程由调用方给定（设备规格或用户自定义）。
    /// 自定义可以超出规格标注——不少型号真机能力比规格更宽；真机不认的值云端会拒收，调用方据此回读退回。
    /// </summary>
    public async Task SetLightColorTempAsync(string did, int kelvin, Models.LightControlSchema? schema = null,
                                             double? rangeMin = null, double? rangeMax = null)
    {
        var ls = schema is { HasColorTemp: true } ? schema : null;
        await SetPropAsync(did, ls?.CtSiid ?? 2, ls?.CtPiid ?? 3,
            SnapToRange(kelvin, rangeMin ?? ls?.CtMin ?? 1000, rangeMax ?? ls?.CtMax ?? 10000, ls?.CtStep ?? 1));
    }

    /// <summary>按步进取整再夹进量程：设备只认区间内的整点，越界写入会被直接拒绝。</summary>
    private static int SnapToRange(int value, double min, double max, double step)
    {
        var s = step <= 0 ? 1 : step;
        var v = Math.Round(value / s) * s;
        v = Math.Clamp(v, min, max);
        return (int)Math.Round(v);
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

    /// <summary>
    /// 米家数据统计接口（app/v2/user/statistics，只给最近 365 天）：按小时/按天取某个统计项的累计值。
    /// 设备没申请统计配置、key 不对时云端回非 0 code，这里统一当“不支持”返回 Ok=false，交给调用方换 key 或放弃。
    /// </summary>
    public async Task<StatSeries> GetStatisticsAsync(string did, string key, string dataType, DateTime from, DateTime to,
        int limit = 0)
    {
        // user/statistics 只认 api.mijia.tech 这套会话凭证（api.io.mi.com 直接回 auth error），所以只打一个域名
        var body = new
        {
            did,
            key,
            data_type = dataType,
            time_start = new DateTimeOffset(from).ToUnixTimeSeconds(),
            time_end = new DateTimeOffset(to).ToUnixTimeSeconds(),
            limit
        };

        // 只有 api.mijia.tech 这套会话凭证能用（api.io.mi.com 会直接回 auth error），所以只打一个域名
        var raw = await TryRawAsync("/v2/user/statistics", body);
        return ParseStatSeries(raw);
    }

    /// <summary>把统计接口的原始信封解析成 <see cref="StatSeries"/>。</summary>
    private static StatSeries ParseStatSeries(JsonElement? raw)
    {
        if (raw is null) return new StatSeries(false, Array.Empty<(DateTime, double)>(), -999, "no response");

        var code = 0;
        var message = "";
        if (raw.Value.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            message = m.GetString() ?? "";
        if (!raw.Value.TryGetProperty("code", out var c) ||
            c.ValueKind != JsonValueKind.Number || (code = c.GetInt32()) != 0)
            return new StatSeries(false, Array.Empty<(DateTime, double)>(), code, message);

        if (!raw.Value.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.Array)
            return new StatSeries(true, Array.Empty<(DateTime, double)>());

        var items = new List<(DateTime Time, double Value)>();
        foreach (var item in r.EnumerateArray())
        {
            if (!item.TryGetProperty("time", out var t) || t.ValueKind != JsonValueKind.Number) continue;
            var text = item.TryGetProperty("value", out var v) ? v.ToString() : null;
            if (ParseStatNumber(text) is not { } number) continue;
            items.Add((DateTimeOffset.FromUnixTimeSeconds(t.GetInt64()).ToLocalTime().DateTime, number));
        }
        return new StatSeries(true, items);
    }

    /// <summary>统计值形如 "[22638]" / "22638" / "[51.8,56.9]"（多值取第一个）。</summary>
    private static double? ParseStatNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('[').TrimEnd(']');
        var first = s.Split(',')[0].Trim().Trim('"');
        return double.TryParse(first, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    }


    /// <summary>
    /// 释放底层 HttpClient 与其连接池。每次登录/重登都会新建一个 MijiaClient，
    /// 不复用也不释放就会把旧会话的 socket 与 handler 一直挂在进程里；退出登录、
    /// 切换账号时务必 Dispose，否则常驻内存随登录次数线性增长。
    /// </summary>
    public void Dispose()
    {
        try { _http.Dispose(); } catch { }
    }
}

/// <summary>数据统计接口的一次返回：<c>Ok</c> 表示云端认这个 key（哪怕是空结果）；<c>Items</c> 是「时间 → 值」。
/// 失败时 <c>Code</c>/<c>Message</c> 带上云端原始错误，便于区分「key 不对」和「接口不可用」。</summary>
public sealed record StatSeries(bool Ok, IReadOnlyList<(DateTime Time, double Value)> Items,
    int Code = 0, string Message = "");
