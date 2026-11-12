// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiHome.Models;

/// <summary>抽屉里一路开关的运行时状态（每台设备各建一份，不跟规格缓存共用）。</summary>
public sealed partial class SwitchChannelState : ObservableObject
{
    public int Siid { get; init; }
    public int Piid { get; init; } = 1;

    /// <summary>展示名：规格描述；多路同名时由设备侧补路序。</summary>
    public string Label { get; set; } = "";

    /// <summary>所属设备：下发时要用它的 TargetDid（虚拟子设备回到父设备）。</summary>
    [JsonIgnore] public MijiaDevice? Device { get; init; }

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isBusy;
}
