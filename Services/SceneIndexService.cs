using System;
using System.Collections.Generic;
using System.Linq;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>设备在某条场景里的角色。</summary>
[Flags]
public enum SceneRole
{
    None = 0,
    /// <summary>该设备的变化会触发这条场景（自动化才可能）。</summary>
    Trigger = 1,
    /// <summary>这条场景会控制该设备。</summary>
    Action = 2
}

/// <summary>一条「设备 → 场景」的引用。</summary>
public sealed class SceneRef
{
    public required MijiaScene Scene { get; init; }
    public SceneRole Role { get; set; }
    public string RoleText => Role switch
    {
        SceneRole.Trigger => "触发",
        SceneRole.Action => "控制",
        _ => "触发 · 控制"
    };
}

/// <summary>
/// 设备维度的场景索引。米家没有「按设备查场景」的接口，
/// 只能把全量场景的动作与触发条件按 did 反向建表，命中在内存里做。
/// did 要同时按完整值与去拆分后缀的 base 值建索引：协议里同一个设备可能叫
/// 1135008228 或 1135008228.s3，场景里写哪种取决于当时怎么建的。
/// </summary>
public sealed class SceneIndexService
{
    private readonly Dictionary<string, List<SceneRef>> _byDid = new(StringComparer.Ordinal);

    /// <summary>索引里的场景总数 / 自动化数（UI 文案用）。</summary>
    public int SceneCount { get; private set; }
    public int AutomationCount { get; private set; }

    /// <summary>参与过任意场景的设备数。</summary>
    public int DeviceCount => _byDid.Count;

    public void Rebuild(IReadOnlyList<MijiaScene> scenes)
    {
        _byDid.Clear();
        SceneCount = 0;
        AutomationCount = 0;

        foreach (var scene in scenes)
        {
            if (string.IsNullOrEmpty(scene.SceneId)) continue;
            if (scene.IsAutomation) AutomationCount++;
            else SceneCount++;

            foreach (var t in scene.Triggers)
                Link(t.DeviceDid, scene, SceneRole.Trigger);

            foreach (var a in scene.Actions)
            {
                Link(a.TargetDid, scene, SceneRole.Action);
                foreach (var w in a.Writes)
                    Link(w.Did, scene, SceneRole.Action);
            }
        }
    }

    private void Link(string? did, MijiaScene scene, SceneRole role)
    {
        if (string.IsNullOrWhiteSpace(did)) return;
        foreach (var key in Keys(did))
        {
            if (!_byDid.TryGetValue(key, out var list))
            {
                list = new List<SceneRef>();
                _byDid[key] = list;
            }

            var existing = list.FirstOrDefault(r => r.Scene.SceneId == scene.SceneId);
            if (existing != null) existing.Role |= role;
            else list.Add(new SceneRef { Scene = scene, Role = role });
        }
    }

    /// <summary>某设备相关的全部场景：既是触发源又是控制对象的排前面。</summary>
    public IReadOnlyList<SceneRef> ForDevice(string? did)
    {
        if (string.IsNullOrWhiteSpace(did)) return Array.Empty<SceneRef>();

        var merged = new Dictionary<string, SceneRef>(StringComparer.Ordinal);
        foreach (var key in Keys(did))
        {
            if (!_byDid.TryGetValue(key, out var list)) continue;
            foreach (var r in list)
            {
                if (merged.TryGetValue(r.Scene.SceneId, out var exist))
                    exist.Role |= r.Role;
                else
                    merged[r.Scene.SceneId] = new SceneRef { Scene = r.Scene, Role = r.Role };
            }
        }

        return merged.Values
            .OrderByDescending(r => (int)r.Role)
            .ThenBy(r => r.Scene.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public int CountFor(string? did) => ForDevice(did).Count;

    /// <summary>完整 did + 去掉 ".sN" 拆分后缀的父 did。</summary>
    private static IEnumerable<string> Keys(string did)
    {
        yield return did;
        var dot = did.LastIndexOf(".s", StringComparison.Ordinal);
        if (dot > 0) yield return did[..dot];
    }
}
