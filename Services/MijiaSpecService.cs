using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MijiaPanel.Models;

namespace MijiaPanel.Services;

/// <summary>
/// 从公开 MIoT 规格站(miot-spec.org)按设备 spec_type 拉取属性定义，解析出空调控制参数。
/// 空调各型号属性布局差异大（温度 piid、模式取值、风速服务各不相同），必须按规格驱动。
/// 按 spec_type 缓存，避免每次开抽屉重复请求。
/// </summary>
public static class MijiaSpecService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, AcControlSchema> Cache = new();
    private static readonly Dictionary<string, SwitchControlSchema> SwitchCache = new();

    /// <summary>
    /// 从 MIoT services JSON 解析可写 on 通道。
    /// 只认 switch / outlet 服务，跳过 indicator-light 等同名 on，避免把指示灯当成开关路。
    /// </summary>
    public static SwitchControlSchema ParseSwitchChannels(JsonElement services)
    {
        var schema = new SwitchControlSchema();
        if (services.ValueKind != JsonValueKind.Array) return schema;

        foreach (var s in services.EnumerateArray())
        {
            var type = s.TryGetProperty("type", out var st) ? st.GetString() ?? "" : "";
            if (!IsSwitchLikeService(type)) continue;
            if (!s.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Array)
                continue;
            var siid = s.GetProperty("iid").GetInt32();
            if (siid < 2) continue; // siid=1 是 device-information，无开关

            foreach (var p in props.EnumerateArray())
            {
                var ptype = p.GetProperty("type").GetString() ?? "";
                if (!ptype.Contains(":property:on:", StringComparison.Ordinal)) continue;
                if (p.TryGetProperty("format", out var fmt) && fmt.GetString() != "bool") continue;
                if (!IsWritable(p)) continue;

                var piid = p.GetProperty("iid").GetInt32();
                var label = p.TryGetProperty("description", out var pd) && !string.IsNullOrWhiteSpace(pd.GetString())
                    ? pd.GetString()!
                    : "";
                schema.Channels.Add(new SwitchChannelDef { Siid = siid, Piid = piid, Label = label });
            }
        }
        return schema;
    }

    /// <summary>switch / outlet 算可控开关；indicator-light、battery 等的 on 不算路。</summary>
    private static bool IsSwitchLikeService(string type) =>
        type.Contains(":service:switch:", StringComparison.Ordinal) ||
        type.Contains(":service:outlet:", StringComparison.Ordinal);

    private static bool IsWritable(JsonElement prop)
    {
        if (!prop.TryGetProperty("access", out var acc) || acc.ValueKind != JsonValueKind.Array)
            return true; // 无 access 字段时按可写试，真机拒了再靠下发回退
        foreach (var a in acc.EnumerateArray())
            if (a.GetString() == "write") return true;
        return false;
    }

    /// <summary>按公开类型模板拉开关通道（设备级 getspec 失败时的回退）。</summary>
    public static async Task<SwitchControlSchema?> GetSwitchSchemaAsync(string specType)
    {
        if (string.IsNullOrEmpty(specType)) return null;
        lock (SwitchCache)
        {
            if (SwitchCache.TryGetValue(specType, out var hit)) return hit;
        }

        try
        {
            var url = "http://miot-spec.org/miot-spec-v2/instance?type=" + Uri.EscapeDataString(specType);
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var schema = ParseSwitchChannels(doc.RootElement.GetProperty("services"));
            lock (SwitchCache) SwitchCache[specType] = schema;
            return schema;
        }
        catch
        {
            // 拉规格失败时不缓存，下次打开抽屉再试；上层按「未知=不显示开关」处理
            return null;
        }
    }

    public static async Task<AcControlSchema?> GetAcSchemaAsync(string specType)
    {
        if (string.IsNullOrEmpty(specType)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(specType, out var hit)) return hit;
        }

        var schema = new AcControlSchema();
        try
        {
            var url = "http://miot-spec.org/miot-spec-v2/instance?type=" + Uri.EscapeDataString(specType);
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var services = doc.RootElement.GetProperty("services");

            JsonElement? air = null, fan = null;
            foreach (var s in services.EnumerateArray())
            {
                var t = s.GetProperty("type").GetString() ?? "";
                if (t.Contains(":service:air-conditioner:", StringComparison.Ordinal)) air = s;
                else if (t.Contains(":service:fan-control:", StringComparison.Ordinal)) fan = s;
            }

            if (air is { } airSvc)
            {
                schema.ModeSiid = airSvc.GetProperty("iid").GetInt32();
                schema.TempSiid = airSvc.GetProperty("iid").GetInt32();
                foreach (var p in airSvc.GetProperty("properties").EnumerateArray())
                {
                    var type = p.GetProperty("type").GetString() ?? "";
                    var piid = p.GetProperty("iid").GetInt32();
                    if (type.Contains(":property:mode:", StringComparison.Ordinal))
                    {
                        schema.ModePiid = piid;
                        FillOptions(p, schema.Modes);
                    }
                    else if (type.Contains(":property:target-temperature:", StringComparison.Ordinal))
                    {
                        schema.TempPiid = piid;
                        if (p.TryGetProperty("value-range", out var vr) && vr.ValueKind == JsonValueKind.Array)
                        {
                            var arr = vr.EnumerateArray().ToList();
                            if (arr.Count >= 3)
                            {
                                schema.TempMin = arr[0].GetDouble();
                                schema.TempMax = arr[1].GetDouble();
                                schema.TempStep = arr[2].GetDouble();
                            }
                        }
                    }
                }
            }

            if (fan is { } fanSvc)
            {
                schema.FanSiid = fanSvc.GetProperty("iid").GetInt32();
                foreach (var p in fanSvc.GetProperty("properties").EnumerateArray())
                {
                    var type = p.GetProperty("type").GetString() ?? "";
                    if (type.Contains(":property:fan-level:", StringComparison.Ordinal))
                    {
                        schema.FanPiid = p.GetProperty("iid").GetInt32();
                        FillOptions(p, schema.Fans);
                    }
                }
            }

            // 至少要有目标温度 piid，且模式或风速任一项可用，才视为有效
            schema.Valid = schema.TempPiid > 0 && (schema.ModePiid > 0 || schema.FanPiid > 0);

            lock (Cache) Cache[specType] = schema;
            return schema;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 value-list 为可选段，优先用中文 comment，其次 description。</summary>
    private static void FillOptions(JsonElement prop, List<AcOptionItem> into)
    {
        if (!prop.TryGetProperty("value-list", out var vl) || vl.ValueKind != JsonValueKind.Array) return;
        foreach (var item in vl.EnumerateArray())
        {
            var value = item.GetProperty("value").GetInt32();
            var label = item.TryGetProperty("comment", out var c) && c.GetString() is { Length: > 0 } s ? s
                : item.TryGetProperty("description", out var d) ? d.GetString() ?? $"档{value}"
                : $"档{value}";
            into.Add(new AcOptionItem { Value = value, Label = label });
        }
    }
}
