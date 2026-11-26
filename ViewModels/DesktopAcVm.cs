// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Models;
using MiHomeHub.Services;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 桌面空调分区：一张按图 1 布局的大空调控制卡（当前温度 + 环形目标温度盘 + 模式/风速 + 电源）。
/// 绑定的空调就是全局默认空调（与设置里的选择同源），换空调等于改全局默认，两处永远一致。
/// 电源 / 模式 / 温度 / 风速命令直接转发主视图模型，下发路径与详情抽屉完全相同。
/// </summary>
public partial class DesktopAcVm : DesktopZoneVm
{
    private MijiaDevice? _ac;
    private MijiaDevice? _subscribed;

    public DesktopAcVm(MainViewModel main, DesktopGroupModel model)
        : base(main, model)
    {
        main.PropertyChanged += OnMainChanged;
        LoadDevices();
        RefreshAppearance();
    }

    public override bool IsAc => true;

    protected override string DangerText => "关闭空调分区";

    protected override string DangerConfirmText => "确认关闭？";

    public override string EmptyHint => "家里没有可控制的空调";

    /// <summary>首拉还没结束、空调还没认出来：这段是「还没数据」，不是「家里没空调」。</summary>
    public bool IsLoading => _ac == null && Main.SyncPending;

    /// <summary>空态要等加载收尾才判定，否则骨架期间就把「家里没有空调」顶上去。</summary>
    public override bool IsEmpty => !IsLoading && Ac == null;

    /// <summary>右上角兜底的分区菜单按钮：只在真正空态出现。加载期菜单在卡面控制行里，两处不能同时挂。</summary>
    public bool ShowCornerMenu => !ShowControls && !IsLoading;

    // ===== 绑定面：全部从当前空调派生，空调换了整组重算 =====

    public MijiaDevice? Ac => _ac;

    public bool ShowControls => _ac != null;

    public string AcName => _ac?.Name ?? "未选择空调";

    /// <summary>名称左边的房间标注；没归到房间就不显示。</summary>
    public string? AcRoom => _ac is { } d && !string.IsNullOrWhiteSpace(d.RoomName) && d.RoomName != "未分配房间"
        ? d.RoomName
        : null;

    /// <summary>副标题：在线报运行状态，离线直接写离线；规格没就位时提示先等一下。</summary>
    public string AcMeta => _ac switch
    {
        { IsOnline: false } => "离线",
        { IsOn: false } => "已关机",
        var d => RunningWord(d)
    };

    private static string RunningWord(MijiaDevice d)
    {
        if (d.AcSchema is { Valid: true } s)
            return AcOptionLabel(s.Modes, d.AcMode, "运行中");
        return "运行中";
    }

    public bool IsOn => _ac?.IsOn ?? false;

    public bool IsOnline => _ac?.IsOnline ?? false;

    /// <summary>是否正在运行（在线且开机）：身份行状态点据此点亮。</summary>
    public bool IsRunning => IsOnline && IsOn;

    /// <summary>室内温度读数（空调自身的环境温度）；这台不报环境温度时留一条短横杠，不要空一块。</summary>
    public string RoomTempText =>
        _ac?.TemperatureC is { } t ? $"{t:0.#}℃" : "—";

    /// <summary>目标温度读数（℃，一位小数）。</summary>
    public string TargetTempText =>
        _ac == null ? "—" : $"{_ac.AcTargetTemp:0.#}";

    /// <summary>温度滑杆量程与步长（规格缺失时用 16–30、步长 1 兜底）。</summary>
    public double TempMin => _ac?.AcSchema?.TempMin ?? 16;
    public double TempMax => _ac?.AcSchema?.TempMax ?? 30;
    public double TempStep => _ac?.AcSchema?.TempStep ?? 1;

    /// <summary>当前目标温度（滑杆双向绑定；松手后由命令下发）。</summary>
    public double TargetTemp => _ac?.AcTargetTemp ?? 26;

