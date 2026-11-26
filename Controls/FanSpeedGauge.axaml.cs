// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MiHomeHub.Models;

namespace MiHomeHub.Controls;

/// <summary>
/// 空调风速档位条：左边把「自动」这类不属于阶梯的档做成胶囊，右边把一档到最高档排成一列
/// 逐级升高的柱子，点亮到当前档为止，点哪根就切哪档。
/// 空调的风速动辄七八档，铺成一排文字要折好几行，做成柱状一眼就能看出档位高低；
/// 桌面空调卡和设备详情抽屉共用这一个控件，只在宿主处传档位表、当前值和下发命令，
/// 不再各画一份、各判定一次「哪档算自动」。
/// </summary>
public partial class FanSpeedGauge : UserControl
{
    /// <summary>档位表（设备规格 value-list 原样传进来，含「自动」这种非阶梯档）。</summary>
    public static readonly StyledProperty<IReadOnlyList<AcOptionItem>?> OptionsProperty =
        AvaloniaProperty.Register<FanSpeedGauge, IReadOnlyList<AcOptionItem>?>(nameof(Options));

    /// <summary>当前档位值（对应 value-list 里的 value）。</summary>
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<FanSpeedGauge, int>(nameof(Value));

    /// <summary>点档位时执行，参数是档位的 value（int）。</summary>
    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<FanSpeedGauge, ICommand?>(nameof(SelectCommand));

    /// <summary>没点亮那几根柱的颜色。玻璃卡和抽屉卡的底不一样，所以不写死，由宿主给。</summary>
    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<FanSpeedGauge, IBrush?>(nameof(BarBrush));

    /// <summary>点亮那几根柱的颜色（默认跟分段钮选中态同一支品牌紫）。</summary>
    public static readonly StyledProperty<IBrush?> BarActiveBrushProperty =
        AvaloniaProperty.Register<FanSpeedGauge, IBrush?>(nameof(BarActiveBrush));

    /// <summary>当前档名，自动档下为空串（自动档自己已经在胶囊上标出来了，不重复）。</summary>
    public static readonly DirectProperty<FanSpeedGauge, string> CurrentLabelProperty =
        AvaloniaProperty.RegisterDirect<FanSpeedGauge, string>(
            nameof(CurrentLabel), o => o.CurrentLabel);

    /// <summary>有没有非阶梯档（决定左边那枚胶囊画不画）。</summary>
    public static readonly DirectProperty<FanSpeedGauge, bool> HasAutoProperty =
        AvaloniaProperty.RegisterDirect<FanSpeedGauge, bool>(
            nameof(HasAuto), o => o.HasAuto);

    private readonly ObservableCollection<FanGaugeChip> _autoChips = new();
    private readonly ObservableCollection<FanGaugeBar> _bars = new();
    private string _currentLabel = "";
    private bool _hasAuto;

    static FanSpeedGauge()
    {
        OptionsProperty.Changed.AddClassHandler<FanSpeedGauge>((c, _) => c.Rebuild());
        ValueProperty.Changed.AddClassHandler<FanSpeedGauge>((c, _) => c.Rebuild());
    }

    public FanSpeedGauge()
    {
        InitializeComponent();
        BarActiveBrush ??= new SolidColorBrush(Color.Parse("#A69BDF"));
        Rebuild();
    }

    public IReadOnlyList<AcOptionItem>? Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public ICommand? SelectCommand
    {
        get => GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public IBrush? BarActiveBrush
    {
        get => GetValue(BarActiveBrushProperty);
        set => SetValue(BarActiveBrushProperty, value);
    }

    public string CurrentLabel
    {
        get => _currentLabel;
        private set => SetAndRaise(CurrentLabelProperty, ref _currentLabel, value);
    }

    public bool HasAuto
    {
        get => _hasAuto;
        private set => SetAndRaise(HasAutoProperty, ref _hasAuto, value);
    }

    /// <summary>左边那排非阶梯档的胶囊数据（模板直接绑）。</summary>
    public ObservableCollection<FanGaugeChip> AutoChips => _autoChips;

    /// <summary>右边那排档位柱的数据（模板直接绑）。</summary>
    public ObservableCollection<FanGaugeBar> Bars => _bars;

    /// <summary>「自动 / Auto」这类不属于档位阶梯的标签判定（空调规格里各型号写法不一）。</summary>
    public static bool IsAutoLabel(string? label) =>
        label != null
        && (label.Contains("自动", StringComparison.Ordinal)
            || label.Contains("auto", StringComparison.OrdinalIgnoreCase));

    /// <summary>按档位表重排一次：柱高从 10 起逐级升到 26，点亮到当前档为止。</summary>
    private void Rebuild()
    {
        var opts = Options ?? Array.Empty<AcOptionItem>();
        var level = Value;

        var auto = new List<AcOptionItem>();
        var steps = new List<AcOptionItem>();
        foreach (var o in opts)
        {
            if (IsAutoLabel(o.Label)) auto.Add(o);
            else steps.Add(o);
        }

        _autoChips.Clear();
        foreach (var o in auto)
        {
            _autoChips.Add(new FanGaugeChip
            {
                Value = o.Value,
                Label = o.Label,
                IsSelected = o.Value == level
            });
        }

        // 当前档在阶梯里的位置：-1 表示现在挂的是自动档 / 值不在表里，这时一根都不点亮
        var lit = steps.FindIndex(o => o.Value == level);
        const double minH = 10;
        const double maxH = 26;

        _bars.Clear();
        for (var i = 0; i < steps.Count; i++)
        {
            var t = steps.Count == 1 ? 1 : i / (double)(steps.Count - 1);
            _bars.Add(new FanGaugeBar
            {
                Value = steps[i].Value,
                Label = steps[i].Label,
                BarHeight = Math.Round(minH + (maxH - minH) * t),
                IsLit = lit >= 0 && i <= lit
            });
        }

        HasAuto = _autoChips.Count > 0;

        var cur = opts.FirstOrDefault(o => o.Value == level);
        CurrentLabel = cur == null || IsAutoLabel(cur.Label) ? "" : cur.Label;
    }
}

/// <summary>非阶梯档（自动）的一枚胶囊。</summary>
public sealed class FanGaugeChip
{
    public required int Value { get; init; }

    public required string Label { get; init; }

    public bool IsSelected { get; init; }
}

/// <summary>阶梯里的一根柱：高度随档位逐级升高，点亮表示当前档位及以下各档。</summary>
public sealed class FanGaugeBar
{
    public required int Value { get; init; }

    public required string Label { get; init; }

    /// <summary>柱高（DIP），从 10 起一档比一档高。</summary>
    public double BarHeight { get; init; }

    /// <summary>是否已点亮（当前档位及更低各档）。</summary>
    public bool IsLit { get; init; }
}
