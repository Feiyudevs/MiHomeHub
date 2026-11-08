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
/// 设备「触发条件能力」接口探针（只读）。
///
/// 背景：自动化里每条触发源都带 sc_id（如门锁「任意门外开锁（除钥匙）」= 39246），
/// 这是小米 IoT 平台上厂商为该型号配置的自动化能力 ID，本地无法推导。
/// 本探针把候选接口逐个打一遍，确认是否存在「按设备取自动化能力列表」的云端接口。
///
/// 用法：MiHome.exe --trigger-probe [did]
/// 输出：%APPDATA%\Feiyu\MiHome\trigger-probe\
///   summary.txt / NN-xxx.json / done.flag
/// </summary>
internal static class TriggerProbe
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
    private static readonly List<string> Hits = new();

    /// <summary>
    /// 触发条件可写性实验：克隆一条现有自动化（enable=false），把触发源换成
    /// 「按 MIoT 规格合成」的条件，提交后读回看云端接不接受、sc_id 会不会被改写，随后删除。
    /// </summary>
    public static async Task RunTriggerTestAsync(string? wantDid)
    {
        _dir = Path.Combine(AppPaths.Dir, "trigger-test");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir)) { try { File.Delete(f); } catch { } }

        W($"MiHome 触发条件可写性实验  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W("原则：只新建 enable=false 的测试自动化，成功后立即删除；不改任何现有对象。");
        W("");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { W("!! 没有登录态"); Finish(); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync())
        {
            W($"!! 续期失败：{auth.LastRefreshError ?? "未知"}"); Finish(); return;
        }
        _client = new MijiaClient(auth);

        var homes = await _client.GetHomesAsync(forceRefresh: true);
        var home = homes.FirstOrDefault();
        if (home == null) { W("!! 没有家庭"); Finish(); return; }

        var devices = await _client.GetAllDevicesAsync();
        var dev = (!string.IsNullOrEmpty(wantDid) ? devices.FirstOrDefault(d => d.Did == wantDid) : null)
                  ?? devices.FirstOrDefault(d => d.SpecType.Contains(":lock:"))
                  ?? devices.FirstOrDefault();
        if (dev == null) { W("!! 没有设备"); Finish(); return; }
        W($"门锁：{dev.Name} did={dev.Did} model={dev.Model}");

        var scenes = await _client.GetSceneBundleAsync();
        var tpl = scenes.FirstOrDefault(s => s.Raw != null
                     && s.Raw["scene_trigger"]?["triggers"] is JsonArray a && a.Count > 0
                     && s.IsAutomation);
        if (tpl == null) { W("!! 没找到带触发条件的自动化模板"); Finish(); return; }
        W($"模板：{tpl.Name} scene_id={tpl.SceneId}");
        W("");

        var stamp = DateTime.Now.ToString("HHmmss");
        var cases = new (string Tag, string Key, int Vt, JsonNode? ValueJson, int ScId, string Name)[]
        {
            ("t1-属性-异常条件", "prop.2.6", 1, JsonValue.Create(2), 0, "密码频繁开锁失败"),
            ("t2-事件-门铃", "event.5.1006", 5, JsonValue.Create(""), 0, "门铃响"),
            ("t3-事件-开锁带筛选", "event.2.1020", 6, SubProps(dev.Model, (2, 1, 1), (2, 3, 2)), 0, "室内开锁"),
            ("t4-事件-开锁-原scid", "event.2.1020", 6, SubProps(dev.Model, (2, 1, 2), (2, 3, 2)), 39246, "任意门外开锁（除钥匙）"),
            ("t5-同scid-换筛选", "event.2.1020", 6, SubProps(dev.Model, (2, 1, 1), (2, 3, 2)), 39246, "室内开锁"),
            ("t6-同scid-换key", "prop.2.6", 1, JsonValue.Create(2), 39246, "密码频繁开锁失败"),
            ("t7-任意scid", "prop.2.6", 1, JsonValue.Create(2), 987654321, "密码频繁开锁失败"),
        };

        var idx = 0;
        foreach (var c in cases)
        {
            idx++;
            var name = $"ZZ-TEST-MiHomeWin-{stamp}-{idx}-可删";
            W($"===== [{idx}] {c.Tag}：{c.Key} sc_id={c.ScId} =====");
            try
            {
                var newId = await _client.SaveSceneAsync(tpl, body =>
                {
                    body["scene_id"] = "0";
                    body["name"] = name;
                    body["enable"] = false;
                    body["enable_push"] = false;
                    body["common_use"] = false;
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                    body["create_time"] = now;
                    body["update_time"] = now;
                    body["home_id"] = home.Id;
                    body["owner_uid"] = home.Uid;
                    body["scene_trigger"] = new JsonObject
                    {
                        ["triggers"] = new JsonArray(BuildTrigger(dev, c.Key, c.Vt, c.ValueJson, c.ScId, c.Name)),
                        ["trigger_logic"] = "or"
                    };
                });
                W($"     提交成功 scene_id={newId}");

                var read = (await _client.GetSceneBundleAsync())
                    .FirstOrDefault(s => s.Name == name);
                if (read?.Raw == null) { W("     !! 读回没找到新对象"); }
                else
                {
                    var got = read.Raw["scene_trigger"]?["triggers"] is JsonArray g && g.Count > 0 ? g[0] : null;
                    var dump = got?.ToJsonString() ?? "(空)";
                    W($"     读回 triggers: {dump}");
                    try { File.WriteAllText(Path.Combine(_dir, $"{idx:00}-readback-{c.Tag}.json"), dump, Encoding.UTF8); } catch { }

                    var okKey = got?["key"]?.ToString() == c.Key;
                    var okSc = got?["sc_id"]?.ToString() == c.ScId.ToString();
                    var okVal = (got?["value_json"]?.ToJsonString() ?? "") == (c.ValueJson?.ToJsonString() ?? "");
                    W($"     校验 key={okKey} sc_id一致={okSc} value_json一致={okVal}");
                }

                if (read != null) { await _client.DeleteSceneAsync(read); W("     已删除测试对象"); }
                else
                {
                    var all = await _client.GetSceneBundleAsync();
                    var target = all.FirstOrDefault(s => s.Name == name);
                    if (target != null) { await _client.DeleteSceneAsync(target); W("     已删除（二次查找）"); }
                    else W("     !! 找不到测试对象，请在米家 App 里手动删除「" + name + "」（关闭状态不会触发）");
                }
            }
            catch (Exception ex)
            {
                W($"     失败：{ex.Message}");
            }
            W("");
        }

        Finish();
    }

    /// <summary>
    /// 端到端验证：用正式代码里的 SceneTriggerCatalog 生成触发源（事件 + 属性各一条），
    /// 克隆一条禁用自动化提交、读回比对、删除。确认 UI 点「保存」不会被打回。
    /// </summary>
    public static async Task RunCatalogTestAsync(string? wantDid)
    {
        _dir = Path.Combine(AppPaths.Dir, "trigger-e2e");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir)) { try { File.Delete(f); } catch { } }

        W($"MiHome 触发条件端到端验证  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W("");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { W("!! 没有登录态"); Finish(); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync())
        {
            W($"!! 续期失败：{auth.LastRefreshError ?? "未知"}"); Finish(); return;
        }
        _client = new MijiaClient(auth);

        var home = (await _client.GetHomesAsync(forceRefresh: true)).FirstOrDefault();
        if (home == null) { W("!! 没有家庭"); Finish(); return; }

        var devices = await _client.GetAllDevicesAsync();
        var dev = (!string.IsNullOrEmpty(wantDid) ? devices.FirstOrDefault(d => d.Did == wantDid) : null)
                  ?? devices.FirstOrDefault(d => d.SpecType.Contains(":lock:"));
        if (dev == null) { W("!! 没有设备"); Finish(); return; }
        W($"设备：{dev.Name} {dev.Model}");

        var options = await SceneTriggerCatalog.BuildOptionsAsync(_client, dev);
        W($"可用条件 {options.Count} 个：{string.Join("、", options.Take(6).Select(o => $"{o.Title}[{o.Key}]"))} …");

        var triggers = new JsonArray();

        // 事件 + 筛选：锁事件（操作位置=门外、锁动作=开锁）
        var evt = options.FirstOrDefault(o => o.Axis == TriggerAxis.Event && o.Args.Count >= 2);
        if (evt != null)
        {
            var groups = new List<TriggerArgGroup>();
            foreach (var a in evt.Args)
            {
                // 出参里挑一个有枚举的，默认取「最后一项」（门锁：操作位置 2=门外、锁动作 2=开锁）
                groups.Add(TriggerArgGroup.Create(a, a.Options.Count > 0 ? a.Options[^1].Value : null));
            }
            triggers.Add(SceneTriggerCatalog.BuildNode(dev, evt, groups, triggers.Count + 1));
        }

        // 属性：异常条件 = 第 2 项（密码频繁失败）
        var prop = options.FirstOrDefault(o => o.Axis == TriggerAxis.Property && o.Prop is { Options.Count: > 1 });
        if (prop != null)
        {
            var spec = new TriggerArgSpec
            {
                Piid = prop.Prop!.Piid,
                Name = prop.Prop.Desc,
                Options = prop.Prop.Options
            };
            var g = TriggerArgGroup.Create(spec, prop.Prop.Options[1].Value);
            triggers.Add(SceneTriggerCatalog.BuildNode(dev, prop, new[] { g }, triggers.Count + 1));
        }

        if (triggers.Count == 0) { W("!! 没生成任何触发源"); Finish(); return; }
        Dump("built-triggers.json", triggers);

        var scenes = await _client.GetSceneBundleAsync();
        var tpl = scenes.FirstOrDefault(s => s.Raw != null && s.IsAutomation
                     && s.Raw["scene_trigger"]?["triggers"] is JsonArray a && a.Count > 0);
        if (tpl == null) { W("!! 没有可克隆的自动化模板"); Finish(); return; }

        var name = $"ZZ-TEST-MiHomeWin-{DateTime.Now:HHmmss}-e2e-可删";
        try
        {
            var newId = await _client.SaveSceneAsync(tpl, body =>
            {
                body["scene_id"] = "0";
                body["name"] = name;
                body["enable"] = false;
                body["enable_push"] = false;
                body["common_use"] = false;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                body["create_time"] = now;
                body["update_time"] = now;
                body["home_id"] = home.Id;
                body["owner_uid"] = home.Uid;
                body["scene_trigger"] = new JsonObject
                {
                    ["express"] = 1,
                    ["triggers"] = triggers.DeepClone()
                };
            });
            W($"✓ 提交成功 scene_id={newId}");

            var read = (await _client.GetSceneBundleAsync()).FirstOrDefault(s => s.Name == name);
            if (read?.Raw != null)
            {
                var got = read.Raw["scene_trigger"]?["triggers"];
                Dump("readback-triggers.json", got);
                W($"读回 {got?.AsArray().Count ?? 0} 条");
                if (got is JsonArray ga)
                    foreach (var t in ga.OfType<JsonObject>())
                        W($"   key={t["key"]} name={t["name"]} value_type={t["value_type"]} value_json={t["value_json"]?.ToJsonString()}");
            }
            else W("!! 读回没找到新对象");

            if (read != null) { await _client.DeleteSceneAsync(read); W("已删除测试对象"); }
        }
        catch (Exception ex)
        {
            W($"失败：{ex.Message}");
        }

        Finish();
    }

    private static void Dump(string file, JsonNode? node)
    {
        try
        {
            File.WriteAllText(Path.Combine(_dir, file),
                node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>合成一条触发源（字段照抄线上真实对象）。</summary>
    private static JsonObject BuildTrigger(MijiaDevice dev, string key, int valueType, JsonNode? valueJson, int scId, string name)
    {
        return new JsonObject
        {
            ["id"] = 0,
            ["order"] = 1,
            ["src"] = "device",
            ["key"] = key,
            ["extra"] = "",
            ["name"] = name,
            ["value"] = "",
            ["value_type"] = valueType,
            ["extra_json"] = new JsonObject
            {
                ["device_name"] = dev.Name,
                ["did"] = dev.Did,
                ["model"] = dev.Model
            },
            ["value_json"] = valueJson,
            ["protocol_type"] = 2,
            ["sc_id"] = scId,
            ["from"] = 1,
            ["value_operation"] = 0,
            ["std_sc_id"] = "",
            ["duration"] = 0,
            ["option_ids"] = new JsonArray(),
            ["data_ver"] = 0
        };
    }

    /// <summary>事件筛选值：{sub_props:{attr:[{key:prop.&lt;model&gt;.&lt;siid&gt;.&lt;piid&gt;,value,value_type:1}],express:0}}</summary>
    private static JsonObject SubProps(string model, params (int Siid, int Piid, int Value)[] attrs)
    {
        var arr = new JsonArray();
        foreach (var a in attrs)
            arr.Add(new JsonObject
            {
                ["key"] = $"prop.{model}.{a.Siid}.{a.Piid}",
                ["value"] = a.Value,
                ["value_type"] = 1
            });
        return new JsonObject
        {
            ["sub_props"] = new JsonObject { ["attr"] = arr, ["express"] = 0 }
        };
    }

    public static async Task RunAsync(string? wantDid)
    {
        _dir = Path.Combine(AppPaths.Dir, "trigger-probe");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir))
        {
            try { File.Delete(f); } catch { }
        }

        W($"MiHome 设备触发条件探针  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"输出目录：{_dir}");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken))
        {
            W("!! 没有登录态：请先在主程序里扫码登录后重跑");
            Finish();
            return;
        }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync())
        {
            W($"!! serviceToken 续期失败：{auth.LastRefreshError ?? "未知"}");
            Finish();
            return;
        }

        _client = new MijiaClient(auth);

        var homes = await _client.GetHomesAsync(forceRefresh: true);
        var home = homes.FirstOrDefault();
        if (home == null)
        {
            W("!! 没有家庭");
            Finish();
            return;
        }

        var devices = await _client.GetAllDevicesAsync();
        MijiaDevice? dev = null;
        if (!string.IsNullOrEmpty(wantDid))
            dev = devices.FirstOrDefault(d => d.Did == wantDid);
        dev ??= devices.FirstOrDefault(d => d.SpecType.Contains(":lock:"))
               ?? devices.FirstOrDefault();
        if (dev == null)
        {
            W("!! 没有设备");
            Finish();
            return;
        }

        W($"家庭：{home.Name} id={home.Id} uid={home.Uid}");
        W($"设备：{dev.Name} did={dev.Did} model={dev.Model}");
        W($"spec_type：{dev.SpecType}");
        W("");

        var did = dev.Did;
        var model = dev.Model;
        var specType = dev.SpecType;

        var full = new
        {
            did,
            model,
            spec_type = specType,
            home_id = home.Id,
            owner_uid = home.Uid,
            app_version = 12,
            language = "zh_CN"
        };
        var slim = new { did, model, spec_type = specType, home_id = home.Id, owner_uid = home.Uid };

        // ===== 0. 云端规格（不依赖猜测的接口名；先确认它里面有没有自动化能力信息） =====
        var spec = await Case("00-miotspec-getspec", "/miotspec/device/getspec",
            new { did, force = false, language = "zh_CN" });
        ScanForAutomation(spec, "getspec");

        // ===== 1. 猜测：设备自动化能力列表（厂商在 IoT 平台配置的那张表） =====
        string[] methods =
        {
            "GetDeviceAutomationList",
            "GetDeviceAutomation",
            "GetDeviceAutomationConfig",
            "GetAutomationConfig",
            "GetDeviceAbility",
            "GetDeviceAbilityList",
            "GetDeviceCapability",
            "GetDeviceCapabilityList",
            "GetSceneCapability",
            "GetSceneAbility",
            "GetTriggerList",
            "GetDeviceTriggerList",
            "GetSceneTriggerList",
            "GetTriggerAndActionList",
            "GetSceneConfig",
            "GetSmartSceneList",
            "GetDeviceSceneList",
        };

        foreach (var m in methods)
            await TryCombo("scene/" + m, SceneBase + m, full, slim);

        foreach (var m in new[] { "GetDeviceAutomationList", "GetDeviceTriggerList", "GetTriggerList", "GetAutomationList" })
            await TryCombo("auto/" + m, AutoBase + m, full, slim);

        foreach (var m in new[] { "GetDeviceAutomationList", "GetTriggerList" })
            await TryCombo("auto2/" + m, AutoBase2 + m, full, slim);

        // ===== 2. 猜测：其他网关下按设备取能力 =====
        await TryCombo("gw/home-getdevicesmart", "/appgateway/miot/home/GetDeviceSmartList", full, slim);
        await TryCombo("gw/device-automation", "/appgateway/miot/device/GetAutomationList", full, slim);

        W("");
        W("===== 结论 =====");
        if (Hits.Count == 0)
            W("所有候选接口都失败了：需要抓包确认米家 App「添加触发条件」实际调用的接口。");
        else
            foreach (var h in Hits)
                W("命中 " + h);

        Finish();
    }

    /// <summary>同一方法名分别用「全字段」和「精简字段」各打一次，任一成功即记为命中。</summary>
    private static async Task TryCombo(string tag, string uri, object fullPayload, object slimPayload)
    {
        var r1 = await Case("a-" + tag, uri, fullPayload);
        if (IsOk(r1)) { Hits.Add($"{uri} (full)"); return; }
        var r2 = await Case("b-" + tag, uri, slimPayload);
        if (IsOk(r2)) Hits.Add($"{uri} (slim)");
    }

    private static bool IsOk(JsonElement? el)
        => el is { ValueKind: JsonValueKind.Object } e
           && e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() == 0;

    /// <summary>在任意响应里找自动化能力特征：sc_id / sa_id / trigger / automation 字段。</summary>
    private static void ScanForAutomation(JsonElement? el, string label)
    {
        if (el is not { } e) return;
        var text = e.GetRawText();
        var flags = new List<string>();
        foreach (var k in new[] { "sc_id", "sa_id", "trigger", "automation", "scene_automation" })
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) flags.Add(k);
        W($"     形态扫描 {label}：{(flags.Count == 0 ? "无自动化特征" : string.Join(",", flags))}  长度={text.Length}");
    }

    private static async Task<JsonElement?> Case(string name, string uri, object payload)
    {
        _idx++;
        var file = Path.Combine(_dir, $"{_idx:00}-{name}.json");
        W($"[{_idx:00}] {uri}");
        try
        {
            var ret = await _client!.RequestRawAsync(uri, payload);
            var raw = JsonSerializer.Serialize(ret, Pretty);
            try { File.WriteAllText(file, raw, Encoding.UTF8); } catch { }

            var code = ret.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32().ToString() : "-";
            var msg = ret.TryGetProperty("message", out var m) ? m.ToString() : "";
            W($"     code={code}  msg={msg}   bytes={raw.Length}");
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
