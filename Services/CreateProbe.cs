using System;
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
/// 「新建场景 / 新建自动化」创建链路探针。
/// 完全复刻 MainViewModel.SaveSceneEditorAsync 的行为：
/// SceneDraftFactory.CreateBlank → 塞一条设备动作（自动化再加一条定时触发）→ SaveSceneAsync。
/// 目的：云端到底回了什么错（还是根本成功，说明问题在 UI 层）。
///
/// 用法：MiHome.exe --create-probe
/// 输出：%APPDATA%\Feiyu\MiHome\create-probe\summary.txt（成功创建的测试对象会立即删除）
/// </summary>
internal static class CreateProbe
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly StringBuilder Sb = new();
    private static string _dir = "";

    private static void W(string s)
    {
        Console.WriteLine(s);
        Sb.AppendLine(s);
    }

    public static async Task RunAsync()
    {
        _dir = Path.Combine(AppPaths.Dir, "create-probe");
        Directory.CreateDirectory(_dir);
        Sb.Clear();

        W($"MiHome 创建链路探针  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W("原则：只新建 enable=false 的测试对象，成功后立即删除；不改任何现有对象。");
        W("");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { W("!! 没有登录态"); await FinishAsync(); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync())
        { W($"!! 续期失败：{auth.LastRefreshError ?? "未知"}"); await FinishAsync(); return; }
        var client = new MijiaClient(auth);

        var homes = await client.GetHomesAsync(forceRefresh: true);
        var home = homes.FirstOrDefault();
        if (home == null) { W("!! 没有家庭"); await FinishAsync(); return; }
        W($"家庭：{home.Name} id={home.Id}");

        var devices = await client.GetAllDevicesAsync();
        var dev = devices.FirstOrDefault(d => !string.IsNullOrEmpty(d.Did));
        if (dev == null) { W("!! 没有设备"); await FinishAsync(); return; }
        W($"测试设备：{dev.Name} did={dev.Did} model={dev.Model}");
        W("");

        var stamp = DateTime.Now.ToString("HHmmss");

        // ===== 修复后全链路：工厂默认值直接提交 =====
        await CaseAsync(client, home, stamp, 1, "场景-工厂默认", automation: false, dev);
        await CaseAsync(client, home, stamp, 2, "自动化-工厂默认", automation: true, dev);
        await CaseAsync(client, home, stamp, 3, "编辑已有-追加动作", automation: false, dev,
            editExisting: true);

        await FinishAsync();
    }

    private static async Task FinishAsync()
    {
        File.WriteAllText(Path.Combine(_dir, "summary.txt"), Sb.ToString());
    }

    private static async Task CaseAsync(
        MijiaClient client, MijiaHome home, string stamp, int idx, string tag,
        bool automation, MijiaDevice dev, bool editExisting = false)
    {
        var name = $"ZZ-TEST-{stamp}-{idx}-可删";
        W($"===== [{idx}] {tag}（{name}） =====");
        string? newId = null;
        MijiaScene? editTarget = null;
        JsonObject? editTargetBackup = null;
        try
        {
            MijiaScene scene;
            if (editExisting)
            {
                // 找一条可编辑的手动场景，追加动作后原样保存（不删，只改回）
                var bundle = await client.GetSceneBundleAsync();
                editTarget = bundle.FirstOrDefault(s => !s.IsAutomation && s.Raw != null)
                    ?? throw new Exception("没有可编辑的手动场景");
                editTargetBackup = (JsonObject)editTarget.Raw.DeepClone();
                scene = editTarget;
                W($"编辑目标：{scene.Name} scene_id={scene.SceneId}");
            }
            else
            {
                scene = SceneDraftFactory.CreateBlank(home, automation, name);
            }

            if (scene.Raw?["scene_action"]?["actions"] is not JsonArray acts)
            { W("!! 没有 scene_action.actions"); return; }

            var write = new JsonObject { ["did"] = dev.Did, ["siid"] = 2, ["piid"] = 1, ["value"] = true };
            var action = SceneDraftFactory.BuildDevicePropAction(dev,
                new SpecPropertyDef
                {
                    Siid = 2, Piid = 1, Name = "on", Desc = "开关",
                    ServiceName = "switch", ServiceDesc = "开关", Format = "bool", Writable = true
                }, write, acts.Count + 1);
            acts.Add(action);
            W($"动作已追加：from={action["from"]} sa_id={action["sa_id"]}");

            if (!editExisting && automation)
            {
                if (scene.Raw?["scene_trigger"]?["triggers"] is not JsonArray trs)
                { W("!! 骨架里没有 scene_trigger.triggers"); return; }
                trs.Add(SceneTriggerCatalog.BuildTimerNode(8, 0, SceneTriggerCatalog.DowWorkday, 1));
                W("定时触发节点已加入（value_json=0 0 8 * * 1,2,3,4,5 *）");
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            newId = await client.SaveSceneAsync(scene, body =>
            {
                if (editExisting) return;
                body["name"] = name;
                body["enable"] = false;
                body["scene_id"] = "0";
                body["enable_push"] = false;
                body["common_use"] = false;
                body["create_time"] = now;
                body["update_time"] = now;
            });
            W($"✓ 保存成功 scene_id={newId}");
        }
        catch (Exception ex)
        {
            W($"✗ 保存失败：{ex.GetType().Name}: {Short(ex.Message)}");
        }

        // 编辑用例：把追加的动作从原场景里移除（恢复原状）；新建用例：读回 + 删除
        if (editExisting && editTarget?.Raw != null && editTargetBackup != null)
        {
            try
            {
                editTarget.Raw = editTargetBackup;
                await client.SaveSceneAsync(editTarget);
                W("已还原被编辑的场景");
            }
            catch (Exception ex)
            {
                W($"!! 还原失败（原场景多了一条测试动作，可手动删除）：{ex.Message}");
            }
        }
        else if (newId != null)
        {
            try
            {
                var bundle = await client.GetSceneBundleAsync();
                var created = bundle.FirstOrDefault(s => s.SceneId == newId);
                if (created?.Raw != null)
                {
                    File.WriteAllText(Path.Combine(_dir, $"{idx:D2}-{tag}.json"),
                        created.Raw.ToJsonString(Pretty));
                    W("云端读回已落盘");
                    await client.DeleteSceneAsync(created);
                    W("已删除测试对象");
                }
                else
                {
                    W("!! 列表里没读到刚创建的对象，需手动确认");
                }
            }
            catch (Exception ex)
            {
                W($"清理阶段异常：{ex.Message}");
            }
        }
        W("");
    }

    /// <summary>手动触发节点，严格照抄真实报文（欢迎场景）。</summary>
    private static JsonObject BuildUserTrigger() => new()
    {
        ["id"] = 1,
        ["order"] = 1,
        ["src"] = "user",
        ["key"] = "user.click",
        ["extra"] = "",
        ["name"] = "",
        ["value"] = "",
        ["value_type"] = 5,
        ["extra_json"] = new JsonObject
        {
            ["voice_alias"] = new JsonObject { ["alias"] = new JsonArray(), ["use_title"] = 1 }
        },
        ["value_json"] = null,
        ["protocol_type"] = 2,
        ["sc_id"] = 1,
        ["from"] = 0,
        ["value_operation"] = 0,
        ["std_sc_id"] = "",
        ["duration"] = 0,
        ["option_ids"] = new JsonArray(),
        ["data_ver"] = 0
    };

    /// <summary>云端报错串很长，保留内层 err 起始后的完整片段。</summary>
    private static string Short(string msg)
    {
        var i = msg.IndexOf("\"err\":", StringComparison.Ordinal);
        return i > 0 ? msg[(i + 7)..].Replace("\\", "").Replace("\"", "'") : msg;
    }
}
