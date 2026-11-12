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

    /// <summary>
    /// 不是直接写在本设备 did 上，而是通过它所属的灯组 / 组内成员间接命中。
    /// 米家把几盏灯打包成灯组后，场景里只会写 group.xxx，单灯自己的 did 不会出现。
    /// </summary>
    public bool ViaGroup { get; set; }

    public string RoleText => (ViaGroup ? "灯组·" : "") + (Role switch
    {
        SceneRole.Trigger => "触发",
        SceneRole.Action => "控制",
        _ => "触发 · 控制"
    });
}

/// <summary>
/// 设备维度的场景索引。米家没有「按设备查场景」的接口，
/// 只能把全量场景的动作与触发条件按 did 反向建表，命中在内存里做。
/// did 要同时按完整值与去拆分后缀的 base 值建索引：协议里同一个设备可能叫
/// 1135008228 或 1135008228.s3，场景里写哪种取决于当时怎么建的。
///
/// 设备组（灯组）额外做一层双向展开：单灯 → 它所属的组 did，组 → 组内成员 did。
/// 这样「被打包进灯组的单灯」也能查到只写了 group.xxx 的场景/自动化，
/// 灯组卡片反过来也能看到建组之前直接写在成员灯上的老场景。
/// </summary>
public sealed class SceneIndexService
{
    private Dictionary<string, List<SceneRef>> _byDid = new(StringComparer.Ordinal);

    /// <summary>成员 did → 它所属的设备组 did（含去 .sN 的 base did）。</summary>
    private Dictionary<string, List<string>> _groupsByMember = new(StringComparer.Ordinal);

    /// <summary>设备组 did → 组内成员 did。</summary>
    private Dictionary<string, List<string>> _membersByGroup = new(StringComparer.Ordinal);

    /// <summary>索引里的场景总数 / 自动化数（UI 文案用）。</summary>
    public int SceneCount { get; private set; }
    public int AutomationCount { get; private set; }

    /// <summary>参与过任意场景的设备数。</summary>
    public int DeviceCount => _byDid.Count;

    /// <summary>
    /// 用当前设备列表重建「组 ↔ 成员」关系。设备列表换代（缓存上屏 / 切家庭 / 刷新合并）后调用。
    /// 整表替换引用而不是原地改，避免与后台的场景重建并发时读到半张表。
    /// </summary>
    public void SetDevices(IReadOnlyList<MijiaDevice> devices)
    {
        var byMember = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var byGroup = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var g in devices)
        {
            var gid = g.Did;
            if (string.IsNullOrWhiteSpace(gid) || !IsGroupDid(gid) || g.GroupMembers.Count == 0) continue;

            var members = new List<string>();
            foreach (var m in g.GroupMembers)
            {
                if (string.IsNullOrWhiteSpace(m)) continue;
                members.Add(m);
                Add(byMember, m, gid);
                var mb = BaseDid(m);
                if (!string.Equals(mb, m, StringComparison.Ordinal)) Add(byMember, mb, gid);
            }
            byGroup[gid] = members;
        }

