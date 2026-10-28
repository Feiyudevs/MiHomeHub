using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MijiaPanel.Models;

/// <summary>小米部分字段（房间 id、时间戳等）在不同接口里 string / Number 混用，统一按文本取。</summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString();
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

public partial class MijiaDevice : ObservableObject
{
    [JsonPropertyName("did")] public string Did { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("home_id")] public string HomeId { get; set; } = "";
    [JsonPropertyName("uid")] public long Uid { get; set; }
    [JsonPropertyName("isOnline")] public bool IsOnline { get; set; }
    [JsonPropertyName("spec_type")] public string SpecType { get; set; } = "";

    /// <summary>
    /// 由房间归属填充，接口本身不带；带 JsonPropertyName 是为了 devices.json 本地缓存能还原房间。
    /// </summary>
    [JsonPropertyName("roomName")] public string RoomName { get; set; } = "未分配房间";

    /// <summary>控制请求进行中，用于禁用开关避免连点。</summary>
    [property: JsonIgnore] [ObservableProperty] private bool _isBusy;

    /// <summary>开关态来自独立接口；缓存写入 isOn，重启后先显示上次状态再联网校正。</summary>
    [property: JsonPropertyName("isOn")]
    [ObservableProperty] private bool _isOn;

    /// <summary>产品真机图，按型号异步取到后回填；为空时卡片用矢量图标。</summary>
    [property: JsonIgnore] [ObservableProperty] private Bitmap? _icon;

    // ===== 设备可控属性的当前值（按类型加载到详情抽屉，不回显在卡片） =====

    /// <summary>灯：亮度 1–100（MIoT light 服务 piid=2）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _brightness = 100;

    /// <summary>灯：色温，单位 K（MIoT light 服务 piid=3，多数灯 2700–6500）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _colorTemp = 4000;

    /// <summary>空调：运行模式（MIoT air-conditioner 服务 piid=2，见 AcModeValues）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _acMode;

    /// <summary>空调：目标温度 ℃（实际 siid/piid 由设备规格 AcSchema 决定）。</summary>
    [property: JsonIgnore] [ObservableProperty] private double _acTargetTemp = 26;

    /// <summary>空调：风速档位（实际 siid/piid 由设备规格 AcSchema 决定）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _acFanLevel;

    /// <summary>窗帘：当前开合度 0–100（siid=2/piid=3），打开/关闭后由详情页回填。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _curtainPosition;

    /// <summary>是否灯（决定抽屉显示亮度/色温面板）。</summary>
    [JsonIgnore] public bool IsLight => Category == "light";

    /// <summary>是否空调（决定抽屉显示模式/温度/风速面板）。</summary>
    [JsonIgnore] public bool IsAc => Category == "air-conditioner";

    /// <summary>是否窗帘（决定抽屉显示打开/暂停/关闭面板）。</summary>
    [JsonIgnore] public bool IsCurtain => Control == MijiaControl.Curtain;

    /// <summary>标准 bool 开关类（抽屉显示电源开关；窗帘走单独的电机三键）。</summary>
    [JsonIgnore] public bool IsPower => Control == MijiaControl.Power;

    /// <summary>该空调的控制参数（按设备规格解析），决定抽屉里模式/温度/风速的显示与下发。</summary>
    [property: JsonIgnore] [ObservableProperty] private AcControlSchema? _acSchema;

    partial void OnAcModeChanged(int value)
    {
        if (_acSchema == null) return;
        foreach (var o in _acSchema.Modes) o.IsSelected = o.Value == value;
    }

    partial void OnAcFanLevelChanged(int value)
    {
        if (_acSchema == null) return;
        foreach (var o in _acSchema.Fans) o.IsSelected = o.Value == value;
    }

    /// <summary>
    /// 图标 key：优先 spec_type 里的设备大类；没有 spec_type 时才退回 model 前缀猜，
    /// 有大类但不认识的直接归到通用图标，免得 model 里带 "switch" 的传感器被当成可开关设备。
    /// </summary>
    [JsonIgnore]
    public string IconKey
    {
        get
        {
            switch (Category)
            {
                case "light": return "light";
                case "switch":
                case "multi-switch":
                case "switch-button":
                case "scene-switch": return "switch";
                case "outlet":
                case "smart-outlet": return "socket";
                case "curtain": return "curtain";
                case "air-conditioner": return "ac";
                case "bath-heater": return "light"; // 浴霸的 siid=2/piid=1 实测是整机 bool on
                case "": break;
                default: return "device";
            }
            var m = Model.ToLowerInvariant();
            if (m.Contains("light") || m.Contains("lamp") || m.Contains("bulb")) return "light";
            if (m.Contains("socket") || m.Contains("plug") || m.Contains("relay")) return "socket";
            if (m.Contains("switch")) return "switch";
            if (m.Contains("curtain")) return "curtain";
            if (m.Contains("airc") || m.Contains("air-conditioner")) return "ac";
            return "device";
        }
    }

    /// <summary>
    /// 控制方式。窗帘的 siid=2/piid=1 是只写的 motor-control（0 停 1 开 2 关）；
    /// 灯/插座/空调的 bool on 写死 siid=2/piid=1。
    /// 开关类（含多路墙开、无线按键）一律不给远程控制，只展示状态。
    /// </summary>
    [JsonIgnore]
    public MijiaControl Control => IconKey switch
    {
        "curtain" => MijiaControl.Curtain,
        "light" or "socket" or "ac" => MijiaControl.Power,
        _ => MijiaControl.None
    };

    /// <summary>传感器、全部开关类、音箱、网关：没有远程开关指令，只列出状态。</summary>
    [JsonIgnore]
    public bool IsControllable => Control != MijiaControl.None;

    /// <summary>抽屉标题旁是否附状态：可控设备显示开/关；不可控仅离线时提示。</summary>
    [JsonIgnore]
    public bool ShowStatusText => IsControllable || !IsOnline;

    /// <summary>spec_type 形如 urn:miot-spec-v2:device:&lt;大类&gt;:&lt;型号段&gt;:&lt;版本&gt;。</summary>
    private string Category
    {
        get
        {
            var parts = SpecType.Split(':');
            return parts.Length > 3 && parts[2] == "device" ? parts[3] : "";
        }
    }
}

public enum MijiaControl
{
    /// <summary>没有可下发的开关指令（传感器、网关、音箱等）。</summary>
    None,

    /// <summary>标准 bool 开关：siid=2, piid=1。</summary>
    Power,

    /// <summary>窗帘：写 siid=2, piid=1 的 1/2，状态读 siid=2, piid=3 的位置百分比。</summary>
    Curtain
}

public class MijiaHome
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("uid")] public long Uid { get; set; }

    [JsonPropertyName("roomlist")] public List<MijiaRoom> Rooms { get; set; } = new();
}

public class MijiaRoom
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("dids")] public List<string> Dids { get; set; } = new();
}

public class MijiaScene
{
    [JsonPropertyName("scene_id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string SceneId { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>由客户端回填，接口在场景对象里不给 home_id。</summary>
    [JsonIgnore] public string HomeId { get; set; } = "";
}
