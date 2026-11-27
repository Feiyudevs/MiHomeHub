// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;

namespace MiHomeHub.Controls;

/// <summary>
/// 一行「可省略的文字 + 紧随其后的角标」：第一个子元素吃剩余宽度（超长省略），
/// 后面的角标按自身宽度先量好，再紧贴文字末尾排下去。
///
/// 普通 Grid 做不到这件事：文字列写 * 会把整行占满，角标被推到最右边、名字短时中间空一大截；
/// 写成 Auto 又不会触发省略，长名字会把角标顶出卡片。这里先量角标，把剩下的宽度给文字，
/// 文字实际占多少，角标就从哪里开始。
/// </summary>
public sealed class TextBadgeRow : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
            return default;

        var available = double.IsFinite(availableSize.Width) ? availableSize.Width : double.PositiveInfinity;
        double trailingWidth = 0;
        double height = 0;

        for (var i = 1; i < Children.Count; i++)
        {
            var badge = Children[i];
            if (!badge.IsVisible)
                continue;
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            trailingWidth += badge.DesiredSize.Width;
            height = Math.Max(height, badge.DesiredSize.Height);
        }

        var text = Children[0];
        if (text.IsVisible)
        {
            var textWidth = double.IsFinite(available) ? Math.Max(0, available - trailingWidth) : double.PositiveInfinity;
            text.Measure(new Size(textWidth, double.PositiveInfinity));
            height = Math.Max(height, text.DesiredSize.Height);
        }

        var width = trailingWidth + (text.IsVisible ? text.DesiredSize.Width : 0);
        return new Size(double.IsFinite(available) ? Math.Min(width, available) : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                child.Arrange(new Rect(x, 0, 0, finalSize.Height));
                continue;
            }

            var width = child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
