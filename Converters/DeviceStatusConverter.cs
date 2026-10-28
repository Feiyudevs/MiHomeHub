using System;
using System.Globalization;
using Avalonia.Data.Converters;
using MijiaPanel.Models;

namespace MijiaPanel.Converters;

/// <summary>设备状态文案：离线 / 已开启 / 已关闭。颜色由 TextBlock.DevStatus 样式类驱动。</summary>
public sealed class DeviceStatusConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MijiaDevice d) return "";
        if (!d.IsOnline) return "离线";
        return d.IsOn ? "已开启" : "已关闭";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
