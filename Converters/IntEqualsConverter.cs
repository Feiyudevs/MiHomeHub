using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MijiaPanel.Converters;

/// <summary>
/// int 是否等于参数（值、参数都转成 int 再比）。用于模式/风速按钮的选中态高亮：
/// <c>Classes.is-on="{Binding DrawerDevice.AcMode, Converter={StaticResource IntEq}, ConverterParameter={x:Static m:AcModeValues.Cool}}"</c>
/// </summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public static readonly IntEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return System.Convert.ToInt32(value, CultureInfo.InvariantCulture)
                   == System.Convert.ToInt32(parameter, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
