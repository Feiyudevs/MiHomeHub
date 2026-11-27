// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Models;
using MiHomeHub.Services;
using QRCoder;

namespace MiHomeHub.ViewModels;

public partial class MainViewModel : ObservableObject
{
    /// <summary>详情抽屉里灯亮度/色温的防抖提交定时器（拖动过程中不连发请求）。</summary>
    private IDisposable? _lightCommitTimer;

    /// <summary>风扇连续档位滑杆的防抖提交定时器。</summary>
    private IDisposable? _fanCommitTimer;

    /// <summary>风扇无级调速滑杆的防抖提交定时器。</summary>
    private IDisposable? _fanSpeedCommitTimer;

    /// <summary>当前详情设备及其属性订阅，用于把亮度/色温变化提交回设备。</summary>
    private MijiaDevice? _detailDevice;
    private PropertyChangedEventHandler? _detailHandler;

    /// <summary>正在回填详情值，期间忽略属性变化，避免把刚读到的值又当成用户改动提交一次。</summary>
    private bool _loadingDetail;

    /// <summary>这次防抖提交里亮度 / 色温各自有没有被拖过：只下发真的变了的那个，不拿本地缓存的另一项去覆盖设备。</summary>
    private bool _lightCommitBrightness;
    private bool _lightCommitColorTemp;

    /// <summary>下发失败后回读设备真实值，期间同样忽略属性变化。</summary>
    private bool _resyncingLight;

    /// <summary>高级设置：抽屉侧边栏只留常用控制卡，其余规格属性（按 MIoT 服务分组）与无入参动作全部收进这里。</summary>
    public ObservableCollection<SpecGroupVm> DrawerSpecAdvancedGroups { get; } = new();
    public ObservableCollection<SpecActionVm> DrawerSpecAdvancedActions { get; } = new();

    /// <summary>详情顶部的实时读数卡（功率/电压/电流/温度/湿度/PM2.5/照度等）；
    /// 点一下即切换「是否显示在主页设备卡」，原「卡片外显」配置区已并进这一排。</summary>
    public ObservableCollection<MetricTileVm> DrawerMetricTiles { get; } = new();

    [ObservableProperty] private bool _hasAdvancedPanel;
    [ObservableProperty] private bool _hasAdvancedActions;
    [ObservableProperty] private bool _isAdvancedExpanded;
    [ObservableProperty] private bool _hasDrawerMetricTiles;

    /// <summary>读数卡下方的说明，带上类型名，提示改动对同类设备一起生效。</summary>
    [ObservableProperty] private string _metricTilesHint = "";

    /// <summary>说明文字的悬停提示：改动对哪一类设备一起生效。</summary>
    [ObservableProperty] private string _metricTilesHintTip = "";

    /// <summary>抽屉「智能场景」：默认折叠，点标题展开（与高级设置同一套开合交互）。</summary>
    [ObservableProperty] private bool _isSmartSceneExpanded;

    /// <summary>高级设置区是否有内容（规格属性组或动作任一存在）。</summary>
    public bool HasAdvanced => HasAdvancedPanel || HasAdvancedActions;

    /// <summary>高级设置里属性组与动作之间那条分割线：两段都有内容才画。</summary>
    public bool HasAdvancedDivider => HasAdvancedPanel && HasAdvancedActions;

    partial void OnHasAdvancedPanelChanged(bool value)
    {
        OnPropertyChanged(nameof(HasAdvanced));
        OnPropertyChanged(nameof(HasAdvancedDivider));
    }

    partial void OnHasAdvancedActionsChanged(bool value)
    {
        OnPropertyChanged(nameof(HasAdvanced));
        OnPropertyChanged(nameof(HasAdvancedDivider));
    }

    /// <summary>滑杆防抖提交定时器（每个属性一份）与动作二次确认超时。</summary>
    private readonly Dictionary<SpecPropVm, IDisposable> _specCommitTimers = new();
    private IDisposable? _actionConfirmTimer;

    [RelayCommand]
    private void OpenDrawer(MijiaDevice? device)
    {
        if (device == null) return;
        // 换设备时先把上一台的画面停掉：解码线程和画面源都只服务一台摄像机
        if (IsCameraLive) StopCameraLiveCore("已停止内置画面");
        DrawerDevice = device;
        IsDrawerOpen = true;
        // 每次打开都回到默认折叠，不继承上一台设备的展开态
        IsSmartSceneExpanded = false;
        BuildSpecPanel(device);

        // 中枢网关：云端设备列表带 localip 时直接用它，没带就回落上次手填/扫描到的地址
        if (device.IsHub)
        {
            HubGeekIp = !string.IsNullOrWhiteSpace(device.LocalIp)
                ? HubGeek.NormalizeIp(device.LocalIp)
                : AppSettingsStore.GetHubIp(device.Did);
            HubGeekHint = "";
            IsHubGeekHelpExpanded = false;
        }

        AttachDrawerDetail(device);
        UpdateDrawerScenes();
    }

    /// <summary>抽屉关闭时释放面板 VM 与拖拽/确认定时器，避免后台轮询继续写隐藏控件。</summary>
    private void ClearSpecPanel()
    {
        // 取消对详情设备的 PropertyChanged 订阅，避免抽屉反复打开/关闭时累积事件处理器
        if (_detailDevice is { } detailDev && _detailHandler is { } handler)
        {
            detailDev.PropertyChanged -= handler;
            detailDev.AcTargetTempSink = null;
            _detailDevice = null;
            _detailHandler = null;
        }
        foreach (var timer in _specCommitTimers.Values) timer.Dispose();
        _specCommitTimers.Clear();
        _fanCommitTimer?.Dispose();
        _fanCommitTimer = null;
        _fanSpeedCommitTimer?.Dispose();
        _fanSpeedCommitTimer = null;
        _actionConfirmTimer?.Dispose();
        _actionConfirmTimer = null;
        DrawerSpecAdvancedGroups.Clear();
        DrawerSpecAdvancedActions.Clear();
        DrawerMetricTiles.Clear();
        HasAdvancedPanel = false;
        HasAdvancedActions = false;
        HasDrawerMetricTiles = false;
        IsAdvancedExpanded = false;
        IsSmartSceneExpanded = false;
    }

    /// <summary>
    /// 开关命令在卡片和抽屉里共用同一个实例。Avalonia 的 Button 会按 Command.CanExecute
    /// 决定自身可用性，异步命令执行期间默认 CanExecute 为 false，会让所有绑定该命令的
    /// 开关一起变灰闪一下；所以这里允许并发，单个设备的连点由各自的 IsBusy 拦截。
    /// 卡片 ToggleButton 的 TwoWay 绑定会先把 IsOn 翻好，这里直接按当前 IsOn 下发。
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleDeviceAsync(MijiaDevice? device)
    {
        if (_client == null || device == null) return;
        if (!device.IsOnline)
        {
            Log($"{device.Name} 离线，指令未下发");
            device.IsOn = !device.IsOn;
            return;
        }
        if (device.IsBusy) return;
        device.IsBusy = true;
        device.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            if (device.Channels.Count == 1)
            {
                var ch = device.Channels[0];
                await _client.SetChannelAsync(ch, device.IsOn);
                ch.IsOn = device.IsOn;
                device.SyncOnFromChannels();
            }
            else if (device.IsCurtain)
            {
                await _client.SetCurtainAsync(device.TargetDid, device.IsOn);
            }
            else
            {
                await _client.SetPowerAsync(device, device.IsOn);
            }
            Log($"{device.Name} → {(device.IsOn ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            Log($"控制失败：{ex.Message}");
            device.IsOn = !device.IsOn;
            device.SyncOnFromChannels();
        }
        finally
        {
            device.IsBusy = false;
            UpdateStats();
        }
    }

