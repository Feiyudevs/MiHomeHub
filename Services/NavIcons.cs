// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Avalonia.Media;

namespace MiHome.Services;

/// <summary>
/// 侧栏导航图标注册表：每个 IconKey 同时备有两套画法 ——
/// Assets/Nav 下的厚涂玻璃 SVG（24 栅格）与 <see cref="Line"/> 里的细线几何，
/// 由设置里的图标模式决定显示哪一套；缺资源的 key 一律回落到 house，不留空位。
/// </summary>
public static class NavIcons
{
    /// <summary>已生成玻璃 SVG 的 key，与 Assets/Nav/&lt;key&gt;.svg 同名。</summary>
    public static readonly HashSet<string> Glass = new()
    {
        "home", "scene", "house", "sofa", "bed", "pot", "dining", "shower", "sink", "washer",
        "book", "window", "hanger", "screen", "plant", "door", "box", "cup",
        "tub", "stairs", "dumbbell", "car"
    };

    /// <summary>细线版本：24 栅格、只描边不填充，配合 RoundLineCap 呈现圆润细线。</summary>
    public static readonly Dictionary<string, string> Line = new()
    {
        ["home"] = "M6,3.5 H9 Q11.5,3.5 11.5,6 V9 Q11.5,11.5 9,11.5 H6 Q3.5,11.5 3.5,9 V6 Q3.5,3.5 6,3.5 Z M15,3.5 H18 Q20.5,3.5 20.5,6 V9 Q20.5,11.5 18,11.5 H15 Q12.5,11.5 12.5,9 V6 Q12.5,3.5 15,3.5 Z M6,12.5 H9 Q11.5,12.5 11.5,15 V18 Q11.5,20.5 9,20.5 H6 Q3.5,20.5 3.5,18 V15 Q3.5,12.5 6,12.5 Z M15,12.5 H18 Q20.5,12.5 20.5,15 V18 Q20.5,20.5 18,20.5 H15 Q12.5,20.5 12.5,18 V15 Q12.5,12.5 15,12.5 Z",
        ["scene"] = "M9,5.5 L10.8,11.2 L16.5,13 L10.8,14.8 L9,20.5 L7.2,14.8 L1.5,13 L7.2,11.2 Z M18,2.3 L18.9,4.6 L21.2,5.5 L18.9,6.4 L18,8.7 L17.1,6.4 L14.8,5.5 L17.1,4.6 Z",
        ["house"] = "M4 10.8 L12 4 L20 10.8 V19 A1.6 1.6 0 0 1 18.4 20.6 H5.6 A1.6 1.6 0 0 1 4 19 Z M9.8 20.6 V14.4 A0.9 0.9 0 0 1 10.7 13.5 H13.3 A0.9 0.9 0 0 1 14.2 14.4 V20.6",
        ["sofa"] = "M6.5 12.5 V9.5 A1.5 1.5 0 0 1 8 8 H16 A1.5 1.5 0 0 1 17.5 9.5 V12.5 M3 12.5 A1.5 1.5 0 0 1 4.5 11 H19.5 A1.5 1.5 0 0 1 21 12.5 V16.5 A1.5 1.5 0 0 1 19.5 18 H4.5 A1.5 1.5 0 0 1 3 16.5 Z M9.5 13 V17 M14.5 13 V17 M6 18 V20 M18 18 V20",
        ["bed"] = "M3.5 20 V6.5 M3.5 14 H20.5 V17.5 H3.5 M6 17.5 V20 M18 17.5 V20 M7 14 V11 A1.5 1.5 0 0 1 8.5 9.5 H12 A1.5 1.5 0 0 1 13.5 11 V14",
        ["pot"] = "M4.5 12 H19.5 V15 A4.5 4.5 0 0 1 15 19.5 H9 A4.5 4.5 0 0 1 4.5 15 Z M6.5 9 H17.5 M12 9 V7 M4.5 13.7 H2.8 M19.5 13.7 H21.2 M9.2 5.4 C9.2 4.3 8.3 3.8 8.3 2.8 M13.2 5.4 C13.2 4.3 12.3 3.8 12.3 2.8",
        ["dining"] = "M8.5 3 V7 A2.5 2.5 0 0 0 13.5 7 V3 M11 7 V21 M16.5 21 V3.5 M16.5 3.5 C19.4 4.6 19.9 8.4 16.5 11",
        ["shower"] = "M5.5 8.5 A6.5 6.5 0 0 1 18.5 8.5 Z M8 11.5 V14 M12 11.5 V14 M16 11.5 V14 M10 17 V18.5 M14 17 V18.5",
        ["sink"] = "M3.5 10.5 H20.5 V12.6 A4.4 4.4 0 0 1 16.1 17 H7.9 A4.4 4.4 0 0 1 3.5 12.6 Z M9 17 V20.5 M15 17 V20.5 M12 10.5 V6 A2 2 0 0 1 16 6 V7.5",
        ["door"] = "M6 20.5 V5 A1.5 1.5 0 0 1 7.5 3.5 H16.5 A1.5 1.5 0 0 1 18 5 V20.5 M4 20.5 H20 M14.6 12 V12.1",
        ["window"] = "M4.5 4 H19.5 A1.5 1.5 0 0 1 21 5.5 V18.5 A1.5 1.5 0 0 1 19.5 20 H4.5 A1.5 1.5 0 0 1 3 18.5 V5.5 A1.5 1.5 0 0 1 4.5 4 Z M3 12 H21 M12 4 V20",
        ["stairs"] = "M4 20 H8 V16 H12 V12 H16 V8 H20",
        ["hanger"] = "M12 7.4 V5.6 C12 4.2 13.1 3.6 14 4.4 M12 7.4 L4 14 A1.6 1.6 0 0 0 5 17 H19 A1.6 1.6 0 0 0 20 14 L12 7.4",
        ["box"] = "M3.8 8.2 L12 4 L20.2 8.2 V15.8 L12 20 L3.8 15.8 Z M3.8 8.2 L12 12.4 L20.2 8.2 M12 12.4 V20",
        ["washer"] = "M5.5 3.2 H18.5 A1.6 1.6 0 0 1 20.1 4.8 V19.2 A1.6 1.6 0 0 1 18.5 20.8 H5.5 A1.6 1.6 0 0 1 3.9 19.2 V4.8 A1.6 1.6 0 0 1 5.5 3.2 Z M3.9 7.2 H20.1 M12 10.2 A4 4 0 0 1 12 18.2 A4 4 0 0 1 12 10.2 M7 5.2 H10.4 M17.2 5.2 V5.21",
        ["book"] = "M4.2 3.4 H19.8 A1.8 1.8 0 0 1 21.6 5.2 V18.8 A1.8 1.8 0 0 1 19.8 20.6 H4.2 A1.8 1.8 0 0 1 2.4 18.8 V5.2 A1.8 1.8 0 0 1 4.2 3.4 Z M9.2 3.4 V20.6 M12.6 8.8 H17.6 M12.6 12.6 H17.6",
        ["screen"] = "M4 4.5 H20 A1.5 1.5 0 0 1 21.5 6 V15 A1.5 1.5 0 0 1 20 16.5 H4 A1.5 1.5 0 0 1 2.5 15 V6 A1.5 1.5 0 0 1 4 4.5 Z M9 20.5 H15 M12 16.5 V20.5",
        ["dumbbell"] = "M3.5 8 V16 M6.5 5 V19 M17.5 5 V19 M20.5 8 V16 M6.5 12 H17.5",
        ["car"] = "M3.5 16 V11.5 L6.5 6 H17.5 L20.5 11.5 V16 M3.5 16 H20.5 M7 16 V18.5 M17 16 V18.5 M3.5 11.5 H20.5",
        ["plant"] = "M12 21 V12.5 M12 12.5 C12 8.6 9.3 6 5 5.6 C5 9.6 8.2 12 12 12.5 Z M12 12.5 C12 9.3 14.2 6.9 18.7 6.4 C18.7 10.4 15.8 12.4 12 12.5 Z M8.8 17.5 H15.2 L14.6 21 H9.4 Z",
        ["cup"] = "M4.5 8.5 H15 V13 A4.5 4.5 0 0 1 10.5 17.5 H9 A4.5 4.5 0 0 1 4.5 13 Z M15 10 H17.2 A2.3 2.3 0 0 1 17.2 14.6 H15 M5.5 20 H15.5 M7.5 6 C7.5 5 8.5 4.6 8.5 3.6 M11.5 6 C11.5 5 12.5 4.6 12.5 3.6",
        ["tub"] = "M3.5 12.5 H20.5 V15.3 A4.6 4.6 0 0 1 15.9 19.9 H8.1 A4.6 4.6 0 0 1 3.5 15.3 Z M6.5 12.5 V7.2 A2.3 2.3 0 0 1 11.1 7.2 V11.5"
    };

    private const string Fallback = "house";

    /// <summary>该 key 是否有玻璃资源可用。</summary>
    public static bool HasGlass(string? key) => key is not null && Glass.Contains(key);

    /// <summary>玻璃 SVG 的 avares 绝对地址；未出图的 key 回落到 house，玻璃模式不留空位。</summary>
    public static string SourceFor(string? key) =>
        $"avares://MiHome/Assets/Nav/{(HasGlass(key) ? key : Fallback)}.svg";

    private static readonly ConcurrentDictionary<string, Geometry> LineCache = new();

    /// <summary>细线几何：每个 key 都给得出，未收录的回落到 house；显示与否由图标模式决定。</summary>
    public static Geometry LineFor(string? key)
    {
        var k = key is not null && Line.ContainsKey(key) ? key : Fallback;
        return LineCache.GetOrAdd(k, x => Geometry.Parse(Line[x]));
    }

    /// <summary>标题栏账户胶囊复用侧栏的 house 细线几何，避免两处画法走样。</summary>
    public static Geometry HouseLine => LineFor("house");
}
