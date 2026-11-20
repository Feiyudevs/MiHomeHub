// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;
using Avalonia.Media.Imaging;

namespace MiHome.ViewModels;

/// <summary>步骤卡片上的一个动作胶囊（打开 / 关闭 / 切换 / 执行）。</summary>
public sealed partial class HotkeyActionOptionVm : ObservableObject
{
    public required HotkeyActionKind Kind { get; init; }
    public required string Label { get; init; }

    /// <summary>所属步骤：动作胶囊直接绑定它的 <c>ChooseCommand</c>，免得在模板里往上找父级。</summary>
    public HotkeyDraftStepVm? Owner { get; init; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 编辑卡里的一张「控制卡片」：一个目标 + 对它做的动作。
/// 列表顺序就是按下组合键后的执行顺序，上下箭头调整，垃圾桶移除。
/// 目标类型决定可选动作：设备开/关/切换、场景执行、自动化开启/关闭/切换。
/// </summary>
public sealed partial class HotkeyDraftStepVm : ObservableObject
{
    public HotkeyTargetType Type { get; init; } = HotkeyTargetType.Device;
    public string Id { get; init; } = "";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private HotkeyActionKind _kind;

    /// <summary>副标题：设备的房间 + 当前开关态 / 场景类型 / 自动化启停态。</summary>
    [ObservableProperty] private string _detail = "";

    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;

    /// <summary>拖拽排序进行中：源卡片淡成虚影。</summary>
    [ObservableProperty] private bool _isDragging;

    /// <summary>真机产品图（与设备卡同一张），取不到时退回类型线性图标。</summary>
    [ObservableProperty] private Bitmap? _deviceIcon;

    public bool HasDeviceIcon => DeviceIcon != null;

    partial void OnDeviceIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasDeviceIcon));

    public ObservableCollection<HotkeyActionOptionVm> Actions { get; } = new();

    public string TypeText => Type switch
    {
        HotkeyTargetType.Scene => "场景",
        HotkeyTargetType.Automation => "自动化",
        _ => "设备",
    };

    public bool IsDevice => Type == HotkeyTargetType.Device;
    public bool IsScene => Type == HotkeyTargetType.Scene;
    public bool IsAutomation => Type == HotkeyTargetType.Automation;

    /// <summary>由 MainViewModel 挂上：列表顺序调整与删除要它来做。</summary>
    public Action<HotkeyDraftStepVm>? RemoveRequested { get; set; }
    public Action<HotkeyDraftStepVm>? MoveUpRequested { get; set; }
    public Action<HotkeyDraftStepVm>? MoveDownRequested { get; set; }

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this);

    [RelayCommand]
    private void MoveUp() => MoveUpRequested?.Invoke(this);

    [RelayCommand]
    private void MoveDown() => MoveDownRequested?.Invoke(this);

    /// <summary>点动作胶囊：同一张卡片只留一个选中。</summary>
    [RelayCommand]
    private void Choose(HotkeyActionOptionVm? option)
    {
        if (option == null) return;
        Kind = option.Kind;
        foreach (var a in Actions) a.IsSelected = ReferenceEquals(a, option);
    }

    /// <summary>可选动作清单：设备三种、场景只有执行、自动化三种。</summary>
    public static IReadOnlyList<(HotkeyActionKind Kind, string Label)> OptionsFor(HotkeyTargetType type) => type switch
    {
        HotkeyTargetType.Scene => new[] { (HotkeyActionKind.RunScene, "执行") },
        HotkeyTargetType.Automation => new[]
        {
            (HotkeyActionKind.AutomationOn, "开启"),
            (HotkeyActionKind.AutomationOff, "关闭"),
            (HotkeyActionKind.ToggleAutomation, "切换"),
        },
        _ => new[]
        {
            (HotkeyActionKind.ToggleDevice, "切换"),
            (HotkeyActionKind.DeviceOn, "打开"),
            (HotkeyActionKind.DeviceOff, "关闭"),
        },
    };

    public static HotkeyDraftStepVm Create(HotkeyTargetType type, string id, string name, HotkeyActionKind? kind = null)
    {
        var step = new HotkeyDraftStepVm { Type = type, Id = id, Name = name };
        var options = OptionsFor(type);

        foreach (var o in options)
            step.Actions.Add(new HotkeyActionOptionVm { Kind = o.Kind, Label = o.Label, Owner = step });

        var chosen = kind ?? options[0].Kind;
        if (options.All(o => o.Kind != chosen)) chosen = options[0].Kind;

        step.Kind = chosen;
        foreach (var a in step.Actions) a.IsSelected = a.Kind == chosen;
        return step;
    }
}
