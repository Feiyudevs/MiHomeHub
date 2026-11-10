using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>
/// 触发条件目录：按设备 MIoT 规格生成「能触发什么」的候选清单，并负责把用户的选择拼回
/// AppSceneService/Edit 认的触发源结构。
///
/// 结构已实测（2026-11-07，--trigger-test）：
///   {src:"device", key:"event.&lt;siid&gt;.&lt;eiid&gt;"|"prop.&lt;siid&gt;.&lt;piid&gt;",
///    value_type: 1 数值 / 4 布尔 / 5 无参事件 / 6 事件+筛选,
///    value_json: 值；事件筛选为 {sub_props:{attr:[{key:"prop.&lt;model&gt;.&lt;siid&gt;.&lt;piid&gt;",value,value_type:1}],express:0}},
///    sc_id: 服务端只校验 &gt; 0，不校验语义，这里按 siid/piid 生成稳定值}
/// </summary>
public static class SceneTriggerCatalog
{
    /// <summary>合成 sc_id 的基数：真实能力 id 都是 4–5 位，避开以免撞号。</summary>
    private const int ScIdBase = 900_000_000;

    /// <summary>某台设备可用的触发条件：先事件、后属性。</summary>
    public static async Task<List<TriggerOption>> BuildOptionsAsync(MijiaClient client, MijiaDevice device)
    {
        var list = new List<TriggerOption>();
        DeviceSpec? spec = null;
        try { spec = await DeviceSpecService.GetAsync(client, device); }
        catch { }
        if (spec == null) return list;

        foreach (var e in spec.Events)
        {
            var args = new List<TriggerArgSpec>();
            foreach (var piid in e.ArgPiids)
            {
                var p = spec.FindProp(e.Siid, piid);
                if (p == null) continue;
                // 枚举/布尔出参才做筛选；时间戳、操作 ID 这类没有筛选意义
                if (p.Options.Count == 0 && !p.IsBool) continue;
                args.Add(new TriggerArgSpec
                {
                    Piid = piid,
                    Name = p.Desc,
                    Options = p.Options,
                    IsBool = p.IsBool
                });
            }

            list.Add(new TriggerOption
            {
                Key = e.Key,
                Title = e.Desc,
                SubTitle = $"事件 · {e.ServiceDesc}",
                Axis = TriggerAxis.Event,
                Siid = e.Siid,
                RefId = e.Eiid,
                Event = e,
                Args = args
            });
        }

        foreach (var p in OrderedProperties(spec))
        {
            list.Add(new TriggerOption
            {
                Key = $"prop.{p.Siid}.{p.Piid}",
                Title = p.Desc,
                SubTitle = $"状态 · {p.ServiceDesc}",
                Axis = TriggerAxis.Property,
                Siid = p.Siid,
                RefId = p.Piid,
                Prop = p
            });
        }

        return list;
    }

    /// <summary>
    /// 属性候选：只留「开关量」和「枚举状态」——这两类才有「变成某值就触发」的意义。
    /// 数值/文本属性（电量、时间戳、统计次数…）走等值触发没有语义，直接不进列表。
    /// 排序：开关量在前，枚举次之；同类按规格顺序。
    /// </summary>
    private static IEnumerable<SpecPropertyDef> OrderedProperties(DeviceSpec spec)
    {
        return spec.Properties
            .Where(p => p.Readable)
            .Where(p => p.IsBool || p.Options.Count > 0)
            .Where(p => !IsReadOnlyReadout(p))
            .OrderBy(p => p.IsBool ? 0 : 1);
    }

    /// <summary>
    /// 纯读数 / 元信息类属性：即使碰巧是枚举，也不该当触发条件（米家 App 的「就执行」列表里同样不给选）。
    /// 按关键词挡掉，避免下拉被「电池电量 / 时间戳 / 统计 / 操作 ID / 操作方式」这类条目淹没。
    /// </summary>
    private static readonly string[] ReadoutKeywords =
    {
        "电量", "时间戳", "时间", "统计", "操作", "次数", "时长", "信号",
        "版本", "序号", "编号", "索引", "功率", "电压", "电流", "固件"
    };