    /// <summary>温度环已填弧占整圆比例（0–1），供面板画扇形进度。</summary>
    public double TempRatio =>
        TempMax > TempMin ? Math.Clamp((TargetTemp - TempMin) / (TempMax - TempMin), 0, 1) : 0;

    /// <summary>模式选项（按规格 value-list 生成，选中态由设备当前值驱动）。</summary>
    public IReadOnlyList<AcOptionItem> ModeOptions => _ac?.AcSchema?.Modes ?? new List<AcOptionItem>();

    public bool HasModeOptions => ModeOptions.Count > 0;

    /// <summary>风速选项（按规格 value-list 生成）。</summary>
    public IReadOnlyList<AcOptionItem> FanOptions => _ac?.AcSchema?.Fans ?? new List<AcOptionItem>();

    public bool HasFanOptions => FanOptions.Count > 0;

    /// <summary>当前风速档位值（风速档位条 ctl:FanSpeedGauge 直接绑这个）。</summary>
    public int FanLevel => _ac?.AcFanLevel ?? 0;

    /// <summary>当前风速文案：模式/风速规格缺失时兜底档位号。</summary>
    public string FanText =>
        _ac?.AcSchema is { Valid: true } s ? AcOptionLabel(s.Fans, _ac.AcFanLevel, "档") : "—";

    /// <summary>当前模式文案（模式分段钮收进下拉时用这一行显示）。</summary>
    public string ModeText =>
        _ac?.AcSchema is { Valid: true } s ? AcOptionLabel(s.Modes, _ac.AcMode, "模式") : "—";

    /// <summary>规格是否就位：没拉到规格前模式/风速分段钮与温度量程都不画。</summary>
    public bool SchemaReady => _ac?.AcSchema is { Valid: true };

    /// <summary>空调是否支持整机电源键（规格里没有 on 就不画电源按钮）。</summary>
    public bool HasPower => _ac?.HasPower ?? false;

