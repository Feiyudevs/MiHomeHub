using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiHome.Models;

/// <summary>
/// 米家场景 / 自动化（同一个接口返回，靠所属列表区分）。
/// 字段按 GetSceneList / GetSimpleSceneList 的原始 JSON 结构映射（2026-11 探针实测）。
/// </summary>
public class MijiaScene
{
    [JsonPropertyName("scene_id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string SceneId { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>启用状态；自动化被关掉后米家端不再触发。</summary>
    [JsonPropertyName("enable")] public bool Enable { get; set; } = true;

    /// <summary>服务端的 type 字段语义不稳定（手动/自动化会混），不要用它判断类别。</summary>
    [JsonPropertyName("type")] public int RawType { get; set; }

    [JsonPropertyName("room_id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string RoomId { get; set; } = "0";

    [JsonPropertyName("create_time")] public string CreateTime { get; set; } = "";
    [JsonPropertyName("update_time")] public string UpdateTime { get; set; } = "";

    [JsonPropertyName("scene_action")] public SceneActionGroup? Action { get; set; }
    [JsonPropertyName("scene_trigger")] public SceneTriggerGroup? Trigger { get; set; }
    [JsonPropertyName("scene_condition")] public SceneConditionGroup? Condition { get; set; }
    [JsonPropertyName("timewindow")] public SceneTimeWindow? TimeWindow { get; set; }

    /// <summary>简化列表里的主触发源，完整列表没有它时用来兜底。</summary>
    [JsonPropertyName("first_trigger")] public SceneTrigger? FirstTrigger { get; set; }

    /// <summary>由客户端回填：接口在场景对象里不给 home_id（GetSceneList 给了，作兜底）。</summary>
    [JsonIgnore]
    public string HomeId
    {
        get => _homeId;
        set => _homeId = value;
    }

    /// <summary>GetSceneList 偶尔带回 home_id。</summary>
    [JsonPropertyName("home_id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string RawHomeId
    {
        get => _homeId;
        set { if (!string.IsNullOrEmpty(value) && value != "0") _homeId = value; }
    }

    private string _homeId = "";

    /// <summary>
    /// GetSceneList 返回的**原始对象**。编辑（AppSceneService/Edit）要求整条对象原样回传，
    /// 缺字段会被服务端当成结构异常，所以反序列化时同时留一份原始 JSON 供保存用。
    /// </summary>
    [JsonIgnore] public System.Text.Json.Nodes.JsonObject? Raw { get; set; }

    /// <summary>true = 自动化（有触发条件）；false = 手动场景（点了才执行）。由客户端按所属列表回填。</summary>
    [JsonIgnore] public bool IsAutomation { get; set; }

    // ===== 展示用派生属性（不参与反序列化） =====

    /// <summary>动作摘要，例如「客厅空调 关 · 小爱音箱 播报 拜拜」。</summary>
    [JsonIgnore] public string ActionSummary => BuildActionSummary();

    /// <summary>触发摘要，例如「PTX 无线开关 按键单击 · 每天 01:00」。</summary>
    [JsonIgnore] public string TriggerSummary => BuildTriggerSummary();

    /// <summary>动作条数（卡片角标用）。</summary>
    [JsonIgnore] public int ActionCount => Action?.Actions.Count ?? 0;

    /// <summary>是否被任意设备动作引用（用于「按设备」分组时的空判断）。</summary>
    [JsonIgnore] public IReadOnlyList<SceneActionItem> Actions => Action?.Actions ?? new List<SceneActionItem>();

    /// <summary>
    /// 这条场景里落到指定设备的动作。
    /// 先按 did 精确匹配；一条都没有时才退回父 did 匹配（场景里可能写 1135.s3，设备侧是 1135）。
    /// 不做双向模糊匹配：多路开关的父设备一旦命中所有通道，会把别的通道一起打开。
    /// </summary>
    public IReadOnlyList<SceneActionItem> ActionsForDevice(string? did)
    {
        if (string.IsNullOrWhiteSpace(did) || Actions.Count == 0)
            return Array.Empty<SceneActionItem>();

        var exact = Actions.Where(a => Targets(a, did, exactOnly: true)).ToList();
        return exact.Count > 0
            ? exact
            : Actions.Where(a => Targets(a, did, exactOnly: false)).ToList();
    }

    /// <summary>该设备在这条场景里是否有可直接下发的属性动作（tts/场景跳转不算）。</summary>
    public bool CanExecuteOnDevice(string? did) =>
        ActionsForDevice(did).Any(a => a.Writes.Count > 0);

    /// <summary>该设备在这条场景里可下发的属性写入条数。</summary>
    public int WritableCountForDevice(string? did) =>
        ActionsForDevice(did).Sum(a => a.Writes.Count(w => SameDid(w.Did ?? a.TargetDid, did)));

    private static bool Targets(SceneActionItem a, string did, bool exactOnly)
    {
        var hit = exactOnly ? string.Equals(a.TargetDid, did, StringComparison.Ordinal)
                            : SameDid(a.TargetDid, did);
        if (hit) return true;
        return a.Writes.Any(w => exactOnly
            ? string.Equals(w.Did ?? a.TargetDid, did, StringComparison.Ordinal)
            : SameDid(w.Did ?? a.TargetDid, did));
    }

    /// <summary>两个 did 是否指同一台设备（忽略 .sN 拆分后缀）。</summary>
    public static bool SameDid(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a, b, StringComparison.Ordinal)
               || string.Equals(BaseDid(a), BaseDid(b), StringComparison.Ordinal);
    }

    private static string BaseDid(string did)
    {
        var dot = did.LastIndexOf(".s", StringComparison.Ordinal);
        return dot > 0 ? did[..dot] : did;
    }

    /// <summary>触发源列表：优先完整条件，其次简化列表的 first_trigger。</summary>
    [JsonIgnore]
    public IReadOnlyList<SceneTrigger> Triggers =>
        Trigger?.Triggers is { Count: > 0 } t ? t
        : FirstTrigger != null ? new List<SceneTrigger> { FirstTrigger }
        : (IReadOnlyList<SceneTrigger>)Array.Empty<SceneTrigger>();

    private string BuildActionSummary()
    {
        var list = Actions;
        if (list.Count == 0) return "无动作";
        var parts = list.Take(3).Select(a => a.DisplayText).Where(s => !string.IsNullOrWhiteSpace(s));
        var text = string.Join(" · ", parts);
        if (list.Count > 3) text += $" 等 {list.Count} 项";
        return text;
    }

    private string BuildTriggerSummary()
    {
        var list = Triggers;
        if (list.Count == 0) return IsAutomation ? "手动触发" : "";
        var parts = list.Take(2).Select(t => t.DisplayText).Where(s => !string.IsNullOrWhiteSpace(s));
        var text = string.Join(" 且 ", parts);
        var express = Trigger?.Express ?? 1;
        if (list.Count > 2) text += $" 等 {list.Count} 个条件";
        else if (express == 2) text = text.Replace(" 且 ", " 或 ");
        return text;
    }

    /// <summary>场景生效时间段（自动化才有），cron 全零表示不限。</summary>
    [JsonIgnore]
    public string TimeWindowText
    {
        get
        {
            var w = TimeWindow;
            if (w == null || string.IsNullOrEmpty(w.From)) return "";
            if (w.From.StartsWith("0 0 0 ", StringComparison.Ordinal) && w.To == w.From) return "";
            return $"{w.From} → {w.To}";
        }
    }
}

/// <summary>场景动作组：mode 0 普通、1 反向（关灯类场景用）。</summary>
public class SceneActionGroup
{
    [JsonPropertyName("mode")] public int Mode { get; set; }
    [JsonPropertyName("actions")] public List<SceneActionItem> Actions { get; set; } = new();
}

/// <summary>一条场景动作。type 0=设备属性、1/3=设备动作、2=执行另一场景、4=设备组命令。</summary>
public class SceneActionItem
{
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("payload_json")] public SceneActionPayload? Payload { get; set; }
    [JsonPropertyName("nested_scene_info")] public SceneNestedScene? Nested { get; set; }

    /// <summary>这条动作作用的设备 did；设备组命令 / 执行场景时为 null。</summary>
    [JsonIgnore] public string? TargetDid => Payload?.Did;

    /// <summary>写给设备的属性列表（set_properties 才有）。</summary>
    [JsonIgnore] public IReadOnlyList<ScenePropWrite> Writes => Payload?.PropWrites ?? Array.Empty<ScenePropWrite>();

    /// <summary>
    /// 设备组批量动作（type=4「全屋照明」一类）不写 payload.did，只在 value.filter_dids
    /// 里点名参与的设备；这些也是本条场景的控制对象，建索引时要一起算上。
    /// </summary>
    [JsonIgnore] public IReadOnlyList<string> FilterDids => Payload?.FilterDids ?? Array.Empty<string>();

    /// <summary>一句话描述，用于卡片摘要与设备详情列表。</summary>
    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            var p = Payload;
            // 执行另一条场景
            if (Type == 2 || !string.IsNullOrEmpty(p?.SceneId))
            {
                var nested = Nested?.SceneName;
                return string.IsNullOrEmpty(nested) ? "执行场景" : $"执行「{nested}」";
            }

            var device = string.IsNullOrWhiteSpace(p?.DeviceName) ? "设备" : p!.DeviceName!;
            var delay = p?.DelayTime > 0 ? $"（延迟 {p.DelayTime} 秒）" : "";

            if (string.Equals(p?.Command, "tts", StringComparison.Ordinal))
                return $"{device} 播报{delay}";

            if (!string.IsNullOrWhiteSpace(p?.CommandType))
                return $"{device} {p!.CommandType}{delay}";

            var writes = Writes;
            if (writes.Count > 0)
            {
                var first = writes[0];
                var act = first.Describe();
                return $"{device} {act}{delay}";
            }

            if (string.Equals(p?.Command, "action", StringComparison.Ordinal))
                return $"{device} {Name}{delay}".Trim();

            return string.IsNullOrEmpty(Name) ? device : $"{device} {Name}{delay}";
        }
    }
}

/// <summary>动作载荷。value 的形态随 command 变化，保留原始 JSON 由上层按需解析。</summary>
public class SceneActionPayload
{
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("delay_time")] public int DelayTime { get; set; }
    [JsonPropertyName("device_name")] public string? DeviceName { get; set; }
    [JsonPropertyName("did")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Did { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("uid")] public string? Uid { get; set; }
    [JsonPropertyName("scene_id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? SceneId { get; set; }
    [JsonPropertyName("command_type")] public string? CommandType { get; set; }

    [JsonPropertyName("value")] public JsonElement? Value { get; set; }
    [JsonPropertyName("value_json")] public JsonElement? ValueJson { get; set; }
    [JsonPropertyName("filter_dids")] public JsonElement? RawFilterDids { get; set; }

    /// <summary>批量动作点名的设备 did：顶层 filter_dids 与 value / value_json 里的都收。</summary>
    [JsonIgnore]
    public IReadOnlyList<string> FilterDids
    {
        get
        {
            if (_filterDids != null) return _filterDids;
            var set = new HashSet<string>(StringComparer.Ordinal);
            ReadDids(set, RawFilterDids);
            if (Value is { ValueKind: JsonValueKind.Object } v &&
                v.TryGetProperty("filter_dids", out var nested)) ReadDids(set, nested);
            if (ValueJson is { ValueKind: JsonValueKind.Object } vj &&
                vj.TryGetProperty("filter_dids", out var nested2)) ReadDids(set, nested2);
            return _filterDids = set.ToList();
        }
    }

    private List<string>? _filterDids;

    /// <summary>filter_dids 可能是数组，也可能是以 did 为键的对象；两种都收成纯字符串。</summary>
    private static void ReadDids(HashSet<string> set, JsonElement? el)
    {
        if (el is not { } e) return;
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                var s = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
            }
        }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
                if (!string.IsNullOrWhiteSpace(p.Name)) set.Add(p.Name);
        }
        else if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString();
            if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
        }
    }

