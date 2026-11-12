// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>
/// 场景 / 自动化接口探针（只读诊断，不写任何云端数据）。
/// 用本地 auth.json 的登录态，把候选接口逐个打一遍，原始响应原样落盘。
///
/// 用法：MiHome.exe --scene-probe
/// 输出：%APPDATA%\Feiyu\MiHome\scene-probe\
///   summary.txt        每个接口的 code / message / 顶层字段
///   01-xxx.json ...    各接口完整原始响应
///   done.flag          跑完标记
///
/// 目的：确认「场景详情（action_list）」「自动化列表」「按设备查场景」到底走哪个接口，
/// 拿到结构后再决定业务层怎么写。跑完即可删除本文件与 Program 里的开关。
/// </summary>
internal static class SceneProbe
{
    private const string SceneBase = "/appgateway/miot/appsceneservice/AppSceneService/";
    private const string AutoBase = "/appgateway/miot/appsceneservice/AutomationService/";
    private const string AutoBase2 = "/appgateway/miot/automationservice/AutomationService/";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string _dir = "";
    private static readonly StringBuilder Log = new();
    private static MijiaClient? _client;
    private static int _idx;

    public static async Task RunAsync()
    {
        _dir = Path.Combine(AppPaths.Dir, "scene-probe");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir))
        {
            try { File.Delete(f); } catch { }
        }

        W($"MiHome 场景接口探针  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"输出目录：{_dir}");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken))
        {
            W("!! 没有登录态：请先在主程序里扫码登录后重跑");
            Finish();
            return;
        }

        if (!auth.HasValidAuth())
        {
            W(".. serviceToken 已过期，尝试用 passToken 续期");
            var ok = await auth.RefreshServiceTokenAsync();
            W($".. 续期{(ok ? "成功" : "失败：" + (auth.LastRefreshError ?? "未知"))}");
            if (!ok)
            {
                Finish();
                return;
            }
        }

        _client = new MijiaClient(auth);

        List<MijiaHome> homes;
        try
        {
            homes = await _client.GetHomesAsync(forceRefresh: true);
        }
        catch (Exception ex)
        {
            W($"!! 拉家庭列表失败：{ex.Message}");
            Finish();
            return;
        }

        W($"家庭 {homes.Count} 个");
        foreach (var h in homes)
            W($"   - {h.Name}  id={h.Id}  uid={h.Uid}  rooms={h.Rooms.Count}");

        var home = homes.FirstOrDefault();
        if (home == null)
        {
            W("!! 没有家庭");
            Finish();
            return;
        }
        W($"使用家庭：{home.Name}（多家庭只探第一个）");
        W("");

        // ===== 1. 手动场景列表（现有实现基线） =====
        var c1 = await Case("01-simple-scene-get_type2", SceneBase + "GetSimpleSceneList",
            new { app_version = 12, get_type = 2, home_id = home.Id, owner_uid = home.Uid });

        await Case("02-simple-scene-get_type0", SceneBase + "GetSimpleSceneList",
            new { app_version = 12, get_type = 0, home_id = home.Id, owner_uid = home.Uid });

        await Case("03-simple-scene-get_type1", SceneBase + "GetSimpleSceneList",
            new { app_version = 12, get_type = 1, home_id = home.Id, owner_uid = home.Uid });

        // ===== 2. 完整场景列表（看是否直接带 action_list） =====
        await Case("04-scene-list", SceneBase + "GetSceneList",
            new { home_id = home.Id, owner_uid = home.Uid });

        await Case("05-scene-list-with-type", SceneBase + "GetSceneList",
            new { app_version = 12, get_type = 2, home_id = home.Id, owner_uid = home.Uid });

        await Case("06-scene-list-v2", SceneBase + "GetSceneListV2",
            new { app_version = 12, home_id = home.Id, owner_uid = home.Uid });

        await Case("07-new-scene-list", SceneBase + "NewGetSceneList",
            new { app_version = 12, get_type = 2, home_id = home.Id, owner_uid = home.Uid });

        // ===== 3. 单个场景详情（拿 scene_id 再打） =====
        var sceneId = FindString(c1, "scene_id");
        var sceneName = FindString(c1, "name");
        W($"首个场景：id={sceneId ?? "(未取到)"} name={sceneName}");
        W("");

        if (!string.IsNullOrEmpty(sceneId))
        {
            await Case("08-scene-info", SceneBase + "GetSceneInfo",
                new { scene_id = sceneId, home_id = home.Id, owner_uid = home.Uid });

            await Case("09-scene-detail", SceneBase + "GetSceneDetail",
                new { scene_id = sceneId, home_id = home.Id, owner_uid = home.Uid });

            await Case("10-scene-info-v2", SceneBase + "GetSceneInfoV2",
                new { scene_id = sceneId, home_id = home.Id, owner_uid = home.Uid });

            await Case("11-scene-action-list", SceneBase + "GetSceneActionList",
                new { scene_id = sceneId, home_id = home.Id, owner_uid = home.Uid });
        }

        // ===== 4. 自动化（如果…就…） =====
        await Case("12-automation-list", AutoBase + "GetAutomationList",
            new { home_id = home.Id, owner_uid = home.Uid });

        await Case("13-automation-list-appver", AutoBase + "GetAutomationList",
            new { app_version = 12, home_id = home.Id, owner_uid = home.Uid });

        await Case("14-home-automation-list", AutoBase + "GetHomeAutomationList",
            new { home_id = home.Id, owner_uid = home.Uid });

        await Case("15-automation-list-alt", AutoBase2 + "GetAutomationList",
            new { home_id = home.Id, owner_uid = home.Uid });

        await Case("16-automation-list-in-scene-svc", SceneBase + "GetAutomationList",
            new { home_id = home.Id, owner_uid = home.Uid });

        await Case("17-simple-automation-list", AutoBase + "GetSimpleAutomationList",
            new { app_version = 12, home_id = home.Id, owner_uid = home.Uid });

        // ===== 5. 按设备查场景（若存在，设备详情可省掉本地索引） =====
        var did = await FirstDidAsync(home);
        W($"首个设备 did：{did ?? "(未取到)"}");
        W("");
        if (!string.IsNullOrEmpty(did))
        {
            await Case("18-scene-list-by-did", SceneBase + "GetSceneListByDid",
                new { did, home_id = home.Id, owner_uid = home.Uid });

            await Case("19-scene-list-by-did-alt", SceneBase + "GetSceneListByDevice",
                new { did, home_id = home.Id, owner_uid = home.Uid });
        }

        Finish();
    }

    private static async Task<JsonElement?> Case(string name, string uri, object payload)
    {
        _idx++;
        var file = Path.Combine(_dir, name + ".json");
        var payloadText = JsonSerializer.Serialize(payload, Pretty).Replace("\r", "").Replace("\n", " ");
        W($"[{_idx:00}] {name}");
        W($"     {uri}");
        W($"     payload {payloadText}");

        try
        {
            var ret = await _client!.RequestRawAsync(uri, payload);
            var raw = JsonSerializer.Serialize(ret, Pretty);
            try { File.WriteAllText(file, raw, Encoding.UTF8); } catch { }

            var code = ret.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32().ToString() : "-";
            var msg = ret.TryGetProperty("message", out var m) ? m.ToString() : "";
            W($"     code={code}  msg={msg}");
            W($"     shape: {Describe(ret)}");
            W("");
            return ret;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(file, $"{{\"probe_error\":\"{ex.GetType().Name}: {ex.Message.Replace("\"", "'")}\"}}", Encoding.UTF8); } catch { }
            W($"     ERROR {ex.GetType().Name}: {ex.Message}");
            W("");
            return null;
        }
    }

    /// <summary>描述响应结构：顶层字段 + result 内字段（数组给长度与首元素字段）。</summary>
    private static string Describe(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return el.ValueKind.ToString();
        var sb = new StringBuilder("{");
        sb.Append(string.Join(", ", el.EnumerateObject().Select(p => p.Name).Take(12)));
        sb.Append('}');

        if (el.TryGetProperty("result", out var r))
        {
            sb.Append("  result=");
            if (r.ValueKind == JsonValueKind.Object)
            {
                sb.Append('{').Append(string.Join(", ", r.EnumerateObject().Select(p => p.Name).Take(16))).Append('}');
                // 常见容器：manual_scene_info_list / automation_list 等
                foreach (var p in r.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).Take(4))
                {
                    var arr = p.Value;
                    var first = arr.GetArrayLength() > 0 ? arr[0] : default;
                    var fields = first.ValueKind == JsonValueKind.Object
                        ? string.Join(", ", first.EnumerateObject().Select(x => x.Name).Take(24))
                        : first.ValueKind.ToString();
                    sb.Append($"\n             {p.Name}[{arr.GetArrayLength()}] -> {fields}");
                }
            }
            else if (r.ValueKind == JsonValueKind.Array)
            {
                var first = r.GetArrayLength() > 0 ? r[0] : default;
                var fields = first.ValueKind == JsonValueKind.Object
                    ? string.Join(", ", first.EnumerateObject().Select(x => x.Name).Take(24))
                    : first.ValueKind.ToString();
                sb.Append($"[{r.GetArrayLength()}] -> {fields}");
            }
            else
            {
                sb.Append(r.ValueKind.ToString());
            }
        }
        return sb.ToString();
    }

    /// <summary>递归找第一个指定字段的字符串值（用于从列表里抠 scene_id）。</summary>
    private static string? FindString(JsonElement? el, string key)
    {
        if (el is not { } e) return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == key)
                    {
                        return p.Value.ValueKind switch
                        {
                            JsonValueKind.String => p.Value.GetString(),
                            JsonValueKind.Number => p.Value.GetRawText(),
                            _ => null
                        };
                    }
                    var found = FindString(p.Value, key);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    var found = FindString(item, key);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
                return null;
            default:
                return null;
        }
    }

    private static async Task<string?> FirstDidAsync(MijiaHome home)
    {
        try
        {
            var devices = await _client!.GetDevicesAsync(home);
            return devices.FirstOrDefault()?.Did;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 自动化「启停」接口探针（幂等写）：只把当前 enable 值原样写回去，语义上零变化，
    /// 用来确认接口名与入参字段，避免拿真实自动化做实验。
    /// 用法：MiHome.exe --scene-probe-enable
    /// 输出：%APPDATA%\Feiyu\MiHome\scene-probe-enable\
    /// </summary>
    public static async Task RunEnableAsync()
    {
        _dir = Path.Combine(AppPaths.Dir, "scene-probe-enable");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir))
        {
            try { File.Delete(f); } catch { }
        }

        W($"MiHome 启停接口探针（幂等写）  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"输出目录：{_dir}");
        W("原则：只把每条自动化当前的 enable 原值写回，不改变任何实际状态。");
        W("");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken))
        {
            W("!! 没有登录态：请先在主程序里登录后重跑");
            Finish();
            return;
        }

        if (!auth.HasValidAuth())
        {
            var ok = await auth.RefreshServiceTokenAsync();
            W($".. serviceToken 续期{(ok ? "成功" : "失败：" + (auth.LastRefreshError ?? "未知"))}");
            if (!ok) { Finish(); return; }
        }

        _client = new MijiaClient(auth);

        List<MijiaScene> scenes;
        try
        {
            scenes = await _client.GetSceneBundleAsync();
        }
        catch (Exception ex)
        {
            W($"!! 拉场景失败：{ex.Message}");
            Finish();
            return;
        }

        var autos = scenes.Where(s => s.IsAutomation).ToList();
        W($"自动化 {autos.Count} 条");
        var target = autos.FirstOrDefault(s => s.Enable) ?? autos.FirstOrDefault();
        if (target == null)
        {
            W("!! 没有自动化可探，结束");
            Finish();
            return;
        }

        long uid = 0;
        try
        {
            var homes = await _client.GetHomesAsync();
            uid = homes.FirstOrDefault(h => h.Id == target.HomeId)?.Uid ?? homes.FirstOrDefault()?.Uid ?? 0;
        }
        catch { }
        W($"探针目标：{target.Name}  scene_id={target.SceneId}  enable={target.Enable}  home={target.HomeId}  uid={uid}");
        W("");

        var candidates = new (string Name, string Uri, object Body)[]
        {
            ("e01-set-scene-enable", SceneBase + "SetSceneEnable",
                new { app_version = 12, scene_id = target.SceneId, enable = target.Enable, home_id = target.HomeId, owner_uid = uid }),
            ("e02-enable-scene", SceneBase + "EnableScene",
                new { app_version = 12, scene_id = target.SceneId, enable = target.Enable, home_id = target.HomeId, owner_uid = uid }),
            ("e03-update-scene-enable", SceneBase + "UpdateSceneEnable",
                new { app_version = 12, scene_id = target.SceneId, enable = target.Enable, home_id = target.HomeId, owner_uid = uid }),
            ("e04-set-scene-status", SceneBase + "SetSceneStatus",
                new { app_version = 12, scene_id = target.SceneId, enable = target.Enable, status = target.Enable ? 1 : 0, home_id = target.HomeId, owner_uid = uid }),
            ("e05-set-scene-enable-int", SceneBase + "SetSceneEnable",
                new { app_version = 12, scene_id = target.SceneId, enable = target.Enable ? 1 : 0, home_id = target.HomeId, owner_uid = uid }),
        };

        foreach (var c in candidates)
            await Case(c.Name, c.Uri, c.Body);

        // 复核：状态必须与探针前一致
        try
        {
            var after = await _client.GetSceneBundleAsync();
            var again = after.FirstOrDefault(s => s.SceneId == target.SceneId);
            W($"复核：{target.Name}  enable {target.Enable} → {again?.Enable}");
            W(again != null && again.Enable == target.Enable ? "状态未变 ✓（探针安全）" : "!! 状态被改动，请到米家 App 确认");
        }
        catch (Exception ex)
        {
            W($"复核失败：{ex.Message}");
        }

        Finish();
    }

    /// <summary>
    /// 自动化「编辑」接口探针（阶段一：只探是否存在，不改任何真实对象）。
    /// 全部请求都用不存在的 scene_id="0" 或空 body，预期全部失败，
    /// 通过错误码/错误文案判断：接口是否存在、缺哪些字段。
    /// 用法：MiHome.exe --scene-probe-edit
    /// 输出：%APPDATA%\Feiyu\MiHome\scene-probe-edit\
    /// </summary>
    public static async Task RunEditAsync()
    {
        _dir = Path.Combine(AppPaths.Dir, "scene-probe-edit");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir))
        {
            try { File.Delete(f); } catch { }
        }

        W($"MiHome 自动化编辑接口探针（阶段一：不碰真实对象）  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"输出目录：{_dir}");
        W("原则：所有写请求都用 scene_id=\"0\"（不存在）或空 body，只会失败，不会改动任何现有场景。");
        W("");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken))
        {
            W("!! 没有登录态：请先在主程序里登录后重跑");
            Finish();
            return;
        }

        if (!auth.HasValidAuth())
        {
            var ok = await auth.RefreshServiceTokenAsync();
            W($".. serviceToken 续期{(ok ? "成功" : "失败：" + (auth.LastRefreshError ?? "未知"))}");
            if (!ok) { Finish(); return; }
        }

        _client = new MijiaClient(auth);

        List<MijiaScene> scenes;
        try
        {
            scenes = await _client.GetSceneBundleAsync();
        }
        catch (Exception ex)
        {
            W($"!! 拉场景失败：{ex.Message}");
            Finish();
            return;
        }

        var home = await FirstHomeAsync();
        if (home == null)
        {
            W("!! 没有家庭");
            Finish();
            return;
        }
        W($"使用家庭：{home.Name}  id={home.Id}  uid={home.Uid}");

        var sample = scenes.FirstOrDefault(s => s.IsAutomation && s.Actions.Count > 0) ?? scenes.FirstOrDefault();
        W($"参考对象（只读，不会传给写接口）：{sample?.Name}  id={sample?.SceneId}");
        W($"场景总数 {scenes.Count}，自动化 {scenes.Count(s => s.IsAutomation)}");
        W("");

        const string bogus = "0";
        var hid = home.Id;
        var uid = home.Uid;

        W("===== A. 修改 / 编辑（scene_id 不存在） =====");
        await Case("a1-modify-scene", SceneBase + "ModifyScene",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });
        await Case("a2-edit-scene", SceneBase + "EditScene",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });
        await Case("a3-update-scene", SceneBase + "UpdateScene",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });
        await Case("a4-modify-scene-info", SceneBase + "ModifySceneInfo",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });
        await Case("a5-modify-automation", SceneBase + "ModifyAutomation",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });

        W("===== A2. 其它候选名（同一服务下的别名 / 别的服务前缀） =====");
        foreach (var name in new[]
                 {
                     "SaveScene", "SetScene", "SceneEdit", "EditSceneInfo", "UpdateSceneInfo",
                     "ModifySceneInfoV2", "NewSceneV2", "ImportScene"
                 })
        {
            await Case("a2-" + name.ToLowerInvariant(), SceneBase + name,
                new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });
        }

        await Case("a2-sceneservice-modify", "/appgateway/miot/sceneservice/SceneService/ModifyScene",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });
        await Case("a2-automationservice-modify", AutoBase + "ModifyAutomation",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });

        W("===== B. 新建（空 body，只探字段要求） =====");        await Case("b1-new-scene", SceneBase + "NewScene",
            new { app_version = 12, home_id = hid, owner_uid = uid });
        await Case("b2-add-scene", SceneBase + "AddScene",
            new { app_version = 12, home_id = hid, owner_uid = uid });
        await Case("b3-create-scene", SceneBase + "CreateScene",
            new { app_version = 12, home_id = hid, owner_uid = uid });
        await Case("b4-new-automation", SceneBase + "NewAutomation",
            new { app_version = 12, home_id = hid, owner_uid = uid });

        W("===== C. 删除（scene_id 不存在） =====");
        await Case("c1-del-scene", SceneBase + "DelScene",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });
        await Case("c2-delete-scene", SceneBase + "DeleteScene",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });

        // ===== E. 裸动词接口：Edit / Delete / Get（真实命名风格，见 imkero.net 抓包笔记） =====
        // 全部指向不存在的 scene_id="0"，只观察服务端校验反应，不会改动任何真实对象。
        W("===== E. 裸动词接口 Edit / Delete / Get =====");
        await Case("e1-edit-bare", SceneBase + "Edit",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });
        await Case("e2-delete-bare", SceneBase + "Delete",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });
        await Case("e3-get-bare", SceneBase + "Get",
            new { app_version = 12, scene_id = bogus, home_id = hid, owner_uid = uid });
        // 对照组：已知存在的执行接口，用同样的假 id，看它的报错长什么样
        await Case("e4-runscene-bogus-control", SceneBase + "NewRunScene",
            new { scene_id = bogus, scene_type = 2, phone_id = "null", home_id = hid, owner_uid = uid });

        // 用真实自动化的原始 JSON 当模板（只读），只把 scene_id 换成 "0" 再提交，
        // 检验「完整对象 + 不存在的 id」时服务端是报「场景不存在」还是报「缺字段」。
        JsonObject? template = await LoadSceneTemplateAsync(hid, uid);
        if (template == null)
        {
            W(".. 没取到可用于模板的场景原始 JSON，跳过 E5");
        }
        else
        {
            W($"模板字段：{string.Join(", ", template.Select(p => p.Key))}");
            var body = (JsonObject)template.DeepClone();
            body["scene_id"] = bogus;
            body["app_version"] = 12;
            body["home_id"] = hid;
            body["owner_uid"] = uid;
            try
            {
                File.WriteAllText(Path.Combine(_dir, "e5-template.json"), body.ToJsonString(Pretty), Encoding.UTF8);
            }
            catch { }
            await Case("e5-edit-full-template-bogus-id", SceneBase + "Edit", body);
        }

        W("===== D. 复核：场景数量与启停态未变 =====");
        try
        {
            var after = await _client.GetSceneBundleAsync();
            W($"场景数量 {scenes.Count} → {after.Count}");
            var beforeIds = scenes.Select(s => s.SceneId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var afterIds = after.Select(s => s.SceneId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            W(beforeIds.SequenceEqual(afterIds) ? "scene_id 集合完全一致 ✓（探针安全）" : "!! scene_id 集合变了，请检查");
            var enableChanged = scenes.Count(s => s.IsAutomation
                && after.FirstOrDefault(a => a.SceneId == s.SceneId) is { } x && x.Enable != s.Enable);
            W($"启停态变化条目：{enableChanged}（应为 0）");
        }
        catch (Exception ex)
        {
            W($"复核失败：{ex.Message}");
        }

        Finish();
    }

    private static async Task<MijiaHome?> FirstHomeAsync()
    {
        try
        {
            var homes = await _client!.GetHomesAsync();
            return homes.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 GetSceneList 原始响应里捞一条带触发条件的自动化 JSON，作为 Edit 的字段模板（只读）。
    /// 用服务端原样返回的对象构造 payload，字段最全，后期真做编辑时也不会因为漏字段被当成删除。
    /// </summary>
    private static async Task<JsonObject?> LoadSceneTemplateAsync(string homeId, long uid)
    {
        try
        {
            var raw = await _client!.RequestRawAsync(SceneBase + "GetSceneList",
                new { app_version = 12, get_type = 2, home_id = homeId, owner_uid = uid });
            if (raw.ValueKind != JsonValueKind.Object) return null;
            var result = raw.TryGetProperty("result", out var r) ? r : raw;
            if (!result.TryGetProperty("scene_info_list", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("scene_trigger", out var t) || t.ValueKind != JsonValueKind.Object) continue;
                return JsonNode.Parse(item.GetRawText())?.AsObject();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ===================================================================
    // 克隆探针：验证「整条场景对象回传 AppSceneService/Edit 能否新建」
    // 只新建一条 enable=false 的测试自动化，成功后立刻删除。
    // 依据：小米官方 miot-plugin-sdk 文档 scene.save(opt) → /scene/edit，
    //       opt = { authed:[...], name, identify, setting:{...} }。
    // ===================================================================
    public static async Task RunCloneAsync()
    {
        _dir = Path.Combine(AppPaths.Dir, "scene-probe-clone");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir)) { try { File.Delete(f); } catch { } }

        W($"MiHome 场景克隆探针（新建副本 → 立即删除）  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"输出目录：{_dir}");
        W("原则：只新建一条 enable=false 的测试自动化，不修改任何现有对象，成功后立刻删除。");
        W("");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken))
        {
            W("!! 没有登录态：请先在主程序里登录后重跑");
            Finish();
            return;
        }
        if (!auth.HasValidAuth())
        {
            var ok = await auth.RefreshServiceTokenAsync();
            W($".. serviceToken 续期{(ok ? "成功" : "失败：" + (auth.LastRefreshError ?? "未知"))}");
            if (!ok) { Finish(); return; }
        }
        _client = new MijiaClient(auth);

        var home = await FirstHomeAsync();
        if (home == null) { W("!! 没有家庭"); Finish(); return; }
        var hid = home.Id;
        var uid = home.Uid;
        W($"使用家庭：{home.Name}  id={hid}  uid={uid}");

        var before = await SceneMapAsync(hid, uid);
        W($"改造前场景数：{before.Count}");

        var candidates = await LoadCloneCandidatesAsync(hid, uid);
        if (candidates.Count == 0) { W("!! 没有可用于克隆的模板场景"); Finish(); return; }

        var stamp = DateTime.Now.ToString("HHmmss");
        var testName = $"ZZ-TEST-MiHomeWin-{stamp}-可删";

        (string id, string name)? created = null;
        var tried = 0;
        foreach (var tpl in candidates.Take(4))
        {
            tried++;
            var tplId = tpl["scene_id"]?.ToString() ?? "";
            var tplName = tpl["name"]?.ToString() ?? "";
            W($"===== 第 {tried} 次尝试：模板「{tplName}」 scene_id={tplId} =====");

            // 整条对象原样回传：scene_id 置 "0"、改名、关闭（保证不会触发）
            var body = (JsonObject)tpl.DeepClone();
            body["scene_id"] = "0";
            body["name"] = testName;
            body["enable"] = false;
            body["enable_push"] = false;
            body["common_use"] = false;
            body["create_time"] = NowUnix();
            body["update_time"] = NowUnix();
            body["app_version"] = 12;
            body["home_id"] = hid;
            body["owner_uid"] = uid;
            Dump($"try{tried}-payload.json", body);

            await Case($"try{tried}-edit-clone", SceneBase + "Edit", body);

            var found = await WaitNewSceneAsync(hid, uid, before, testName);
            if (found != null) { created = found; break; }
            W("");
        }

        if (created == null)
        {
            W("!! 所有模板都没能新建成功。检查上面每次的 code / msg 定位是哪个字段被拒。");
            W("   现有对象未被修改（下面的集合比对可证）。");
        }
        else
        {
            W($"✓ 新建成功：scene_id={created.Value.id}  name={created.Value.name}");
            W("   说明 AppSceneService/Edit 接受整条对象回传，编辑功能可以按「取回→改→提交」实现。");
            W("");
            W("===== 删除测试对象 =====");
            await TryDeleteAsync(hid, uid, created.Value.id);
            var final = await SceneMapAsync(hid, uid);
            if (final.ContainsKey(created.Value.id))
            {
                W("!! 删除失败，测试自动化仍在，请到米家 App 里手动删除「" + testName + "」（它是关闭状态，不会触发）");
            }
            else
            {
                W($"✓ 已删除，场景数回到 {final.Count}");
            }
        }

        W("");
        W("===== 复核：原有场景未受影响 =====");
        var end = await SceneMapAsync(hid, uid);
        var vanished = before.Keys.Where(k => !end.ContainsKey(k)).ToList();
        W($"原有场景数量 {before.Count}，现存 {end.Count}");
        W(vanished.Count == 0 ? "原有 scene_id 全部仍在 ✓" : "!! 有场景消失：" + string.Join(",", vanished.Take(5)));

        Finish();
    }

    /// <summary>轮询等待新场景出现（云端有延迟），返回新出现的 (id, name)。</summary>
    private static async Task<(string id, string name)?> WaitNewSceneAsync(
        string homeId, long uid, Dictionary<string, string> before, string expectName)
    {
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(1200);
            var now = await SceneMapAsync(homeId, uid);
            var added = now.Where(p => !before.ContainsKey(p.Key)).ToList();
            if (added.Count > 0)
            {
                var hit = added.FirstOrDefault(p => p.Value.Contains("ZZ-TEST"));
                var pick = hit.Key != null ? hit : added[0];
                W($"     场景数 {before.Count} → {now.Count}，新增：{pick.Value}");
                return (pick.Key, pick.Value);
            }
        }
        W($"     场景数仍为 {before.Count}，没有新增");
        return null;
    }

    private static async Task<bool> TryDeleteAsync(string homeId, long uid, string sceneId)
    {
        var attempts = new (string name, object payload)[]
        {
            ("d1-delete-scene-id", new { app_version = 12, scene_id = sceneId, home_id = homeId, owner_uid = uid }),
            ("d2-delete-us-id", new { app_version = 12, us_id = sceneId, home_id = homeId, owner_uid = uid }),
            ("d3-delete-usid-array", new { app_version = 12, us_id = new[] { sceneId }, home_id = homeId, owner_uid = uid }),
        };

        foreach (var (name, payload) in attempts)
        {
            await Case(name, SceneBase + "Delete", payload);
            var map = await SceneMapAsync(homeId, uid);
            if (!map.ContainsKey(sceneId)) return true;
        }
        return false;
    }

    /// <summary>取出场景集合：scene_id → name。</summary>
    private static async Task<Dictionary<string, string>> SceneMapAsync(string homeId, long uid)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var raw = await _client!.RequestRawAsync(SceneBase + "GetSceneList",
                new { app_version = 12, get_type = 2, home_id = homeId, owner_uid = uid });
            var result = raw.TryGetProperty("result", out var r) ? r : raw;
            if (!result.TryGetProperty("scene_info_list", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return map;
            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                var id = it.TryGetProperty("scene_id", out var sid) ? sid.ToString() : "";
                var name = it.TryGetProperty("name", out var n) ? n.ToString() : "";
                if (id.Length > 0) map[id] = name;
            }
        }
        catch (Exception ex)
        {
            W($"     （取场景列表失败：{ex.Message}）");
        }
        return map;
    }

    private static void CopyIf(JsonObject from, JsonObject to, string key)
    {
        if (from.TryGetPropertyValue(key, out var v) && v != null) to[key] = v.DeepClone();
    }

    /// <summary>从 setting 里收集涉及的 did，作为 authed 设备列表。</summary>
    private static JsonArray CollectDids(JsonObject setting)
    {
        var set = new List<string>();
        void Add(string? did)
        {
            if (!string.IsNullOrWhiteSpace(did) && did != "null" && !set.Contains(did)) set.Add(did);
        }

        if (setting["scene_action"] is JsonObject act && act["actions"] is JsonArray actions)
            foreach (var a in actions.OfType<JsonObject>())
                if (a["payload_json"] is JsonObject p) Add(p["did"]?.ToString());

        if (setting["scene_trigger"] is JsonObject trg && trg["triggers"] is JsonArray triggers)
            foreach (var t in triggers.OfType<JsonObject>()) Add(t["did"]?.ToString());

        var arr = new JsonArray();
        foreach (var d in set) arr.Add(d);
        return arr;
    }

    private static string NowUnix() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

    private static void Dump(string file, JsonNode node)
    {
        try { File.WriteAllText(Path.Combine(_dir, file), node.ToJsonString(Pretty), Encoding.UTF8); } catch { }
    }

    /// <summary>
    /// 挑可克隆的模板：必须带 scene_trigger。
    /// 优先没有蓝牙子设备（did 以 blt. 开头）的——那种设备云端校验 home_id 会报
    /// 「scene device home_id invalid」，与报文形状无关。
    /// </summary>
    private static async Task<List<JsonObject>> LoadCloneCandidatesAsync(string homeId, long uid)
    {
        var list = new List<(int score, JsonObject obj)>();
        try
        {
            var raw = await _client!.RequestRawAsync(SceneBase + "GetSceneList",
                new { app_version = 12, get_type = 2, home_id = homeId, owner_uid = uid });
            if (raw.ValueKind != JsonValueKind.Object) return new List<JsonObject>();
            var result = raw.TryGetProperty("result", out var r) ? r : raw;
            if (!result.TryGetProperty("scene_info_list", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<JsonObject>();

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("scene_trigger", out var t) || t.ValueKind != JsonValueKind.Object) continue;
                var obj = JsonNode.Parse(item.GetRawText())?.AsObject();
                if (obj == null) continue;

                var dids = new List<string>();
                if (t.TryGetProperty("triggers", out var trs) && trs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tr in trs.EnumerateArray())
                    {
                        if (tr.ValueKind != JsonValueKind.Object) continue;
                        var d = tr.TryGetProperty("did", out var dd) ? dd.ToString()
                            : (tr.TryGetProperty("extra_json", out var ej) && ej.ValueKind == JsonValueKind.Object
                                && ej.TryGetProperty("did", out var ejd) ? ejd.ToString() : "");
                        if (!string.IsNullOrWhiteSpace(d)) dids.Add(d);
                    }
                }

                var score = 2;
                if (dids.Count == 0) score = 1;                              // 定时/手动这类无设备触发
                else if (dids.All(d => !d.StartsWith("blt.", StringComparison.OrdinalIgnoreCase))) score = 0;

                list.Add((score, obj));
            }
        }
        catch (Exception ex)
        {
            W($"     （取模板失败：{ex.Message}）");
        }

        foreach (var (score, obj) in list.OrderBy(x => x.score).Take(6))
            W($"     候选(score={score})：{obj["name"]}");
        W("");

        return list.OrderBy(x => x.score).Select(x => x.obj).ToList();
    }

    // ===================================================================
    // 顺序探针：验证「重排 actions + 重写 order」云端是否照做。
    // 做法：克隆一条多动作自动化 → 颠倒动作顺序 → 新建 → 读回比对 → 删除。
    // 不修改任何现有对象。模板按已验证可克隆的候选逐个试（有的自动化里含解绑设备会被拒）。
    // ===================================================================
    public static async Task RunOrderAsync()
    {
        _dir = Path.Combine(AppPaths.Dir, "scene-probe-order");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir)) { try { File.Delete(f); } catch { } }

        W($"MiHome 动作顺序探针  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W("做法：克隆一条多动作自动化 → 颠倒顺序 → 新建 → 读回 → 删除。不修改任何现有对象。");
        W("");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { W("!! 没有登录态"); Finish(); return; }
        if (!auth.HasValidAuth())
        {
            var ok = await auth.RefreshServiceTokenAsync();
            W($".. serviceToken 续期{(ok ? "成功" : "失败：" + (auth.LastRefreshError ?? "未知"))}");
            if (!ok) { Finish(); return; }
        }
        _client = new MijiaClient(auth);

        var home = await FirstHomeAsync();
        if (home == null) { W("!! 没有家庭"); Finish(); return; }
        var hid = home.Id;
        var uid = home.Uid;

        var before = await SceneMapAsync(hid, uid);
        var candidates = await LoadCloneCandidatesAsync(hid, uid);
        if (candidates.Count == 0) { W("!! 没有可用模板"); Finish(); return; }

        var stamp = DateTime.Now.ToString("HHmmss");
        var testName = $"ZZ-TEST-ORDER-{stamp}-可删";

        (string id, string name)? created = null;
        JsonArray? submitted = null;
        var tried = 0;

        foreach (var tpl in candidates)
        {
            var src = (tpl["scene_action"] as JsonObject)?["actions"] as JsonArray;
            if (src == null || src.Count < 3) continue;
            if (++tried > 4) break;

            var tplName = tpl["name"]?.ToString() ?? "";
            W($"===== 第 {tried} 次：模板「{tplName}」 共 {src.Count} 个动作 =====");
            W("  原顺序：");
            for (var i = 0; i < src.Count; i++)
                W($"    [{i}] order={src[i]?["order"]} gid={src[i]?["group_id"]} name={src[i]?["name"]}");

            var body = (JsonObject)tpl.DeepClone();
            body["scene_id"] = "0";
            body["name"] = testName;
            body["enable"] = false;
            body["enable_push"] = false;
            body["common_use"] = false;
            body["create_time"] = NowUnix();
            body["update_time"] = NowUnix();
            body["app_version"] = 12;
            body["home_id"] = hid;
            body["owner_uid"] = uid;

            var arr = (body["scene_action"] as JsonObject)?["actions"] as JsonArray;
            if (arr == null) { W("  !! 缺 scene_action.actions"); continue; }

            // 颠倒顺序，并像界面拖拽后那样重写 order 与 group_id
            var reversed = arr.Select(x => x!.AsObject()).ToList();
            reversed.Reverse();
            arr.Clear();

            var seq = 0;
            var prevOld = -1;
            var curNew = 0;
            for (var i = 0; i < reversed.Count; i++)
            {
                var a = reversed[i];
                a["order"] = i + 1;
                var old = SceneEditActionRow.IntOf(a["group_id"]);
                if (old <= 0) { a["group_id"] = 0; prevOld = -1; }
                else
                {
                    if (old != prevOld) curNew = ++seq;
                    prevOld = old;
                    a["group_id"] = curNew;
                }
                arr.Add(a);
            }

            W("  提交顺序（颠倒后）：");
            for (var i = 0; i < arr.Count; i++)
                W($"    [{i}] order={arr[i]?["order"]} gid={arr[i]?["group_id"]} name={arr[i]?["name"]}");

            Dump($"try{tried}-order-payload.json", body);
            await Case($"try{tried}-order-edit", SceneBase + "Edit", body);

            created = await WaitNewSceneAsync(hid, uid, before, testName);
            if (created != null) { submitted = arr; break; }
            W("  （这个模板被拒，换下一个）");
            W("");
        }

        if (created == null || submitted == null)
        {
            W("!! 所有模板都没能新建成功，无法验证顺序。");
            W("   现有对象未被修改。");
            Finish();
            return;
        }

        await Task.Delay(1500);
        var readback = await LoadSceneByIdAsync(hid, uid, created.Value.id);
        if (readback == null)
        {
            W("!! 读回失败");
        }
        else
        {
            var got = (readback["scene_action"] as JsonObject)?["actions"] as JsonArray;
            W("");
            W("===== 读回的顺序 =====");
            if (got != null)
                for (var i = 0; i < got.Count; i++)
                    W($"  [{i}] order={got[i]?["order"]} gid={got[i]?["group_id"]} name={got[i]?["name"]} dname={got[i]?["payload_json"]?["device_name"]}");

            var okOrder = got != null && got.Count == submitted.Count;
            var okPayload = okOrder;
            if (okOrder)
            {
                for (var i = 0; i < submitted.Count; i++)
                {
                    if (SceneEditActionRow.IntOf(submitted[i]?["order"]) != SceneEditActionRow.IntOf(got![i]?["order"]))
                    {
                        okOrder = false;
                        break;
                    }
                    var want = submitted[i]?["payload_json"]?["device_name"]?.ToString() ?? "";
                    var have = got![i]?["payload_json"]?["device_name"]?.ToString() ?? "";
                    if (want.Length > 0 && want != have) okPayload = false;
                }
            }

            W("");
            W(okOrder && okPayload
                ? "✓ 云端按提交的 order 保存，拖拽排序会同步到手机 App"
                : !okOrder
                    ? "!! 读回的 order 与提交不一致，顺序可能不生效"
                    : "!! order 对了但设备顺序变了，需要再核对");
        }

        W("");
        W("===== 清理 =====");
        await TryDeleteAsync(hid, uid, created.Value.id);
        var end = await SceneMapAsync(hid, uid);
        W(end.ContainsKey(created.Value.id)
            ? $"!! 删除失败，请手动删「{testName}」（已关闭，不会触发）"
            : "✓ 测试对象已删除");
        W($"原有场景 {before.Count}，现存 {end.Count}");

        Finish();
    }

    private static async Task<JsonObject?> LoadSceneByIdAsync(string homeId, long uid, string sceneId)
    {
        try
        {
            var raw = await _client!.RequestRawAsync(SceneBase + "GetSceneList",
                new { app_version = 12, get_type = 2, home_id = homeId, owner_uid = uid });
            if (raw.ValueKind != JsonValueKind.Object) return null;
            var result = raw.TryGetProperty("result", out var r) ? r : raw;
            if (!result.TryGetProperty("scene_info_list", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("scene_id", out var sid)) continue;
                if (sid.ToString() == sceneId) return JsonNode.Parse(item.GetRawText())?.AsObject();
            }
            return null;
        }
        catch { return null; }
    }

    private static void W(string s)
    {
        Log.AppendLine(s);
        Flush();
    }

    private static void Flush()
    {
        try { File.WriteAllText(Path.Combine(_dir, "summary.txt"), Log.ToString(), Encoding.UTF8); } catch { }
    }

    private static void Finish()
    {
        W("");
        W($"结束 {DateTime.Now:HH:mm:ss}");
        Flush();
        try { File.WriteAllText(Path.Combine(_dir, "done.flag"), DateTime.Now.ToString("O")); } catch { }
    }
}