    /// <summary>设备列表换代（切家庭 / 刷新）后重取空调：did 变了或换了一代实例都要跟着走。</summary>
    public override void LoadDevices()
    {
        var next = Main.DesktopAc;
        if (ReferenceEquals(next, _subscribed) && ReferenceEquals(next, _ac)) return;

        _ac = next;
        if (!ReferenceEquals(_subscribed, next))
        {
            if (_subscribed != null) _subscribed.PropertyChanged -= OnAcChanged;
            _subscribed = next;
            if (next != null) next.PropertyChanged += OnAcChanged;
        }

        RaiseAll();
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.DesktopAc)
            or nameof(MainViewModel.HasDesktopAc)
            or nameof(MainViewModel.SelectedDesktopAcValue))
        {
            LoadDevices();
        }
        else if (e.PropertyName == nameof(MainViewModel.SyncPending))
        {
            // 加载收尾时空调可能仍是 null（家里确实没有），此时要从骨架切到空提示
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShowCornerMenu));
        }
    }

    private void OnAcChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MijiaDevice.IsOn):
            case nameof(MijiaDevice.AcMode):
            case nameof(MijiaDevice.AcTargetTemp):
            case nameof(MijiaDevice.AcFanLevel):
            case nameof(MijiaDevice.AcSchema):
                RaiseAll();
                break;
            case nameof(MijiaDevice.IsOnline):
                OnPropertyChanged(nameof(AcMeta));
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(IsRunning));
                break;
            case nameof(MijiaDevice.TemperatureC):
                OnPropertyChanged(nameof(RoomTempText));
                break;
            case nameof(MijiaDevice.Name):
            case nameof(MijiaDevice.RoomName):
                OnPropertyChanged(nameof(AcName));
                OnPropertyChanged(nameof(AcRoom));
                OnPropertyChanged(nameof(AcMeta));
                break;
        }
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Ac), nameof(ShowControls), nameof(IsLoading), nameof(IsEmpty),
                     nameof(ShowCornerMenu),
                     nameof(AcName), nameof(AcRoom), nameof(AcMeta),
                     nameof(IsOn), nameof(IsOnline), nameof(IsRunning), nameof(RoomTempText),
                     nameof(TargetTempText), nameof(TempMin), nameof(TempMax), nameof(TempStep),
                     nameof(TargetTemp), nameof(TempRatio), nameof(ModeOptions), nameof(HasModeOptions),
                     nameof(FanOptions), nameof(HasFanOptions), nameof(FanLevel), nameof(ModeText),
                     nameof(FanText), nameof(SchemaReady), nameof(HasPower)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ===== 命令：转发主视图模型，下发路径与详情抽屉完全相同 =====

    [RelayCommand]
    private void TogglePower() => Main.DesktopAcTogglePowerCommand.Execute(null);

    [RelayCommand]
    private void SetMode(object? value)
    {
        if (value is int mode) Main.DesktopAcSetModeCommand.Execute(mode);
    }

    [RelayCommand]
    private void SetFan(object? value)
    {
        if (value is int level) Main.DesktopAcSetFanCommand.Execute(level);
    }

    /// <summary>温度环里的加号：按规格步长往上一档（到顶就原地不动）。</summary>
    [RelayCommand]
    private void TempUp() => NudgeTemp(1);

    /// <summary>温度环里的减号：按规格步长往下一档（到底就原地不动）。</summary>
    [RelayCommand]
    private void TempDown() => NudgeTemp(-1);

    private void NudgeTemp(int direction)
    {
        var d = _ac;
        if (d == null) return;

        var step = TempStep;
        if (step <= 0) step = 1;
        var next = Math.Round((d.AcTargetTemp + direction * step) / step) * step;
        next = Math.Clamp(next, TempMin, TempMax);
        if (Math.Abs(next - d.AcTargetTemp) < 0.001) return;

        Main.DesktopAcSetTempCommand.Execute(next);
    }

    /// <summary>换空调浮层：贴桌面窗拿不到浮层，开一个独立小窗列出空调。落点矩形由 View 换算后传进来。</summary>
    public void OpenAcPicker(PixelRect anchor) => DesktopHost.OpenAcPicker(this, anchor);

    // ===== 换空调浮层 =====

    public ObservableCollection<AcPickItem> AcPicks { get; } = new();

    /// <summary>每次打开浮层都重建：空调可能刚被刷新换代，当前项也要重新比。</summary>
    public void ReloadAcPicks()
    {
        var current = Main.SelectedDesktopAcValue as string ?? "";
        AcPicks.Clear();
        foreach (var o in Main.DesktopAcOptions)
        {
            var did = o.Value as string ?? "";
            AcPicks.Add(new AcPickItem
            {
                Label = o.Label,
                Meta = o.Meta,
                Did = did,
                IsCurrent = did.Length > 0 && did == current
            });
        }
    }

    /// <summary>选中一台：写全局默认空调（与设置同源），卡片立刻跟着换。</summary>
    public void PickAc(string? did)
    {
        if (string.IsNullOrEmpty(did)) return;
        Main.SelectedDesktopAcValue = did;
        LoadDevices();
        ReloadAcPicks();
    }

    protected override void RemoveZone() => DesktopHost.HideAc();

    /// <summary>分区被随总开关收起时断开订阅，避免旧实例上的事件挂着。</summary>
    public void Detach()
    {
        Main.PropertyChanged -= OnMainChanged;
        if (_subscribed != null) _subscribed.PropertyChanged -= OnAcChanged;
        _subscribed = null;
    }

    private static string AcOptionLabel(List<AcOptionItem> opts, int value, string fallback) =>
        opts.FirstOrDefault(o => o.Value == value)?.Label ?? $"{fallback}{value}";
}

/// <summary>换空调浮层里的一台空调。</summary>
public sealed class AcPickItem
{
    public required string Label { get; init; }

    /// <summary>房间标注；未分配房间时为 null，不占位。</summary>
    public string? Meta { get; init; }

    public required string Did { get; init; }

    /// <summary>是否就是当前正在控制的这台（选中态只用颜色区分，不加勾）。</summary>
    public bool IsCurrent { get; init; }
}
