// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MiHomeHub.Models;
using MiHomeHub.Services;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 一张桌面数据卡的视图模型：把首页看板的读数、曲线、区间、空态原样搬进卡窗。
///
/// 数据只读、不另算：首页 <c>MainViewModel</c> 每拍回填好的 <c>TempTrendSeries</c> / <c>AirPm25</c>
/// 等属性就是唯一数据源，桌面卡只是换一个窗壳显示，所以两边的数字永远一致。
/// 没有真实设备时和首页一样铺示例数据（接入后自动换成真实读数）；只有首页双击标题进入整段演示时，
/// 桌面卡不跟着换成演示态，仍留一句说明。
/// </summary>
public partial class DataCardVm : DesktopZoneVm
{
    private readonly DataCardDef _def;
    private readonly AirCardItem? _air;
    private bool _refreshQueued;

    public DataCardVm(MainViewModel main, DesktopGroupModel model)
        : base(main, model)
    {
        _def = DataCardCatalog.Find(model.Metric) ?? DataCardCatalog.All[0];
        // 父类构造器只能用公共上下限，这里拿到指标定义后按大卡/小卡档位重新夹一次
        Width = Math.Clamp(model.Width, MinWidthDip, MaxWidthDip);
        _air = ResolveAir(main, model.Metric);
        _height = model.Height > 1 ? model.Height : _def.DefaultHeight;

        AccentBrush = new SolidColorBrush(DataCardCatalog.Accent(_def.Key));
        // 首页体重卡不铺面积渐变，桌面这张也别多一层 Fill
        ChartFill = _def.Key == DataCardCatalog.Scale
            ? Brushes.Transparent
            : DataCardCatalog.ChartFill(_def.Key);
        TipText = _def.Description;
        RefreshAppearance();

        main.PropertyChanged += OnMainPropertyChanged;
        main.DevicesReloaded += OnDevicesReloaded;
        main.TrendCardsUpdated += OnTrendCardsUpdated;
        _air?.PropertyChanged += OnAirPropertyChanged;
        Refresh();
    }

    public DataCardDef Definition => _def;

    public string MetricKey => _def.Key;

    /// <summary>标题前的线性图标：和首页 TrendCard 用同一组 24 网格几何，缩到 13 显示。</summary>
    public Geometry IconGeometry => _def.Key switch
    {
        DataCardCatalog.Temp => Geometry.Parse("M14,4 V14.6 A2.6,2.6 0 1 1 10,14.6 V4 A2,2 0 1 1 14,4"),
        DataCardCatalog.Hum => Geometry.Parse("M12,3 C12,3 17.5,9.4 17.5,13.8 A5.5,5.5 0 0 1 6.5,13.8 C6.5,9.4 12,3 12,3 Z"),
        DataCardCatalog.Power => Geometry.Parse("M13,2 L4,14 L11,14 L10,22 L19,10 L12,10 Z"),
        DataCardCatalog.Water => Geometry.Parse("M5.5,5 H18.5 L17.2,20 H6.8 Z M6.2,10.5 H17.8 M9,16.5 Q12,14.5 15,16.5"),
        DataCardCatalog.Gas => Geometry.Parse("M12,3 C12,3 7.4,8.5 7.4,12.8 A4.6,4.6 0 0 0 16.6,12.8 C16.6,8.5 12,3 12,3 Z M12,9.5 C12,9.5 10.1,11.9 10.1,14 A1.9,1.9 0 0 0 13.9,14 C13.9,11.9 12,9.5 12,9.5 Z"),
        DataCardCatalog.Tds => Geometry.Parse("M12,3 C12,3 17.5,9.4 17.5,13.8 A5.5,5.5 0 0 1 6.5,13.8 C6.5,9.4 12,3 12,3 Z M9.2,13.6 L11.2,15.6 L15,11.8"),
        DataCardCatalog.Scale => Geometry.Parse("M5,4 H19 A2,2 0 0 1 21,6 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 V6 A2,2 0 0 1 5,4 Z M8.8,9.6 A3.2,3.2 0 0 1 15.2,9.6 M12,6.4 V9.6 M12,9.6 L13.6,11.2"),
        _ => new StreamGeometry(),
    };