    /// <summary>多路开关逐路下发：先翻本地态（按钮 TwoWay 绑定），失败再翻回来。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleChannelAsync(SwitchChannelState? channel)
    {
        if (_client == null || channel?.Device is not { } device) return;
        if (!device.IsOnline)
        {
            channel.IsOn = !channel.IsOn;
            device.SyncOnFromChannels();
            Log($"{device.Name} 离线，指令未下发");
            return;
        }
        if (channel.IsBusy) return;
        channel.IsBusy = true;
        device.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetChannelAsync(channel, channel.IsOn);
            device.SyncOnFromChannels();
            Log($"{device.Name} · {channel.Label} → {(channel.IsOn ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            channel.IsOn = !channel.IsOn;
            device.SyncOnFromChannels();
            Log($"控制失败：{ex.Message}");
        }
        finally
        {
            channel.IsBusy = false;
            UpdateStats();
        }
    }

    /// <summary>
    /// 关掉一台设备：走卡片电源键、抽屉逐路那两条命令同一条下发路径，不另写一份协议细节。
    /// 多路设备（卡面只有「N 路」角标、没有整机开关）逐路关，其余按整机开关关。
    /// </summary>
    private async Task TurnOffDeviceAsync(MijiaDevice device)
    {
        if (device.Channels.Count > 1)
        {
            foreach (var channel in device.Channels.Where(c => c.IsOn).ToList())
            {
                channel.IsOn = false;
                await ToggleChannelAsync(channel);
            }
            return;
        }

        device.IsOn = false;
        await ToggleDeviceAsync(device);
    }

    /// <summary>
    /// 抽屉打开时订阅当前设备的亮度/色温变化（滑动防抖后提交），并异步回填设备可控属性。
    /// 用同一实例反复打开也会重新订阅，避免 ObservableProperty 相同值不触发而漏刷新。
    /// </summary>
    private void AttachDrawerDetail(MijiaDevice device)
    {
        if (_detailDevice != null && _detailHandler != null)
        {
            _detailDevice.PropertyChanged -= _detailHandler;
            _detailDevice.AcTargetTempSink = null;
        }
        _detailDevice = device;
        // 目标温度步进器写进来的绝对值交给这里校准并下发（设备自己不知道网络那层）
        device.AcTargetTempSink = temp => _ = ApplyAcTargetTempAsync(device, temp);
        _detailHandler = (_, e) =>
        {
            if (_loadingDetail || _resyncingLight || _applyingStates) return;
            if (e.PropertyName is nameof(MijiaDevice.Brightness) or nameof(MijiaDevice.ColorTemp))
                ScheduleLightCommit(device, e.PropertyName);
            else if (e.PropertyName is nameof(MijiaDevice.SpeakerVolume))
                ScheduleSpeakerVolumeCommit(device);
            else if (e.PropertyName is nameof(MijiaDevice.FanLevel))
                ScheduleFanLevelCommit(device);
            else if (e.PropertyName is nameof(MijiaDevice.FanSpeed))
                ScheduleFanSpeedCommit(device);
        };
        device.PropertyChanged += _detailHandler;
        _ = LoadDetailAsync(device);
    }

    /// <summary>读取灯/空调的可控属性回填抽屉，让滑杆与按钮反映设备当前真实值。</summary>
    private async Task LoadDetailAsync(MijiaDevice device)
    {
        if (_client is not { } client) return;
        try
        {
            _loadingDetail = true;

            // 规格可能还在后台解析：打开抽屉时就地补一次，面板立刻可用
            if (device.Spec == null)
            {
                var spec = await DeviceSpecService.GetAsync(client, device);
                if (spec != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        device.ApplySpec(spec);
                        if (ReferenceEquals(DrawerDevice, device)) BuildSpecPanel(device);
                    });
                }
            }
            else if (ReferenceEquals(DrawerDevice, device))
            {
                BuildSpecPanel(device);
            }

            await LoadSpecPanelValuesAsync(device);
            if (!device.IsOnline) return;