        _groupsByMember = byMember;
        _membersByGroup = byGroup;
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<string>();
            map[key] = list;
        }
        if (!list.Contains(value, StringComparer.Ordinal)) list.Add(value);
    }

    public void Rebuild(IReadOnlyList<MijiaScene> scenes)
    {
        // 先建新表、最后整体换引用：后台重建期间 UI 线程上的 ForDevice 不会读到 Clear 到一半的字典
        var index = new Dictionary<string, List<SceneRef>>(StringComparer.Ordinal);
        var sceneCount = 0;
        var automationCount = 0;

        foreach (var scene in scenes)
        {
            if (string.IsNullOrEmpty(scene.SceneId)) continue;
            if (scene.IsAutomation) automationCount++;
            else sceneCount++;

            foreach (var t in scene.Triggers)
                Link(index, t.DeviceDid, scene, SceneRole.Trigger);

            foreach (var a in scene.Actions)
            {
                Link(index, a.TargetDid, scene, SceneRole.Action);
                foreach (var w in a.Writes)
                    Link(index, w.Did, scene, SceneRole.Action);
                // 「全屋照明」这类打包控制（type=4）不写设备 did，只在 value.filter_dids 里点名
                foreach (var fd in a.FilterDids)
                    Link(index, fd, scene, SceneRole.Action);
            }
        }

        _byDid = index;
        SceneCount = sceneCount;
        AutomationCount = automationCount;
    }

    private static void Link(Dictionary<string, List<SceneRef>> index, string? did, MijiaScene scene, SceneRole role)
    {
        if (string.IsNullOrWhiteSpace(did)) return;
        foreach (var key in Keys(did))
        {
            if (!index.TryGetValue(key, out var list))
            {
                list = new List<SceneRef>();
                index[key] = list;
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

        var index = _byDid;
        var groupsByMember = _groupsByMember;
        var membersByGroup = _membersByGroup;
        var merged = new Dictionary<string, SceneRef>(StringComparer.Ordinal);

        // 先收直接命中，直接命中优先（ViaGroup 会被覆盖成 false）
        foreach (var key in Keys(did))
            Collect(index, key, viaGroup: false, merged);

        // 再按灯组关系展开：单灯补上所属组，灯组卡片补上组内成员
        foreach (var key in ExpandThroughGroups(did, groupsByMember, membersByGroup))
            Collect(index, key, viaGroup: true, merged);

        return merged.Values
            .OrderByDescending(r => (int)r.Role)
            .ThenBy(r => r.Scene.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static void Collect(Dictionary<string, List<SceneRef>> index, string key,
                                bool viaGroup, Dictionary<string, SceneRef> merged)
    {
        if (!index.TryGetValue(key, out var list)) return;
        foreach (var r in list)
        {
            if (merged.TryGetValue(r.Scene.SceneId, out var exist))
            {
                exist.Role |= r.Role;
                if (!viaGroup) exist.ViaGroup = false;
            }
            else
            {
                merged[r.Scene.SceneId] = new SceneRef
                {
                    Scene = r.Scene,
                    Role = r.Role,
                    ViaGroup = viaGroup
                };
            }
        }
    }

    /// <summary>
    /// 与设备组相关的 did：单灯 → 所属组；灯组 → 组内成员。
    /// 只展开两层（组里还嵌子组时再补一层成员），不做全图遍历，
    /// 免得 A 组的成员又牵出 B 组、把不相干的场景一并挂上来。
    /// </summary>
    private static IEnumerable<string> ExpandThroughGroups(
        string did,
        Dictionary<string, List<string>> groupsByMember,
        Dictionary<string, List<string>> membersByGroup)
    {
        var first = new List<string>();
        foreach (var key in Keys(did))
        {
            if (IsGroupDid(key))
            {
                if (membersByGroup.TryGetValue(key, out var members)) first.AddRange(members);
            }
            else if (groupsByMember.TryGetValue(key, out var groups)) first.AddRange(groups);
        }

        var seen = new HashSet<string>(Keys(did), StringComparer.Ordinal);
        foreach (var k in first)
        {
            if (!seen.Add(k)) continue;
            yield return k;
            if (IsGroupDid(k) && membersByGroup.TryGetValue(k, out var subs))
            {
                foreach (var m in subs)
                    if (seen.Add(m)) yield return m;
            }
        }
    }

    public int CountFor(string? did) => ForDevice(did).Count;

    /// <summary>完整 did + 去掉 ".sN" 拆分后缀的父 did。</summary>
    private static IEnumerable<string> Keys(string did)
    {
        yield return did;
        var dot = did.LastIndexOf(".s", StringComparison.Ordinal);
        if (dot > 0) yield return did[..dot];
    }

    private static string BaseDid(string did)
    {
        var dot = did.LastIndexOf(".s", StringComparison.Ordinal);
        return dot > 0 ? did[..dot] : did;
    }

    private static bool IsGroupDid(string did) =>
        did.StartsWith("group.", StringComparison.OrdinalIgnoreCase);
}