    /// <summary>
    /// 数据卡也是桌面组件的一种：底色、浓度、毛玻璃与描边全部跟「桌面组件外观」走，
    /// 和旁边的设备分区摆在一起时是同一块玻璃；深度改浅后主色 / 曲线 / 档位色一并换成浅底那一档。
    /// </summary>
    public override void RefreshAppearance()
    {
        base.RefreshAppearance();
        RefreshThemeBrushes();
    }

    /// <summary>
    /// 按当前外观取主色与曲线填充：底色换成纸白后，深色主题那套亮橙 / 亮蓝会在白底上糊成一片，
    /// 所以跟首页看板一样按深浅主题各取一档。档位色也要跟着换（见 <see cref="ResolveGrade"/>）。
    /// </summary>
    private void RefreshThemeBrushes()
    {
        var light = DesktopPalette.IsLight(DesktopHost.Look);
        AccentBrush = ResolveAccent(_def.Key, light);
        ChartFill = ResolveChartFill(_def.Key, light);
        if (ShowGrade) GradeBrush = ResolveGrade(_air?.IsPoor == true, _air?.IsMild == true, light);
    }

    private static IBrush? ThemedBrush(string key, bool light)
    {
        var variant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (Application.Current?.TryGetResource(key, variant, out var res) != true) return null;
        return res as IBrush;
    }

    private static IBrush ResolveAccent(string key, bool light)
    {
        // 深色底维持数据卡自己那套主色（与首页深色主题一致，不再另翻一遍资源）；
        // 只有纸白底才换成浅色主题里压深的一档，白底上仍看得清读数与曲线
        if (!light) return new SolidColorBrush(DataCardCatalog.Accent(key));

        var resKey = key switch
        {
            DataCardCatalog.Temp => "TrendTempBrush",
            DataCardCatalog.Hum => "TrendHumBrush",
            DataCardCatalog.Power => "TrendPowerBrush",
            DataCardCatalog.Water => "TrendWaterBrush",
            DataCardCatalog.Gas => "TrendGasBrush",
            DataCardCatalog.Tds => "TrendQualityBrush",
            DataCardCatalog.Scale => "TrendScaleBrush",
            "pm25" => "AirPm25Brush",
            "pm10" => "AirPm10Brush",
            "hcho" => "AirHchoBrush",
            "tvoc" => "AirTvocBrush",
            "co2" => "AirCo2Brush",
            _ => "",
        };
        return (resKey.Length > 0 ? ThemedBrush(resKey, light) : null)
               ?? new SolidColorBrush(DataCardCatalog.Accent(key));
    }

    private static IBrush ResolveChartFill(string key, bool light)
    {
        // 体重卡只画点阵，不铺面积填充（和首页一致）
        if (key == DataCardCatalog.Scale) return Brushes.Transparent;
        // 深色底沿用原来的填充；纸白底换成浅色主题的淡填充，压在近白卡面上才不糊
        if (!light) return DataCardCatalog.ChartFill(key);

        var resKey = key switch
        {
            DataCardCatalog.Temp => "TrendTempFill",
            DataCardCatalog.Hum => "TrendHumFill",
            DataCardCatalog.Power => "TrendPowerFill",
            DataCardCatalog.Water => "TrendWaterFill",
            DataCardCatalog.Gas => "TrendGasFill",
            DataCardCatalog.Tds => "TrendQualityFill",
            _ => "",
        };
        return (resKey.Length > 0 ? ThemedBrush(resKey, light) : null)
               ?? DataCardCatalog.ChartFill(key);
    }

