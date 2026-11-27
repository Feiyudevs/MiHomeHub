// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Models;
using MiHomeHub.Services;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 首页「数据看板」趋势卡的数据侧。
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

    /// <summary>用水 / 用气卡的逐单记录来自米家云端：本地只留一份缓存，定时补拉。</summary>
    private DateTime _usageFetchAt = DateTime.MinValue;
    private bool _usageBusy;

    /// <summary>水 / 气的云端日统计（米家「使用记录」同源）：一次拉半年，本月、近 30 天和近半年都从这里取。</summary>
    private readonly Dictionary<string, StatSeries> _waterDayStats = new();
    private readonly Dictionary<string, StatSeries> _gasDayStats = new();

    [ObservableProperty] private bool _showEnvTrend;
    [ObservableProperty] private bool _hasMetering;

    /// <summary>家里有没有能报温度 / 湿度的设备：没有就不铺那张卡，三类都没有时整段不出现。</summary>
    [ObservableProperty] private bool _hasTempReading;
    [ObservableProperty] private bool _hasHumReading;

    /// <summary>有没有带水表的用水设备（热水器 / 净水器）：没有就不铺「用水」卡。</summary>
    [ObservableProperty] private bool _hasWaterMeter;

    /// <summary>有没有净水器能回读出水 TDS：没有就只铺「用水」，不铺「水质」。</summary>
    [ObservableProperty] private bool _hasWaterQuality;

    /// <summary>有没有能回读用气量的燃气热水器：没有就铺一张预览卡。</summary>
    [ObservableProperty] private bool _hasGasMeter;

    // —— 逐卡预览：没有可用设备的卡不再空着，改铺一份示例数据，并在卡上标「预览」 ——

    /// <summary>这张卡现在画的是不是演示数据：缺设备时为真，双击标题行整段演示时全部为真。</summary>
    [ObservableProperty] private bool _isTempPreview;
    [ObservableProperty] private bool _isHumPreview;
    [ObservableProperty] private bool _isPowerPreview;
    [ObservableProperty] private bool _isWaterPreview;
    [ObservableProperty] private bool _isGasPreview;
    [ObservableProperty] private bool _isQualityPreview;
    [ObservableProperty] private bool _isScalePreview;

    /// <summary>首页上有没有任意一张卡在铺预览数据（标题旁挂「预览 · 演示数据」标记）。</summary>
    public bool HasAnyTrendPreview =>
        IsTempPreview || IsHumPreview || IsPowerPreview || IsWaterPreview
        || IsGasPreview || IsQualityPreview || IsScalePreview;

    /// <summary>铺出来的卡里有没有预览卡：用户把预览卡全关掉时，标题旁不再挂标记。</summary>
    public bool HasVisibleTrendPreview =>
        (IsTempPreview && TempCardShown) || (IsHumPreview && HumCardShown)
        || (IsPowerPreview && PowerCardShown) || (IsWaterPreview && WaterCardShown)
        || (IsGasPreview && GasCardShown) || (IsQualityPreview && QualityCardShown)
        || (IsScalePreview && ScaleCardShown);

    // 范围下拉在预览态也要能用：换范围会把示例数据换一种口径重画，卡片的交互和真数据一致
    public bool CanPickPowerRange => HasMetering || IsPowerPreview;
    public bool CanPickWaterRange => HasWaterMeter || IsWaterPreview;
    public bool CanPickGasRange => HasGasMeter || IsGasPreview;
    public bool CanPickScaleRange => HasScale || IsScalePreview;

    partial void OnIsTempPreviewChanged(bool value) => RaiseTrendPreviewFlags();
    partial void OnIsHumPreviewChanged(bool value) => RaiseTrendPreviewFlags();
    partial void OnIsPowerPreviewChanged(bool value) => RaiseTrendPreviewFlags();
    partial void OnIsWaterPreviewChanged(bool value) => RaiseTrendPreviewFlags();
    partial void OnIsGasPreviewChanged(bool value) => RaiseTrendPreviewFlags();
    partial void OnIsQualityPreviewChanged(bool value) => RaiseTrendPreviewFlags();
    partial void OnIsScalePreviewChanged(bool value) => RaiseTrendPreviewFlags();

    private void RaiseTrendPreviewFlags()
    {
        OnPropertyChanged(nameof(HasAnyTrendPreview));
        OnPropertyChanged(nameof(HasVisibleTrendPreview));
        OnPropertyChanged(nameof(CanPickPowerRange));
        OnPropertyChanged(nameof(CanPickWaterRange));
        OnPropertyChanged(nameof(CanPickGasRange));
        OnPropertyChanged(nameof(CanPickScaleRange));
    }

    partial void OnHasWaterMeterChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPickWaterRange));
        RaiseTrendRowFlags();
    }

    partial void OnHasWaterQualityChanged(bool value) => RaiseTrendRowFlags();

    partial void OnHasGasMeterChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPickGasRange));
        RaiseTrendRowFlags();
    }

    /// <summary>整个区块去留变化时，首拉骨架与「单卡并排」的显示条件跟着翻转。</summary>
    partial void OnShowEnvTrendChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTrendSkeleton));
        RaiseTrendRowFlags();
    }

    /// <summary>首页实际铺出来的大趋势卡数量（不含那一排空气小卡）。</summary>
    public int BigTrendCardCount =>
        (TempCardShown ? 1 : 0) + (HumCardShown ? 1 : 0) + (PowerCardShown ? 1 : 0)
        + (WaterCardShown ? 1 : 0) + (GasCardShown ? 1 : 0) + (QualityCardShown ? 1 : 0)
        + (ScaleCardShown ? 1 : 0);

    /// <summary>数据看板里的全部卡片：大趋势卡 + 空气小卡。决定整段去留与「全关」确认。</summary>
    public int TrendCardCount => BigTrendCardCount + AirCardCount;

    /// <summary>只剩一张温度/湿度/用电卡、又有快捷场景时，两者并排；水/气卡较宽，不挤进 300 宽的单卡位。</summary>
    public bool ShowTrendQuickRow =>
        ShowEnvTrend && !ShowTrendSkeleton && BigTrendCardCount == 1 && ShowQuickScenes
        && (TempCardShown || HumCardShown || PowerCardShown);

    /// <summary>趋势网格的列数上限：一张卡独占一行，两张并排，三张及以上按三列均分。</summary>
    public int TrendGridColumns => Math.Max(1, Math.Min(3, BigTrendCardCount));

    /// <summary>趋势区（标题行 + 卡片网格）独占一行：有卡可铺时才出现。</summary>
    public bool ShowTrendSolo => ShowEnvTrend && !ShowTrendQuickRow;

    /// <summary>快捷场景正常铺在趋势区下方；并到同一行后交给合并区渲染，这里让位。</summary>
    public bool ShowQuickScenesSolo => ShowQuickScenes && !ShowTrendQuickRow;

    private void RaiseTrendRowFlags()
    {
        OnPropertyChanged(nameof(BigTrendCardCount));
        OnPropertyChanged(nameof(TrendCardCount));
        OnPropertyChanged(nameof(TrendGridColumns));
        OnPropertyChanged(nameof(ShowTrendQuickRow));
        OnPropertyChanged(nameof(ShowTrendSolo));
        OnPropertyChanged(nameof(ShowQuickScenesSolo));
        OnPropertyChanged(nameof(ShowAirRow));
        OnPropertyChanged(nameof(DataBoardVisible));
        OnPropertyChanged(nameof(TempCardShown));
        OnPropertyChanged(nameof(HumCardShown));
        OnPropertyChanged(nameof(PowerCardShown));
        OnPropertyChanged(nameof(WaterCardShown));
        OnPropertyChanged(nameof(GasCardShown));
        OnPropertyChanged(nameof(QualityCardShown));
        OnPropertyChanged(nameof(ScaleCardShown));
        OnPropertyChanged(nameof(HasVisibleTrendPreview));
    }

    partial void OnHasTempReadingChanged(bool value) => RaiseTrendRowFlags();
    partial void OnHasHumReadingChanged(bool value) => RaiseTrendRowFlags();

    partial void OnHasMeteringChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPickPowerRange));
        RaiseTrendRowFlags();
    }

    partial void OnIsTrendPreviewChanged(bool value) => RaiseTrendRowFlags();

    /// <summary>
    /// 每张卡在不在首页：用户选过就按选择走；没选过就全部铺出来——
    /// 没有设备的卡不再空着，改展示一份示例数据并标「预览」。
    /// </summary>
    public bool TempCardShown => CardShown("temp");
    public bool HumCardShown => CardShown("hum");
    public bool PowerCardShown => CardShown("power");
    public bool WaterCardShown => CardShown("water");
    public bool GasCardShown => CardShown("gas");
    public bool QualityCardShown => CardShown("tds");
    public bool ScaleCardShown => CardShown("scale");

    /// <summary>
    /// 首页是否显示「数据看板」整块：用户主动隐藏整块、或者把所有卡片全关掉时为 false。
    /// 设置页的开关直接绑这个属性；重新打开时如果卡片全关，会顺手把所有卡片恢复显示。
    /// </summary>
    public bool DataBoardVisible
    {
        get => !AppSettingsStore.DataBoardHidden && TrendCardCount > 0;
        set
        {
            if (value == DataBoardVisible) return;

            if (value)
            {
                var showAllCards = TrendCardCount == 0;
                AppSettingsStore.ShowDataBoard(showAllCards);
                if (showAllCards) SyncTrendCardOptions();
            }
            else
            {
                AppSettingsStore.SetDataBoardHidden(true);
            }

            RefreshDataBoardVisibility();
        }
    }

    /// <summary>用户是否主动把「数据看板」整块隐藏：编辑弹窗据此停用「首页」开关并说明原因。</summary>
    public bool IsDataBoardHidden => AppSettingsStore.DataBoardHidden;

    /// <summary>整块显隐或卡片数量变化后，同步首页区块、骨架和设置开关。</summary>
    private void RefreshDataBoardVisibility()
    {
        ShowEnvTrend = DataBoardVisible;
        OnPropertyChanged(nameof(DataBoardVisible));
        OnPropertyChanged(nameof(IsDataBoardHidden));
        OnPropertyChanged(nameof(ShowTrendSkeleton));
        RaiseTrendRowFlags();
    }

    /// <summary>首页上排的大趋势卡。编辑弹窗里排在左列，顺序只管左列内部。</summary>
    private static readonly string[] BigTrendCardKeys =
        { "temp", "hum", "power", "water", "gas", "tds", "scale" };

    /// <summary>首页大卡下方那一排的空气小卡。编辑弹窗里排在右列，顺序与大卡互不影响。</summary>
    private static readonly string[] SmallTrendCardKeys = { "pm25", "pm10", "hcho", "tvoc", "co2" };

    /// <summary>数据看板默认顺序：先是七张大卡，后面接五枚空气小卡。</summary>
    private static readonly string[] DefaultTrendCardOrder =
        BigTrendCardKeys.Concat(SmallTrendCardKeys).ToArray();

    private static bool IsBigTrendCardKey(string key) => Array.IndexOf(BigTrendCardKeys, key) >= 0;

    /// <summary>首页趋势卡的排列顺序，编辑弹窗里拖过之后即时更新；首页面板按它排卡片。</summary>
    [ObservableProperty] private IReadOnlyList<string> _trendCardOrder = LoadTrendCardOrder();

    private static IReadOnlyList<string> LoadTrendCardOrder()
    {
        var saved = AppSettingsStore.GetTrendCardOrder();
        var order = new List<string>(DefaultTrendCardOrder.Length);

        foreach (var key in saved)
            if (IsTrendCardKey(key) && !order.Contains(key))
                order.Add(key);

        // 设置里可能只存了部分 key（旧数据/人为改坏）：缺的按默认顺序补在后面
        foreach (var key in DefaultTrendCardOrder)
            if (!order.Contains(key))
                order.Add(key);

        return order;
    }

    private static bool IsTrendCardKey(string key) => Array.IndexOf(DefaultTrendCardOrder, key) >= 0;

    /// <summary>
    /// 弹窗里拖完排序：大卡、小卡两列各按自己的顺序落盘，首页按同一个 key 数组重排。
    /// 两列的顺序互不影响——左边拖大卡不会带动右边的小卡，反之亦然。
    /// </summary>
    public void CommitTrendCardOrder()
    {
        if (BigTrendCardOptions.Count == 0 && SmallTrendCardOptions.Count == 0) return;

        var keys = BigTrendCardOptions.Concat(SmallTrendCardOptions)
            .Select(option => option.Key)
            .ToList();
        AppSettingsStore.SetTrendCardOrder(keys);
        TrendCardOrder = keys;
    }

    /// <summary>按给定顺序重排弹窗里的行；未列出的行保持原有相对位置。重排后按大卡 / 小卡拆回两列。</summary>
    private void ApplyTrendCardOrderToOptions(IReadOnlyList<string> order)
    {
        for (var target = 0; target < order.Count; target++)
        {
            var item = TrendCardOptions.FirstOrDefault(option => option.Key == order[target]);
            if (item is null) continue;

            var current = TrendCardOptions.IndexOf(item);
            if (current >= 0 && current != target) TrendCardOptions.Move(current, target);
        }

        SplitTrendCardOptions();
    }

    /// <summary>把 12 行分成两列：左列大卡、右列空气小卡；各自保留在主列表里的相对顺序。</summary>
    private void SplitTrendCardOptions()
    {
        BigTrendCardOptions.Clear();
        SmallTrendCardOptions.Clear();

        foreach (var option in TrendCardOptions)
            (IsBigTrendCardKey(option.Key) ? BigTrendCardOptions : SmallTrendCardOptions).Add(option);
    }

    private bool CardShown(string key)
    {
        // 双击标题行的整段演示是「把所有卡的样子摆出来看」，不掺用户自己的显隐
        if (IsTrendPreview) return true;
        return AppSettingsStore.GetTrendCardShown(key) ?? true;
    }

    /// <summary>这张卡对应的真实设备在不在：编辑弹窗据此在卡下留一句「暂无设备 · 显示示例数据」。</summary>
    private bool CardHasDevice(string key) => key switch
    {
        "temp" => HasTempReading,
        "hum" => HasHumReading,
        "power" => HasMetering,
        "water" => HasWaterMeter,
        "gas" => HasGasMeter,
        "tds" => HasWaterQuality,
        "scale" => HasScale,
        _ when AirMetrics.ByKey(key) is { } air => HasAirMetric(air.Standard),
        _ => false
    };

    /// <summary>「数据看板」标题旁的编辑弹窗：逐张勾选首页要铺哪几张卡。</summary>
    [ObservableProperty] private bool _isTrendCardEditOpen;

    /// <summary>编辑里把所有卡关掉后点「完成」的二次确认：首页整块会消失，需要明确提醒。</summary>
    [ObservableProperty] private bool _isTrendHideConfirmOpen;

    public ObservableCollection<TrendCardOptionItem> TrendCardOptions { get; } = new();

    /// <summary>弹窗左列：七张大趋势卡，拖动只改这一列内部顺序。</summary>
    public ObservableCollection<TrendCardOptionItem> BigTrendCardOptions { get; } = new();

    /// <summary>弹窗右列：五枚空气小卡，顺序与大卡各自独立。</summary>
    public ObservableCollection<TrendCardOptionItem> SmallTrendCardOptions { get; } = new();

    private bool _trendCardOptionsBuilt;

    [RelayCommand]
    private void OpenTrendCardEdit()
    {
        BuildTrendCardOptions();
        SyncTrendCardOptions();
        IsTrendHideConfirmOpen = false;
        IsTrendCardEditOpen = true;
    }

    /// <summary>
    /// 设置页「桌面数据卡」摘要里的「管理」：收起设置，直接回到首页看板的编辑弹窗。
    /// 看板整块被隐藏时也走这里——那个弹窗里还会说明「首页」开关当前不生效。
    /// </summary>
    [RelayCommand]
    private void ManageDesktopDataCards()
    {
        IsSettingsOpen = false;
        OpenTrendCardEdit();
    }

    /// <summary>关闭编辑弹窗：如果一张卡都没开，先弹重点提示，确认后才真正收起。</summary>
    [RelayCommand]
    private void FinishTrendCardEdit()
    {
        if (TrendCardCount == 0)
        {
            IsTrendHideConfirmOpen = true;
            return;
        }

        IsTrendCardEditOpen = false;
    }

    /// <summary>确认整块隐藏：首页不再出现数据看板，之后再从设置重新打开。</summary>
    [RelayCommand]
    private void ConfirmTrendHide()
    {
        AppSettingsStore.SetDataBoardHidden(true);
        IsTrendHideConfirmOpen = false;
        IsTrendCardEditOpen = false;
        RefreshDataBoardVisibility();
    }

    /// <summary>返回编辑继续调整：不隐藏整块，留在弹窗里把某张卡打开。</summary>
    [RelayCommand]
    private void CancelTrendHide() => IsTrendHideConfirmOpen = false;

    /// <summary>弹窗里「恢复默认」：清掉手动显隐、拖动排序和整块隐藏，回到全部卡片 + 默认顺序。</summary>
    [RelayCommand]
    private void ResetTrendCardChoices()
    {
        AppSettingsStore.ClearTrendCardLayout();
        TrendCardOrder = DefaultTrendCardOrder;
        ApplyTrendCardOrderToOptions(DefaultTrendCardOrder);
        SyncTrendCardOptions();
        ReapplyTrendCardChoices();
        RefreshDataBoardVisibility();
    }

    private void BuildTrendCardOptions()
    {
        if (_trendCardOptionsBuilt) return;
        _trendCardOptionsBuilt = true;
        TrendCardOptions.Add(new TrendCardOptionItem("temp", "温度", "室内温度的曲线与舒适度", OnTrendCardToggled, OnTrendCardDesktopToggled));
        TrendCardOptions.Add(new TrendCardOptionItem("hum", "湿度", "室内湿度的曲线", OnTrendCardToggled, OnTrendCardDesktopToggled));
        TrendCardOptions.Add(new TrendCardOptionItem("power", "用电", "用电量与用电趋势", OnTrendCardToggled, OnTrendCardDesktopToggled));
        TrendCardOptions.Add(new TrendCardOptionItem("water", "用水", "热水器、净水器的用水量", OnTrendCardToggled, OnTrendCardDesktopToggled));
        TrendCardOptions.Add(new TrendCardOptionItem("gas", "用气", "燃气热水器的用气量", OnTrendCardToggled, OnTrendCardDesktopToggled));
        TrendCardOptions.Add(new TrendCardOptionItem("tds", "水质", "净水器出水 TDS 与滤芯余量", OnTrendCardToggled, OnTrendCardDesktopToggled));
        TrendCardOptions.Add(new TrendCardOptionItem("scale", "体重", "体重、体脂率与体重曲线", OnTrendCardToggled, OnTrendCardDesktopToggled));
        // 空气小卡：一项一枚，只报当前数值与一句档位，没有对应设备的卡铺示例数据
        TrendCardOptions.Add(new TrendCardOptionItem("pm25", "PM2.5", "PM2.5 细颗粒物浓度", OnTrendCardToggled, OnTrendCardDesktopToggled));
        TrendCardOptions.Add(new TrendCardOptionItem("pm10", "PM10", "PM10 可吸入颗粒物浓度", OnTrendCardToggled, OnTrendCardDesktopToggled));
        TrendCardOptions.Add(new TrendCardOptionItem("hcho", "甲醛", "室内甲醛浓度（mg/m³）", OnTrendCardToggled, OnTrendCardDesktopToggled));
        TrendCardOptions.Add(new TrendCardOptionItem("tvoc", "TVOC", "挥发性有机物浓度（mg/m³）", OnTrendCardToggled, OnTrendCardDesktopToggled));
        TrendCardOptions.Add(new TrendCardOptionItem("co2", "CO₂", "室内二氧化碳浓度（ppm）", OnTrendCardToggled, OnTrendCardDesktopToggled));

        // 上次拖过的顺序优先：弹窗每次打开看到的行序就是首页实际的卡片顺序
        ApplyTrendCardOrderToOptions(TrendCardOrder);
    }

    /// <summary>把弹窗里的每一行对齐到当前状态：首页上现在显不显示、桌面上有没有。</summary>
    private void SyncTrendCardOptions()
    {
        foreach (var item in TrendCardOptions)
        {
            item.HasDevice = CardHasDevice(item.Key);
            // 看板整块隐藏时「首页」开关先停用：打开了首页也看不见，留一句提示比让人白点强
            item.HomeEnabled = !IsDataBoardHidden;
            item.SyncOn(CardShown(item.Key));
            item.SyncDesktop(DesktopHost.IsDataCardShown(item.Key));
        }
    }

    private void OnTrendCardToggled(TrendCardOptionItem item)
    {
        AppSettingsStore.SetTrendCardShown(item.Key, item.IsOn);
        ReapplyTrendCardChoices();
    }

    /// <summary>弹窗里的「桌面」开关：直接投/收这张数据卡；位置尺寸由 DesktopHost 保留，下次原样回来。</summary>
    private void OnTrendCardDesktopToggled(TrendCardOptionItem item)
        => DesktopHost.SetDataCardShown(item.Key, item.IsOnDesktop);

    /// <summary>桌面卡片被别处增删（卡窗右键关闭 / 移除）时，把弹窗里的开关跟着回写。</summary>
    private void OnDesktopDataCardStateChanged()
    {
        if (!IsTrendCardEditOpen) return;
        foreach (var item in TrendCardOptions)
            item.SyncDesktop(DesktopHost.IsDataCardShown(item.Key));
    }

    /// <summary>编辑弹窗开着才订阅桌面数据卡的增删事件，关掉就摘掉，别让静态事件一直攥着视图模型。</summary>
    partial void OnIsTrendCardEditOpenChanged(bool value)
    {
        if (value) DesktopHost.DataCardStateChanged += OnDesktopDataCardStateChanged;
        else DesktopHost.DataCardStateChanged -= OnDesktopDataCardStateChanged;
    }

    /// <summary>显隐一改：重算整段与逐卡去留，并让读数跟着刷一遍（新铺出来的卡不该是空的）。</summary>
    private void ReapplyTrendCardChoices()
    {
        RefreshTrendCards();
        RaiseTrendRowFlags();
    }

    /// <summary>默认展示真实采样；双击标题行切到演示数据（供预览样式），再双击还原。</summary>
    [ObservableProperty] private bool _isTrendPreview;

    /// <summary>预览数据说明弹窗：首次出现缺设备的预览卡时自动弹一次，之后点卡上的「预览」标识也能再看。</summary>
    [ObservableProperty] private bool _isTrendPreviewNoticeOpen;
    [ObservableProperty] private string _trendPreviewNoticeSubtitle = "";
    [ObservableProperty] private string _trendPreviewNoticeBody = "";

    /// <summary>点卡片上的「预览」标识 / 首次自动提示：说明这张卡为什么是示例数据。</summary>
    [RelayCommand]
    private void OpenTrendPreviewNotice(string? key)
    {
        TrendPreviewNoticeSubtitle = key switch
        {
            "temp" => "「温度」卡片",
            "hum" => "「湿度」卡片",
            "power" => "「用电」卡片",
            "water" => "「用水」卡片",
            "gas" => "「用气」卡片",
            "tds" => "「水质」卡片",
            "scale" => "「体重」卡片",
            _ when AirMetrics.ByKey(key) is { } air => $"「{air.Title}」卡片",
            _ => "数据看板"
        };
        TrendPreviewNoticeBody = key switch
        {
            "temp" => "这张卡暂时没有能报温度的传感器，先展示一份示例数据：曲线、舒适度和全天高低值都是演示内容。接入支持温度读数的设备并同步后，卡片会自动换成真实读数；也可以在「编辑」里先关掉这张卡。",
            "hum" => "这张卡暂时没有能报湿度的设备，先展示一份示例数据：曲线和舒适度都是演示内容。接入支持湿度读数的设备并同步后，卡片会自动换成真实读数；也可以在「编辑」里先关掉这张卡。",
            "power" => "这张卡暂时没有能回读用电的设备（插座报功率、空调报累计电量的才会有），先展示一份按典型家庭作息生成的示例数据。接入这类设备并同步后，卡片会自动换成真实读数；也可以在「编辑」里先关掉这张卡。",
            "water" => "这张卡暂时没有能回读用水量的设备（燃气热水器、净水器才会报），先展示一份示例数据。接入这类设备并同步后，卡片会自动换成真实读数；也可以在「编辑」里先关掉这张卡。",
            "gas" => "这张卡暂时没有能回读用气量的燃气热水器，先展示一份示例数据。接入燃气热水器并同步后，卡片会自动换成真实读数；也可以在「编辑」里先关掉这张卡。",
            "tds" => "这张卡暂时没有能回读出水和进水 TDS 数值的净水器，先展示一份示例数据。接入净水器并同步后，卡片会自动换成真实读数；也可以在「编辑」里先关掉这张卡。",
            "scale" => "这张卡暂时没有体脂秤，先展示一份示例数据：最近一次称重和体重曲线都是演示内容。接入体脂秤并同步后，卡片会自动换成真实读数；也可以在「编辑」里先关掉这张卡。",
            _ when AirMetrics.ByKey(key) is { } air =>
                $"这张卡暂时没有能检测{air.Subject}的设备，先展示一份示例数据。接入能报{air.Subject}的设备并同步后，卡片会自动换成真实读数；也可以在「编辑」里先关掉这张卡。",
            _ => "这些卡片暂时没有对应的可用设备，先展示一份示例数据，方便你了解每张卡长什么样、能看哪些读数。接入对应设备并同步后，卡片会自动换成真实读数；暂时不需要的卡片可以在「编辑」里关掉。"
        };
        IsTrendPreviewNoticeOpen = true;
    }

    [RelayCommand]
    private void CloseTrendPreviewNotice() => IsTrendPreviewNoticeOpen = false;

    /// <summary>首次遇到预览卡时自动说明一次，之后只在用户点「预览」标识时再出现。</summary>
    private void OfferTrendPreviewNoticeOnce()
    {
        if (IsTrendPreview || IsTrendPreviewNoticeOpen) return;
        if (!HasVisibleTrendPreview || !IsLoggedIn) return;
        if (AppSettingsStore.TrendPreviewNoticeSeen) return;
        AppSettingsStore.SetTrendPreviewNoticeSeen();
        OpenTrendPreviewNotice(null);
    }

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

    // —— 用水卡：和用电卡同构（今日逐小时 / 近 7 天逐日），数据来自热水器水表或净水器累计制水 ——

    [ObservableProperty] private string _waterTitle = "用水";
    [ObservableProperty] private string _waterChartUnit = "L";
    /// <summary>大数字后面的单位：日常按升，月度 / 半年按吨（括号带上立方数，和米家账单一致）。</summary>
    [ObservableProperty] private string _waterValueUnit = "L（升）";
    [ObservableProperty] private bool _waterIsDailyBars;
    /// <summary>用水卡是柱状（净水器累计流量）还是曲线（热水器的逐单记录）。</summary>
    [ObservableProperty] private bool _waterIsBars = true;
    [ObservableProperty] private IReadOnlyList<double> _waterTrendSeries = Array.Empty<double>();
    [ObservableProperty] private string _waterTrendValue = "—";
    [ObservableProperty] private string _waterTrendNow = "";
    [ObservableProperty] private string _waterTrendDelta = "";
    [ObservableProperty] private string _waterTrendLow = "—";
    [ObservableProperty] private string _waterTrendHigh = "—";
    [ObservableProperty] private bool _hasWaterDelta;
    [ObservableProperty] private int _waterTrendFocus = -1;
    [ObservableProperty] private bool _showWaterChart;
    [ObservableProperty] private string _waterTrendPlaceholder = "";

    /// <summary>用水卡右侧小字：最少 / 最多多少升。</summary>
    public string WaterTrendRange => $"{WaterTrendLow} · {WaterTrendHigh}";

    partial void OnWaterTrendLowChanged(string value) => OnPropertyChanged(nameof(WaterTrendRange));
    partial void OnWaterTrendHighChanged(string value) => OnPropertyChanged(nameof(WaterTrendRange));

    [ObservableProperty] private IReadOnlyList<OptionItem> _waterRangeOptions = new List<OptionItem>
    {
        new() { Label = "今日", Value = "0" },
        new() { Label = "7 天", Value = "7" },
        new() { Label = "30 天", Value = "30" },
        new() { Label = "本月", Value = "month" },
        new() { Label = "半年", Value = "180" }
    };

    [ObservableProperty] private string _selectedWaterRange = AppSettingsStore.GetWaterRange();

    // —— 用气卡：燃气热水器每次洗完一单的用气量累加成气表，今日逐小时 / 近 7 天逐日 ——

    [ObservableProperty] private string _gasTitle = "用气";
    [ObservableProperty] private string _gasChartUnit = "m³";
    [ObservableProperty] private bool _gasIsDailyBars;
    /// <summary>用气卡画每日柱状：一天的用气量本来就是一个台阶，柱高比曲线好比较；半年那种上百格仍退回曲线。</summary>
    [ObservableProperty] private bool _gasIsBars;
    /// <summary>柱后那条参照虚线：上一等长窗口的日均用气；NaN 表示这期没有可比数据。</summary>
    [ObservableProperty] private double _gasTrendBaseline = double.NaN;
    [ObservableProperty] private IReadOnlyList<double> _gasTrendSeries = Array.Empty<double>();
    [ObservableProperty] private string _gasTrendValue = "—";
    [ObservableProperty] private string _gasTrendNow = "";
    [ObservableProperty] private string _gasTrendDelta = "";
    [ObservableProperty] private string _gasTrendLow = "—";
    [ObservableProperty] private string _gasTrendHigh = "—";
    [ObservableProperty] private bool _hasGasDelta;
    [ObservableProperty] private int _gasTrendFocus = -1;
    [ObservableProperty] private bool _showGasChart;
    [ObservableProperty] private string _gasTrendPlaceholder = "";

    /// <summary>用气卡右侧小字：最少 / 最多多少 m³。</summary>
    public string GasTrendRange => $"{GasTrendLow} · {GasTrendHigh}";

    partial void OnGasTrendLowChanged(string value) => OnPropertyChanged(nameof(GasTrendRange));
    partial void OnGasTrendHighChanged(string value) => OnPropertyChanged(nameof(GasTrendRange));

    [ObservableProperty] private IReadOnlyList<OptionItem> _gasRangeOptions = new List<OptionItem>
    {
        new() { Label = "今日", Value = "0" },
        new() { Label = "7 天", Value = "7" },
        new() { Label = "30 天", Value = "30" },
        new() { Label = "本月", Value = "month" },
        new() { Label = "半年", Value = "180" }
    };

    [ObservableProperty] private string _selectedGasRange = AppSettingsStore.GetGasRange();

    [ObservableProperty] private IReadOnlyList<OptionItem> _gasSources = Array.Empty<OptionItem>();
    [ObservableProperty] private string _selectedGasSource = AppSettingsStore.GetTrendSource("gas");
    [ObservableProperty] private bool _canPickGasSource;

    // —— 水质卡：只在家里有净水器时报 TDS，出水越低越纯净；曲线靠本地采样攒 ——

    [ObservableProperty] private string _waterQualityValue = "—";
    [ObservableProperty] private string _waterQualityNote = "";
    [ObservableProperty] private string _waterQualityFoot = "";
    [ObservableProperty] private string _waterQualityLow = "—";
    [ObservableProperty] private string _waterQualityHigh = "—";
    [ObservableProperty] private IReadOnlyList<double> _waterQualitySeries = Array.Empty<double>();
    [ObservableProperty] private bool _showWaterQualityChart;
    [ObservableProperty] private string _waterQualityPlaceholder = "";

    /// <summary>水质卡右侧小字：最低 / 最高 TDS，单位只写一次。</summary>
    public string WaterQualityRange => $"最低 {WaterQualityLow} · 最高 {WaterQualityHigh} ppm";

    partial void OnWaterQualityLowChanged(string value) => OnPropertyChanged(nameof(WaterQualityRange));
    partial void OnWaterQualityHighChanged(string value) => OnPropertyChanged(nameof(WaterQualityRange));

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

    /// <summary>卡片大标题固定是「用电」：看今天还是看近 30 天由右上角的范围下拉表示，标题不重复带时间口径。</summary>
    [ObservableProperty] private string _powerTitle = "用电";

    /// <summary>柱子单位：逐小时是 W，逐日是度。</summary>
    [ObservableProperty] private string _powerChartUnit = "W";

    /// <summary>柱子的横轴是一天还是一小时。</summary>
    [ObservableProperty] private bool _powerIsDailyBars;

    /// <summary>用电卡画柱还是画线：7 / 30 天用柱，半年 / 1 年用线（点数太多，柱子会挤成一片）。</summary>
    [ObservableProperty] private bool _powerIsBars = true;

    [ObservableProperty] private IReadOnlyList<OptionItem> _powerRangeOptions = new List<OptionItem>
    {
        new() { Label = "今日", Value = "0" },
        new() { Label = "7 天", Value = "7" },
        new() { Label = "30 天", Value = "30" },
        new() { Label = "半年", Value = "180" },
        new() { Label = "1 年", Value = "365" }
    };

    [ObservableProperty] private string _selectedPowerRange = AppSettingsStore.GetPowerRange().ToString();

    // —— 看板刷新节奏：编辑弹窗里可以设「更新频率」，只节流这块看板 ——

    /// <summary>
    /// 看板多久重算一次读数与曲线。0 = 跟随刷新（设备读数约 40 秒到一次，跟着它走最省）；
    /// 1 / 5 / 15 分钟则是隔这么久才重算，数字不会一直跳。设备卡与首页统计不受影响。
    /// </summary>
    [ObservableProperty] private IReadOnlyList<OptionItem> _boardRefreshOptions = new List<OptionItem>
    {
        new() { Label = "随设备刷新", Value = "0" },
        new() { Label = "1 分钟", Value = "1" },
        new() { Label = "5 分钟", Value = "5" },
        new() { Label = "15 分钟", Value = "15" }
    };

    [ObservableProperty] private string _selectedBoardRefresh = AppSettingsStore.GetDataBoardRefreshMinutes().ToString();

    /// <summary>上次重算看板的时刻；「跟随刷新」时不参与节流判断。</summary>
    private DateTime _lastTrendRefreshAt = DateTime.MinValue;

    /// <summary>到点没有：0 分钟（跟随刷新）永远算到点，还没算过也先算一次，别让看板空着。</summary>
    private bool BoardRefreshDue(DateTime now) =>
        AppSettingsStore.DataBoardRefreshMinutes <= 0
        || _lastTrendRefreshAt == DateTime.MinValue
        || now - _lastTrendRefreshAt >= TimeSpan.FromMinutes(AppSettingsStore.DataBoardRefreshMinutes);

    partial void OnSelectedBoardRefreshChanged(string value)
    {
        var minutes = int.TryParse(value, out var m) ? m : 0;
        AppSettingsStore.SetDataBoardRefreshMinutes(minutes);
        // 改完立刻按新节奏重算一次，不用等下一拍
        _lastTrendRefreshAt = DateTime.MinValue;
        RefreshTrendCards();
    }

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
    [ObservableProperty] private IReadOnlyList<OptionItem> _waterSources = Array.Empty<OptionItem>();
    [ObservableProperty] private IReadOnlyList<OptionItem> _waterQualitySources = Array.Empty<OptionItem>();

    /// <summary>每张卡各自选中的数据来源 did；空串 = 全屋口径（温湿度跟随环境概览、用电/用水取叠加）。</summary>
    [ObservableProperty] private string _selectedTempSource = AppSettingsStore.GetTrendSource("temp");
    [ObservableProperty] private string _selectedHumSource = AppSettingsStore.GetTrendSource("hum");
    [ObservableProperty] private string _selectedPowerSource = AppSettingsStore.GetTrendSource("power");
    [ObservableProperty] private string _selectedWaterSource = AppSettingsStore.GetTrendSource("water");
    [ObservableProperty] private string _selectedWaterQualitySource = AppSettingsStore.GetTrendSource("tds");

    /// <summary>只有一台能选时才值得挂下拉，否则「全屋」和那台设备是同一个数。</summary>
    [ObservableProperty] private bool _canPickTempSource;
    [ObservableProperty] private bool _canPickHumSource;
    [ObservableProperty] private bool _canPickPowerSource;
    [ObservableProperty] private bool _canPickWaterSource;
    [ObservableProperty] private bool _canPickWaterQualitySource;

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
            var water = d.WaterLiters;
            var tds = d.TdsOut;
            var gas = d.GasM3;
            if (t is null && h is null && w is null && e is null && water is null && tds is null && gas is null) continue;
            byDevice[d.TargetDid] = new EnvReading(t, h, w, e, water, tds, gas);
        }

        _envHistory.Sample(DateTime.Now, _liveTemp, _liveHumidity, power, byDevice);
        // 采样照旧落盘，只有卡片重算跟着「更新频率」走：调慢后数字不会每拍都动
        if (BoardRefreshDue(DateTime.Now)) RefreshTrendCards();
    }

    /// <summary>
    /// 重启后合成水表/气表别从 0 重来：用历史里这台设备最后一次累计值接上，
    /// 否则新采样的 0 会把旧曲线的正向增量全部卡住。
    /// </summary>
    private void RestoreMeterBaselines()
    {
        foreach (var d in _allDevices.Where(x => x.IsWaterDevice))
        {
            if (!d.WaterMeterInit && d.WaterLiters is null &&
                _envHistory.LatestDevice(d.TargetDid, r => r.WaterLiters) is { } water)
            {
                d.WaterMeterInit = true;
                d.WaterLiters = water;
            }

            if (!d.GasMeterInit && d.GasM3 is null &&
                _envHistory.LatestDevice(d.TargetDid, r => r.GasM3) is { } gas)
            {
                d.GasMeterInit = true;
                d.GasM3 = gas;
            }
        }
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

    /// <summary>
    /// 重算数据看板每张卡的文字与序列：有设备就用真实采样，没有设备的卡改铺一份示例数据并标「预览」。
    /// 没有历史时真实卡的序列为空、数值退回当前读数。
    /// </summary>
    /// <summary>
    /// 数据看板整拍重算完成。桌面数据卡、其他独立窗口都挂在这一拍上同步，
    /// 不再靠猜属性名去拼刷新时机。
    /// </summary>
    public event Action? TrendCardsUpdated;

    public void RefreshTrendCards()
    {
        try
        {
            RefreshTrendCardsCore();
        }
        finally
        {
            TrendCardsUpdated?.Invoke();
        }
    }

    private void RefreshTrendCardsCore()
    {
        var now = DateTime.Now;
        // 记下这一拍：开了「更新频率」后，轮询里的定时重算按这一刻起算
        _lastTrendRefreshAt = now;
        // 每类卡片有没有真实设备：显隐由「用户选择 / 默认全铺」决定，数据由「真实 / 演示」决定
        HasTempReading = _allDevices.Any(d => !d.IsWaterDevice && d.TemperatureC.HasValue);
        HasHumReading = _allDevices.Any(d => !d.IsWaterDevice && d.Humidity.HasValue);
        HasWaterMeter = _allDevices.Any(d => d.HasWaterMeter);
        HasWaterQuality = _allDevices.Any(d => d.TdsOut.HasValue);
        HasGasMeter = _allDevices.Any(d => d.HasGasMeter);
        HasScale = _allDevices.Any(IsScaleDevice);
        // 云端电量统计可能补出「只报累计电量」的设备（空调这类），用电卡到这一步才算真正定下来
        HasMetering = _allDevices.Any(d => d.PowerWatts.HasValue || d.EnergyKwh.HasValue
            || _cloudEnergy.ContainsKey(d.TargetDid));

        // 逐卡预览标记：整段演示，或缺这类设备时，用示例数据把卡片铺满
        IsTempPreview = IsTrendPreview || !HasTempReading;
        IsHumPreview = IsTrendPreview || !HasHumReading;
        IsPowerPreview = IsTrendPreview || !HasMetering;
        IsWaterPreview = IsTrendPreview || !HasWaterMeter;
        IsGasPreview = IsTrendPreview || !HasGasMeter;
        IsQualityPreview = IsTrendPreview || !HasWaterQuality;
        IsScalePreview = IsTrendPreview || !HasScale;

        // 空气小卡与上面这些大卡同一拍刷新：整段演示时也铺示例数据，卡片样式和真实态一致
        RefreshAirCards();

        ShowEnvTrend = DataBoardVisible;

        if (IsTrendPreview)
        {
            // 整段演示：没有设备可选，来源下拉全部收起；范围下拉保留，切范围会把示例数据换一种口径重画
            CanPickTempSource = false;
            CanPickHumSource = false;
            CanPickPowerSource = false;
            CanPickWaterSource = false;
            CanPickWaterQualitySource = false;
            ApplyPreviewData();
            EnvTrendCaption = "近 24 小时 · 每 5 分钟采样";
            return;
        }

        RebuildTrendSources();
        // 体脂秤记录走云端 eco 接口，半小时补拉一次；拉到后由 ApplyScaleData 回填卡片
        if (!IsScalePreview) _ = EnsureScaleHistoryAsync();

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
        if (IsTempPreview)
        {
            ApplyTempPreview(now);
        }
        else
        {
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
        }

        // 湿度
        if (IsHumPreview)
        {
            ApplyHumPreview(now);
        }
        else
        {
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
        }

        // 标题口径：只拿真实曲线判断——预览卡里的曲线不是采样来的，别拿它当「近 24 小时」的证据
        var tempChartReal = ShowTempChart && !IsTempPreview;
        var humChartReal = ShowHumChart && !IsHumPreview;
        if ((!IsTempPreview && tempWindow.Full) || (!IsHumPreview && humWindow.Full))
            EnvTrendCaption = "近 24 小时 · 每 5 分钟采样";
        else if (tempChartReal || humChartReal)
            EnvTrendCaption = $"近 {SpanText(tempChartReal ? tempWindow.Window : humWindow.Window)} · 每 5 分钟采样";
        else if (HasTempReading || HasHumReading)
            EnvTrendCaption = "正在积累 · 每 5 分钟采样";
        else if (HasWaterMeter || HasWaterQuality || HasGasMeter)
            EnvTrendCaption = "每 5 分钟采样";
        else
            EnvTrendCaption = ""; // 全是预览卡时，口径由各卡自己的范围和单位说明

        if (IsPowerPreview) ApplyPowerPreview(now); else RefreshPower(now);
        if (IsWaterPreview) ApplyWaterPreview(now); else RefreshWater(now);
        if (IsQualityPreview) ApplyQualityPreview(now); else RefreshWaterQuality(now);
        if (IsGasPreview) ApplyGasPreview(now); else RefreshGas(now);
        RefreshUsageRecords(now);
        if (IsScalePreview) ApplyScalePreview();

        ShowEnvTrend = DataBoardVisible;

        // 首次看到预览卡时说明一次，之后只在用户点「预览」标识时再出现
        OfferTrendPreviewNoticeOnce();
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
        var tempDevices = SortedSourceDevices(d => !d.IsWaterDevice && d.TemperatureC.HasValue);
        var humDevices = SortedSourceDevices(d => !d.IsWaterDevice && d.Humidity.HasValue);
        var powerDevices = SortedSourceDevices(d => d.PowerWatts.HasValue || d.EnergyKwh.HasValue);
        var waterDevices = SortedSourceDevices(d => d.HasWaterMeter);
        var gasDevices = SortedSourceDevices(d => d.HasGasMeter);
        var tdsDevices = SortedSourceDevices(d => d.TdsOut.HasValue);

        var signature = string.Join("|",
            string.Join(",", tempDevices.Select(SignatureOf)),
            string.Join(",", humDevices.Select(SignatureOf)),
            string.Join(",", powerDevices.Select(SignatureOf)),
            string.Join(",", waterDevices.Select(SignatureOf)),
            string.Join(",", gasDevices.Select(SignatureOf)),
            string.Join(",", tdsDevices.Select(SignatureOf)));

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
            SelectedWaterSource = StillThere(SelectedWaterSource, waterDevices);
            SelectedGasSource = StillThere(SelectedGasSource, gasDevices);
            // 只有一台设备时没有「全部设备叠加」这一项，直接落到它自己
            if (waterDevices.Count == 1) SelectedWaterSource = waterDevices[0].TargetDid;
            if (gasDevices.Count == 1) SelectedGasSource = gasDevices[0].TargetDid;
            // 水质没有「全部设备平均」这种口径：没有有效选择时落到第一台净水器
            SelectedWaterQualitySource = SelectedWaterQualitySource.Length > 0 &&
                tdsDevices.Any(d => d.TargetDid == SelectedWaterQualitySource)
                ? SelectedWaterQualitySource
                : tdsDevices.FirstOrDefault()?.TargetDid ?? "";
        }
        finally
        {
            _resettingSources = false;
        }

        TempSources = BuildSources(tempDevices);
        HumSources = BuildSources(humDevices);
        PowerSources = BuildSources(powerDevices, "全部设备（叠加）");
        // 水/气只有一台设备时不再给「全部设备（叠加）」：那一项和这台设备其实是同一个口径
        WaterSources = BuildSources(waterDevices, waterDevices.Count > 1 ? "全部设备（叠加）" : null);
        GasSources = BuildSources(gasDevices, gasDevices.Count > 1 ? "全部设备（叠加）" : null);
        WaterQualitySources = BuildSources(tdsDevices);

        // 热水器才有逐单记录，净水器只累计制水：范围选项跟着数据源走
        var waterLongRanges = waterDevices.Any(d => d.CategoryName == "water-heater");
        WaterRangeOptions = BuildRangeOptions(waterLongRanges);
        GasRangeOptions = BuildRangeOptions(true);
        if (!WaterRangeOptions.Any(o => o.Value.ToString() == SelectedWaterRange)) SelectedWaterRange = "0";
        if (!GasRangeOptions.Any(o => o.Value.ToString() == SelectedGasRange)) SelectedGasRange = "0";

        // 有可选设备就挂下拉：一张卡至少要能说清「这个数是哪来的」，只一台设备时也能点开看一眼
        CanPickTempSource = tempDevices.Count > 0;
        CanPickHumSource = humDevices.Count > 0;
        CanPickPowerSource = powerDevices.Count > 0;
        CanPickWaterSource = waterDevices.Count > 0;
        CanPickGasSource = gasDevices.Count > 0;
        CanPickWaterQualitySource = tdsDevices.Count > 0;
    }

    /// <summary>范围下拉：今日 / 7 天，热水器再补 30 天、本月与半年。</summary>
    private static IReadOnlyList<OptionItem> BuildRangeOptions(bool allowLong)
    {
        var list = new List<OptionItem>
        {
            new() { Label = "今日", Value = "0" },
            new() { Label = "7 天", Value = "7" }
        };
        if (allowLong)
        {
            list.Add(new OptionItem { Label = "30 天", Value = "30" });
            list.Add(new OptionItem { Label = "本月", Value = "month" });
            list.Add(new OptionItem { Label = "半年", Value = "180" });
        }
        return list;
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

    /// <summary>用电/用水/用气多一项「全部设备（叠加）」；只有一台设备时传 null，不再铺这一项。</summary>
    private static IReadOnlyList<OptionItem> BuildSources(IReadOnlyList<MijiaDevice> devices, string? allTitle)
    {
        var list = new List<OptionItem>(devices.Count + (allTitle is null ? 0 : 1));
        if (allTitle is not null)
            list.Add(new OptionItem { Label = allTitle, Value = "" });

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
    partial void OnSelectedWaterSourceChanged(string value) => OnSourceChanged("water", value);
    partial void OnSelectedGasSourceChanged(string value) => OnSourceChanged("gas", value);
    partial void OnSelectedWaterQualitySourceChanged(string value) => OnSourceChanged("tds", value);

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
        RefreshTrendCards();
    }

    /// <summary>
    /// 整段演示：把所有卡都铺成示例数据（双击标题行时用）。
    /// 只填 UI，绝不写进 <see cref="EnvHistoryStore"/>，也不会被当成真实读数。
    /// </summary>
    private void ApplyPreviewData()
    {
        var now = DateTime.Now;
        ApplyTempPreview(now);
        ApplyHumPreview(now);
        ApplyPowerPreview(now);
        ApplyWaterPreview(now);
        ApplyGasPreview(now);
        ApplyQualityPreview(now);
        ApplyScalePreview();
        EnvTrendCaption = "近 24 小时 · 每 5 分钟采样";
    }

    /// <summary>温度演示：曲线按一天里真实的冷暖节奏走（15 点最高、凌晨最低）。</summary>
    private void ApplyTempPreview(DateTime now)
    {
        var count = (int)(TrendWindow.TotalMinutes / TrendStepMinutes) + 1;
        var temps = DemoCurve(now, count, 21.4, 3.2, 15, 0.45);
        TempTrendSeries = temps;
        TempTrendPlaceholder = "";
        ShowTempChart = true;

        var tNow = temps[^1];
        TempTrendValue = $"{tNow:0.#}";
        TempTrendNote = TempComfortOf(tNow);
        TempTrendLow = MinText(temps, "℃");
        TempTrendHigh = MaxText(temps, "℃");
        TempTrendDelta = $"较昨日 {tNow - temps[0]:+0.0;-0.0;0.0}℃";
        HasTempDelta = true;
    }

    /// <summary>湿度演示：和温度反过来，凌晨最潮、午后最干。</summary>
    private void ApplyHumPreview(DateTime now)
    {
        var count = (int)(TrendWindow.TotalMinutes / TrendStepMinutes) + 1;
        var hums = DemoCurve(now, count, 52, 8, 3, 2.6);
        HumTrendSeries = hums;
        HumTrendPlaceholder = "";
        ShowHumChart = true;

        var hNow = hums[^1];
        HumTrendValue = $"{hNow:0}";
        HumTrendNote = HumComfortOf(hNow);
        HumTrendLow = MinText(hums, "%");
        HumTrendHigh = MaxText(hums, "%");
        HumTrendDelta = $"较昨日 {hNow - hums[0]:+0;-0;0}%";
        HasHumDelta = true;
    }

    /// <summary>用电演示：今日按典型家庭作息给逐小时平均功率，多天给每天几度。</summary>
    private void ApplyPowerPreview(DateTime now)
    {
        var rangeDays = PowerRangeDays;
        if (rangeDays > 0)
        {
            var daily = DemoPowerDays(now, rangeDays);
            PowerChartUnit = "度";
            PowerIsDailyBars = true;
            PowerIsBars = rangeDays <= 30;
            PowerTrendSeries = daily;
            PowerTrendPlaceholder = "";
            ShowPowerChart = true;
            PowerTrendFocus = daily.Length - 1;

            var total = daily.Sum();
            PowerTrendValue = $"{total:0.##}";
            PowerTrendNow = $"日均 {total / daily.Length:0.##} 度";
            PowerTrendDelta = "";
            HasPowerDelta = false;
            PowerTrendLow = $"最少 {daily.Min():0.##} 度";
            PowerTrendHigh = $"最多 {daily.Max():0.##} 度";
            return;
        }

        var bars = DemoPowerBars(now);
        PowerChartUnit = "W";
        PowerIsDailyBars = false;
        PowerIsBars = true;
        PowerTrendSeries = bars;
        PowerTrendPlaceholder = "";
        ShowPowerChart = true;

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

    /// <summary>用水演示：热水器一天两段洗澡高峰；多天 / 本月按每天用了多少升，大范围换算成吨。</summary>
    private void ApplyWaterPreview(DateTime now)
    {
        WaterTrendPlaceholder = "";
        WaterTrendDelta = "";
        HasWaterDelta = false;

        var rangeDays = WaterRangeDays;
        var isMonth = WaterIsMonthRange;
        var useM3 = isMonth || rangeDays >= 30;
        var length = isMonth ? now.Day : rangeDays;

        if (length > 0)
        {
            var daily = DemoDailyUsage(now, length, 196, 38);
            var series = useM3 ? daily.Select(v => v / 1000.0).ToArray() : daily;
            WaterIsDailyBars = true;
            WaterIsBars = false; // 和接了燃气热水器的真卡一致：多天是折线，不是一天一根柱
            WaterChartUnit = useM3 ? "吨" : "L";
            WaterValueUnit = useM3 ? "m³（吨）" : "L（升）";
            WaterTrendSeries = series;
            ShowWaterChart = true;
            WaterTrendFocus = series.Length - 1;

            var dailyTotal = series.Sum();
            var format = isMonth ? "0.##" : useM3 ? "0.###" : "0.#";
            WaterTrendValue = dailyTotal.ToString(format);
            WaterTrendNow = $"日均 {daily.Sum() / daily.Length:0.#} L";
            WaterTrendLow = $"最少 {series.Min().ToString(format)} {WaterChartUnit}";
            WaterTrendHigh = $"最多 {series.Max().ToString(format)} {WaterChartUnit}";
            return;
        }

        var bars = DemoWaterBars(now);
        WaterIsBars = true;
        WaterIsDailyBars = false;
        WaterChartUnit = "L";
        WaterValueUnit = "L（升）";
        WaterTrendSeries = bars;
        ShowWaterChart = true;

        var focusHour = -1;
        var total = 0.0;
        var lo = double.MaxValue;
        var hi = double.MinValue;
        for (var i = 0; i < bars.Length; i++)
        {
            var v = bars[i];
            if (double.IsNaN(v)) continue;
            focusHour = i;
            total += v;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        WaterTrendFocus = focusHour;
        WaterTrendValue = $"{total:0.#}";
        WaterTrendNow = "本次 6.4 L · 8.4 L/min";
        WaterTrendDelta = "较昨日 +9%";
        HasWaterDelta = true;
        WaterTrendLow = lo == double.MaxValue ? "—" : $"最少 {lo:0.#} L";
        WaterTrendHigh = hi == double.MinValue ? "—" : $"最多 {hi:0.#} L";
    }

    /// <summary>用气演示：和用水高峰同步，晚上洗澡那一单气量最大；多天 / 本月按每天几 m³。</summary>
    private void ApplyGasPreview(DateTime now)
    {
        GasChartUnit = "m³";
        GasTrendPlaceholder = "";
        GasTrendDelta = "";
        HasGasDelta = false;

        var rangeDays = GasRangeDays;
        var isMonth = GasIsMonthRange;
        var length = isMonth ? now.Day : rangeDays;

        if (length > 0)
        {
            var daily = DemoDailyUsage(now, length, 2.1, 0.34);
            // 一天一根柱子：用量是台阶，柱高才好比；半年上百格仍走曲线，和真实卡一致
            GasIsBars = rangeDays != 180;
            GasIsDailyBars = true;
            GasTrendBaseline = double.NaN;
            GasTrendSeries = daily;
            ShowGasChart = true;
            GasTrendFocus = GasIsBars ? daily.Length - 1 : -1;

            var dailyTotal = daily.Sum();
            GasTrendValue = $"{dailyTotal:0.###}";
            GasTrendNow = $"日均 {dailyTotal / daily.Length:0.###} m³";
            GasTrendLow = $"最少 {daily.Min():0.###} m³";
            GasTrendHigh = $"最多 {daily.Max():0.###} m³";
            return;
        }

        var bars = DemoGasBars(now);
        GasIsBars = true;
        GasIsDailyBars = false;
        GasTrendBaseline = double.NaN;
        GasTrendSeries = bars;
        ShowGasChart = true;

        var focusHour = -1;
        var total = 0.0;
        var lo = double.MaxValue;
        var hi = double.MinValue;
        for (var i = 0; i < bars.Length; i++)
        {
            var v = bars[i];
            if (double.IsNaN(v)) continue;
            focusHour = i;
            total += v;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        GasTrendFocus = focusHour;
        GasTrendValue = $"{total:0.###}";
        GasTrendNow = "本次 0.42 m³";
        GasTrendDelta = "较昨日 +7%";
        HasGasDelta = true;
        GasTrendLow = lo == double.MaxValue ? "—" : $"最少 {lo:0.###} m³";
        GasTrendHigh = hi == double.MinValue ? "—" : $"最多 {hi:0.###} m³";
    }

    /// <summary>水质演示：出水 TDS 在 12 上下小幅波动，右侧小字带进水与滤芯。</summary>
    private void ApplyQualityPreview(DateTime now)
    {
        var count = (int)(TrendWindow.TotalMinutes / TrendStepMinutes) + 1;
        var tdsSeries = DemoCurve(now, count, 12.4, 1.6, 13, 0.22);
        WaterQualitySeries = tdsSeries;
        ShowWaterQualityChart = true;
        WaterQualityPlaceholder = "";
        WaterQualityValue = $"{tdsSeries[^1]:0}";
        WaterQualityNote = WaterQualityNoteOf(tdsSeries[^1]);
        WaterQualityFoot = "进水 118 ppm · 滤芯 78%";
        WaterQualityLow = MinText(tdsSeries, "");
        WaterQualityHigh = MaxText(tdsSeries, "");
    }

    /// <summary>
    /// 体重演示：真人不会每天上秤，按范围只给 9~14 次称重，点与点之间留出间隔；
    /// 体重整体缓慢下降，但每次称重允许上下浮动，不是一条笔直的下坡线。
    /// </summary>
    private void ApplyScalePreview()
    {
        CanPickScaleMember = false;
        var rangeDays = Math.Max(30, ScaleRangeDays);
        var count = rangeDays switch
        {
            <= 30 => 9,
            <= 90 => 11,
            <= 180 => 12,
            _ => 14
        };

        var today = DateTime.Today;
        var step = (rangeDays - 1.0) / (count - 1);
        var startWeight = 68.9;
        var drop = rangeDays <= 30 ? 0.9 : rangeDays <= 90 ? 1.3 : rangeDays <= 180 ? 1.7 : 2.3;

        var weights = new double[count];
        var dates = new List<DateTime>(count);
        for (var i = 0; i < count; i++)
        {
            dates.Add(today.AddDays(-(int)Math.Round((count - 1 - i) * step)));
            var wobble = Math.Sin(i * 1.35) * 0.38
                + Math.Sin(i * 0.55 + 1.7) * 0.24
                + Math.Sin(i * 2.6) * 0.08;
            weights[i] = startWeight - i * (drop / (count - 1)) + wobble;
        }

        ScaleTrendSeries = weights;
        ScaleTrendLows = Array.Empty<double>();
        ScaleTrendHighs = Array.Empty<double>();
        ScaleTrendLabels = dates
            .Select(d => d.Year == today.Year ? d.ToString("M/d") : d.ToString("yyyy/M/d"))
            .ToList();
        ShowScaleChart = true;
        ScaleTrendPlaceholder = "";
        ScaleTrendValue = $"{weights[^1]:0.0}";
        ScaleTrendNote = "今天 07:20 称重";
        ScaleTrendMeta = "体脂 19.4% · BMI 23.6";
        ScaleTrendRange = $"{ScaleWindowText(rangeDays)} {weights.Min():0.0}–{weights.Max():0.0}kg";
        var dw = weights[^1] - weights[^2];
        ScaleTrendDelta = $"较上次 {dw:+0.0;-0.0;0.0}kg";
        HasScaleDelta = Math.Abs(dw) >= 0.05;
    }

    /// <summary>每天几度的示例值：冬天略高、夏天偏低，日与日之间有起伏；今天还没过完，给个偏低的数。</summary>
    private static double[] DemoPowerDays(DateTime now, int days)
    {
        var pts = new double[days];
        var start = now.Date.AddDays(-(days - 1));
        for (var i = 0; i < days; i++)
        {
            var date = start.AddDays(i);
            var seasonal = Math.Cos((date.DayOfYear - 200) / 365.0 * Math.PI * 2) * 1.3;
            var wobble = Math.Sin(i * 1.7) * 0.32 + Math.Sin(i * 0.42 + 0.8) * 0.24;
            pts[i] = 6.4 + seasonal + wobble;
            if (date == now.Date) pts[i] *= 0.72;
        }
        return pts;
    }

    /// <summary>每天用量的示例值（水按升、气按 m³ 量级）：周末略高，日与日之间有起伏，今天还没过完。</summary>
    private static double[] DemoDailyUsage(DateTime now, int days, double mean, double amp)
    {
        var pts = new double[days];
        var start = now.Date.AddDays(-(days - 1));
        for (var i = 0; i < days; i++)
        {
            var date = start.AddDays(i);
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.06 : 1.0;
            var value = mean + Math.Sin(i * 0.9) * amp * 0.55 + Math.Sin(i * 0.23 + 1.4) * amp * 0.35;
            pts[i] = Math.Max(0, value * weekend);
        }
        if (days > 1) pts[^1] *= 0.85; // 今天还没过完
        return pts;
    }

    /// <summary>
    /// 一天的温湿度演示曲线：慢的昼夜起伏 + 三层不同周期的抖动，读起来像真实探头而不是一根光滑正弦。
    /// </summary>
    private static double[] DemoCurve(DateTime now, int count, double mean, double amp, double peakHour, double jitter)
    {
        var pts = new double[count];
        var start = now.AddMinutes(-(count - 1) * TrendStepMinutes);
        for (var i = 0; i < count; i++)
        {
            var hour = start.AddMinutes(i * TrendStepMinutes).TimeOfDay.TotalHours;
            var phase = (hour - peakHour) / 24.0 * Math.PI * 2;
            var wobble = Math.Sin(i * 0.22) * jitter
                + Math.Sin(i * 0.55 + 1.1) * jitter * 0.45
                + Math.Sin(i * 2.4 + 0.6) * jitter * 0.22;
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

    private static double[] DemoWaterBars(DateTime now)
    {
        // 每小时用水（L）：早起洗漱、晚上洗澡两段高峰，夜里为零
        double[] profile =
        [
            1, 0, 0, 0, 0, 1, 16, 38, 9, 3, 1, 2,
            5, 2, 1, 1, 2, 6, 22, 48, 28, 9, 3, 1
        ];
        var bars = new double[24];
        for (var i = 0; i < 24; i++)
            bars[i] = i <= now.Hour ? profile[i] : double.NaN;
        return bars;
    }

    private static double[] DemoGasBars(DateTime now)
    {
        // 每小时用气（m³）：早上洗漱、晚上洗澡两段高峰，夜里只有点火待机
        double[] profile =
        [
            0.001, 0, 0, 0, 0, 0.001, 0.03, 0.12, 0.04, 0.01, 0.002, 0.003,
            0.02, 0.01, 0.004, 0.003, 0.005, 0.02, 0.08, 0.35, 0.18, 0.05, 0.012, 0.002
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

        PowerChartUnit = "W";
        PowerIsDailyBars = false;
        PowerIsBars = true;

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

    // ===== 用水卡 =====

    /// <summary>用水卡当前范围：今日逐小时，或近 7 / 30 天逐日、本月逐日。</summary>
    private int WaterRangeDays => ParseRangeDays(SelectedWaterRange);
    private bool WaterIsMonthRange => SelectedWaterRange == "month";

    private static int ParseRangeDays(string raw) =>
        int.TryParse(raw, out var n) && n is 30 or 180 ? n : n == 7 ? 7 : 0;

    // ===== 范围的智能取窗 =====

    /// <summary>「这几天有数据」的最低标准：不足 3 天就当这段是空的，该后推 / 放大范围。</summary>
    private const int DayWindowMinPoints = 3;

    /// <summary>
    /// 数据没铺满窗口时，空出超过一周的那一头才收口到第一 / 最后一天；
    /// 只差几天就保持原窗口，免得为了两天空缺改掉「近半年」的口径。
    /// </summary>
    private const int TrimGapDays = 7;

    /// <summary>放大范围时按这串档位往上走，和下拉里的选项对齐。</summary>
    private static readonly int[] PowerSpanLadder = { 7, 30, 180, 365 };
    private static readonly int[] UsageSpanLadder = { 7, 30, 180 };

    /// <summary>
    /// 把「用户选的天数」落到真有数据的日期上：先以今天为终点，够 3 天就原样用；
    /// 不够就把窗口整体后推到最后一笔记录（跨度不变）；再不够就沿档位放大跨度。
    /// 返回实际窗口，卡片标题、累计值和「日均」都按这个口径算。
    /// </summary>
    private static (DateTime End, int Span) FitDayWindow(int preferred, DateTime today, DateTime? lastDataDay,
        Func<DateTime, int, int> count, IReadOnlyList<int> ladder)
    {
        var end = today;
        var span = preferred;
        if (lastDataDay is null) return (end, span);

        if (count(end, span) < DayWindowMinPoints && lastDataDay.Value.Date < today)
            end = lastDataDay.Value.Date;

        if (count(end, span) >= DayWindowMinPoints) return (end, span);

        // 放大只在「真的能多出点东西」时生效：新装的设备只有一天数据，
        // 别把 7 天硬撑成一年，那样图上还是一根柱子，只是横轴拉得更空
        foreach (var bigger in ladder)
        {
            if (bigger <= preferred) continue;
            if (count(end, bigger) >= DayWindowMinPoints) return (end, bigger);
        }
        return (end, span);
    }

    /// <summary>
    /// 数据没铺满窗口时把空边收口到第一 / 最后一天：两头空得都不足一周就保持原窗口。
    /// 只裁两头，中间的空档照旧留着——那是真的没记录，不该被抹平。
    /// </summary>
    private static (DateTime Start, int Length) TrimToData(DateTime start, int length, Func<int, bool> hasData)
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < length; i++)
        {
            if (!hasData(i)) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return (start, length);

        if (first < TrimGapDays && length - 1 - last < TrimGapDays) return (start, length);
        return (start.AddDays(first), last - first + 1);
    }

    /// <summary>水 / 气真正用过的日子：云端日统计和逐单记录里任一有大干零的那天。</summary>
    private static HashSet<DateTime> UsageDataDays(IReadOnlyList<MijiaDevice> devices,
        IReadOnlyDictionary<string, StatSeries> stats, IReadOnlyList<UsageRecord> records, bool water)
    {
        var days = new HashSet<DateTime>();
        foreach (var d in devices)
        {
            if (!stats.TryGetValue(d.TargetDid, out var stat) || !stat.Ok) continue;
            foreach (var (time, value) in stat.Items)
                if (value > 0) days.Add(time.Date);
        }
        foreach (var r in records)
            if ((water ? r.WaterL : r.GasL) > 0) days.Add(r.Time.Date);
        return days;
    }

    private static int CountUsageDays(IReadOnlyCollection<DateTime> dataDays, DateTime end, int span)
    {
        var start = end.Date.AddDays(-(span - 1));
        var count = 0;
        foreach (var day in dataDays)
            if (day >= start && day <= end.Date) count++;
        return count;
    }

    /// <summary>
    /// 「本月」没记录时往前翻：从本月开始一个月一个月往回找，哪个月有 3 天以上用过水 / 气就用哪个月。
    /// 本月的长度按已过天数算，往月的长度按整月算；返回的起点也决定标题上的月份。
    /// </summary>
    private static (DateTime Start, int Length) FitUsageMonth(IReadOnlyCollection<DateTime> dataDays, DateTime now)
    {
        for (var back = 0; back < 12; back++)
        {
            var start = new DateTime(now.Year, now.Month, 1).AddMonths(-back);
            var length = back == 0 ? now.Day : DateTime.DaysInMonth(start.Year, start.Month);
            if (CountUsageDays(dataDays, start.AddDays(length - 1), length) >= DayWindowMinPoints)
                return (start, length);
        }
        return (new DateTime(now.Year, now.Month, 1), now.Day);
    }

    partial void OnSelectedWaterRangeChanged(string value)
    {
        AppSettingsStore.SetWaterRange(SelectedWaterRange);
        if (_resettingSources) return;
        // 换到 30 天 / 本月时缓存可能还没覆盖那么早，别等十分钟的刷新窗口
        _usageFetchAt = DateTime.MinValue;
        RefreshTrendCards();
    }

    /// <summary>
    /// 用水卡：今天按小时看用了多少升，或近 7 天每天用了多少。
    /// 水表是累计计数器，取相邻采样的正向差值；没有水表的设备不进这张卡。
    /// </summary>
    private void RefreshWater(DateTime now)
    {
        var devices = _allDevices.Where(d => d.HasWaterMeter).ToList();
        HasWaterMeter = devices.Count > 0;

        var did = SelectedWaterSource;
        var single = did.Length == 0 ? null : devices.FirstOrDefault(d => d.TargetDid == did);
        var sources = did.Length == 0
            ? devices
            : single is { } one ? new List<MijiaDevice> { one } : new List<MijiaDevice>();

        var noun = single is not null
            ? (single.CategoryName == "water-purifier" ? "制水" : "用水")
            : devices.Count > 0 && devices.All(d => d.CategoryName == "water-purifier")
                ? "制水"
                : "用水";
        var rangeDays = WaterRangeDays;
        var isMonth = WaterIsMonthRange;
        var heaters = devices.Where(d => d.CategoryName == "water-heater").ToList();
        var pickedHeaters = did.Length == 0
            ? heaters
            : heaters.Where(d => d.TargetDid == did).ToList();

        // 燃气热水器能拿到米家云端的逐单记录：整张卡就用这份数据，和温度/湿度卡一样只有一张图
        if (pickedHeaters.Count > 0)
        {
            RefreshWaterFromRecords(now, sources, pickedHeaters, rangeDays, isMonth, noun);
            return;
        }

        // 只有净水器（累计制水）时仍按本地采样，历史只留 7 天
        if (rangeDays > 7 || isMonth) rangeDays = 0;
        WaterTitle = noun;
        WaterChartUnit = "L";
        WaterValueUnit = "L（升）";
        WaterIsBars = true;
        WaterIsDailyBars = rangeDays > 0;

        if (rangeDays > 0)
        {
            RefreshWaterDays(now, did, noun);
            return;
        }

        var bars = _envHistory.HourlyWater(now, did);
        var hasUse = false;
        foreach (var v in bars)
            if (!double.IsNaN(v) && v > 0.01) { hasUse = true; break; }

        ShowWaterChart = HasWaterMeter && hasUse;
        WaterTrendPlaceholder = !HasWaterMeter
            ? "未发现能回读用水量的设备（燃气热水器、净水器才会报）"
            : ShowWaterChart
                ? ""
                : did.Length > 0
                    ? $"这台设备今天还没有{noun}记录"
                    : $"今天还没有{noun}记录";
        WaterTrendSeries = ShowWaterChart ? bars : Array.Empty<double>();

        var usage = _envHistory.Water(now.Date, now, did);
        var fullDay = usage.Coverage >= 0.75;
        WaterTrendValue = usage.Coverage > 0 ? $"{usage.Liters:0.#}" : "—";

        // 大数字只装「从开始记录到现在」的已用升数，右侧小字把起点交代清楚；
        // 正在用水时优先报当前流量，避免把「这一单还没记完」误读成总用量
        if (!fullDay && usage.Coverage > 0)
        {
            var since = now - usage.Span;
            var flowText = WaterFlowText(sources);
            var live = flowText.Length > 0 ? flowText : WaterBriefNote(sources, did);
            WaterTrendNow = $"自 {since:HH:mm} 起" +
                (live.Length > 0 ? $" · {live}" : $" · 已用 {usage.Liters:0.#} L");
        }
        else
        {
            WaterTrendNow = WaterLiveNote(sources, did);
        }

        var yesterday = _envHistory.Water(now.Date.AddDays(-1), now.Date, did);
        var canCompare = fullDay && yesterday.Coverage >= 0.75 && yesterday.Liters >= 1;
        if (canCompare)
        {
            var percent = (usage.Liters - yesterday.Liters) / yesterday.Liters * 100;
            WaterTrendDelta = $"较昨日 {percent:+0;-0;0}%";
            HasWaterDelta = true;
        }
        else
        {
            WaterTrendDelta = "";
            HasWaterDelta = false;
        }

        var focus = -1;
        if (ShowWaterChart)
        {
            for (var i = Math.Min(now.Hour, bars.Length - 1); i >= 0; i--)
                if (!double.IsNaN(bars[i])) { focus = i; break; }
        }
        WaterTrendFocus = focus;

        if (!ShowWaterChart)
        {
            WaterTrendLow = "—";
            WaterTrendHigh = "—";
            return;
        }

        var low = double.MaxValue;
        var high = double.MinValue;
        foreach (var v in bars)
        {
            if (double.IsNaN(v) || v <= 0.01) continue;
            if (v < low) low = v;
            if (v > high) high = v;
        }
        WaterTrendLow = low == double.MaxValue ? "—" : $"最少 {low:0.#} L";
        WaterTrendHigh = high == double.MinValue ? "—" : $"最多 {high:0.#} L";
    }

    /// <summary>
    /// 用水卡（有燃气热水器时）：优先用米家云端日统计（和「使用记录」页同一份），
    /// 还没拉到统计时退回逐单事件缓存。今日按小时（事件），7 / 30 天、本月、半年按天（统计）；
    /// 月度口径按「吨（m³）」，和米家账单一致。
    /// 选中的跨度里没有记录时，窗口会整体后推到最后一笔用水，再不够就放大跨度——
    /// 热水器关了一个月，这张卡也不该只剩空坐标轴。
    /// </summary>
    private void RefreshWaterFromRecords(DateTime now, IReadOnlyList<MijiaDevice> sources,
        List<MijiaDevice> heaters, int rangeDays, bool isMonth, string noun)
    {
        var records = heaters.SelectMany(d => UsageRecordStore.Cached(d.TargetDid)).ToList();
        var dataDays = UsageDataDays(heaters, _waterDayStats, records, water: true);

        DateTime start;
        var length = 0;
        if (isMonth)
        {
            (start, length) = FitUsageMonth(dataDays, now);
        }
        else if (rangeDays > 0)
        {
            var (end, span) = FitDayWindow(rangeDays, now.Date,
                dataDays.Count > 0 ? dataDays.Max() : (DateTime?)null,
                (windowEnd, windowSpan) => CountUsageDays(dataDays, windowEnd, windowSpan),
                UsageSpanLadder);
            start = end.AddDays(-(span - 1));
            length = span;
            rangeDays = span;
        }
        else
        {
            start = now.Date;
        }

        // 半年里只有最近几天用过水时，横轴收口到首尾有记录的那天，
        // 否则柱子全挤在右端，左边一大截空着
        if (rangeDays > 0 && length > 0)
        {
            var trimmed = TrimToData(start, length, i => dataDays.Contains(start.AddDays(i)));
            if (trimmed.Length != length)
            {
                start = trimmed.Start;
                length = trimmed.Length;
            }
        }

        var stats = rangeDays > 0 || isMonth
            ? StatsDailySeries(heaters, _waterDayStats, start, length, water: true)
            : null;
        var seriesLiters = stats ?? UsageSeries(records, now, start, length, water: true);
        var useM3 = isMonth || rangeDays >= 30;
        var series = useM3 ? seriesLiters.Select(v => v / 1000.0).ToArray() : seriesLiters;
        var total = series.Sum();
        var floor = useM3 ? 0.0005 : 0.01;
        var hasUse = total > floor;
        var format = isMonth ? "0.##" : useM3 ? "0.###" : "0.#";

        WaterTitle = noun;
        WaterIsBars = false;
        WaterIsDailyBars = isMonth || rangeDays > 0;
        WaterChartUnit = useM3 ? "吨" : "L";
        WaterValueUnit = useM3 ? "m³（吨）" : "L（升）";
        ShowWaterChart = hasUse;
        WaterTrendSeries = hasUse ? series : Array.Empty<double>();
        WaterTrendPlaceholder = hasUse
            ? ""
            : isMonth || rangeDays > 0
                ? $"这段时间还没有{noun}记录"
                : $"今天还没有{noun}记录";
        WaterTrendValue = hasUse ? total.ToString(format) : "—";
        WaterTrendNow = WaterLiveNote(sources, SelectedWaterSource);

        var yesterday = rangeDays == 0 && !isMonth && hasUse
            ? RecordDayTotal(records, now.Date.AddDays(-1), water: true)
            : 0;
        if (!useM3 && yesterday > 0.5)
        {
            var percent = (total - yesterday) / yesterday * 100;
            WaterTrendDelta = $"较昨日 {percent:+0;-0;0}%";
            HasWaterDelta = true;
        }
        else
        {
            WaterTrendDelta = "";
            HasWaterDelta = false;
        }

        WaterTrendFocus = -1;
        var range = BucketRangeText(series, useM3 ? "吨" : "L", format, floor);
        WaterTrendLow = hasUse ? range.Low : "—";
        WaterTrendHigh = hasUse ? range.High : "—";
    }

    /// <summary>把逐单记录按范围聚合成曲线：今日每小时一个点，多天每天一个点。</summary>
    private static double[] UsageSeries(IReadOnlyList<UsageRecord> records, DateTime now,
        DateTime start, int length, bool water)
    {
        if (length <= 0)
        {
            var hours = Math.Clamp(now.Hour + 1, 1, 24);
            var hourly = new double[hours];
            foreach (var r in records)
            {
                if (r.Time.Date != now.Date) continue;
                var idx = r.Time.Hour;
                if (idx < 0 || idx >= hours) continue;
                hourly[idx] += water ? r.WaterL : r.GasL / 1000.0;
            }
            return hourly;
        }

        var series = new double[length];
        var startDay = start.Date;
        foreach (var r in records)
        {
            var idx = (r.Time.Date - startDay).Days;
            if (idx < 0 || idx >= length) continue;
            series[idx] += water ? r.WaterL : r.GasL / 1000.0;
        }
        return series;
    }

    /// <summary>
    /// 米家云端日统计（stat_day_v3，prop.7.1 / prop.7.2）按范围聚成每天一格：
    /// 值本身就是当天总量（水是升、气也是升），不需要再按事件逐单累加。
    /// 返回 null 表示这几台设备还没拉到统计，交给事件记录兜底。
    /// </summary>
    private static double[]? StatsDailySeries(IReadOnlyList<MijiaDevice> heaters,
        IReadOnlyDictionary<string, StatSeries> stats, DateTime start, int length, bool water)
    {
        if (length <= 0) return null;
        var series = new double[length];
        var any = false;

        foreach (var d in heaters)
        {
            if (!stats.TryGetValue(d.TargetDid, out var stat) || !stat.Ok) continue;
            foreach (var (time, value) in stat.Items)
            {
                var idx = (time.Date - start.Date).Days;
                if (idx < 0 || idx >= length) continue;
                series[idx] += water ? value : value / 1000.0;
                any = true;
            }
        }

        return any ? series : null;
    }

    /// <summary>
    /// 上一等长窗口的日均（柱后那条参照虚线）。窗口里记录的天数不到一半，就当没有可比数据。
    /// </summary>
    private static double PreviousWindowDailyAverage(IReadOnlyList<MijiaDevice> heaters,
        IReadOnlyDictionary<string, StatSeries> stats, DateTime start, int length, bool water)
    {
        if (length <= 0) return double.NaN;
        var from = start.AddDays(-length);
        var to = start.AddDays(-1);
        var sum = 0.0;
        var days = new HashSet<DateTime>();
        foreach (var d in heaters)
        {
            if (!stats.TryGetValue(d.TargetDid, out var stat) || !stat.Ok) continue;
            foreach (var (time, value) in stat.Items)
            {
                if (time.Date < from || time.Date > to) continue;
                sum += water ? value : value / 1000.0;
                days.Add(time.Date);
            }
        }
        return days.Count * 2 >= length ? sum / length : double.NaN;
    }

    private static double RecordDayTotal(IReadOnlyList<UsageRecord> records, DateTime day, bool water) =>
        records.Where(r => r.Time.Date == day.Date)
            .Sum(r => water ? r.WaterL : r.GasL / 1000.0);

    /// <summary>曲线里单点最少 / 最多：跳过没用水（没点火）的那些天。</summary>
    private static (string Low, string High) BucketRangeText(double[] series, string unit, string format,
        double floor)
    {
        var low = double.MaxValue;
        var high = double.MinValue;
        foreach (var v in series)
        {
            if (v <= floor) continue;
            if (v < low) low = v;
            if (v > high) high = v;
        }
        if (low == double.MaxValue) return ("—", "—");
        return ($"最少 {low.ToString(format)} {unit}", $"最多 {high.ToString(format)} {unit}");
    }

    /// <summary>近 7 天逐日用水：每根柱子是那一天一共用了多少升。</summary>
    private void RefreshWaterDays(DateTime now, string did, string noun)
    {
        const int days = 7;
        var bars = new double[days];
        Array.Fill(bars, double.NaN);
        var hasData = false;
        var hasUse = false;

        for (var i = 0; i < days; i++)
        {
            var day = now.Date.AddDays(i - (days - 1));
            var end = day >= now.Date ? now : day.AddDays(1);
            var usage = _envHistory.Water(day, end, did);
            if (usage.Coverage <= 0) continue;
            bars[i] = usage.Liters;
            hasData = true;
            if (usage.Liters > 0.01) hasUse = true;
        }

        ShowWaterChart = HasWaterMeter && hasUse;
        WaterTrendPlaceholder = !HasWaterMeter
            ? "未发现能回读用水量的设备（燃气热水器、净水器才会报）"
            : hasUse
                ? ""
                : did.Length > 0
                    ? $"这台设备这段时间还没有{noun}记录"
                    : $"这段时间还没有{noun}记录";
        WaterTrendSeries = ShowWaterChart ? bars : Array.Empty<double>();

        var focus = -1;
        for (var i = 0; i < bars.Length; i++)
            if (!double.IsNaN(bars[i])) focus = i;
        WaterTrendFocus = focus;

        var total = 0.0;
        var counted = 0;
        for (var i = 0; i < bars.Length; i++)
        {
            if (double.IsNaN(bars[i])) continue;
            total += bars[i];
            counted++;
        }

        WaterTrendValue = hasData ? $"{total:0.#}" : "—";
        WaterTrendNow = !hasUse
            ? ""
            : counted < days
                ? $"{counted} 天有记录 · 日均 {total / counted:0.#} L"
                : $"日均 {total / days:0.#} L";

        // 逐日范围和「较昨日」不是一个口径，切换期间先不挂这枚对比胶囊
        WaterTrendDelta = "";
        HasWaterDelta = false;

        var low = double.MaxValue;
        var high = double.MinValue;
        for (var i = 0; i < bars.Length; i++)
        {
            if (double.IsNaN(bars[i]) || bars[i] <= 0.01) continue;
            if (bars[i] < low) low = bars[i];
            if (bars[i] > high) high = bars[i];
        }
        WaterTrendLow = low == double.MaxValue ? "—" : $"最少 {low:0.#} L";
        WaterTrendHigh = high == double.MinValue ? "—" : $"最多 {high:0.#} L";
    }

    /// <summary>当前水流量汇总：几台设备同时放热水就把流量加起来，没有就空串。</summary>
    private static string WaterFlowText(IReadOnlyList<MijiaDevice> sources)
    {
        var flow = sources.Where(d => d.IsOnline && d.WaterFlowLpm.HasValue).Sum(d => d.WaterFlowLpm!.Value);
        return flow > 0.05 ? $"当前 {flow:0.#} L/min" : "";
    }

    /// <summary>用水卡右侧那句实时说明：优先当前水流量，其次水温 / 工作状态，净水器报 TDS。</summary>
    private static string WaterLiveNote(IReadOnlyList<MijiaDevice> sources, string did)
    {
        if (sources.Count == 0) return "";

        var online = sources.Where(d => d.IsOnline).ToList();
        if (online.Count == 0) return "设备离线";

        if (did.Length > 0 && online.Count == 1)
        {
            var d = online[0];
            if (d.WaterFlowLpm is { } flow && flow > 0.05)
            {
                // 这次洗到一半时：本次已放多少 + 当前流量（水温单独留给下一句，别挤在一行）
                var prefix = d.SessionWaterLiters is { } session && session >= 0.1
                    ? $"本次 {session:0.#} L · "
                    : "当前 ";
                return $"{prefix}{flow:0.#} L/min";
            }
            if (d.IsUsingWater) return "正在用水";
            if (d.WaterStatus == 2) return "预热中";
            if (d.TdsOut is { } tds) return $"出水 TDS {tds:0}";
            if (d.WaterTempC is { } wt && d.InputWaterTempC is { } it) return $"水温 {wt:0.#}℃ · 进水 {it:0.#}℃";
            if (d.WaterTempC is { } justTemp) return $"水温 {justTemp:0.#}℃";
            if (d.InputWaterTempC is { } justInput) return $"进水 {justInput:0.#}℃";
            return "";
        }

        var totalFlow = online.Where(d => d.WaterFlowLpm.HasValue).Sum(d => d.WaterFlowLpm!.Value);
        if (totalFlow > 0.05) return $"当前 {totalFlow:0.#} L/min";

        var heater = online.FirstOrDefault(d => d.WaterTempC.HasValue);
        if (heater?.WaterTempC is { } temp) return $"水温 {temp:0.#}℃";

        var purifier = online.FirstOrDefault(d => d.TdsOut.HasValue);
        if (purifier?.TdsOut is { } purifierTds) return $"出水 TDS {purifierTds:0}";
        return "";
    }

    /// <summary>
    /// 用水卡刚启动、覆盖率还不够时右侧那句实时说明：只留一个最有信息量的词，
    /// 前面还要拼「自 HH:mm 起」，不能像覆盖率满了以后那样把水温/进水都塞进来。
    /// </summary>
    private static string WaterBriefNote(IReadOnlyList<MijiaDevice> sources, string did)
    {
        if (sources.Count == 0) return "";

        var online = sources.Where(d => d.IsOnline).ToList();
        if (online.Count == 0) return "设备离线";

        var device = did.Length > 0 && online.Count == 1 ? online[0] : null;
        if (device is null)
        {
            var heater = online.FirstOrDefault(d => d.WaterTempC.HasValue);
            if (heater?.WaterTempC is { } temp) return $"水温 {temp:0.#}℃";
            var purifier = online.FirstOrDefault(d => d.TdsOut.HasValue);
            return purifier?.TdsOut is { } outTds ? $"出水 TDS {outTds:0}" : "";
        }

        if (device.IsUsingWater) return "正在用水";
        if (device.WaterStatus == 2) return "预热中";
        if (device.WaterTempC is { } wt) return $"水温 {wt:0.#}℃";
        if (device.TdsOut is { } tds) return $"出水 TDS {tds:0}";
        return "";
    }

    // ===== 用气卡 =====

    /// <summary>用气卡当前范围：今日逐小时，或近 7 / 30 天逐日、本月逐日。</summary>
    private int GasRangeDays => ParseRangeDays(SelectedGasRange);
    private bool GasIsMonthRange => SelectedGasRange == "month";

    partial void OnSelectedGasRangeChanged(string value)
    {
        AppSettingsStore.SetGasRange(SelectedGasRange);
        if (_resettingSources) return;
        // 换到 30 天 / 本月时缓存可能还没覆盖那么早，别等十分钟的刷新窗口
        _usageFetchAt = DateTime.MinValue;
        RefreshTrendCards();
    }

    /// <summary>
    /// 用气卡：直接聚合米家云端的逐单记录（每单的用气量）。
    /// 今日按小时、7 / 30 天和本月按天，大数字是当前范围的累计用气，和用水卡同构。
    /// </summary>
    private void RefreshGas(DateTime now)
    {
        var devices = _allDevices.Where(d => d.HasGasMeter).ToList();
        HasGasMeter = devices.Count > 0;

        var did = SelectedGasSource;
        var single = did.Length == 0 ? null : devices.FirstOrDefault(d => d.TargetDid == did);
        var sources = did.Length == 0
            ? devices
            : single is { } one ? new List<MijiaDevice> { one } : new List<MijiaDevice>();

        var rangeDays = GasRangeDays;
        var isMonth = GasIsMonthRange;

        var picked = did.Length == 0 ? devices : devices.Where(d => d.TargetDid == did).ToList();
        var records = picked.SelectMany(d => UsageRecordStore.Cached(d.TargetDid)).ToList();
        var dataDays = UsageDataDays(picked, _gasDayStats, records, water: false);

        DateTime start;
        var length = 0;
        if (isMonth)
        {
            (start, length) = FitUsageMonth(dataDays, now);
        }
        else if (rangeDays > 0)
        {
            // 选中的跨度先按今天取；这段时间没记录就整体后推到最后一笔用气，再不足 3 天就放大跨度
            var (end, span) = FitDayWindow(rangeDays, now.Date,
                dataDays.Count > 0 ? dataDays.Max() : (DateTime?)null,
                (windowEnd, windowSpan) => CountUsageDays(dataDays, windowEnd, windowSpan),
                UsageSpanLadder);
            start = end.AddDays(-(span - 1));
            length = span;
            rangeDays = span;

            // 半年里只有最近几天用过气时，横轴收口到首尾有记录的那天，
            // 否则柱子全挤在右端，左边一大截空着
            var trimmed = TrimToData(start, length, i => dataDays.Contains(start.AddDays(i)));
            if (trimmed.Length != length)
            {
                start = trimmed.Start;
                length = trimmed.Length;
            }
        }
        else
        {
            start = now.Date;
        }

        var stats = rangeDays > 0 || isMonth
            ? StatsDailySeries(picked, _gasDayStats, start, length, water: false)
            : null;
        var series = stats ?? UsageSeries(records, now, start, length, water: false);
        var total = series.Sum();
        var hasUse = total > 0.0005;
        var format = isMonth ? "0.##" : "0.###";

        GasTitle = "用气";
        GasChartUnit = "m³";
        // 一天一根柱子：用量是台阶，柱高才好比；上百格塞不下柱子仍走曲线。
        // 长范围收口到几十天后也照样能用柱子，按真正画出来的格数判断
        GasIsBars = isMonth || length <= 30;
        GasIsDailyBars = isMonth || rangeDays > 0;

        ShowGasChart = HasGasMeter && hasUse;
        GasTrendPlaceholder = !HasGasMeter
            ? "未发现能回读用气量的燃气热水器"
            : hasUse
                ? ""
                : isMonth || rangeDays > 0
                    ? "这段时间还没有用气记录"
                    : "今天还没有用气记录";
        GasTrendSeries = hasUse ? series : Array.Empty<double>();
        GasTrendValue = hasUse ? total.ToString(format) : "—";
        GasTrendNow = GasLiveNote(sources, did);
        // 当前那一格（今天或这个小时）高亮；半年没有柱子就不标
        GasTrendFocus = hasUse && GasIsBars ? series.Length - 1 : -1;

        // 柱后参照线：上一等长窗口的日均，看这期比上期多还是少
        GasTrendBaseline = double.NaN;
        if (hasUse && GasIsBars && (isMonth || rangeDays > 0))
            GasTrendBaseline = PreviousWindowDailyAverage(picked, _gasDayStats, start, length, water: false);

        var yesterday = rangeDays == 0 && !isMonth && hasUse
            ? RecordDayTotal(records, now.Date.AddDays(-1), water: false)
            : 0;
        if (yesterday >= 0.01)
        {
            var percent = (total - yesterday) / yesterday * 100;
            GasTrendDelta = $"较昨日 {percent:+0;-0;0}%";
            HasGasDelta = true;
        }
        else
        {
            GasTrendDelta = "";
            HasGasDelta = false;
        }

        var range = BucketRangeText(series, "m³", format, 0.0005);
        GasTrendLow = hasUse ? range.Low : "—";
        GasTrendHigh = hasUse ? range.High : "—";
    }

    /// <summary>
    /// 用水 / 用气卡的记录：先贴本地缓存，超过十分钟再从米家云端拉一次。
    /// 记录本身就是米家「使用记录」页看的那份（usage-report 的 set-usage-record 事件）。
    /// </summary>
    private void RefreshUsageRecords(DateTime now)
    {
        var heaters = UsageHeaters();
        if (heaters.Count == 0 || _client is null || _usageBusy) return;
        if (now - _usageFetchAt < TimeSpan.FromMinutes(10)) return;

        // 水 / 气统一拉满半年：日统计一条就够画本月、近 30 天和近半年，不用随范围反复补历史
        const int statsDays = 186;
        _usageBusy = true;
        _ = FetchUsageRecordsAsync(heaters, now.Date.AddDays(-(statsDays - 1)), statsDays);
    }

    /// <summary>能报「每次用水」的热水器：净水器只有累计流量，没有这类逐单记录。</summary>
    private List<MijiaDevice> UsageHeaters() =>
        _allDevices.Where(d => d.CategoryName == "water-heater" && (d.HasWaterMeter || d.HasGasMeter)).ToList();

    private async Task FetchUsageRecordsAsync(List<MijiaDevice> heaters, DateTime from, int days)
    {
        var changed = false;
        try
        {
            var to = DateTime.Now;
            if (_client is null) return;
            foreach (var d in heaters)
            {
                var did = d.TargetDid;
                // 米家「使用记录」的日统计：prop.7.1 水、prop.7.2 气，返回值都是升
                var water = await _client.GetStatisticsAsync(did, "prop.7.1", "stat_day_v3", from, to, days);
                var gas = await _client.GetStatisticsAsync(did, "prop.7.2", "stat_day_v3", from, to, days);
                if (water.Ok && water.Items.Count > 0)
                {
                    _waterDayStats[did] = water;
                    changed = true;
                }
                if (gas.Ok && gas.Items.Count > 0)
                {
                    _gasDayStats[did] = gas;
                    changed = true;
                }
                if (!water.Ok && water.Code != -999)
                    Log($"用水统计不可用：{d.Name} code={water.Code} {water.Message}");
                if (!gas.Ok && gas.Code != -999)
                    Log($"用气统计不可用：{d.Name} code={gas.Code} {gas.Message}");

                // 今天的逐小时仍按逐单事件画，只补今天这一页，不再往前翻半年
                if (UsageRecordStore.IsFresh(did, TimeSpan.FromMinutes(10))
                    && UsageRecordStore.Covers(did, to.Date)) continue;
                await UsageRecordStore.EnsureAsync(_client, did, d.Uid, to.Date, to);
                changed = true;
            }
        }
        catch (Exception ex)
        {
            Log($"拉取使用记录失败：{ex.Message}");
        }
        finally
        {
            _usageBusy = false;
            _usageFetchAt = DateTime.Now;
        }

        // 缓存有更新就把两张卡重画一遍；这次调用会被十分钟节流挡住，不会递归打接口
        if (changed) RefreshTrendCards();
    }

    /// <summary>用气卡右侧实时说明：正在洗 / 预热 / 上一单用了多少气。</summary>
    private static string GasLiveNote(IReadOnlyList<MijiaDevice> sources, string did)
    {
        if (sources.Count == 0) return "";

        var online = sources.Where(d => d.IsOnline).ToList();
        if (online.Count == 0) return "设备离线";

        var device = did.Length > 0 && online.Count == 1 ? online[0] : null;
        if (device is null)
        {
            var heater = online.FirstOrDefault(d => d.IsUsingWater);
            if (heater is not null) return "正在用气";
            var session = online.OrderByDescending(d => d.SessionGasM3 ?? 0).FirstOrDefault();
            return session?.SessionGasM3 is { } gas && gas > 0.001 ? $"本次 {gas:0.###} m³" : "";
        }

        if (device.IsUsingWater) return "正在用气";
        if (device.WaterStatus == 2) return "预热中";
        if (device.SessionGasM3 is { } one && one > 0.001) return $"本次 {one:0.###} m³";
        return "";
    }

    // ===== 水质卡 =====

    /// <summary>
    /// 水质卡：净水器的出水 TDS 曲线。只列真有 TDS 的净水器，不做「全屋平均」这种没有意义的口径。
    /// 同时把进水 TDS 与滤芯余量写进右侧小字，免得为了看滤芯再点进设备详情。
    /// </summary>
    private void RefreshWaterQuality(DateTime now)
    {
        var devices = _allDevices.Where(d => d.TdsOut.HasValue).ToList();
        HasWaterQuality = devices.Count > 0;

        if (!HasWaterQuality)
        {
            WaterQualityValue = "—";
            WaterQualityNote = "";
            WaterQualityFoot = "";
            WaterQualityLow = "—";
            WaterQualityHigh = "—";
            WaterQualitySeries = Array.Empty<double>();
            ShowWaterQualityChart = false;
            WaterQualityPlaceholder = "";
            return;
        }

        var selected = SelectedWaterQualitySource;
        var device = devices.FirstOrDefault(d => d.TargetDid == selected) ?? devices[0];

        WaterQualityValue = device.TdsOut is { } tds ? $"{tds:0}" : "—";
        WaterQualityNote = WaterQualityNoteOf(device.TdsOut);

        if (!device.IsOnline)
        {
            WaterQualityFoot = "设备离线 · 显示上次读数";
        }
        else
        {
            var parts = new List<string>(2);
            if (device.TdsIn is { } tdsIn) parts.Add($"进水 {tdsIn:0} ppm");
            var life = device.Consumables.Where(c => c.HasValue).OrderBy(c => c.Percent).FirstOrDefault();
            if (life != null) parts.Add($"滤芯 {life.Percent}%");
            WaterQualityFoot = string.Join(" · ", parts);
        }

        var pick = (Func<EnvPoint, double?>)(p => _envHistory.TdsOf(p, device.TargetDid));
        var coverage = _envHistory.Coverage(pick, now, TrendWindow);
        var window = PickWindow(coverage);
        var series = coverage.Count >= WarmingPoints
            ? _envHistory.Resample(pick, now, window.Window, TimeSpan.FromMinutes(TrendStepMinutes))
            : Array.Empty<double>();

        ShowWaterQualityChart = series.Length > 0;
        WaterQualityPlaceholder = series.Length > 0 ? "" : "正在采样 · 攒到半小时后出曲线";
        WaterQualitySeries = series;
        WaterQualityLow = MinText(series, "");
        WaterQualityHigh = MaxText(series, "");
    }

    private static string WaterQualityNoteOf(double? tds)
    {
        if (tds is not { } t) return "";
        if (t <= 50) return "纯净";
        if (t <= 150) return "尚可";
        return "偏高";
    }

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
    /// 选中的跨度里没有读数时，窗口整体后推到最后一笔记录；仍不足 3 天就放大跨度。
    /// </summary>
    private void RefreshPowerDays(DateTime now, int days, IReadOnlyList<MijiaDevice> sources,
        MijiaDevice? single, string did)
    {
        var (end, span) = FitDayWindow(days, now.Date, LastPowerDay(sources, now),
            (windowEnd, windowSpan) => CountPowerDays(sources, now, windowEnd, windowSpan),
            PowerSpanLadder);

        var filterStart = end.AddDays(-(span - 1));
        var bars = BuildPowerDays(sources, now, end, span);

        // 数据没铺满这段窗口时（例如一年里只装了两个月），把横轴收口到首尾有读数的那天，
        // 否则柱子全挤在右边一小块，卡片看着像空的
        var trimmed = TrimToData(filterStart, span, i => !double.IsNaN(bars[i]));
        var trimmedView = trimmed.Length != span;
        if (trimmedView)
        {
            var skip = (trimmed.Start - filterStart).Days;
            bars = bars[skip..(skip + trimmed.Length)];
            end = trimmed.Start.AddDays(trimmed.Length - 1);
            span = trimmed.Length;
        }

        var any = false;
        foreach (var v in bars)
            if (!double.IsNaN(v)) { any = true; break; }

        if (span <= 30) DiagDays(sources, now, end, span);

        PowerTitle = "用电";
        PowerChartUnit = "度";
        PowerIsDailyBars = true;
        PowerIsBars = span <= 30;

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
            : counted < span
                ? $"{counted} 天有记录 · 日均 {total / counted:0.##} 度"
                : $"日均 {total / span:0.##} 度";

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
    private void DiagDays(IReadOnlyList<MijiaDevice> sources, DateTime now, DateTime end, int days)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < days; i++)
            {
                var day = end.Date.AddDays(i - (days - 1));
                var dayEnd = day >= now.Date ? now : day.AddDays(1);
                var parts = new List<string>();
                foreach (var d in sources)
                {
                    if (DayKwhOf(d, day, dayEnd) is not { } kwh || kwh <= 0.005) continue;
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
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 范围 {days} 天（至 {end:M/d}）设备 {sources.Count}{Environment.NewLine}{body}");
        }
        catch
        {
            // 排查日志失败不影响界面
        }
    }

    /// <summary>逐日用电：给一段窗口（终点 + 天数），每天所有设备叠加。</summary>
    private double[] BuildPowerDays(IReadOnlyList<MijiaDevice> sources, DateTime now, DateTime end, int days)
    {
        var bars = new double[days];
        Array.Fill(bars, double.NaN);

        for (var i = 0; i < days; i++)
        {
            var day = end.Date.AddDays(i - (days - 1));
            var dayEnd = day >= now.Date ? now : day.AddDays(1);
            var sum = 0.0;
            var hit = false;
            foreach (var d in sources)
            {
                if (DayKwhOf(d, day, dayEnd) is not { } kwh) continue;
                sum += kwh;
                hit = true;
            }
            if (hit) bars[i] = sum;
        }

        return bars;
    }

    /// <summary>窗口里有几天真的读到用电：不足 3 天就算「这段没数据」，该后推 / 放大范围。</summary>
    private int CountPowerDays(IReadOnlyList<MijiaDevice> sources, DateTime now, DateTime end, int span)
    {
        var count = 0;
        for (var i = 0; i < span; i++)
        {
            var day = end.Date.AddDays(i - (span - 1));
            var dayEnd = day >= now.Date ? now : day.AddDays(1);
            foreach (var d in sources)
            {
                if (DayKwhOf(d, day, dayEnd) is not null) { count++; break; }
            }
        }
        return count;
    }

    /// <summary>最后一次读到用电是哪天：云端日统计能追一年，本地采样只补最近一周。</summary>
    private DateTime? LastPowerDay(IReadOnlyList<MijiaDevice> sources, DateTime now)
    {
        DateTime? last = null;
        foreach (var d in sources)
        {
            if (_cloudEnergy.TryGetValue(d.TargetDid, out var cloud))
                foreach (var (date, _) in cloud.Days)
                    if (date.Date <= now.Date && (last is null || date.Date > last.Value)) last = date.Date;

            for (var i = 0; i < 7; i++)
            {
                var day = now.Date.AddDays(-i);
                var to = day >= now.Date ? now : day.AddDays(1);
                if (_envHistory.Energy(day, to, d.TargetDid).Coverage <= 0) continue;
                if (last is null || day > last.Value) last = day;
                break;
            }
        }
        return last;
    }

    /// <summary>某台设备某一天用了多少度：历史日优先云端日统计（本地只留最近 7 天，撑不到半年 / 一年）。</summary>
    private double? DayKwhOf(MijiaDevice d, DateTime day, DateTime end)
    {
        var today = DateTime.Today;
        var cloud = CloudDayKwh(d.TargetDid, day);
        if (cloud is { } past && day.Date < today) return past;

        // 本地只留最近 7 天，再早的日子扫一遍点也只会得到 0 覆盖率
        if (day.Date < today.AddDays(-(EnvHistoryStore.KeepDays - 1))) return cloud;

        var (kwh, coverage) = _envHistory.Energy(day, end, d.TargetDid);
        if (coverage > 0) return kwh;

        if (!d.PowerWatts.HasValue && cloud is { } fallback) return fallback;
        return null;
    }

    private double? CloudDayKwh(string did, DateTime day) =>
        _cloudEnergy.TryGetValue(did, out var cloud) ? cloud.DayKwh(day) : null;

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

