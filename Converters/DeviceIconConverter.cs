using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MiHome.Models;

namespace MiHome.Converters;

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

    // 图标形状固定，所有卡片只读共享，避免滚动重新绑定时重复解析和分配原生路径。
    private static readonly Geometry LightGeometry = Geometry.Parse(Light);
    private static readonly Geometry SwitchGeometry = Geometry.Parse(Switch);
    private static readonly Geometry SocketGeometry = Geometry.Parse(Socket);
    private static readonly Geometry CurtainGeometry = Geometry.Parse(Curtain);
    private static readonly Geometry AirConGeometry = Geometry.Parse(AirCon);
    private static readonly Geometry DeviceGeometry = Geometry.Parse(Device);

    public static readonly DeviceIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!typeof(Geometry).IsAssignableFrom(targetType)) return null;
        return For(value switch
        {
            MijiaDevice d => d.IconKey,
            string s => s,
            _ => null
        });
    }

    /// <summary>按图标 key 取共享几何；场景/自动化卡片的行首图标也走这里，保证同一个设备同一副画。</summary>
    public static Geometry For(string? key) => key switch
    {
        "light" => LightGeometry,
        "switch" => SwitchGeometry,
        "socket" => SocketGeometry,
        "curtain" => CurtainGeometry,
        "ac" => AirConGeometry,
        _ => DeviceGeometry
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 场景编辑面板里每一张执行顺序卡的行首图标：
/// 设备动作复用设备卡同款图标（按 IconKey），其余动作按类型给语义图标
/// （延时=时钟、执行/开关场景=场景/电源、通知=铃铛、播报=喇叭、设备组=宫格）。
/// 真机图由卡片上的 Image 直接绑定 Device.Icon 覆盖，这里只负责「没有真机图」时的矢量回退。
/// </summary>
public sealed class SceneRowIconConverter : IValueConverter
{
    private static readonly Geometry SceneGeometry = Geometry.Parse(
        "M9,5.5 L10.8,11.2 L16.5,13 L10.8,14.8 L9,20.5 L7.2,14.8 L1.5,13 L7.2,11.2 Z M18,2.3 L18.9,4.6 L21.2,5.5 L18.9,6.4 L18,8.7 L17.1,6.4 L14.8,5.5 L17.1,4.6 Z");
    private static readonly Geometry ClockGeometry = Geometry.Parse(
        "M12,12 m-9,0 a9,9 0 1 0 18,0 a9,9 0 1 0 -18,0 M12,7 L12,12 L15,14");
    private static readonly Geometry PowerGeometry = Geometry.Parse(
        "M12,3 L12,11 M6.7,6.7 A7.5,7.5 0 1 0 17.3,6.7");
    private static readonly Geometry BellGeometry = Geometry.Parse(
        "M12,3 A5.5,5.5 0 0 0 6.5,8.5 V14 L4.5,17 H19.5 L17.5,14 V8.5 A5.5,5.5 0 0 0 12,3 Z M10,19.5 A2,2 0 0 0 14,19.5");
    private static readonly Geometry SpeakerGeometry = Geometry.Parse(
        "M4,9.5 V14.5 H7.5 L12,18 V6 L7.5,9.5 Z M15.2,9.2 A4,4 0 0 1 15.2,14.8");
    private static readonly Geometry GroupGeometry = Geometry.Parse(
        "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z");

    public static readonly SceneRowIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SceneEditActionRow row) return DeviceIconConverter.For(null);
        if (row.IsDelay) return ClockGeometry;
        return row.Kind switch
        {
            SceneEditRowKind.DeviceProp or SceneEditRowKind.DeviceAction
                => DeviceIconConverter.For(row.Device?.IconKey),
            SceneEditRowKind.Tts => SpeakerGeometry,
            SceneEditRowKind.SceneRun => SceneGeometry,
            SceneEditRowKind.SceneToggle => PowerGeometry,
            SceneEditRowKind.Notify => BellGeometry,
            SceneEditRowKind.DeviceGroup => GroupGeometry,
            _ => DeviceIconConverter.For(null)
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
