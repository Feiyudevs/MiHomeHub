// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiHomeHub.Services;

/// <summary>窗口行为偏好持久化在 %APPDATA%\Feiyu\MiHomeHub\app.json，与 theme.json 同目录。</summary>
public static class AppSettingsStore
{
    private static readonly string Dir = AppPaths.Dir;
    private static readonly string FilePath = Path.Combine(Dir, "app.json");

    public static bool AutoStart { get; private set; } = true;

    /// <summary>true = 点关闭收进托盘继续运行；false = 直接退出。</summary>
    public static bool CloseToTray { get; private set; } = true;

    /// <summary>false = 启动后直接静默驻留托盘，不弹主界面。</summary>
    public static bool ShowMainWindowOnStartup { get; private set; }

    /// <summary>小爱语音指令默认下发的音箱 did；空串表示还没选过。</summary>
    public static string DefaultSpeakerDid { get; private set; } = "";

    /// <summary>true = 卡片带投影；false = 扁平无投影。</summary>
    public static bool CardShadow { get; private set; } = true;

    /// <summary>双击桌面空白处：显示 / 隐藏所有桌面分区卡片（运行期状态，不写回分区总开关）。</summary>
    public static bool DoubleClickDesktopToggles { get; private set; } = true;

    /// <summary>鼠标移入折叠的桌面分区时自动展开；移出后自动收起。</summary>
    public static bool HoverExpandEnabled { get; private set; } = true;

    /// <summary>上次选中的家庭 id；空 = 用米家返回的第一个家庭。</summary>
    public static string HomeId { get; private set; } = "";

    /// <summary>侧栏房间的自定义顺序（不含「全部」）；空表示仍按设备数量自动排。</summary>
    public static string[] RoomOrder { get; private set; } = Array.Empty<string>();

    /// <summary>true = 设备列表不显示墙壁开关 / 场景按键（多键并入所属开关）；false = 全部显示。</summary>
    public static bool HideSwitches { get; private set; }

    /// <summary>用户单独隐藏的设备 did：主列表里折到末尾的「已隐藏」区，控制与场景不受影响。</summary>
    public static string[] HiddenDids { get; private set; } = Array.Empty<string>();

    /// <summary>摄像头画面用的本地 go2rtc 地址（空 = 默认 http://127.0.0.1:1984）。</summary>
    public static string Go2rtcBase { get; private set; } = "";

    /// <summary>中枢网关「自动化极客版」的地址缓存：key = 网关 did，value = 局域网 IP。
    /// 云端设备列表带 localip 时优先用云端的；没带或换了网段时用这里记下的手动值。</summary>
    public static IReadOnlyDictionary<string, string> HubIps => _hubIps;

    private static Dictionary<string, string> _hubIps = new(StringComparer.Ordinal);

    /// <summary>卡片外显参数：key = 设备类型，value = 卡片副标题上要显示的规格属性短名。
    /// 空表示该类型还没调过，按 <see cref="CardMetricsCatalog.Defaults(string)"/> 兜底。</summary>
    public static IReadOnlyDictionary<string, string[]> CardMetrics => _cardMetrics;

    private static Dictionary<string, string[]> _cardMetrics = new(StringComparer.Ordinal);

