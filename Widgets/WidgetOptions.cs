using System.Collections.Generic;

namespace MiHomeHub.Widgets;

/// <summary>
/// 组件设置的通用读写约定。
///
/// 所有组件的设置都存成 <c>Dictionary&lt;string,string&gt;</c>（widgets.json 里已有该类型的
/// source-gen 注册，不新增 DTO 就不会漏注册导致裁剪后 JSON 报错）。
/// 缺键 = 用组件自己的默认值，所以老版本存档（没有 Options 字段）天然兼容。
/// </summary>
public static class WidgetOptions
{
    public const string On = "1";
    public const string Off = "0";

    public static bool ReadBool(IReadOnlyDictionary<string, string>? map, string key, bool fallback)
    {
        if (map is null || !map.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw))
            return fallback;
        return raw != Off;
    }

    public static string WriteBool(bool value) => value ? On : Off;
}

