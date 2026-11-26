// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Models;
using MiHomeHub.Services;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 首页「环境与能耗」趋势卡的数据侧。
///
/// 曲线数据不来自米家云（接口只给当前读数），而是本地每 5 分钟采一格攒出来的：
/// 温度/湿度取环境概览同一口径，功率取所有在线计量设备（插座等 electric-power）瞬时瓦数之和。
/// 只有启动之后的数据，不伪造过去——刚装好时卡片会明说「正在采样」。
/// </summary>
public partial class MainViewModel
{
    /// <summary>采样间隔：与 <see cref="EnvHistoryStore.SampleMinutes"/> 保持一致。</summary>
    private const int TrendStepMinutes = 5;

    private static readonly TimeSpan TrendWindow = TimeSpan.FromHours(24);

    /// <summary>少于这么多格（约半小时）就先不画曲线：几个点连成一条横跨全宽的直线，比空着更误导。</summary>
    private const int WarmingPoints = 6;

    /// <summary>攒够这么长的跨度才回到「近 24 小时」的口径，之前横轴只画已有的一段。</summary>
    private static readonly TimeSpan FullCoverage = TimeSpan.FromHours(20);

    private readonly EnvHistoryStore _envHistory = EnvHistoryStore.ForAppData();

    /// <summary>云端「电量统计」：did → 今天逐小时用电。规格里没有瞬时功率的设备（空调）靠它。</summary>
    private readonly Dictionary<string, DeviceEnergy> _cloudEnergy = new(StringComparer.Ordinal);
    private DateTime _cloudEnergyAt = DateTime.MinValue;
    private bool _cloudEnergyBusy;

    /// <summary>上次写进 energy-days.log 的内容，内容没变就不重复写。</summary>
    private string _diagDaysSig = "";

    /// <summary>环境概览当前读数（由 <see cref="RecomputeEnvironment"/> 回填），采样与卡片共用同一份。</summary>
    private double? _liveTemp;
    private double? _liveHumidity;

    /// <summary>可选项的设备签名：只有设备增删或改名才重建菜单，平时只翻勾。</summary>
    private string _sourceSignature = "";

    /// <summary>建列表时正在复位失效的来源，避免「改属性 → 又重算一整轮」的递归。</summary>
    private bool _resettingSources;

    [ObservableProperty] private bool _showEnvTrend;
    [ObservableProperty] private bool _hasMetering;

    /// <summary>家里有没有能报温度 / 湿度的设备：没有就不铺那张卡，三类都没有时整段不出现。</summary>
    [ObservableProperty] private bool _hasTempReading;
    [ObservableProperty] private bool _hasHumReading;

