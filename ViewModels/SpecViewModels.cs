using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MiHome.Models;

namespace MiHome.ViewModels;

/// <summary>通用属性面板的一组（按 MIoT 服务分组），渲染成抽屉里的一张卡片。</summary>
public sealed class SpecGroupVm
{
    public required string Title { get; init; }
    public required IReadOnlyList<SpecPropVm> Items { get; init; }
}

/// <summary>规格属性的可编辑/只读行。控件形态由 <see cref="SpecPropertyDef.Kind"/> 决定。</summary>
public sealed partial class SpecPropVm : ObservableObject
{
    public required MijiaDevice Device { get; init; }
    public required SpecPropertyDef Def { get; init; }

    public ObservableCollection<SpecOptionVm> Options { get; } = new();

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private double _number;
    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>下拉选中值，双绑 OptionPicker；变更即下发，回填/失败回滚走 <see cref="SetPickedQuiet"/>。</summary>
    [ObservableProperty] private object? _pickedValue;

    partial void OnPickedValueChanged(object? value)
    {
        if (_loading || value is not int v) return;
        ChoiceCommit?.Invoke(this, v);
    }

    /// <summary>滑杆拖动后的防抖提交，由 MainViewModel 注入。</summary>
    public Action<SpecPropVm>? SliderCommit { get; set; }

    /// <summary>下拉选中后的即时提交，由 MainViewModel 注入，与分段按钮同一条 SetSpecChoice 链路。</summary>
    public Action<SpecPropVm, int>? ChoiceCommit { get; set; }

    private bool _loading;

    public string Title => Def.Desc;
    public string Unit => Def.Unit;

    public bool IsToggle => Def.Writable && Def.Kind == SpecControlKind.Toggle;
    public bool IsSlider => Def.Writable && Def.Kind == SpecControlKind.Slider;
    public bool IsChoice => Def.Writable && Def.Kind == SpecControlKind.Choice;
    public bool IsValue => !IsToggle && !IsSlider && !IsChoice;

    /// <summary>选项超过 4 个时改用下拉菜单，避免分段按钮过多。</summary>
    public bool IsLargeChoice => IsChoice && Options.Count > 4;

    public bool IsCompactChoice => IsChoice && Options.Count <= 4;

    public double Min => Def.Min;
    public double Max => Def.Max;
    public double Step => Def.Step;

    public string SliderText
    {
        get
        {
            var formatted = Format(Number);
            // 对于过大的数值（可能是编码值或 bit field），不显示具体数字
            if (Number > 1000000 && string.IsNullOrEmpty(Def.Unit))
                return "已设置";
            return formatted + Def.Unit;
        }
    }

    partial void OnNumberChanged(double value)
    {
        OnPropertyChanged(nameof(SliderText));
        if (_loading) return;
        SliderCommit?.Invoke(this);
    }

    /// <summary>选中下拉项后同步数值，但不触发滑杆提交。</summary>
    public void SetNumberQuiet(double value)
    {
        _loading = true;
        try
        {
            Number = value;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>回填/回滚下拉选中值，不触发下发。</summary>
    public void SetPickedQuiet(object? value)
    {
        _loading = true;
        try
        {
            PickedValue = value;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>把轮询/回填的原始值映射到对应控件状态。</summary>
    public void Load(object? raw)
    {
        _loading = true;
        try
        {
            switch (Def.Kind)
            {
                case SpecControlKind.Toggle:
                    IsOn = raw is true;
                    break;
                case SpecControlKind.Slider:
                case SpecControlKind.Choice:
                    Number = ToDouble(raw);
                    foreach (var o in Options) o.IsSelected = (int)Math.Round(Number) == o.Value;
                    if (Def.Kind == SpecControlKind.Choice) PickedValue = (int)Math.Round(Number);
                    break;
            }
            ValueText = FormatRaw(raw);
        }
        finally
        {
            _loading = false;
        }
    }

    public string FormatRaw(object? raw)
    {
        if (raw == null) return "—";
        if (raw is bool b) return b ? "是" : "否";
        if (Def.Options.Count > 0)
        {
            var v = (int)Math.Round(ToDouble(raw));
            var hit = Def.Options.FirstOrDefault(o => o.Value == v);
            return hit?.Label ?? v.ToString(CultureInfo.InvariantCulture);
        }
        if (raw is string s) return s;
        return Format(ToDouble(raw)) + Def.Unit;
    }

    private static double ToDouble(object? raw) => raw switch
    {
        double d => d,
        int i => i,
        long l => l,
        string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
        _ => 0
    };

    private static string Format(double v)
    {
        var rounded = Math.Round(v, 1);
        return rounded == Math.Truncate(rounded)
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.#", CultureInfo.InvariantCulture);
    }
}

/// <summary>通用下拉里的一个选项（数值型 value-list）。</summary>
public sealed partial class SpecOptionVm : ObservableObject, IPickOption
{
    public required SpecPropVm Prop { get; init; }
    public required SpecOptionDef Def { get; init; }

    [ObservableProperty] private bool _isSelected;

    public string Label => Def.Label;
    public int Value => Def.Value;
    public string? Meta => null;

    // IPickOption 要求 object；保持对外仍是 int，避免既有调用点被装箱语义带偏
    object IPickOption.Value => Value;
}

/// <summary>规格里无入参的动作；执行前需二次确认，避免误触（扫地机回充、净化器复位等）。</summary>
public sealed partial class SpecActionVm : ObservableObject
{
    public required MijiaDevice Device { get; init; }
    public required SpecActionDef Def { get; init; }

    /// <summary>同名动作（多路面板每个服务各有一个）用服务名区分。</summary>
    public string? TitleOverride { get; init; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConfirming;

    public string Title => TitleOverride ?? Def.Desc;

    partial void OnIsConfirmingChanged(bool value) => OnPropertyChanged(nameof(ButtonText));

    public string ButtonText => IsConfirming ? "确认执行" : "执行";
}
