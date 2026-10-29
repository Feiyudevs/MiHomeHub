using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiHome.Models;

/// <summary>规格里解析出的一路可写 on（有线墙开每路一个；无线按键规格里没有）。</summary>
public sealed class SwitchChannelDef
{
    public int Siid { get; init; }
    public int Piid { get; init; } = 1;

    /// <summary>展示名，优先规格 description，其次「路 N」。</summary>
    public string Label { get; init; } = "";
}

/// <summary>
/// 开关类设备的可写 on 通道定义（按 spec_type 缓存，不含运行时状态）。
/// 无线场景键 / 遥控键的规格只有 event，没有可写 on → Channels 为空 → 不显示开关。
/// </summary>
public sealed class SwitchControlSchema
{
    public List<SwitchChannelDef> Channels { get; } = new();

    public bool Valid => Channels.Count > 0;
}

/// <summary>抽屉里一路开关的运行时状态（每台设备各建一份，不跟规格缓存共用）。</summary>
public sealed partial class SwitchChannelState : ObservableObject
{
    public int Siid { get; init; }
    public int Piid { get; init; } = 1;
    public string Label { get; init; } = "";

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isBusy;
}
