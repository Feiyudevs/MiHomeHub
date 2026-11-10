using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiHome.Models;

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
    /// 设备组（did 形如 group.xxx）的成员 did，来自云端 extra.member_list.member_ship 的键。
    /// 普通设备为空；落进 devices.json 缓存，重启后不用重新解析原始响应。
    /// </summary>
    [JsonPropertyName("groupMembers")] public List<string> GroupMembers { get; set; } = new();

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

    /// <summary>卡片副标题里的读数（温度/湿度等），由轮询按规格回填；空串表示没有读数。</summary>
    [property: JsonIgnore] [ObservableProperty] private string _metricsText = "";

    /// <summary>本地指令下发后的抑制截止时间（TickCount64）；这段时间内轮询不覆盖本地状态。</summary>
    [property: JsonIgnore] [ObservableProperty] private long _pendingUntilMs;

    // ===== 环境读数：轮询回填，卡片副标题与环境概览共用 =====

    /// <summary>最近一次读到的温度（℃，已按 0.1 单位缩回）；null 表示这台设备没有温度读数。</summary>
    [JsonIgnore] public double? TemperatureC { get; set; }

    /// <summary>最近一次读到的相对湿度（%，已按 0.1 单位缩回）。</summary>
    [JsonIgnore] public double? Humidity { get; set; }

    // ===== 耗材（滤芯/刷头 life-level、电量 battery-level 百分比） =====

    /// <summary>规格里的耗材余量项；随规格解析重建，卡片与抽屉按需展示。</summary>
    [JsonIgnore] public List<ConsumableState> Consumables { get; } = new();

    [JsonIgnore] public bool HasConsumables => Consumables.Count > 0;
    [JsonIgnore] public bool HasConsumableWarning => Consumables.Any(c => c.IsLow);

    /// <summary>耗材值变化后刷新汇总标记（抽屉图示与环境概览的「耗材不足」）。</summary>
    public void RefreshConsumableFlags()
    {
        OnPropertyChanged(nameof(HasConsumables));
        OnPropertyChanged(nameof(HasConsumableWarning));
    }

    // ===== 小爱音箱：音量与播放 =====

    /// <summary>是否为可控制的小爱音箱（按 spec 大类判定，规格缺失时看有没有 volume/文本指令）。</summary>
    [property: JsonIgnore] [ObservableProperty] private bool _isSpeaker;

    /// <summary>音箱音量 0–100，轮询回填；拖动滑杆时由本地值先行。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _speakerVolume = 50;

    /// <summary>本地播放态：下发播放/暂停后翻转，接口没有统一回读。</summary>
    [property: JsonIgnore] [ObservableProperty] private bool _isPlaying;

    [JsonIgnore] public int SpeakerVolumeSiid { get; private set; }
    [JsonIgnore] public int SpeakerVolumePiid { get; private set; }
    [JsonIgnore] public int PlaySiid { get; private set; }
    [JsonIgnore] public int PlayAiid { get; private set; }
    [JsonIgnore] public int PauseSiid { get; private set; }
    [JsonIgnore] public int PauseAiid { get; private set; }
    [JsonIgnore] public int TogglePlaySiid { get; private set; }
    [JsonIgnore] public int TogglePlayAiid { get; private set; }
    [JsonIgnore] public int PreviousSiid { get; private set; }
    [JsonIgnore] public int PreviousAiid { get; private set; }
    [JsonIgnore] public int NextSiid { get; private set; }
    [JsonIgnore] public int NextAiid { get; private set; }
    [JsonIgnore] public int TextDirectiveSiid { get; private set; }
    [JsonIgnore] public int TextDirectiveAiid { get; private set; }

    /// <summary>播放状态回读（playing-state）：1 播放中、0 停止、2 暂停。</summary>
    [JsonIgnore] public int PlayingStateSiid { get; private set; }
    [JsonIgnore] public int PlayingStatePiid { get; private set; }

    /// <summary>播放状态原值（playing-state 枚举）：0 停止、1 播放中、2 暂停。
    /// 音箱规格里只有这一个状态枚举，没有任何曲目文本属性，所以「正在播放什么」拿不到，只能报状态。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _playingState;

    /// <summary>是否支持 execute-text-directive（小爱语音指令依赖这个动作）。</summary>
    [JsonIgnore] public bool HasTextDirective => TextDirectiveSiid > 0 && TextDirectiveAiid > 0;

    /// <summary>按设备规格解析出的控制参数；为空表示还没拿到规格，走旧版 siid=2/piid=1 兜底。</summary>
    [property: JsonIgnore] [ObservableProperty] private DeviceSpec? _spec;

    /// <summary>是否有整机 bool 电源开关（灯/插座/空调/浴霸）。多路开关走 <see cref="Channels"/>。</summary>
    [property: JsonIgnore] [ObservableProperty] private bool _hasPower;

    /// <summary>整机电源开关的 siid/piid（规格可写 on 优先，未解析时回落 2/1）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _powerSiid = 2;
    [property: JsonIgnore] [ObservableProperty] private int _powerPiid = 1;

    /// <summary>多路开关/虚拟子设备的每一路可写 on。无规格时为空的集合。</summary>
    [JsonIgnore] public ObservableCollection<SwitchChannelState> Channels { get; } = new();

    /// <summary>
    /// 下发/查询真正要打的 did：虚拟子设备 did 形如 &lt;parent&gt;.sN，
    /// 米家只认父设备 + 通道 siid，直接打子 did 会卡住后报错。
    /// </summary>
    [JsonIgnore] public string TargetDid { get; private set; } = "";
    [JsonIgnore] public int ChannelSiid { get; private set; }
    [JsonIgnore] public bool IsVirtual => ChannelSiid > 0;

    /// <summary>是否灯（决定抽屉显示亮度/色温面板）。</summary>
    [JsonIgnore] public bool IsLight => Category == "light";

    /// <summary>是否空调（决定抽屉显示模式/温度/风速面板）。</summary>
    [JsonIgnore] public bool IsAc => Category == "air-conditioner";

    /// <summary>是否窗帘（决定抽屉显示打开/暂停/关闭面板）。</summary>
    [JsonIgnore] public bool IsCurtain => Category == "curtain";

    /// <summary>抽屉里的整机电源开关卡片（开关类走多路通道卡片，不重复显示）。</summary>
    [JsonIgnore] public bool IsPower => HasPower;

    /// <summary>卡片是否显示单个开关：整机电源、窗帘、单路开关都算。</summary>
    [JsonIgnore] public bool ShowCardSwitch => IsControllable && Channels.Count <= 1;

    /// <summary>多路开关卡片只显示「N 路」角标，具体每一路进抽屉操作。</summary>
    [JsonIgnore] public bool ShowChannelBadge => Channels.Count > 1;

    /// <summary>抽屉是否显示通道列表（单路开关也要能单独操作）。</summary>
    [JsonIgnore] public bool HasChannels => Channels.Count > 0;

    /// <summary>卡片开关的聚合态（多路时任意一路开即视为开）。</summary>
    [JsonIgnore] public bool IsControllable => IsCurtain || HasPower || Channels.Count > 0;

    /// <summary>卡片副标题：房间 + 读数（有读数时）。</summary>
    [JsonIgnore]
    public string CardSubtitle =>
        string.IsNullOrEmpty(MetricsText) ? RoomName : $"{RoomName} · {MetricsText}";

    partial void OnMetricsTextChanged(string value) => OnPropertyChanged(nameof(CardSubtitle));

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
    /// 轮询/刷新拿到的新对象把字段刷到现有实例上，**保留实例本身**。
    /// 这样设备卡片的绑定、文本布局、图标与规格状态都不需要重建——
    /// 重建会让 Skia 每次重新排版文本并新分配字体对象（Avalonia 字体缓存命中问题，只增不减），
    /// 每轮刷新都整批重建的话，常驻内存会随刷新次数一直涨。
    /// </summary>
    public void UpdateFrom(MijiaDevice other)
    {
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal))
        {
            Name = other.Name;
            OnPropertyChanged(nameof(Name));
        }

        if (!string.Equals(Model, other.Model, StringComparison.Ordinal))
        {
            Model = other.Model;
            OnPropertyChanged(nameof(IconKey));
        }

        if (!string.Equals(HomeId, other.HomeId, StringComparison.Ordinal))
            HomeId = other.HomeId;

        Uid = other.Uid;

        if (IsOnline != other.IsOnline)
        {
            IsOnline = other.IsOnline;
            OnPropertyChanged(nameof(IsOnline));
        }

        if (!string.Equals(SpecType, other.SpecType, StringComparison.Ordinal))
            SpecType = other.SpecType;

        if (!string.Equals(RoomName, other.RoomName, StringComparison.Ordinal))
        {
            RoomName = other.RoomName;
            OnPropertyChanged(nameof(RoomName));
            // 卡片副标题 = 房间 + 读数，房间变了要一起通知，否则卡片文字不刷新
            OnPropertyChanged(nameof(CardSubtitle));
        }
    }

    /// <summary>
    /// 规格未到手前的兜底：灯/插座/空调沿用 siid=2/piid=1 的整机 on；
    /// 开关类不猜，等规格解析出真实通道。虚拟子设备这里只拆 did。
    /// </summary>
    public void ApplyLegacyDefaults()
    {
        ParseDid();
        if (Spec != null) return;
        var key = IconKey;
        var legacyPower = key is "light" or "socket" or "ac";
        HasPower = legacyPower;
        if (legacyPower)
        {
            PowerSiid = 2;
            PowerPiid = 1;
        }
        RaiseControlChanged();
    }

    /// <summary>用解析出的规格重建控制模型：多路开关每路一条通道，其余补整机电源开关。</summary>
    public void ApplySpec(DeviceSpec spec)
    {
        Spec = spec;
        Channels.Clear();

        var onChannels = spec.OnChannels;
        if (IsVirtual)
        {
            var match = onChannels.FirstOrDefault(c => c.Siid == ChannelSiid);
            if (match != null) Channels.Add(NewChannel(match));
        }
        else
        {
            foreach (var c in onChannels) Channels.Add(NewChannel(c));
        }

        if (Channels.Count > 0)
        {
            // 多路墙开的规格 description 常都是「开关」，补上路序才能分辨
            if (Channels.Count > 1 && Channels.Select(c => c.Label).Distinct().Count() < Channels.Count)
            {
                var n = 0;
                foreach (var c in Channels) c.Label = $"{c.Label} {++n}";
            }
            HasPower = false;
        }
        else if (spec.AnyWritableOn is { } on)
        {
            HasPower = true;
            PowerSiid = on.Siid;
            PowerPiid = on.Piid;
        }
        else
        {
            HasPower = false;
        }

        ApplySpeaker(spec);
        ApplyConsumables(spec);
        RaiseControlChanged();
    }

    /// <summary>按规格判定音箱能力：音量属性与播放/文本指令动作，决定抽屉控制栏与语音悬浮球入口。</summary>
    private void ApplySpeaker(DeviceSpec spec)
    {
        var category = Category;
        var isSpeakerCategory = category is "speaker" or "voice-assistant" ||
                                category.Contains("speaker", StringComparison.Ordinal);

        var volume = spec.SpeakerVolume;
        var text = spec.FindAction("execute-text-directive");
        var play = spec.FindAction("play");
        var pause = spec.FindAction("pause");
        var toggle = spec.FindAction("toggle-play");
        var previous = spec.FindAction("previous");
        var next = spec.FindAction("next");
        var playingState = spec.PlayingState;

        // 只认音箱大类或真的带文本指令；电视也有 volume + play/pause，不能按「音量+播放」误判成音箱
        IsSpeaker = isSpeakerCategory || text != null;

        SpeakerVolumeSiid = volume?.Siid ?? 0;
        SpeakerVolumePiid = volume?.Piid ?? 0;
        PlaySiid = play?.Siid ?? 0;
        PlayAiid = play?.Aiid ?? 0;
        PauseSiid = pause?.Siid ?? 0;
        PauseAiid = pause?.Aiid ?? 0;
        TogglePlaySiid = toggle?.Siid ?? 0;
        TogglePlayAiid = toggle?.Aiid ?? 0;
        PreviousSiid = previous?.Siid ?? 0;
        PreviousAiid = previous?.Aiid ?? 0;
        NextSiid = next?.Siid ?? 0;
        NextAiid = next?.Aiid ?? 0;
        TextDirectiveSiid = text?.Siid ?? 0;
        TextDirectiveAiid = text?.Aiid ?? 0;
        PlayingStateSiid = playingState?.Siid ?? 0;
        PlayingStatePiid = playingState?.Piid ?? 0;
    }

    private void ApplyConsumables(DeviceSpec spec)
    {
        Consumables.Clear();
        // 同名耗材是两条真实余量（门锁的干电池与摄像头锂电、扫地机的主副刷），全部列出，重名按出现顺序加序号
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in spec.ConsumableProps)
        {
            var desc = string.IsNullOrWhiteSpace(p.Desc) ? p.Name : p.Desc;
            var seq = (labels.TryGetValue(desc, out var n) ? n : 0) + 1;
            labels[desc] = seq;
            Consumables.Add(new ConsumableState
            {
                Siid = p.Siid,
                Piid = p.Piid,
                Desc = seq > 1 ? $"{desc} {seq}" : desc
            });
        }
        RefreshConsumableFlags();
    }

    private SwitchChannelState NewChannel(SpecPropertyDef def) => new()
    {
        Siid = def.Siid,
        Piid = def.Piid,
        Label = string.IsNullOrEmpty(def.Desc) ? $"路 {def.Siid}" : def.Desc,
        Device = this
    };

    private void RaiseControlChanged()
    {
        OnPropertyChanged(nameof(IsPower));
        OnPropertyChanged(nameof(IsControllable));
        OnPropertyChanged(nameof(ShowCardSwitch));
        OnPropertyChanged(nameof(ShowChannelBadge));
        OnPropertyChanged(nameof(HasChannels));
    }

    /// <summary>多路开关的聚合态：任意一路开即显示为开；单路时与通道一致。</summary>
    public void SyncOnFromChannels()
    {
        if (Channels.Count == 0) return;
        IsOn = Channels.Any(c => c.IsOn);
    }

    /// <summary>把 &lt;parent&gt;.sN 拆成父 did + 通道 siid；普通 did 原样保留。</summary>
    private void ParseDid()
    {
        TargetDid = Did;
        ChannelSiid = 0;
        var dot = Did.LastIndexOf(".s", StringComparison.Ordinal);
        if (dot <= 0) return;
        if (!int.TryParse(Did[(dot + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch) || ch <= 0)
            return;
        TargetDid = Did[..dot];
        ChannelSiid = ch;
    }

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

/// <summary>一个耗材余量项（滤芯/刷头/电量）：Percent 为 0–100，未知时 -1。</summary>
public sealed partial class ConsumableState : ObservableObject
{
    public int Siid { get; init; }
    public int Piid { get; init; }
    public required string Desc { get; init; }

    [ObservableProperty] private int _percent = -1;

    public bool HasValue => Percent >= 0;
    public bool IsLow => HasValue && Percent <= 20;
    public string ValueText => HasValue ? $"{Percent}%" : "—";

    partial void OnPercentChanged(int value)
    {
        OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(IsLow));
        OnPropertyChanged(nameof(ValueText));
    }
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

// 场景 / 自动化模型见 Models/MijiaScene.cs（探针确认的完整结构）。
