using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiHome.Models;

/// <summary>
/// 触发条件下拉里的一行：一条触发源或一条状态条件。
/// 米家的触发结构本应用只读，这里只负责把「全部条件」摊开给人看
/// （卡片上那一行摘要最多只说两条，剩下的会被「等 N 个条件」吃掉）。
/// </summary>
public sealed class SceneConditionItem
{
    /// <summary>「触发」或「状态」，显示成行首的小胶囊。</summary>
    public required string Kind { get; init; }

    public required string Text { get; init; }
}

/// <summary>
/// 编辑面板里的一行：场景动作中对某台设备的某个属性做的一次写入。
/// 直接持有原始 JSON 节点（<see cref="Node"/>），改动就地写回，
/// 保存时把整条场景对象原样回传给 AppSceneService/Edit。
/// </summary>
public sealed partial class SceneActionEditItem : ObservableObject
{
    /// <summary>原始报文里 {did, siid, piid, value} 那个节点。</summary>
    public required JsonObject Node { get; init; }

    public required string DeviceName { get; init; }
    public required string PropName { get; init; }
    public string Unit { get; init; } = "";

    public bool IsBool { get; private init; }
    public bool IsNumber { get; private init; }
    public bool IsOption { get; private init; }
    public bool IsText => !IsBool && !IsNumber && !IsOption;

    /// <summary>数值控件量程（规格没给时按 0–100 兜底）。</summary>
    public double Min { get; private init; }
    public double Max { get; private init; } = 100;
    public double Step { get; private init; } = 1;

    public List<string> OptionNames { get; } = new();
    public List<long> OptionValues { get; } = new();

    /// <summary>枚举属性的下拉候选：走程序内自绘的 OptionPicker（原生 ComboBox 滚一下就会误选）。</summary>
    public List<OptionItem> OptionItems { get; } = new();

    [ObservableProperty] private bool _boolValue;
    [ObservableProperty] private double _numValue;
    [ObservableProperty] private string _textValue = "";
    [ObservableProperty] private int _optionIndex = -1;

    /// <summary>初始赋值期间不写回节点，避免构造时就触发 Apply。</summary>
    private bool _ready;

    public string Title => string.IsNullOrEmpty(Unit) ? PropName : $"{PropName}（{Unit}）";
    public string DeviceText => DeviceName;

    partial void OnBoolValueChanged(bool value) => Apply(() => Node["value"] = JsonValue.Create(value));
    partial void OnNumValueChanged(double value) => Apply(() => Node["value"] = JsonValue.Create(Normalize(value)));
    partial void OnTextValueChanged(string value) => Apply(() => Node["value"] = JsonValue.Create(value));