    static AppSettingsStore()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("autoStart", out var a))
                AutoStart = a.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("closeToTray", out var c))
                CloseToTray = c.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("showMainWindowOnStartup", out var w))
                ShowMainWindowOnStartup = w.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("defaultSpeakerDid", out var s) && s.ValueKind == JsonValueKind.String)
                DefaultSpeakerDid = s.GetString() ?? "";
            if (root.TryGetProperty("cardShadow", out var d))
                CardShadow = d.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("doubleClickDesktopToggles", out var dc))
                DoubleClickDesktopToggles = dc.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("hoverExpand", out var he))
                HoverExpandEnabled = he.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("go2rtcBase", out var gb) && gb.ValueKind == JsonValueKind.String)
                Go2rtcBase = gb.GetString() ?? "";
            if (root.TryGetProperty("hubIps", out var hi) && hi.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in hi.EnumerateObject())
                    if (item.Value.ValueKind == JsonValueKind.String &&
                        item.Value.GetString() is { Length: > 0 } ip)
                        _hubIps[item.Name] = ip;
            }
            if (root.TryGetProperty("hideSwitches", out var hs))
                HideSwitches = hs.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("homeId", out var hid) && hid.ValueKind == JsonValueKind.String)
                HomeId = hid.GetString() ?? "";
            if (root.TryGetProperty("hiddenDids", out var hd) && hd.ValueKind == JsonValueKind.Array)
            {
                var dids = new List<string>();
                foreach (var item in hd.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } did)
                        dids.Add(did);
                HiddenDids = dids.ToArray();
            }
            if (root.TryGetProperty("cardMetrics", out var cm) && cm.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in cm.EnumerateObject())
                {
                    if (item.Value.ValueKind != JsonValueKind.Array) continue;
                    var names = new List<string>();
                    foreach (var n in item.Value.EnumerateArray())
                        if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name)
                            names.Add(name);
                    _cardMetrics[item.Name] = names.ToArray();
                }
            }
            if (root.TryGetProperty("roomOrder", out var ro) && ro.ValueKind == JsonValueKind.Array)
            {
                var order = new List<string>();
                foreach (var item in ro.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                        order.Add(name);
                RoomOrder = order.ToArray();
            }
        }
        catch
        {
            // 偏好文件损坏时静默回落默认值
        }
    }

    public static void SetAutoStart(bool on)
    {
        AutoStart = on;
        Save();
    }

    public static void SetCloseToTray(bool on)
    {
        CloseToTray = on;
        Save();
    }

    public static void SetShowMainWindowOnStartup(bool on)
    {
        ShowMainWindowOnStartup = on;
        Save();
    }

    public static void SetDefaultSpeakerDid(string did)
    {
        DefaultSpeakerDid = did;
        Save();
    }

    public static void SetCardShadow(bool on)
    {
        CardShadow = on;
        Save();
    }

    public static void SetDoubleClickDesktopToggles(bool on)
    {
        DoubleClickDesktopToggles = on;
        Save();
    }

    public static void SetHoverExpandEnabled(bool on)
    {
        HoverExpandEnabled = on;
        Save();
    }

    /// <summary>隐藏墙壁开关 / 场景按键类设备。</summary>
    public static void SetHideSwitches(bool on)
    {
        HideSwitches = on;
        Save();
    }

    /// <summary>单独隐藏的设备清单；写盘只存 did。</summary>
    public static void SetHiddenDids(IEnumerable<string> dids)
    {
        HiddenDids = dids.Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        Save();
    }

    /// <summary>记住当前选中的家庭，下次启动直接显示这个家的设备。</summary>
    public static void SetHomeId(string? id)
    {
        var raw = (id ?? "").Trim();
        if (HomeId == raw) return;
        HomeId = raw;
        Save();
    }

    /// <summary>侧栏房间拖拽后的新顺序；只存房间名，不存「全部」。</summary>
    public static void SetRoomOrder(IEnumerable<string> names)
    {
        RoomOrder = names.ToArray();
        Save();
    }

    /// <summary>摄像头桥接地址；写空表示回到默认 127.0.0.1:1984。</summary>
    public static void SetGo2rtcBase(string? url)
    {
        var raw = (url ?? "").Trim();
        if (raw.Length > 0 && !raw.Contains("://", StringComparison.Ordinal))
            raw = "http://" + raw;
        Go2rtcBase = raw.TrimEnd('/');
        Save();
    }

    /// <summary>中枢网关极客版用的 IP；写空 = 删掉这个网关的记录（下次重开抽屉按云端 localip 兜底）。</summary>
    public static void SetHubIp(string did, string? ip)
    {
        if (string.IsNullOrWhiteSpace(did)) return;
        var raw = (ip ?? "").Trim();
        // 允许直接粘「http://192.168.x.x」或带端口，统一存成裸 IP
        if (raw.Contains("://", StringComparison.Ordinal))
            raw = raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..];
        raw = raw.Split('/')[0].Split(':')[0].Trim();
        if (raw.Length == 0)
        {
            if (_hubIps.Remove(did)) Save();
            return;
        }
        if (_hubIps.TryGetValue(did, out var old) && old == raw) return;
        _hubIps[did] = raw;
        Save();
    }

    public static string GetHubIp(string did) =>
        string.IsNullOrWhiteSpace(did) ? "" : (_hubIps.TryGetValue(did, out var ip) ? ip : "");

    /// <summary>卡片外显参数按设备类型写入；空数组表示这类设备一张读数都不外显。</summary>
    public static void SetCardMetrics(string typeKey, IEnumerable<string> names)
    {
        _cardMetrics[typeKey] = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Save();
    }

    /// <summary>去掉某类型的自定义外显，回到默认（插座=功率，其余=温度+湿度）。</summary>
    public static void ClearCardMetrics(string typeKey)
    {
        if (_cardMetrics.Remove(typeKey)) Save();
    }

    private static void Save()
    {
        var payload = JsonSerializer.Serialize(new
        {
            autoStart = AutoStart,
            closeToTray = CloseToTray,
            showMainWindowOnStartup = ShowMainWindowOnStartup,
            defaultSpeakerDid = DefaultSpeakerDid,
            cardShadow = CardShadow,
            doubleClickDesktopToggles = DoubleClickDesktopToggles,
            hoverExpand = HoverExpandEnabled,
            hideSwitches = HideSwitches,
            homeId = HomeId,
            hiddenDids = HiddenDids,
            roomOrder = RoomOrder,
            go2rtcBase = Go2rtcBase,
            hubIps = _hubIps,
            cardMetrics = _cardMetrics
        });
        // 写盘出 UI 线程；进程被强杀时可能丢最后一次偏好，可接受
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, payload);
            }
            catch
            {
                // 写入失败不影响本次运行
            }
        });
    }
}
