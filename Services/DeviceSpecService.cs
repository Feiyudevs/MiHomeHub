// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MiHomeHub.Models;

namespace MiHomeHub.Services;

/// <summary>
/// 设备 MIoT 规格解析：优先用云端下发的设备实例规格（带真实 iid），失败再退回公开规格站。
/// 按 spec_type 缓存（同型号共用一份），解析结果驱动通用属性面板与开关通道。
/// </summary>
public static class DeviceSpecService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>规格缓存上限：超出时淘汰最久未使用的条目，避免 DeviceSpec 对象只增不减。</summary>
    private static readonly int MaxSpecCache = 256;

    private static readonly ConcurrentDictionary<string, Lazy<Task<DeviceSpec?>>> Cache = new();

    /// <summary>LRU 访问顺序，用于淘汰最久未使用的规格。</summary>
    private static readonly LinkedList<string> SpecLru = new();
    private static readonly object SpecLruLock = new();

    private static void TouchSpecLru(string key)
    {
        lock (SpecLruLock)
        {
            SpecLru.Remove(key);
            SpecLru.AddLast(key);
            while (SpecLru.Count > MaxSpecCache)
            {
                var old = SpecLru.First!.Value;
                SpecLru.RemoveFirst();
                Cache.TryRemove(old, out _);
            }
        }
    }

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
        var task = Cache.GetOrAdd(key, _ => new Lazy<Task<DeviceSpec?>>(() => FetchAsync(client, device, key))).Value;
        // 每次访问都更新 LRU：热门规格留在缓存里，冷门规格优先淘汰
        TouchSpecLru(key);
        return task;
    }

    /// <summary>清空全部规格缓存；退出登录时调用，避免跨会话残留 DeviceSpec 对象。</summary>
    public static void Clear()
    {
        Cache.Clear();
        lock (SpecLruLock) SpecLru.Clear();
    }

    private static async Task<DeviceSpec?> FetchAsync(MijiaClient client, MijiaDevice device, string key)
    {
        try
        {
            var root = await client.GetSpecAsync(device.Did);
            var spec = ParseRoot(root);
            if (spec.Valid)
            {
                ApplyModelQuirks(spec, device.Model);
                return spec;
            }
        }
        catch
        {
            // 设备级规格可能因离线/权限失败，继续走公开规格
        }

        // 公开规格站：先按上报的 spec_type，再试去掉尾部 ID 的写法；
        // 一些第三方设备（文光汇这类计量插座）列表里根本没有 spec_type，
        // 这时再用 model 反推公开规格地址，否则插座永远只有开关、没有电量/电压读数。
        foreach (var type in PublicSpecTypes(device))
        {
            try
            {
                var url = "https://miot-spec.org/miot-spec-v2/instance?type=" +
                          Uri.EscapeDataString(type);
                var json = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var spec = ParseRoot(doc.RootElement);
                if (spec.Valid)
                {
                    ApplyModelQuirks(spec, device.Model);
                    return spec;
                }
            }
            catch
            {
                // 单个地址 404/网络失败继续试下一个候选
            }
        }

        Cache.TryRemove(key, out _);
        return null;
    }

    /// <summary>
    /// 公开规格站可尝试的地址：设备上报的 spec_type、去掉尾部 0000XXXX ID 的写法，
    /// 以及 spec_type 缺失时按 model 反推的插座类规格（厂家-型号、厂家-中段-型号，版本 1/2）。
    /// </summary>
    private static List<string> PublicSpecTypes(MijiaDevice device)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? type)
        {
            if (!string.IsNullOrWhiteSpace(type) && seen.Add(type)) list.Add(type);
        }

        var specType = device.SpecType?.Trim() ?? "";
        Add(specType);
        var parts = specType.Split(':');
        if (parts.Length >= 8) Add(string.Join(':', parts.Take(parts.Length - 1)));

        var category = device.CategoryName;
        var isSocket = category is "outlet" or "smart-outlet" ||
                       (category.Length == 0 && device.IconKey == "socket");
        if (isSocket)
        {
            var model = (device.Model ?? "").Trim().ToLowerInvariant();
            var segs = model.Split('.');
            if (segs.Length >= 2)
            {
                var names = new List<string> { $"{segs[0]}-{segs[^1]}" };
                if (segs.Length >= 3) names.Add($"{segs[0]}-{string.Join('-', segs.Skip(1))}");
                foreach (var name in names)
                {
                    for (var version = 1; version <= 2; version++)
                        Add($"urn:miot-spec-v2:device:outlet:0000A002:{name}:{version}");
                }
            }
        }

        return list;
    }

    /// <summary>
    /// 个别第三方型号的规格单位与固件上报口径对不上，解析完按型号校准：
    /// 文光汇 v8icwf 的耗电量是 0.01Wh 计数器（1/100000 度），v9wh 是 0.01 度；
    /// v9wh 还把功率（高 20 位）、电压（低 12 位）打包在 5.1 data 里，这里补两条只读读数。
    /// </summary>
    private static void ApplyModelQuirks(DeviceSpec spec, string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        if (m == "sixwgh.plug.v8icwf")
        {
            SetCounterScale(spec, 0.00001);
        }
        else if (m == "sixwgh.plug.v9wh")
        {
            SetCounterScale(spec, 0.01);
            if (spec.FindProp(5, 1) is { } data)
            {
                AddPackedProp(spec, data, "electric-power", "功率", "W", 0.01, 31, 12);
                AddPackedProp(spec, data, "voltage", "电压", "V", 0.1, 11, 0);
            }
        }
    }

    private static void SetCounterScale(DeviceSpec spec, double scale)
    {
        foreach (var p in spec.Properties)
        {
            if (p.Name is "power-consumption" or "energy-consumption" or "power-consume" or "electricity-consumption")
            {
                p.UnitScale = scale;
                p.ValueScale = scale;
            }
        }
    }

    private static void AddPackedProp(DeviceSpec spec, SpecPropertyDef source,
        string name, string desc, string unit, double scale, int highBit, int lowBit)
    {
        if (spec.Properties.Any(p => p.Name == name)) return;
        spec.Properties.Add(new SpecPropertyDef
        {
            Siid = source.Siid,
            Piid = source.Piid,
            Name = name,
            Desc = desc,
            RawDesc = desc,
            ServiceName = source.ServiceName,
            ServiceDesc = source.ServiceDesc,
            Format = "uint32",
            Readable = true,
            Writable = false,
            Unit = unit,
            UnitScale = 1,
            ValueScale = scale,
            BitHigh = highBit,
            BitLow = lowBit
        });
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
            spec.Services.Add(serviceName);
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

            if (svc.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in events.EnumerateArray())
                {
                    var evt = ParseEvent(e, siid, serviceDesc);
                    if (evt != null) spec.Events.Add(evt);
                }
            }
        }
        return spec;
    }

    private static SpecEventDef? ParseEvent(JsonElement e, int siid, string serviceDesc)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("type", out var t)) return null;
        var parts = (t.GetString() ?? "").Split(':');
        if (parts.Length < 5 || parts[2] != "event") return null;

        var eiid = e.TryGetProperty("iid", out var eiidEl) ? eiidEl.GetInt32() : 0;
        if (eiid <= 0) return null;

        var args = new List<int>();
        if (e.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in argsEl.EnumerateArray())
            {
                // 规格站给的是 piid 数字数组；个别设备给对象，取 iid
                if (a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var p) && p > 0) args.Add(p);
                else if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("iid", out var ai) && ai.GetInt32() > 0)
                    args.Add(ai.GetInt32());
            }
        }

        // 事件名不做 CleanLabel：中文 comment 里常有「（除钥匙）」这类关键区分信息
        var desc = ReadText(e, "comment") ?? ReadText(e, "description") ?? parts[3];
        return new SpecEventDef
        {
            Siid = siid,
            Eiid = eiid,
            Name = parts[3],
            Desc = desc,
            ServiceDesc = serviceDesc,
            ArgPiids = args
        };
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
        var rawUnit = ReadText(p, "unit");
        var unitScale = CounterScale(rawUnit, rawDesc, name);

        var def = new SpecPropertyDef
        {
            Siid = siid,
            Piid = piid,
            Name = name,
            Desc = desc,
            RawDesc = rawDesc,
            ServiceName = serviceName,
            ServiceDesc = serviceDesc,
            Format = ReadText(p, "format") ?? "",
            Readable = readable,
            Writable = writable,
            Unit = NormalizeUnit(rawUnit, name),
            UnitScale = unitScale,
            ValueScale = name is "power-consumption" or "energy-consumption" or "power-consume"
                or "electricity-consumption" ? unitScale : UnitValueScale(name, rawUnit),
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
                var rawLabel = ReadText(item, "comment") ?? ReadText(item, "description");
                // 清理标签：去掉括号内的技术说明和末尾的数字 ID
                var label = CleanLabel(rawLabel ?? "");
                // 规格里没写 comment/description 时只剩值本身，而 CleanLabel 会把纯数字标签整段削空，
                // 空标签在下拉 / 分段按钮里就是一枚没有字的胶囊，回落到原始数值
                if (string.IsNullOrWhiteSpace(label)) label = value.ToString();
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

    /// <summary>
    /// 规格里的单位是英文键（percentage / kelvin / celsius），换成界面符号。
    /// 第三方机型常把「没有单位」写成 none / null / n/a / 「-」，这些一律当没写，
    /// 否则电压就成了「650none」这种读不通的字；交回 <see cref="GuessUnit"/> 按属性名补。
    /// </summary>
    private static string NormalizeUnit(string? unit, string name)
    {
        var text = unit?.Trim() ?? "";
        if (text.Length == 0 || IsPlaceholderUnit(text)) return GuessUnit(name);

        var lower = text.ToLowerInvariant();
        // 耗电量统一按「度」展示，倍率由 ValueScale 折算（0.01 度 / 1Wh 计数器都能读对）
        if (name is "power-consumption" or "energy-consumption" or "power-consume" or "electricity-consumption")
        {
            if (lower.Contains("wh") || lower.Contains("千瓦时") || lower.Contains("度")) return "度";
        }

        return lower switch
        {
            "percentage" => "%",
            "kelvin" => "K",
            "celsius" => "℃",
            "degrees" => "°",
            "seconds" => "秒",
            "minutes" => "分钟",
            "hours" => "小时",
            "days" => "天",
            "watt" or "watts" => "W",
            "kilowatt" or "kilowatts" => name is "electric-power" or "power" or "surge-power" ? "W" : "kW",
            "volt" or "volts" or "mv" or "毫伏" => "V",
            "ampere" or "amperes" or "ma" or "毫安" => "A",
            "lux" => "lx",
            "ppm" => "ppm",
            "microgram-per-cubic-meter" => "µg/m³",
            "mg/m3" or "mg/m³" => "mg/m³",
            "db" => "dB",
            "rgb" => "",
            _ => text
        };
    }

    /// <summary>非耗电量属性里，原始值到展示值的倍率（mA→A、mV→V、kW→W）。</summary>
    private static double UnitValueScale(string name, string? unit)
    {
        var u = (unit ?? "").Trim().ToLowerInvariant();
        if (name is "electric-current" or "current" && (u == "ma" || u.Contains("毫安"))) return 0.001;
        if (name is "voltage" or "electric-voltage" && (u == "mv" || u.Contains("毫伏"))) return 0.001;
        if (name is "electric-power" or "power" or "surge-power" && (u == "kw" || u.Contains("千瓦"))) return 1000;
        return 1;
    }

    /// <summary>规格里表示「这个属性没有单位」的占位写法。</summary>
    private static bool IsPlaceholderUnit(string text) =>
        text.Equals("none", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("null", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("na", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("default", StringComparison.OrdinalIgnoreCase) ||
        text is "-" or "—" or "无" or "无单位";

    /// <summary>规格常不给 unit，按属性名补一个展示单位。</summary>
    private static string GuessUnit(string name) => name switch
    {
        "temperature" or "target-temperature" => "℃",
        "relative-humidity" or "humidity" or "battery-level" or "battery" or "charge-progress" => "%",
        "pm2.5" or "pm2.5-density" or "pm10" or "pm10-density" or "pm1" or "pm1-density" => "µg/m³",
        "illuminance" => "lx",
        "co2" or "co2-density" => "ppm",
        "hcho" or "hcho-density" => "mg/m³",
        "noise" or "noise-decibel" => "dB",
        "voltage" or "electric-voltage" => "V",
        "electric-current" or "current" => "A",
        "electric-power" or "power" or "surge-power" => "W",
        "power-consumption" or "energy-consumption" => "度",
        "color-temperature" => "K",
        "brightness" => "%",
        "tds-in" or "tds-out" => "ppm",
        "filter-used-flow" or "filter-left-flow" => "L",
        "cumu-flow-out" or "cumu-flow-in" or "flow-out" or "flow-in" => "mL",
        _ => ""
    };

    /// <summary>
    /// 耗电量计数器的单位倍率。MIoT 规格把倍率写进属性描述里（「耗电量（单位:0,01Kwh）」「电量（单位：1Wh）」），
    /// 属性值只是整数计数器：不换算就会把 0.01 度读成 1 度（放 100 倍），把 1Wh 读成 1 度（放 1000 倍）。
    /// 描述在 <see cref="CleanLabel"/> 前取，括号里的单位说明会被清理掉，所以只看原始描述。
    /// </summary>
    public static double CounterScale(string? unit, string? rawDesc, string name)
    {
        if (name is not ("power-consumption" or "energy-consumption" or "power-consume" or "electricity-consumption"))
            return 1;

        var text = $"{unit} {rawDesc}".ToLowerInvariant()
            .Replace('，', ',').Replace('：', ':').Replace(" ", "");

        var isKwh = text.Contains("kwh") || text.Contains("kw·h") || text.Contains("千瓦时") || text.Contains("度");
        if (isKwh)
        {
            if (text.Contains("0,001") || text.Contains("0.001")) return 0.001;
            if (text.Contains("0,01") || text.Contains("0.01")) return 0.01;
            return 1;
        }
        // 只写 Wh 没写 kWh 的是瓦时计数器
        if (text.Contains("wh") || text.Contains("瓦时")) return 0.001;
        return 1;
    }

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
        ["pm2.5-density"] = "PM2.5",
        ["pm10"] = "PM10",
        ["pm10-density"] = "PM10",
        ["pm1"] = "PM1",
        ["pm1-density"] = "PM1",
        ["illuminance"] = "照度",
        ["battery-level"] = "电量",
        ["battery"] = "电量",
        ["charge-progress"] = "电量",
        ["co2"] = "二氧化碳",
        ["co2-density"] = "二氧化碳",
        ["tvoc"] = "TVOC",
        ["tvoc-density"] = "eTVOC",
        ["voc-density"] = "VOC",
        ["hcho"] = "甲醛",
        ["hcho-density"] = "甲醛",
        ["noise"] = "噪音",
        ["noise-decibel"] = "噪音",
        ["dust"] = "粉尘",
        ["dust-density"] = "粉尘",
        ["tds-in"] = "TDS进水",
        ["tds-out"] = "TDS出水",
        ["air-quality"] = "空气质量",
        ["water-level"] = "水位",
        ["cumu-flow-out"] = "累计制水",
        ["cumu-flow-in"] = "累计进水",
        ["flow-out"] = "本次制水",
        ["flow-in"] = "本次进水",
        ["filter-used-flow"] = "滤芯已用",
        ["filter-left-flow"] = "滤芯剩余",
        ["discharge-water"] = "水流量",
        ["input-water-temp"] = "进水温度",
        ["single-water"] = "单次用水",
        ["single-gas"] = "单次用气",
        ["usage-time"] = "使用时长",
        ["power-consumption"] = "耗电量",
        ["electric-power"] = "功率",
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
