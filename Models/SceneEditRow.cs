using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiHome.Models;

/// <summary>编辑面板里一张卡片的类型。米家的动作不止「写属性」一种，必须按 type 分派。</summary>
public enum SceneEditRowKind
{
    /// <summary>set_properties：对某台设备写一个属性（可改值）。</summary>
    DeviceProp,

    /// <summary>command=action：设备动作（如「开/关」），参数不是属性值，本应用只展示。</summary>
    DeviceAction,

    /// <summary>tts：音箱播报（可改文字）。</summary>
    Tts,

    /// <summary>type=2：执行另一条场景。</summary>
    SceneRun,

    /// <summary>type=3：开启 / 关闭另一条场景。</summary>
    SceneToggle,

    /// <summary>type=1：给手机发通知。</summary>
    Notify,

    /// <summary>延时：不是独立动作，而是下面若干动作共用的 delay_time，界面上单列一张卡。</summary>
    Delay,

    /// <summary>没识别出来的动作，原样展示，不动它。</summary>
    Unknown
}

/// <summary>
/// 编辑面板里的一张卡片。所有动作平铺、同级，可以拖拽换顺序。
/// 直接持原始动作节点，改动就地写回 JSON，保存时整条场景对象原样回传 AppSceneService/Edit。
/// 「延时」卡是虚拟行：它没有自己的动作节点，只代表下面一段动作共用的 delay_time。
/// </summary>
public sealed partial class SceneEditActionRow : ObservableObject
{
    /// <summary>对应的原始动作节点（延时卡没有）。</summary>
    public JsonObject? Action { get; init; }

    public required SceneEditRowKind Kind { get; init; }

    /// <summary>动作的 payload_json 节点。</summary>
    public JsonObject? Payload { get; init; }

    [ObservableProperty] private string _title = "";

    /// <summary>副标题（可随控件变化，例如场景开关的「执行后关闭」）。</summary>
    [ObservableProperty] private string _subtitle = "";

    /// <summary>
    /// 卡片副标题：真动作直接用 Subtitle，延时卡用实时算出来的说明。
    /// 设备属性卡优先用「房间 · 当前状态」（关闭 / 打开 / 亮度 50%），
    /// 状态随右侧控件变化实时重算——不把「开关」这类属性标识文字摆到界面上。
    /// </summary>
    public string Caption => IsVirtual
        ? DelayCaption
        : HasValues && Primary is { } primary
            ? (RoomName.Length > 0 ? $"{RoomName} · {primary.ActionText}" : primary.ActionText)
            : Subtitle;

    partial void OnSubtitleChanged(string value) => OnPropertyChanged(nameof(Caption));

    /// <summary>房间名（属性卡副标题的前半段），没有房间就留空。</summary>
    public string RoomName { get; init; } = "";

    /// <summary>拖拽中的卡片半透明显示。</summary>
    [ObservableProperty] private bool _isDragging;

    /// <summary>
    /// 删除前的弱确认：第一次点 × 只把这张卡标红待确认，再点一次才真删。
    /// 比弹确认框轻——用户只是想删一条动作，不值得打断一次模态。
    /// </summary>
    [ObservableProperty] private bool _pendingRemove;

    /// <summary>弱确认的守卫号：每次点 × 自增，用来作废上一轮的自动复位计时。</summary>
    public int RemoveConfirmToken { get; set; }

    /// <summary>虚拟行（目前只有延时卡）：不写报文、不能拖动。</summary>
    public bool IsVirtual => Kind == SceneEditRowKind.Delay;
    public bool IsDelay => Kind == SceneEditRowKind.Delay;
    public bool IsRealAction => !IsVirtual;

    public bool IsDeviceProp => Kind == SceneEditRowKind.DeviceProp;
    public bool IsSceneRun => Kind == SceneEditRowKind.SceneRun;
    public bool IsSceneToggle => Kind == SceneEditRowKind.SceneToggle;
    public bool IsTts => Kind == SceneEditRowKind.Tts;
    public bool IsNotify => Kind == SceneEditRowKind.Notify;
    public bool IsUnknown => Kind == SceneEditRowKind.Unknown;

    /// <summary>设备属性写入项（set_properties 才有，通常是 1 条）。</summary>
    public IReadOnlyList<SceneActionEditItem> Values { get; init; } = Array.Empty<SceneActionEditItem>();
    public bool HasValues => Values.Count > 0;
    public bool HasSingleValue => Values.Count == 1;
    public bool HasMultipleValues => Values.Count > 1;
    public SceneActionEditItem? Primary => Values.Count > 0 ? Values[0] : null;

    // ===== 延时 =====

    /// <summary>
    /// 改延时时要写回的 payload 节点集合：这一张延时卡管辖它下面连续的一段动作，
    /// 段内每个动作的 delay_time 都要一起改，否则拍平后同一段会出现互相矛盾的多个延时。
    /// </summary>
    public IReadOnlyList<JsonObject> DelayTargets { get; init; } = Array.Empty<JsonObject>();