    /// <summary>档位文字：和首页空气小卡同三档（绿 / 琥珀 / 红），深浅底各取主题里那一支。</summary>
    private static IBrush ResolveGrade(bool poor, bool mild, bool light)
    {
        var resKey = poor ? "DangerBrush" : mild ? "AmberTextBrush" : "GreenBrush";
        return ThemedBrush(resKey, light)
               ?? new SolidColorBrush(Color.Parse(poor ? "#FF8E8E" : mild ? "#FFD48A" : "#7BE3A8"));
    }

    /// <summary>
    /// 桌面卡右上角的时间和来源下拉：值和选项全部借用首页看板同一份
    /// <see cref="MainViewModel"/> 属性，点哪边都是改同一个 <c>SelectedPowerRange</c> 等字段，
    /// 首页跟着重算，桌面卡再由 <see cref="MainViewModel.TrendCardsUpdated"/> 统一刷新。
    /// </summary>
    public bool CanPickRange => _def.Key switch
    {
        DataCardCatalog.Power => Main.CanPickPowerRange,
        DataCardCatalog.Water => Main.CanPickWaterRange,
        DataCardCatalog.Gas => Main.CanPickGasRange,
        DataCardCatalog.Scale => Main.CanPickScaleRange,
        _ => false,
    };

    public IReadOnlyList<OptionItem> RangeOptions => _def.Key switch
    {
        DataCardCatalog.Power => Main.PowerRangeOptions,
        DataCardCatalog.Water => Main.WaterRangeOptions,
        DataCardCatalog.Gas => Main.GasRangeOptions,
        DataCardCatalog.Scale => Main.ScaleRangeOptions,
        _ => Array.Empty<OptionItem>(),
    };

