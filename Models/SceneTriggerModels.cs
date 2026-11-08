using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MiHome.Models;

/// <summary>触发条件的两个来源：事件上报、属性变化。</summary>
public enum TriggerAxis
{
    /// <summary>设备事件（门锁开锁、门铃响、按键单击…）。</summary>
    Event,

    /// <summary>设备属性（开关、异常状态、温度阈值…）。</summary>
    Property
}

/// <summary>
/// 一个可选条件（下拉里的第二级）。事件带出参（可做筛选），属性带取值域。
/// 由 <see cref="Services.SceneTriggerCatalog"/> 按 MIoT 规格生成。
/// </summary>
public sealed class TriggerOption
{
    /// <summary>写入触发源的 key：event.&lt;siid&gt;.&lt;eiid&gt; 或 prop.&lt;siid&gt;.&lt;piid&gt;。</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    /// <summary>「事件 · 门锁」这类次级说明。</summary>
    public required string SubTitle { get; init; }

    public required TriggerAxis Axis { get; init; }

    public int Siid { get; init; }

    /// <summary>事件为 eiid，属性为 piid。</summary>
    public int RefId { get; init; }

    public SpecPropertyDef? Prop { get; init; }
    public SpecEventDef? Event { get; init; }

    /// <summary>事件出参里可筛选的那些（枚举 / 布尔），属性类为空。</summary>
    public List<TriggerArgSpec> Args { get; init; } = new();
}

/// <summary>一个可筛选的事件出参。</summary>
public sealed class TriggerArgSpec
{
    public int Piid { get; init; }
    public required string Name { get; init; }
    public List<SpecOptionDef> Options { get; init; } = new();
    public bool IsBool { get; init; }
}

/// <summary>
/// 编辑器里的一行筛选值：一排出片（任意 / 各枚举值），选中的那片决定写进 value_json 的值。
/// chip 自带 Select 命令，模板里直接 Command="{Binding SelectCommand}" 即可，不用绕父级绑定。
/// </summary>
public sealed partial class TriggerValueChip : ObservableObject
{
    public required string Label { get; init; }

    /// <summary>写入 value_json 的值；null 表示「任意」，该参数不参与筛选。</summary>
    public required object? Value { get; init; }

    public required TriggerArgGroup Owner { get; init; }

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select() => Owner.Pick(this);
}

/// <summary>事件的一个出参（如「操作位置」「锁动作」）在当前条件里的取值。</summary>
public sealed partial class TriggerArgGroup : ObservableObject
{
    public int Piid { get; init; }
    public required string Name { get; init; }

    public ObservableCollection<TriggerValueChip> Chips { get; } = new();

    [ObservableProperty] private TriggerValueChip? _selected;

    /// <summary>
    /// 筛选片默认收起：一行显示「参数名 · 当前值」，点开才铺开取值片。
    /// 锁事件一次带三个出参，全展开会有二十多个胶囊，收起后一屏能看完。
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>数值型出参（时间戳之类）不做筛选，固定「任意」。</summary>
    public bool IsNumericOnly { get; init; }

    public object? Value => Selected?.Value;

    public string Summary => Selected is { Value: not null } ? Selected.Label : "任意";

    /// <summary>是否已选了具体值（收起时用高亮提示「这条已经筛过了」）。</summary>
    public bool HasPick => Selected is { Value: not null };

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    public void Pick(TriggerValueChip chip)
    {
        foreach (var c in Chips) c.IsSelected = ReferenceEquals(c, chip);
        Selected = chip;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasPick));
    }

    /// <summary>默认落在第一片（「任意」）上。</summary>
    public void ResetToAny()
    {
        if (Chips.Count > 0) Pick(Chips[0]);
    }

    /// <summary>构造后按值回填（编辑已有条件时用）。</summary>
    public void Apply(object? value)
    {
        var hit = Chips.FirstOrDefault(c => Equals(c.Value, value));
        Pick(hit ?? (Chips.Count > 0 ? Chips[0] : null!));
    }

    public static TriggerArgGroup Create(TriggerArgSpec spec, object? current)
    {
        var group = new TriggerArgGroup { Piid = spec.Piid, Name = spec.Name, IsNumericOnly = spec.Options.Count == 0 && !spec.IsBool };
        group.Chips.Add(new TriggerValueChip { Label = "任意", Value = null, Owner = group });
        if (spec.IsBool)
        {
            group.Chips.Add(new TriggerValueChip { Label = "是", Value = true, Owner = group });
            group.Chips.Add(new TriggerValueChip { Label = "否", Value = false, Owner = group });
        }
        else
        {
            foreach (var o in spec.Options)
                group.Chips.Add(new TriggerValueChip { Label = o.Label, Value = o.Value, Owner = group });
        }
        group.Apply(current);
        // 编辑已有条件时，已经筛过的那组自动展开，其余保持收起
        group.IsExpanded = group.HasPick;
        return group;
    }
}

/// <summary>触发条件列表里的一行（已有条件）。</summary>
public sealed class SceneTriggerRow
{
    public int Index { get; init; }

    /// <summary>主行：设备名 + 条件名。</summary>
    public required string Title { get; init; }

    /// <summary>次行：筛选摘要，或「定时 / 手动」这类不可编辑说明。</summary>
    public required string Subtitle { get; init; }

    /// <summary>是否可以在本应用里改（定时、手动触发只读）。</summary>
    public required bool Editable { get; init; }

    /// <summary>行首胶囊文案：事件 / 状态 / 定时 / 手动。</summary>
    public required string KindLabel { get; init; }

    public required JsonObject Node { get; init; }
}
