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

    /// <summary>由房间归属填充，不属于接口字段。</summary>
    [JsonIgnore] public string RoomName { get; set; } = "未分配房间";

    /// <summary>控制请求进行中，用于禁用开关避免连点。</summary>
    [JsonIgnore] [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isOn;

    /// <summary>产品真机图，按型号异步取到后回填；为空时卡片用矢量图标。</summary>
    [JsonIgnore] [ObservableProperty] private Bitmap? _icon;

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
                case "switch": return "switch";
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
    /// 控制方式。窗帘的 siid=2/piid=1 是只写的 motor-control（0 停 1 开 2 关），
    /// 和灯/开关/插座/空调的 bool on 不是一回事，必须分开下发。
    /// </summary>
    [JsonIgnore]
    public MijiaControl Control => IconKey switch
    {
        "curtain" => MijiaControl.Curtain,
        "light" or "switch" or "socket" or "ac" => MijiaControl.Power,
        _ => MijiaControl.None
    };

    /// <summary>传感器、音箱、网关这类没有开关指令，只列出不给开关按钮。</summary>
    [JsonIgnore]
    public bool IsControllable => Control != MijiaControl.None;

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