    private static bool IsReadOnlyReadout(SpecPropertyDef p)
    {
        var text = $"{p.Desc} {p.Name}";
        foreach (var k in ReadoutKeywords)
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>把「设备 + 条件 + 筛选值」拼成一条触发源。</summary>
    public static JsonObject BuildNode(
        MijiaDevice device,
        TriggerOption option,
        IReadOnlyList<TriggerArgGroup> groups,
        int order)
    {
        var filters = groups.Where(g => g.Value != null).ToList();

        JsonNode? valueJson;
        int valueType;
        string name;

        if (option.Axis == TriggerAxis.Event)
        {
            var evt = option.Event!;
            if (filters.Count == 0)
            {
                valueType = 5;
                valueJson = JsonValue.Create("");
                name = evt.Desc;
            }
            else
            {
                valueType = 6;
                var attrs = new JsonArray();
                foreach (var g in filters)
                {
                    attrs.Add(new JsonObject
                    {
                        ["key"] = $"prop.{device.Model}.{evt.Siid}.{g.Piid}",
                        ["value"] = ToNode(g.Value),
                        // 出参里布尔和枚举混着来，按值的实际类型给 value_type
                        ["value_type"] = g.Value is bool ? 4 : 1
                    });
                }
                valueJson = new JsonObject
                {
                    ["sub_props"] = new JsonObject
                    {
                        ["attr"] = attrs,
                        ["express"] = 0
                    }
                };
                name = $"{evt.Desc}（{string.Join(" · ", filters.Select(g => g.Summary))}）";
            }
        }
        else
        {
            var prop = option.Prop!;
            var group = groups.FirstOrDefault();
            var value = group?.Value;
            if (prop.IsBool)
            {
                valueType = 4;
                var on = value is bool b && b;
                valueJson = JsonValue.Create(on);
                name = $"{prop.Desc}{Suffix(on)}";
            }
            else if (prop.Options.Count > 0)
            {
                valueType = 1;
                valueJson = ToNode(value ?? prop.Options[0].Value);
                name = $"{prop.Desc} {group?.Summary ?? ""}".Trim();
            }
            else
            {
                valueType = 1;
                valueJson = ToNode(value);
                var unit = string.IsNullOrEmpty(prop.Unit) ? "" : prop.Unit;
                name = $"{prop.Desc} {group?.Summary ?? ""}{unit}".Trim();
            }
        }

        return new JsonObject
        {
            ["id"] = 0,
            ["order"] = order,
            ["src"] = "device",
            ["key"] = option.Key,
            ["extra"] = "",
            ["name"] = name,
            ["value"] = "",
            ["value_type"] = valueType,
            ["extra_json"] = new JsonObject
            {
                ["device_name"] = device.Name,
                ["did"] = device.Did,
                ["model"] = device.Model
            },
            ["value_json"] = valueJson,
            ["protocol_type"] = 2,
            ["sc_id"] = ScIdBase + option.Siid * 100 + option.RefId,
            ["from"] = 1,
            ["value_operation"] = 0,
            ["std_sc_id"] = "",
            ["duration"] = 0,
            ["option_ids"] = new JsonArray(),
            ["data_ver"] = 0
        };
    }

    private static string Suffix(bool on) => on ? "打开" : "关闭";

    // ===================================================================
    // 定时触发（src=timer）。结构 2026-11-08 按真实报文实锤（scene-probe/04-scene-list.json）：
    //   {src:"timer", key:"timer.crontab", value_type:5, value:"",
    //    extra_json:{time_zone:"Asia/Shanghai"}, value_json:"0 0 6 * * * *", sc_id:12, from:0}
    // cron 为 7 段：秒 分 时 日 月 周 年；星期 1=周一 … 7=周日（米家网关同源约定）。
    // ===================================================================

    /// <summary>星期字段的特殊值：0=每天，8=工作日（周一至周五），9=周末；1–7 = 单个星期几。</summary>
    public const int DowEveryDay = 0;
    public const int DowWorkday = 8;
    public const int DowWeekend = 9;

    private static readonly string[] WeekNames = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    /// <summary>星期字段 → cron 片段。</summary>
    private static string DowToCron(int dow) => dow switch
    {
        DowWorkday => "1,2,3,4,5",
        DowWeekend => "6,7",
        DowEveryDay or < 0 => "*",
        >= 1 and <= 7 => dow.ToString(),
        _ => "*"
    };

    /// <summary>1–7 → 「周一…周日」。</summary>
    public static string WeekDayName(int dow) =>
        dow is >= 1 and <= 7 ? WeekNames[dow - 1] : "每天";

    /// <summary>星期字段的中文描述（不带时间）。</summary>
    private static string DowLabel(int dow) => dow switch
    {
        DowWorkday => "工作日",
        DowWeekend => "周末",
        DowEveryDay or < 0 => "每天",
        >= 1 and <= 7 => WeekNames[dow - 1],
        _ => "每天"
    };

    /// <summary>星期字段值 → 中文描述（供预览文案复用）。</summary>
    public static string DowLabelOf(int dow) => DowLabel(dow);

    /// <summary>把「时间 + 重复」拼成一条定时触发源（结构照抄真实报文）。</summary>
    public static JsonObject BuildTimerNode(int hour, int minute, int dow, int order)
    {
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        var cron = $"0 {minute} {hour} * * {DowToCron(dow)} *";
        var name = $"{hour:00}:{minute:00} {DowLabel(dow)}";

        return new JsonObject
        {
            ["id"] = 0,
            ["order"] = order,
            ["src"] = "timer",
            ["key"] = "timer.crontab",
            ["extra"] = "",
            ["name"] = name,
            ["value"] = "",
            ["value_type"] = 5,
            ["extra_json"] = new JsonObject { ["time_zone"] = TimeZoneId() },
            ["value_json"] = cron,
            ["protocol_type"] = 2,
            ["sc_id"] = 12,
            ["from"] = 0,
            ["value_operation"] = 0,
            ["std_sc_id"] = "",
            ["duration"] = 0,
            ["option_ids"] = new JsonArray(),
            ["data_ver"] = 0
        };
    }

    /// <summary>解析已有定时触发的 cron（形态不对时回退 08:00 每天）。</summary>
    public static (int Hour, int Minute, int Dow) ParseTimerNode(JsonObject node)
    {
        var cron = node["value_json"]?.ToString();
        var seg = cron?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        if (seg.Length < 6
            || !int.TryParse(seg[2], out var hour) || hour is < 0 or > 23
            || !int.TryParse(seg[1], out var minute) || minute is < 0 or > 59)
            return (8, 0, DowEveryDay);

        var dowField = seg[5];
        var dow = dowField switch
        {
            "*" => DowEveryDay,
            "1,2,3,4,5" => DowWorkday,
            "6,7" => DowWeekend,
            _ when int.TryParse(dowField, out var d) && d is >= 1 and <= 7 => d,
            _ => DowEveryDay
        };
        return (hour, minute, dow);
    }

    /// <summary>cron → 人话（「每天 08:00」），列表副标题用。</summary>
    public static string DescribeTimerCron(string? cron)
    {
        var seg = cron?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        if (seg.Length < 6
            || !int.TryParse(seg[2], out var hour) || hour is < 0 or > 23
            || !int.TryParse(seg[1], out var minute) || minute is < 0 or > 59)
            return "按时间触发";

        var dowField = seg[5];
        if (dowField != "*" && dowField.Contains(',', StringComparison.Ordinal)
            && dowField != "1,2,3,4,5" && dowField != "6,7")
        {
            var days = dowField
                .Split(',')
                .Select(s => int.TryParse(s, out var d) && d is >= 1 and <= 7 ? WeekNames[d - 1] : null)
                .Where(s => s != null);
            return $"每周{string.Join("、", days)} {hour:00}:{minute:00}";
        }

        var dow = dowField switch
        {
            "*" => DowEveryDay,
            "1,2,3,4,5" => DowWorkday,
            "6,7" => DowWeekend,
            _ when int.TryParse(dowField, out var d) && d is >= 1 and <= 7 => d,
            _ => DowEveryDay
        };
        return $"{DowLabel(dow)} {hour:00}:{minute:00}";
    }

    /// <summary>定时触发写进 extra_json 的时区，与新建场景骨架同一套判定。</summary>
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

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(value.ToString())
    };

