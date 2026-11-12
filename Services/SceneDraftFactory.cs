// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Text.Json.Nodes;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>
/// 新建场景 / 自动化用的空白报文骨架。
/// AppSceneService/Edit 要求整条对象原样回传（缺字段会被服务端当成结构异常），
/// 所以这里按 2026-11 探针实测到的完整字段生成，再把动作 / 触发条件 / 条件清空。
/// <c>scene_id = "0"</c> 是服务端约定的「新建」标记（探针实测可新建成功）。
/// </summary>
internal static class SceneDraftFactory
{
    /// <summary>造一条空白场景（<paramref name="automation"/> = true 时是「如果…就…」）。</summary>
    public static MijiaScene CreateBlank(MijiaHome home, bool automation, string name)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var uid = home.Uid.ToString();

        var raw = new JsonObject
        {
            ["scene_id"] = "0",
            ["uid"] = uid,
            ["home_id"] = home.Id,
            ["name"] = name,
            ["template_id"] = "0",
            // type 与「手动 / 自动化」无关（服务端这个字段语义不稳定），一律 0
            ["type"] = 0,
            ["local_dev"] = "",
            ["enable"] = true,
            ["enable_push"] = false,
            ["common_use"] = false,
            ["timespan"] = null,
            ["scene_trigger"] = automation
                // 自动化的触发条件由用户在面板里添加（保存前有校验）
                ? new JsonObject
                {
                    ["express"] = 1,
                    ["triggers"] = new JsonArray()
                }
                // 手动场景必须带「手动触发」节点，否则云端报
                // -101002000 "scene trigger not exist"（2026-11-08 --create-probe 实锤）
                : new JsonObject
                {
                    ["express"] = 1,
                    ["triggers"] = new JsonArray(BuildUserTrigger())
                },
            ["scene_condition"] = new JsonObject
            {
                ["express"] = 0,
                ["conditions"] = new JsonArray()
            },
            ["scene_action"] = new JsonObject
            {
                ["mode"] = 1,
                ["actions"] = new JsonArray()
            },
            ["value_format"] = 1,
            // 生效时间段：from == to 且全零 = 不限时间（与 MijiaScene.TimeWindowText 的判定一致）
            ["timewindow"] = new JsonObject
            {
                ["from"] = "0 0 0 * * * *",
                ["to"] = "0 0 0 * * * *",
                ["filter"] = "",
                ["extra"] = "",
                ["time_zone"] = TimeZoneId()
            },
            ["create_time"] = now,
            ["update_time"] = now,
            ["sub_usIds"] = new JsonArray(),
            ["no_record_log"] = false,
            ["parent_usId"] = "0",
            ["scene_id_v1"] = "0",
            ["room_id"] = "0",
            ["common_used_roomIds"] = new JsonArray(),
            ["tags"] = new JsonObject(),
            ["extra"] = null,
            ["icon_url"] = "",
            ["enable_consist"] = false,
            ["scene_version"] = 0,
            ["temporary"] = false,
            ["data_type"] = 0,
            ["hm_extra_info"] = null,
            ["scene_else_action"] = null
        };

        return new MijiaScene
        {
            SceneId = "0",
            Name = name,
            Enable = true,
            RoomId = "0",
            CreateTime = now,
            UpdateTime = now,
            HomeId = home.Id,
            IsAutomation = automation,
            Raw = raw
        };
    }

    /// <summary>
    /// 造一条设备写属性动作（set_properties）。
    /// <paramref name="write"/> 是 <c>{did, siid, piid, value}</c> 节点，由界面上的值控件就地改写。
    /// </summary>
    public static JsonObject BuildDevicePropAction(
        MijiaDevice device, SpecPropertyDef prop, JsonObject write, int order)
    {
        return new JsonObject
        {
            ["group_id"] = 0,
            ["id"] = order,
            ["order"] = order,
            ["type"] = 0,
            ["name"] = string.IsNullOrWhiteSpace(prop.Desc) ? prop.Name : prop.Desc,
            ["payload"] = "",
            ["payload_json"] = new JsonObject
            {
                ["command"] = "set_properties",
                ["delay_time"] = 0,
                ["device_name"] = device.Name,
                ["did"] = device.Did,
                ["model"] = device.Model,
                ["uid"] = device.Uid.ToString(),
                ["value"] = new JsonArray(write.DeepClone())
            },
            ["protocol_type"] = 2,
            // from=1 + 非零 sa_id 是云端唯一接受的组合（2026-11-08 --create-probe 实锤：
            // from=0/3 或 sa_id=0 都报 -101004004 "scene action sa_id or from invalid"）。
            // sa_id 是 int32 且不查重，给个随机大数避开真实报文里的 4–5 位小号。
            ["sa_id"] = NextSaId(),
            ["from"] = 1,
            ["device_group_id"] = 0,
            ["nested_scene_info"] = null,
            ["std_sa_id"] = "",
            ["option_ids"] = new JsonArray(),
            ["data_ver"] = 0
        };
    }

    /// <summary>
    /// 造一条「执行另一条场景」动作（type=2）。
    /// 结构按真实报文（scene-probe/04-scene-list.json 里「执行 “回家”」那条）：
    /// payload_json 只带 delay_time + scene_id，nested_scene_info 补场景名供展示。
    /// </summary>
    public static JsonObject BuildRunSceneAction(MijiaScene target, int order)
    {
        return new JsonObject
        {
            ["group_id"] = 0,
            ["id"] = order,
            ["order"] = order,
            ["type"] = 2,
            ["name"] = $"执行 “{target.Name}”",
            ["payload"] = "",
            ["payload_json"] = new JsonObject
            {
                ["delay_time"] = 0,
                ["scene_id"] = target.SceneId
            },
            ["protocol_type"] = 2,
            ["sa_id"] = 0,
            ["from"] = 0,
            ["device_group_id"] = 0,
            ["nested_scene_info"] = new JsonObject
            {
                ["scene_name"] = target.Name,
                ["icon"] = ""
            },
            ["std_sa_id"] = "",
            ["option_ids"] = new JsonArray(),
            ["data_ver"] = 0
        };
    }

    /// <summary>米家云端用的时区名：国内固定 Asia/Shanghai，其它地区退回本机时区 id。</summary>
    private static string TimeZoneId()
    {
        try
        {
            return TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.FromHours(8)
                ? "Asia/Shanghai"
                : TimeZoneInfo.Local.Id;
        }
        catch
        {
            return "Asia/Shanghai";
        }
    }

    private static readonly Random _rng = new();

    /// <summary>动作的 sa_id：云端只校验非零（int32），给随机大数避开真实报文里的小号。</summary>
    private static int NextSaId() => _rng.Next(65536, int.MaxValue);

    /// <summary>
    /// 手动触发节点（src=user）。结构照抄真实报文（scene-probe/04-scene-list.json「欢迎场景」）：
    /// sc_id=1、from=0、value_json=null、extra_json.voice_alias。
    /// </summary>
    public static JsonObject BuildUserTrigger() => new()
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
}
