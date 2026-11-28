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

    /// <summary>桌面空调组件绑定的空调 did；空串表示自动取第一台在线空调。</summary>
    public static string DesktopAcDid { get; private set; } = "";

    /// <summary>桌面监控分区绑定的摄像头 did；空串表示自动取第一台摄像头。</summary>
    public static string CameraDid { get; private set; } = "";

    /// <summary>true = 卡片带投影；false = 扁平无投影。</summary>
    public static bool CardShadow { get; private set; } = true;

    /// <summary>
    /// 硬件加速渲染：true = 走 AngleEgl + DirectComposition，false = 软件渲染。
    /// 默认打开：没有可用显卡的机器由 Avalonia 的渲染回退自动降到软件渲染，不会起不来；
    /// 需要省内存的用户可在设置里关掉（或命令行 --lowmem）。
    /// 只在启动构建 AppBuilder 时生效，改完必须重启应用。
    /// </summary>
    public static bool UseGpu { get; private set; } = true;

    /// <summary>双击桌面空白处：显示 / 隐藏所有桌面分区卡片（运行期状态，不写回分区总开关）。</summary>
    public static bool DoubleClickDesktopToggles { get; private set; } = true;

    /// <summary>
    /// 桌面分区卡片「显示 / 隐藏」的全局快捷键组合（macOS 用 Carbon 注册，免辅助功能权限）。
    /// 空串 = 不注册。默认 <c>Alt+Cmd+D</c>。
    /// </summary>
    public static string DesktopToggleHotkey { get; private set; } = DefaultDesktopToggleHotkey;

    /// <summary>
    /// 桌面卡片显隐快捷键的默认组合。
    /// 之前这里被下拉框写回的空串覆盖过一次，导致快捷键悄悄失效——
    /// 现在读盘时空串一律回落成默认值，下拉框那边也不再接受空值。
    /// </summary>
    public const string DefaultDesktopToggleHotkey = "Alt+Cmd+D";

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

    /// <summary>打开摄像头实时画面时默认静音；关掉则接上就出声。</summary>
    public static bool CameraMuteOnOpen { get; private set; } = true;
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

    /// <summary>趋势卡选中的数据来源：key = temp / hum / power / water / tds，value = 设备 did；空串（缺键）= 全屋口径。</summary>
    private static Dictionary<string, string> _trendSources = new(StringComparer.Ordinal);

    /// <summary>首页趋势卡的显示开关：key = temp / hum / power / water / gas / tds；
    /// 缺键表示还没选过，默认全部铺出来（没有设备的卡展示预览数据）。</summary>
    private static Dictionary<string, bool> _trendCards = new(StringComparer.Ordinal);

    /// <summary>首页趋势卡的排列顺序（key 数组）；空表示还没拖过，按默认顺序铺。</summary>
    private static List<string> _trendCardOrder = new();

    /// <summary>用户是否把整个「数据看板」从首页隐藏（与单卡显隐独立；隐藏后可从设置重新打开）。</summary>
    public static bool DataBoardHidden { get; private set; }

    /// <summary>
    /// 「数据看板」自己的重算间隔（分钟）：0 = 跟随设备轮询（约 40 秒一次），1 / 5 / 15 = 隔这么久才重算读数与曲线。
    /// 只影响这块看板，设备卡与首页统计照旧。
    /// </summary>
    public static int DataBoardRefreshMinutes { get; private set; }

    /// <summary>「预览数据」说明弹窗是否已经自动提示过：只自动弹一次，之后点卡片上的「预览」标识再看。</summary>
    public static bool TrendPreviewNoticeSeen { get; private set; }

    /// <summary>每盏灯自定义的色温区间：key = 设备 did，value = [最小K, 最大K]；没设过的灯按设备规格量程。</summary>
    private static Dictionary<string, int[]> _lightRanges = new(StringComparer.Ordinal);

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
            if (root.TryGetProperty("desktopAcDid", out var ad) && ad.ValueKind == JsonValueKind.String)
                DesktopAcDid = ad.GetString() ?? "";
            if (root.TryGetProperty("cameraDid", out var cd) && cd.ValueKind == JsonValueKind.String)
                CameraDid = cd.GetString() ?? "";
            if (root.TryGetProperty("cardShadow", out var d))
                CardShadow = d.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("useGpu", out var ug))
                UseGpu = ug.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("doubleClickDesktopToggles", out var dc))
                DoubleClickDesktopToggles = dc.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("desktopToggleHotkey", out var dk) && dk.ValueKind == JsonValueKind.String)
            {
                var combo = dk.GetString();
                DesktopToggleHotkey = string.IsNullOrWhiteSpace(combo) ? DefaultDesktopToggleHotkey : combo.Trim();
            }
            if (root.TryGetProperty("hoverExpand", out var he))
                HoverExpandEnabled = he.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("cameraMuteOnOpen", out var muteOnOpen)) 
                CameraMuteOnOpen = muteOnOpen.ValueKind != JsonValueKind.False;
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
            if (root.TryGetProperty("trendSources", out var ts) && ts.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in ts.EnumerateObject())
                    if (item.Value.ValueKind == JsonValueKind.String &&
                        item.Value.GetString() is { Length: > 0 } did)
                        _trendSources[item.Name] = did;
            }
            if (root.TryGetProperty("trendCards", out var tc) && tc.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in tc.EnumerateObject())
                    if (item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        _trendCards[item.Name] = item.Value.ValueKind == JsonValueKind.True;
            }
            if (root.TryGetProperty("trendCardOrder", out var tco) && tco.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tco.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String &&
                        item.GetString() is { Length: > 0 } key &&
                        !_trendCardOrder.Contains(key))
                        _trendCardOrder.Add(key);
            }
            if (root.TryGetProperty("dataBoardHidden", out var dbh))
                DataBoardHidden = dbh.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("dataBoardRefreshMinutes", out var dbrm) && dbrm.ValueKind == JsonValueKind.Number)
            {
                var minutes = (int)Math.Round(dbrm.GetDouble());
                if (minutes is 1 or 5 or 15) DataBoardRefreshMinutes = minutes;
            }
            if (root.TryGetProperty("trendPreviewNoticeSeen", out var tpn))
                TrendPreviewNoticeSeen = tpn.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("roomOrder", out var ro) && ro.ValueKind == JsonValueKind.Array)
            {
                var order = new List<string>();
                foreach (var item in ro.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                        order.Add(name);
                RoomOrder = order.ToArray();
            }
            if (root.TryGetProperty("lightRanges", out var lr) && lr.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in lr.EnumerateObject())
                {
                    if (item.Value.ValueKind != JsonValueKind.Array) continue;
                    var pair = item.Value.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.Number)
                        .Select(v => (int)Math.Round(v.GetDouble()))
                        .ToArray();
                    if (pair.Length == 2 && pair[0] > 0 && pair[1] > pair[0])
                        _lightRanges[item.Name] = pair;
                }
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

    public static void SetDesktopAcDid(string did)
    {
        DesktopAcDid = did;
        Save();
    }

    /// <summary>桌面监控分区看哪台摄像头；写空 = 自动取设备表里的第一台。</summary>
    public static void SetCameraDid(string did)
    {
        CameraDid = did;
        Save();
    }

    public static void SetCameraMuteOnOpen(bool on)
    {
        CameraMuteOnOpen = on;
        Save();
    }
    public static void SetCardShadow(bool on)
    {
        CardShadow = on;
        Save();
    }

    /// <summary>硬件加速开关；下次启动构建渲染管线时读取，当前会话不切换。</summary>
    public static void SetUseGpu(bool on)
    {
        UseGpu = on;
        Save();
    }

    public static void SetDoubleClickDesktopToggles(bool on)
    {
        DoubleClickDesktopToggles = on;
        Save();
    }

    /// <summary>
    /// 设置桌面分区卡片显隐的全局快捷键组合。
    /// 空串按「忽略」处理：设置页那个下拉框在当前值不在候选里时会把 SelectedItem 置空并回写，
    /// 结果就是用户的组合被悄悄抹掉、快捷键失效（真发生过一次）。
    /// 想真的取消绑定请用 <see cref="ClearDesktopToggleHotkey"/>。
    /// </summary>
    public static void SetDesktopToggleHotkey(string combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return;
        DesktopToggleHotkey = combo.Trim();
        Save();
    }

    /// <summary>明确取消桌面卡片显隐快捷键（不再注册任何组合）。</summary>
    public static void ClearDesktopToggleHotkey()
    {
        DesktopToggleHotkey = "";
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

    /// <summary>趋势卡选中的数据来源；<paramref name="key"/> 取 temp / hum / power / water / gas / tds。</summary>
    public static string GetTrendSource(string key) =>
        _trendSources.TryGetValue(key, out var did) ? did : "";

    /// <summary>记住趋势卡用哪台设备的值；写空表示回到全屋口径。</summary>
    public static void SetTrendSource(string key, string? did)
    {
        var raw = (did ?? "").Trim();
        if (GetTrendSource(key) == raw) return;
        if (raw.Length == 0) _trendSources.Remove(key);
        else _trendSources[key] = raw;
        Save();
    }

    /// <summary>首页某张趋势卡用户选过显隐没有：null = 没选过，跟随设备自动决定。</summary>
    public static bool? GetTrendCardShown(string key) =>
        _trendCards.TryGetValue(key, out var on) ? on : null;

    /// <summary>记住某张趋势卡显不显示：关掉的卡不上首页；打开的卡没设备也会展示预览数据。</summary>
    public static void SetTrendCardShown(string key, bool on)
    {
        if (GetTrendCardShown(key) == on) return;
        _trendCards[key] = on;
        Save();
    }

    /// <summary>首页趋势卡当前的排列顺序；空数组表示还没拖过，按默认顺序走。</summary>
    public static IReadOnlyList<string> GetTrendCardOrder() => _trendCardOrder;

    /// <summary>记住用户拖出来的卡片顺序。去重并只留前 12 个 key（七张大卡 + 五枚空气小卡），顺序没变就不重复写盘。</summary>
    public static void SetTrendCardOrder(IEnumerable<string> keys)
    {
        var next = keys.Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();
        if (next.Count == 0 || _trendCardOrder.SequenceEqual(next)) return;

        _trendCardOrder.Clear();
        _trendCardOrder.AddRange(next);
        Save();
    }

    /// <summary>「恢复默认」一次清掉显隐、排序和整块隐藏：三件事只写一次盘，避免异步写互相覆盖。</summary>
    public static void ClearTrendCardLayout()
    {
        if (_trendCards.Count == 0 && _trendCardOrder.Count == 0 && !DataBoardHidden) return;
        _trendCards.Clear();
        _trendCardOrder.Clear();
        DataBoardHidden = false;
        Save();
    }

    /// <summary>从设置重新打开数据看板：取消整块隐藏；showAllCards 为 true 时把所有卡片恢复显示（保留拖动顺序）。
    /// 两件事一次写盘，避免「取消隐藏」和「恢复卡片」两次异步写互相覆盖。</summary>
    public static void ShowDataBoard(bool showAllCards)
    {
        var changed = DataBoardHidden;
        DataBoardHidden = false;
        if (showAllCards && _trendCards.Count > 0)
        {
            _trendCards.Clear();
            changed = true;
        }
        if (changed) Save();
    }

    /// <summary>隐藏 / 显示整块「数据看板」；不改变每张卡自己的显隐选择。</summary>
    public static void SetDataBoardHidden(bool hidden)
    {
        if (DataBoardHidden == hidden) return;
        DataBoardHidden = hidden;
        Save();
    }

    /// <summary>记下「预览数据说明」已经自动提示过。</summary>
    public static void SetTrendPreviewNoticeSeen()
    {
        if (TrendPreviewNoticeSeen) return;
        TrendPreviewNoticeSeen = true;
        Save();
    }

    /// <summary>「数据看板」刷新频率：0 = 跟随设备刷新，1 / 5 / 15 = 每分钟 / 每 5 分钟 / 每 15 分钟重算一次。</summary>
    public static int GetDataBoardRefreshMinutes() => DataBoardRefreshMinutes;

    /// <summary>记住看板的刷新节奏；非法值一律落回「跟随设备刷新」。</summary>
    public static void SetDataBoardRefreshMinutes(int minutes)
    {
        var value = minutes is 1 or 5 or 15 ? minutes : 0;
        if (DataBoardRefreshMinutes == value) return;
        DataBoardRefreshMinutes = value;
        Save();
    }

    /// <summary>用电卡选中的范围：0 = 今日逐小时，7 / 30 / 180 / 365 = 近几天逐日；没选过默认近 30 天。</summary>
    public static int GetPowerRange()
    {
        var raw = GetTrendSource("powerRange");
        return int.TryParse(raw, out var days) && days is 0 or 7 or 30 or 180 or 365 ? days : 30;
    }

    /// <summary>记住用电卡看的是今天的逐小时还是几天来的逐日。</summary>
    public static void SetPowerRange(int days) =>
        SetTrendSource("powerRange", (days is 7 or 30 or 180 or 365 ? days : 0).ToString());

    /// <summary>用水卡选中的范围：0 = 今日逐小时，7 / 30 / 180 = 近几天逐日，month = 本月；没选过默认今天。</summary>
    public static string GetWaterRange()
    {
        var raw = GetTrendSource("waterRange");
        return raw is "7" or "30" or "180" or "month" ? raw : "0";
    }

    /// <summary>记住用水卡看今天、近几天、本月还是近半年。</summary>
    public static void SetWaterRange(string range) =>
        SetTrendSource("waterRange", range is "7" or "30" or "180" or "month" ? range : "0");

    /// <summary>用气卡选中的范围：0 = 今日逐小时，7 / 30 / 180 = 近几天逐日，month = 本月；没选过默认今天。</summary>
    public static string GetGasRange()
    {
        var raw = GetTrendSource("gasRange");
        return raw is "7" or "30" or "180" or "month" ? raw : "0";
    }

    /// <summary>记住用气卡看今天、近几天、本月还是近半年。</summary>
    public static void SetGasRange(string range) =>
        SetTrendSource("gasRange", range is "7" or "30" or "180" or "month" ? range : "0");

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

    /// <summary>记住某盏灯的色温区间（只认 did：同一 physical 灯换 did 不共享，符合「每台设备各自设」）。</summary>
    public static bool TryGetLightRange(string did, out int min, out int max)
    {
        min = max = 0;
        if (string.IsNullOrEmpty(did) || !_lightRanges.TryGetValue(did, out var pair) || pair.Length != 2) return false;
        min = pair[0];
        max = pair[1];
        return min > 0 && max > min;
    }

    /// <summary>写入某盏灯的自定义色温区间。</summary>
    public static void SetLightRange(string did, int min, int max)
    {
        if (string.IsNullOrEmpty(did) || min <= 0 || max <= min) return;
        _lightRanges[did] = new[] { min, max };
        Save();
    }

    /// <summary>删掉某盏灯的自定义区间，回到设备规格量程。</summary>
    public static void ClearLightRange(string did)
    {
        if (!string.IsNullOrEmpty(did) && _lightRanges.Remove(did)) Save();
    }

    private static void Save()
    {
        var payload = JsonSerializer.Serialize(new
        {
            autoStart = AutoStart,
            closeToTray = CloseToTray,
            showMainWindowOnStartup = ShowMainWindowOnStartup,
            defaultSpeakerDid = DefaultSpeakerDid,
            desktopAcDid = DesktopAcDid,
            cameraDid = CameraDid,
            cardShadow = CardShadow,
            useGpu = UseGpu,
            doubleClickDesktopToggles = DoubleClickDesktopToggles,
            desktopToggleHotkey = DesktopToggleHotkey,
            hoverExpand = HoverExpandEnabled,
            hideSwitches = HideSwitches,
            homeId = HomeId,
            hiddenDids = HiddenDids,
            roomOrder = RoomOrder,
            go2rtcBase = Go2rtcBase,
            cameraMuteOnOpen = CameraMuteOnOpen,
            hubIps = _hubIps,
            cardMetrics = _cardMetrics,
            trendSources = _trendSources,
            trendCards = _trendCards,
            trendCardOrder = _trendCardOrder,
            dataBoardHidden = DataBoardHidden,
            dataBoardRefreshMinutes = DataBoardRefreshMinutes,
            trendPreviewNoticeSeen = TrendPreviewNoticeSeen,
            lightRanges = _lightRanges
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
