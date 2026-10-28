using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MijiaPanel.Models;

namespace MijiaPanel.Converters;

/// <summary>
/// 设备图标：24 网格、圆头描边的单线路径，按 MijiaDevice.IconKey 取几何。
/// 颜色不在此处决定：由样式类 Path.DevIcon(.on) + DynamicResource 驱动，主题切换自动跟随。
/// </summary>
public sealed class DeviceIconConverter : IValueConverter
{
    // 整圆用两段半圆弧拼：起止点重合的单段弧会被几何解析器当成空操作丢掉。
    private const string Light = "M7.8,9.6 A4.2,4.2 0 0 1 16.2,9.6 A4.2,4.2 0 0 1 7.8,9.6 Z M9.9,15.2 H14.1 M10.8,17.6 H13.2";
    private const string Switch = "M4.5,6.5 H19.5 V17.5 H4.5 Z M12,6.5 V17.5";
    private const string Socket = "M6.5,12 A5.5,5.5 0 0 1 17.5,12 A5.5,5.5 0 0 1 6.5,12 Z M10,9.6 V14.4 M14,9.6 V14.4";
    private const string Curtain = "M3.5,6 H20.5 M7,6 V17.5 M17,6 V17.5 M3.5,20 H20.5";
    private const string AirCon = "M4,5 H20 V11.5 H4 Z M7.5,15.5 H13.5 M10.5,19 H16.5";
    private const string Device = "M6,5.5 H18 V18.5 H6 Z M10.5,12 H13.5";

    public static readonly DeviceIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!typeof(Geometry).IsAssignableFrom(targetType)) return null;
        var key = value is MijiaDevice d ? d.IconKey : "device";
        return Geometry.Parse(key switch
        {
            "light" => Light,
            "switch" => Switch,
            "socket" => Socket,
            "curtain" => Curtain,
            "ac" => AirCon,
            _ => Device
        });
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
