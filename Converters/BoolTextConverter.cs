using System;
using System.Globalization;
using Avalonia.Data.Converters;
using MiHome.Models;

namespace MiHome.Converters;

/// <summary>bool → 文本，参数格式 "trueText|falseText"。</summary>
public sealed class BoolTextConverter : IValueConverter
{
    public static readonly BoolTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|') ?? Array.Empty<string>();
        var b = value is true;
        if (parts.Length < 2) return b.ToString();
        return b ? parts[0] : parts[1];
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>设备存在但不可控（传感器/网关等）→ 显示只读提示。</summary>
public sealed class DeviceReadOnlyConverter : IValueConverter
{
    public static readonly DeviceReadOnlyConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is MijiaDevice d && !d.IsControllable;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
