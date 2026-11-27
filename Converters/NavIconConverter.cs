// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using MiHomeHub.Models;
using MiHomeHub.Services;

namespace MiHomeHub.Converters;

/// <summary>
/// 侧栏 / 房间标题的 Tag 可能是裸 key 字符串（首页、智能、快捷键），也可能是
/// <see cref="NavIconRef"/>（房间行，带可选自定义图标）。这里统一取出默认 key。
/// </summary>
internal static class NavIconValue
{
    public static string? Key(object? value) => value switch
    {
        NavIconRef r => r.Key,
        string s => s,
        _ => null,
    };

    public static CustomIcon? Custom(object? value) =>
        value is NavIconRef r ? r.Custom : null;
}

/// <summary>IconKey → Assets/Nav 下的玻璃 SVG；未出图的 key 取 house，显示与否由图标模式控制。</summary>
public sealed class NavSvgConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, IImage?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = NavIconValue.Key(value);
        return key is null ? null : Cache.GetOrAdd(key, Load);
    }

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
        => NavIcons.LineFor(NavIconValue.Key(value));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Tag → 自定义图标的位图 / 矢量图；没有自定义图标时返回 null（由可见性转换器收掉）。</summary>
public sealed class NavCustomImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => NavIconValue.Custom(value)?.Image;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Tag → 是否有自定义图标。</summary>
public sealed class NavCustomVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => NavIconValue.Custom(value) is not null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Tag → 默认图标的不透明度（有自定义图标时 0，否则 1）。</summary>
public sealed class NavIconOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => NavIconValue.Custom(value) is null ? 1d : 0d;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