    /// <summary>整个区块去留变化时，首拉骨架与「单卡并排」的显示条件跟着翻转。</summary>
    partial void OnShowEnvTrendChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTrendSkeleton));
        RaiseTrendRowFlags();
    }

    /// <summary>当前实际存在的趋势卡数量：三张里只算真有设备能提供数据的。</summary>
    public int TrendCardCount => IsTrendPreview
        ? 3
        : (HasTempReading ? 1 : 0) + (HasHumReading ? 1 : 0) + (HasMetering ? 1 : 0);

    /// <summary>只剩一张趋势卡、又有快捷场景时，两者并排占一行，右侧场景可以铺两行。</summary>
    public bool ShowTrendQuickRow =>
        ShowEnvTrend && !ShowTrendSkeleton && TrendCardCount == 1 && ShowQuickScenes;

    /// <summary>趋势卡独占一行（不止一张，或下面没有快捷场景可并排）。</summary>
    public bool ShowTrendSolo => ShowEnvTrend && !ShowTrendQuickRow;

    /// <summary>快捷场景正常铺在趋势区下方；并到同一行后交给合并区渲染，这里让位。</summary>
    public bool ShowQuickScenesSolo => ShowQuickScenes && !ShowTrendQuickRow;

    private void RaiseTrendRowFlags()
    {
        OnPropertyChanged(nameof(TrendCardCount));
        OnPropertyChanged(nameof(ShowTrendQuickRow));
        OnPropertyChanged(nameof(ShowTrendSolo));
        OnPropertyChanged(nameof(ShowQuickScenesSolo));
    }

    partial void OnHasTempReadingChanged(bool value) => RaiseTrendRowFlags();
    partial void OnHasHumReadingChanged(bool value) => RaiseTrendRowFlags();
    partial void OnHasMeteringChanged(bool value) => RaiseTrendRowFlags();
    partial void OnIsTrendPreviewChanged(bool value) => RaiseTrendRowFlags();

    /// <summary>默认展示真实采样；双击标题行切到演示数据（供预览样式），再双击还原。</summary>
    [ObservableProperty] private bool _isTrendPreview;

    [ObservableProperty] private IReadOnlyList<double> _tempTrendSeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _humTrendSeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _powerTrendSeries = Array.Empty<double>();

    [ObservableProperty] private string _tempTrendValue = "—";
    [ObservableProperty] private string _tempTrendNote = "";
    [ObservableProperty] private string _tempTrendDelta = "";
    [ObservableProperty] private string _tempTrendLow = "—";
    [ObservableProperty] private string _tempTrendHigh = "—";
    [ObservableProperty] private bool _hasTempDelta;

    [ObservableProperty] private string _humTrendValue = "—";
    [ObservableProperty] private string _humTrendNote = "";
    [ObservableProperty] private string _humTrendDelta = "";
    [ObservableProperty] private string _humTrendLow = "—";
    [ObservableProperty] private string _humTrendHigh = "—";
    [ObservableProperty] private bool _hasHumDelta;

    [ObservableProperty] private string _powerTrendValue = "—";
    [ObservableProperty] private string _powerTrendNow = "";
    [ObservableProperty] private string _powerTrendDelta = "";
    [ObservableProperty] private string _powerTrendLow = "—";
    [ObservableProperty] private string _powerTrendHigh = "—";
    [ObservableProperty] private bool _hasPowerDelta;
    [ObservableProperty] private int _powerTrendFocus = -1;

    // —— 卡片最底下那行「最低 … 最高 …」搬到了大数字右侧：两段合成一句，卡片底部整块留给曲线 ——

    /// <summary>温度卡右侧小字：最低 12.9℃ · 最高 26℃。</summary>
    public string TempTrendRange => $"最低 {TempTrendLow} · 最高 {TempTrendHigh}";

    /// <summary>湿度卡右侧小字：最低 31% · 最高 55%。</summary>
    public string HumTrendRange => $"最低 {HumTrendLow} · 最高 {HumTrendHigh}";

    /// <summary>用电卡右侧小字：Low/High 本身已带「待机 / 峰值」「最少 / 最多」，直接拼。</summary>
    public string PowerTrendRange => $"{PowerTrendLow} · {PowerTrendHigh}";

    // 高低值随时会重算，合成句跟着刷新（否则右侧那行会停在旧数字上）
    partial void OnTempTrendLowChanged(string value) => OnPropertyChanged(nameof(TempTrendRange));
    partial void OnTempTrendHighChanged(string value) => OnPropertyChanged(nameof(TempTrendRange));
    partial void OnHumTrendLowChanged(string value) => OnPropertyChanged(nameof(HumTrendRange));
    partial void OnHumTrendHighChanged(string value) => OnPropertyChanged(nameof(HumTrendRange));
    partial void OnPowerTrendLowChanged(string value) => OnPropertyChanged(nameof(PowerTrendRange));
    partial void OnPowerTrendHighChanged(string value) => OnPropertyChanged(nameof(PowerTrendRange));

    // —— 用电卡的范围：今日逐小时，或近 7 / 30 天逐日（历史靠云端电量统计 + 本地采样拼） ——

    /// <summary>卡片大标题：今日用电 / 近 7 天用电。</summary>
    [ObservableProperty] private string _powerTitle = "今日用电";

    /// <summary>柱子单位：逐小时是 W，逐日是度。</summary>
    [ObservableProperty] private string _powerChartUnit = "W";

    /// <summary>柱子的横轴是一天还是一小时。</summary>
    [ObservableProperty] private bool _powerIsDailyBars;

    [ObservableProperty] private IReadOnlyList<OptionItem> _powerRangeOptions = new List<OptionItem>
    {
        new() { Label = "今日", Value = "0" },
        new() { Label = "7 天", Value = "7" },
        new() { Label = "30 天", Value = "30" }
    };

    [ObservableProperty] private string _selectedPowerRange = AppSettingsStore.GetPowerRange().ToString();

    // —— 数据成熟度：刚装好时先不硬画 24 小时，曲线区改放一句说明 ——

    [ObservableProperty] private bool _showTempChart;
    [ObservableProperty] private bool _showHumChart;
    [ObservableProperty] private bool _showPowerChart;
    [ObservableProperty] private string _tempTrendPlaceholder = "";
    [ObservableProperty] private string _humTrendPlaceholder = "";
    [ObservableProperty] private string _powerTrendPlaceholder = "";

    /// <summary>标题右侧的口径说明：攒满一天写「近 24 小时」，早期写实际跨度。</summary>
    [ObservableProperty] private string _envTrendCaption = "每 5 分钟采样";

    // —— 卡片右上角的数据来源下拉：走程序内自绘的 OptionPicker，选一台设备看它自己的那条曲线 ——

    [ObservableProperty] private IReadOnlyList<OptionItem> _tempSources = Array.Empty<OptionItem>();
    [ObservableProperty] private IReadOnlyList<OptionItem> _humSources = Array.Empty<OptionItem>();
    [ObservableProperty] private IReadOnlyList<OptionItem> _powerSources = Array.Empty<OptionItem>();

    /// <summary>三张卡各自选中的数据来源 did；空串 = 全屋口径（温湿度跟随环境概览、用电取叠加）。</summary>
    [ObservableProperty] private string _selectedTempSource = AppSettingsStore.GetTrendSource("temp");
    [ObservableProperty] private string _selectedHumSource = AppSettingsStore.GetTrendSource("hum");
    [ObservableProperty] private string _selectedPowerSource = AppSettingsStore.GetTrendSource("power");

    /// <summary>只有一台能选时才值得挂下拉，否则「全屋」和那台设备是同一个数。</summary>
    [ObservableProperty] private bool _canPickTempSource;
    [ObservableProperty] private bool _canPickHumSource;
    [ObservableProperty] private bool _canPickPowerSource;

    /// <summary>
    /// 轮询拿到温湿度后调一次：写进本地历史并按最新数据刷新三张卡。
    /// 间隔不足 5 分钟的重复调用只会更新最后一格，不会把曲线灌出锯齿。
    /// </summary>
    private void SampleTrendNow()
    {
        var metered = _allDevices.Where(d => d.PowerWatts.HasValue || d.EnergyKwh.HasValue).ToList();
        HasMetering = metered.Count > 0;

        // 离线设备的功率是云的陈旧值，宁可不采也不要把「关机了还在耗电」画进曲线
        // 只有 electric-power（瞬时瓦数）能进全屋叠加；空调那种只报累计电量的按差值另算
        var onlineMetered = metered.Where(d => d.IsOnline && d.PowerWatts.HasValue).ToList();
        double? power = onlineMetered.Count > 0
            ? onlineMetered.Sum(d => d.PowerWatts!.Value)
            : null;

        // 每台设备再单独记一份：卡上切到某台设备时，画的就是它自己的历史
        var byDevice = new Dictionary<string, EnvReading>(StringComparer.Ordinal);
        foreach (var d in _allDevices)
        {
            if (!d.IsOnline) continue; // 离线读数同样是陈旧值
            var t = d.TemperatureC;
            var h = d.Humidity;
            var w = d.PowerWatts;
            var e = d.EnergyKwh;
            if (t is null && h is null && w is null && e is null) continue;
            byDevice[d.TargetDid] = new EnvReading(t, h, w, e);
        }

        _envHistory.Sample(DateTime.Now, _liveTemp, _liveHumidity, power, byDevice);
        RefreshTrendCards();
    }

    /// <summary>
    /// 拉云端「电量统计」（米家 App 里那张用电统计表）：规格解析完成后先来一次，之后每 10 分钟跟着轮询刷新。
    /// 只对规格里有 electric-power / power-consumption 的设备试，别的设备本来就没有统计表。
    /// </summary>
    private async Task RefreshEnergyStatsAsync(bool force = false)
    {
        if (_client is not { } client || _cloudEnergyBusy) return;
        if (!force && (DateTime.Now - _cloudEnergyAt).TotalMinutes < 10) return;

        _cloudEnergyBusy = true;
        try
        {
            var targets = _allDevices
                .Where(d => d.IsOnline && EnergyStatsService.MightHaveStats(d))
                .ToList();

            foreach (var d in targets)
            {
                DeviceEnergy? energy;
                try { energy = await EnergyStatsService.FetchTodayAsync(client, d); }
                catch { energy = null; }

                if (energy is not null) _cloudEnergy[d.TargetDid] = energy;
                else _cloudEnergy.Remove(d.TargetDid);
            }

            _cloudEnergyAt = DateTime.Now;
            RefreshTrendCards();
        }
        finally
        {
            _cloudEnergyBusy = false;
        }
    }

    /// <summary>重算三张趋势卡的文字与序列；没有历史时序列为空、数值退回当前读数。</summary>
    public void RefreshTrendCards()
    {
        var now = DateTime.Now;
        // 每类卡片只在真有设备能提供这类读数时才存在，没有就不拿「未发现传感器」的空壳占版面
        HasTempReading = _allDevices.Any(d => d.TemperatureC.HasValue);
        HasHumReading = _allDevices.Any(d => d.Humidity.HasValue);
        ShowEnvTrend = IsTrendPreview || HasTempReading || HasHumReading || HasMetering;

        if (IsTrendPreview)
        {
            ApplyPreviewData();
            return;
        }

        RebuildTrendSources();

        // 选了某台设备就只读它自己的那份；没选就还是全屋口径（温湿度跟随环境概览、用电取叠加）。
        // 刚切到某台设备、它自己被采到的格子还不够时，切换之前那一段沿用全屋曲线，
        // 免得一切换整条线就空掉——旧数据不剔除，新的房间从切换那一刻接着画。
        var tempPick = PickFor(p => p.Temp, r => r.Temp, SelectedTempSource,
            _envHistory.FirstAt(SelectedTempSource, r => r.Temp));
        var humPick = PickFor(p => p.Humidity, r => r.Humidity, SelectedHumSource,
            _envHistory.FirstAt(SelectedHumSource, r => r.Humidity));

        double? tempNow = SourceNowValue(SelectedTempSource, d => d.TemperatureC, _liveTemp);
        double? humNow = SourceNowValue(SelectedHumSource, d => d.Humidity, _liveHumidity);

        var tempCoverage = _envHistory.Coverage(tempPick, now, TrendWindow);
        var humCoverage = _envHistory.Coverage(humPick, now, TrendWindow);
        var tempWindow = PickWindow(tempCoverage);
        var humWindow = PickWindow(humCoverage);

        // 温度
        var tempPlaceholder = PlaceholderFor(tempNow, tempCoverage, "未发现温度传感器", "正在采样 · 攒到半小时后出曲线");
        ShowTempChart = tempPlaceholder.Length == 0;
        TempTrendPlaceholder = tempPlaceholder;

        var temps = ShowTempChart
            ? _envHistory.Resample(tempPick, now, tempWindow.Window, TimeSpan.FromMinutes(TrendStepMinutes))
            : Array.Empty<double>();
        TempTrendSeries = temps;
        TempTrendValue = tempNow is { } tn ? $"{tn:0.#}" : "—";
        TempTrendNote = TempComfortOf(tempNow);
        TempTrendLow = MinText(temps, "℃");
        TempTrendHigh = MaxText(temps, "℃");

        // 「较昨日」只在真攒满一天时才有意义；早期窗口里第一格不是昨天同一时刻
        double? tempDelta = tempWindow.Full && tempNow is { } t && temps.Length > 0 && !double.IsNaN(temps[0])
            ? t - temps[0]
            : null;
        TempTrendDelta = tempDelta is { } dt ? $"较昨日 {dt:+0.0;-0.0;0.0}℃" : "";
        HasTempDelta = tempDelta.HasValue;

        // 湿度
        var humPlaceholder = PlaceholderFor(humNow, humCoverage, "未发现湿度传感器", "正在采样 · 攒到半小时后出曲线");
        ShowHumChart = humPlaceholder.Length == 0;
        HumTrendPlaceholder = humPlaceholder;

        var hums = ShowHumChart
            ? _envHistory.Resample(humPick, now, humWindow.Window, TimeSpan.FromMinutes(TrendStepMinutes))
            : Array.Empty<double>();
        HumTrendSeries = hums;
        HumTrendValue = humNow is { } hn ? $"{hn:0}" : "—";
        HumTrendNote = HumComfortOf(humNow);
        HumTrendLow = MinText(hums, "%");
        HumTrendHigh = MaxText(hums, "%");

        double? humDelta = humWindow.Full && humNow is { } h && hums.Length > 0 && !double.IsNaN(hums[0])
            ? h - hums[0]
            : null;
        HumTrendDelta = humDelta is { } dh ? $"较昨日 {dh:+0;-0;0}%" : "";
        HasHumDelta = humDelta.HasValue;

        // 标题口径：任一曲线已经攒满一天就按 24 小时说，否则说实际跨度
        if (tempWindow.Full || humWindow.Full)
            EnvTrendCaption = "近 24 小时 · 每 5 分钟采样";
        else if (ShowTempChart || ShowHumChart)
            EnvTrendCaption = $"近 {SpanText(ShowTempChart ? tempWindow.Window : humWindow.Window)} · 每 5 分钟采样";
        else if (HasTempReading || HasHumReading)
            EnvTrendCaption = "正在积累 · 每 5 分钟采样";
        else
            EnvTrendCaption = ""; // 只剩用电卡时，口径由用电卡自己的范围和单位说明

        RefreshPower(now);

        // 云端电量统计可能补出「只报累计电量」的设备，用电卡到这一步才算真正定下来
        ShowEnvTrend = IsTrendPreview || HasTempReading || HasHumReading || HasMetering;
    }

    /// <summary>
    /// 横轴窗口：攒满一天就用 24 小时；早期贴合实测跨度（至少铺 30 分钟），
    /// 免得几个点被拉成一条横跨全天的假长线、缩在右端成一个钩子。
    /// </summary>
    private static (TimeSpan Window, bool Full) PickWindow((int Count, TimeSpan Span) coverage)
    {
        if (coverage.Span >= FullCoverage) return (TrendWindow, true);

        var minutes = Math.Max(30, Math.Ceiling(coverage.Span.TotalMinutes / TrendStepMinutes) * TrendStepMinutes);
        return (TimeSpan.FromMinutes(minutes), false);
    }

    /// <summary>曲线区该画图还是该说一句话：没传感器说明没传感器，格子不够就说明还在攒。</summary>
    private static string PlaceholderFor(double? live, (int Count, TimeSpan Span) coverage,
        string missingText, string warmingText)
    {
        if (live is null) return missingText;
        if (coverage.Count < WarmingPoints) return warmingText;
        return "";
    }

    /// <summary>窗口长度的人话：「30 分钟」「1 小时」「3 小时」。</summary>
    private static string SpanText(TimeSpan window)
    {
        if (window < TimeSpan.FromHours(1))
            return $"{Math.Max(5, (int)Math.Round(window.TotalMinutes))} 分钟";
        if (window < TimeSpan.FromHours(2)) return "1 小时";
        return $"{Math.Round(window.TotalHours):0} 小时";
    }

    /// <summary>
    /// 刷新三张卡的来源下拉：温度、湿度各列出有该读数的设备，用电多一项「全部设备（叠加）」。
    /// 设备没变就不重建列表——每拍重建会让正开着的下拉重新播一遍展开动画。
    /// </summary>
    private void RebuildTrendSources()
    {
        var tempDevices = SortedSourceDevices(d => d.TemperatureC.HasValue);
        var humDevices = SortedSourceDevices(d => d.Humidity.HasValue);
        var powerDevices = SortedSourceDevices(d => d.PowerWatts.HasValue || d.EnergyKwh.HasValue);

        var signature = string.Join("|",
            string.Join(",", tempDevices.Select(SignatureOf)),
            string.Join(",", humDevices.Select(SignatureOf)),
            string.Join(",", powerDevices.Select(SignatureOf)));

        if (signature == _sourceSignature) return;
        _sourceSignature = signature;

        // 选中的设备被撤掉或换了家庭，直接落到当前正在供数的那台（温湿度都不再保留「全屋」项）；
        // 这一段只写配置，不触发整卡重算
        _resettingSources = true;
        try
        {
            SelectedTempSource = ValidOrDefault(SelectedTempSource, tempDevices);
            SelectedHumSource = ValidOrDefault(SelectedHumSource, humDevices);
            SelectedPowerSource = StillThere(SelectedPowerSource, powerDevices);
        }
        finally
        {
            _resettingSources = false;
        }

        TempSources = BuildSources(tempDevices);
        HumSources = BuildSources(humDevices);
        PowerSources = BuildSources(powerDevices, "全部设备（叠加）");

        // 有可选设备就挂下拉：一张卡至少要能说清「这个数是哪来的」，只一台设备时也能点开看一眼
        CanPickTempSource = tempDevices.Count > 0;
        CanPickHumSource = humDevices.Count > 0;
        CanPickPowerSource = powerDevices.Count > 0;
    }

    private List<MijiaDevice> SortedSourceDevices(Func<MijiaDevice, bool> has) =>
        _allDevices.Where(has)
            .OrderBy(d => d.RoomName, StringComparer.CurrentCulture)
            .ThenBy(d => d.Name, StringComparer.CurrentCulture)
            .ToList();

    private static string SignatureOf(MijiaDevice d) => $"{d.TargetDid}:{d.RoomName}:{d.Name}";

    private static string StillThere(string did, IReadOnlyList<MijiaDevice> devices) =>
        did.Length > 0 && !devices.Any(d => d.TargetDid == did) ? "" : did;

    /// <summary>
    /// 温湿度卡的来源：选中那台还在就保留，否则落到「现在正在供数的那台」——
    /// 优先环境概览用的那台（在线、温湿度都有），再退到列表里第一台。
    /// 这样下拉里没有「全屋默认」这一项时，默认选中的就是卡片当前数字的真实出处。
    /// </summary>
    private string ValidOrDefault(string did, IReadOnlyList<MijiaDevice> devices)
    {
        if (did.Length > 0 && devices.Any(d => d.TargetDid == did)) return did;

        var envSource = _allDevices.FirstOrDefault(d => d.IsOnline && d.TemperatureC.HasValue && d.Humidity.HasValue);
        if (envSource is not null && devices.Any(d => d.TargetDid == envSource.TargetDid))
            return envSource.TargetDid;

        return devices.FirstOrDefault()?.TargetDid ?? "";
    }

    /// <summary>下拉逐台列出设备：房间做行尾弱色，名称做行主文字。</summary>
    private static IReadOnlyList<OptionItem> BuildSources(IReadOnlyList<MijiaDevice> devices)
    {
        var list = new List<OptionItem>(devices.Count);

        foreach (var d in devices)
            list.Add(new OptionItem
            {
                Label = d.Name,
                Value = d.TargetDid,
                Meta = string.IsNullOrWhiteSpace(d.RoomName) ? null : d.RoomName
            });

        return list;
    }

    /// <summary>用电卡多一项「全部设备（叠加）」：把全屋各表加起来，其余按台列出。</summary>
    private static IReadOnlyList<OptionItem> BuildSources(IReadOnlyList<MijiaDevice> devices, string allTitle)
    {
        var list = new List<OptionItem>(devices.Count + 1)
        {
            new() { Label = allTitle, Value = "" }
        };

        foreach (var d in devices)
            list.Add(new OptionItem
            {
                Label = d.Name,
                Value = d.TargetDid,
                Meta = string.IsNullOrWhiteSpace(d.RoomName) ? null : d.RoomName
            });

        return list;
    }

    partial void OnSelectedTempSourceChanged(string value) => OnSourceChanged("temp", value);
    partial void OnSelectedHumSourceChanged(string value) => OnSourceChanged("hum", value);
    partial void OnSelectedPowerSourceChanged(string value) => OnSourceChanged("power", value);

    /// <summary>下拉里选了一台：写回配置并按新口径重画；建列表期间的复位不重算。</summary>
    private void OnSourceChanged(string key, string did)
    {
        AppSettingsStore.SetTrendSource(key, did);
        if (_resettingSources) return;
        RefreshTrendCards();
    }

    /// <summary>
    /// 取数函数：没选设备时读全屋那一项；选了就读这台设备自己的那一项，
    /// 但这台设备被采到之前的那一段沿用全屋口径——换房间不会让已经画出来的曲线整条消失，
    /// 它开始上报以后才完全按它自己的读数走。
    /// </summary>
    private static Func<EnvPoint, double?> PickFor(
        Func<EnvPoint, double?> all, Func<EnvReading, double?> field, string did, DateTime? firstAt)
    {
        if (did.Length == 0) return all;

        return p =>
        {
            // 这台设备还没开始上报（或本来就没这一项）：接着画切换前那整条全屋曲线
            if (firstAt is not { } start || p.Time < start) return all(p);

            if (p.ByDevice is { } by && by.TryGetValue(did, out var reading) && field(reading) is { } value)
                return value;

            // 已经在报它自己了，这一格它没数就是没数，不拿全屋值凑
            return null;
        };
    }

    /// <summary>卡片主读数的当前值：没选设备就用全屋口径的值，选了就取那台设备的实时读数。</summary>
    private double? SourceNowValue(string did, Func<MijiaDevice, double?> read, double? fallback)
    {
        if (did.Length == 0) return fallback;
        var device = _allDevices.FirstOrDefault(d => d.TargetDid == did);
        return device is null ? fallback : read(device);
    }

    /// <summary>标题行双击：真实 ⇄ 演示数据。演示只填界面，不进 <see cref="EnvHistoryStore"/>。</summary>
    public void ToggleTrendPreview()
    {
        IsTrendPreview = !IsTrendPreview;
        if (IsTrendPreview) ApplyPreviewData();
        else RefreshTrendCards();
    }

    /// <summary>
    /// 演示数据：只填 UI，绝不写进 <see cref="EnvHistoryStore"/>，也不会被当成真实读数。
    /// 曲线按一天里真实的冷暖节奏走（15 点最高、凌晨最低；湿度反过来），功率按典型家庭作息。
    /// </summary>
    private void ApplyPreviewData()
    {
        var now = DateTime.Now;
        var count = (int)(TrendWindow.TotalMinutes / TrendStepMinutes) + 1;

        var temps = DemoCurve(now, count, 21.4, 3.2, 15, 0.10);
        var hums = DemoCurve(now, count, 52, 8, 3, 0.5);
        HasTempReading = true;
        HasHumReading = true;
        TempTrendSeries = temps;
        HumTrendSeries = hums;
        TempTrendPlaceholder = "";
        HumTrendPlaceholder = "";
        ShowTempChart = true;
        ShowHumChart = true;
        EnvTrendCaption = "近 24 小时 · 每 5 分钟采样";

        var tNow = temps[^1];
        TempTrendValue = $"{tNow:0.#}";
        TempTrendNote = TempComfortOf(tNow);
        TempTrendLow = MinText(temps, "℃");
        TempTrendHigh = MaxText(temps, "℃");
        TempTrendDelta = $"较昨日 {tNow - temps[0]:+0.0;-0.0;0.0}℃";
        HasTempDelta = true;

        var hNow = hums[^1];
        HumTrendValue = $"{hNow:0}";
        HumTrendNote = HumComfortOf(hNow);
        HumTrendLow = MinText(hums, "%");
        HumTrendHigh = MaxText(hums, "%");
        HumTrendDelta = $"较昨日 {hNow - hums[0]:+0;-0;0}%";
        HasHumDelta = true;

        var bars = DemoPowerBars(now);
        PowerTrendSeries = bars;
        HasMetering = true;
        ShowPowerChart = true;
        PowerTrendPlaceholder = "";

        var focus = -1;
        var kwh = 0.0;
        var low = double.MaxValue;
        var high = double.MinValue;
        for (var i = 0; i < bars.Length; i++)
        {
            var v = bars[i];
            if (double.IsNaN(v)) continue;
            focus = i;
            kwh += v / 1000.0;
            if (v < low) low = v;
            if (v > high) high = v;
        }
        PowerTrendFocus = focus;
        PowerTrendValue = $"{kwh:0.##}";
        PowerTrendNow = focus >= 0 ? $"当前 {FormatWatts(bars[focus])}" : "";
        PowerTrendDelta = "较昨日 -6%";
        HasPowerDelta = true;
        PowerTrendLow = low == double.MaxValue ? "—" : $"待机 {FormatWatts(low)}";
        PowerTrendHigh = high == double.MinValue ? "—" : $"峰值 {FormatWatts(high)}";
    }

    private static double[] DemoCurve(DateTime now, int count, double mean, double amp, double peakHour, double jitter)
    {
        var pts = new double[count];
        var start = now.AddMinutes(-(count - 1) * TrendStepMinutes);
        for (var i = 0; i < count; i++)
        {
            var hour = start.AddMinutes(i * TrendStepMinutes).TimeOfDay.TotalHours;
            var phase = (hour - peakHour) / 24.0 * Math.PI * 2;
            var wobble = Math.Sin(i * 0.22) * jitter + Math.Sin(i * 0.55 + 1.1) * jitter * 0.45;
            pts[i] = mean + amp * Math.Cos(phase) + wobble;
        }
        return pts;
    }

    private static double[] DemoPowerBars(DateTime now)
    {
        // 每小时平均功率（W）：夜间待机、早晚两个高峰，午后有小峰值
        int[] profile =
        [
            45, 42, 40, 39, 41, 58, 175, 330, 255, 120, 96, 112,
            205, 148, 104, 92, 98, 142, 368, 515, 468, 318, 158, 72
        ];
        var bars = new double[24];
        for (var i = 0; i < 24; i++)
            bars[i] = i <= now.Hour ? profile[i] : double.NaN;
        return bars;
    }

    private void RefreshPower(DateTime now)
    {
        var metered = _allDevices
            .Where(d => d.PowerWatts.HasValue || d.EnergyKwh.HasValue || _cloudEnergy.ContainsKey(d.TargetDid))
            .ToList();
        HasMetering = metered.Count > 0;

        // 选了某台设备就只看它；没选就是全屋在线计量设备的叠加
        var did = SelectedPowerSource;
        var single = did.Length == 0 ? null : metered.FirstOrDefault(d => d.TargetDid == did);
        var sources = did.Length == 0
            ? metered
            : single is { } one ? new List<MijiaDevice> { one } : new List<MijiaDevice>();

        // 逐台设备算每小时的平均功率再叠加：云端有「电量统计」就用云端（更准，空调这类只有它），
        // 没有就用本地采样；换房间 / 换设备后照旧按各自的历史重算，不删任何一段
        var rangeDays = PowerRangeDays;
        if (rangeDays > 0)
        {
            RefreshPowerDays(now, rangeDays, sources, single, did);
            return;
        }

        PowerTitle = "今日用电";
        PowerChartUnit = "W";
        PowerIsDailyBars = false;

        var bars = new double[24];
        Array.Fill(bars, double.NaN);
        var todayKwh = 0.0;
        var hasTodayKwh = false;
        var anyCloud = false;

        foreach (var d in sources)
        {
            if (_cloudEnergy.TryGetValue(d.TargetDid, out var cloud) && cloud.HasData) anyCloud = true;

            foreach (var (hour, watts) in DeviceHourly(d, now))
                bars[hour] = double.IsNaN(bars[hour]) ? watts : bars[hour] + watts;

            if (TodayKwhOf(d, now) is { } kwh)
            {
                todayKwh += kwh;
                hasTodayKwh = true;
            }
        }

        var filled = 0;
        foreach (var v in bars)
            if (!double.IsNaN(v)) filled++;

        // 有一格采到就成形：切到单台设备时它自己的历史本来就短，再等「攒够几小时」就整块空了
        ShowPowerChart = HasMetering && filled >= 1;
        PowerTrendPlaceholder = !HasMetering
            ? "未发现能回读用电的设备（插座报功率、空调报累计电量的才会有）"
            : ShowPowerChart
                ? ""
                : did.Length > 0
                    ? "这台设备今天还没采到用电读数"
                    : "今天刚开始记 · 采到读数就出柱状图";

        PowerTrendSeries = ShowPowerChart ? bars : Array.Empty<double>();

        if (!HasMetering)
            PowerTrendNow = "";
        else if (single is { } device)
            PowerTrendNow = !device.IsOnline
                ? "设备离线"
                : device.PowerWatts is { } watts
                    ? $"当前 {FormatWatts(watts)}"
                    : CloudTodayOf(device.TargetDid) is { } today
                        ? $"今日 {today:0.##} 度"
                        : device.EnergyKwh is { } kwhNow
                            ? $"累计 {kwhNow:0.##} 度"
                            : "";
        else
        {
            var liveWatts = metered.Where(d => d.IsOnline && d.PowerWatts.HasValue).Sum(d => d.PowerWatts!.Value);
            PowerTrendNow = metered.Any(d => d.IsOnline) ? $"当前 {FormatWatts(liveWatts)}" : "设备离线";
        }

        // 高亮当前小时；这一小时还没采到数就往前找最近一个有数的小时
        var focus = -1;
        if (ShowPowerChart)
        {
            for (var i = Math.Min(now.Hour, bars.Length - 1); i >= 0; i--)
            {
                if (!double.IsNaN(bars[i])) { focus = i; break; }
            }
        }
        PowerTrendFocus = focus;

        var (_, localCoverage) = _envHistory.Energy(now.Date, now, did);
        var (yesterdayKwh, yesterdayCoverage) = _envHistory.Energy(now.Date.AddDays(-1), now.Date, did);

        var hasToday = hasTodayKwh && (anyCloud || localCoverage >= 0.5);
        PowerTrendValue = hasToday ? $"{todayKwh:0.##}" : "—";

        var canCompare = hasToday && yesterdayCoverage >= 0.5 && yesterdayKwh >= 0.05;
        if (canCompare)
        {
            var percent = (todayKwh - yesterdayKwh) / yesterdayKwh * 100;
            PowerTrendDelta = $"较昨日 {percent:+0;-0;0}%";
            HasPowerDelta = true;
        }
        else
        {
            PowerTrendDelta = "";
            HasPowerDelta = false;
        }

        if (!ShowPowerChart)
        {
            PowerTrendLow = "—";
            PowerTrendHigh = "—";
            return;
        }

        var low = double.MaxValue;
        var high = double.MinValue;
        foreach (var v in bars)
        {
            if (double.IsNaN(v)) continue;
            if (v < low) low = v;
            if (v > high) high = v;
        }
        PowerTrendLow = low == double.MaxValue ? "—" : $"待机 {FormatWatts(low)}";
        PowerTrendHigh = high == double.MinValue ? "—" : $"峰值 {FormatWatts(high)}";
    }

    /// <summary>用电卡当前看几天：0 = 今日逐小时，7 / 30 = 近 N 天逐日。</summary>
    private int PowerRangeDays => int.TryParse(SelectedPowerRange, out var n) && n > 0 ? n : 0;

    partial void OnSelectedPowerRangeChanged(string value)
    {
        AppSettingsStore.SetPowerRange(PowerRangeDays);
        if (_resettingSources) return;
        RefreshTrendCards();
        _ = RefreshEnergyStatsAsync(); // 换范围时顺手补一次云端历史（有节流，不会每次切换都打请求）
    }

    /// <summary>
    /// 逐日柱状：每根柱子是那天一共用了多少度。云端日统计（空调这类只有累计电量的）优先，
    /// 没有就用本地采样积分；两边都没有的那天留空，不画假柱子。
    /// </summary>
    private void RefreshPowerDays(DateTime now, int days, IReadOnlyList<MijiaDevice> sources,
        MijiaDevice? single, string did)
    {
        var bars = new double[days];
        Array.Fill(bars, double.NaN);
        var any = false;

        for (var i = 0; i < days; i++)
        {
            var day = now.Date.AddDays(i - (days - 1));
            var end = day >= now.Date ? now : day.AddDays(1);
            var sum = 0.0;
            var hit = false;
            foreach (var d in sources)
            {
                if (DayKwhOf(d, day, end) is not { } kwh) continue;
                sum += kwh;
                hit = true;
            }
            if (!hit) continue;
            bars[i] = sum;
            any = true;
        }

        DiagDays(now, days, sources);

        PowerTitle = days == 7 ? "近 7 天用电" : $"近 {days} 天用电";
        PowerChartUnit = "度";
        PowerIsDailyBars = true;

        ShowPowerChart = HasMetering && any;
        PowerTrendPlaceholder = !HasMetering
            ? "未发现能回读用电的设备（插座报功率、空调报累计电量的才会有）"
            : any
                ? ""
                : did.Length > 0
                    ? "这台设备这段时间还没有用电记录"
                    : "这段时间还没有用电记录";
        PowerTrendSeries = ShowPowerChart ? bars : Array.Empty<double>();

        var focus = -1;
        for (var i = 0; i < bars.Length; i++)
            if (!double.IsNaN(bars[i])) focus = i;
        PowerTrendFocus = focus;

        var total = 0.0;
        var counted = 0;
        for (var i = 0; i < bars.Length; i++)
        {
            if (double.IsNaN(bars[i])) continue;
            total += bars[i];
            counted++;
        }

        PowerTrendValue = any ? $"{total:0.##}" : "—";
        PowerTrendNow = !any
            ? ""
            : counted < days
                ? $"{counted} 天有记录 · 日均 {total / counted:0.##} 度"
                : $"日均 {total / days:0.##} 度";

        // 逐日范围和「较昨日」不是一个口径，切换期间先不挂这枚对比胶囊
        PowerTrendDelta = "";
        HasPowerDelta = false;

        var low = double.MaxValue;
        var high = double.MinValue;
        for (var i = 0; i < bars.Length; i++)
        {
            if (double.IsNaN(bars[i])) continue;
            if (bars[i] < low) low = bars[i];
            if (bars[i] > high) high = bars[i];
        }

        PowerTrendLow = low == double.MaxValue ? "—" : $"最少 {low:0.##} 度";
        PowerTrendHigh = high == double.MinValue ? "—" : $"最多 {high:0.##} 度";
    }

    /// <summary>排查用：逐日柱子里每台设备各贡献多少、取自云端还是本地，写进 energy-days.log（内容变了才写）。</summary>
    private void DiagDays(DateTime now, int days, IReadOnlyList<MijiaDevice> sources)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < days; i++)
            {
                var day = now.Date.AddDays(i - (days - 1));
                var end = day >= now.Date ? now : day.AddDays(1);
                var parts = new List<string>();
                foreach (var d in sources)
                {
                    if (DayKwhOf(d, day, end) is not { } kwh || kwh <= 0.005) continue;
                    var fromCloud = !d.PowerWatts.HasValue && CloudDayKwh(d.TargetDid, day) is not null;
                    parts.Add($"{d.Name}={kwh:0.###}{(fromCloud ? "(云)" : "(本地)")}");
                }
                if (parts.Count == 0) continue;
                sb.Append($"{day:yyyy-MM-dd} {string.Join(" ", parts)}{Environment.NewLine}");
            }

            var body = sb.ToString();
            if (body.Length == 0 || body == _diagDaysSig) return;
            _diagDaysSig = body;

            System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppPaths.Dir, "energy-days.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 范围 {days} 天 设备 {sources.Count}{Environment.NewLine}{body}");
        }
        catch
        {
            // 排查日志失败不影响界面
        }
    }

    /// <summary>某台设备某一天用了多少度：本机能读功率的走本地采样，只有累计电量的读云端日统计。</summary>
    private double? DayKwhOf(MijiaDevice d, DateTime day, DateTime end)
    {
        if (!d.PowerWatts.HasValue && CloudDayKwh(d.TargetDid, day) is { } cloud) return cloud;

        var (kwh, coverage) = _envHistory.Energy(day, end, d.TargetDid);
        return coverage > 0 ? kwh : null;
    }

    private double? CloudDayKwh(string did, DateTime day)
    {
        if (!_cloudEnergy.TryGetValue(did, out var cloud)) return null;
        foreach (var (date, kwh) in cloud.Days)
            if (date.Date == day.Date) return kwh;
        return null;
    }

    /// <summary>一台设备今天逐小时的平均功率（W）：能自己报瞬时功率的用本地采样，空调这类只有云端电量统计的用云端。</summary>
    private IEnumerable<(int Hour, double Watts)> DeviceHourly(MijiaDevice d, DateTime now)
    {
        var local = _envHistory.HourlyPower(now, d.TargetDid);
        var localHas = local.Any(v => !double.IsNaN(v));

        // 插座这类本机能读到瞬时功率，本地采样比云端日统计更细也更贴当前
        if (d.PowerWatts.HasValue && localHas)
        {
            for (var h = 0; h < 24; h++)
                if (!double.IsNaN(local[h])) yield return (h, local[h]);
            yield break;
        }

        if (_cloudEnergy.TryGetValue(d.TargetDid, out var cloud) && cloud.HasHourly)
        {
            for (var h = 0; h < 24; h++)
                if (!double.IsNaN(cloud.HourlyKwh[h]))
                    yield return (h, cloud.HourlyKwh[h] * 1000); // 一小时用了几度 = 那一小时的平均功率（kW）
            yield break;
        }

        for (var h = 0; h < 24; h++)
            if (!double.IsNaN(local[h])) yield return (h, local[h]);
    }

    /// <summary>这台设备今天的用电量（度）：插座按本地采样积分/计数器差值，空调这类用云端日统计。</summary>
    private double? TodayKwhOf(MijiaDevice d, DateTime now)
    {
        if (!d.PowerWatts.HasValue && CloudTodayOf(d.TargetDid) is { } cloud) return cloud;
        var (kwh, coverage) = _envHistory.Energy(now.Date, now, d.TargetDid);
        return coverage > 0 ? kwh : null;
    }

    private double? CloudTodayOf(string did) =>
        _cloudEnergy.TryGetValue(did, out var cloud) && cloud.HasData ? cloud.TodayKwh : null;


    private static string MinText(IReadOnlyList<double> series, string unit)
    {
        var min = double.MaxValue;
        foreach (var v in series)
            if (!double.IsNaN(v) && v < min) min = v;
        return min == double.MaxValue ? "—" : $"{min:0.#}{unit}";
    }

    private static string MaxText(IReadOnlyList<double> series, string unit)
    {
        var max = double.MinValue;
        foreach (var v in series)
            if (!double.IsNaN(v) && v > max) max = v;
        return max == double.MinValue ? "—" : $"{max:0.#}{unit}";
    }

    private static string TempComfortOf(double? temp)
    {
        if (temp is not { } t) return "";
        if (t < 18) return "体感偏冷";
        if (t > 27) return "体感偏热";
        return "体感舒适";
    }

    private static string HumComfortOf(double? humidity)
    {
        if (humidity is not { } h) return "";
        if (h < 35) return "空气偏干";
        if (h > 70) return "空气偏湿";
        return "湿度舒适";
    }

    private static string FormatWatts(double watts) =>
        watts >= 1000 ? $"{watts / 1000:0.##} kW" : $"{watts:0} W";
}