            if (device.IsLight)
            {
                var st = await client.GetLightStateAsync(device.TargetDid, device.LightSchema);
                if (st != null)
                {
                    if (st.Brightness is { } b) device.Brightness = b;
                    if (st.ColorTemp is { } ct) device.ColorTemp = ct;
                    device.LightMode = st.Mode;
                }
            }
            else if (device.IsCurtain)
            {
                var pos = await client.GetCurtainPositionAsync(device.TargetDid);
                if (pos.HasValue) device.CurtainPosition = pos.Value;
            }
            else if (device.IsAc)
            {
                var schema = await MijiaSpecService.GetAcSchemaAsync(device.SpecType);
                device.AcSchema = schema;
                if (schema is { Valid: true })
                {
                    var st = await client.GetAcStateAsync(device.TargetDid, schema);
                    if (st != null)
                    {
                        device.AcMode = st.Mode;
                        device.AcTargetTemp = st.TargetTemp;
                        device.AcFanLevel = st.FanLevel;
                    }
                }
                else
                {
                    Log($"未获取到 {device.Name} 的空调控制参数，暂时无法下发");
                }
            }
            else if (device.IsFan)
            {
                await LoadFanStateAsync(client, device);
            }
        }
        catch (Exception ex)
        {
            Log($"读取 {device.Name} 详情失败：{ex.Message}");
        }
        finally
        {
            _loadingDetail = false;
        }
    }

    /// <summary>
    /// 风扇回读：档位、模式、摆风一次读完。规格里没有的可写项就跳过，
    /// 界面按 FanSchema 决定显示哪几张卡，读不到的项保持本地值不动。
    /// </summary>
    private async Task LoadFanStateAsync(MijiaClient client, MijiaDevice device)
    {
        if (device.FanSchema is not { Valid: true } schema) return;
        var query = new List<(string Did, int Siid, int Piid)>();
        if (schema.LevelPiid > 0) query.Add((device.TargetDid, schema.LevelSiid, schema.LevelPiid));
        if (schema.HasSpeed) query.Add((device.TargetDid, schema.SpeedSiid, schema.SpeedPiid));
        if (schema.ModePiid > 0) query.Add((device.TargetDid, schema.ModeSiid, schema.ModePiid));
        if (schema.HasSwingH) query.Add((device.TargetDid, schema.SwingHSiid, schema.SwingHPiid));
        if (schema.HasSwingV) query.Add((device.TargetDid, schema.SwingVSiid, schema.SwingVPiid));
        if (query.Count == 0) return;

        var map = await client.ReadPropsAsync(query);
        if (_client == null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(DrawerDevice, device)) return;
            if (schema.LevelPiid > 0 && map.TryGetValue((device.TargetDid, schema.LevelSiid, schema.LevelPiid), out var lv) && lv != null)
                device.FanLevel = (int)Math.Round(ToDouble(lv));
            if (schema.HasSpeed && map.TryGetValue((device.TargetDid, schema.SpeedSiid, schema.SpeedPiid), out var sp) && sp != null)
                device.FanSpeed = (int)Math.Round(ToDouble(sp));
            if (schema.ModePiid > 0 && map.TryGetValue((device.TargetDid, schema.ModeSiid, schema.ModePiid), out var mv) && mv != null)
                device.FanMode = ToInt(mv);
            if (schema.HasSwingH && map.TryGetValue((device.TargetDid, schema.SwingHSiid, schema.SwingHPiid), out var sv))
                device.SwingHOn = sv is true;
            if (schema.HasSwingV && map.TryGetValue((device.TargetDid, schema.SwingVSiid, schema.SwingVPiid), out var tv))
                device.SwingVOn = tv is true;
            UpdateStats();
        });
    }

    /// <summary>按规格把抽屉里的通用属性/通道/电源读一遍，填进对应的行。</summary>
    private async Task LoadSpecPanelValuesAsync(MijiaDevice device)
    {
        if (_client is not { } client || !device.IsOnline) return;
        var query = new List<(string Did, int Siid, int Piid)>();
        foreach (var vm in DrawerSpecAdvancedGroups.SelectMany(g => g.Items))
            query.Add((device.TargetDid, vm.Def.Siid, vm.Def.Piid));
        // 顶部读数卡：把能读的指标一起取了，打开抽屉立刻有值
        foreach (var tile in DrawerMetricTiles)
            query.Add((device.TargetDid, tile.Def.Siid, tile.Def.Piid));
        foreach (var ch in device.Channels)
            query.Add((device.TargetDid, ch.Siid, ch.Piid));
        if (device.HasPower) query.Add((device.TargetDid, device.PowerSiid, device.PowerPiid));
        if (device.IsCurtain) query.Add((device.TargetDid, 2, 3));
        if (query.Count == 0) return;

        var map = await client.ReadPropsAsync(query.Distinct().ToList());
        if (_client == null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(DrawerDevice, device)) return;
            ApplySpecValues(device, map);
            UpdateDrawerMetricTiles(device, map);
            foreach (var ch in device.Channels)
                if (map.TryGetValue((device.TargetDid, ch.Siid, ch.Piid), out var v))
                    ch.IsOn = v is true;
            device.SyncOnFromChannels();
            if (device.HasPower &&
                map.TryGetValue((device.TargetDid, device.PowerSiid, device.PowerPiid), out var pv))
                device.IsOn = pv is true;
            if (device.IsCurtain &&
                map.TryGetValue((device.TargetDid, 2, 3), out var cv) && cv != null)
                device.CurtainPosition = Math.Clamp(ToInt(cv), 0, 100);
            UpdateStats();
        });
    }

    /// <summary>只读属性只有在有信息量时才列出：布尔/枚举状态、温湿度等读数、电源类指标。</summary>
    private static readonly HashSet<string> MetricPropNames = new(StringComparer.Ordinal)
    {
        "temperature", "relative-humidity", "humidity", "pm2.5", "pm2.5-density", "pm10",
        "pm10-density", "pm1", "pm1-density", "illuminance",
        "co2", "co2-density", "tvoc", "tvoc-density", "voc-density", "hcho", "hcho-density",
        "noise", "noise-decibel", "dust", "dust-density", "tds-in", "tds-out", "air-quality", "water-level",
        "electric-power", "power", "surge-power", "power-consumption", "energy-consumption",
        "voltage", "electric-voltage", "electric-current", "current", "motion-detected",
        "contact-state", "occupancy", "smoke-state", "leak", "door-state", "submersion-state"
    };

    /// <summary>
    /// 按规格重建抽屉的「高级设置」：侧边栏主体只放常用控制卡，
    /// 其余规格属性与无入参动作全部收进折叠区；专门卡片已覆盖的属性不再重复列出。
    /// </summary>
    private void BuildSpecPanel(MijiaDevice device)
    {
        DrawerSpecAdvancedGroups.Clear();
        DrawerSpecAdvancedActions.Clear();
        DrawerMetricTiles.Clear();
        var spec = device.Spec;
        if (spec == null)
        {
            HasAdvancedPanel = false;
            HasAdvancedActions = false;
            HasDrawerMetricTiles = false;
            return;
        }

        BuildMetricTiles(device);

        // 按 siid 分组：多路面板的规格里会有多个同名服务（Light 1/2/3、Curtain 1/2），
        // 按描述分组会把它们并成一张卡，出现多行同名的滑杆。
        // 耗材卡（滤芯/电量）已经单独列出，高级设置里不再重复铺这些只读行
        var consumableKeys = spec.ConsumableProps.Select(c => (c.Siid, c.Piid)).ToHashSet();
        var groups = spec.Properties
            .Where(p => !consumableKeys.Contains((p.Siid, p.Piid)) && ShouldShowProp(device, p))
            .GroupBy(p => p.Siid)
            .ToList();
        var descCounts = groups
            .GroupBy(g => g.First().ServiceDesc)
            .ToDictionary(g => g.Key, g => g.Count());
        var descSeq = new Dictionary<string, int>(StringComparer.Ordinal);
        var groupTitles = new Dictionary<int, string>();
        foreach (var group in groups)
        {
            var desc = group.First().ServiceDesc;
            var title = desc;
            if (descCounts.TryGetValue(desc, out var total) && total > 1)
            {
                descSeq[desc] = descSeq.GetValueOrDefault(desc) + 1;
                title = $"{desc} {descSeq[desc]}";
            }

            var groupTitle = string.IsNullOrWhiteSpace(title) ? "属性" : title;
            groupTitles[group.Key] = groupTitle;
            DrawerSpecAdvancedGroups.Add(new SpecGroupVm
            {
                Title = groupTitle,
                Items = BuildPropVms(device, group.ToList(), ScheduleSpecCommit)
            });
        }

        var actions = spec.Actions
            .Where(a => a.InputCount == 0 && ShouldShowAction(device, spec, a))
            .ToList();
        var actionCounts = actions.GroupBy(a => a.Desc).ToDictionary(g => g.Key, g => g.Count());
        foreach (var action in actions)
        {
            var title = action.Desc;
            if (actionCounts[action.Desc] > 1 && groupTitles.TryGetValue(action.Siid, out var serviceTitle))
                title = $"{serviceTitle} · {action.Desc}";

            DrawerSpecAdvancedActions.Add(new SpecActionVm
            {
                Device = device,
                Def = action,
                TitleOverride = title
            });
        }

        HasAdvancedPanel = DrawerSpecAdvancedGroups.Count > 0;
        HasAdvancedActions = DrawerSpecAdvancedActions.Count > 0;
    }

    /// <summary>详情顶部的读数卡：把这台设备规格里能读的指标各铺一枚，值随轮询刷新；
    /// 卡片自己就是开关，点一下决定它显不显示在主页设备卡上。</summary>
    private void BuildMetricTiles(MijiaDevice device)
    {
        DrawerMetricTiles.Clear();
        var selected = CardMetricsCatalog.EffectiveNames(device);
        foreach (var p in CardMetricsCatalog.Candidates(device))
        {
            // 点亮状态必须拿归一后的标准键去比（selected 里存的是 pm2.5 / relative-humidity 这类键），
            // 拿规格原始短名（pm2.5-density、co2-density……）比会永远对不上：
            // 读数早就在卡片上了，这里却一直显示没点亮
            var tile = new MetricTileVm { Def = p };
            tile.IsShown = selected.Contains(tile.Name);
            DrawerMetricTiles.Add(tile);
        }
        HasDrawerMetricTiles = DrawerMetricTiles.Count > 0;
        var label = CardMetricsCatalog.TypeLabel(device);
        // 说明只留最短的一句，改动影响哪些设备收进悬停提示，不在正文里堆两行
        MetricTilesHint = "点亮房子即在主页显示";
        MetricTilesHintTip = $"改动对{label}类设备一起生效。";
    }

    /// <summary>用本轮读到的原始值刷新读数卡；没读到的保持上一次的值。</summary>
    private void UpdateDrawerMetricTiles(MijiaDevice device,
        Dictionary<(string Did, int Siid, int Piid), object?> map)
    {
        if (!ReferenceEquals(DrawerDevice, device)) return;
        foreach (var tile in DrawerMetricTiles)
        {
            if (!map.TryGetValue((device.TargetDid, tile.Def.Siid, tile.Def.Piid), out var raw) || raw == null)
                continue;
            tile.ValueText = CardMetricsCatalog.FormatValue(tile.Def, ToDouble(raw));
        }
    }

    /// <summary>切换一张读数卡的「主页可见」状态；写回按类型保存，并让同类设备立刻按新配置刷新。</summary>
    [RelayCommand]
    private void ToggleMetricTile(MetricTileVm? item)
    {
        if (item == null || DrawerDevice == null) return;
        var typeKey = CardMetricsCatalog.TypeKey(DrawerDevice);
        var current = CardMetricsCatalog.EffectiveNames(typeKey).ToList();
        if (!current.Remove(item.Name)) current.Add(item.Name);
        AppSettingsStore.SetCardMetrics(typeKey, current);
        AfterCardMetricsChanged(typeKey);
    }

    /// <summary>恢复该类型的默认外显（插座=功率，其余=温度+湿度）。</summary>
    [RelayCommand]
    private void ResetCardMetrics()
    {
        if (DrawerDevice == null) return;
        var typeKey = CardMetricsCatalog.TypeKey(DrawerDevice);
        AppSettingsStore.ClearCardMetrics(typeKey);
        AfterCardMetricsChanged(typeKey);
    }

    private void AfterCardMetricsChanged(string typeKey)
    {
        var selected = CardMetricsCatalog.EffectiveNames(typeKey);
        // 卡片副标题当场按新配置重拼，不等下一拍轮询：否则刚关掉的读数会继续挂在卡片上，
        // 看上去就是「详情里没点亮，主页/桌面照样显示」。重拼只动副标题，
        // 温湿度读数本身（TemperatureC / Humidity）不动，环境概览不会闪空
        foreach (var d in _allDevices) RebuildCardSubtitle(d);
        foreach (var vm in DrawerMetricTiles) vm.IsShown = selected.Contains(vm.Name);
        RecomputeEnvironment();
        if (_client != null && IsLoggedIn) _ = RefreshStatesAsync(includeMetrics: true);
    }

    private List<SpecPropVm> BuildPropVms(MijiaDevice device, List<SpecPropertyDef> defs, Action<SpecPropVm> commit)
    {
        var items = new List<SpecPropVm>(defs.Count);
        foreach (var def in defs)
        {
            var vm = new SpecPropVm
            {
                Device = device,
                Def = def,
                SliderCommit = commit,
                ChoiceCommit = (p, v) =>
                {
                    var opt = p.Options.FirstOrDefault(o => o.Value == v);
                    if (opt is not null) _ = SetSpecChoiceAsync(opt);
                }
            };
            foreach (var opt in def.Options)
                vm.Options.Add(new SpecOptionVm { Prop = vm, Def = opt });
            items.Add(vm);
        }
        return items;
    }

    private static bool ShouldShowProp(MijiaDevice device, SpecPropertyDef p)
    {
        // 虚拟子设备只是父设备的一路通道，别把父设备的灯/窗帘服务也铺到它身上
        if (device.IsVirtual && p.Siid != device.ChannelSiid) return false;

        // 整机电源/多路通道已在专门卡片里展示
        if (device.HasPower && p.Siid == device.PowerSiid && p.Piid == device.PowerPiid) return false;
        if (p.InSwitchService) return false;

        // 灯/空调/窗帘的专门滑杆与按钮已覆盖这些属性
        if (device.IsLight && p.ServiceName == "light" &&
            p.Name is "brightness" or "color-temperature") return false;
        if (device.IsAc && p.Name is "mode" or "target-temperature" or "fan-level") return false;
        // 风扇的档位/摆风/模式已由专门面板展示（含摆风开关），高级设置里不再重复
        if (device.IsFan && p.Name is "fan-level" or "speed-level" or "wind-level" or "mode"
            or "fan-mode" or "horizontal-swing" or "vertical-swing" or "swing") return false;
        if (device.IsCurtain && p.Name is "motor-control" or "current-position" or "target-position")
            return false;

        // 规格没给描述（Desc 退化成短名）的多是厂商内部参数（lock-state、light-mode-one），不铺面板
        if (p.Desc == p.Name) return false;

        // 可写但没有范围/选项的可写项（配置字符串等）P0 没有合适的控件，不铺只读行
        if (p.Writable && p.Kind == SpecControlKind.Value) return false;
        if (p.Writable) return true;
        return p.IsBool || p.Options.Count > 0 || MetricPropNames.Contains(p.Name);
    }

    /// <summary>同一服务里已有开关/滑杆的动作不重复铺按钮（toggle、亮度增减）。</summary>
    private static bool ShouldShowAction(MijiaDevice device, DeviceSpec spec, SpecActionDef a)
    {
        if (device.IsVirtual && a.Siid != device.ChannelSiid) return false;
        if (a.Desc == a.Name) return false;
        if (device.IsCurtain && a.Name == "motion-state-toggle") return false;

        var coveredBy = a.Name switch
        {
            "toggle" => "on",
            "brightness-up" or "brightness-down" => "brightness",
            _ => null
        };
        return coveredBy == null ||
               !spec.Properties.Any(p => p.Siid == a.Siid && p.Writable && p.Name == coveredBy);
    }

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    [RelayCommand]
    private void ToggleSmartScenes() => IsSmartSceneExpanded = !IsSmartSceneExpanded;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleSpecAsync(SpecPropVm? prop)
    {
        if (_client == null || prop == null) return;
        var d = prop.Device;
        if (!d.IsOnline)
        {
            prop.IsOn = !prop.IsOn;
            Log($"{d.Name} 离线，指令未下发");
            return;
        }
        if (prop.IsBusy) return;
        prop.IsBusy = true;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetPropAsync(d.TargetDid, prop.Def.Siid, prop.Def.Piid, prop.IsOn);
            prop.ValueText = prop.IsOn ? "是" : "否";
            Log($"{d.Name} · {prop.Title} → {(prop.IsOn ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            prop.IsOn = !prop.IsOn;
            Log($"设置 {d.Name} · {prop.Title} 失败：{ex.Message}");
        }
        finally
        {
            prop.IsBusy = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetSpecChoiceAsync(SpecOptionVm? option)
    {
        if (option == null) return;
        var prop = option.Prop;
        var d = prop.Device;
        // 下拉已先改了显示值、SyncSelection 也挪走了 IsSelected，回滚基准取设备侧已生效的 Number
        var previous = (object?)(int)Math.Round(prop.Number);
        if (_client == null)
        {
            prop.SetPickedQuiet(previous);
            return;
        }
        if (!d.IsOnline)
        {
            prop.SetPickedQuiet(previous);
            Log($"{d.Name} 离线，指令未下发");
            return;
        }
        if (prop.IsBusy)
        {
            prop.SetPickedQuiet(previous);
            return;
        }
        prop.IsBusy = true;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetPropAsync(d.TargetDid, prop.Def.Siid, prop.Def.Piid, option.Value);
            foreach (var o in prop.Options) o.IsSelected = o == option;
            prop.SetNumberQuiet(option.Value);
            prop.SetPickedQuiet(option.Value);
            Log($"{d.Name} · {prop.Title} → {option.Label}");
        }
        catch (Exception ex)
        {
            prop.SetPickedQuiet(previous);
            Log($"设置 {d.Name} · {prop.Title} 失败：{ex.Message}");
        }
        finally
        {
            prop.IsBusy = false;
        }
    }

    /// <summary>拖动结束后 500ms 再提交一次，避免滑杆每帧发请求。</summary>
    private void ScheduleSpecCommit(SpecPropVm prop)
    {
        if (_specCommitTimers.Remove(prop, out var old)) old.Dispose();
        _specCommitTimers[prop] = DispatcherTimer.RunOnce(() =>
        {
            _specCommitTimers.Remove(prop);
            _ = CommitSpecNumberAsync(prop);
        }, TimeSpan.FromMilliseconds(500));
    }

    private async Task CommitSpecNumberAsync(SpecPropVm prop)
    {
        if (_client == null) return;
        var d = prop.Device;
        if (!d.IsOnline)
        {
            Log($"{d.Name} 离线，指令未下发");
            return;
        }
        object value = prop.Def.Format is "float" or "double"
            ? Math.Round(prop.Number, 1)
            : (int)Math.Round(prop.Number);
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetPropAsync(d.TargetDid, prop.Def.Siid, prop.Def.Piid, value);
            Log($"{d.Name} · {prop.Title} → {prop.SliderText}");
        }
        catch (Exception ex)
        {
            Log($"设置 {d.Name} · {prop.Title} 失败：{ex.Message}");
        }
    }

    /// <summary>无入参动作先要点一次「确认执行」，4 秒内不确认自动撤回。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunSpecActionAsync(SpecActionVm? action)
    {
        if (_client == null || action == null) return;
        var d = action.Device;
        if (!d.IsOnline)
        {
            Log($"{d.Name} 离线，指令未下发");
            return;
        }

        if (!action.IsConfirming)
        {
            action.IsConfirming = true;
            _actionConfirmTimer?.Dispose();
            _actionConfirmTimer = DispatcherTimer.RunOnce(() => action.IsConfirming = false,
                TimeSpan.FromSeconds(4));
            return;
        }

        _actionConfirmTimer?.Dispose();
        action.IsConfirming = false;
        if (action.IsBusy) return;
        action.IsBusy = true;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.RunActionAsync(d.TargetDid, action.Def.Siid, action.Def.Aiid);
            Log($"{d.Name} · {action.Title} 已执行");
            await RefreshStatesAsync(includeMetrics: false);
        }
        catch (Exception ex)
        {
            Log($"{d.Name} · {action.Title} 执行失败：{ex.Message}");
        }
        finally
        {
            action.IsBusy = false;
        }
    }

    /// <summary>拖动结束后停 600ms 再提交一次亮度/色温，避免连发请求。</summary>
    private void ScheduleLightCommit(MijiaDevice device, string? changed)
    {
        if (changed == nameof(MijiaDevice.Brightness)) _lightCommitBrightness = true;
        else _lightCommitColorTemp = true;

        _lightCommitTimer?.Dispose();
        _lightCommitTimer = DispatcherTimer.RunOnce(
            () => _ = CommitLightAsync(device), TimeSpan.FromMilliseconds(600));
    }

    /// <summary>
    /// 把拖过的亮度/色温下发给设备。
    /// 量程与 siid/piid 都按设备规格走：写死 2700–6500 时，量程更窄的灯（3000–6500、3000–6400）
    /// 收到端点值会被云端按越界拒收，灯一动不动；量程更宽的灯组（1700–7000）又摸不到真正的端点。
    /// 色温还多一步：灯停在预设场景（阅读/影院等）里时写色温不生效，先把 mode 拨回可调色温档。
    /// </summary>
    private async Task CommitLightAsync(MijiaDevice device)
    {
        var brightness = _lightCommitBrightness;
        var colorTemp = _lightCommitColorTemp;
        _lightCommitBrightness = _lightCommitColorTemp = false;

        if (_client is not { } client || !device.IsOnline) return;

        var schema = device.LightSchema;
        var ctSupported = schema?.HasColorTemp ?? true;
        var failures = new List<string>();

        if (colorTemp && ctSupported && schema is { ResetModeValue: >= 0, ModePiid: > 0 } &&
            device.LightMode != schema.ResetModeValue)
        {
            try
            {
                await client.SetPropAsync(device.TargetDid, schema.ModeSiid, schema.ModePiid, schema.ResetModeValue);
                device.LightMode = schema.ResetModeValue;
            }
            catch (Exception ex)
            {
                failures.Add($"预设模式 {ex.Message}");
            }
        }

        if (brightness)
        {
            try
            {
                await client.SetLightBrightnessAsync(device.TargetDid, device.Brightness, schema);
            }
            catch (Exception ex)
            {
                failures.Add($"亮度 {ex.Message}");
            }
        }

        if (colorTemp && ctSupported)
        {
            try
            {
                // 量程用生效中的区间（可能包含用户自定义的超出规格的端点），不拿规格硬夹
                await client.SetLightColorTempAsync(device.TargetDid, device.ColorTemp, schema,
                    device.ColorTempMin, device.ColorTempMax);
            }
            catch (Exception ex)
            {
                failures.Add($"色温 {ex.Message}");
            }
        }

        if (failures.Count == 0)
            Log($"{device.Name} 亮度 {device.Brightness}% · 色温 {device.ColorTemp}K");
        else
        {
            Log($"灯光设置失败（{device.Name}）：{string.Join("；", failures)}");
            // 被拒收时滑杆停在设备从未采纳的值上，再拖到同一处就不再生效；回读一次真实值把它拉回来
            await ResyncLightAsync(device);
        }
    }

    /// <summary>下发失败后回读设备的真实亮度/色温，避免滑杆停在设备从未采纳的位置。</summary>
    private async Task ResyncLightAsync(MijiaDevice device)
    {
        if (_client is not { } client || !device.IsOnline) return;
        try
        {
            _resyncingLight = true;
            var st = await client.GetLightStateAsync(device.TargetDid, device.LightSchema);
            if (st == null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(DrawerDevice, device)) return;
                if (st.Brightness is { } b) device.Brightness = b;
                if (st.ColorTemp is { } ct) device.ColorTemp = ct;
                device.LightMode = st.Mode;
            });
        }
        catch (Exception ex)
        {
            Log($"回读 {device.Name} 灯光状态失败：{ex.Message}");
        }
        finally
        {
            _resyncingLight = false;
        }
    }

    // ===== 灯：色温量程自定义（按设备 did 记在 app.json，端点可超出设备规格标注） =====

    /// <summary>展开 / 收起色温量程编辑区；展开时以当前生效的区间为起点。</summary>
    [RelayCommand]
    private void ToggleLightRange()
    {
        if (DrawerDevice is not { HasColorTemp: true } d) return;
        if (d.IsEditingLightRange)
        {
            d.IsEditingLightRange = false;
            return;
        }
        d.LightRangeEditMin = Math.Round(d.ColorTempMin);
        d.LightRangeEditMax = Math.Round(d.ColorTempMax);
        d.IsEditingLightRange = true;
    }

    /// <summary>保存自定义量程：写进配置并立刻改写滑杆端点；和规格完全一致时按「没自定义」处理。</summary>
    [RelayCommand]
    private void SaveLightRange()
    {
        if (DrawerDevice is not { HasColorTemp: true } d || string.IsNullOrEmpty(d.Did)) return;

        var lo = MijiaDevice.ColorTempFloor;
        var hi = MijiaDevice.ColorTempCeiling;
        var min = (int)Math.Round(Math.Clamp(d.LightRangeEditMin, lo, hi));
        var max = (int)Math.Round(Math.Clamp(d.LightRangeEditMax, lo, hi));
        if (max - min < MijiaDevice.LightRangeGap) max = Math.Min(hi, min + MijiaDevice.LightRangeGap);
        if (max - min < 1)
        {
            Log($"{d.Name} 的色温量程太小，未保存");
            return;
        }

        var specMin = (int)Math.Round(d.SpecColorTempMin);
        var specMax = (int)Math.Round(d.SpecColorTempMax);
        if (min == specMin && max == specMax)
        {
            AppSettingsStore.ClearLightRange(d.Did);
            d.ApplyLightRangeOverride(null, null);
        }
        else
        {
            AppSettingsStore.SetLightRange(d.Did, min, max);
            d.ApplyLightRangeOverride(min, max);
        }

        d.IsEditingLightRange = false;
        Log($"{d.Name} 色温量程 {min}–{max}K");
    }

    /// <summary>删掉这盏灯的自定义量程，回到设备规格的上下限。</summary>
    [RelayCommand]
    private void ResetLightRange()
    {
        if (DrawerDevice is not { HasColorTemp: true } d) return;
        AppSettingsStore.ClearLightRange(d.Did);
        d.ApplyLightRangeOverride(null, null);
        d.LightRangeEditMin = Math.Round(d.SpecColorTempMin);
        d.LightRangeEditMax = Math.Round(d.SpecColorTempMax);
        d.IsEditingLightRange = false;
        Log($"{d.Name} 色温量程恢复为设备默认");
    }

    /// <summary>风扇连续档位滑杆：停 600ms 再提交一次，拖动过程不连发。</summary>
    private void ScheduleFanLevelCommit(MijiaDevice device)
    {
        _fanCommitTimer?.Dispose();
        _fanCommitTimer = DispatcherTimer.RunOnce(
            () => _ = CommitFanLevelAsync(device), TimeSpan.FromMilliseconds(600));
    }

    /// <summary>只有「量程式」档位走滑杆提交；有 value-list 的离散档由分段按钮直接下发。</summary>
    private async Task CommitFanLevelAsync(MijiaDevice device)
    {
        if (_client == null || !device.IsOnline) return;
        if (device.FanSchema is not { Valid: true } s || s.LevelPiid <= 0 || s.HasLevels) return;
        try
        {
            await _client.SetPropAsync(device.TargetDid, s.LevelSiid, s.LevelPiid, device.FanLevel);
            Log($"{device.Name} 风速 → {device.FanLevel}");
        }
        catch (Exception ex)
        {
            Log($"设置风扇风速失败：{ex.Message}");
        }
    }

    /// <summary>无级调速滑杆：同样 600ms 防抖，停手再下发。</summary>
    private void ScheduleFanSpeedCommit(MijiaDevice device)
    {
        _fanSpeedCommitTimer?.Dispose();
        _fanSpeedCommitTimer = DispatcherTimer.RunOnce(
            () => _ = CommitFanSpeedAsync(device), TimeSpan.FromMilliseconds(600));
    }

    private async Task CommitFanSpeedAsync(MijiaDevice device)
    {
        if (_client == null || !device.IsOnline) return;
        if (device.FanSchema is not { Valid: true } s || !s.HasSpeed) return;
        try
        {
            await _client.SetPropAsync(device.TargetDid, s.SpeedSiid, s.SpeedPiid, device.FanSpeed);
            Log($"{device.Name} 无级调速 → {device.FanSpeed}");
        }
        catch (Exception ex)
        {
            Log($"设置无级调速失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SetAcMode(int mode)
    {
        if (_client == null || DrawerDevice is not { IsAc: true } d || !d.IsOnline)
        {
            Log("空调离线或未选中，指令未下发");
            return;
        }
        if (d.AcSchema is not { Valid: true } s)
        {
            Log($"{d.Name} 缺少控制参数，模式未下发");
            return;
        }
        d.AcMode = mode;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetAcModeAsync(d.TargetDid, s.ModeSiid, s.ModePiid, mode);
            Log($"{d.Name} 模式 → {AcOptionLabel(s.Modes, mode, "模式")}");
        }
        catch (Exception ex)
        {
            Log($"设置空调模式失败：{ex.Message}");
        }
    }

    /// <summary>空调目标温度：步进器写进来的绝对值在这里校准（步长与范围取设备规格）并立即下发。</summary>
    private async Task ApplyAcTargetTempAsync(MijiaDevice d, double target)
    {
        if (_client == null || !d.IsAc || !d.IsOnline)
        {
            Log("空调离线或未选中，指令未下发");
            d.RefreshAcTargetTempInput();
            return;
        }
        if (d.AcSchema is not { Valid: true } s)
        {
            Log($"{d.Name} 缺少控制参数，温度未下发");
            d.RefreshAcTargetTempInput();
            return;
        }

        var step = s.TempStep > 0 ? s.TempStep : 1;
        var next = Math.Round(target / step) * step;
        next = Math.Clamp(next, s.TempMin, s.TempMax);
        if (Math.Abs(next - d.AcTargetTemp) < 0.001) return;

        var prev = d.AcTargetTemp;
        d.AcTargetTemp = next;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetAcTargetTempAsync(d.TargetDid, s.TempSiid, s.TempPiid, next, s);
            Log($"{d.Name} 温度 → {next:0.#}℃");
        }
        catch (Exception ex)
        {
            // 云端拒收就把显示拨回上一次的值，别停在设备从未采纳的位置
            d.AcTargetTemp = prev;
            d.RefreshAcTargetTempInput();
            Log($"设置空调温度失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SetAcFan(int level)
    {
        if (_client == null || DrawerDevice is not { IsAc: true } d || !d.IsOnline)
        {
            Log("空调离线或未选中，指令未下发");
            return;
        }
        if (d.AcSchema is not { Valid: true } s)
        {
            Log($"{d.Name} 缺少控制参数，风速未下发");
            return;
        }
        d.AcFanLevel = level;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetAcFanLevelAsync(d.TargetDid, s.FanSiid, s.FanPiid, level);
            Log($"{d.Name} 风速 → {AcOptionLabel(s.Fans, level, "档")}");
        }
        catch (Exception ex)
        {
            Log($"设置空调风速失败：{ex.Message}");
        }
    }

    // ===== 摄像头：画面走本地 go2rtc 桥接（云端只有属性，没有视频数据） =====

    private void ApplyCameraProbe(CameraProbeResult probe)
    {
        CameraBridgeHint = probe.Hint;
        CameraSecurityHint = probe.SecurityHint;
    }

    private async Task CopyTextAsync(string text)
    {
        try
        {
            var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (window?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
            else
                Log("写剪贴板失败：找不到主窗口");
        }
        catch (Exception ex)
        {
            Log($"写剪贴板失败：{ex.Message}");
        }
    }

    /// <summary>打开摄像头抽屉时探一次桥接状态，把结论和下一步显示在卡片里，用户不用点按钮才知道没配上。</summary>
    private async Task RefreshCameraBridgeAsync(MijiaDevice device)
    {
        var probe = await CameraBridge.ProbeAsync(device);
        ApplyCameraProbe(probe);
        Log($"{device.Name} 画面源自检：{probe.CodeText} · 流 {(probe.StreamName == null ? "未匹配" : "已匹配")}");
    }

    [RelayCommand]
    private async Task OpenCameraLive()
    {
        if (DrawerDevice is not { IsCamera: true } d) return;
        var probe = await CameraBridge.ProbeAsync(d);
        ApplyCameraProbe(probe);

        if (!probe.Ready)
        {
            // 配置/网关问题才把三步准备展开；只是没认到摄像头就别吓用户。
            IsCameraSetupExpanded = probe.Stage is CameraProbeStage.Config or CameraProbeStage.Gateway;
            Log($"{d.Name} 实时画面未打开：{probe.CodeText} {probe.Hint}");
            if (probe.Code == CameraProbeCode.StreamMissing)
            {
                CameraBridge.Open(CameraBridge.ConsoleUrl);
                CameraBridgeHint = "已打开画面源控制台：找到这台摄像机、添加一次后回来重试。";
            }
            return;
        }

        var name = probe.StreamName!;
        if (CameraBridge.Open(CameraBridge.PlayUrl(name)))
        {
            CameraBridgeHint = $"已打开 {name} 的实时画面（浏览器窗口）。一直黑屏的话多是 H.265 浏览器解不了，改用「播放器打开」。";
            Log($"{d.Name} 实时画面 → {name}");
        }
        else
        {
            CameraBridgeHint = $"调用浏览器失败，手动打开：{CameraBridge.PlayUrl(name)}";
            Log($"打开画面失败：{CameraBridge.PlayUrl(name)}");
        }
    }

    /// <summary>H.265 兜底：把 go2rtc 的 RTSP 交给本机 mpv / VLC / PotPlayer，不额外下载组件。</summary>
    [RelayCommand]
    private async Task OpenCameraWithPlayer()
    {
        if (DrawerDevice is not { IsCamera: true } d) return;
        var probe = await CameraBridge.ProbeAsync(d);
        ApplyCameraProbe(probe);
        if (!probe.Ready)
        {
            IsCameraSetupExpanded = probe.Stage is CameraProbeStage.Config or CameraProbeStage.Gateway;
            Log($"{d.Name} 播放器打开失败：{probe.CodeText} {probe.Hint}");
            return;
        }

        var name = probe.StreamName!;
        var url = CameraBridge.RtspUrl(name);
        if (CameraBridge.TryOpenPlayer(url, out var player, out var error))
        {
            CameraBridgeHint = $"已用 {player} 打开 {name} 的画面（H.265 也能放）。";
            Log($"{d.Name} 实时画面 → {player}（RTSP）");
            return;
        }

        CameraBridgeHint = error == null
            ? $"没找到本机播放器（mpv / VLC / PotPlayer），已复制 RTSP 地址：{url}。装上任意一个后，这个按钮会直接用它打开。"
            : $"调用播放器失败：{error}。已复制 RTSP 地址：{url}。";
        Log($"{d.Name} 播放器打开失败：{error ?? "未找到本机播放器"}");
        await CopyTextAsync(url);
    }

    /// <summary>把「只绑本机」的 go2rtc 配置片段放进剪贴板，避免整个局域网都能打开控制台。</summary>
    [RelayCommand]
    private async Task CopyCameraConfig()
    {
        await CopyTextAsync(CameraBridge.RecommendedConfig);
        CameraBridgeHint = "已复制推荐配置：粘贴到 go2rtc.yaml 顶层并重启，api 和 rtsp 就只对本机开放（控制台不再对整个局域网可见）。";
        CameraSecurityHint = "";
        Log("已复制 go2rtc 推荐配置");
    }

    /// <summary>自检结果只含阶段、错误码和是否匹配，不带账号、令牌、视频地址和设备 did。</summary>
    [RelayCommand]
    private async Task CopyCameraDiagnostic()
    {
        if (DrawerDevice is not { IsCamera: true } d) return;
        var probe = await CameraBridge.ProbeAsync(d);
        ApplyCameraProbe(probe);

        var lines = new[]
        {
            "MiHomeHub 画面源自检",
            $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"阶段：{probe.Stage} / {probe.Code}",
            $"画面源：{probe.BaseUrl}",
            $"仅本机：{(probe.LoopbackHost ? "是" : "否")}",
            $"设备型号：{d.Model}",
            $"匹配流：{(probe.StreamName == null ? "未匹配" : "已匹配")}",
            $"流数量：{probe.Streams.Count}",
            $"自动结论：{probe.Hint}"
        };
        await CopyTextAsync(string.Join(Environment.NewLine, lines));
        CameraBridgeHint = "已复制自检结果（不含账号、令牌、视频地址和设备 did），可直接贴给维护者排查。";
    }

    [RelayCommand]
    private void OpenGo2rtcConsole()
    {
        if (CameraBridge.Open(CameraBridge.ConsoleUrl))
        {
            CameraBridgeHint = "已打开画面源控制台（浏览器窗口）。";
            Log("打开画面源控制台");
        }
        else
        {
            CameraBridgeHint = $"调用浏览器失败，手动打开：{CameraBridge.ConsoleUrl}";
        }
    }

    [RelayCommand]
    private void ToggleCameraSetup() => IsCameraSetupExpanded = !IsCameraSetupExpanded;

    // ===== 中枢网关：自动化极客版（网关自托管的网页，端口固定 80） =====

    /// <summary>正在局域网扫描极客版网页，防止连点重复扫。</summary>
    private bool _findingHub;

    [RelayCommand]
    private void OpenHubGeek()
    {
        if (DrawerDevice is not { IsHub: true } d) return;
        var url = HubGeek.BuildUrl(HubGeekIp);
        if (url.Length == 0)
        {
            HubGeekHint = "还不知道网关 IP：点「自动查找」，或在上面手动填一次（米家 App 中枢功能页里能看到）。";
            Log($"{d.Name} 极客版未打开：缺少网关 IP");
            return;
        }

        if (HubGeek.Open(url))
        {
            HubGeekHint = $"已打开 {url}。登录码在米家 App 里现取（一次性、几分钟有效），同一个浏览器登录过就不用再输。";
            Log($"{d.Name} 极客版 → {url}");
        }
        else
        {
            HubGeekHint = $"调用浏览器失败，手动打开：{url}";
        }
    }

    [RelayCommand]
    private async Task FindHubGateway()
    {
        if (DrawerDevice is not { IsHub: true } d || _findingHub) return;

        _findingHub = true;
        HubGeekHint = "正在局域网里找极客版网页（约十几秒）…";
        try
        {
            var found = await HubGeek.FindAsync();
            if (found == null)
            {
                HubGeekHint = "这个网段里没扫到极客版网页。可以按米家 App 里显示的 IP 手动填一次，或确认电脑和网关在同一网络。";
                Log($"{d.Name} 极客版：局域网未找到");
                return;
            }

            HubGeekIp = found;
            HubGeekHint = $"已找到网关：{found}。点「打开极客版」即可，登录码去米家 App 里取。";
            Log($"{d.Name} 极客版：局域网找到 {found}");
        }
        finally
        {
            _findingHub = false;
        }
    }

    [RelayCommand]
    private void ToggleHubGeekHelp() => IsHubGeekHelpExpanded = !IsHubGeekHelpExpanded;

    [RelayCommand]
    private async Task RefreshCameraBridge()
    {
        if (DrawerDevice is not { IsCamera: true } d) return;
        await RefreshCameraBridgeAsync(d);
    }

    private static string AcOptionLabel(List<AcOptionItem> opts, int value, string fallback) =>
        opts.FirstOrDefault(o => o.Value == value)?.Label ?? $"{fallback}{value}";

    // ===== 风扇：档位 / 模式 / 摆风 =====

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetFanLevel(int level)
    {
        if (_client == null || DrawerDevice is not { IsFan: true } d || !d.IsOnline)
        {
            Log("风扇离线或未选中，指令未下发");
            return;
        }
        if (d.FanSchema is not { Valid: true } s || s.LevelPiid <= 0)
        {
            Log($"{d.Name} 缺少档位参数，风速未下发");
            return;
        }
        d.FanLevel = level;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetPropAsync(d.TargetDid, s.LevelSiid, s.LevelPiid, level);
            Log($"{d.Name} 风速 → {AcOptionLabel(s.Levels, level, "档")}");
        }
        catch (Exception ex)
        {
            Log($"设置风扇风速失败：{ex.Message}");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetFanMode(int mode)
    {
        if (_client == null || DrawerDevice is not { IsFan: true } d || !d.IsOnline) return;
        if (d.FanSchema is not { Valid: true } s || s.ModePiid <= 0) return;
        d.FanMode = mode;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetPropAsync(d.TargetDid, s.ModeSiid, s.ModePiid, mode);
            Log($"{d.Name} 模式 → {AcOptionLabel(s.Modes, mode, "模式")}");
        }
        catch (Exception ex)
        {
            Log($"设置风扇模式失败：{ex.Message}");
        }
    }

    /// <summary>摆风开关：按钮先翻本地态（TwoWay 绑定），失败再翻回来。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleFanSwing(string? axis)
    {
        if (_client == null || DrawerDevice is not { IsFan: true } d || !d.IsOnline)
        {
            Log("风扇离线或未选中，指令未下发");
            return;
        }
        if (d.FanSchema is not { Valid: true } s) return;

        var vertical = string.Equals(axis, "v", StringComparison.OrdinalIgnoreCase);
        if (vertical ? !s.HasSwingV : !s.HasSwingH) return;
        var siid = vertical ? s.SwingVSiid : s.SwingHSiid;
        var piid = vertical ? s.SwingVPiid : s.SwingHPiid;
        var value = vertical ? d.SwingVOn : d.SwingHOn;

        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetPropAsync(d.TargetDid, siid, piid, value);
            Log($"{d.Name} {(vertical ? "上下" : "左右")}摆风 → {(value ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            if (vertical) d.SwingVOn = !value;
            else d.SwingHOn = !value;
            Log($"设置摆风失败：{ex.Message}");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task CurtainOpenAsync() => SendCurtainMotorAsync(MijiaClient.CurtainOpen, "打开");

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task CurtainPauseAsync() => SendCurtainMotorAsync(MijiaClient.CurtainPause, "暂停");

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task CurtainCloseAsync() => SendCurtainMotorAsync(MijiaClient.CurtainClose, "关闭");

    /// <summary>窗帘电机三键共用：0 停 / 1 开 / 2 关，同一设备由 IsBusy 拦连点。</summary>
    private async Task SendCurtainMotorAsync(int motor, string label)
    {
        if (_client == null || DrawerDevice is not { IsCurtain: true } d || !d.IsOnline)
        {
            Log("窗帘离线或未选中，指令未下发");
            return;
        }
        if (d.IsBusy) return;
        d.IsBusy = true;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetCurtainMotorAsync(d.TargetDid, motor);
            Log($"{d.Name} 窗帘 → {label}");
            // 开/关会让开合度持续变化；稍等再读一次，把抽屉里的百分比跟上
            if (motor != MijiaClient.CurtainPause)
            {
                await Task.Delay(1200);
                var pos = await _client.GetCurtainPositionAsync(d.TargetDid);
                if (pos.HasValue) d.CurtainPosition = pos.Value;
            }
            else
            {
                var pos = await _client.GetCurtainPositionAsync(d.TargetDid);
                if (pos.HasValue) d.CurtainPosition = pos.Value;
            }
        }
        catch (Exception ex)
        {
            Log($"窗帘控制失败：{ex.Message}");
        }
        finally
        {
            d.IsBusy = false;
            UpdateStats();
        }
    }
}
