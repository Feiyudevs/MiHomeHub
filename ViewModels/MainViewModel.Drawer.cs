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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;
using MiHome.Services;
using QRCoder;

namespace MiHome.ViewModels;

public partial class MainViewModel : ObservableObject
{
    /// <summary>详情抽屉里灯亮度/色温的防抖提交定时器（拖动过程中不连发请求）。</summary>
    private IDisposable? _lightCommitTimer;

    /// <summary>当前详情设备及其属性订阅，用于把亮度/色温变化提交回设备。</summary>
    private MijiaDevice? _detailDevice;
    private PropertyChangedEventHandler? _detailHandler;

    /// <summary>正在回填详情值，期间忽略属性变化，避免把刚读到的值又当成用户改动提交一次。</summary>
    private bool _loadingDetail;

    /// <summary>高级设置：抽屉侧边栏只留常用控制卡，其余规格属性（按 MIoT 服务分组）与无入参动作全部收进这里。</summary>
    public ObservableCollection<SpecGroupVm> DrawerSpecAdvancedGroups { get; } = new();
    public ObservableCollection<SpecActionVm> DrawerSpecAdvancedActions { get; } = new();

    [ObservableProperty] private bool _hasAdvancedPanel;
    [ObservableProperty] private bool _hasAdvancedActions;
    [ObservableProperty] private bool _isAdvancedExpanded;

    /// <summary>高级设置区是否有内容（属性组或动作任一存在）。</summary>
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
        DrawerDevice = device;
        IsDrawerOpen = true;
        BuildSpecPanel(device);
        AttachDrawerDetail(device);
    }

    /// <summary>抽屉关闭时释放面板 VM 与拖拽/确认定时器，避免后台轮询继续写隐藏控件。</summary>
    private void ClearSpecPanel()
    {
        foreach (var timer in _specCommitTimers.Values) timer.Dispose();
        _specCommitTimers.Clear();
        _actionConfirmTimer?.Dispose();
        _actionConfirmTimer = null;
        DrawerSpecAdvancedGroups.Clear();
        DrawerSpecAdvancedActions.Clear();
        HasAdvancedPanel = false;
        HasAdvancedActions = false;
        IsAdvancedExpanded = false;
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
    /// 抽屉打开时订阅当前设备的亮度/色温变化（滑动防抖后提交），并异步回填设备可控属性。
    /// 用同一实例反复打开也会重新订阅，避免 ObservableProperty 相同值不触发而漏刷新。
    /// </summary>
    private void AttachDrawerDetail(MijiaDevice device)
    {
        if (_detailDevice != null && _detailHandler != null)
            _detailDevice.PropertyChanged -= _detailHandler;
        _detailDevice = device;
        _detailHandler = (_, e) =>
        {
            if (_loadingDetail || _applyingStates) return;
            if (e.PropertyName is nameof(MijiaDevice.Brightness) or nameof(MijiaDevice.ColorTemp))
                ScheduleLightCommit(device);
            else if (e.PropertyName is nameof(MijiaDevice.SpeakerVolume))
                ScheduleSpeakerVolumeCommit(device);
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
                var st = await client.GetLightStateAsync(device.TargetDid);
                if (st != null)
                {
                    device.Brightness = st.Brightness;
                    device.ColorTemp = st.ColorTemp;
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

    /// <summary>按规格把抽屉里的通用属性/通道/电源读一遍，填进对应的行。</summary>
    private async Task LoadSpecPanelValuesAsync(MijiaDevice device)
    {
        if (_client is not { } client || !device.IsOnline) return;
        var query = new List<(string Did, int Siid, int Piid)>();
        foreach (var vm in DrawerSpecAdvancedGroups.SelectMany(g => g.Items))
            query.Add((device.TargetDid, vm.Def.Siid, vm.Def.Piid));
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
        "temperature", "relative-humidity", "humidity", "pm2.5", "pm10", "illuminance",
        "battery-level", "battery", "co2", "tvoc", "tds-in", "air-quality", "water-level",
        "power-consumption", "voltage", "electric-current", "motion-detected",
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
        var spec = device.Spec;
        if (spec == null)
        {
            HasAdvancedPanel = false;
            HasAdvancedActions = false;
            return;
        }

        // 按 siid 分组：多路面板的规格里会有多个同名服务（Light 1/2/3、Curtain 1/2），
        // 按描述分组会把它们并成一张卡，出现多行同名的滑杆。
        var groups = spec.Properties
            .Where(p => ShouldShowProp(device, p))
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
    private void ScheduleLightCommit(MijiaDevice device)
    {
        _lightCommitTimer?.Dispose();
        _lightCommitTimer = DispatcherTimer.RunOnce(
            () => _ = CommitLightAsync(device), TimeSpan.FromMilliseconds(600));
    }

    private async Task CommitLightAsync(MijiaDevice device)
    {
        if (_client == null || !device.IsOnline) return;
        try
        {
            await _client.SetLightBrightnessAsync(device.TargetDid, device.Brightness);
            await _client.SetLightColorTempAsync(device.TargetDid, device.ColorTemp);
            Log($"{device.Name} 亮度 {device.Brightness} · 色温 {device.ColorTemp}K");
        }
        catch (Exception ex)
        {
            Log($"灯光设置失败：{ex.Message}");
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

    [RelayCommand]
    private async Task AcTempUp() => await AdjustAcTempAsync(1);

    [RelayCommand]
    private async Task AcTempDown() => await AdjustAcTempAsync(-1);

    /// <summary>空调目标温度加减（步长与范围取自设备规格），改完立即下发。</summary>
    private async Task AdjustAcTempAsync(double dir)
    {
        if (_client == null || DrawerDevice is not { IsAc: true } d || !d.IsOnline)
        {
            Log("空调离线或未选中，指令未下发");
            return;
        }
        if (d.AcSchema is not { Valid: true } s)
        {
            Log($"{d.Name} 缺少控制参数，温度未下发");
            return;
        }
        var next = Math.Round((d.AcTargetTemp + s.TempStep * dir) / s.TempStep) * s.TempStep;
        next = Math.Clamp(next, s.TempMin, s.TempMax);
        d.AcTargetTemp = next;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetAcTargetTempAsync(d.TargetDid, s.TempSiid, s.TempPiid, next, s);
            Log($"{d.Name} 温度 → {next:0.#}℃");
        }
        catch (Exception ex)
        {
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

    private static string AcOptionLabel(List<AcOptionItem> opts, int value, string fallback) =>
        opts.FirstOrDefault(o => o.Value == value)?.Label ?? $"{fallback}{value}";

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