    partial void OnOptionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(OptionValue));
        Apply(() =>
        {
            if (value < 0 || value >= OptionValues.Count) return;
            Node["value"] = JsonValue.Create(OptionValues[value]);
        });
    }

    /// <summary>
    /// OptionPicker 的选中值（与 <see cref="OptionIndex"/> 双向镜像）：
    /// 选中的枚举值本身，浮层里点一下就是它，不需要在外面再记一层下标。
    /// </summary>
    public long? OptionValue
    {
        get => OptionIndex >= 0 && OptionIndex < OptionValues.Count ? OptionValues[OptionIndex] : null;
        set
        {
            var index = value is { } v ? OptionValues.IndexOf(v) : -1;
            if (index >= 0) OptionIndex = index;
        }
    }

    private void Apply(Action write)
    {
        if (!_ready) return;
        try { write(); } catch { }
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ActionText));
    }

    /// <summary>整数量程保持整数，避免 26 变成 26.0 被设备拒绝。</summary>
    private object Normalize(double v)
    {
        var rounded = Math.Round(v);
        return Step >= 1 && Math.Abs(v - rounded) < 0.0001 ? (long)rounded : Math.Round(v, 2);
    }

    /// <summary>一行值的可读描述，用于面板上的实时回显。</summary>
    public string Summary => IsBool
        ? (BoolValue ? "开" : "关")
        : IsOption
            ? (OptionIndex >= 0 && OptionIndex < OptionNames.Count ? OptionNames[OptionIndex] : "未选择")
            : IsNumber
                ? NumValue.ToString("0.##", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit)
                : TextValue;

    /// <summary>
    /// 把这一步说成「调整到什么状态」：开关类只给动词（打开 / 关闭），
    /// 不再把属性名「开关」这种标识文字摆到界面上；数值类写成「亮度 50%」这种一眼能读懂的状态。
    /// </summary>
    public string ActionText
    {
        get
        {
            if (IsBool) return BoolValue ? "打开" : "关闭";
            if (IsOption) return Summary;
            if (IsNumber)
            {
                var v = NumValue.ToString("0.##", CultureInfo.InvariantCulture);
                var text = string.IsNullOrEmpty(Unit) ? v : v + Unit;
                return string.IsNullOrWhiteSpace(PropName) ? text : $"{PropName} {text}";
            }
            return Summary;
        }
    }

    /// <summary>
    /// 由原始写节点构造。规格可用时按规格决定控件类型，否则退回 JSON 值本身的类型。
    /// </summary>
    public static SceneActionEditItem Create(JsonObject node, string deviceName, SpecPropertyDef? def)
    {
        var value = node["value"];
        var kind = value?.GetValueKind() ?? JsonValueKind.Null;

        var isBool = def != null ? def.IsBool : kind is JsonValueKind.True or JsonValueKind.False;
        var isOption = !isBool && def is { Options.Count: > 0 };
        var isNumber = !isBool && !isOption &&
                       (def != null
                           ? def.HasRange || IsNumericFormat(def.Format)
                           : kind == JsonValueKind.Number);

        var item = new SceneActionEditItem
        {
            Node = node,
            DeviceName = deviceName,
            PropName = def?.Desc ?? $"属性 {node["siid"]}.{node["piid"]}",
            Unit = def?.Unit ?? "",
            IsBool = isBool,
            IsNumber = isNumber,
            IsOption = isOption,
            Min = def is { HasRange: true } ? def.Min : 0,
            Max = def is { HasRange: true } ? def.Max : 100,
            Step = def is { HasRange: true, Step: > 0 } ? def.Step : 1
        };

        if (isOption && def != null)
        {
            foreach (var o in def.Options)
            {
                var label = string.IsNullOrWhiteSpace(o.Label) ? o.Value.ToString() : o.Label;
                item.OptionNames.Add(label);
                item.OptionValues.Add(o.Value);
                // Value 统一成 long：OptionPicker 用 Equals 比对选中项，规格给的是 int，
                // 类型不一致会导致「永远是未选中 → 触发器显示『选择』」。
                item.OptionItems.Add(new OptionItem { Label = label, Value = (long)o.Value });
            }
        }

        // 回填当前值（_ready 仍为 false，赋值不会写回节点）
        if (isBool)
        {
            item.BoolValue = TryBool(value, out var b) && b;
        }
        else if (isNumber)
        {
            item.NumValue = TryNum(value, out var d) ? d : item.Min;
        }
        else if (isOption)
        {
            var current = TryLong(value, out var l) ? l : long.MinValue;
            item.OptionIndex = item.OptionValues.IndexOf(current);
        }
        else
        {
            item.TextValue = AsString(value) ?? value?.ToJsonString() ?? "";
        }

        item._ready = true;
        return item;
    }

    private static bool TryBool(JsonNode? n, out bool value)
    {
        value = false;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out value)) return true;
        return false;
    }

    private static bool TryNum(JsonNode? n, out double value)
    {
        value = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<double>(out value)) return true;
        if (v.TryGetValue<long>(out var l)) { value = l; return true; }
        if (v.TryGetValue<string>(out var s) &&
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return true;
        return false;
    }

    private static bool TryLong(JsonNode? n, out long value)
    {
        value = 0;
        if (TryNum(n, out var d)) { value = (long)Math.Round(d); return true; }
        return false;
    }

    private static string? AsString(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool IsNumericFormat(string format) =>
        format.StartsWith("int", StringComparison.OrdinalIgnoreCase)
        || format.StartsWith("uint", StringComparison.OrdinalIgnoreCase)
        || format.Equals("float", StringComparison.OrdinalIgnoreCase);
}