    [JsonIgnore] private List<ScenePropWrite>? _writes;

    /// <summary>把 value 里的 [{did,siid,piid,value}] 抽成强类型；形态不符时返回空。</summary>
    [JsonIgnore]
    public IReadOnlyList<ScenePropWrite> PropWrites
    {
        get
        {
            if (_writes != null) return _writes;
            _writes = new List<ScenePropWrite>();
            if (Value is not { ValueKind: JsonValueKind.Array } arr) return _writes;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("siid", out var siidEl) || siidEl.ValueKind != JsonValueKind.Number) continue;
                if (!item.TryGetProperty("piid", out var piidEl) || piidEl.ValueKind != JsonValueKind.Number) continue;
                var w = new ScenePropWrite
                {
                    Did = item.TryGetProperty("did", out var d) ? d.ToString() : Did,
                    Siid = siidEl.GetInt32(),
                    Piid = piidEl.GetInt32(),
                    Raw = item.TryGetProperty("value", out var v) ? v.Clone() : (JsonElement?)null
                };
                _writes.Add(w);
            }
            return _writes;
        }
    }
}

/// <summary>动作里的一条属性写入目标（A 阶段只用于展示，B 阶段可直接下发）。</summary>
public sealed class ScenePropWrite
{
    public string? Did { get; init; }
    public int Siid { get; init; }
    public int Piid { get; init; }
    public JsonElement? Raw { get; init; }

