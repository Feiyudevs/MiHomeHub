// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace MiHomeHub.Controls;

/// <summary>
/// 首页「数据看板」的卡片网格：在 <see cref="AdaptiveGridPanel"/> 的自适应列宽之上，
/// 按用户拖动后的顺序排列卡片（<see cref="Order"/>），不真正挪动 Children。
/// 这样换序只重排位置，不会把卡片从可视化树上摘下再挂回，入场动画不会被重放。
/// </summary>
public sealed class TrendCardPanel : AdaptiveGridPanel
{
    /// <summary>卡片顺序：key 数组（temp / hum / power / water / gas / tds / scale + 五枚空气小卡
    /// pm25 / pm10 / hcho / tvoc / co2）。
    /// 为空或含未知 key 时，未知的卡片排在已知卡片之后，并保持它们原有的相对顺序。</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> OrderProperty =
        AvaloniaProperty.Register<TrendCardPanel, IReadOnlyList<string>?>(nameof(Order));

    /// <summary>挂在每个卡片容器上的 key，面板据此查它在 <see cref="Order"/> 里的位置。</summary>
    public static readonly AttachedProperty<string?> CardKeyProperty =
        AvaloniaProperty.RegisterAttached<TrendCardPanel, Control, string?>("CardKey");

    static TrendCardPanel()
    {
        AffectsMeasure<TrendCardPanel>(OrderProperty);
    }

    public IReadOnlyList<string>? Order
    {
        get => GetValue(OrderProperty);
        set => SetValue(OrderProperty, value);
    }

    public static string? GetCardKey(Control element) => element.GetValue(CardKeyProperty);

    public static void SetCardKey(Control element, string? value) => element.SetValue(CardKeyProperty, value);

    protected override List<Control> VisibleChildren()
    {
        var children = base.VisibleChildren();
        var order = Order;
        if (order is null || order.Count == 0 || children.Count < 2) return children;

        // OrderBy 是稳定排序：没登记 key 的卡片仍然按 XAML 里的先后排在一起
        return children.OrderBy(child => Rank(child, order)).ToList();
    }

    private static int Rank(Control child, IReadOnlyList<string> order)
    {
        var key = GetCardKey(child);
        if (string.IsNullOrEmpty(key)) return int.MaxValue;

        for (var i = 0; i < order.Count; i++)
            if (string.Equals(order[i], key, System.StringComparison.Ordinal)) return i;

        return int.MaxValue;
    }
}