    public string RangeValue
    {
        get => _def.Key switch
        {
            DataCardCatalog.Power => Main.SelectedPowerRange,
            DataCardCatalog.Water => Main.SelectedWaterRange,
            DataCardCatalog.Gas => Main.SelectedGasRange,
            DataCardCatalog.Scale => Main.SelectedScaleRange,
            _ => "",
        };
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            switch (_def.Key)
            {
                case DataCardCatalog.Power: Main.SelectedPowerRange = value; break;
                case DataCardCatalog.Water: Main.SelectedWaterRange = value; break;
                case DataCardCatalog.Gas: Main.SelectedGasRange = value; break;
                case DataCardCatalog.Scale: Main.SelectedScaleRange = value; break;
            }
        }
    }

    public bool CanPickSource => _def.Key switch
    {
        DataCardCatalog.Temp => Main.CanPickTempSource,
        DataCardCatalog.Hum => Main.CanPickHumSource,
        DataCardCatalog.Power => Main.CanPickPowerSource,
        DataCardCatalog.Water => Main.CanPickWaterSource,
        DataCardCatalog.Gas => Main.CanPickGasSource,
        DataCardCatalog.Tds => Main.CanPickWaterQualitySource,
        DataCardCatalog.Scale => Main.CanPickScaleMember,
        _ => false,
    };

    /// <summary>体重卡的成员下拉和首页一样显示成员备注；其余来源下拉只显示设备名。</summary>
    public bool SourceShowMeta => _def.Key == DataCardCatalog.Scale;

    public IReadOnlyList<OptionItem> SourceOptions => _def.Key switch
    {
        DataCardCatalog.Temp => Main.TempSources,
        DataCardCatalog.Hum => Main.HumSources,
        DataCardCatalog.Power => Main.PowerSources,
        DataCardCatalog.Water => Main.WaterSources,
        DataCardCatalog.Gas => Main.GasSources,
        DataCardCatalog.Tds => Main.WaterQualitySources,
        DataCardCatalog.Scale => Main.ScaleSources,
        _ => Array.Empty<OptionItem>(),
    };

    public string SourceValue
    {
        get => _def.Key switch
        {
            DataCardCatalog.Temp => Main.SelectedTempSource,
            DataCardCatalog.Hum => Main.SelectedHumSource,
            DataCardCatalog.Power => Main.SelectedPowerSource,
            DataCardCatalog.Water => Main.SelectedWaterSource,
            DataCardCatalog.Gas => Main.SelectedGasSource,
            DataCardCatalog.Tds => Main.SelectedWaterQualitySource,
            DataCardCatalog.Scale => Main.SelectedScaleMember,
            _ => "",
        };
        set
        {
            // 用电 / 用水 / 用气的「全部设备（叠加）」就是空串，不能像其它来源那样把空值当成没选。
            if (string.IsNullOrEmpty(value))
            {
                switch (_def.Key)
                {
                    case DataCardCatalog.Power: Main.SelectedPowerSource = ""; break;
                    case DataCardCatalog.Water: Main.SelectedWaterSource = ""; break;
                    case DataCardCatalog.Gas: Main.SelectedGasSource = ""; break;
                }
                return;
            }

            switch (_def.Key)
            {
                case DataCardCatalog.Temp: Main.SelectedTempSource = value; break;
                case DataCardCatalog.Hum: Main.SelectedHumSource = value; break;
                case DataCardCatalog.Power: Main.SelectedPowerSource = value; break;
                case DataCardCatalog.Water: Main.SelectedWaterSource = value; break;
                case DataCardCatalog.Gas: Main.SelectedGasSource = value; break;
                case DataCardCatalog.Tds: Main.SelectedWaterQualitySource = value; break;
                case DataCardCatalog.Scale: Main.SelectedScaleMember = value; break;
            }
        }
    }

    private IReadOnlyList<OptionItem>? _lastRangeOptions, _lastSourceOptions;
    private string? _lastRangeValue, _lastSourceValue;
    private bool? _lastCanPickRange, _lastCanPickSource;

    /// <summary>
    /// 下拉的六项只在真的变了才通知：以前每拍都全量通知一遍，OptionPicker 会跟着重建选项、
    /// 把胶囊重新排一次版；数据刷新时卡面本来不该有任何别的东西动。
    /// </summary>
    private void NotifyPickers()
    {
        Notify(ref _lastCanPickRange, CanPickRange, nameof(CanPickRange));
        Notify(ref _lastRangeOptions, RangeOptions, nameof(RangeOptions));
        Notify(ref _lastRangeValue, RangeValue, nameof(RangeValue));
        Notify(ref _lastCanPickSource, CanPickSource, nameof(CanPickSource));
        Notify(ref _lastSourceOptions, SourceOptions, nameof(SourceOptions));
        Notify(ref _lastSourceValue, SourceValue, nameof(SourceValue));
    }

    private void Notify(ref IReadOnlyList<OptionItem>? last, IReadOnlyList<OptionItem>? now, string name)
    {
        if (ReferenceEquals(last, now)) return;
        last = now;
        OnPropertyChanged(name);
    }

    private void Notify(ref string? last, string? now, string name)
    {
        if (string.Equals(last, now, StringComparison.Ordinal)) return;
        last = now;
        OnPropertyChanged(name);
    }

    private void Notify(ref bool? last, bool now, string name)
    {
        if (last == now) return;
        last = now;
        OnPropertyChanged(name);
    }
    private static AirCardItem? ResolveAir(MainViewModel main, string key) => key switch
    {
        "pm25" => main.AirPm25,
        "pm10" => main.AirPm10,
        "hcho" => main.AirHcho,
        "tvoc" => main.AirTvoc,
        "co2" => main.AirCo2,
        _ => null
    };

    /// <summary>这张卡的设计尺寸（DIP），重建窗、重置大小都用它。</summary>
    public Size DesignSize => new(_def.DefaultWidth, _def.DefaultHeight);

    /// <summary>右下角缩放下限：大卡半档 200×100，空气小卡本来就是 200×100、不再缩。</summary>
    public Size MinCells => new(4, 2);

    protected override double MinWidthDip => 200;
    protected override double MaxWidthDip => _def.Compact ? 400 : 800;

    /// <summary>大卡在当前高度下显示曲线；空气小卡永远只报读数。</summary>
    public bool IsFull => !_def.Compact && Height >= 150;

    /// <summary>空气卡只有一档，右键菜单不显示「切换大小」。</summary>
    public bool CanChangeSize => !_def.Compact;

    public string SizeActionText => IsFull ? "切换小卡 200×100" : "切换大卡 400×200";

    public override bool ShowCount => false;

    public override string EmptyHint => "";

    public override bool IsEmpty => false;

    protected override string DangerText => "移除数据卡";

    protected override string DangerConfirmText => "确认移除？";

    /// <summary>卡面主数值下的小字：舒适度 / 体脂率 / 来源房间，没有就留空。</summary>
    [ObservableProperty] private string _noteText = "";

    /// <summary>右上角「近 24 小时 · 每 5 分钟采样」一类的口径说明。</summary>
    [ObservableProperty] private string _captionText = "";

    [ObservableProperty] private string _titleText = "";

    [ObservableProperty] private string _valueText = "—";

    [ObservableProperty] private string _unitText = "";

    [ObservableProperty] private string _deltaText = "";

    [ObservableProperty] private bool _hasDelta;

    [ObservableProperty] private string _footLeft = "";

    [ObservableProperty] private string _footRight = "";

    /// <summary>没有任何真实数据时的空态文案；<see cref="HasValue"/> 为 false 时显示它。</summary>
    [ObservableProperty] private string _emptyText = "接入设备后显示";

    [ObservableProperty] private bool _hasValue = true;

    /// <summary>曲线区是否真的画图（大卡 + 有真实序列 + 不在攒数据阶段）。</summary>
    [ObservableProperty] private bool _chartReady;

    /// <summary>曲线画不了时的说明（正在采样 / 无传感器），只在 <see cref="IsFull"/> 下显示。</summary>
    [ObservableProperty] private string _chartPlaceholderText = "";

    [ObservableProperty] private IReadOnlyList<double>? _series;

    [ObservableProperty] private string _chartUnit = "";

    [ObservableProperty] private bool _isBars;

    [ObservableProperty] private bool _isDots;

    [ObservableProperty] private bool _isDailyBars;

    [ObservableProperty] private bool _showBand;

    [ObservableProperty] private bool _showMeanLine;

    [ObservableProperty] private double _baseline = double.NaN;

    [ObservableProperty] private int _highlightIndex = -1;

    [ObservableProperty] private IReadOnlyList<double>? _lows;

    [ObservableProperty] private IReadOnlyList<double>? _highs;

    [ObservableProperty] private IReadOnlyList<string>? _xLabels;

    /// <summary>体重按天称重（一格式 = 一天），用水 / 用气按小时，其余曲线 5 分钟一格。</summary>
    public double ChartTimeStep => _def.Key switch
    {
        DataCardCatalog.Scale => 1440,
        DataCardCatalog.Water or DataCardCatalog.Gas => 60,
        _ => 5,
    };

    /// <summary>主色：数值、图标、曲线、档位标签都用它；空气卡另有档位色。</summary>
    [ObservableProperty] private IBrush _accentBrush = Brushes.Transparent;

    [ObservableProperty] private IBrush _chartFill = Brushes.Transparent;

    /// <summary>空气小卡的档位文字（优 / 良 / 偏高 / 超标）。</summary>
    [ObservableProperty] private string _gradeText = "";

    [ObservableProperty] private bool _showGrade;

    [ObservableProperty] private IBrush _gradeBrush = Brushes.Transparent;

    /// <summary>空气卡的来源房间；大卡留空。</summary>
    [ObservableProperty] private string _sourceText = "";

    /// <summary>悬停提示：指标全称与当前来源。</summary>
    [ObservableProperty] private string _tipText = "";

    [ObservableProperty] private double _height;

    partial void OnHeightChanged(double value)
    {
        if (value < 1) return;
        Model.Height = value;
        OnPropertyChanged(nameof(IsFull));
        OnPropertyChanged(nameof(ShowChart));
        OnPropertyChanged(nameof(ShowChartPanel));
        OnPropertyChanged(nameof(SizeActionText));
    }

    partial void OnChartReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowChart));
        OnPropertyChanged(nameof(ShowChartPanel));
    }

    /// <summary>只有大卡且曲线可用才显示图表；XAML 直接绑这个。</summary>
    public bool ShowChart => IsFull && ChartReady;

    /// <summary>曲线区整块在不在：大卡 + 有真实读数；没读数时由空态文案接管。</summary>
    public bool ShowChartPanel => IsFull && HasValue;

    /// <summary>窄卡没有左右分栏，把区间和补充说明拼成一行，放不下就省略。</summary>
    public string FootText
        => FootLeft.Length == 0 ? FootRight
        : FootRight.Length == 0 ? FootLeft
        : $"{FootLeft} · {FootRight}";

    partial void OnFootLeftChanged(string value) => OnPropertyChanged(nameof(FootText));

    partial void OnFootRightChanged(string value) => OnPropertyChanged(nameof(FootText));

    partial void OnHasValueChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowChartPanel));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>整卡空态：数值行被一句话替代。</summary>
    public bool ShowEmptyState => !HasValue;

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not { } n) return;
        if (!IsTrendSignal(n)) return;
        QueueRefresh();
    }

    private void OnAirPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => QueueRefresh();

    private void OnTrendCardsUpdated() => QueueRefresh();

    private void OnDevicesReloaded() => QueueRefresh();

    /// <summary>
    /// 首页一拍会连着改几十个属性：一张卡每改一个就重算一次太浪费。
    /// 同一轮 UI 消息里只排一次，最后按最终值铺一遍，曲线也不会因为中间态白播一次过渡。
    /// </summary>
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    /// <summary>只认和看板有关的属性名，设备开关、日志之类的改动不该把卡片拉起来重算。</summary>
    private static bool IsTrendSignal(string name)
        => name.StartsWith("Temp", StringComparison.Ordinal)
           || name.StartsWith("Hum", StringComparison.Ordinal)
           || name.StartsWith("Power", StringComparison.Ordinal)
           || name.StartsWith("Water", StringComparison.Ordinal)
           || name.StartsWith("Gas", StringComparison.Ordinal)
           || name.StartsWith("Scale", StringComparison.Ordinal)
           || name.StartsWith("Air", StringComparison.Ordinal)
           || name.StartsWith("Show", StringComparison.Ordinal)
           || name.StartsWith("Has", StringComparison.Ordinal)
           || name is "IsTrendPreview" or "ShowEnvTrend" or "EnvTrendCaption"
               or "IsTempPreview" or "IsHumPreview" or "IsPowerPreview" or "IsWaterPreview"
               or "IsGasPreview" or "IsQualityPreview" or "IsScalePreview";

    /// <summary>按当前首页数据重铺整张卡；卡窗显示前也会先调一次，避免看到空壳。</summary>
    public void Refresh()
    {
        if (!Model.Shown) return;

        var m = Main;
        NotifyPickers();
        // 这里不再先把各项清一遍再置回：刷新只写最终值。
        // 以前 ShowGrade / HasDelta / FootLeft / Series / ChartReady 都会先清后置，
        // 每个 tick 都制造一串 true→false→true，胶囊、曲线跟着隐藏再出现 —— 一排卡同时刷新就是一起闪。
        // 各分支（含 SetUnavailable / RefreshAir）负责把自己用到的字段一次写到位。

        if (_def.Compact)
        {
            RefreshAir(m);
            return;
        }

        TitleText = _def.Title;
        ChartUnit = _def.Unit;
        CaptionText = m.EnvTrendCaption;

        if (m.IsTrendPreview)
        {
            SetUnavailable("演示中 · 不显示示例数据");
            return;
        }

        switch (_def.Key)
        {
            case DataCardCatalog.Temp:
                HasValue = true;
                ValueText = m.TempTrendValue;
                UnitText = "℃";
                ChartUnit = "℃";
                NoteText = m.TempTrendNote;
                DeltaText = m.TempTrendDelta;
                HasDelta = m.HasTempDelta;
                FootLeft = m.TempTrendRange;
                FootRight = "";
                SetChart(m.TempTrendSeries, m.ShowTempChart, m.TempTrendPlaceholder, isBars: false);
                break;

            case DataCardCatalog.Hum:
                HasValue = true;
                ValueText = m.HumTrendValue;
                UnitText = "%";
                ChartUnit = "%";
                NoteText = m.HumTrendNote;
                DeltaText = m.HumTrendDelta;
                HasDelta = m.HasHumDelta;
                FootLeft = m.HumTrendRange;
                FootRight = "";
                SetChart(m.HumTrendSeries, m.ShowHumChart, m.HumTrendPlaceholder, isBars: false);
                break;

            case DataCardCatalog.Power:
                HasValue = true;
                TitleText = m.PowerTitle;
                CaptionText = "用电趋势";
                ValueText = m.PowerTrendValue;
                UnitText = "kWh（度）";
                ChartUnit = m.PowerChartUnit;
                DeltaText = m.PowerTrendDelta;
                HasDelta = m.HasPowerDelta;
                NoteText = m.PowerTrendNow;
                FootLeft = m.PowerTrendRange;
                HighlightIndex = m.PowerTrendFocus;
                SetChart(m.PowerTrendSeries, m.ShowPowerChart, m.PowerTrendPlaceholder,
                    isBars: m.PowerIsBars, isDailyBars: m.PowerIsDailyBars);
                break;

            case DataCardCatalog.Water:
                HasValue = true;
                TitleText = m.WaterTitle;
                CaptionText = "用水趋势";
                ValueText = m.WaterTrendValue;
                UnitText = m.WaterValueUnit;
                ChartUnit = m.WaterChartUnit;
                DeltaText = m.WaterTrendDelta;
                HasDelta = m.HasWaterDelta;
                NoteText = m.WaterTrendNow;
                FootLeft = m.WaterTrendRange;
                HighlightIndex = m.WaterTrendFocus;
                SetChart(m.WaterTrendSeries, m.ShowWaterChart, m.WaterTrendPlaceholder,
                    isBars: m.WaterIsBars, isDailyBars: m.WaterIsDailyBars);
                break;

            case DataCardCatalog.Gas:
                HasValue = true;
                TitleText = m.GasTitle;
                CaptionText = "用气趋势";
                ValueText = m.GasTrendValue;
                UnitText = "m³";
                ChartUnit = m.GasChartUnit;
                DeltaText = m.GasTrendDelta;
                HasDelta = m.HasGasDelta;
                NoteText = m.GasTrendNow;
                FootLeft = m.GasTrendRange;
                Baseline = m.GasTrendBaseline;
                HighlightIndex = m.GasTrendFocus;
                SetChart(m.GasTrendSeries, m.ShowGasChart, m.GasTrendPlaceholder,
                    isBars: m.GasIsBars, isDailyBars: m.GasIsDailyBars);
                break;

            case DataCardCatalog.Tds:
                HasValue = true;
                CaptionText = "TDS · 出水";
                ValueText = m.WaterQualityValue;
                UnitText = "ppm";
                ChartUnit = "ppm";
                NoteText = m.WaterQualityFoot;
                FootLeft = m.WaterQualityRange;
                DeltaText = m.WaterQualityNote;
                HasDelta = m.WaterQualityNote.Length > 0;
                SetChart(m.WaterQualitySeries, m.ShowWaterQualityChart, m.WaterQualityPlaceholder, isBars: false);
                break;

            case DataCardCatalog.Scale:
                HasValue = true;
                CaptionText = "称重记录";
                ValueText = m.ScaleTrendValue;
                UnitText = "kg";
                ChartUnit = "kg";
                NoteText = m.ScaleTrendMeta;
                FootLeft = m.ScaleTrendRange;
                DeltaText = m.ScaleTrendDelta;
                HasDelta = m.HasScaleDelta;
                TipText = m.ScaleTrendNote;
                SetChart(m.ScaleTrendSeries, m.ShowScaleChart, m.ScaleTrendPlaceholder,
                    isBars: false, isDots: true);
                Lows = m.ScaleTrendLows;
                Highs = m.ScaleTrendHighs;
                XLabels = m.ScaleTrendLabels;
                ShowBand = true;
                ShowMeanLine = true;
                break;
        }
    }

    /// <summary>空气小卡：一项一枚，只报当前读数 + 档位 + 来源房间。</summary>
    private void RefreshAir(MainViewModel m)
    {
        TitleText = _def.Title;
        ChartUnit = _def.Unit;
        CaptionText = "空气";
        ChartReady = false;
        Series = null;
        // 小卡没有曲线和脚注：这几个字段一次写成空，不再靠刷新开头的「先清一遍」
        ChartPlaceholderText = "";
        NoteText = "";
        DeltaText = "";
        HasDelta = false;
        FootLeft = "";
        FootRight = "";

        if (_air is null || m.IsTrendPreview)
        {
            SetUnavailable("接入空气检测设备后显示");
            return;
        }

        HasValue = true;
        ValueText = _air.ValueText;
        UnitText = _air.UnitText;
        GradeText = _air.GradeText;
        ShowGrade = _air.GradeText.Length > 0;
        GradeBrush = ResolveGrade(_air.IsPoor, _air.IsMild, DesktopPalette.IsLight(DesktopHost.Look));
        SourceText = _air.SourceText;
        TipText = _air.TipText;
        CaptionText = _air.SourceText.Length > 0 ? _air.SourceText : "空气";
    }

    /// <summary>没有真实读数：清空数值、曲线和脚注，只留一句可执行的空态。</summary>
    private void SetUnavailable(string text)
    {
        HasValue = false;
        EmptyText = text;
        ValueText = "—";
        UnitText = "";
        ChartReady = false;
        Series = null;
        NoteText = "";
        DeltaText = "";
        HasDelta = false;
        FootLeft = "";
        FootRight = "";
        ShowGrade = false;
        SourceText = "";
        ChartPlaceholderText = "";
    }

    private void SetChart(IReadOnlyList<double> series, bool show, string placeholder, bool isBars = false,
        bool isDots = false, bool isDailyBars = false)
    {
        Series = series;
        IsBars = isBars;
        IsDots = isDots;
        IsDailyBars = isDailyBars;
        ChartReady = show && series.Count > 0;
        ChartPlaceholderText = show
            ? ""
            : string.IsNullOrWhiteSpace(placeholder) ? "正在积累曲线数据…" : placeholder;
    }

    /// <summary>设备列表换代（切家庭 / 刷新 / 缓存上屏）后重新读一遍看板数据。</summary>
    public override void LoadDevices() => Refresh();

    /// <summary>卡窗关掉 / 被移除时摘掉订阅，别让一张已消失的卡继续被首页每拍唤醒。</summary>
    public void Detach()
    {
        Main.PropertyChanged -= OnMainPropertyChanged;
        Main.DevicesReloaded -= OnDevicesReloaded;
        Main.TrendCardsUpdated -= OnTrendCardsUpdated;
        _air?.PropertyChanged -= OnAirPropertyChanged;
    }

    protected override void RemoveZone() => DesktopHost.RemoveGroup(this);
}