    /// <summary>值的中文描述：bool 说开关，数字带属性猜测。</summary>
    public string Describe()
    {
        if (Raw is not { } v) return $"siid{Siid}.piid{Piid}";
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return v.ValueKind == JsonValueKind.True ? "开" : "关";

        var text = v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i)
                ? i.ToString(CultureInfo.InvariantCulture) : v.GetDouble().ToString("0.#", CultureInfo.InvariantCulture),
            JsonValueKind.String => v.GetString() ?? "",
            _ => ""
        };

        var name = (Siid, Piid) switch
        {
            (2, 2) => "亮度",
            (2, 3) => "色温",
            (2, 4) => "模式",
            (3, 2) => "档位",
            _ => null
        };
        if (name != null) return string.IsNullOrEmpty(text) ? name : $"{name} {text}";
        return string.IsNullOrEmpty(text) ? $"siid{Siid}.piid{Piid}" : $"设为 {text}";
    }
}

/// <summary>触发条件组。express：1=全部满足（且），2=任一满足（或）。</summary>
public class SceneTriggerGroup
{
    [JsonPropertyName("express")] public int Express { get; set; } = 1;
    [JsonPropertyName("triggers")] public List<SceneTrigger> Triggers { get; set; } = new();
}

/// <summary>一条触发条件：src=device（设备事件/属性）、timer（定时）、user（手动/手机）。</summary>
public class SceneTrigger
{
    [JsonPropertyName("src")] public string Src { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("extra_json")] public SceneTriggerExtra? Extra { get; set; }
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("order")] public int Order { get; set; }

    /// <summary>触发源设备 did（device 类型才有）。</summary>
    [JsonIgnore] public string? DeviceDid => Extra?.Did;

    [JsonIgnore]
    public string DisplayText => Src switch
    {
        "device" => string.IsNullOrEmpty(Extra?.DeviceName)
            ? (string.IsNullOrEmpty(Name) ? "设备事件" : Name)
            : $"{Extra!.DeviceName} {Name}".Trim(),
        "timer" => string.IsNullOrEmpty(Name) ? "定时" : Name,
        "user" => string.IsNullOrEmpty(Name) ? "手动触发" : Name,
        _ => string.IsNullOrEmpty(Name) ? Src : Name
    };
}

/// <summary>触发条件的附加信息：设备名、did、型号；定时类里 Extra 可能是 cron 字符串。</summary>
public class SceneTriggerExtra
{
    [JsonPropertyName("device_name")] public string? DeviceName { get; set; }

    [JsonPropertyName("did")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Did { get; set; }

    [JsonPropertyName("model")] public string? Model { get; set; }
}

/// <summary>执行条件（「且满足」里的状态判断），结构同触发条件。</summary>
public class SceneConditionGroup
{
    [JsonPropertyName("express")] public int Express { get; set; }
    [JsonPropertyName("conditions")] public List<SceneTrigger> Conditions { get; set; } = new();
}

/// <summary>自动化生效时间段（cron 表达式）。</summary>
public class SceneTimeWindow
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("filter")] public string Filter { get; set; } = "";
    [JsonPropertyName("time_zone")] public string TimeZone { get; set; } = "";
}

/// <summary>动作里的嵌套场景信息（type=2 执行场景时给出被执行的场景名）。</summary>
public class SceneNestedScene
{
    [JsonPropertyName("scene_name")] public string SceneName { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
}