    /// <summary>
    /// 已有触发源的次级描述：属性触发显示「= 值」，事件触发显示筛选条件。
    /// 规格拿不到时退回 key 的原始形态，不至于空着。
    /// </summary>
    /// <summary>
    /// 安全取子节点：父节点不是对象时返回 null。
    /// 云端个别触发源的 value_json / extra_json 直接给成字符串或数字，
    /// 直接 ?["x"] 会抛 InvalidOperationException（"The node must be of type 'JsonObject'"）并崩进程。
    /// </summary>
    public static JsonNode? Sub(JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    public static async Task<string> DescribeAsync(MijiaClient? client, MijiaDevice? device, JsonObject node)
    {
        var key = node["key"]?.ToString() ?? "";
        var valueJson = node["value_json"];

        if (client == null || device == null)
            return valueJson is null ? key : valueJson.ToJsonString();

        DeviceSpec? spec = null;
        try { spec = await DeviceSpecService.GetAsync(client, device); }
        catch { }
        if (spec == null) return key;

        var parts = key.Split('.');
        if (parts.Length < 3) return key;

        if (parts[0] == "event" && int.TryParse(parts[1], out var siid) && int.TryParse(parts[2], out var eiid))
        {
            var attrs = Sub(Sub(valueJson, "sub_props"), "attr") as JsonArray;
            if (attrs == null || attrs.Count == 0) return "任意上报都触发";
            var labels = new List<string>();
            foreach (var a in attrs.OfType<JsonObject>())
            {
                var propKey = a["key"]?.ToString() ?? "";
                var seg = propKey.Split('.');
                if (seg.Length < 4 || !int.TryParse(seg[^2], out var s2) || !int.TryParse(seg[^1], out var p2))
                    continue;
                var def = spec.FindProp(s2, p2);
                var valText = LabelOf(def, a["value"]);
                labels.Add(string.IsNullOrEmpty(def?.Desc) ? valText : $"{def!.Desc} {valText}".Trim());
            }
            return labels.Count == 0 ? "任意上报都触发" : string.Join(" · ", labels);
        }

        if (parts[0] == "prop" && int.TryParse(parts[1], out var psiid) && int.TryParse(parts[2], out var ppiid))
        {
            var def = spec.FindProp(psiid, ppiid);
            var text = LabelOf(def, valueJson);
            return string.IsNullOrEmpty(def?.Desc) ? key : $"{def!.Desc} = {text}";
        }

        return key;
    }

    private static string LabelOf(SpecPropertyDef? def, JsonNode? value)
    {
        if (value == null) return "";
        if (value is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b ? "开" : "关";
            if (v.TryGetValue<int>(out var i))
                return def?.Options.FirstOrDefault(o => o.Value == i)?.Label ?? i.ToString();
        }
        return value.ToJsonString().Trim('"');
    }
}
