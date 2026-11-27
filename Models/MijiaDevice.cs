// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MiHomeHub.Services;

namespace MiHomeHub.Models;

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
    /// <summary>型号</summary>
    [JsonPropertyName("model")] public string Model { get; set; } = "";

    /// <summary>
    /// 米家设备图标目录里的选中项 id（数字）。非 0 时对应 /v2/device/switch_subclass
    /// 目录里的一张图标，米家会把它当成这台设备的封面。
    /// </summary>
    [JsonPropertyName("subclass_id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? SubclassId { get; set; }
    [JsonPropertyName("home_id")] public string HomeId { get; set; } = "";
    [JsonPropertyName("uid")] public long Uid { get; set; }
    [JsonPropertyName("isOnline")] public bool IsOnline { get; set; }
    [JsonPropertyName("spec_type")] public string SpecType { get; set; } = "";

    /// <summary>
    /// 局域网 IP：设备列表原始响应里带 localip（Wi-Fi / 网线接入的设备才有）。
    /// 中枢网关的「自动化极客版」网页就在这个 IP 的 80 端口，界面直接用它拼地址。
    /// </summary>
    [JsonPropertyName("localip")] public string LocalIp { get; set; } = "";

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

    /// <summary>
    /// 设备自己的封面图地址，来自设备列表原始字段（自定义封面上传的 devPicInfo.logo，或云端按设备下发的 icon）。
    /// 米家展示顺序是「设备封面 > 型号产品图」，产品配置里没有该型号时（如 togo.light.t1）就靠这个拿图。
    /// </summary>
    [JsonPropertyName("customIconUrl")] public string? CustomIconUrl { get; set; }

    /// <summary>产品真机图，按型号异步取到后回填；为空时卡片用矢量图标。</summary>
    [property: JsonIgnore] [ObservableProperty] private Bitmap? _icon;

    /// <summary>用户给这台设备换的自定义图标；按 did 存，和房间 / 场景共用同一套存储。</summary>
    [property: JsonIgnore] [ObservableProperty] private CustomIcon? _customIcon;

    [JsonIgnore] public bool HasCustomIcon => CustomIcon is not null;

    partial void OnCustomIconChanged(CustomIcon? value) =>
        OnPropertyChanged(nameof(HasCustomIcon));

    /// <summary>从自定义图标存储重新读一次；设备刷新、实例复用或图标被清除后都走这里。</summary>
    public void ReloadCustomIcon() =>
        CustomIcon = CustomIconStore.Get(CustomIconStore.DeviceScope, Did);

    // ===== 设备可控属性的当前值（按类型加载到详情抽屉，不回显在卡片） =====

    /// <summary>灯：亮度 1–100（MIoT light 服务 piid=2）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _brightness = 100;

    /// <summary>灯：色温，单位 K（MIoT light 服务 piid=3，多数灯 2700–6500）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _colorTemp = 4000;

    /// <summary>空调：运行模式（MIoT air-conditioner 服务 piid=2，见 AcModeValues）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _acMode;

    /// <summary>空调：目标温度 ℃（实际 siid/piid 由设备规格 AcSchema 决定）。</summary>
    [property: JsonIgnore] [ObservableProperty] private double _acTargetTemp = 26;

    partial void OnAcTargetTempChanged(double value) =>
        OnPropertyChanged(nameof(AcTargetTempInput));

    /// <summary>
    /// 目标温度步进器写入点（胶囊步进器的 Value 绑这里）：
    /// 设备自己不知道网络那一层，写进来的绝对值交给视图模型去校准并下发。
    /// </summary>
    [JsonIgnore] public Action<double>? AcTargetTempSink { get; set; }

    /// <summary>步进器读到的当前目标温度；写回时转给 <see cref="AcTargetTempSink"/>。</summary>
    [JsonIgnore]
    public double AcTargetTempInput
    {
        get => AcTargetTemp;
        set => AcTargetTempSink?.Invoke(value);
    }

    /// <summary>把界面上的温度拨回设备真实值（离线、缺规格或下发被拒时用，避免显示停在没落地的数）。</summary>
    public void RefreshAcTargetTempInput() => OnPropertyChanged(nameof(AcTargetTempInput));

    /// <summary>空调：风速档位（实际 siid/piid 由设备规格 AcSchema 决定）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _acFanLevel;

    /// <summary>窗帘：当前开合度 0–100（siid=2/piid=3），打开/关闭后由详情页回填。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _curtainPosition;

    /// <summary>卡片副标题里的读数（温度/湿度/功率等），由轮询按外显配置回填；空串表示没有读数。</summary>
    [property: JsonIgnore] [ObservableProperty] private string _metricsText = "";

    /// <summary>
    /// 卡片上逐枚展示的读数标签（已按详情页读数卡的左右顺序排好，一项一枚）：
    /// 卡片按标签排版，换行了也只是整枚换到下一行，不会把「20µg/m³」从中间截断。
    /// </summary>
    [JsonIgnore] public ObservableCollection<string> CardMetricTags { get; } = new();

    /// <summary>卡片上是否还留房间名：读数标签到三枚以上时让位，把整行宽度留给读数，
    /// 免得读数标签叠出卡片；房间在分组标题上已经写着。由读数同步时一起刷新。</summary>
    [property: JsonIgnore] [ObservableProperty] private bool _showCardRoom = true;

    /// <summary>外显读数缓存：属性短名 → 最近一次格式化后的文本。
    /// 温湿度这类慢速读数不是每拍都读，靠这份缓存保证副标题不会在两拍之间闪没。</summary>
    [JsonIgnore] public Dictionary<string, string> CardMetricTexts { get; } = new(StringComparer.Ordinal);

    /// <summary>用户在列表里单独隐藏了这台设备：只在主列表里折叠到末尾，控制/场景/统计照旧。
    /// 状态来自 app.json 的 hiddenDids，不进 devices.json 缓存。</summary>
    [property: JsonIgnore] [ObservableProperty] private bool _isHidden;

    /// <summary>本地指令下发后的抑制截止时间（TickCount64）；这段时间内轮询不覆盖本地状态。</summary>
    [property: JsonIgnore] [ObservableProperty] private long _pendingUntilMs;

    // ===== 环境读数：轮询回填，卡片副标题与环境概览共用 =====

    /// <summary>最近一次读到的温度（℃，已按 0.1 单位缩回）；null 表示这台设备没有温度读数。</summary>
    [JsonIgnore] public double? TemperatureC { get; set; }

    /// <summary>最近一次读到的相对湿度（%，已按 0.1 单位缩回）。</summary>
    [JsonIgnore] public double? Humidity { get; set; }

    /// <summary>最近一次读到的瞬时功率（W）。只认 electric-power（功率），
    /// 不认 power-consumption（累计电量）。null 表示这台设备没有计量能力/没读到。</summary>
    [JsonIgnore] public double? PowerWatts { get; set; }

    /// <summary>最近一次读到的累计耗电量（kWh，power-consumption 计数器）。空调、浴霸这类
    /// 只报累计电量、没有瞬时功率；首页用电卡用相邻两次读数的差值折出这一段用了多少度。</summary>
    [JsonIgnore] public double? EnergyKwh { get; set; }

    /// <summary>
    /// 最近一次读到的空气读数：标准键（pm2.5 / pm10 / hcho / tvoc / co2）→ 已按展示单位归一的数值。
    /// 跟卡片外显配置无关，也不参与卡片副标题——首页那排空气小卡统一从这里取，
    /// 醛类和 TVOC 已在回填时折成 mg/m³（规格按 µg/m³ 报的机型不会差三个数量级）。
    /// </summary>
    [JsonIgnore] public Dictionary<string, double> AirReadings { get; } = new(StringComparer.Ordinal);

    // ===== 用水读数：热水器/净水器的水表、流量、水温与水质 =====

    /// <summary>水表读数（升）：热水器把每次完成的「单次用水」累加成一个只增不减的水表，
    /// 净水器直接用累计制水量；首页用水卡用相邻两次采样的差值算这一段用了多少水。</summary>
    [JsonIgnore] public double? WaterLiters { get; set; }

    /// <summary>最近一次读到的水流量（L/min，燃气热水器的 discharge-water 按 0.1 单位折回）。</summary>
    [JsonIgnore] public double? WaterFlowLpm { get; set; }

    /// <summary>热水器当前水温（℃）。这类设备的 temperature 是水温，不是室温。</summary>
    [JsonIgnore] public double? WaterTempC { get; set; }

    /// <summary>热水器进水温度（℃）。</summary>
    [JsonIgnore] public double? InputWaterTempC { get; set; }

    /// <summary>净水器出水 TDS（ppm）。</summary>
    [JsonIgnore] public double? TdsOut { get; set; }

    /// <summary>净水器进水 TDS（ppm）。</summary>
    [JsonIgnore] public double? TdsIn { get; set; }

    /// <summary>热水器工作状态：0 待机 / 1 用水中 / 2 预热中（按规格枚举回填）。</summary>
    [JsonIgnore] public int WaterStatus { get; set; }

    /// <summary>本次用水升数（燃气热水器 water-level，用完会清零），只用于实时显示，不能直接当累计表。</summary>
    [JsonIgnore] public double? SessionWaterLiters { get; set; }

    /// <summary>水表是否已经起好基线：第一次读到「单次用水」时只记基线，不把历史记录算进今天。</summary>
    [JsonIgnore] public bool WaterMeterInit { get; set; }

    /// <summary>上一次的「单次用水」读数（升）：变化一次就代表刚完成一次用水，累加进 <see cref="WaterLiters"/>。</summary>
    [JsonIgnore] public double? LastSingleWaterLiters { get; set; }

    /// <summary>这台机器确实能读「单次用水」：读到过就不再用 water-level 增量兜底，免得两套计数重复。</summary>
    [JsonIgnore] public bool UsesSingleWater { get; set; }

    /// <summary>没有「单次用水」记录的机型退化为累计 water-level 增量时，上一次的读数。</summary>
    [JsonIgnore] public double? LastSessionWaterLevelLiters { get; set; }

    /// <summary>正在放热水（工作状态 = 用水中）。</summary>
    [JsonIgnore] public bool IsUsingWater => WaterStatus == 1;

    /// <summary>有没有水表读数：首页用水卡只挑这些设备，没有水表的普通设备不占位置。</summary>
    [JsonIgnore] public bool HasWaterMeter => WaterLiters.HasValue;

    // ===== 用气读数：燃气热水器的「单次用气」累加成只增气表 =====

    /// <summary>累计用气量（m³）：把热水器每次完成的「单次用气」累加成一个只增不减的气表。</summary>
    [JsonIgnore] public double? GasM3 { get; set; }

    /// <summary>最近一次完成的单次用气（m³），只用于卡片上「本次」提示，不参与累计。</summary>
    [JsonIgnore] public double? SessionGasM3 { get; set; }

    /// <summary>气表是否已经起好基线：第一次读到「单次用气」时只记基线，不把历史记录算进今天。</summary>
    [JsonIgnore] public bool GasMeterInit { get; set; }

    /// <summary>上一次的「单次用气」读数（m³）：变化一次就代表刚完成一次用气，累加进 <see cref="GasM3"/>。</summary>
    [JsonIgnore] public double? LastSingleGasM3 { get; set; }

    /// <summary>有没有气表读数：首页用气卡只挑这些设备。</summary>
    [JsonIgnore] public bool HasGasMeter => GasM3.HasValue;

    // ===== 耗材（滤芯/刷头 life-level、电量 battery-level/charge-progress 百分比） =====

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

    /// <summary>
    /// 是否用水设备（燃气/电热水器、净水器）：它们的 temperature 是水温，不是室温，
    /// 不能混进环境概览和温度趋势，首页用水卡按它们的水表单独统计。
    /// </summary>
    [JsonIgnore] public bool IsWaterDevice => Category is "water-heater" or "water-purifier";

    /// <summary>是否窗帘（决定抽屉显示打开/暂停/关闭面板）。</summary>
    [JsonIgnore] public bool IsCurtain => Category == "curtain";

    /// <summary>
    /// 是否风扇类（落地扇 / 循环扇 / 塔扇 / 吊扇）：抽屉里按规格显示档位、摆风、模式。
    /// 大类写 fan 或带 fan 的都算；个别型号 spec_type 缺失时按 model 兜底（dmaker.fan / zhimi.fan）。
    /// </summary>
    [JsonIgnore]
    public bool IsFan
    {
        get
        {
            if (Category.Contains("fan", StringComparison.Ordinal)) return true;
            var m = Model.ToLowerInvariant();
            return Category.Length == 0 && (m.Contains(".fan.") || m.StartsWith("dmaker.fan", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 是否摄像头 / 可视门铃：抽屉里给「监控画面」入口。
    /// 米家云端接口不提供画面（画面要本地 P2P 拉流），入口接的是本地 go2rtc 桥接，见 CameraBridge。
    /// </summary>
    [JsonIgnore]
    public bool IsCamera
    {
        get
        {
            if (Category.Contains("camera", StringComparison.Ordinal)) return true;
            var m = Model.ToLowerInvariant();
            return m.Contains(".camera.") || m.Contains(".ipc") || m.Contains(".doorbell");
        }
    }

    /// <summary>
    /// 是否中枢网关（xiaomi.gateway.hub1 等）：只有它在局域网里自带「自动化极客版」网页，
    /// 抽屉里给它一个可点击的入口。其他网关（多模网关、音箱网关）没有这个页面。
    /// </summary>
    [JsonIgnore]
    public bool IsHub => Model.ToLowerInvariant().Contains("gateway.hub");

    /// <summary>抽屉里的整机电源开关卡片（开关类走多路通道卡片，不重复显示）。</summary>
    [JsonIgnore] public bool IsPower => HasPower;

    /// <summary>
    /// 类目上没有整机开关的设备：网关 / 中枢、路由器、门锁、音箱、各类传感器。
    /// 它们的规格里也常带可写 on（指示灯、夜灯、报警开关、播放开关），但那不是「设备开关」，
    /// 卡片上不该出现电源键，也不该点亮开启态的右缘渐变。
    /// 判定词按大类拆成几段匹配：spec_type 的大类段形如 device:router / device:lock / device:speaker。
    /// </summary>
    [JsonIgnore]
    public bool HasNoCardPower
    {
        get
        {
            var cat = Category.ToLowerInvariant();
            var model = Model.ToLowerInvariant();
            return cat.Contains("gateway", StringComparison.Ordinal)
                   || cat.Contains("sensor", StringComparison.Ordinal)
                   || cat.Contains("router", StringComparison.Ordinal)
                   || cat.Contains("lock", StringComparison.Ordinal)
                   || cat.Contains("speaker", StringComparison.Ordinal)
                   || cat.Contains("voice-assistant", StringComparison.Ordinal)
                   || cat is "hub" or "central-hub"
                   || model.Contains("gateway", StringComparison.Ordinal)
                   || model.Contains(".router.", StringComparison.Ordinal)
                   || model.Contains(".lock.", StringComparison.Ordinal)
                   || model.Contains(".wifispeaker.", StringComparison.Ordinal);
        }
    }

    /// <summary>卡片是否显示单个开关：整机电源、窗帘、单路通断模块都算；墙壁开关 / 场景按键、网关 / 路由 / 门锁 / 音箱 / 传感器不算。</summary>
    [JsonIgnore] public bool ShowCardSwitch => IsControllable && Channels.Count <= 1 && !HasNoCardPower && !IsKeyPanel;

    /// <summary>多路开关卡片只显示「N 路」角标，具体每一路进抽屉操作。</summary>
    [JsonIgnore] public bool ShowChannelBadge => Channels.Count > 1;

    /// <summary>抽屉是否显示通道列表（单路开关也要能单独操作）。</summary>
    [JsonIgnore] public bool HasChannels => Channels.Count > 0;

    /// <summary>卡片开关的聚合态（多路时任意一路开即视为开）。</summary>
    [JsonIgnore] public bool IsControllable => IsCurtain || HasPower || Channels.Count > 0;

    /// <summary>设备组（米家把多盏灯等打包成一个虚拟设备，did 形如 group.xxx）。</summary>
    [JsonIgnore] public bool IsGroup => Did.StartsWith("group.", StringComparison.OrdinalIgnoreCase);

    /// <summary>组再进设备列表时卡片上的标签：灯组要能一眼认出来，其他组归为设备组。</summary>
    [JsonIgnore] public string GroupTag => IsGroup ? (IsLight ? "灯组" : "设备组") : "";

    /// <summary>多键开关被米家拆出来的单键虚拟设备（did 形如 &lt;parent&gt;.sN）。</summary>
    [JsonIgnore] public bool IsSplitChild => ChannelSiid > 0;

    /// <summary>墙壁开关 / 场景按键类设备（多键以虚拟子设备出现）；设置里可以整类隐藏。</summary>
    [JsonIgnore] public bool IsSwitchDevice => !IsGroup && IconKey == "switch";

    /// <summary>
    /// 物理按键面板：墙上的开关、场景键、智能中控屏。它们的规格里带 switch-sensor（按键传感）服务，
    /// 卡面只当入口，不给电源键；单路通断器、电脑开机卡、水阀这类没有按键服务的设备照常给开关。
    /// 规格还没到手时先按面板处理，避免墙上开关的卡片先闪一下电源键。
    /// </summary>
    [JsonIgnore]
    public bool IsKeyPanel => IsSwitchDevice && (Spec == null || Spec.HasService("switch-sensor"));

    /// <summary>卡片副标题：房间 + 读数（有读数时），段间用「·」紧连，一行放得下就不折行。</summary>
    [JsonIgnore]
    public string CardSubtitle =>
        string.IsNullOrEmpty(MetricsText) ? RoomName : $"{RoomName}·{MetricsText}";

    /// <summary>悬停提示用的完整读数：不省略，所有已选读数都展示出来。</summary>
    [JsonIgnore]
    public string CardSubtitleTooltip =>
        CardMetricTexts.Count == 0 ? CardSubtitle : $"{RoomName}·{string.Join("·", CardMetricTexts.Values)}";

    partial void OnMetricsTextChanged(string value)
    {
        OnPropertyChanged(nameof(CardSubtitle));
        OnPropertyChanged(nameof(CardSubtitleTooltip));
    }

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

    partial void OnAcSchemaChanged(AcControlSchema? value)
    {
        if (value == null) return;
        foreach (var o in value.Modes) o.IsSelected = o.Value == AcMode;
        foreach (var o in value.Fans) o.IsSelected = o.Value == AcFanLevel;
    }

    // ===== 风扇：档位 / 摆风 / 模式（siid/piid 由设备规格解析） =====

    /// <summary>该风扇的控制参数（按设备规格解析），决定抽屉里档位/摆风/模式的显示与下发。</summary>
    [property: JsonIgnore] [ObservableProperty] private FanControlSchema? _fanSchema;

    /// <summary>风扇档位当前值（枚举档或连续量程都落在这里）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _fanLevel;

    /// <summary>无级调速当前值（speed-level，1–100；0 表示该型号没有这条属性）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _fanSpeed;

    /// <summary>风扇模式当前值（0 表示该型号没有模式属性）。</summary>
    [property: JsonIgnore] [ObservableProperty] private int _fanMode;

    /// <summary>左右摆风是否开启。</summary>
    [property: JsonIgnore] [ObservableProperty] private bool _swingHOn;

    /// <summary>上下摆风是否开启。</summary>
    [property: JsonIgnore] [ObservableProperty] private bool _swingVOn;

    partial void OnFanLevelChanged(int value)
    {
        if (_fanSchema == null) return;
        foreach (var o in _fanSchema.Levels) o.IsSelected = o.Value == value;
    }

    partial void OnFanModeChanged(int value)
    {
        if (_fanSchema == null) return;
        foreach (var o in _fanSchema.Modes) o.IsSelected = o.Value == value;
    }

    // ===== 灯：亮度 / 色温（siid-piid 与值域由设备规格解析） =====

    /// <summary>该灯的控制参数（按设备规格解析）：决定滑杆量程与实际下发的 siid/piid。</summary>
    [property: JsonIgnore] [ObservableProperty] private LightControlSchema? _lightSchema;

    /// <summary>最近一次读到的预设 mode（读不到为 null）；拖色温前据此决定要不要拨回可调档。</summary>
    [JsonIgnore] public int? LightMode { get; set; }

    /// <summary>色温兜底上下限。自定义量程允许超出设备规格——不少型号实际能力比规格标注更宽，
    /// 硬卡在规格上反而调不到真机的端点；超出真机能力的值云端会拒收，界面回读后自己退回。</summary>
    public const int ColorTempFloor = 1000;
    public const int ColorTempCeiling = 10000;

    /// <summary>用户给这盏灯自定义的色温区间（null = 跟设备规格）；存 app.json，按 did 记。</summary>
    private int? _ctMinOverride;
    private int? _ctMaxOverride;

    /// <summary>设备规格里这盏灯自己的色温量程（不含用户自定义）。</summary>
    [JsonIgnore] public double SpecColorTempMin => LightSchema is { HasColorTemp: true } s ? s.CtMin : 2700;

    /// <summary>设备规格里这盏灯自己的色温上限（不含用户自定义）。</summary>
    [JsonIgnore] public double SpecColorTempMax => LightSchema is { HasColorTemp: true } s ? s.CtMax : 6500;

    /// <summary>色温滑杆左端：用户自定义过就用自己的，否则跟设备规格。规格没解析出来时按多数灯的 2700K。</summary>
    [JsonIgnore] public double ColorTempMin => _ctMinOverride ?? SpecColorTempMin;

    /// <summary>色温滑杆右端：用户自定义过就用自己的，否则跟设备规格。</summary>
    [JsonIgnore] public double ColorTempMax => _ctMaxOverride ?? SpecColorTempMax;

    /// <summary>色温步进（多数型号 1K）。</summary>
    [JsonIgnore] public double ColorTempStep => LightSchema is { HasColorTemp: true } s ? s.CtStep : 1;

    /// <summary>亮度滑杆左端。</summary>
    [JsonIgnore] public double BrightnessMin => LightSchema is { HasBrightness: true } s ? s.BrightMin : 1;

    /// <summary>亮度滑杆右端。</summary>
    [JsonIgnore] public double BrightnessMax => LightSchema is { HasBrightness: true } s ? s.BrightMax : 100;

    /// <summary>
    /// 这台灯能不能调色温。规格解析过、但 light 服务里没有 color-temperature（只有彩光的灯）时为 false，
    /// 抽屉里就不出这张卡，免得滑杆拖了没反应。规格没拿到时按老样子显示。
    /// </summary>
    [JsonIgnore] public bool HasColorTemp => LightSchema?.HasColorTemp ?? true;

    /// <summary>抽屉里那行量程说明，如「范围 3000K – 6500K」。</summary>
    [JsonIgnore] public string ColorTempRangeText => $"范围 {ColorTempMin:0}K – {ColorTempMax:0}K";

    /// <summary>这盏灯硬件支持的上下限（设备规格标注），编辑时只作参考：可以照着收窄，也可以放宽去试真机的实际能力。</summary>
    [JsonIgnore] public string SpecColorTempRangeText => LightSchema is { HasColorTemp: true }
        ? $"规格标注 {SpecColorTempMin:0}–{SpecColorTempMax:0}K，可手动放宽或收窄"
        : "未读到设备规格，先按通用 2700–6500K 起调，可手动放宽或收窄";

    /// <summary>这盏灯是否设过自定义区间（行尾按钮据此显示「自定义 / 重置」）。</summary>
    [JsonIgnore] public bool HasCustomColorTempRange => _ctMinOverride.HasValue || _ctMaxOverride.HasValue;

    /// <summary>色温量程编辑区是否展开。</summary>
    [property: JsonIgnore] [ObservableProperty] private bool _isEditingLightRange;

    /// <summary>自定义量程时最小与最大之间至少留的行程（K），否则滑杆只剩一个点。</summary>
    public const int LightRangeGap = 100;

    /// <summary>编辑中的最小 / 最大色温（点「保存」才写进配置并生效）。</summary>
    [property: JsonIgnore] [ObservableProperty] private double _lightRangeEditMin;
    [property: JsonIgnore] [ObservableProperty] private double _lightRangeEditMax;

    /// <summary>编辑态最小值的上界：给最大值留出至少 <see cref="LightRangeGap"/> 的行程。</summary>
    [JsonIgnore] public double LightRangeMinUpper =>
        Math.Max(ColorTempFloor, Math.Min(ColorTempCeiling, LightRangeEditMax - LightRangeGap));

    /// <summary>编辑态最大值的下界：压在最小值上方至少 <see cref="LightRangeGap"/>。</summary>
    [JsonIgnore] public double LightRangeMaxLower =>
        Math.Max(ColorTempFloor, Math.Min(ColorTempCeiling, LightRangeEditMin + LightRangeGap));

    partial void OnLightRangeEditMinChanged(double value) =>
        OnPropertyChanged(nameof(LightRangeMaxLower));

    partial void OnLightRangeEditMaxChanged(double value) =>
        OnPropertyChanged(nameof(LightRangeMinUpper));

    partial void OnIsEditingLightRangeChanged(bool value) =>
        OnPropertyChanged(nameof(LightRangeToggleText));

    /// <summary>量程行尾按钮文字：没设过「自定义」，设过「调整」，展开中「收起」。</summary>
    [JsonIgnore] public string LightRangeToggleText =>
        IsEditingLightRange ? "收起" : HasCustomColorTempRange ? "调整" : "自定义";

    /// <summary>规格重建后让界面重新读量程与显隐。</summary>
    partial void OnLightSchemaChanged(LightControlSchema? value)
    {
        OnPropertyChanged(nameof(HasColorTemp));
        OnPropertyChanged(nameof(SpecColorTempMin));
        OnPropertyChanged(nameof(SpecColorTempMax));
        OnPropertyChanged(nameof(ColorTempMin));
        OnPropertyChanged(nameof(ColorTempMax));
        OnPropertyChanged(nameof(ColorTempStep));
        OnPropertyChanged(nameof(BrightnessMin));
        OnPropertyChanged(nameof(BrightnessMax));
        OnPropertyChanged(nameof(ColorTempRangeText));
        OnPropertyChanged(nameof(SpecColorTempRangeText));
        // 量程换了以后，原来的自定义区间要按新规格重新夹一遍（换型号/规格晚到时尤其重要）
        if (_ctMinOverride.HasValue || _ctMaxOverride.HasValue)
            ApplyLightRangeOverride(_ctMinOverride, _ctMaxOverride);
    }

    /// <summary>
    /// 套用用户自定义的色温区间（null 表示回到设备规格量程）。
    /// 只夹在兜底量程（1000–10000K）里，不按设备规格硬卡：不少型号真机能力比规格标注更宽，
    /// 规格写 3000–6500 的灯实际常常能到 7000K 乃至更低；能不能落色交给设备自己回话。
    /// 本地色温若落在新区间外也一并夹回来，界面不会停在拖不动的位置。
    /// </summary>
    public void ApplyLightRangeOverride(int? min, int? max)
    {
        var newMin = min is { } m ? Math.Clamp(m, ColorTempFloor, ColorTempCeiling) : (int?)null;
        var newMax = max is { } x ? Math.Clamp(x, ColorTempFloor, ColorTempCeiling) : (int?)null;
        // 区间小于 1K 就没行程了，这种配置当没设
        if (newMin is { } a && newMax is { } b && b - a < 1) newMin = newMax = null;

        if (newMin == _ctMinOverride && newMax == _ctMaxOverride) return;

        _ctMinOverride = newMin;
        _ctMaxOverride = newMax;
        OnPropertyChanged(nameof(ColorTempMin));
        OnPropertyChanged(nameof(ColorTempMax));
        OnPropertyChanged(nameof(ColorTempRangeText));
        OnPropertyChanged(nameof(HasCustomColorTempRange));
        OnPropertyChanged(nameof(LightRangeToggleText));

        var rangeMin = (int)Math.Round(ColorTempMin);
        var rangeMax = (int)Math.Round(ColorTempMax);
        if (ColorTemp < rangeMin) ColorTemp = rangeMin;
        else if (ColorTemp > rangeMax) ColorTemp = rangeMax;
    }

    /// <summary>
    /// 按规格解析灯光能力：亮度、色温的可写属性与值域，以及「可调色温」对应的预设 mode。
    /// 只认 light 服务里的属性；灯大类下找不到时退回同名属性（第三方型号偶尔把服务名写得不一样）。
    /// </summary>
    private void ApplyLight(DeviceSpec spec)
    {
        SpecPropertyDef? Pick(string name)
        {
            var inLight = spec.Properties.FirstOrDefault(p => p.Writable && p.IsNumeric && p.Name == name &&
                                                              p.ServiceName is "light" or "lamp");
            if (inLight != null) return inLight;
            return IsLight ? spec.Properties.FirstOrDefault(p => p.Writable && p.IsNumeric && p.Name == name) : null;
        }

        var bright = Pick("brightness");
        var ct = Pick("color-temperature");
        var mode = spec.Properties.FirstOrDefault(p => p.Writable && p.IsNumeric && p.Name == "mode" &&
                                                       p.ServiceName is "light" or "lamp");
        if (bright == null && ct == null && mode == null)
        {
            LightSchema = null;
            return;
        }

        var schema = new LightControlSchema();
        if (bright != null)
        {
            schema.HasBrightness = true;
            schema.BrightSiid = bright.Siid;
            schema.BrightPiid = bright.Piid;
            if (bright.HasRange)
            {
                schema.BrightMin = bright.Min;
                schema.BrightMax = bright.Max;
                schema.BrightStep = bright.Step <= 0 ? 1 : bright.Step;
            }
        }
        if (ct != null)
        {
            schema.HasColorTemp = true;
            schema.CtSiid = ct.Siid;
            schema.CtPiid = ct.Piid;
            if (ct.HasRange)
            {
                schema.CtMin = ct.Min;
                schema.CtMax = ct.Max;
                schema.CtStep = ct.Step <= 0 ? 1 : ct.Step;
            }
        }
        if (mode != null)
        {
            schema.ModeSiid = mode.Siid;
            schema.ModePiid = mode.Piid;
            // 预设场景（阅读/影院/温馨…）会把色温锁死在固定值，写 color-temperature 不生效。
            // 枚举里代表「不套预设、色温可调」的那一档按名字认：色温模式 / 空场景 / 无模式 / 正常模式。
            foreach (var o in mode.Options)
            {
                var label = o.Label;
                if (label.Contains("色温", StringComparison.Ordinal) ||
                    label.Contains("空场景", StringComparison.Ordinal) ||
                    label.Contains("无模式", StringComparison.Ordinal) ||
                    label.Contains("正常", StringComparison.Ordinal) ||
                    label.Contains("Color Temperature", StringComparison.OrdinalIgnoreCase) ||
                    label.Contains("None", StringComparison.OrdinalIgnoreCase) ||
                    label.Contains("Normal", StringComparison.OrdinalIgnoreCase))
                {
                    schema.ResetModeValue = o.Value;
                    break;
                }
            }
        }

        LightSchema = schema.Valid ? schema : null;
        if (schema.Valid)
        {
            // 量程变了以后，本地缓存的旧值可能落在区间外（比如 2700 落到 3000–6500 的灯上），先夹回区间
            ClampLightValues(schema);
        }
    }

    /// <summary>把本地亮度/色温夹进量程（含用户自定义的色温区间），避免滑杆一开始就停在区间外、拖到端点不产生变化。</summary>
    private void ClampLightValues(LightControlSchema schema)
    {
        if (schema.HasColorTemp)
        {
            var ct = Math.Clamp(ColorTemp, (int)Math.Round(ColorTempMin), (int)Math.Round(ColorTempMax));
            if (ct != ColorTemp) ColorTemp = ct;
        }
        if (schema.HasBrightness)
        {
            var b = Math.Clamp(Brightness, (int)Math.Round(BrightnessMin), (int)Math.Round(BrightnessMax));
            if (b != Brightness) Brightness = b;
        }
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
                case "fan": return "fan";
                case "camera": return "camera";
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

        if (!string.Equals(LocalIp, other.LocalIp, StringComparison.Ordinal))
            LocalIp = other.LocalIp;

        // 灯组成员来自设备列表的原始响应（DTO 不映射），刷新时复用旧实例，
        // 不在这里同步就会把成员表清空，单灯再也反查不到「只写了 group.xxx」的场景。
        if (!GroupMembers.SequenceEqual(other.GroupMembers))
        {
            GroupMembers.Clear();
            GroupMembers.AddRange(other.GroupMembers);
        }

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
        var legacyPower = key is "light" or "socket" or "ac" or "fan" or "camera";
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
        if (IsFan) ApplyFan(spec);
        ApplyLight(spec);
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

    /// <summary>
    /// 按规格解析风扇能力：档位（fan-level / speed-level，离散 or 量程）、摆风（bool）、模式（枚举）。
    /// 只认可写属性，读不到可写档位时面板不显示风速卡，剩下的属性仍走通用高级设置。
    /// </summary>
    private void ApplyFan(DeviceSpec spec)
    {
        var schema = new FanControlSchema();

        var level = spec.Properties.FirstOrDefault(p => p.Writable && p.IsNumeric && p.Name == "fan-level")
                 ?? spec.Properties.FirstOrDefault(p => p.Writable && p.IsNumeric &&
                        p.Name.StartsWith("fan-level", StringComparison.Ordinal))
                 ?? spec.Properties.FirstOrDefault(p => p.Writable && p.IsNumeric &&
                        p.Name is "wind-level" or "fan-speed" or "fan_speed")
                 ?? spec.Properties.FirstOrDefault(p => p.Writable && p.IsNumeric &&
                        p.ServiceName.StartsWith("fan", StringComparison.Ordinal) &&
                        p.Name.Contains("level", StringComparison.Ordinal) &&
                        !p.Name.Contains("angle", StringComparison.Ordinal));
        if (level != null)
        {
            schema.LevelSiid = level.Siid;
            schema.LevelPiid = level.Piid;
            if (level.Options.Count > 0)
            {
                foreach (var o in level.Options)
                    schema.Levels.Add(new AcOptionItem { Value = o.Value, Label = o.Label });
            }
            else if (level.HasRange)
            {
                schema.LevelHasRange = true;
                schema.LevelMin = level.Min;
                schema.LevelMax = level.Max;
                schema.LevelStep = level.Step <= 0 ? 1 : level.Step;
            }
        }

        // 无级调速是另一条属性（dmaker.fan.p9 之类同时有 1–4 挡和无级 1–100），单独出滑杆
        var speed = spec.Properties.FirstOrDefault(p => p.Writable && p.IsNumeric &&
            p.Name is "speed-level" or "fan-speed-percent" or "speed-percent" &&
            !ReferenceEquals(p, level));
        if (speed != null)
        {
            schema.SpeedSiid = speed.Siid;
            schema.SpeedPiid = speed.Piid;
            schema.SpeedHasRange = speed.HasRange;
            if (speed.HasRange)
            {
                schema.SpeedMin = speed.Min;
                schema.SpeedMax = speed.Max;
                schema.SpeedStep = speed.Step <= 0 ? 1 : speed.Step;
            }
        }

        var swingH = spec.Properties.FirstOrDefault(p => p.Writable && p.IsBool &&
            p.Name is "horizontal-swing" or "swing-horizontal" or "swing-h");
        var swingV = spec.Properties.FirstOrDefault(p => p.Writable && p.IsBool &&
            p.Name is "vertical-swing" or "swing-vertical" or "swing-v");
        // 只有一个摆风属性、名字就叫 swing 的型号，按左右摆风处理
        var swingAny = spec.Properties.FirstOrDefault(p =>
            p.Writable && p.IsBool && p.Name == "swing" && p.ServiceName.StartsWith("fan", StringComparison.Ordinal));
        if (swingH != null || swingAny != null)
        {
            var sw = swingH ?? swingAny!;
            schema.SwingHSiid = sw.Siid;
            schema.SwingHPiid = sw.Piid;
        }
        if (swingV != null)
        {
            schema.SwingVSiid = swingV.Siid;
            schema.SwingVPiid = swingV.Piid;
        }

        var mode = spec.Properties.FirstOrDefault(p => p.Writable && p.IsNumeric && p.Options.Count > 0 &&
            p.Name is "mode" or "fan-mode" && p.ServiceName.StartsWith("fan", StringComparison.Ordinal));
        if (mode != null)
        {
            schema.ModeSiid = mode.Siid;
            schema.ModePiid = mode.Piid;
            foreach (var o in mode.Options)
                schema.Modes.Add(new AcOptionItem { Value = o.Value, Label = o.Label });
        }

        schema.Valid = schema.LevelPiid > 0 || schema.HasSpeed || schema.HasSwingH || schema.HasSwingV || schema.ModePiid > 0;
        FanSchema = schema.Valid ? schema : null;
        if (schema.Valid) ApplyFanValuesCache();
    }

    /// <summary>规格重建后把缓存里的当前值重新标到选项上（否则选中态会全灭）。</summary>
    private void ApplyFanValuesCache()
    {
        foreach (var o in FanSchema!.Levels) o.IsSelected = o.Value == FanLevel;
        foreach (var o in FanSchema!.Modes) o.IsSelected = o.Value == FanMode;
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

    /// <summary>
    /// 规格还没解析到（离线 / 首次启动）时，用被并回父卡的单键子设备补齐通道，
    /// 否则子卡藏起来后这几个键就再也点不到了。下发走的是同一套「父 did + 通道 siid」。
    /// </summary>
    public void EnsureFallbackChannels(IEnumerable<MijiaDevice> children)
    {
        if (Spec != null || Channels.Count > 0) return;
        foreach (var child in children.Where(c => c.ChannelSiid > 0).OrderBy(c => c.ChannelSiid))
        {
            Channels.Add(new SwitchChannelState
            {
                Siid = child.ChannelSiid,
                Label = string.IsNullOrWhiteSpace(child.Name) ? $"路 {child.ChannelSiid}" : child.Name,
                Device = this,
                IsOn = child.IsOn
            });
        }

        if (Channels.Count == 0) return;
        HasPower = false;
        SyncOnFromChannels();
        RaiseControlChanged();
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

    /// <summary>spec_type 里的设备大类（空表示规格还没到）。卡片外显按大类分组用。</summary>
    [JsonIgnore] public string CategoryName => Category;

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