    [ObservableProperty] private double _delaySeconds;

    /// <summary>延时卡的副标题：人话描述这一等等多久、管几项。</summary>
    public string DelayCaption
    {
        get
        {
            var seconds = (int)Math.Round(DelaySeconds);
            if (seconds <= 0) return "0 表示不延时，保存后这一步会消失";
            var count = DelayTargets.Count;
            return count > 1
                ? $"等待 {FormatDelay(seconds)}后，一起执行下面 {count} 项"
                : $"等待 {FormatDelay(seconds)}后执行下面 1 项";
        }
    }

    partial void OnDelaySecondsChanged(double value)
    {
        OnPropertyChanged(nameof(DelayCaption));
        OnPropertyChanged(nameof(Caption));
        if (!_ready) return;

        var seconds = (int)Math.Round(value);
        if (seconds < 0) seconds = 0;
        foreach (var p in DelayTargets)
        {
            try { p["delay_time"] = seconds; } catch { }
        }
    }

    /// <summary>把秒数说成人话：0 / 45 秒 / 3 分钟 / 1 小时 10 分。</summary>
    public static string FormatDelay(int seconds)
    {
        if (seconds <= 0) return "无延时";
        if (seconds < 60) return $"{seconds} 秒";
        var m = seconds / 60;
        var s = seconds % 60;
        if (m < 60) return s == 0 ? $"{m} 分钟" : $"{m} 分 {s} 秒";
        var h = m / 60;
        m %= 60;
        return m == 0 ? $"{h} 小时" : $"{h} 小时 {m} 分";
    }

    // ===== 执行场景 =====

    /// <summary>
    /// 目标场景候选（走程序内自绘的 OptionPicker，不再用系统 ComboBox：
    /// 原生 ComboBox 在滚轮滚动时会直接把选中项切走）。
    /// </summary>
    public IReadOnlyList<OptionItem> SceneOptions { get; init; } = Array.Empty<OptionItem>();

    /// <summary>
    /// 当前选中的目标场景 id，与 OptionPicker 双向绑定。
    /// 只有用户真的在浮层里点了别的场景才会走到写回（初始赋值时 _ready 还是 false）。
    /// </summary>
    [ObservableProperty] private string _selectedSceneId = "";

    partial void OnSelectedSceneIdChanged(string value)
    {
        if (!_ready) return;
        ApplySceneId(value);
    }

    /// <summary>把 scene_id 写回报文，同时刷新卡片副标题。</summary>
    public void ApplySceneId(string sceneId)
    {
        if (string.IsNullOrEmpty(sceneId)) return;

        var label = sceneId;
        foreach (var option in SceneOptions)
        {
            if (!Equals(option.Value, sceneId)) continue;
            label = option.Label;
            break;
        }

        try
        {
            if (Payload != null) Payload["scene_id"] = sceneId;
            if (Action != null) Action["name"] = $"执行 “{label}”";
        }
        catch { }
        Subtitle = $"顺序执行「{label}」";
    }

    // ===== 场景启停 =====

    [ObservableProperty] private bool _toggleEnabled;

    partial void OnToggleEnabledChanged(bool value)
    {
        if (!_ready) return;
        try { if (Payload != null) Payload["enable"] = value; } catch { }
        Subtitle = value ? "执行后开启该场景" : "执行后关闭该场景";
    }

    // ===== 播报 =====

    [ObservableProperty] private string _ttsText = "";

    partial void OnTtsTextChanged(string value)
    {
        if (!_ready) return;
        try
        {
            if (Payload?["value"] is JsonArray arr && arr.Count > 0) arr[0] = value;
            else if (Payload != null) Payload["value"] = new JsonArray(value);
        }
        catch { }
    }

    // ===== 通知 =====

    [ObservableProperty] private string _notifyTitle = "";

    partial void OnNotifyTitleChanged(string value)
    {
        if (!_ready) return;
        try { if (Payload != null) Payload["title"] = value; } catch { }
    }

    [ObservableProperty] private string _notifyDesc = "";

    partial void OnNotifyDescChanged(string value)
    {
        if (!_ready) return;
        try { if (Payload != null) Payload["desc"] = value; } catch { }
    }

    // ===== 初始赋值保护 =====

    private bool _ready;

    /// <summary>构造期间不写回节点；所有初始值填完后调用一次。</summary>
    public void MarkReady()
    {
        _ready = true;
        // 副标题里带着「打开 / 关闭」这类状态词，值一变就得跟着刷新
        Primary?.PropertyChanged += OnPrimaryChanged;
    }

    private void OnPrimaryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Caption));

    /// <summary>读一个可能来自 int / long / string 的整数字段。</summary>
    public static int IntOf(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var si)) return si;
        return 0;
    }
}
