// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using MiHome.Services;

namespace MiHome.Converters;

/// <summary>IconKey → Assets/Nav 下的玻璃 SVG；未出图的 key 取 house，显示与否由图标模式控制。</summary>
public sealed class NavSvgConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, IImage?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string key ? Cache.GetOrAdd(key, Load) : null;

    private static IImage? Load(string key)
    {
        try
        {
            return new SvgImage { Source = SvgSource.Load(NavIcons.SourceFor(key)) };
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>IconKey → 细线几何；两套画法都会产出，由图标模式切换可见性。</summary>
public sealed class NavLineConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => NavIcons.LineFor(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
