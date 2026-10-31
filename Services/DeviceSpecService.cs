using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>
/// 设备 MIoT 规格解析：优先用云端下发的设备实例规格（带真实 iid），失败再退回公开规格站。
/// 按 spec_type 缓存（同型号共用一份），解析结果驱动通用属性面板与开关通道。
/// </summary>
public static class DeviceSpecService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly ConcurrentDictionary<string, Lazy<Task<DeviceSpec?>>> Cache = new();

    /// <summary>
    /// 内部配置服务（设备信息、场景键位、屏幕保护/整机设置、遥控器配对）不是日常控制项，
    /// 面板展示它们会刷出几十行字符串配置，直接整段跳过。
    /// </summary>
    private static readonly HashSet<string> InternalServices = new(StringComparer.Ordinal)
    {
        "device-information", "scene", "misc", "switch-ext", "default"
    };

    /// <summary>拉取并解析某台设备的规格；同 spec_type 的设备共用一次请求。</summary>
    public static Task<DeviceSpec?> GetAsync(MijiaClient client, MijiaDevice device)
    {
        var key = string.IsNullOrEmpty(device.SpecType) ? device.Did : device.SpecType;
        return Cache.GetOrAdd(key, _ => new Lazy<Task<DeviceSpec?>>(() => FetchAsync(client, device, key))).Value;
    }

    private static async Task<DeviceSpec?> FetchAsync(MijiaClient client, MijiaDevice device, string key)
    {
        try
        {
            var root = await client.GetSpecAsync(device.Did);
            var spec = ParseRoot(root);
            if (spec.Valid) return spec;
        }
        catch
        {
            // 设备级规格可能因离线/权限失败，继续走公开规格
        }

        try
        {
            if (string.IsNullOrEmpty(device.SpecType)) return null;
            var url = "https://miot-spec.org/miot-spec-v2/instance?type=" +
                      Uri.EscapeDataString(device.SpecType);
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var spec = ParseRoot(doc.RootElement);
            if (spec.Valid) return spec;
        }
        catch
        {
            // 网络失败不缓存，下次打开抽屉或重新加载时再试
        }

        Cache.TryRemove(key, out _);
        return null;
    }

    /// <summary>兼容 getspec（{services:[…]}）和 miot-spec.org（{services:[…]}）两种外壳。</summary>
    public static DeviceSpec ParseRoot(JsonElement root)
    {
        var spec = new DeviceSpec();
        if (root.ValueKind != JsonValueKind.Object) return spec;
        if (!root.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Array)
            return spec;
        return Parse(services);
    }

    public static DeviceSpec Parse(JsonElement services)
    {
        var spec = new DeviceSpec();
        if (services.ValueKind != JsonValueKind.Array) return spec;

        foreach (var svc in services.EnumerateArray())
        {
            if (svc.ValueKind != JsonValueKind.Object) continue;
            if (!svc.TryGetProperty("type", out var typeEl)) continue;

            var parts = (typeEl.GetString() ?? "").Split(':');
            if (parts.Length < 5 || parts[0] != "urn" || parts[2] != "service") continue;
            var serviceName = parts[3];
            if (InternalServices.Contains(serviceName)) continue;

            var siid = svc.TryGetProperty("iid", out var siidEl) ? siidEl.GetInt32() : 0;
            if (siid <= 0) continue;
            // 公开规格站只有英文 description，中文放在 comment（如「灯带」「布窗」「运动状态切换」），中文优先；
            // 部分设备（第三方电视/红外遥控）云端无规格或 comment 缺失，用短名翻译兜底
            var serviceDesc = ReadText(svc, "comment") ?? Translate(serviceName) ?? ReadText(svc, "description") ?? serviceName;

            if (svc.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in props.EnumerateArray())
                {
                    var prop = ParseProperty(p, siid, serviceName, serviceDesc);
                    if (prop != null) spec.Properties.Add(prop);
                }
            }

            if (svc.TryGetProperty("actions", out var acts) && acts.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in acts.EnumerateArray())
                {
                    var action = ParseAction(a, siid, serviceDesc);
                    if (action != null) spec.Actions.Add(action);
                }
            }
        }
        return spec;
    }

    private static SpecPropertyDef? ParseProperty(JsonElement p, int siid, string serviceName, string serviceDesc)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty("type", out var t)) return null;
        var parts = (t.GetString() ?? "").Split(':');
        if (parts.Length < 5 || parts[2] != "property") return null;
        var name = parts[3];

        var piid = p.TryGetProperty("iid", out var piidEl) ? piidEl.GetInt32() : 0;
        if (piid <= 0) return null;

        var access = p.TryGetProperty("access", out var acc) && acc.ValueKind == JsonValueKind.Array
            ? acc.EnumerateArray().Select(x => x.GetString()).ToList()
            : new List<string?>();
        var readable = access.Count == 0 || access.Contains("read");
        var writable = access.Contains("write");

        var min = 0d;
        var max = 0d;
        var step = 1d;
        var hasRange = false;
        if (p.TryGetProperty("value-range", out var vr) && ReadRange(vr) is { } range)
        {
            min = range.min;
            max = range.max;
            step = range.step;
            hasRange = true;
        }

        var rawDesc = ReadText(p, "comment") ?? Translate(name) ?? ReadText(p, "description") ?? name;
        // 属性描述也可能带括号技术说明和末尾数字，统一清理
        var desc = CleanLabel(rawDesc);

        var def = new SpecPropertyDef
        {
            Siid = siid,
            Piid = piid,
            Name = name,
            Desc = desc,
            ServiceName = serviceName,
            ServiceDesc = serviceDesc,
            Format = ReadText(p, "format") ?? "",
            Readable = readable,
            Writable = writable,
            Unit = NormalizeUnit(ReadText(p, "unit"), name),
            Min = min,
            Max = max,
            Step = step,
            HasRange = hasRange
        };

        if (p.TryGetProperty("value-list", out var vl) && vl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in vl.EnumerateArray())
            {
                if (!item.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number) continue;
                var value = v.TryGetInt32(out var iv) ? iv : (int)v.GetDouble();
                var rawLabel = ReadText(item, "comment") ?? ReadText(item, "description") ?? value.ToString();
                // 清理标签：去掉括号内的技术说明和末尾的数字 ID
                var label = CleanLabel(rawLabel);
                def.Options.Add(new SpecOptionDef { Value = value, Label = label });
            }
        }

        return def;
    }

    private static SpecActionDef? ParseAction(JsonElement a, int siid, string serviceDesc)
    {
        if (a.ValueKind != JsonValueKind.Object) return null;
        if (!a.TryGetProperty("type", out var t)) return null;
        var parts = (t.GetString() ?? "").Split(':');
        if (parts.Length < 5 || parts[2] != "action") return null;
        var aiid = a.TryGetProperty("iid", out var aiidEl) ? aiidEl.GetInt32() : 0;
        if (aiid <= 0) return null;

        var inputs = a.TryGetProperty("in", out var inEl) && inEl.ValueKind == JsonValueKind.Array
            ? inEl.GetArrayLength() : 0;

        return new SpecActionDef
        {
            Siid = siid,
            Aiid = aiid,
            Name = parts[3],
            Desc = ReadText(a, "comment") ?? Translate(parts[3]) ?? ReadText(a, "description") ?? parts[3],
            ServiceDesc = serviceDesc,
            InputCount = inputs
        };
    }

    private static (double min, double max, double step)? ReadRange(JsonElement vr)
    {
        if (vr.ValueKind == JsonValueKind.Array)
        {
            var arr = vr.EnumerateArray().ToList();
            if (arr.Count < 2) return null;
            var min = AsDouble(arr[0]);
            var max = AsDouble(arr[1]);
            var step = arr.Count >= 3 ? AsDouble(arr[2]) : 1;
            return (min, max, step <= 0 ? 1 : step);
        }
        if (vr.ValueKind == JsonValueKind.Object &&
            vr.TryGetProperty("min", out var mn) && vr.TryGetProperty("max", out var mx))
        {
            var step = vr.TryGetProperty("step", out var st) ? AsDouble(st) : 1;
            return (AsDouble(mn), AsDouble(mx), step <= 0 ? 1 : step);
        }
        return null;
    }

    private static double AsDouble(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;

    private static string? ReadText(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>规格里的单位是英文键（percentage / kelvin / celsius），换成界面符号。</summary>
    private static string NormalizeUnit(string? unit, string name) => unit switch
    {
        "percentage" => "%",
        "kelvin" => "K",
        "celsius" => "℃",
        "degrees" => "°",
        "seconds" => "秒",
        "minutes" => "分钟",
        "hours" => "小时",
        "days" => "天",
        "rgb" => "",
        null or "" => GuessUnit(name),
        _ => unit
    };

    /// <summary>规格常不给 unit，按属性名补一个展示单位。</summary>
    private static string GuessUnit(string name) => name switch
    {
        "temperature" or "target-temperature" => "℃",
        "relative-humidity" or "humidity" or "battery-level" or "battery" => "%",
        "pm2.5" or "pm10" => "µg/m³",
        "illuminance" => "lx",
        "co2" => "ppm",
        "power-consumption" => "W",
        "color-temperature" => "K",
        "brightness" => "%",
        _ => ""
    };

    /// <summary>
    /// MIoT 短名到中文的翻译表。公开规格站只有英文 description，云端 getspec 的 comment 优先，
    /// 但部分设备（如第三方电视/红外遥控）云端无规格或 comment 缺失，这里兜底。
    /// </summary>
    private static readonly Dictionary<string, string> ZhNames = new(StringComparer.Ordinal)
    {
        // 服务
        ["light"] = "灯",
        ["television"] = "电视",
        ["ir-tv-control"] = "红外电视控制",
        ["remote-control-management"] = "遥控器管理",
        ["switch"] = "开关",
        ["outlet"] = "插座",
        ["curtain"] = "窗帘",
        ["air-conditioner"] = "空调",
        ["fan"] = "风扇",
        ["humidifier"] = "加湿器",
        ["purifier"] = "净化器",
        ["vacuum"] = "扫地机",
        ["sensor"] = "传感器",
        ["battery"] = "电池",
        ["environment"] = "环境",
        ["physical-controls-locked"] = "童锁",
        ["indicator-light"] = "指示灯",

        // 属性
        ["on"] = "开关",
        ["brightness"] = "亮度",
        ["color-temperature"] = "色温",
        ["mode"] = "模式",
        ["fan-level"] = "风速",
        ["target-temperature"] = "目标温度",
        ["temperature"] = "温度",
        ["relative-humidity"] = "湿度",
        ["humidity"] = "湿度",
        ["pm2.5"] = "PM2.5",
        ["pm10"] = "PM10",
        ["illuminance"] = "照度",
        ["battery-level"] = "电量",
        ["battery"] = "电量",
        ["co2"] = "二氧化碳",
        ["tvoc"] = "TVOC",
        ["tds-in"] = "TDS进水",
        ["air-quality"] = "空气质量",
        ["water-level"] = "水位",
        ["power-consumption"] = "功耗",
        ["voltage"] = "电压",
        ["electric-current"] = "电流",
        ["motion-detected"] = "运动检测",
        ["contact-state"] = "接触状态",
        ["occupancy"] = "占用",
        ["smoke-state"] = "烟雾状态",
        ["leak"] = "漏水",
        ["door-state"] = "门状态",
        ["submersion-state"] = "浸水状态",
        ["default-power-on-state"] = "默认上电状态",
        ["flex-switch"] = "凌动开关",
        ["night-light-switch"] = "夜灯开关",
        ["remote-control-addable"] = "遥控器添加开关",
        ["remote-control-list"] = "遥控器管理",
        ["remote-add"] = "添加遥控",
        ["remote-del"] = "删除遥控",
        ["remote-switch"] = "遥控开关",

        // 动作
        ["toggle"] = "切换",
        ["brightness-up"] = "亮度+",
        ["brightness-down"] = "亮度-",
        ["channel-down"] = "频道-",
        ["channel-up"] = "频道+",
        ["volume-down"] = "音量-",
        ["volume-up"] = "音量+",
        ["volume-increase"] = "音量增加值",
        ["volume-decrease"] = "音量减少值",
        ["turn-on"] = "开机",
        ["turn-off"] = "关机",
        ["mute-on"] = "静音",
        ["mute-off"] = "取消静音",
        ["input-source-switch"] = "切换输入源",
        ["toggle-light-mode"] = "切换灯光模式",
        ["delete-remote-control"] = "删除遥控器"
    };

    /// <summary>按短名查中文，未命中返回 null。</summary>
    private static string? Translate(string name) =>
        ZhNames.TryGetValue(name, out var zh) ? zh : null;

    /// <summary>
    /// 清理值列表标签和属性描述：去掉括号内的技术说明、冒号后的详细解释、
    /// 末尾的数字 ID，以及包含"字节/bit"等技术性内容的描述，只保留用户友好的名称部分。
    /// </summary>
    private static string CleanLabel(string label)
    {
        if (string.IsNullOrEmpty(label)) return label;

        // 去掉第一个左括号及其后面的内容（包括全角和半角括号）
        var parenIndex = label.IndexOfAny(new[] { '(', '（' });
        if (parenIndex >= 0)
            label = label[..parenIndex];

        // 去掉冒号及其后面的内容（包括全角和半角冒号）- 用于"节律功能：字节0开关..."这种情况
        var colonIndex = label.IndexOfAny(new[] { ':', '：' });
        if (colonIndex >= 0)
            label = label[..colonIndex];

        // 如果包含技术性关键词（字节、bit、参数、配置等），只保留关键词前面的部分
        var techKeywords = new[] { "字节", "bit", "byte", "参数", "配置", "功能" };
        foreach (var keyword in techKeywords)
        {
            var techIndex = label.IndexOf(keyword, StringComparison.Ordinal);
            if (techIndex > 0)  // 只处理关键词不在开头的情况
            {
                label = label[..techIndex];
                break;
            }
        }

        // 去掉末尾的数字和空格
        label = label.TrimEnd();
        while (label.Length > 0 && (char.IsDigit(label[^1]) || label[^1] == ' '))
            label = label[..^1];

        // 限制长度，超出部分用省略号
        const int maxLength = 12;
        if (label.Length > maxLength)
            label = label[..maxLength] + "…";

        return label.Trim();
    }
}
