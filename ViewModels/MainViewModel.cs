// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Models;
using MiHomeHub.Services;
using MiHomeHub.Services.Telemetry;
using MiHomeHub.Services.Update;
using QRCoder;

namespace MiHomeHub.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly MijiaAuth _auth;
    private MijiaClient? _client;
    private List<MijiaDevice> _allDevices = new();

    /// <summary>实际上屏的设备清单：并进灯组的单灯、并回父卡的多键子设备、被设置隐藏的开关都不在这里。
    /// 控制 / 场景 / 快捷键仍走 <see cref="_allDevices"/>，这里只决定列表与统计看得到什么。</summary>
    private List<MijiaDevice> _visibleDevices = new();

    private string _selectedRoom = "全部";

    /// <summary>用户单独隐藏的设备 did（app.json 的 hiddenDids）。只影响主列表：卡片折到末尾的「已隐藏」区，
    /// 设备本身仍可控制、仍进场景与统计。</summary>
    private readonly HashSet<string> _hiddenDids = new(AppSettingsStore.HiddenDids, StringComparer.Ordinal);

    /// <summary>设备名排序器：zh-CN 文化下汉字按拼音序，忽略大小写。</summary>
    private static readonly StringComparer DeviceNameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);

    /// <summary>字母开头（A–Z/a–z）的名称排最前，其余（汉字、数字等）靠后；各组内仍按拼音序。</summary>
    private static IOrderedEnumerable<MijiaDevice> OrderDevices(IEnumerable<MijiaDevice> src) =>
        src.OrderBy(d => StartsLatin(d.Name) ? 0 : 1)
           .ThenBy(d => d.Name, DeviceNameComparer);

    private static bool StartsLatin(string? name) =>
        !string.IsNullOrEmpty(name) && char.IsAsciiLetter(name[0]);

    /// <summary>抽屉里音箱音量滑杆的防抖提交定时器。</summary>
    private IDisposable? _speakerVolumeTimer;

    /// <summary>轮询回填进行中：此时属性变化来自服务端，不应触发反向提交。</summary>
    private bool _applyingStates;

    /// <summary>搜索防抖：连续输入只在静默 250ms 后重滤一次，避免每键全量 Clear+Add。</summary>
    private IDisposable? _searchDebounce;

    /// <summary>状态轮询：10 秒一拍，每 4 拍顺带刷新温湿度读数。</summary>
    private DispatcherTimer? _pollTimer;
    private bool _polling;
    /// <summary>当前处于后台降频轮询（主窗口收起、桌面组件仍在桌面上）。</summary>
    private bool _backgroundPolling;
    private int _pollTick;
    private long _lastPollErrorLogAt;
    /// <summary>最近一次「悬停分区」的即时刷新时刻（TickCount64），用于 1 秒去重。</summary>
    private long _lastHoverRefreshAt;
    /// <summary>快节拍窗口的截止时刻（TickCount64）；0 表示不在快节拍里。</summary>
    private long _fastPollUntil;
    private const int PollSeconds = 10;
    /// <summary>主窗口收起、但桌面上还开着组件分区时的降频轮询间隔。</summary>
    private const int BackgroundPollSeconds = 30;
    /// <summary>刚下发播放/暂停后，把音箱状态快速拉齐的短节拍。</summary>
    private const int FastPollSeconds = 2;
    /// <summary>快节拍持续时长：云端 playing-state 有延迟，10 秒内每 2 秒读一次足够收敛。</summary>
    private const long FastPollWindowMs = 10_000;
    private const int SuppressMs = 5000;

    /// <summary>设备列表代次：切家庭/重载时递增，过期的规格解析结果直接丢弃。</summary>
    private int _loadGeneration;

    [ObservableProperty] private string _statusText = "未登录";
    /// <summary>标题栏展示的当前家庭名，选中项变化时同步。</summary>
    [ObservableProperty] private string _homeName = "我的家";
    /// <summary>当前选中的家庭；下拉切换时改这里并重拉设备。</summary>
    [ObservableProperty] private HomeOption? _selectedHome;
    [ObservableProperty] private bool _canSwitchHome;
    /// <summary>标题栏家庭芯片的提示文案：单家庭只读，多家庭提示可切换。</summary>
    public string HomeChipTip => CanSwitchHome ? "切换家庭，刷新该家庭下的设备" : "当前家庭";
    [ObservableProperty] private Bitmap? _qrImage;
    [ObservableProperty] private string _qrHint = "点击下方按钮获取二维码，用米家 App 扫码登录";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private bool _isDark = !ThemeStore.IsLight;
    [ObservableProperty] private bool _iconGlass = IconStyleStore.Glass;
    [ObservableProperty] private bool _autoStart = StartupRegistration.IsEnabled();

    /// <summary>开机启动没开上的原因（成功时为空），显示在设置页的「开机启动」下面。</summary>
    [ObservableProperty] private string _autoStartErrorText = StartupRegistration.LastError ?? "";
    [ObservableProperty] private bool _showMainWindowOnStartup = AppSettingsStore.ShowMainWindowOnStartup;
    [ObservableProperty] private bool _closeToTray = AppSettingsStore.CloseToTray;
    /// <summary>true = 卡片带投影；false = 扁平无投影。</summary>
    [ObservableProperty] private bool _cardShadow = AppSettingsStore.CardShadow;

    /// <summary>硬件加速渲染开关：只写偏好，下次启动才真正换渲染管线。</summary>
    [ObservableProperty] private bool _useGpu = AppSettingsStore.UseGpu;

    /// <summary>切换渲染管线必须重启，开关落盘前先弹这层确认。</summary>
    [ObservableProperty] private bool _isGpuRestartConfirmOpen;

    /// <summary>确认弹窗正文：按开 / 关分别说明。</summary>
    [ObservableProperty] private string _gpuRestartMessage = "";

    /// <summary>程序内部回拨开关时置位，避免再次弹确认。</summary>
    private bool _gpuToggleSuppressed;

    /// <summary>弹窗确认前暂存的目标值；取消时据此把开关拨回原状。</summary>
    private bool _pendingGpuValue;

    /// <summary>摄像头画面桥（go2rtc）地址，改动即写回 app.json。</summary>
    /// <summary>打开实时画面时是否默认静音（默认静音）。</summary>
    [ObservableProperty] private bool _cameraMuteOnOpen = AppSettingsStore.CameraMuteOnOpen;

    [ObservableProperty] private string _go2rtcBase = AppSettingsStore.Go2rtcBase;

    /// <summary>打开摄像头抽屉时探测到的桥接状态，显示在「监控画面」卡片里。</summary>
    [ObservableProperty] private string _cameraBridgeHint = "";

    /// <summary>画面源不在本机时的安全提示；空字符串表示整行收起。</summary>
    [ObservableProperty] private string _cameraSecurityHint = "";

    /// <summary>「监控画面」里的设置引导默认收起：普通用户只看「打开画面」，配置一次后再也不用展开。</summary>
    [ObservableProperty] private bool _isCameraSetupExpanded;

    partial void OnGo2rtcBaseChanged(string value) => AppSettingsStore.SetGo2rtcBase(value);
    partial void OnCameraMuteOnOpenChanged(bool value) => AppSettingsStore.SetCameraMuteOnOpen(value);

    // ===== 中枢网关「自动化极客版」：端口固定 80（实时通道 8082），只有 IP 需要确定 =====

    /// <summary>极客版网页用的网关 IP；改动即按 did 写回 app.json，下次打开不用重填。</summary>
    [ObservableProperty] private string _hubGeekIp = "";

    /// <summary>极客版卡片的结果/提示文字（打开成功、扫描结果、登录码说明）。</summary>
    [ObservableProperty] private string _hubGeekHint = "";

    /// <summary>「登录码在哪看」三步引导默认收起，普通用户点一下才展开。</summary>
    [ObservableProperty] private bool _isHubGeekHelpExpanded;

    /// <summary>可点击的极客版地址：http://IP（80 端口浏览器默认，不显示端口更干净）。</summary>
    public string HubGeekAddress => HubGeek.BuildUrl(HubGeekIp);

    partial void OnHubGeekIpChanged(string value)
    {
        OnPropertyChanged(nameof(HubGeekAddress));
        if (DrawerDevice is { IsHub: true } d) AppSettingsStore.SetHubIp(d.Did, value);
    }

    // ── 更新（「关于」页）：检查 GitHub Release → 下载安装包 → 静默安装 ──
    [ObservableProperty] private string _updateStatusText = "检查更新可获取最新版本";
    [ObservableProperty] private string _updateNotes = "";
    /// <summary>测试通道提示：稳定通道下为空、整行收起，普通用户看不到这行。</summary>
    [ObservableProperty] private string _updateChannelHint = UpdateService.IsTestChannel
        ? "测试通道 · 只接收内部测试版本，验证完请删除 channel.txt 回到正式通道"
        : "";
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isUpdateBusy;
    [ObservableProperty] private double _updateProgress;
    /// <summary>更新说明是否已展开（说明长了折叠显示，避免把卡片撑成一篇长文）。</summary>
    [ObservableProperty] private bool _isUpdateNotesExpanded;
    /// <summary>说明超过折叠行数：只有这种才给「展开全部」按钮。</summary>
    [ObservableProperty] private bool _hasLongUpdateNotes;
    /// <summary>短说明直接整段显示（没有折叠按钮）。</summary>
    [ObservableProperty] private bool _hasShortUpdateNotes;
    /// <summary>长说明处于折叠态（显示截断摘要）。</summary>
    [ObservableProperty] private bool _showUpdateNotesCollapsed;
    /// <summary>折叠按钮文案。</summary>
    [ObservableProperty] private string _updateNotesToggleText = "查看完整说明";
    /// <summary>强制更新时弹出不可跳过的更新框。</summary>
    [ObservableProperty] private bool _isForceUpdateOpen;

    // ── 标题栏更新提示条：检测到新版本时出现，可「查看详情」跳设置-关于，也能 X 掉（本会话不再出现） ──
    [ObservableProperty] private bool _isUpdateBannerVisible;
    [ObservableProperty] private string _updateBannerText = "";
    /// <summary>「查看详情」打开的更新详情弹窗（普通模态：可以稍后再装）。</summary>
    [ObservableProperty] private bool _isUpdateDetailsOpen;
    [ObservableProperty] private string _updateDialogTitle = "";
    /// <summary>网盘兜底页（后台 disk_url / 清单 disk_url）；有值才显示「网盘下载」入口。</summary>
    [ObservableProperty] private string _updateDiskUrl = "";
    [ObservableProperty] private bool _hasUpdateDiskUrl;
    /// <summary>用户点过 X：本次运行不再自动弹回，重新检查出新版本也不打扰。</summary>
    private bool _updateBannerDismissed;

    /// <summary>卡片里折叠显示的说明（超过 <see cref="UpdateNotesPreviewLimit"/> 字就截断）。</summary>
    private const int UpdateNotesPreviewLimit = 180;
    [ObservableProperty] private string _updateNotesPreview = "";

    /// <summary>已发现的新版本清单（点「立即更新」时用）。</summary>
    private UpdateManifest? _pendingUpdate;

    // ── 匿名使用统计（可选，默认关闭；只报事件，不报账号与设备名） ──
    [ObservableProperty] private bool _telemetryEnabled = TelemetryService.IsEnabled;
    // 状态与「不含隐私信息」并成一行，设置页只占两行（标题 + 这一行）
    [ObservableProperty] private string _telemetryHint = TelemetryService.StatusText + TelemetryPrivacySuffix;

    private const string TelemetryPrivacySuffix = " · 不含隐私信息";

    partial void OnTelemetryEnabledChanged(bool value)
    {
        TelemetryService.SetEnabled(value);
        TelemetryHint = TelemetryService.StatusText + TelemetryPrivacySuffix;
        Log(value ? "已开启匿名使用统计" : "已关闭匿名使用统计");
    }

    /// <summary>由 App 注入：真正退出进程（安装器需要先关掉自己才能替换 app 目录）。</summary>
    public Action? RequestExit { get; set; }

    /// <summary>由 App 注入：弹一条非模态托盘气泡（启动时发现新版本用）。</summary>
    public Action<string, string>? NotifyUser { get; set; }

    /// <summary>一次会话只弹一次气泡，避免自动检查重复刷新时反复打扰。</summary>
    private bool _startupUpdateNotified;

    /// <summary>双击桌面空白处显示 / 隐藏所有桌面分区卡片。</summary>
    [ObservableProperty] private bool _doubleClickDesktopToggles = AppSettingsStore.DoubleClickDesktopToggles;

    /// <summary>
    /// 桌面分区卡片「显示 / 隐藏」的全局快捷键组合。
    /// macOS 上用 Carbon 注册（免辅助功能权限）；Windows 上保留双击桌面的方式。
    /// </summary>
    [ObservableProperty] private string _desktopToggleHotkey = AppSettingsStore.DesktopToggleHotkey;

    /// <summary>设置页里可选的快捷键组合（macOS 用 Carbon 注册，只需要系统级全局热键，不需要辅助功能权限）。</summary>
    public IReadOnlyList<string> DesktopToggleHotkeyOptions { get; } = new[]
    {
        "Alt+Cmd+D",
        "Alt+Cmd+H",
        "Ctrl+Alt+D",
        "Ctrl+Alt+H",
        "Alt+Cmd+1",
        "Alt+Cmd+2",
        "Ctrl+Shift+F12"
    };

    /// <summary>是否 macOS（桌面双击要辅助功能权限，macOS 上换成全局快捷键）。</summary>
    public bool IsMacOsPlatform => OperatingSystem.IsMacOS();

    /// <summary>鼠标移入折叠的桌面分区时自动展开。</summary>
    [ObservableProperty] private bool _hoverExpand = AppSettingsStore.HoverExpandEnabled;

    /// <summary>设备列表隐藏墙壁开关 / 场景按键类设备（多键键位并回所属开关，不再单独出卡）。</summary>
    [ObservableProperty] private bool _hideSwitches = AppSettingsStore.HideSwitches;

    partial void OnHideSwitchesChanged(bool value)
    {
        AppSettingsStore.SetHideSwitches(value);
        RebuildVisibleDevices();
        RebuildRooms();
        ApplyFilter();
        RebuildHomeGroups();
        UpdateStats();
        RefreshSpeakers();
        Log(value ? "已隐藏开关类设备" : "已显示开关类设备");
    }

    /// <summary>设置弹窗当前 Tab：0 通用 / 1 桌面组件 / 2 账户 / 3 关于。</summary>
    [ObservableProperty] private int _settingsTab;

    /// <summary>写注册表失败回滚开关时置位，避免回滚本身又触发一次写入。</summary>
    private bool _revertingAutoStart;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _onlineCount;

    /// <summary>
    /// 「总设备」卡：真实设备数。在线卡的 TotalCount 走的是上屏清单，灯组只算一台；
    /// 这里把灯组按组内单灯逐台计数，组本身和拆键虚拟行都不算设备。
    /// </summary>
    [ObservableProperty] private int _totalDeviceCount;

    [ObservableProperty] private int _lightOnCount;
    [ObservableProperty] private string _lightTotalText = "";
    [ObservableProperty] private int _curtainOnCount;
    [ObservableProperty] private string _curtainTotalText = "";
    [ObservableProperty] private int _acOnCount;
    [ObservableProperty] private string _acTotalText = "";
    [ObservableProperty] private int _offlineCount;
    [ObservableProperty] private bool _hasOffline;

    /// <summary>
    /// 统计卡弹窗的口径：设备类型卡各自列本类型设备，在线卡列掉线设备。
    /// 四张类型卡共用同一个弹窗视图，只有标题、量词和过滤条件不同。
    /// </summary>
    public enum StatListKind
    {
        Lights,
        Curtains,
        Ac,
        Offline,
    }

    private StatListKind _statListKind = StatListKind.Lights;

    /// <summary>统计卡弹窗当前列出的设备（按 <see cref="_statListKind"/> 过滤，弹窗开着时随轮询同步）。</summary>
    public ObservableCollection<MijiaDevice> StatDevices { get; } = new();

    [ObservableProperty] private bool _hasStatDevices;
    [ObservableProperty] private bool _isStatListOpen;
    [ObservableProperty] private string _statListTitle = "";

    /// <summary>清单为空时置 null：DialogPopup 的副标题按非 null 判定显示，空串会留一条空白行。</summary>
    [ObservableProperty] private string? _statListSubtitle;

    /// <summary>清单为空时的正文提示（空列表只剩一行副标题会像没加载出来）。</summary>
    [ObservableProperty] private string _statListEmptyText = "";

    /// <summary>弹窗标题栏「全关」是否可点：只在类型卡且有开着的在线设备时出现（离线卡没有可下发的指令）。</summary>
    [ObservableProperty] private bool _canCloseAllStatDevices;

    /// <summary>「全关」悬停提示：把量词和动词按类型说清楚，窗帘那栏写「合上」而不是「关掉」。</summary>
    [ObservableProperty] private string _closeAllStatTip = "";
    [ObservableProperty] private int _roomCount;
    [ObservableProperty] private int _sceneCount;
    [ObservableProperty] private bool _hasDevices;
    [ObservableProperty] private bool _hasScenes;
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _offlineText = "";

    [ObservableProperty] private string _greet = "";
    [ObservableProperty] private string _clockText = "--:--";
    [ObservableProperty] private string _dateText = "";

    [ObservableProperty] private string _bannerTitle = "全屋设备";
    [ObservableProperty] private string _bannerDesc = "统一管理家中所有智能设备";
    [ObservableProperty] private string _bannerIconKey = "house";

    /// <summary>当前房间的自定义图标；null 时横幅继续用 <see cref="BannerIconKey"/> 的默认画法。</summary>
    [ObservableProperty] private CustomIcon? _bannerCustomIcon;

    public bool BannerHasCustomIcon => BannerCustomIcon is not null;

    /// <summary>默认横幅图标的不透明度：有自定义图标时隐到 0。</summary>
    public double BannerDefaultIconOpacity => BannerCustomIcon is null ? 1 : 0;

    partial void OnBannerCustomIconChanged(CustomIcon? value)
    {
        OnPropertyChanged(nameof(BannerHasCustomIcon));
        OnPropertyChanged(nameof(BannerDefaultIconOpacity));
    }

    [ObservableProperty] private int _bannerTotal;
    [ObservableProperty] private int _bannerOn;
    [ObservableProperty] private int _bannerOffline;

    /// <summary>当前房间页（含「全部」）里还亮着的灯数；房间横幅上的数量与「一键全关」入口据此显示。</summary>
    [ObservableProperty] private int _bannerLightOn;

    public bool HasBannerLightOn => BannerLightOn > 0;

    public string BannerLightText => $"{BannerLightOn} 盏灯亮着";

    partial void OnBannerLightOnChanged(int value)
    {
        OnPropertyChanged(nameof(HasBannerLightOn));
        OnPropertyChanged(nameof(BannerLightText));
    }

    // ===== 环境概览：温湿度聚合 + 耗材提醒 =====

    /// <summary>概览明细里的逐设备读数（温度/湿度）。</summary>
    public ObservableCollection<EnvReadingItem> EnvReadings { get; } = new();

    /// <summary>全屋耗材项（滤芯/刷头 life-level），不足时排前面。</summary>
    public ObservableCollection<ConsumableItem> ConsumableItems { get; } = new();

    [ObservableProperty] private string _envTemperatureText = "—";
    [ObservableProperty] private string _envHumidityText = "—";
    /// <summary>明细大读数：数值与单位分开，单位用小字，避免「21℃」整串压在一起。</summary>
    [ObservableProperty] private string _envTemperatureValue = "—";
    [ObservableProperty] private string _envTemperatureUnit = "";
    [ObservableProperty] private string _envHumidityValue = "—";
    [ObservableProperty] private string _envHumidityUnit = "";
    /// <summary>明细里「逐设备读数」的右上角数量提示。</summary>
    [ObservableProperty] private string _envReadingsCountText = "";
    /// <summary>聚合读数的来源设备名，明细里提示「数据来自 …」。</summary>
    [ObservableProperty] private string _envSourceText = "";
    [ObservableProperty] private bool _hasEnvironment;
    [ObservableProperty] private bool _hasConsumables;
    [ObservableProperty] private bool _hasConsumableWarning;
    [ObservableProperty] private int _consumableLowCount;
    [ObservableProperty] private bool _isEnvDetailOpen;

    public string ConsumablePillText => HasConsumableWarning ? $"耗材不足 {ConsumableLowCount}" : "耗材正常";
    /// <summary>明细耗材卡右上角的状态语：正常说「全部正常」，不足时说「N 项余量偏低」。</summary>
    public string ConsumableStatusText => HasConsumableWarning ? $"{ConsumableLowCount} 项余量偏低" : "全部正常";
    public bool ShowEnvOverview => HasEnvironment || HasConsumables;

    partial void OnConsumableLowCountChanged(int value)
    {
        OnPropertyChanged(nameof(ConsumablePillText));
        OnPropertyChanged(nameof(ConsumableStatusText));
    }
    partial void OnHasEnvironmentChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEnvOverview));
        OnPropertyChanged(nameof(ShowEnvSkeleton));
        OnPropertyChanged(nameof(ShowTrendSkeleton));
    }
    partial void OnHasConsumablesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEnvOverview));
        OnPropertyChanged(nameof(ShowEnvSkeleton));
    }

    partial void OnHasConsumableWarningChanged(bool value)
    {
        OnPropertyChanged(nameof(ConsumablePillText));
        OnPropertyChanged(nameof(ConsumableStatusText));
        OnPropertyChanged(nameof(ShowEnvOverview));
        OnPropertyChanged(nameof(ShowEnvSkeleton));
    }

    // ===== 小爱音箱：默认音箱与语音指令 =====

    public ObservableCollection<RoomChip> Rooms { get; } = new();

    /// <summary>侧栏房间的自定义顺序（不含「全部」）：拖拽时即时更新，松手才写盘。</summary>
    private string[] _roomOrder = AppSettingsStore.RoomOrder;
    public ObservableCollection<HomeOption> HomeOptions { get; } = new();
    public ObservableCollection<MijiaDevice> Devices { get; } = new();

    /// <summary>用户单独隐藏的设备：不占房间宫格，统一折在列表最后的「已隐藏」区。</summary>
    public ObservableCollection<MijiaDevice> HiddenDevices { get; } = new();

    /// <summary>是否还有被隐藏的设备（没有时整块区域不出现）。</summary>
    [ObservableProperty] private bool _hasHiddenDevices;

    /// <summary>「已隐藏」区是否展开；默认收起，隐藏后仍能在末尾一键找回。</summary>
    [ObservableProperty] private bool _hiddenSectionExpanded;

    /// <summary>「已隐藏」标题：带上台数，收起时也能看出藏了几台。</summary>
    [ObservableProperty] private string _hiddenSectionText = "";

    /// <summary>折叠箭头的旋转角：收起时指向右（-90），展开时朝下（0）。</summary>
    public double HiddenSectionAngle => HiddenSectionExpanded ? 0 : -90;

    partial void OnHiddenSectionExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(HiddenSectionAngle));

    public ObservableCollection<DeviceGroup> HomeGroups { get; } = new();

    /// <summary>用户手动收起的房间名：刷新设备重建分组时沿用，已收起的房间不会自己弹开。</summary>
    private readonly HashSet<string> _collapsedRooms = new(StringComparer.Ordinal);

    /// <summary>复用的临时集合：当前还有灯开着的房间名，供侧栏房间灯泡逐行比对（避免每拍新建 HashSet）。</summary>
    private readonly HashSet<string> _litRooms = new(StringComparer.Ordinal);

    /// <summary>复用的临时计数：房间名 → 开着的灯数，首页房间标题与「一键全关」按它显示。</summary>
    private readonly Dictionary<string, int> _litLightCounts = new(StringComparer.Ordinal);

    /// <summary>点首页房间标题行：展开 ⇄ 收起该房间的设备卡片。</summary>
    [RelayCommand]
    private void ToggleRoom(DeviceGroup? group)
    {
        if (group is null) return;
        group.IsExpanded = !group.IsExpanded;
        if (group.IsExpanded) _collapsedRooms.Remove(group.Name);
        else _collapsedRooms.Add(group.Name);
    }
    public ObservableCollection<SceneCardItem> SceneItems { get; } = new();

    /// <summary>自动化（如果…就…）：和手动场景同一个接口返回，客户端按 IsAutomation 归类。</summary>
    public ObservableCollection<SceneCardItem> AutomationItems { get; } = new();

    /// <summary>
    /// 智能页展示用的场景集合：在完整列表上套标题栏搜索词（同实例，只筛不重建）。
    /// 首页快捷场景、桌面分区、选择器仍读完整列表，搜索不该影响它们。
    /// </summary>
    public ObservableCollection<SceneCardItem> SceneViewItems { get; } = new();

    /// <summary>智能页展示用的自动化集合，规则同 <see cref="SceneViewItems"/>。</summary>
    public ObservableCollection<SceneCardItem> AutomationViewItems { get; } = new();

    /// <summary>设备详情「智能场景」里的手动场景：会控制本设备的场景。</summary>
    public ObservableCollection<SceneCardItem> DrawerScenes { get; } = new();

    /// <summary>设备详情「智能场景」里的自动化：被本设备触发 / 会控制本设备的自动化。</summary>
    public ObservableCollection<SceneCardItem> DrawerAutomations { get; } = new();

    /// <summary>场景页分段：0 场景 / 1 自动化。</summary>
    [ObservableProperty] private int _sceneTab;

    /// <summary>设备 → 场景 反查表，随场景列表重建，抽屉打开时零网络开销。</summary>
    private readonly SceneIndexService _sceneIndex = new();

    /// <summary>最近一次拉到的场景全集（手动 + 自动化）。</summary>
    private List<MijiaScene> _scenes = new();

    /// <summary>场景索引是否已经拉过一次：桌面弹窗在没进过智能页时按需补拉。</summary>
    private bool _scenesLoadedOnce;

    public bool HasAutomations => AutomationItems.Count > 0;

    /// <summary>「智能场景」是否有关联项：场景与自动化任一命中。</summary>
    public bool HasDrawerScenes => DrawerScenes.Count + DrawerAutomations.Count > 0;
    public bool HasDrawerSceneGroup => DrawerScenes.Count > 0;
    public bool HasDrawerAutomationGroup => DrawerAutomations.Count > 0;

    /// <summary>标题右侧的构成概览：「2 个场景 · 3 个自动化」。</summary>
    public string DrawerSceneSummary =>
        $"{DrawerScenes.Count} 个场景 · {DrawerAutomations.Count} 个自动化";

    /// <summary>自动化分段空态：加载结束且一条都没有。</summary>
    public bool ShowAutomationEmpty => !SyncPending && !HasAutomations;

    /// <summary>分段标题上的计数。</summary>
    public string SceneCountText => $"{SceneItems.Count} 个场景";
    public string AutomationCountText => $"{AutomationItems.Count} 个自动化";

    /// <summary>切换场景页分段（参数来自 XAML，字符串化后解析）。</summary>
    [RelayCommand]
    private void SelectSceneTab(string? tab)
    {
        if (int.TryParse(tab, out var index)) SceneTab = index;
    }

    // 分段选中态：用 bool 直给 XAML，省掉 IntEq 转换器的资源引用
    public bool IsSceneTabManual => SceneTab == 0;
    public bool IsSceneTabAutomation => SceneTab == 1;

    partial void OnSceneTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsSceneTabManual));
        OnPropertyChanged(nameof(IsSceneTabAutomation));
    }

    /// <summary>
    /// 开屏这一轮同步是否仍在进行。<see cref="IsLoading"/> 只覆盖设备+场景+首拍开关状态，
    /// 温湿度/耗材要等后台规格解析完再读一次，所以等待期用这个标志兜住，否则那段只有空白没有骨架。
    /// </summary>
    public bool IsInitialSync { get; private set; }

    /// <summary>开屏/切家庭这一轮开始：骨架等待期开启（含后台规格解析）。</summary>
    private void BeginInitialSync()
    {
        if (IsInitialSync) return;
        IsInitialSync = true;
        RaiseSyncState();
    }

    /// <summary>开屏同步收尾：置位后通知所有骨架派生态（只有 UI 线程会调用）。</summary>
    private void FinishInitialSync()
    {
        if (!IsInitialSync) return;
        IsInitialSync = false;
        RaiseSyncState();
    }

    private void RaiseSyncState()
    {
        OnPropertyChanged(nameof(SyncPending));
        OnPropertyChanged(nameof(TrayMediaSlot));
        OnPropertyChanged(nameof(TraySpeakerPending));
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowSceneSkeleton));
        OnPropertyChanged(nameof(ShowQuickScenes));
        OnPropertyChanged(nameof(ShowEnvSkeleton));
        OnPropertyChanged(nameof(ShowTrendSkeleton));
        RaiseTrendRowFlags();
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ShowHomeEmpty));
        OnPropertyChanged(nameof(ShowSceneEmpty));
        OnPropertyChanged(nameof(ShowSceneNoMatch));
        OnPropertyChanged(nameof(ShowAutomationNoMatch));
    }

    /// <summary>开屏同步仍在等待数据（含后台的规格解析与温湿度/耗材读数）。</summary>
    public bool SyncPending => IsLoading || IsInitialSync;

    /// <summary>首次拉取期间的骨架格数量，只用来给 ItemsControl 供位。</summary>
    /// <remarks>槽位必须是非 null 项：Avalonia 的 ItemsControl 对 null 项不套 DataTemplate，骨架会整片空白。</remarks>
    public IReadOnlyList<string> SkeletonSlots { get; } =
        Enumerable.Range(0, 10).Select(i => i.ToString()).ToArray();

    /// <summary>概览统计卡的骨架槽位：真实统计卡固定五张（在线/总设备/灯光/窗帘/空调），占位数量与之一致。</summary>
    public IReadOnlyList<string> StatSkeletonSlots { get; } =
        Enumerable.Range(0, 5).Select(i => i.ToString()).ToArray();

    /// <summary>环境胶囊的骨架槽位：温度/湿度/耗材三枚。</summary>
    public IReadOnlyList<string> EnvSkeletonSlots { get; } =
        Enumerable.Range(0, 3).Select(i => i.ToString()).ToArray();

    /// <summary>环境趋势卡的骨架槽位：温度/湿度/用电三张，与真卡一一对应。</summary>
    public IReadOnlyList<string> TrendSkeletonSlots { get; } =
        Enumerable.Range(0, 3).Select(i => i.ToString()).ToArray();

    /// <summary>列表还空着时铺骨架；已有设备只是刷新状态，不动内容。</summary>
    public bool ShowSkeleton => SyncPending && TotalCount == 0;

    /// <summary>还在首拉且环境读数没到货时铺胶囊占位；读数到了立刻换真胶囊。</summary>
    public bool ShowEnvSkeleton => SyncPending && !ShowEnvOverview;

    /// <summary>还在首拉且趋势区还没成形时，先铺图表骨架卡；任何一类数据到货就整块换成真卡。</summary>
    public bool ShowTrendSkeleton => SyncPending && DataBoardVisible && !ShowEnvTrend;

    /// <summary>场景首次拉取期间铺骨架；已有场景只是刷新时不动内容。</summary>
    public bool ShowSceneSkeleton => SyncPending && !HasScenes;

    /// <summary>首页快捷场景区是否出现：有场景，或场景还在首次拉取中。</summary>
    public bool ShowQuickScenes => HasScenes || ShowSceneSkeleton;

    /// <summary>空房间提示要等加载结束再判定，否则骨架期间就冒出来。</summary>
    public bool ShowEmptyHint => !SyncPending && !HasDevices;

    /// <summary>首页空态：加载结束、既没有设备也没有场景时，整块换成空提示。</summary>
    public bool ShowHomeEmpty => !SyncPending && TotalCount == 0 && !HasScenes && !HasAutomations;

    /// <summary>场景页空态：加载结束后既没有手动场景也没有自动化。</summary>
    public bool ShowSceneEmpty => !SyncPending && !HasScenes && !HasAutomations;

    /// <summary>智能页筛选空态：确实有条目，但当前关键词一条也没命中（跟「一条都没有」分开说）。</summary>
    public bool ShowSceneNoMatch => !SyncPending && SceneViewItems.Count == 0 && SceneItems.Count > 0;
    public bool ShowAutomationNoMatch => !SyncPending && AutomationViewItems.Count == 0 && AutomationItems.Count > 0;

    /// <summary>标题栏搜索框水印：明说这一页搜的是什么，搜索跟着当前页走。</summary>
    public string SearchWatermark => SelectedNavIndex switch
    {
        2 => "搜索场景、自动化…",
        3 => "搜索已绑定的快捷键…",
        _ => "搜索设备、房间…",
    };

    /// <summary>快捷键页没有标题栏可筛的列表，搜索胶囊整体收起。</summary>
    public bool ShowSearchBox => SelectedNavIndex != 3;

    /// <summary>搜索框里有内容：标题栏右端显示一键清空钮。</summary>
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    /// <summary>设置里的两个下拉选项：Value 直接对上 IsDark / IconGlass。</summary>
    public IReadOnlyList<OptionItem> ThemeOptions { get; } =
    [
        new() { Label = "深色主题", Value = true },
        new() { Label = "浅色主题", Value = false }
    ];

    public IReadOnlyList<OptionItem> IconOptions { get; } =
    [
        new() { Label = "多色图标", Value = true },
        new() { Label = "细线描边", Value = false }
    ];

    public MainViewModel()
    {
        _auth = MijiaAuth.LoadOrEmpty();
        CanQuickLogin = _auth.CanQuickLogin();
        if (_auth.HasValidAuth())
        {
            IsLoggedIn = true;
            StatusText = "已登录";
            _ = InitClientAsync();
        }
        else if (CanQuickLogin)
        {
            // 存过 passToken：启动直接静默换新 serviceToken，不必再点「一键登录」
            _ = AutoLoginAsync();
        }
        else
        {
            IsLoginOpen = true;
            QrHint = "点击下方按钮获取二维码，用米家 App 扫码登录";
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => TickClock();
        timer.Start();
        TickClock();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(PollSeconds) };
        _pollTimer.Tick += (_, _) => _ = PollTickAsync();
        _pollTimer.Start();

        // 全局快捷键：装载 hotkeys.json、安装键盘钩子（窗口收起进托盘也常驻）
        InitHotkeys();

        // 房间 / 场景自定义图标换过：只刷新对应那一枚，不重建整个列表
        CustomIconStore.Changed += OnCustomIconChanged;

        // 启动后无感检查更新：24 小时内查过就跳过联网；缓存里的「有更新」由
        // NotifyCachedUpdate 在托盘就绪后补提示（构造函数里 NotifyUser 还没注入）。
        if (UpdateService.ShouldAutoCheck()) _ = AutoCheckUpdateAsync();
    }

    /// <summary>
    /// 状态轮询降级（窗口最小化/收托盘时调用）。
    /// 桌面上还开着组件分区时不能停——否则组件上的开关/读数会定格；这种情形改为降频到
    /// <see cref="BackgroundPollSeconds"/> 秒继续刷。桌面上没有组件才真正停掉，减少后台网络与分配。
    /// </summary>
    public void PausePolling()
    {
        if (_pollTimer is null) return;
        _fastPollUntil = 0;
        if (DesktopHost.Enabled && DesktopHost.GroupCount > 0)
        {
            _pollTimer.Interval = TimeSpan.FromSeconds(BackgroundPollSeconds);
            if (!_pollTimer.IsEnabled) _pollTimer.Start();
            if (!_backgroundPolling) Log("主窗口已收起，桌面组件仍在桌面上：状态轮询降频到 30 秒");
            _backgroundPolling = true;
            return;
        }
        _backgroundPolling = false;
        _pollTimer.Stop();
    }

    /// <summary>硬停轮询（退出登录等场景）：不看桌面组件，直接停。</summary>
    public void StopPolling()
    {
        _backgroundPolling = false;
        _fastPollUntil = 0;
        _pollTimer?.Stop();
    }

    /// <summary>
    /// 桌面分区增减后回调：后台降频正在跑，但桌面上已经一块分区都不剩时，
    /// 没必要继续每 30 秒唤醒一次，直接停掉。
    /// </summary>
    public void RecheckBackgroundPolling()
    {
        if (!_backgroundPolling) return;
        if (DesktopHost.Enabled && DesktopHost.GroupCount > 0) return;
        _backgroundPolling = false;
        _pollTimer?.Stop();
    }

    /// <summary>恢复状态轮询（窗口从托盘/最小化恢复时调用）。</summary>
    public void ResumePolling()
    {
        if (_pollTimer is null) return;
        // 后台降频期间可能已改成 30 秒，回前台要还原成正常节奏
        _backgroundPolling = false;
        _fastPollUntil = 0;
        _pollTimer.Interval = TimeSpan.FromSeconds(PollSeconds);
        if (!_pollTimer.IsEnabled) _pollTimer.Start();
        // 后台期间手机端可能改过启停，回到前台立刻对齐一次
        _ = SyncSceneEnableStatesAsync(force: true);
    }

    /// <summary>轮询一拍：在途请求未回时直接跳过，避免网络抖动时请求叠加。</summary>
    private async Task PollTickAsync()
    {
        if (_client == null || !IsLoggedIn || _polling) return;
        _polling = true;
        try
        {
            _pollTick++;
            await RefreshStatesAsync(includeMetrics: _pollTick % 4 == 1);
            // 智能页/抽屉在前台时顺带对齐自动化启停态（最小间隔 30s，内部节流）
            if (IsSceneSurfaceActive) await SyncSceneEnableStatesAsync(force: false);
        }
        finally
        {
            _polling = false;
            RestoreNormalPollIntervalIfNeeded();
        }
    }

    private void RestoreNormalPollIntervalIfNeeded()
    {
        if (_fastPollUntil == 0 || _pollTimer is null) return;
        if (Environment.TickCount64 < _fastPollUntil) return;
        _fastPollUntil = 0;
        // 快节拍结束：回到当前应处的节拍（后台降频中就是 30 秒，否则 10 秒）
        _pollTimer.Interval = TimeSpan.FromSeconds(_backgroundPolling ? BackgroundPollSeconds : PollSeconds);
    }

    /// <summary>
    /// 刚给音箱下发播放/暂停后调用：把轮询切到 2 秒一拍并持续 10 秒，立刻补一拍，
    /// 让 playing-state 一落定界面就跟上，而不是等满一个 10 秒（后台时 30 秒）的节拍。
    /// 该设备没有可读的 playing-state 时不折腾节拍。
    /// </summary>
    public void AccelerateSpeakerStateSync(MijiaDevice? device)
    {
        if (device is not { PlayingStateSiid: > 0 }) return;
        if (_pollTimer is null) return;
        _fastPollUntil = Environment.TickCount64 + FastPollWindowMs;
        if (_pollTimer.Interval > TimeSpan.FromSeconds(FastPollSeconds))
            _pollTimer.Interval = TimeSpan.FromSeconds(FastPollSeconds);
        // 轮询已停（退出登录/无桌面组件的最小化）时由调用方自己触发刷新，这里不擅自启动计时器
        if (_pollTimer.IsEnabled) _ = PollTickAsync();
    }

    /// <summary>
    /// 桌面分区的即时刷新：鼠标移入分区、分区展开时立刻补一拍，不用等最多 30 秒的定时器。
    /// 悬停是高频动作，1 秒内的重复触发直接忽略（指针在子元素间移动会冒泡出多次 PointerEntered）；
    /// 展开是用户刻意动作，不做去重。已有轮询在途时直接跳过，不叠加请求。
    /// </summary>
    public void RequestZoneRefresh(bool includeMetrics)
    {
        if (_client == null || !IsLoggedIn) return;
        var now = Environment.TickCount64;
        if (!includeMetrics && now - _lastHoverRefreshAt < 1000) return;
        _lastHoverRefreshAt = now;
        _ = RefreshOnDemandAsync(includeMetrics);
    }

    private async Task RefreshOnDemandAsync(bool includeMetrics)
    {
        if (_client == null || !IsLoggedIn || _polling) return;
        _polling = true;
        try
        {
            await RefreshStatesAsync(includeMetrics);
        }
        finally
        {
            _polling = false;
        }
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        SyncRoomChips();
        // 水印跟着页走：首页/房间搜设备，智能页搜场景与自动化
        OnPropertyChanged(nameof(SearchWatermark));
        // 快捷键页没有可搜列表，整颗搜索胶囊收起
        OnPropertyChanged(nameof(ShowSearchBox));
        if (value == 3 && !string.IsNullOrEmpty(SearchText)) SearchText = "";
        // 切到智能页：立刻对齐一次启停态，避免看到手机端已改过的旧状态
        if (value == 2) _ = SyncSceneEnableStatesAsync(force: true);
    }

    partial void OnIsDarkChanged(bool value)
    {
        ThemeStore.Set(!value);
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
        // 桌面分区底色跟着主题卡片色走，切主题要重算一次
        DesktopHost.OnThemeChanged();
    }

    partial void OnIconGlassChanged(bool value) => IconStyleStore.Set(value);

    partial void OnCardShadowChanged(bool value)
    {
        AppSettingsStore.SetCardShadow(value);
        CardShadowScope.Apply(value);
    }

    partial void OnUseGpuChanged(bool value)
    {
        // 拨开关只是改了内存里的值；写盘与真正换管线都等确认弹窗，
        // 取消时把开关拨回已保存的偏好（不落盘）。
        if (_gpuToggleSuppressed) return;

        _pendingGpuValue = value;
        GpuRestartMessage = value
            ? "开启后界面改由显卡绘制。这项设置需要下次启动应用才生效，是否现在重启？"
            : "关闭后界面改回软件绘制。这项设置需要下次启动应用才生效，是否现在重启？";
        IsGpuRestartConfirmOpen = true;
    }

    /// <summary>确认弹窗里选了「稍后」：丢弃这次切换，开关回到当前已保存的值。</summary>
    [RelayCommand]
    private void CancelGpuRestart()
    {
        IsGpuRestartConfirmOpen = false;
        _gpuToggleSuppressed = true;
        try { UseGpu = AppSettingsStore.UseGpu; }
        finally { _gpuToggleSuppressed = false; }
    }

    /// <summary>确认弹窗里选了「立即重启」：先落盘，再交给 View 拉起重进程。</summary>
    public void CommitGpuChoice()
    {
        AppSettingsStore.SetUseGpu(_pendingGpuValue);
        _gpuToggleSuppressed = true;
        try { UseGpu = _pendingGpuValue; }
        finally { _gpuToggleSuppressed = false; }
        IsGpuRestartConfirmOpen = false;
        Log(_pendingGpuValue
            ? "已开启硬件加速，重启应用后生效"
            : "已关闭硬件加速，重启应用后生效");
    }

    partial void OnDoubleClickDesktopTogglesChanged(bool value)
    {
        AppSettingsStore.SetDoubleClickDesktopToggles(value);
        Log(value ? "已开启双击桌面隐藏组件" : "已关闭双击桌面隐藏组件");
    }

    partial void OnDesktopToggleHotkeyChanged(string value)
    {
        // 下拉框在当前值不在候选里时会回写空串：这时候保持原样，别把用户的组合抹掉
        if (string.IsNullOrWhiteSpace(value))
        {
            var current = AppSettingsStore.DesktopToggleHotkey;
            if (!string.IsNullOrEmpty(current)) DesktopToggleHotkey = current;
            return;
        }

        AppSettingsStore.SetDesktopToggleHotkey(value);
        if (OperatingSystem.IsMacOS())
        {
            var ok = Services.MacOS.MacOsCarbonHotkey.TryRegister(value);
            Log(ok
                ? $"桌面组件快捷键已设为 {value}"
                : $"快捷键 {value} 注册失败（可能已被其它程序占用）");
        }
    }

    partial void OnHoverExpandChanged(bool value)
    {
        AppSettingsStore.SetHoverExpandEnabled(value);
        Log(value ? "已开启移入自动展开" : "已关闭移入自动展开");
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_revertingAutoStart) return;
        if (StartupRegistration.Apply(value))
        {
            AppSettingsStore.SetAutoStart(value);
            AutoStartErrorText = "";
            Log(value ? "已开启开机启动" : "已关闭开机启动");
            return;
        }

        _revertingAutoStart = true;
        AutoStart = !value;
        _revertingAutoStart = false;
        // 失败原因如实显示出来：以前只有一句「注册表不可写」，macOS 上写不进
        // ~/Library/LaunchAgents 时这句话既不准确，也没告诉用户到底哪儿不对。
        AutoStartErrorText = StartupRegistration.LastError
            ?? (OperatingSystem.IsMacOS()
                ? "开机启动项注册失败"
                : "开机启动设置失败：当前用户注册表不可写");
        Log($"开机启动设置失败：{AutoStartErrorText}");
    }

    partial void OnShowMainWindowOnStartupChanged(bool value)
    {
        AppSettingsStore.SetShowMainWindowOnStartup(value);
        Log(value ? "下次启动后显示主界面" : "下次启动后静默驻留托盘");
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        AppSettingsStore.SetCloseToTray(value);
        Log(value ? "关闭窗口后收进托盘" : "关闭窗口即退出应用");
    }

    partial void OnSearchTextChanged(string value)
    {
        // 清空钮的显隐要跟敲键同步，不能等防抖
        OnPropertyChanged(nameof(HasSearchText));
        // 高频键入只改搜索框，列表重建压到防抖尾部（Zonex：防抖收口）
        _searchDebounce?.Dispose();
        _searchDebounce = DispatcherTimer.RunOnce(() =>
        {
            _searchDebounce = null;
            // 一套搜索管所有页面：设备一列（首页分组 / 房间列表）+ 智能页的场景与自动化
            ApplyFilter();
            RebuildHomeGroups();
            ApplySceneFilter();
        }, TimeSpan.FromMilliseconds(250));
    }

    partial void OnSelectedHomeChanged(HomeOption? value)
    {
        foreach (var o in HomeOptions) o.IsSelected = o.Id == value?.Id;
        if (value == null) return;
        HomeName = value.Name;
        OnPropertyChanged(nameof(HomeChipTip));
        // 首次从无到有只是回填，不重复拉；用户下拉切换时才重载该家庭设备
        if (_suppressHomeReload) return;
        AppSettingsStore.SetHomeId(value.Id);
        _ = LoadDevicesForSelectedHomeAsync();
    }

    partial void OnCanSwitchHomeChanged(bool value) => OnPropertyChanged(nameof(HomeChipTip));

    /// <summary>刷新家庭列表时回填 SelectedHome，避免触发一次多余的设备重拉。</summary>
    private bool _suppressHomeReload;

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowQuickLoginAction));
        OnPropertyChanged(nameof(ShowQrLoginAction));
    }

    partial void OnCanQuickLoginChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowQuickLoginAction));
        OnPropertyChanged(nameof(ShowQrLoginAction));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(SyncPending));
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ShowHomeEmpty));
        OnPropertyChanged(nameof(ShowSceneEmpty));
        OnPropertyChanged(nameof(ShowSceneSkeleton));
        OnPropertyChanged(nameof(ShowQuickScenes));
        OnPropertyChanged(nameof(ShowEnvSkeleton));
        OnPropertyChanged(nameof(ShowTrendSkeleton));
    }

    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowHomeEmpty));
    }

    partial void OnHasDevicesChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyHint));

    partial void OnHasScenesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowHomeEmpty));
        OnPropertyChanged(nameof(ShowSceneEmpty));
        OnPropertyChanged(nameof(ShowSceneSkeleton));
        OnPropertyChanged(nameof(ShowQuickScenes));
        RaiseTrendRowFlags();
    }

    private void TickClock()
    {
        var n = DateTime.Now;
        ClockText = n.ToString("HH:mm:ss");
        var week = "日一二三四五六"[(int)n.DayOfWeek];
        DateText = $"{n.Year}年{n.Month}月{n.Day}日 星期{week}";
        var h = n.Hour;
        Greet = h < 5 ? "凌晨好" : h < 9 ? "早上好" : h < 12 ? "上午好" : h < 14 ? "中午好" : h < 18 ? "下午好" : "晚上好";
    }

    /// <summary>换到配对的服务凭证后再建客户端（凭证顺序反了会被判 auth error）。</summary>
    private async Task InitClientAsync(bool refreshToken = true)
    {
        // 先铺上次缓存的设备，联网成功后整表替换，避免开屏干等
        ApplyCachedDevices();
        // 没有缓存时 TotalCount 仍为 0，这里提前置加载态，让概览/设备骨架在刷 token 的等待期就出现
        BeginInitialSync();
        IsLoading = true;
        if (refreshToken)
        {
            try
            {
                if (!await _auth.RefreshServiceTokenAsync()) Log("刷新 serviceToken 失败，请重新扫码登录");
            }
            catch (Exception ex)
            {
                Log($"刷新 serviceToken 出错：{ex.Message}");
            }
        }
        _client?.Dispose();
        _client = new MijiaClient(_auth);
        await LoadDevicesAsync();
    }

    /// <summary>读 %APPDATA% 下的设备缓存直接上屏；没有缓存则静默跳过。</summary>
    private void ApplyCachedDevices()
    {
        var cached = DeviceCacheStore.Load();
        if (cached == null) return;
        // 旧版本缓存可能混着多个家庭的设备：只留当前（或上次）家庭 + 共享设备，开屏不混显
        var keepHome = SelectedHome?.Id ?? AppSettingsStore.HomeId;
        if (!string.IsNullOrEmpty(keepHome))
            cached = cached
                .Where(d => string.IsNullOrEmpty(d.HomeId) || d.HomeId == keepHome || d.HomeId == "shared")
                .ToList();
        if (cached.Count == 0) return;
        foreach (var d in cached) d.ApplyLegacyDefaults();
        Dispatcher.UIThread.Post(() =>
        {
            _allDevices = cached;
            RebuildVisibleDevices();
            SyncSceneGroups();
            RebuildRooms();
            ApplyFilter();
            RebuildHomeGroups();
            UpdateStats();
            RefreshSpeakers();
        });
        Log($"已加载本地缓存的 {cached.Count} 个设备");
        _ = LoadIconsAsync(cached);
    }

    /// <summary>刷新：先刷家庭列表，再只拉「当前选中家庭」的设备（不是所有家庭混在一起）。</summary>
    [RelayCommand]
    private Task LoadDevicesAsync() => LoadSelectedHomeDevicesAsync(forceHomeRefresh: true);

    /// <summary>把 homelist 同步成标题栏下拉项；选中项尽量保持用户当前选择（否则回落到上次用过的家庭）。</summary>
    private void SyncHomeOptions(List<MijiaHome> homes)
    {
        var prevId = SelectedHome?.Id;
        // 冷启动还没有选中项：沿用上次切换过的那个家庭，避免每次都跳回列表第一个
        if (string.IsNullOrEmpty(prevId)) prevId = AppSettingsStore.HomeId;
        _suppressHomeReload = true;
        try
        {
            HomeOptions.Clear();
            foreach (var h in homes)
            {
                var name = string.IsNullOrWhiteSpace(h.Name) ? "我的家" : h.Name.Trim();
                HomeOptions.Add(new HomeOption { Id = h.Id, Name = name, Uid = h.Uid });
            }

            CanSwitchHome = HomeOptions.Count > 1;
            var next = HomeOptions.FirstOrDefault(o => o.Id == prevId) ?? HomeOptions.FirstOrDefault();
            SelectedHome = next;
            foreach (var o in HomeOptions) o.IsSelected = o.Id == next?.Id;
            if (next != null) HomeName = next.Name;
        }
        finally
        {
            _suppressHomeReload = false;
        }
    }

    /// <summary>标题栏下拉切换家庭：选中项变化会触发重载；点当前项则强制刷新。</summary>
    [RelayCommand]
    private async Task SwitchHomeAsync(HomeOption? home)
    {
        if (home == null) return;
        // 选中即收起浮层（原来是 Flyout，选完自动关；换成本项目自绘浮层后要显式关）
        IsHomeOpen = false;
        if (SelectedHome?.Id == home.Id)
        {
            await LoadDevicesForSelectedHomeAsync();
            return;
        }
        SelectedHome = home; // OnSelectedHomeChanged 里会发起加载
    }

    /// <summary>家庭下拉切换后的重载：家庭列表已在手里，直接按选中项拉设备。</summary>
    private Task LoadDevicesForSelectedHomeAsync() => LoadSelectedHomeDevicesAsync(forceHomeRefresh: false);

    /// <summary>
    /// 只加载「当前选中家庭」的设备（启动、刷新、切换家庭共用）。
    /// 同一 did 复用旧实例；本次没拉到的旧设备（包括换家庭前的）一并丢弃，不会混显。
    /// </summary>
    private async Task LoadSelectedHomeDevicesAsync(bool forceHomeRefresh)
    {
        if (_client == null) return;
        BeginInitialSync();
        IsLoading = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (forceHomeRefresh)
            {
                Log("正在拉取家庭列表…");
                var homes = await _client.GetHomesAsync(forceRefresh: true);
                await Dispatcher.UIThread.InvokeAsync(() => SyncHomeOptions(homes.ToList()));
            }

            var home = SelectedHome == null ? null : _client.FindHome(SelectedHome.Id);
            if (home == null)
            {
                // 极端情况：本地选中项还在，云端列表已刷过 — 强刷 homelist 再找一次
                var homes = await _client.GetHomesAsync(forceRefresh: true);
                await Dispatcher.UIThread.InvokeAsync(() => SyncHomeOptions(homes.ToList()));
                home = SelectedHome == null ? null : _client.FindHome(SelectedHome.Id);
                if (home == null)
                {
                    Log("没有可用的家庭，已停止加载设备");
                    FinishInitialSync();
                    return;
                }
            }

            var homeName = string.IsNullOrWhiteSpace(home.Name) ? "我的家" : home.Name.Trim();
            Log($"正在拉取家庭「{homeName}」的设备…");
            var fresh = await LoadHomeDevicesAsync(home);
            foreach (var d in fresh) d.ApplyLegacyDefaults();
            var gen = ++_loadGeneration;

            // 网络请求的 await 会在线程池回来，ObservableCollection 的增删必须切回 UI 线程，
            // 否则 ItemsControl 收不到集合通知，页面上就一个卡片都不显示。
            // 设备到手就先渲染并落盘，开关状态和场景随后补，不让用户干等
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                HomeName = homeName;
                // 只留本次拉到的那批：其他家庭的旧卡片在这里被丢掉
                _allDevices = MergeDevices(_allDevices, fresh);
                RebuildVisibleDevices();
                SyncSceneGroups();
                RebuildRooms();
                ApplyFilter();
                RebuildHomeGroups();
                UpdateStats();
                RefreshSpeakers();
            });

            // 合并后的实例（旧对象被复用），后续图标/规格/状态都回填到它们身上
            var devices = _allDevices;
            _ = Task.Run(() => DeviceCacheStore.Save(devices));

            // 先按旧版 siid 兜底读一次状态，规格解析完再用真实 iid 校正一遍
            await Task.WhenAll(LoadScenesAsync(), RefreshStatesAsync(includeMetrics: false));

            // 真机图按型号取：磁盘缓存命中秒出，过期后对比产品库再决定是否重下
            _ = LoadIconsAsync(devices);

            // 规格解析较慢（每种型号一次请求），放后台跑，完成后再刷新状态与读数
            _ = ResolveSpecsThenRefreshAsync(devices, gen);

            Log($"「{homeName}」已加载 {_allDevices.Count} 个设备、{SceneItems.Count} 个场景");
            TelemetryService.Track("devices_loaded", "加载设备", new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["count"] = DeviceCountBucket(_allDevices.Count),
                ["ms"] = (int)Math.Min(sw.ElapsedMilliseconds, 60000),
            });

            // 设备加载完成后立即收缩原生渲染资源：大量卡片渲染会产生 Avalonia 合成层与 GPU 纹理，
            // GC 不会自动回收这些原生内存，需主动调用 SetProcessWorkingSetSize 让 Windows 收缩工作集。
            Services.MemoryTrimmer.TrimForce();
        }
        catch (Exception ex)
        {
            Log($"拉取设备失败：{ex.Message}");
            TelemetryService.Track("devices_loaded", "加载设备", new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["ms"] = (int)Math.Min(sw.ElapsedMilliseconds, 60000),
            });
            // 断网/接口失败时若还没铺上屏，再兜一次缓存
            if (!HasDevices) ApplyCachedDevices();
            // 这轮拿不到数据，后台读数链不会启动，骨架要在这里收尾，不能让它一直转
            FinishInitialSync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private List<MijiaDevice>? _sharedDevices;

    /// <summary>家庭设备 + 共享设备（共享列表只拉一次，按 did 去重）。</summary>
    private async Task<List<MijiaDevice>> LoadHomeDevicesAsync(MijiaHome home)
    {
        var devices = await _client!.GetDevicesAsync(home);
        try
        {
            _sharedDevices ??= await _client.GetSharedDevicesAsync();
            var seen = devices.Select(d => d.Did).ToHashSet();
            foreach (var d in _sharedDevices)
                if (seen.Add(d.Did)) devices.Add(d);
        }
        catch
        {
            // 共享设备是增量能力，拉取失败不影响家庭设备
        }
        return devices;
    }

    /// <summary>后台解析规格，完成后用真实 siid/piid 重读状态与读数；列表已换代则丢弃。</summary>
    private async Task ResolveSpecsThenRefreshAsync(List<MijiaDevice> devices, int generation)
    {
        try
        {
            await ResolveSpecsAsync(devices, generation);
            if (generation != _loadGeneration) return;
            await ResolveAcSchemasAsync(devices, generation);
            await RefreshStatesAsync(includeMetrics: true);
        }
        catch (Exception ex)
        {
            Log($"解析设备规格失败：{ex.Message}");
        }
        finally
        {
            // 温湿度/耗材这一拍才算真正收尾；切家庭换了代时由新一轮自己收尾
            if (generation == _loadGeneration) Dispatcher.UIThread.Post(FinishInitialSync);
        }
    }

    private async Task ResolveSpecsAsync(List<MijiaDevice> devices, int generation)
    {
        if (_client is not { } client) return;
        var groups = devices
            .GroupBy(d => string.IsNullOrEmpty(d.SpecType) ? d.Did : d.SpecType)
            .Select(g => g.OrderByDescending(x => x.IsOnline).ToList())
            .ToList();
        if (groups.Count == 0) return;

        using var gate = new SemaphoreSlim(4);
        var ok = 0;
        await Task.WhenAll(groups.Select(async group =>
        {
            await gate.WaitAsync();
            try
            {
                var spec = await DeviceSpecService.GetAsync(client, group[0]);
                if (spec == null || generation != _loadGeneration) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var d in group) d.ApplySpec(spec);
                    if (DrawerDevice != null && group.Any(x => ReferenceEquals(x, DrawerDevice)))
                    {
                        BuildSpecPanel(DrawerDevice);
                        _ = LoadSpecPanelValuesAsync(DrawerDevice);
                    }
                });
                ok++;
            }
            catch
            {
                // 单个型号失败不影响其它设备
            }
            finally
            {
                gate.Release();
            }
        }));
        if (ok > 0) Log($"已解析 {ok}/{groups.Count} 种设备的控制规格");

        await ResolveAcSchemasAsync(devices, generation);
        if (generation != _loadGeneration) return;

        // 规格解析后才知道哪些是音箱、哪些有耗材；这里统一刷新这两块的候选与汇总
        RefreshSpeakers();
        RebuildConsumables();
        RecomputeEnvironment();
        _ = RefreshEnergyStatsAsync(true);
    }

    /// <summary>
    /// 空调控制参数（模式/温度/风速的 siid/piid 与选项）按 spec_type 拉取并缓存到设备实例；
    /// 规格服务自带缓存，重复调用不重复请求。拉到后补读一次空调三态，桌面空调卡开机即可见真实值。
    /// </summary>
    private async Task ResolveAcSchemasAsync(List<MijiaDevice> devices, int generation)
    {
        var acs = devices.Where(d => d.IsAc && d.AcSchema == null).ToList();
        if (acs.Count == 0) return;

        using var gate = new SemaphoreSlim(4);
        foreach (var group in acs.GroupBy(d => string.IsNullOrEmpty(d.SpecType) ? d.Did : d.SpecType))
        {
            await gate.WaitAsync();
            try
            {
                var schema = await MijiaSpecService.GetAcSchemaAsync(group.Key);
                if (generation != _loadGeneration) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var d in group)
                    {
                        d.AcSchema = schema;
                        if (schema is { Valid: true } && d.IsOnline && _client != null)
                            _ = LoadAcStateAsync(_client, d);
                    }
                });
            }
            catch
            {
                // 单个型号失败不阻塞其余设备
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>按规格读一次空调模式/目标温度/风速回填（规格刚就位时用一次）。</summary>
    private async Task LoadAcStateAsync(MijiaClient client, MijiaDevice d)
    {
        if (d.AcSchema is not { Valid: true } s) return;
        try
        {
            var st = await client.GetAcStateAsync(d.TargetDid, s);
            if (st != null)
            {
                d.AcMode = st.Mode;
                d.AcTargetTemp = st.TargetTemp;
                d.AcFanLevel = st.FanLevel;
            }
        }
        catch
        {
            // 读取失败留待下一拍轮询
        }
    }

    /// <summary>
    /// 读在线设备的开关/通道/窗帘状态；includeMetrics 时顺带读温湿度。
    /// 刚下发过指令的设备（抑制窗口内）直接跳过，避免本地乐观态被旧状态覆盖。
    /// </summary>
    private async Task RefreshStatesAsync(bool includeMetrics)
    {
        if (_client is not { } client) return;

        // 合成水表/气表在重启后先接上历史累计值，再让这一拍的新读数参与差值
        RestoreMeterBaselines();

        var now = Environment.TickCount64;
        var query = new List<(string Did, int Siid, int Piid)>();
        var keys = new HashSet<(string, int, int)>();

        foreach (var d in _allDevices)
        {
            if (!d.IsOnline || d.PendingUntilMs > now) continue;
            var did = d.TargetDid;

            // 音箱音量单独读，不依赖有没有电源属性（播放类设备常常没有 on/off）
            if (d.IsSpeaker && d.SpeakerVolumeSiid > 0) Add((did, d.SpeakerVolumeSiid, d.SpeakerVolumePiid));
            // 播放状态：规格里有 playing-state 就回读（云端值确实会变，pause 后实测由 1 变 2）
            if (d.IsSpeaker && d.PlayingStateSiid > 0) Add((did, d.PlayingStateSiid, d.PlayingStatePiid));

            if (d.Channels.Count > 0)
            {
                foreach (var ch in d.Channels) Add((did, ch.Siid, ch.Piid));
            }
            else if (d.IsCurtain)
            {
                Add((did, 2, 3));
            }
            else if (d.HasPower)
            {
                Add((did, d.PowerSiid, d.PowerPiid));
            }

            // 外显字段里近实时的那部分（功率/电压/电流等）每拍都读，卡片读数才跟得上
            foreach (var p in CardMetricsCatalog.FastProps(d)) Add((did, p.Siid, p.Piid));

            // 空调：模式 / 目标温度 / 风速（桌面空调卡与详情抽屉都靠这份回读；规格缺失就跳过）
            if (d.IsAc && d.AcSchema is { Valid: true } ac)
            {
                if (ac.ModePiid > 0) Add((did, ac.ModeSiid, ac.ModePiid));
                if (ac.TempPiid > 0) Add((did, ac.TempSiid, ac.TempPiid));
                if (ac.FanPiid > 0) Add((did, ac.FanSiid, ac.FanPiid));
            }

            // 用电卡要的是全屋所有报功率的设备：空调、浴霸这类默认外显温湿度，
            // 以前从没请求过 electric-power，整台设备就被漏在「全部设备（叠加）」之外
            if (d.Spec is { } powerSpec)
            {
                if ((powerSpec.FindMetric("electric-power") ?? powerSpec.FindProp("electric-power")) is { } powerProp)
                    Add((did, powerProp.Siid, powerProp.Piid));
                // 空调之类只报累计耗电量（power-consumption，kWh）：读它的计数器，用差值算电量
                if ((powerSpec.FindMetric("power-consumption") ?? powerSpec.FindProp("power-consumption")) is { } counterProp)
                    Add((did, counterProp.Siid, counterProp.Piid));
            }

            // 用水卡 / 水质卡：每拍都读水表与 TDS，差值才连得上（不跟着卡片外显配置走）
            if (d.IsWaterDevice && d.Spec is { } waterSpec)
                foreach (var p in WaterProps(waterSpec, d))
                    Add((did, p.Siid, p.Piid));

            // 抽屉打开时，顶部读数卡的指标也每拍读一遍，数值才跟手
            if (ReferenceEquals(d, DrawerDevice))
                foreach (var p in CardMetricsCatalog.Candidates(d)) Add((did, p.Siid, p.Piid));

            if (includeMetrics)
            {
                // 温湿度总是回读，不管设备卡上有没有选它们：环境概览和首页趋势卡都靠这份读数
                if (d.Spec?.FindMetric("temperature") is { } t) Add((did, t.Siid, t.Piid));
                var humProp = d.Spec?.FindMetric("relative-humidity") ?? d.Spec?.FindMetric("humidity");
                if (humProp != null) Add((did, humProp.Siid, humProp.Piid));
                // 空气读数（PM2.5 / PM10 / 甲醛 / TVOC / CO₂）：首页那排空气小卡靠它，
                // 同样不跟卡片外显配置走——外显里没勾这几项的设备以前一次都没被请求过
                if (d.Spec is { } airSpec)
                    foreach (var (_, prop) in AirMetrics.Props(airSpec)) Add((did, prop.Siid, prop.Piid));
                // 耗材变化慢，跟着温湿度这一拍（约 16s）读一次即可
                foreach (var c in d.Consumables) Add((did, c.Siid, c.Piid));
            }
        }
        if (query.Count == 0) return;

        Dictionary<(string Did, int Siid, int Piid), object?> map;
        try
        {
            map = await client.ReadPropsAsync(query);
        }
        catch (Exception ex)
        {
            // 轮询失败每分钟只记一条，避免日志被断网刷屏
            if (now - _lastPollErrorLogAt > 60_000)
            {
                _lastPollErrorLogAt = now;
                Log($"刷新设备状态失败：{ex.Message}");
            }
            return;
        }
        if (_client == null) return;
        await Dispatcher.UIThread.InvokeAsync(() => ApplyStates(map, includeMetrics, now));

        void Add((string Did, int Siid, int Piid) key)
        {
            if (keys.Add(key)) query.Add(key);
        }
    }

    private void ApplyStates(Dictionary<(string Did, int Siid, int Piid), object?> map,
        bool includeMetrics, long now)
    {
        var touched = false;
        var airTouched = false;
        var consumablesTouched = false;
        _applyingStates = true; // 轮询回填期间不把属性变化当成用户拖动，避免滑杆反向下发
        try
        {
            foreach (var d in _allDevices)
            {
                if (!d.IsOnline || d.PendingUntilMs > now) continue;
                var changed = false;

                if (d.Channels.Count > 0)
                {
                    foreach (var ch in d.Channels)
                    {
                        if (!map.TryGetValue((d.TargetDid, ch.Siid, ch.Piid), out var v)) continue;
                        var on = v is true;
                        if (ch.IsOn != on)
                        {
                            ch.IsOn = on;
                            changed = true;
                        }
                    }
                    d.SyncOnFromChannels();
                }
                else if (d.IsCurtain)
                {
                    if (map.TryGetValue((d.TargetDid, 2, 3), out var v) && v != null)
                    {
                        var pos = Math.Clamp(ToInt(v), 0, 100);
                        if (d.CurtainPosition != pos)
                        {
                            d.CurtainPosition = pos;
                            changed = true;
                        }
                        d.IsOn = pos > 0;
                    }
                }
                else if (d.HasPower &&
                         map.TryGetValue((d.TargetDid, d.PowerSiid, d.PowerPiid), out var v))
                {
                    var on = v is true;
                    if (d.IsOn != on)
                    {
                        d.IsOn = on;
                        changed = true;
                    }
                }

                // 音箱音量回读；拖动滑杆期间设备在抑制窗口内，不会走到这里
                if (d.IsSpeaker && d.SpeakerVolumeSiid > 0 &&
                    map.TryGetValue((d.TargetDid, d.SpeakerVolumeSiid, d.SpeakerVolumePiid), out var vv) && vv != null)
                {
                    var vol = Math.Clamp(ToInt(vv), 0, 100);
                    if (d.SpeakerVolume != vol) d.SpeakerVolume = vol;
                }

                // 播放状态回读：1 播放中、0 停止、2 暂停（规格里只有这个枚举，没有曲目文本属性）
                if (d.IsSpeaker && d.PlayingStateSiid > 0 &&
                    map.TryGetValue((d.TargetDid, d.PlayingStateSiid, d.PlayingStatePiid), out var ps) && ps != null)
                {
                    var state = ToInt(ps);
                    if (d.PlayingState != state) d.PlayingState = state;
                    var playing = state == 1;
                    if (d.IsPlaying != playing) d.IsPlaying = playing;
                }

                // 空调模式 / 目标温度 / 风速回读：桌面空调卡与抽屉共用设备实例，一处回填两处都跟着变
                if (ApplyAcState(d, map)) changed = true;

                // 卡片副标题外显读数：每拍按已缓存的文本重算，慢速读数不会中间消失
                if (ApplyCardMetrics(d, map)) changed = true;

                // 瞬时功率：规格里有 electric-power 的设备（插座、空调、浴霸……）都回读，
                // 首页用电卡按它求和；跟卡片副标题上勾没勾「功率」无关
                if (ApplyPowerReading(d, map)) changed = true;

                // 用水设备的水表 / 水流量 / 水温 / TDS：首页用水卡靠这份读数攒历史
                if (ApplyWaterReading(d, map)) changed = true;

                // 空气读数：规格里有 pm2.5 / hcho 这类属性的设备，不管是快拍（卡片外显里勾了）
                // 还是慢拍（每 4 拍补读一次）回来的值，都记进 AirReadings 供首页小卡使用
                if (ApplyAirReadings(d, map)) airTouched = true;

                // 抽屉顶部读数卡
                if (ReferenceEquals(d, DrawerDevice)) UpdateDrawerMetricTiles(d, map);

                if (includeMetrics)
                {
                    // 温湿度是环境概览与趋势卡的输入，不能被「卡片上显不显示」绑住：
                    // 即使用户在详情里把湿度从设备卡上关掉，这里照样回填读数
                    if (ApplyEnvReading(d, map)) changed = true;

                    var consChanged = false;
                    foreach (var c in d.Consumables)
                    {
                        if (!map.TryGetValue((d.TargetDid, c.Siid, c.Piid), out var cv) || cv == null) continue;
                        var pct = Math.Clamp(ToInt(cv), 0, 100);
                        if (c.Percent != pct)
                        {
                            c.Percent = pct;
                            consChanged = true;
                        }
                    }
                    if (consChanged)
                    {
                        d.RefreshConsumableFlags();
                        consumablesTouched = true;
                    }
                }
                if (ReferenceEquals(d, DrawerDevice)) ApplySpecValues(d, map);
                touched |= changed;
            }
            if (touched) UpdateStats();
            // 空气读数在快拍里也能到货：变了就单独刷一次小卡，不用等下一拍慢速重算；
            // 但小卡属于「数据看板」，开了「更新频率」时要跟着看板的节奏走
            if (airTouched && BoardRefreshDue(DateTime.Now)) RefreshAirCards();
            if (includeMetrics)
            {
                RecomputeEnvironment();
                if (consumablesTouched) RebuildConsumables();
            }
        }
        finally
        {
            _applyingStates = false;
        }
    }

    /// <summary>
    /// 卡片副标题外显读数：按设备类型取用户选定的字段（插座默认功率，其余默认温湿度）。
    /// 读到新值就更新缓存，再用缓存重新拼副标题——温湿度每 4 拍才读一次，
    /// 靠缓存才能在中间几拍里保留读数而不闪没。
    /// </summary>
    private static bool ApplyCardMetrics(MijiaDevice d,
        Dictionary<(string Did, int Siid, int Piid), object?> map)
    {
        var spec = d.Spec;
        if (spec == null) return false;
        var names = CardMetricsCatalog.EffectiveNames(d);
        var changed = false;

        foreach (var name in names)
        {
            var prop = CardMetricsCatalog.FindSelectedMetric(spec, name);
            if (prop == null) continue;
            var metricKey = CardMetricsCatalog.StandardMetricName(prop.Name) ?? name;
            if (!map.TryGetValue((d.TargetDid, prop.Siid, prop.Piid), out var raw) || raw == null)
                continue;

            var value = ToDouble(raw);
            var text = CardMetricsCatalog.FormatValue(prop, value);
            if (!d.CardMetricTexts.TryGetValue(metricKey, out var old) || old != text)
            {
                d.CardMetricTexts[metricKey] = text;
                changed = true;
            }

            if (metricKey == "temperature")
            {
                // 热水器/净水器的 temperature 是水温：记到 WaterTempC，不能混进室温
                if (d.IsWaterDevice) d.WaterTempC = value;
                else d.TemperatureC = CardMetricsCatalog.NormalizeTemperature(value);
            }
            else if (metricKey is "relative-humidity" or "humidity")
                d.Humidity = CardMetricsCatalog.NormalizeHumidity(value);
        }

        return changed | RebuildCardSubtitle(d);
    }

    /// <summary>
    /// 按当前外显配置重拼卡片副标题：只认选中的读数，顺手把缓存里已经不再外显的键剔掉
    /// （悬停提示读的是整份缓存，留着会让关掉的读数继续挂在提示里）。
    /// 轮询回填和「详情里改读数开关」都走这一个口径，改完当即生效，不用等下一拍。
    /// </summary>
    private static bool RebuildCardSubtitle(MijiaDevice d)
    {
        var names = CardMetricsCatalog.EffectiveNames(d);
        // 副标题顺序跟详情页那排读数卡一致（左→右），不看点击先后；
        // 候选里没有的旧名字仍按存储顺序补在后面，不让已配好的读数静默消失
        var ordered = new List<string>(names.Count);
        foreach (var p in CardMetricsCatalog.Candidates(d))
        {
            var key = CardMetricsCatalog.StandardMetricName(p.Name) ?? p.Name;
            if (names.Contains(key) && !ordered.Contains(key)) ordered.Add(key);
        }
        foreach (var name in names)
            if (!ordered.Contains(name)) ordered.Add(name);

        var stale = d.CardMetricTexts.Keys.Where(k => !ordered.Contains(k)).ToList();
        foreach (var k in stale) d.CardMetricTexts.Remove(k);

        var parts = new List<string>(ordered.Count);
        foreach (var name in ordered)
            if (d.CardMetricTexts.TryGetValue(name, out var t) && t.Length > 0)
                parts.Add(t);

        // 卡片副标题只有一行，段间用「·」紧连（两边不留空格）—— 只用于悬停提示与旧口径；
        // 卡面上真正逐枚排的是 CardMetricTags，标签换行时整枚下移，不会把读数截成两半
        var text2 = string.Join("·", parts);
        var tagsChanged = SyncCardMetricTags(d, parts);
        if (d.MetricsText == text2) return tagsChanged;
        d.MetricsText = text2;
        return true;
    }

    /// <summary>把读数标签同步成当前外显的那几项；顺序没变就不动集合，免得每拍轮询都重建一遍标签控件。</summary>
    private static bool SyncCardMetricTags(MijiaDevice d, List<string> parts)
    {
        if (d.CardMetricTags.Count == parts.Count)
        {
            var same = true;
            for (var i = 0; i < parts.Count; i++)
                if (!string.Equals(d.CardMetricTags[i], parts[i], StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            if (same) return false;
        }

        d.CardMetricTags.Clear();
        foreach (var part in parts) d.CardMetricTags.Add(part);
        // 读数标签三枚以上就放不下一行带房间名的了：卡片上让出房间，把宽度留给读数
        d.ShowCardRoom = parts.Count < 3;
        return true;
    }

    /// <summary>
    /// 温湿度单独回填：读写口径与卡片副标题无关，只认规格里有没有这两个属性。
    /// 环境概览、首页趋势卡都读 <see cref="MijiaDevice.TemperatureC"/> / <see cref="MijiaDevice.Humidity"/>，
    /// 所以用户在详情里把湿度从设备卡上关掉，也不该让这两处断粮。
    /// </summary>
    private static bool ApplyEnvReading(MijiaDevice d,
        Dictionary<(string Did, int Siid, int Piid), object?> map)
    {
        var spec = d.Spec;
        if (spec == null) return false;

        // 热水器/净水器只有水温，没有室温：环境概览与温度趋势不该被一缸 45℃ 的热水拉高
        if (d.IsWaterDevice) return false;

        var changed = false;

        if (spec.FindMetric("temperature") is { } tp &&
            map.TryGetValue((d.TargetDid, tp.Siid, tp.Piid), out var tr) && tr != null)
        {
            var t = CardMetricsCatalog.NormalizeTemperature(ToDouble(tr));
            if (d.TemperatureC != t)
            {
                d.TemperatureC = t;
                changed = true;
            }
        }

        var hp = spec.FindMetric("relative-humidity") ?? spec.FindMetric("humidity");
        if (hp != null &&
            map.TryGetValue((d.TargetDid, hp.Siid, hp.Piid), out var hr) && hr != null)
        {
            var h = CardMetricsCatalog.NormalizeHumidity(ToDouble(hr));
            if (d.Humidity != h)
            {
                d.Humidity = h;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// 空气读数回填：规格里能报 PM2.5 / PM10 / 甲醛 / TVOC / 二氧化碳的设备，
    /// 把原始值归一成卡片口径（甲醛、TVOC 一律 mg/m³）后记进 <see cref="MijiaDevice.AirReadings"/>。
    /// 跟卡片外显配置无关：首页那排空气小卡统一读这份，用户在设备卡上没勾甲醛也照读。
    /// </summary>
    private static bool ApplyAirReadings(MijiaDevice d,
        Dictionary<(string Did, int Siid, int Piid), object?> map)
    {
        var spec = d.Spec;
        if (spec == null) return false;

        var changed = false;
        foreach (var (metric, prop) in AirMetrics.Props(spec))
        {
            if (!map.TryGetValue((d.TargetDid, prop.Siid, prop.Piid), out var raw) || raw == null) continue;

            var value = AirMetrics.Normalize(metric, prop, ToDouble(raw));
            if (d.AirReadings.TryGetValue(metric.Standard, out var old) && Math.Abs(old - value) < 0.0005)
                continue;

            d.AirReadings[metric.Standard] = value;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 用电读数单独回填：瞬时功率认 electric-power（W）；空调、浴霸这类只有
    /// power-consumption（累计耗电量，kWh）的，读它的计数器，交给历史按差值算电量。
    /// 首页用电卡的全屋叠加靠它，所以不受「卡片副标题上勾没勾功率」影响——
    /// 这些设备默认只显示温湿度，以前连功率都没被请求过，全屋用电因此漏了一截。
    /// </summary>
    private static bool ApplyPowerReading(MijiaDevice d,
        Dictionary<(string Did, int Siid, int Piid), object?> map)
    {
        var spec = d.Spec;
        if (spec == null) return false;

        var changed = false;

        if ((spec.FindMetric("electric-power") ?? spec.FindProp("electric-power")) is { } prop &&
            map.TryGetValue((d.TargetDid, prop.Siid, prop.Piid), out var raw) && raw != null)
        {
            // 与 FormatValue 的口径一致：打包属性先取 bit 段；kW 规格先折回 W；
            // 上限过百万的型号是 0.01W 编码，按百分之一瓦缩回
            var value = prop.Decode(ToDouble(raw)) * prop.ValueScale;
            var watts = prop.HasRange && prop.Max >= 1_000_000 ? value / 100 : value;
            if (d.PowerWatts is not { } oldW || Math.Abs(oldW - watts) >= 0.01)
            {
                d.PowerWatts = watts;
                changed = true;
            }
        }

        if ((spec.FindMetric("power-consumption") ?? spec.FindProp("power-consumption")) is { } counter &&
            map.TryGetValue((d.TargetDid, counter.Siid, counter.Piid), out var craw) && craw != null)
        {
            // 计数器是整数：多数插座按 0.01 度、少数按瓦时编码（规格描述里写明）。
            // 按规格给出的倍率折算成度，否则累计电量会被放大 100 / 1000 倍。
            var kwh = counter.Decode(ToDouble(craw)) * counter.UnitScale;
            if (d.EnergyKwh is not { } oldKwh || Math.Abs(oldKwh - kwh) >= 0.001)
            {
                d.EnergyKwh = kwh;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// 用水/水质读数：热水器的本次用水、单次用水记录、水流量、水温与进水温度；
    /// 净水器的累计制水（cumu-flow-out，毫升折升）与进出水 TDS。
    /// 每拍都读：单次用水一变就累进合成水表，历史采样按合成水表的差值算用量。
    /// </summary>
    private static bool ApplyWaterReading(MijiaDevice d,
        Dictionary<(string Did, int Siid, int Piid), object?> map)
    {
        var spec = d.Spec;
        if (spec == null || !d.IsWaterDevice) return false;

        var changed = false;
        var isPurifier = d.CategoryName == "water-purifier";

        bool TryGet(SpecPropertyDef prop, out double value)
        {
            value = 0;
            if (!map.TryGetValue((d.TargetDid, prop.Siid, prop.Piid), out var raw) || raw == null) return false;
            value = ToDouble(raw);
            return true;
        }

        if (isPurifier)
        {
            if (spec.FindProp("cumu-flow-out") is { } cumu && TryGet(cumu, out var ml))
            {
                var liters = Math.Round(ml / 1000, 3);
                if (d.WaterLiters is not { } oldL || Math.Abs(oldL - liters) >= 0.001)
                {
                    d.WaterLiters = liters;
                    changed = true;
                }
            }
        }
        else
        {
            // water-level 是「本次用水升数」，用完就清零，不能直接当累计表；
            // 真正靠得住的是 usage-report 里的「单次用水」——每完成一次用水变一次，
            // 变化时把整次用量累进 WaterLiters（合成一个只增不减的水表）。
            if (spec.FindProp("water-level") is { } level && TryGet(level, out var sessionLiters))
            {
                if (d.SessionWaterLiters is not { } oldSession || Math.Abs(oldSession - sessionLiters) >= 0.1)
                {
                    d.SessionWaterLiters = sessionLiters;
                    changed = true;
                }
            }

            var single = spec.FindProp("single-water");
            if (single != null && TryGet(single, out var singleLiters))
            {
                if (!d.UsesSingleWater)
                {
                    // 从 water-level 兜底切到单次用水记录：只换口径，不把同一单重复加一遍
                    d.UsesSingleWater = true;
                    d.LastSingleWaterLiters = singleLiters;
                    if (!d.WaterMeterInit)
                    {
                        // 起基线：机器里存的上一单可能是今早用的，不把它算进「从此刻开始」的账
                        d.WaterMeterInit = true;
                        d.WaterLiters = 0;
                    }
                    changed = true;
                }
                else if (singleLiters > 0.01 &&
                         (d.LastSingleWaterLiters is not { } last || Math.Abs(last - singleLiters) >= 0.01))
                {
                    // 单次用水是「最后一单」的用量，不是累计值，换了一单就直接加这一单
                    d.WaterLiters = (d.WaterLiters ?? 0) + singleLiters;
                    d.LastSingleWaterLiters = singleLiters;
                    changed = true;
                }
            }

            // 用气：usage-report 的 single-gas 单位是 0.001 m³（规格里误写成 L），
            // 同样只在「洗完一单」后变化，变一次就把整单气量累进合成气表。
            var singleGas = spec.FindProp("single-gas");
            if (singleGas != null && TryGet(singleGas, out var singleGasRaw))
            {
                var gasM3 = Math.Round(singleGasRaw * 0.001, 3);
                if (!d.GasMeterInit)
                {
                    d.GasMeterInit = true;
                    d.GasM3 = 0;
                    d.LastSingleGasM3 = gasM3;
                    if (d.SessionGasM3 is not { } oldGas || Math.Abs(oldGas - gasM3) >= 0.001)
                        d.SessionGasM3 = gasM3;
                    changed = true;
                }
                else
                {
                    if (d.SessionGasM3 is not { } oldGas || Math.Abs(oldGas - gasM3) >= 0.001)
                    {
                        d.SessionGasM3 = gasM3;
                        changed = true;
                    }
                    if (d.LastSingleGasM3 is null)
                    {
                        // 重启后气表从历史接上：第一次读数只对齐「上一单」，不重复加一次
                        d.LastSingleGasM3 = gasM3;
                        changed = true;
                    }
                    else if (gasM3 > 0.0005 &&
                             Math.Abs((d.LastSingleGasM3 ?? 0) - gasM3) >= 0.0005)
                    {
                        d.GasM3 = (d.GasM3 ?? 0) + gasM3;
                        d.LastSingleGasM3 = gasM3;
                        changed = true;
                    }
                }
            }
            else if (!d.UsesSingleWater && spec.FindProp("water-level") is { } fallback && TryGet(fallback, out var levelNow))
            {
                // 没有单次用水记录的老机型：退化成本次用水的正向增量，至少不会把清零算成负用水
                if (!d.WaterMeterInit)
                {
                    d.WaterMeterInit = true;
                    d.WaterLiters = 0;
                    d.LastSessionWaterLevelLiters = levelNow;
                    changed = true;
                }
                else
                {
                    var prev = d.LastSessionWaterLevelLiters ?? 0;
                    if (levelNow > prev)
                    {
                        d.WaterLiters = (d.WaterLiters ?? 0) + (levelNow - prev);
                        changed = true;
                    }
                    d.LastSessionWaterLevelLiters = levelNow;
                }
            }
            if (spec.FindProp("discharge-water") is { } flow && TryGet(flow, out var flowRaw))
            {
                var lpm = Math.Round(flowRaw * 0.1, 1);
                if (d.WaterFlowLpm is not { } oldFlow || Math.Abs(oldFlow - lpm) >= 0.1)
                {
                    d.WaterFlowLpm = lpm;
                    changed = true;
                }
            }
            if (spec.FindProp("input-water-temp") is { } input && TryGet(input, out var inputTemp))
            {
                if (d.InputWaterTempC is not { } oldIn || Math.Abs(oldIn - inputTemp) >= 0.1)
                {
                    d.InputWaterTempC = inputTemp;
                    changed = true;
                }
            }
            // 工作状态只认热水器主服务里那一个 status（换气/增压服务也各有 status，不能混）
            var status = spec.Properties.FirstOrDefault(p =>
                p.Readable && p.Name == "status" && p.ServiceName == "water-heater");
            if (status != null && TryGet(status, out var st))
            {
                var state = (int)st;
                if (d.WaterStatus != state)
                {
                    d.WaterStatus = state;
                    changed = true;
                }
            }
        }

        // 两类设备都可能把水温放在 temperature 里；数值本身就是 ℃，不再按 0.1 缩
        if (spec.FindProp("temperature") is { } temp && TryGet(temp, out var waterTemp))
        {
            if (d.WaterTempC is not { } oldT || Math.Abs(oldT - waterTemp) >= 0.1)
            {
                d.WaterTempC = waterTemp;
                changed = true;
            }
        }

        if (isPurifier)
        {
            if (spec.FindProp("tds-out") is { } tdsOut && TryGet(tdsOut, out var outTds))
            {
                if (d.TdsOut is not { } oldOut || Math.Abs(oldOut - outTds) >= 1)
                {
                    d.TdsOut = outTds;
                    changed = true;
                }
            }
            if (spec.FindProp("tds-in") is { } tdsIn && TryGet(tdsIn, out var inTds))
            {
                if (d.TdsIn is not { } oldIn || Math.Abs(oldIn - inTds) >= 1)
                {
                    d.TdsIn = inTds;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>用水设备每次轮询要读的属性：水表、流量、水温、工作状态，净水器再加 TDS。</summary>
    private static IEnumerable<SpecPropertyDef> WaterProps(DeviceSpec spec, MijiaDevice d)
    {
        var isPurifier = d.CategoryName == "water-purifier";
        var names = isPurifier
            ? new[] { "cumu-flow-out", "tds-out", "tds-in", "temperature" }
            : new[] { "water-level", "single-water", "single-gas", "discharge-water", "input-water-temp", "temperature", "status" };

        foreach (var name in names)
        {
            // status 只认热水器主服务里的那一个
            var prop = name == "status"
                ? spec.Properties.FirstOrDefault(p => p.Readable && p.Name == name && p.ServiceName == "water-heater")
                : spec.FindProp(name);
            if (prop != null) yield return prop;
        }
    }

    /// <summary>把轮询到的空调模式 / 目标温度 / 风速回填到设备实例（与规格面板无关的三态直读）。</summary>
    private bool ApplyAcState(MijiaDevice d,
        Dictionary<(string Did, int Siid, int Piid), object?> map)
    {
        if (d.IsAc && d.AcSchema is { Valid: true } s)
        {
            var changed = false;
            if (s.ModePiid > 0 && map.TryGetValue((d.TargetDid, s.ModeSiid, s.ModePiid), out var mv) && mv != null)
            {
                var mode = ToInt(mv);
                if (d.AcMode != mode) { d.AcMode = mode; changed = true; }
            }
            if (s.TempPiid > 0 && map.TryGetValue((d.TargetDid, s.TempSiid, s.TempPiid), out var tv) && tv != null)
            {
                var temp = ToDouble(tv);
                if (Math.Abs(d.AcTargetTemp - temp) > s.TempStep / 2) { d.AcTargetTemp = temp; changed = true; }
            }
            if (s.FanPiid > 0 && map.TryGetValue((d.TargetDid, s.FanSiid, s.FanPiid), out var fv) && fv != null)
            {
                var fan = ToInt(fv);
                if (d.AcFanLevel != fan) { d.AcFanLevel = fan; changed = true; }
            }
            return changed;
        }
        return false;
    }

    private void ApplySpecValues(MijiaDevice d,
        Dictionary<(string Did, int Siid, int Piid), object?> map)
    {
        foreach (var vm in DrawerSpecAdvancedGroups.SelectMany(g => g.Items))
        {
            if (_specCommitTimers.ContainsKey(vm)) continue; // 拖动中的滑杆不被轮询拽回
            if (map.TryGetValue((d.TargetDid, vm.Def.Siid, vm.Def.Piid), out var v))
                vm.Load(v);
        }
    }

    private static int ToInt(object? v) => v switch
    {
        int i => i,
        double d => (int)d,
        long l => (int)l,
        string s when int.TryParse(s, out var i) => i,
        _ => 0
    };

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        int i => i,
        long l => l,
        string s when double.TryParse(s, out var d) => d,
        _ => 0
    };

    /// <summary>
    /// 按型号取真机图回填卡片。型号数量有限且同型号共用一张图，所以先按 model 分组，
    /// 并发限制在 5，避免几十个型号同时打产品库。
    /// </summary>
    private async Task LoadIconsAsync(List<MijiaDevice> devices)
    {
        var groups = devices.GroupBy(d => d.Model).Where(g => !string.IsNullOrEmpty(g.Key)).ToList();

        // 型号 → 拟物图地址：走米家 App 同款的产品配置接口，整批一次问齐（每批最多 100 个型号）。
        // 内置型号快照没有收录的新设备（trytogo 这类三方灯具），靠这里拿到它们自己的产品图。
        var onlineIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var client = _client;
        if (client != null && groups.Count > 0)
        {
            try
            {
                onlineIcons = await client.GetProductIconUrlsAsync(groups.Select(g => g.Key));
            }
            catch (Exception ex)
            {
                Log($"产品图地址获取失败：{ex.Message}");
            }
        }
        Task<string?> ResolveOnline(string model) =>
            Task.FromResult(onlineIcons.TryGetValue(model, out var url) ? url : null);

        using var gate = new SemaphoreSlim(5);
        var ok = 0;
        try
        {
            await Task.WhenAll(groups.Select(async g =>
            {
                await gate.WaitAsync();
                try
                {
                    // 设备自己的封面（devPicInfo.logo / 设备列表 icon）优先，米家也是这个顺序；
                    // 同型号多台共用第一张，用户给单台换过封面时以那台为准
                    var preferredIcon = g.Select(d => d.CustomIconUrl)
                        .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
                    var bmp = await MijiaIconService.GetAsync(g.Key, preferredIcon, ResolveOnline);
                    if (bmp == null) return;
                    ok++;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var d in g) d.Icon = bmp;
                        // 编辑弹窗开着时同步刷新卡片图标（打开时可能还没取到产品图）
                        if (IsHotkeyEditorOpen) RefreshDraftStepDetails();
                    });
                }
                finally
                {
                    gate.Release();
                }
            }));
            if (ok > 0) Log($"已匹配 {ok}/{groups.Count} 个型号的产品图标");
        }
        catch (Exception ex)
        {
            Log($"读取设备图标失败：{ex.Message}");
        }

        // 冷拉取这一路解出来的大缓冲（产品图原图像素、HTTP 字节、规格 JSON）全是一次性垃圾，
        // 又都落在大对象堆上——GC 默认既不压缩 LOH，也不会马上把提交内存还给系统，进程就停在
        // 加载峰值不动。这里收一次，顺带强制压缩 LOH，把虚高的常驻内存压回真实用量。
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        // Aggressive 模式在 .NET 中强制要求 blocking:true，否则会抛 ArgumentException；
        // 该调用在丢弃的 Task 内，异常一旦冒出就成了未观察异常，最终在 finalizer 线程 rethrow 拖崩进程。
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true);
    }

    private async Task LoadScenesAsync()
    {
        if (_client == null) return;
        var scenes = new List<MijiaScene>();
        try
        {
            // 一次拿全：手动场景 + 自动化 + 各自的动作/触发条件（见 MijiaClient.GetSceneBundleAsync）
            scenes = await _client.GetSceneBundleAsync();
        }
        catch (Exception ex)
        {
            Log($"拉取场景失败：{ex.Message}");
        }

        _sceneIndex.Rebuild(scenes);
        _scenesLoadedOnce = true;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _scenes = scenes;

            SceneItems.Clear();
            AutomationItems.Clear();
            // 新建的卡片放上面：按创建时间倒序（解析不了时间戳的按 0 沉底，其余保持云端相对顺序）
            var i = 0;
            foreach (var s in scenes.Where(x => !x.IsAutomation).OrderByDescending(Newness))
                SceneItems.Add(SceneCardItem.Wrap(s, i++));
            i = 0;
            foreach (var s in scenes.Where(x => x.IsAutomation).OrderByDescending(Newness))
                AutomationItems.Add(SceneCardItem.Wrap(s, i++));

            UpdateDrawerScenes();
            RaiseSceneCounters();
            // 场景列表刚换过：展示集合同步套一次当前关键词
            ApplySceneFilter();
            UpdateStats();
            // 快捷键的目标名 / 自动化启停态跟着场景一起刷新
            RefreshHotkeyViews();
        });
    }

    /// <summary>排序键：创建时间（unix 秒字符串），解析失败按 0（沉底）。</summary>
    private static long Newness(MijiaScene s) =>
        long.TryParse(s.CreateTime, out var t) ? t : 0;

    /// <summary>
    /// 设备列表换代后同步「灯组 ↔ 成员」关系到场景索引。
    /// 桌面弹窗可能比主窗口更早打开，关系表到位后要立即重填一次清单。
    /// </summary>
    private void SyncSceneGroups()
    {
        _sceneIndex.SetDevices(_allDevices);
        if (DrawerDevice != null) UpdateDrawerScenes();
    }

    /// <summary>设备详情「智能场景」：命中内存索引，不发网络请求。按场景 / 自动化分两组。</summary>
    private void UpdateDrawerScenes()
    {
        DrawerScenes.Clear();
        DrawerAutomations.Clear();
        if (DrawerDevice is { } device)
        {
            var seed = 0;
            foreach (var r in _sceneIndex.ForDevice(device.Did))
            {
                var card = SceneCardItem.Wrap(r.Scene, seed++);
                card.RoleText = r.RoleText;
                card.TargetDid = device.TargetDid;
                (r.Scene.IsAutomation ? DrawerAutomations : DrawerScenes).Add(card);
            }
        }
        OnPropertyChanged(nameof(HasDrawerScenes));
        OnPropertyChanged(nameof(HasDrawerSceneGroup));
        OnPropertyChanged(nameof(HasDrawerAutomationGroup));
        OnPropertyChanged(nameof(DrawerSceneSummary));
    }

    private void RaiseSceneCounters()
    {
        OnPropertyChanged(nameof(HasAutomations));
        OnPropertyChanged(nameof(ShowAutomationEmpty));
        OnPropertyChanged(nameof(SceneCountText));
        OnPropertyChanged(nameof(AutomationCountText));
        // 空态同时看手动场景和自动化，任一变化都要重判
        OnPropertyChanged(nameof(ShowSceneEmpty));
        OnPropertyChanged(nameof(ShowSceneNoMatch));
        OnPropertyChanged(nameof(ShowAutomationNoMatch));
        OnPropertyChanged(nameof(ShowHomeEmpty));
        OnPropertyChanged(nameof(ShowQuickScenes));
        OnPropertyChanged(nameof(ShowSceneSkeleton));
    }

    private bool MatchesSearch(MijiaDevice d)
    {
        var q = SearchText.Trim();
        if (q.Length == 0) return true;
        return d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || d.RoomName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 智能页筛选：把完整场景/自动化列表按当前关键词筛进展示集合。
    /// 命中范围是卡片上看得见的文字（名称 + 摘要 + 副说明），搜「回家」「灯」都能落到卡片。
    /// </summary>
    private void ApplySceneFilter()
    {
        var q = SearchText.Trim();
        FillSceneView(SceneViewItems, SceneItems, q);
        FillSceneView(AutomationViewItems, AutomationItems, q);
        OnPropertyChanged(nameof(ShowSceneNoMatch));
        OnPropertyChanged(nameof(ShowAutomationNoMatch));
    }

    /// <summary>筛进展示集合；同实例同顺序就不动，避免卡片重绑重排（字体缓存只增不减）。</summary>
    private static void FillSceneView(ObservableCollection<SceneCardItem> target,
        IReadOnlyList<SceneCardItem> source, string q)
    {
        var want = q.Length == 0
            ? (source as List<SceneCardItem> ?? source.ToList())
            : source.Where(c => MatchesScene(c, q)).ToList();
        if (SameSequence(target, want)) return;
        target.Clear();
        foreach (var c in want) target.Add(c);
    }

    private static bool MatchesScene(SceneCardItem c, string q) =>
        HasKeyword(c.Name, q) || HasKeyword(c.Summary, q) || HasKeyword(c.Detail, q);

    private static bool HasKeyword(string? text, string q) =>
        !string.IsNullOrEmpty(text) && text.Contains(q, StringComparison.OrdinalIgnoreCase);

    private void RebuildRooms()
    {
        var names = _visibleDevices.Select(d => d.RoomName)
            .Where(n => !string.IsNullOrEmpty(n))
            .GroupBy(n => n)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();
        names = ApplyRoomOrder(names);
        if (!names.Contains(_selectedRoom)) _selectedRoom = "全部";

        // 房间没变化就不重建侧栏 chip：重建同样会触发文本重排
        if (Rooms.Count == names.Count + 1 && string.Equals(Rooms[0].Name, "全部", StringComparison.Ordinal))
        {
            var same = true;
            for (var i = 0; i < names.Count; i++)
            {
                if (!string.Equals(Rooms[i + 1].Name, names[i], StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                RoomCount = names.Count;
                SyncRoomChips();
                SyncRoomLightBulbs();
                return;
            }
        }

        Rooms.Clear();
        Rooms.Add(new RoomChip("全部"));
        foreach (var n in names)
            Rooms.Add(new RoomChip(n));
        RoomCount = names.Count;
        SyncRoomChips();
        SyncRoomLightBulbs();
    }

    /// <summary>
    /// 用户拖过的房间排前面（按保存的先后），没排过的（新出现的房间）仍按设备数排在后面，
    /// 这样加新房间不会打乱已有顺序。
    /// </summary>
    private List<string> ApplyRoomOrder(List<string> names)
    {
        if (_roomOrder.Length == 0) return names;

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _roomOrder.Length; i++)
            if (!rank.ContainsKey(_roomOrder[i])) rank[_roomOrder[i]] = i;

        return names.OrderBy(n => rank.TryGetValue(n, out var r) ? r : int.MaxValue).ToList();
    }

    /// <summary>
    /// 侧栏房间拖拽落点：只改显示顺序，「全部」固定第一行；拖拽过程中连续调用，先不动磁盘，
    /// 松手时由 <see cref="SaveRoomOrder"/> 落盘一次。
    /// </summary>
    public void MoveRoom(int from, int to)
    {
        if (from <= 0 || from >= Rooms.Count) return;
        to = Math.Clamp(to, 1, Rooms.Count - 1);
        if (to == from) return;

        Rooms.Move(from, to);
        _roomOrder = Rooms.Skip(1).Select(c => c.Name).ToArray();
    }

    /// <summary>拖拽结束写回房间顺序；写盘失败不影响本次运行。</summary>
    public void SaveRoomOrder()
    {
        if (_roomOrder.Length == 0) return;
        AppSettingsStore.SetRoomOrder(_roomOrder);
    }

    /// <summary>侧栏只允许一处高亮：只有停在「房间」页时，当前房间才显示选中态。</summary>
    private void SyncRoomChips()
    {
        foreach (var c in Rooms)
            c.IsSelected = SelectedNavIndex == 1 && c.Name == _selectedRoom;
    }

    /// <summary>
    /// 按当前设置算一遍上屏清单（每次设备列表换代、切换「隐藏开关」后调用）：
    /// 1) 灯组成员在卡片上让位给灯组本身，不再散落成一盏一张卡；
    /// 2) 米家把多键开关拆出的 .sN 单键并回父卡（父卡按规格显示「N 路」，逐路在抽屉里操作）；
    /// 3) 打开「隐藏开关」时整类开关不上屏。
    /// </summary>
    private void RebuildVisibleDevices()
    {
        var groupMembers = new HashSet<string>(StringComparer.Ordinal);
        var byDid = new Dictionary<string, MijiaDevice>(_allDevices.Count, StringComparer.Ordinal);
        var childrenByParent = new Dictionary<string, List<MijiaDevice>>(StringComparer.Ordinal);

        foreach (var d in _allDevices)
        {
            if (!string.IsNullOrEmpty(d.Did)) byDid[d.Did] = d;
            if (d.IsGroup)
            {
                foreach (var m in d.GroupMembers) groupMembers.Add(m);
            }
            else if (d.IsSplitChild)
            {
                var list = childrenByParent.TryGetValue(d.TargetDid, out var e) ? e : new List<MijiaDevice>();
                childrenByParent[d.TargetDid] = list;
                list.Add(d);
            }
        }

        // 父卡先把键位补齐：规格解析晚到时也照样能逐路控制，不会因为子卡藏起来而丢操作
        foreach (var (parentDid, children) in childrenByParent)
            byDid[parentDid]?.EnsureFallbackChannels(children);

        _visibleDevices = _allDevices.Where(d =>
        {
            if (groupMembers.Contains(d.Did)) return false;
            if (d.IsSplitChild)
            {
                var parent = byDid[d.TargetDid];
                // 父卡确实能逐路下发时才并回去；父卡没有通道就仍单列，避免按键彻底失联
                if (parent != null && parent.Channels.Count > 0) return false;
            }
            if (HideSwitches && d.IsSwitchDevice) return false;
            return true;
        }).ToList();

        // 单独隐藏状态只认 did：设备实例换代（缓存上屏 / 刷新合并）后重新标一遍
        foreach (var d in _visibleDevices)
            d.IsHidden = !string.IsNullOrEmpty(d.Did) && _hiddenDids.Contains(d.Did);

        // 自定义设备图标同样只认 did：实例换代、缓存上屏、刷新合并后都重新挂回设备
        foreach (var d in _allDevices)
            d.ReloadCustomIcon();

        // 自定义色温区间同样只认 did：实例换代、规格重解析后重新套一遍
        foreach (var d in _visibleDevices)
        {
            if (AppSettingsStore.TryGetLightRange(d.Did, out var ctMin, out var ctMax))
                d.ApplyLightRangeOverride(ctMin, ctMax);
            else
                d.ApplyLightRangeOverride(null, null);
        }
    }

    private void ApplyFilter()
    {
        var scoped = _visibleDevices
            .Where(d => (_selectedRoom == "全部" || d.RoomName == _selectedRoom) && MatchesSearch(d))
            .ToList();

        var list = OrderDevices(scoped.Where(d => !d.IsHidden)).ToList();
        var hidden = OrderDevices(scoped.Where(d => d.IsHidden)).ToList();

        // 集合没变就别重建：Clear+Add 会让每张卡片重新绑定数据、重排文字，
        // 而重排会在 Avalonia 的字体缓存里不断新建字体对象（只增不减），
        // 每轮刷新都整批重建的话内存会随刷新次数一直涨。
        if (!SameSequence(Devices, list))
        {
            Devices.Clear();
            foreach (var d in list) Devices.Add(d);
        }

        if (!SameSequence(HiddenDevices, hidden))
        {
            HiddenDevices.Clear();
            foreach (var d in hidden) HiddenDevices.Add(d);
        }

        HasHiddenDevices = hidden.Count > 0;
        HiddenSectionText = $"已隐藏 · {hidden.Count} 台设备";
        // 搜索命中藏在收起区里的设备时自动展开，否则会看起来「搜不到」
        if (hidden.Count > 0 && !string.IsNullOrEmpty(SearchText)) HiddenSectionExpanded = true;
        // 最后一台也恢复了就收起折叠区，下次再隐藏从收起态开始
        if (!HasHiddenDevices) HiddenSectionExpanded = false;

        HasDevices = Devices.Count > 0 || HiddenDevices.Count > 0;
        UpdateBanner();
    }

    private void RebuildHomeGroups()
    {
        // 搜索时一律展开：命中结果藏在收起的房间里会看起来「搜不到」
        var forceExpand = !string.IsNullOrWhiteSpace(SearchText);
        // 房间标题上的灯数按整间房算，不受搜索 / 收起影响：搜到一台设备时也照样知道这屋还有几盏灯亮着
        var litByRoom = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in _visibleDevices)
        {
            if (d.IsOnline && d.IsOn && d.IconKey == "light" && !string.IsNullOrEmpty(d.RoomName))
                litByRoom[d.RoomName] = litByRoom.GetValueOrDefault(d.RoomName) + 1;
        }

        var groups = new List<DeviceGroup>();
        foreach (var g in _visibleDevices
                     .Where(d => !d.IsHidden)
                     .GroupBy(d => d.RoomName)
                     .OrderByDescending(g => g.Count()))
        {
            var items = OrderDevices(g.Where(MatchesSearch)).ToList();
            if (items.Count == 0) continue;
            groups.Add(new DeviceGroup
            {
                Name = g.Key,
                Total = g.Count(),
                Items = items,
                IsExpanded = forceExpand || !_collapsedRooms.Contains(g.Key),
                LightOnCount = litByRoom.GetValueOrDefault(g.Key),
                CustomIcon = CustomIconStore.Get(CustomIconStore.RoomScope, g.Key)
            });
        }

        if (SameGroups(HomeGroups, groups))
            return;

        HomeGroups.Clear();
        foreach (var g in groups) HomeGroups.Add(g);
    }

    /// <summary>两个列表是否同一批实例、同样顺序（用于跳过无意义的重建）。</summary>
    private static bool SameSequence<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : class
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i]))
                return false;
        return true;
    }

    private static bool SameGroups(IReadOnlyList<DeviceGroup> a, List<DeviceGroup> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal) ||
                !SameSequence(a[i].Items, b[i].Items))
                return false;
        return true;
    }

    /// <summary>
    /// 按 did 把新拉到的设备合并进现有列表：同 did 的沿用旧实例、只刷新字段，
    /// 这样已渲染的卡片绑定与文本布局都不用重建。新设备/已移除的设备按需增删。
    /// </summary>
    private static List<MijiaDevice> MergeDevices(List<MijiaDevice> existing, List<MijiaDevice> fresh)
    {
        if (existing.Count == 0) return fresh;

        var byDid = new Dictionary<string, MijiaDevice>(existing.Count, StringComparer.Ordinal);
        foreach (var d in existing)
            if (!string.IsNullOrEmpty(d.Did)) byDid[d.Did] = d;

        var merged = new List<MijiaDevice>(fresh.Count);
        foreach (var d in fresh)
        {
            if (!string.IsNullOrEmpty(d.Did) && byDid.TryGetValue(d.Did, out var keep))
            {
                keep.UpdateFrom(d);
                merged.Add(keep);
            }
            else
            {
                merged.Add(d);
            }
        }
        return merged;
    }

    /// <summary>
    /// 真实设备数：按 did 去重后统计。
    /// 1) 灯组（group.xxx）是虚拟设备，不计自身，只把组内成员按单灯各算一台；
    /// 2) 多键开关拆出的 .sN 虚拟行不计，物理设备由父卡代表；
    /// 3) 隐藏的开关仍算真实设备，口径不受「隐藏开关」设置影响。
    /// </summary>
    private int CountPhysicalDevices()
    {
        var dids = new HashSet<string>(StringComparer.Ordinal);
        var parents = new HashSet<string>(StringComparer.Ordinal);
        var orphanChildren = new HashSet<string>(StringComparer.Ordinal);

        foreach (var d in _allDevices)
        {
            if (d.IsGroup)
            {
                foreach (var m in d.GroupMembers)
                    if (!string.IsNullOrEmpty(m)) dids.Add(m);
                continue;
            }
            if (string.IsNullOrEmpty(d.Did)) continue;
            if (d.IsSplitChild)
            {
                // 父卡不在清单里的极端情况：仍按这台物理设备记一次，别让设备凭空消失
                if (!string.IsNullOrEmpty(d.TargetDid)) orphanChildren.Add(d.TargetDid);
                else dids.Add(d.Did);
                continue;
            }
            parents.Add(d.Did);
            dids.Add(d.Did);
        }

        foreach (var did in orphanChildren)
            if (!parents.Contains(did)) dids.Add(did);

        return dids.Count;
    }

    private void UpdateStats()
    {
        TotalCount = _visibleDevices.Count;
        OnlineCount = _visibleDevices.Count(d => d.IsOnline);
        OfflineCount = TotalCount - OnlineCount;
        TotalDeviceCount = CountPhysicalDevices();
        HasScenes = SceneItems.Count > 0;
        TotalText = $"/ {TotalCount}";
        OfflineText = $"离线 {OfflineCount}";
        HasOffline = OfflineCount > 0;
        // 三张类型卡：数字是「在线且开」，斜杠后是该类型总数，与弹窗清单同一口径
        var lights = _visibleDevices.Where(d => d.IconKey == "light").ToList();
        var curtains = _visibleDevices.Where(d => d.IconKey == "curtain").ToList();
        var acs = _visibleDevices.Where(d => d.IconKey == "ac").ToList();
        LightOnCount = lights.Count(d => d.IsOnline && d.IsOn);
        SyncRoomLightBulbs();
        CurtainOnCount = curtains.Count(d => d.IsOnline && d.IsOn);
        AcOnCount = acs.Count(d => d.IsOnline && d.IsOn);
        LightTotalText = $"/ {lights.Count}";
        CurtainTotalText = $"/ {curtains.Count}";
        AcTotalText = $"/ {acs.Count}";
        RefreshStatList();
        UpdateBanner();
        // 桌面分区按 did 从这里取实例：列表换代后让它们重绑一次（同实例时内部直接跳过）
        RaiseDevicesReloaded();
    }

    /// <summary>
    /// 侧栏房间行右侧的小灯泡 + 首页房间标题上的灯数：哪些房间还有灯开着、各有几盏
    /// （在线且开，与「灯光」统计卡同一口径）。每拍轮询、每次开关灯都会走到这里，
    /// 房间数只十几条，按房间名归拢一次再逐行比对，值没变时 setter 自己会跳过通知，不会白刷界面。
    /// </summary>
    private void SyncRoomLightBulbs()
    {
        _litRooms.Clear();
        _litLightCounts.Clear();
        foreach (var d in _visibleDevices)
        {
            if (!d.IsOnline || !d.IsOn || d.IconKey != "light" || string.IsNullOrEmpty(d.RoomName)) continue;
            _litRooms.Add(d.RoomName);
            _litLightCounts[d.RoomName] = _litLightCounts.GetValueOrDefault(d.RoomName) + 1;
        }

        foreach (var chip in Rooms)
        {
            // 「全部」是伪房间，不对应某一间房，永远不点亮
            if (chip.IsLocked) chip.HasLightOn = false;
            else chip.HasLightOn = _litRooms.Contains(chip.Name);
        }

        // 首页每个房间标题：灯数变化才刷新，收起状态下也照常显示
        foreach (var group in HomeGroups)
            group.LightOnCount = _litLightCounts.GetValueOrDefault(group.Name);
    }

    /// <summary>
    /// 同步统计卡弹窗的清单（弹窗开着时轮询与开关动作都会走到这里，设备状态变了列表跟着变）。
    /// 同实例同顺序时不重建：Clear+Add 会让每张卡重新绑定并重排文字，
    /// 而重排在字体缓存里只增不减，每轮刷新整批重建会把内存一直顶上去。
    /// </summary>
    private void RefreshStatList()
    {
        var kind = _statListKind;
        var list = kind switch
        {
            StatListKind.Lights => OnFirst("light", _visibleDevices),
            StatListKind.Curtains => OnFirst("curtain", _visibleDevices),
            StatListKind.Ac => OnFirst("ac", _visibleDevices),
            _ => OrderDevices(_visibleDevices.Where(d => !d.IsOnline)).ToList(),
        };
        if (!SameSequence(StatDevices, list))
        {
            StatDevices.Clear();
            foreach (var d in list) StatDevices.Add(d);
        }

        HasStatDevices = list.Count > 0;
        StatListTitle = kind switch
        {
            StatListKind.Lights => "全屋灯光",
            StatListKind.Curtains => "全屋窗帘",
            StatListKind.Ac => "全屋空调",
            _ => "离线设备",
        };
        // 量词与动词按类型走：写死一个会出现「3 台窗帘」「开窗帘的电源键」
        var unit = kind switch { StatListKind.Lights => "盏", StatListKind.Curtains => "副", _ => "台" };
        var verb = kind switch { StatListKind.Lights => "开关灯", StatListKind.Curtains => "开合", StatListKind.Ac => "开关", _ => "控制" };
        StatListSubtitle = HasStatDevices
            ? kind == StatListKind.Offline
                ? $"共 {list.Count} 台 · 云端失联，指令下发不会生效"
                : $"共 {list.Count} {unit} · 开着的排在上面，点卡片看详情，点电源键{verb}"
            : null;
        StatListEmptyText = kind switch
        {
            StatListKind.Lights => "家里还没有识别到灯",
            StatListKind.Curtains => "家里还没有识别到窗帘",
            StatListKind.Ac => "家里还没有识别到空调",
            _ => "所有设备都在线，暂无离线设备",
        };

        // 「全关」按开着的在线设备计数：一台都不开着时按钮收起，点完立刻消失就是「都关了」的反馈
        var onCount = list.Count(d => d.IsOnline && d.IsOn);
        CanCloseAllStatDevices = kind != StatListKind.Offline && onCount > 0;
        CloseAllStatTip = kind switch
        {
            StatListKind.Lights => $"把开着的 {onCount} 盏灯全部关掉",
            StatListKind.Curtains => $"把开着的 {onCount} 副窗帘全部合上",
            StatListKind.Ac => $"把开着的 {onCount} 台空调全部关掉",
            _ => "",
        };
    }

    /// <summary>
    /// 取某个设备图标 key 对应的设备清单：开着的排在上边（与统计卡数字同一口径：在线且开），
    /// OrderBy 稳定，两组内部都保持原来的拼音序。
    /// </summary>
    private static List<MijiaDevice> OnFirst(string iconKey, IEnumerable<MijiaDevice> src) =>
        OrderDevices(src.Where(d => d.IconKey == iconKey))
            .OrderBy(d => d.IsOnline && d.IsOn ? 0 : 1)
            .ToList();

    private void UpdateBanner()
    {
        var list = _selectedRoom == "全部"
            ? _visibleDevices
            : _visibleDevices.Where(d => d.RoomName == _selectedRoom).ToList();
        BannerTitle = _selectedRoom == "全部" ? "全屋设备" : _selectedRoom;
        BannerDesc = _selectedRoom == "全部"
            ? "统一管理家中所有智能设备与传感器"
            : $"{_selectedRoom} 内的设备与状态";
        BannerIconKey = Rooms.FirstOrDefault(c => c.Name == _selectedRoom)?.IconKey ?? "house";
        BannerCustomIcon = Rooms.FirstOrDefault(c => c.Name == _selectedRoom)?.CustomIcon;
        BannerTotal = list.Count;
        BannerOn = list.Count(d => d.IsOn);
        BannerOffline = list.Count(d => !d.IsOnline);
        BannerLightOn = list.Count(d => d.IconKey == "light" && d.IsOnline && d.IsOn);
    }

    /// <summary>
    /// 环境概览：优先取一台同时有温湿度的在线设备作为展示来源，否则对全屋读数取平均。
    /// 明细里列出每台有读数的设备，耗材单独在 <see cref="RebuildConsumables"/> 汇总。
    /// </summary>
    private void RecomputeEnvironment()
    {
        // 环境概览只统计温湿度读数；插座外显的功率不算环境读数；
        // 热水器/净水器的 temperature 是水温，也不进室温口径
        var readings = _allDevices
            .Where(d => !d.IsWaterDevice && (d.TemperatureC.HasValue || d.Humidity.HasValue))
            .ToList();
        HasEnvironment = readings.Count > 0;

        EnvReadings.Clear();
        foreach (var d in readings.OrderBy(d => d.RoomName).ThenBy(d => d.Name, DeviceNameComparer))
        {
            var itemTemp = d.TemperatureC;
            var itemHum = d.Humidity;
            EnvReadings.Add(new EnvReadingItem
            {
                Title = d.Name,
                Subtitle = string.IsNullOrEmpty(d.RoomName) ? "未分配房间" : d.RoomName,
                // 温度是主读数；只有湿度时退回湿度，温湿度都有时湿度作次要读数
                Value = itemTemp.HasValue ? $"{itemTemp.Value:0.#}℃" : itemHum.HasValue ? $"{itemHum.Value:0.#}%" : d.MetricsText,
                Extra = itemTemp.HasValue && itemHum.HasValue ? $"{itemHum.Value:0.#}%" : ""
            });
        }
        EnvReadingsCountText = readings.Count > 0 ? $"{readings.Count} 台设备" : "";

        var source = _allDevices.FirstOrDefault(d =>
            d.IsOnline && !d.IsWaterDevice && d.TemperatureC.HasValue && d.Humidity.HasValue);
        double? temp = source?.TemperatureC;
        double? hum = source?.Humidity;
        if (source == null)
        {
            var temps = _allDevices.Where(d => !d.IsWaterDevice && d.TemperatureC.HasValue)
                .Select(d => d.TemperatureC!.Value).ToList();
            var hums = _allDevices.Where(d => !d.IsWaterDevice && d.Humidity.HasValue)
                .Select(d => d.Humidity!.Value).ToList();
            temp = temps.Count > 0 ? temps.Average() : null;
            hum = hums.Count > 0 ? hums.Average() : null;
        }

        EnvTemperatureText = temp.HasValue ? $"{temp.Value:0.#}℃" : "—";
        EnvHumidityText = hum.HasValue ? $"{hum.Value:0.#}%" : "—";
        EnvTemperatureValue = temp.HasValue ? $"{temp.Value:0.#}" : "—";
        EnvTemperatureUnit = temp.HasValue ? "℃" : "";
        EnvHumidityValue = hum.HasValue ? $"{hum.Value:0.#}" : "—";
        EnvHumidityUnit = hum.HasValue ? "%" : "";
        EnvSourceText = source != null
            ? $"数据来自 {source.Name}"
            : (temp.HasValue || hum.HasValue ? "全屋读数平均" : "");

        // 首页趋势卡与概览共用同一份读数；每拍都调，内部按 5 分钟一格节流
        _liveTemp = temp;
        _liveHumidity = hum;
        SampleTrendNow();
        _ = RefreshEnergyStatsAsync();  // 云端电量统计 10 分钟刷一次，内部自己节流
    }

    /// <summary>全屋耗材汇总：不足的排前面，同组内余量低的更靠前。</summary>
    private void RebuildConsumables()
    {
        var items = new List<ConsumableItem>();
        foreach (var d in _allDevices)
            foreach (var c in d.Consumables.Where(c => c.HasValue))
                items.Add(new ConsumableItem
                {
                    DeviceName = d.Name,
                    Desc = c.Desc,
                    ValueText = c.ValueText,
                    Percent = c.Percent,
                    IsLow = c.IsLow
                });

        items.Sort((a, b) =>
        {
            if (a.IsLow != b.IsLow) return a.IsLow ? -1 : 1;
            return a.Percent.CompareTo(b.Percent);
        });

        ConsumableItems.Clear();
        foreach (var i in items) ConsumableItems.Add(i);
        HasConsumables = items.Count > 0;
        ConsumableLowCount = items.Count(i => i.IsLow);
        HasConsumableWarning = ConsumableLowCount > 0;
    }

    /// <summary>环境概览「查看明细」：用弹窗展示逐设备读数与耗材，避免把首页撑高。</summary>
    [RelayCommand]
    private void OpenEnvDetail() => IsEnvDetailOpen = true;

    [RelayCommand]
    private void CloseEnvDetail() => IsEnvDetailOpen = false;

    /// <summary>「灯光」卡：弹窗列全屋灯（含关着的，便于直接开灯）。</summary>
    [RelayCommand]
    private void OpenLightList() => OpenStatList(StatListKind.Lights);

    /// <summary>「窗帘」卡：同一个弹窗，换成全屋窗帘。</summary>
    [RelayCommand]
    private void OpenCurtainList() => OpenStatList(StatListKind.Curtains);

    /// <summary>「空调」卡：同一个弹窗，换成全屋空调。</summary>
    [RelayCommand]
    private void OpenAcList() => OpenStatList(StatListKind.Ac);

    /// <summary>「在线设备」卡：弹窗列离线设备。</summary>
    [RelayCommand]
    private void OpenOfflineList() => OpenStatList(StatListKind.Offline);

    private void OpenStatList(StatListKind kind)
    {
        _statListKind = kind;
        // 口径先落一次，卡片与弹窗同一时刻打开时不闪旧数据
        RefreshStatList();
        IsStatListOpen = true;
    }

    [RelayCommand]
    private void CloseStatList() => IsStatListOpen = false;

    /// <summary>
    /// 标题栏「全关」：把清单里开着的在线设备逐台关掉。先取快照，因为每台下发完都会重排清单
    /// （开着的排上面，弹窗开着时跟着轮询同步）；串行下发，别把批量请求一次砸给云端。
    /// </summary>
    [RelayCommand]
    private async Task CloseAllStatDevices()
    {
        var targets = StatDevices.Where(d => d.IsOnline && d.IsOn).ToList();
        foreach (var device in targets)
            await TurnOffDeviceAsync(device);
    }

    /// <summary>
    /// 首页某个房间标题上的「一键全关」：只关这一间里开着的灯（在线且开），其他设备不动。
    /// 取快照再逐台下发，开关过程中房间标题的灯数会跟着一起回落。
    /// </summary>
    [RelayCommand]
    private async Task TurnOffRoomLights(DeviceGroup? group)
    {
        if (group == null) return;
        await TurnOffLightsAsync(_visibleDevices.Where(d => d.RoomName == group.Name));
    }

    /// <summary>房间页横幅上的「一键全关」：当前房间（「全部」时为全屋）里开着的灯全部关掉。</summary>
    [RelayCommand]
    private async Task TurnOffSelectedRoomLights()
    {
        await TurnOffLightsAsync(_selectedRoom == "全部"
            ? _visibleDevices
            : _visibleDevices.Where(d => d.RoomName == _selectedRoom));
    }

    private async Task TurnOffLightsAsync(IEnumerable<MijiaDevice> scope)
    {
        var targets = scope.Where(d => d.IconKey == "light" && d.IsOnline && d.IsOn).ToList();
        foreach (var device in targets)
            await TurnOffDeviceAsync(device);
    }

    [RelayCommand]
    private void PickRoom(RoomChip? chip)
    {
        if (chip == null) return;
        _selectedRoom = chip.Name;
        SelectedNavIndex = 1;
        SyncRoomChips();
        ApplyFilter();
    }

    // ===== 自定义房间 / 场景图标（右键菜单与房间横幅共用一套入口） =====

    /// <summary>视图订阅后弹出图标选择窗；视图模型只负责说明「给谁换」。</summary>
    public event EventHandler<CustomIconTarget>? IconEditorRequested;

    [RelayCommand]
    private void EditRoomIcon(RoomChip? chip)
    {
        if (chip == null) return;
        IconEditorRequested?.Invoke(this, new CustomIconTarget(
            CustomIconStore.RoomScope, chip.Name, $"房间图标 · {chip.Name}"));
    }

    /// <summary>房间页横幅图标直接点一下：给当前选中的房间换图。</summary>
    [RelayCommand]
    private void EditSelectedRoomIcon()
    {
        var chip = Rooms.FirstOrDefault(c => c.Name == _selectedRoom);
        if (chip == null) return;
        EditRoomIcon(chip);
    }

    /// <summary>房间页横幅右键：当前房间回到默认画法。</summary>
    [RelayCommand]
    private void ResetSelectedRoomIcon()
    {
        var chip = Rooms.FirstOrDefault(c => c.Name == _selectedRoom);
        if (chip == null || !chip.HasCustomIcon) return;
        if (CustomIconStore.Clear(CustomIconStore.RoomScope, chip.Name))
            Log($"「{chip.Name}」已恢复默认图标");
    }

    /// <summary>设备详情抽屉里的图标入口：按 did 换图，设备卡 / 桌面组件会一起更新。</summary>
    [RelayCommand]
    private void EditDrawerDeviceIcon()
    {
        var device = DrawerDevice;
        if (device == null || string.IsNullOrEmpty(device.Did)) return;
        IconEditorRequested?.Invoke(this, new CustomIconTarget(
            CustomIconStore.DeviceScope, device.Did, $"设备图标 · {device.Name}"));
    }

    /// <summary>设备详情右键：这台设备回到默认设备图 / 矢量图标。</summary>
    [RelayCommand]
    private void ResetDrawerDeviceIcon()
    {
        var device = DrawerDevice;
        if (device == null || !device.HasCustomIcon || string.IsNullOrEmpty(device.Did)) return;
        if (CustomIconStore.Clear(CustomIconStore.DeviceScope, device.Did))
            Log($"「{device.Name}」已恢复默认图标");
    }

    [RelayCommand]
    private void EditSceneIcon(SceneCardItem? item)
    {
        if (item == null || string.IsNullOrEmpty(item.Scene.SceneId)) return;
        var kind = item.IsAutomation ? "自动化图标" : "场景图标";
        IconEditorRequested?.Invoke(this, new CustomIconTarget(
            CustomIconStore.SceneScope, item.Scene.SceneId, $"{kind} · {item.Name}"));
    }

    /// <summary>编辑弹窗基础信息卡里的图标入口：改的仍是当前这条场景的 scene_id。</summary>
    [RelayCommand]
    private void EditEditingSceneIcon()
    {
        if (!SceneEditCanChangeIcon || _editingScene is null) return;
        var kind = IsAutomationEditor ? "自动化图标" : "场景图标";
        var name = string.IsNullOrWhiteSpace(SceneEditName) ? _editingScene.Name : SceneEditName.Trim();
        IconEditorRequested?.Invoke(this, new CustomIconTarget(
            CustomIconStore.SceneScope, _editingScene.SceneId, $"{kind} · {name}", FromEditor: true));
    }

    /// <summary>编辑弹窗基础信息卡右键：当前场景回到默认画法。</summary>
    [RelayCommand]
    private void ResetEditingSceneIcon()
    {
        if (!SceneEditCanChangeIcon || _editingScene is null) return;
        if (string.IsNullOrEmpty(_editingScene.SceneId)) return;
        var name = string.IsNullOrWhiteSpace(SceneEditName) ? _editingScene.Name : SceneEditName.Trim();
        if (CustomIconStore.Clear(CustomIconStore.SceneScope, _editingScene.SceneId))
            Log($"「{name}」已恢复默认图标");
    }

    [RelayCommand]
    private void ResetRoomIcon(RoomChip? chip)
    {
        if (chip == null || !chip.HasCustomIcon) return;
        if (CustomIconStore.Clear(CustomIconStore.RoomScope, chip.Name))
            Log($"「{chip.Name}」已恢复默认图标");
    }

    [RelayCommand]
    private void ResetSceneIcon(SceneCardItem? item)
    {
        if (item == null || !item.HasCustomIcon) return;
        if (CustomIconStore.Clear(CustomIconStore.SceneScope, item.Scene.SceneId))
            Log($"「{item.Name}」已恢复默认图标");
    }

    /// <summary>图标存储变更：把同一房间 / 场景的所有展示位换成新图标，不重建列表。</summary>
    private void OnCustomIconChanged(object? sender, CustomIconChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnCustomIconChanged(sender, e));
            return;
        }

        if (e.Scope == CustomIconStore.DeviceScope)
        {
            foreach (var d in _allDevices)
                if (string.Equals(d.Did, e.Key, StringComparison.Ordinal))
                    d.ReloadCustomIcon();

            // 抽屉里的设备可能不在当前过滤列表里，单独兜一次
            if (DrawerDevice is { } drawer &&
                string.Equals(drawer.Did, e.Key, StringComparison.Ordinal))
                drawer.ReloadCustomIcon();
            return;
        }

        if (e.Scope == CustomIconStore.RoomScope)
        {
            foreach (var chip in Rooms)
                if (string.Equals(chip.Name, e.Key, StringComparison.Ordinal))
                    chip.ReloadCustomIcon();

            foreach (var group in HomeGroups)
                if (string.Equals(group.Name, e.Key, StringComparison.Ordinal))
                    group.ReloadCustomIcon();

            if (string.Equals(_selectedRoom, e.Key, StringComparison.Ordinal))
                BannerCustomIcon = Rooms.FirstOrDefault(c => c.Name == _selectedRoom)?.CustomIcon;
            return;
        }

        if (e.Scope != CustomIconStore.SceneScope) return;

        // 同一张卡可能同时挂在完整列表和展示列表里：按实例去重，避免重复刷新
        var seen = new HashSet<SceneCardItem>();
        foreach (var list in new[] { SceneItems, AutomationItems, DrawerScenes, DrawerAutomations })
            foreach (var card in list)
            {
                if (!string.Equals(card.Scene.SceneId, e.Key, StringComparison.Ordinal)) continue;
                if (seen.Add(card)) card.ReloadCustomIcon();
            }

        // 编辑弹窗开着同一条场景时，基础信息卡里的图标也要立即换
        if (_editingScene is { } editing &&
            string.Equals(editing.SceneId, e.Key, StringComparison.Ordinal))
            SceneEditCustomIcon = CustomIconStore.Get(CustomIconStore.SceneScope, e.Key);
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    /// <summary>关于页「赞赏支持」：只打开收款码弹窗，不发任何网络请求。</summary>
    [RelayCommand]
    private void OpenSupport() => IsSupportOpen = true;

    [RelayCommand]
    private void CloseSupport() => IsSupportOpen = false;

    [RelayCommand]
    private void OpenLog() => IsLogOpen = true;

    [RelayCommand]
    private void CloseOverlays()
    {
        // 关掉登录面板就打断长轮询，否则命令一直执行中，再点登录会「无反应」
        if (IsLoginOpen) CancelAuth();
        // 抽屉收起就不再需要画面：解码线程和内置画面源一起收掉，别在后台吃 CPU
        if (IsCameraLive) StopCameraLiveCore("已停止内置画面");
        if (IsDrawerOpen) ClearSpecPanel();
        IsDrawerOpen = false;
        IsLoginOpen = false;
        IsSupportOpen = false;
        IsSettingsOpen = false;
        IsEnvDetailOpen = false;
        IsTrendCardEditOpen = false;
        IsStatListOpen = false;
        IsVoiceOpen = false;
        IsMusicOpen = false;
        IsHomeOpen = false;
        IsLogOpen = false;
    }

    [RelayCommand]
    private void ToggleTheme() => IsDark = !IsDark;

    [RelayCommand]
    private async Task RunSceneAsync(SceneCardItem? item)
    {
        if (_client == null || item == null) return;
        item.IsRunning = true;
        try
        {
            await _client.RunSceneAsync(item.Scene);
            Log($"场景「{item.Name}」已执行");
            await RefreshStatesQuietAsync();
        }
        catch (Exception ex)
        {
            Log($"场景执行失败：{ex.Message}");
        }
        finally
        {
            item.IsRunning = false;
        }
    }

    /// <summary>
    /// 只对指定设备执行这条场景里的动作：逐条 set_prop，不下发整条场景、不改云端定义。
    /// 适用于「我只想开这盏灯」，也避免场景里其他设备离线导致整条失败。
    /// </summary>
    [RelayCommand]
    private async Task RunSceneOnDeviceAsync(SceneCardItem? item)
    {
        if (_client == null || item == null) return;
        var did = item.TargetDid ?? DrawerDevice?.TargetDid;
        if (string.IsNullOrEmpty(did)) return;

        item.IsRunning = true;
        try
        {
            var (applied, skipped) = await _client.RunSceneOnDeviceAsync(item.Scene, did);
            var who = DeviceNameOf(did);
            Log(skipped > 0
                ? $"已对「{who}」单独执行场景「{item.Name}」的 {applied} 项动作（跳过 {skipped} 项无法单拆的动作）"
                : $"已对「{who}」单独执行场景「{item.Name}」的 {applied} 项动作");
            await RefreshStatesQuietAsync();
        }
        catch (Exception ex)
        {
            Log($"单设备执行失败：{ex.Message}");
        }
        finally
        {
            item.IsRunning = false;
        }
    }

    /// <summary>
    /// 启停自动化：先乐观更新开关，失败回滚。
    /// 走 SetSceneStatus（探针已确认接口），只动 enable 一个字段，不碰场景的动作定义。
    /// </summary>
    [RelayCommand]
    private async Task ToggleAutomationAsync(SceneCardItem? item)
    {
        if (_client == null || item == null || !item.IsAutomation) return;

        var target = !item.IsEnabled;
        item.IsEnabled = target;
        _togglingScenes.Add(item.Scene.SceneId);
        try
        {
            await _client.SetSceneEnabledAsync(item.Scene, target);
            item.Scene.Enable = target;
            Log($"自动化「{item.Name}」已{(target ? "开启" : "关闭")}");
            RaiseSceneCounters();
        }
        catch (Exception ex)
        {
            item.IsEnabled = !target;
            Log($"「{item.Name}」启停失败：{ex.Message}");
        }
        finally
        {
            _togglingScenes.Remove(item.Scene.SceneId);
        }
    }

    // ===== 启停态同步（手机端改动 → 本应用界面） =====

    /// <summary>正在切换中的场景 id：同步时跳过，避免把本地乐观态覆盖回去。</summary>
    private readonly HashSet<string> _togglingScenes = new(StringComparer.Ordinal);

    /// <summary>同步最小间隔：设备状态 10 秒一拍，场景启停态没必要跟着这么勤。</summary>
    private static readonly TimeSpan SceneSyncInterval = TimeSpan.FromSeconds(30);

    private DateTime _lastSceneSyncUtc = DateTime.MinValue;
    private bool _sceneSyncing;

    /// <summary>只有智能页或设备抽屉在前台时才需要对齐启停态，其它页面看不到这些开关。</summary>
    private bool IsSceneSurfaceActive => SelectedNavIndex == 2 || IsDrawerOpen;

    /// <summary>
    /// 把 Scene.Enable 与卡片开关对齐到云端。只改状态字段，不重建列表、不触发重建动画。
    /// force=true 用于切到智能页 / 从托盘恢复时立刻对齐一次，不受最小间隔限制。
    /// </summary>
    private async Task SyncSceneEnableStatesAsync(bool force)
    {
        if (_client == null || !IsLoggedIn) return;
        if (SceneItems.Count == 0 && AutomationItems.Count == 0) return;
        if (_sceneSyncing) return;
        if (!force && DateTime.UtcNow - _lastSceneSyncUtc < SceneSyncInterval) return;

        _sceneSyncing = true;
        try
        {
            var map = await _client.GetSceneEnableMapAsync();
            if (map.Count == 0) return;
            _lastSceneSyncUtc = DateTime.UtcNow;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var scene in _scenes)
                {
                    if (_togglingScenes.Contains(scene.SceneId)) continue;
                    if (!map.TryGetValue(scene.SceneId, out var enable) || scene.Enable == enable) continue;
                    scene.Enable = enable;
                }

                // 卡片开关一律跟 Scene.Enable 对齐（含多份实例：列表 / 抽屉 / 按设备分组）
                foreach (var card in SceneItems.Concat(AutomationItems)
                             .Concat(DrawerScenes).Concat(DrawerAutomations))
                {
                    if (_togglingScenes.Contains(card.Scene.SceneId)) continue;
                    card.IsEnabled = card.Scene.Enable;
                }
            });
        }
        catch
        {
            // 同步失败保持现有状态，等下一拍，不打扰用户
        }
        finally
        {
            _sceneSyncing = false;
        }
    }

    // ===================================================================
    // 自动化编辑（写云端，接口已实测：AppSceneService/Edit + Delete）
    // 报文 = GetSceneList 返回的整条原始对象，改动就地写回后原样提交。
    // ===================================================================

    public ObservableCollection<SceneEditActionRow> SceneEditRows { get; } = new();

    [ObservableProperty] private bool _isSceneEditorOpen;

    /// <summary>场景详情是否嵌在主窗口里（false = 从桌面设备弹窗发起的独立弹窗，主窗口不参与）。</summary>
    [ObservableProperty] private bool _isSceneEditorInline = true;
    [ObservableProperty] private string _sceneEditName = "";
    [ObservableProperty] private bool _sceneEditEnabled;
    [ObservableProperty] private string _sceneEditTrigger = "";
    [ObservableProperty] private string _sceneEditCondition = "";
    [ObservableProperty] private string _sceneEditActionCount = "";
    [ObservableProperty] private string _sceneEditNote = "";
    [ObservableProperty] private bool _sceneEditBusy;
    [ObservableProperty] private bool _sceneEditConfirmDelete;

    /// <summary>编辑弹窗基础信息卡左上角的图标预览：有自定义图就用它，没有就跟随名称画默认线稿。</summary>
    [ObservableProperty] private CustomIcon? _sceneEditCustomIcon;

    public bool SceneEditHasCustomIcon => SceneEditCustomIcon is not null;

    public double SceneEditDefaultIconOpacity => SceneEditCustomIcon is null ? 1 : 0;

    public Geometry SceneEditIconGeometry => SceneCardItem.SceneIconFor(SceneEditName);

    /// <summary>新建的场景还没有 scene_id，图标没处可存；先禁用入口，保存后回到智能页再换。</summary>
    public bool SceneEditCanChangeIcon =>
        IsSceneEditMode && !string.IsNullOrEmpty(_editingScene?.SceneId);

    public string SceneEditIconTip => SceneEditCanChangeIcon
        ? "点击给这个场景换一张图标（SVG 代码 / .svg 文件 / PNG）"
        : "保存到米家后可以设置场景图标";

    partial void OnSceneEditCustomIconChanged(CustomIcon? value)
    {
        OnPropertyChanged(nameof(SceneEditHasCustomIcon));
        OnPropertyChanged(nameof(SceneEditDefaultIconOpacity));
    }

    partial void OnSceneEditNameChanged(string value)
    {
        // 名称决定默认线稿（回家 / 睡眠 / 观影…），输入时预览也要跟着换
        OnPropertyChanged(nameof(SceneEditIconGeometry));
    }

    /// <summary>执行顺序列表是否展开（标题行点击可折叠，长自动化面板不用一直滚）。</summary>
    [ObservableProperty] private bool _isSceneOrderExpanded = true;

    [RelayCommand]
    private void ToggleSceneOrder() => IsSceneOrderExpanded = !IsSceneOrderExpanded;

    // ===== 触发条件编辑（按设备 MIoT 规格选择，写回 scene_trigger） =====

    /// <summary>已生效的触发条件列表（定时 / 手动类只读）。</summary>
    public ObservableCollection<SceneTriggerRow> SceneTriggerRows { get; } = new();

    [ObservableProperty] private bool _isTriggerEditorOpen;
    [ObservableProperty] private string _triggerEditorTitle = "添加触发条件";
    [ObservableProperty] private bool _triggerEditorBusy;
    [ObservableProperty] private string _triggerCount = "";
    [ObservableProperty] private bool _canEditTrigger;

    /// <summary>状态条件（scene_condition）本应用仍只读，摊成一句话给人看。</summary>
    [ObservableProperty] private string _sceneStateConditionText = "";
    public bool HasStateCondition => !string.IsNullOrEmpty(SceneStateConditionText);
    partial void OnSceneStateConditionTextChanged(string value) => OnPropertyChanged(nameof(HasStateCondition));

    /// <summary>第一级：可选设备。</summary>
    public ObservableCollection<OptionItem> TriggerDeviceOptions { get; } = new();
    [ObservableProperty] private object? _selectedTriggerDeviceValue;

    /// <summary>第零级：触发类型（设备触发 / 定时触发）。</summary>
    public ObservableCollection<OptionItem> TriggerKindOptions { get; } = new();
    [ObservableProperty] private object? _selectedTriggerKindValue;

    /// <summary>当前选的是不是定时触发（弹窗里两套表单按它切换）。</summary>
    public bool IsTimerTriggerKind =>
        string.Equals(SelectedTriggerKindValue?.ToString(), "timer", StringComparison.Ordinal);

    partial void OnSelectedTriggerKindValueChanged(object? value)
    {
        OnPropertyChanged(nameof(IsTimerTriggerKind));
        if (_fillingTrigger) return;

        if (IsTimerTriggerKind)
        {
            UpdateTriggerPreview();
            return;
        }

        // 切回设备触发：设备与条件清单可能还没载过（编辑定时条目进来又切回设备）
        if (_triggerDevice == null)
        {
            var did = SelectedTriggerDeviceValue?.ToString()
                      ?? TriggerDeviceOptions.FirstOrDefault()?.Value?.ToString() ?? "";
            if (did.Length == 0) return;

            _fillingTrigger = true;
            SelectedTriggerDeviceValue = did;
            _fillingTrigger = false;
            _ = LoadTriggerConditionsAsync(did, null);
        }
        else
        {
            UpdateTriggerPreview();
        }
    }

    /// <summary>定时触发的时刻（TimePicker 双向绑定）。</summary>
    [ObservableProperty] private TimeSpan? _timerTime = new TimeSpan(8, 0, 0);

    /// <summary>定时触发的重复：0=每天 8=工作日 9=周末 1–7=单个星期几（见 SceneTriggerCatalog）。</summary>
    public ObservableCollection<OptionItem> TimerRepeatOptions { get; } = new();
    [ObservableProperty] private object? _selectedTimerRepeatValue;

    partial void OnTimerTimeChanged(TimeSpan? value)
    {
        if (!_fillingTrigger && IsTimerTriggerKind) UpdateTriggerPreview();
    }

    partial void OnSelectedTimerRepeatValueChanged(object? value)
    {
        if (!_fillingTrigger && IsTimerTriggerKind) UpdateTriggerPreview();
    }

    private int TimerDow =>
        SelectedTimerRepeatValue is { } v && int.TryParse(v.ToString(), out var d) ? d : 0;

    /// <summary>① 设备行上显示的名字（弹窗选完设备后只看得见这一行，名字得常显）。</summary>
    [ObservableProperty] private string _triggerDeviceLabel = "选择设备";

    /// <summary>「选择设备」弹窗的数据源：与 ① 行的候选同源（有 did 的都算）。</summary>
    public IReadOnlyList<MijiaDevice> TriggerDeviceCandidates =>
        _allDevices.Where(d => !string.IsNullOrEmpty(d.Did)).ToList();

    /// <summary>第二级：该设备可用的条件（事件 + 属性）。</summary>
    public ObservableCollection<OptionItem> TriggerConditionOptions { get; } = new();
    [ObservableProperty] private object? _selectedTriggerConditionKey;

    /// <summary>第三级：事件出参 / 属性取值的筛选片。</summary>
    public ObservableCollection<TriggerArgGroup> TriggerArgGroups { get; } = new();
    [ObservableProperty] private bool _hasTriggerArgs;

    /// <summary>第三级标题：事件是「可选的筛选」，属性是「必选的取值」。</summary>
    [ObservableProperty] private string _triggerArgSectionTitle = "④ 筛选（可不选＝任意）";

    [ObservableProperty] private string _triggerPreview = "";

    /// <summary>正在编辑第几条（-1 = 新增）。</summary>
    private int _editingTriggerIndex = -1;

    private MijiaDevice? _triggerDevice;
    private List<TriggerOption> _triggerOptions = new();

    /// <summary>程序内回填时不要触发「换设备 → 重拉条件」这类联动，避免把刚填好的状态清掉。</summary>
    private bool _fillingTrigger;

    partial void OnSelectedTriggerDeviceValueChanged(object? value)
    {
        var did = value?.ToString() ?? "";
        TriggerDeviceLabel = TriggerDeviceOptions
            .FirstOrDefault(o => string.Equals(o.Value?.ToString(), did, StringComparison.Ordinal))?.Label
            ?? "选择设备";

        if (_fillingTrigger) return;
        _ = LoadTriggerConditionsAsync(did, null);
    }

    partial void OnSelectedTriggerConditionKeyChanged(object? value)
    {
        if (_fillingTrigger) return;
        BuildTriggerArgs(value?.ToString() ?? "", null);
    }

    /// <summary>打开编辑器：index = -1 新增，否则载入该条已有条件。</summary>
    [RelayCommand]
    private Task AddTrigger() => BeginTriggerEditAsync(-1);

    [RelayCommand]
    private Task EditTrigger(SceneTriggerRow? row) =>
        row is { Editable: true } ? BeginTriggerEditAsync(row.Index) : Task.CompletedTask;

    /// <summary>
    /// 编辑器可以展示时请求宿主弹出窗口（由视图订阅后 ShowDialog）。
    /// 视图模型不直接引用窗口类型，弹窗逻辑只落在 View 侧。
    /// </summary>
    public event EventHandler? TriggerEditorOpened;

    private async Task BeginTriggerEditAsync(int index)
    {
        try
        {
            await BeginTriggerEditCoreAsync(index);
        }
        catch (Exception ex)
        {
            // 命令里的异常不会崩进程，只会让「添加条件」看起来毫无反应——必须让它可见
            Log($"打开触发条件编辑器失败：{ex.Message}");
            IsTriggerEditorOpen = false;
        }
    }

    private async Task BeginTriggerEditCoreAsync(int index)
    {
        if (_client == null) { Log("添加条件：客户端还没就绪，先等设备列表加载完"); return; }
        if (_editingScene?.Raw == null) { Log("添加条件：当前没有可编辑的场景报文"); return; }

        _editingTriggerIndex = index;
        TriggerEditorTitle = index >= 0 ? "修改触发条件" : "添加触发条件";
        TriggerArgGroups.Clear();
        HasTriggerArgs = false;
        TriggerPreview = "";

        // 第零级：触发类型；第一级：可做触发的设备（有规格、且不是纯虚拟组）
        if (TriggerKindOptions.Count == 0)
        {
            TriggerKindOptions.Add(new OptionItem { Label = "设备触发", Value = "device" });
            TriggerKindOptions.Add(new OptionItem { Label = "定时", Value = "timer" });
        }
        if (TimerRepeatOptions.Count == 0)
        {
            TimerRepeatOptions.Add(new OptionItem { Label = "每天", Value = SceneTriggerCatalog.DowEveryDay });
            TimerRepeatOptions.Add(new OptionItem { Label = "工作日（周一至周五）", Value = SceneTriggerCatalog.DowWorkday });
            TimerRepeatOptions.Add(new OptionItem { Label = "周末", Value = SceneTriggerCatalog.DowWeekend });
            for (var d = 1; d <= 7; d++)
                TimerRepeatOptions.Add(new OptionItem { Label = SceneTriggerCatalog.WeekDayName(d), Value = d });
        }

        TriggerDeviceOptions.Clear();
        foreach (var d in _allDevices.Where(d => !string.IsNullOrEmpty(d.Did)))
        {
            TriggerDeviceOptions.Add(new OptionItem
            {
                Label = d.Name,
                Value = d.Did,
                Meta = string.IsNullOrEmpty(d.RoomName) ? null : d.RoomName
            });
        }

        JsonObject? node = null;
        if (index >= 0)
        {
            var arr = TriggerArray(create: false);
            if (arr != null && index < arr.Count && arr[index] is JsonObject exist) node = exist;
        }

        // 定时触发：不挑设备，直接回填时间与重复
        if (node != null && string.Equals(node["src"]?.ToString(), "timer", StringComparison.Ordinal))
        {
            var (hour, minute, dow) = SceneTriggerCatalog.ParseTimerNode(node);
            _triggerDevice = null;
            _fillingTrigger = true;
            SelectedTriggerKindValue = "timer";
            TimerTime = new TimeSpan(hour, minute, 0);
            SelectedTimerRepeatValue = dow;
            _fillingTrigger = false;

            UpdateTriggerPreview();
            IsTriggerEditorOpen = true;
            TriggerEditorOpened?.Invoke(this, EventArgs.Empty);
            return;
        }

        _fillingTrigger = true;
        SelectedTriggerKindValue = "device";
        _fillingTrigger = false;

        var did = node?["extra_json"]?["did"]?.ToString() ?? "";
        var target = _allDevices.FirstOrDefault(d => MijiaScene.SameDid(d.Did, did))
                     ?? _allDevices.FirstOrDefault(d => d.Did == TriggerDeviceOptions.FirstOrDefault()?.Value?.ToString());

        _fillingTrigger = true;
        SelectedTriggerDeviceValue = target?.Did;
        _fillingTrigger = false;

        if (target == null)
        {
            Log("没有可用于触发条件的设备");
            IsTriggerEditorOpen = false;
            return;
        }

        // 先开窗（标题、设备清单已就绪），条件清单在窗内异步补齐
        IsTriggerEditorOpen = true;
        TriggerEditorOpened?.Invoke(this, EventArgs.Empty);

        await LoadTriggerConditionsAsync(target.Did, node);
    }

    /// <summary>拉某台设备的条件清单；<paramref name="existing"/> 非空时回填原值。</summary>
    private async Task LoadTriggerConditionsAsync(string did, JsonObject? existing)
    {
        if (_client == null) return;

        _triggerDevice = _allDevices.FirstOrDefault(d => MijiaScene.SameDid(d.Did, did));
        _triggerOptions = new List<TriggerOption>();
        TriggerConditionOptions.Clear();
        TriggerArgGroups.Clear();
        HasTriggerArgs = false;

        if (_triggerDevice == null) return;

        TriggerEditorBusy = true;
        try
        {
            _triggerOptions = await SceneTriggerCatalog.BuildOptionsAsync(_client, _triggerDevice);
        }
        catch (Exception ex)
        {
            Log($"读取设备能力失败：{ex.Message}");
        }
        finally
        {
            TriggerEditorBusy = false;
        }

        foreach (var o in _triggerOptions)
        {
            TriggerConditionOptions.Add(new OptionItem
            {
                Label = o.Title,
                Value = o.Key,
                Meta = o.SubTitle.Split('·')[0].Trim()
            });
        }

        var key = existing?["key"]?.ToString()
                  ?? (TriggerConditionOptions.Count > 0 ? TriggerConditionOptions[0].Value?.ToString() : null);

        _fillingTrigger = true;
        SelectedTriggerConditionKey = key;
        _fillingTrigger = false;

        BuildTriggerArgs(key ?? "", existing);
    }

    /// <summary>按条件生成第三级：事件出参的筛选片（默认收起），或属性的取值片（必选，直接展开）。</summary>
    private void BuildTriggerArgs(string key, JsonObject? existing)
    {
        TriggerArgGroups.Clear();
        HasTriggerArgs = false;
        TriggerPreview = "";

        var option = _triggerOptions.FirstOrDefault(o => o.Key == key);
        if (option == null) { UpdateTriggerPreview(); return; }

        TriggerArgSectionTitle = option.Axis == TriggerAxis.Property ? "④ 取值" : "④ 筛选（可不选＝任意）";

        // 事件：每个出参一行，收起态显示「参数名 · 当前值」
        foreach (var arg in option.Args)
            TriggerArgGroups.Add(TriggerArgGroup.Create(arg, ReadArgValue(existing, option, arg.Piid)));

        // 属性：只有一个取值组，而且必须选，直接展开
        if (option.Axis == TriggerAxis.Property && option.Prop is { } p)
        {
            var spec = p.IsBool
                ? new TriggerArgSpec { Piid = p.Piid, Name = $"{p.Desc}为", IsBool = true }
                : new TriggerArgSpec { Piid = p.Piid, Name = $"{p.Desc}为", Options = p.Options };
            var group = TriggerArgGroup.Create(spec, ReadPropValue(existing, p));
            group.IsExpanded = true;
            TriggerArgGroups.Add(group);
        }

        HasTriggerArgs = TriggerArgGroups.Count > 0;
        UpdateTriggerPreview();
    }

    private static object? ReadPropValue(JsonObject? existing, SpecPropertyDef p)
    {
        var v = existing?["value_json"];
        if (v is not JsonValue jv) return null;
        if (p.IsBool && jv.TryGetValue<bool>(out var b)) return b;
        if (jv.TryGetValue<int>(out var i)) return i;
        return null;
    }

    /// <summary>编辑已有事件条件：把 sub_props.attr 里的值按 piid 找回来。</summary>
    private static object? ReadArgValue(JsonObject? existing, TriggerOption option, int piid)
    {
        if (existing?["value_json"]?["sub_props"]?["attr"] is not JsonArray attrs) return null;
        foreach (var a in attrs.OfType<JsonObject>())
        {
            var k = a["key"]?.ToString() ?? "";
            var seg = k.Split('.');
            if (seg.Length < 4) continue;
            if (!int.TryParse(seg[^1], out var p2) || p2 != piid) continue;
            if (a["value"] is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<int>(out var i)) return i;
            }
        }
        return null;
    }

    private void UpdateTriggerPreview()
    {
        if (IsTimerTriggerKind)
        {
            var t = TimerTime ?? new TimeSpan(8, 0, 0);
            TriggerPreview = $"{SceneTriggerCatalog.DowLabelOf(TimerDow)} {t.Hours:00}:{t.Minutes:00} 触发";
            return;
        }

        var key = SelectedTriggerConditionKey?.ToString() ?? "";
        var option = _triggerOptions.FirstOrDefault(o => o.Key == key);
        if (option == null || _triggerDevice == null) { TriggerPreview = ""; return; }

        if (option.Axis == TriggerAxis.Event)
        {
            var filters = TriggerArgGroups.Where(g => g.Value != null).Select(g => g.Summary).ToList();
            var tail = filters.Count == 0 ? "任意上报" : string.Join(" · ", filters);
            TriggerPreview = $"当「{_triggerDevice.Name}」上报 {option.Title}（{tail}）时触发";
        }
        else
        {
            var p = option.Prop!;
            var text = TriggerArgGroups.FirstOrDefault()?.Summary ?? "任意";
            TriggerPreview = $"当「{_triggerDevice.Name}」的 {p.Desc} 变为 {text} 时触发";
        }
    }

    [RelayCommand]
    private void CancelTriggerEditor()
    {
        IsTriggerEditorOpen = false;
        _editingTriggerIndex = -1;
        TriggerArgGroups.Clear();
        HasTriggerArgs = false;
        _fillingTrigger = true;
        SelectedTriggerKindValue = "device";
        _fillingTrigger = false;
    }

    /// <summary>确认：把编辑器里的选择拼成触发源，写进 scene_trigger.triggers（保存时才提交云端）。</summary>
    [RelayCommand]
    private async Task ConfirmTrigger()
    {
        if (_editingScene?.Raw == null) return;

        var arr = TriggerArray(create: true);
        if (arr == null) return;

        // 替换已有条件时保持它的排序位；新增才排到末尾
        var order = _editingTriggerIndex >= 0 && _editingTriggerIndex < arr.Count
            ? _editingTriggerIndex + 1
            : arr.Count + 1;

        JsonObject node;
        if (IsTimerTriggerKind)
        {
            var t = TimerTime ?? new TimeSpan(8, 0, 0);
            node = SceneTriggerCatalog.BuildTimerNode(t.Hours, t.Minutes, TimerDow, order);
        }
        else
        {
            if (_triggerDevice == null) { Log("请先选择触发设备"); return; }

            var key = SelectedTriggerConditionKey?.ToString() ?? "";
            var option = _triggerOptions.FirstOrDefault(o => o.Key == key);
            if (option == null) { Log("请先选择触发条件"); return; }

            node = SceneTriggerCatalog.BuildNode(
                _triggerDevice, option, TriggerArgGroups.ToList(), order);
        }

        if (_editingTriggerIndex >= 0 && _editingTriggerIndex < arr.Count)
            arr[_editingTriggerIndex] = node;
        else
            arr.Add(node);

        IsTriggerEditorOpen = false;
        _editingTriggerIndex = -1;
        await RefreshTriggerRowsAsync();
    }

    [RelayCommand]
    private async Task RemoveTrigger(SceneTriggerRow? row)
    {
        if (row == null) return;
        var arr = TriggerArray(create: false);
        if (arr == null || row.Index < 0 || row.Index >= arr.Count) return;
        arr.RemoveAt(row.Index);
        if (IsTriggerEditorOpen) { IsTriggerEditorOpen = false; _editingTriggerIndex = -1; }
        await RefreshTriggerRowsAsync();
    }

    /// <summary>取（必要时创建）场景的触发源数组。</summary>
    private JsonArray? TriggerArray(bool create)
    {
        if (_editingScene?.Raw == null) return null;
        if (_editingScene.Raw["scene_trigger"] is JsonObject group && group["triggers"] is JsonArray arr)
            return arr;
        if (!create) return null;

        var fresh = new JsonObject
        {
            ["express"] = 1,
            ["triggers"] = new JsonArray()
        };
        _editingScene.Raw["scene_trigger"] = fresh;
        return (JsonArray)fresh["triggers"]!;
    }

    /// <summary>重建触发条件列表与卡片摘要（保存前都只改内存里的原始报文）。</summary>
    private async Task RefreshTriggerRowsAsync()
    {
        SceneTriggerRows.Clear();
        var arr = TriggerArray(create: false);

        if (arr != null)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JsonObject node) continue;
                var src = node["src"]?.ToString() ?? "";
                var name = node["name"]?.ToString() ?? "";
                var extra = node["extra_json"];
                var did = SceneTriggerCatalog.Sub(extra, "did")?.ToString() ?? "";

                if (src == "device")
                {
                    var dev = _allDevices.FirstOrDefault(d => MijiaScene.SameDid(d.Did, did));
                    var key = node["key"]?.ToString() ?? "";
                    // 云端个别触发源结构不规范（value_json 是字符串等），
                    // 描述失败只让这一行退化成 key，绝不让整条编辑面板崩掉
                    string desc;
                    try
                    {
                        desc = await SceneTriggerCatalog.DescribeAsync(_client, dev, node);
                    }
                    catch (Exception ex)
                    {
                        desc = key;
                        Log($"触发条件描述失败：{ex.Message}");
                    }
                    SceneTriggerRows.Add(new SceneTriggerRow
                    {
                        Index = i,
                        Title = string.IsNullOrWhiteSpace(name) ? desc : name,
                        Subtitle = $"{dev?.Name ?? SceneTriggerCatalog.Sub(extra, "device_name")?.ToString() ?? "设备"} · {desc}",
                        KindLabel = key.StartsWith("event.", StringComparison.Ordinal) ? "事件" : "状态",
                        Editable = CanEditTrigger,
                        Node = node
                    });
                }
                else if (src == "timer")
                {
                    var cron = node["value_json"]?.ToString();
                    var desc = SceneTriggerCatalog.DescribeTimerCron(cron);
                    SceneTriggerRows.Add(new SceneTriggerRow
                    {
                        Index = i,
                        Title = string.IsNullOrWhiteSpace(name) ? desc : name,
                        Subtitle = desc,
                        KindLabel = "定时",
                        Editable = CanEditTrigger,
                        Node = node
                    });
                }
                else
                {
                    SceneTriggerRows.Add(new SceneTriggerRow
                    {
                        Index = i,
                        Title = string.IsNullOrWhiteSpace(name) ? "手动触发" : name,
                        Subtitle = "手动 / 语音触发，本应用不改",
                        KindLabel = "手动",
                        Editable = false,
                        Node = node
                    });
                }
            }
        }

        TriggerCount = SceneTriggerRows.Count == 0 ? "" : $"{SceneTriggerRows.Count} 条";

        SceneEditTrigger = SceneTriggerRows.Count == 0
            ? (_editingScene?.IsAutomation == true ? "还没有触发条件" : "手动触发")
            : string.Join("，", SceneTriggerRows.Take(2).Select(r => r.Title))
              + (SceneTriggerRows.Count > 2 ? $" 等 {SceneTriggerRows.Count} 个条件" : "");
    }

    public string DeleteButtonText => SceneEditConfirmDelete ? "确认删除" : "删除";

    /// <summary>面板底部的补充说明（没有可编辑动作、或部分动作被跳过）。</summary>
    public bool HasSceneEditNote => !string.IsNullOrEmpty(SceneEditNote);

    partial void OnSceneEditNoteChanged(string value) => OnPropertyChanged(nameof(HasSceneEditNote));

    partial void OnSceneEditConfirmDeleteChanged(bool value) => OnPropertyChanged(nameof(DeleteButtonText));

    private MijiaScene? _editingScene;

    /// <summary>打开面板时的原始报文快照：取消编辑时回滚，避免未保存的改动残留在内存里。</summary>
    private JsonObject? _editingSceneBackup;

    /// <summary>新建模式：面板来自「新建场景 / 新建自动化」，保存即创建，不显示删除与「以此新建」。</summary>
    [ObservableProperty] private bool _isSceneNew;

    /// <summary>编辑面板内的可见报错：云端拒绝 / 校验不通过时直接显示在面板底部，别让它只躺在日志面板里。</summary>
    [ObservableProperty] private string _sceneEditError = "";
    public bool HasSceneEditError => !string.IsNullOrEmpty(SceneEditError);
    partial void OnSceneEditErrorChanged(string value) => OnPropertyChanged(nameof(HasSceneEditError));

    public bool IsSceneEditMode => !IsSceneNew;

    /// <summary>当前编辑的是不是自动化：手动场景没有触发条件，整块卡片不显示。</summary>
    public bool IsAutomationEditor => _editingScene?.IsAutomation == true;

    /// <summary>名称框占位文案：场景 / 自动化分开写，别让手工场景被叫成自动化。</summary>
    public string SceneEditNameWatermark =>
        IsAutomationEditor ? "给这条自动化起个名字" : "给这个场景起个名字";

    /// <summary>启停说明：自动化是「不再触发」，手动场景是「不会被执行」。</summary>
    public string SceneEditEnableHint =>
        IsAutomationEditor ? "关闭后云端不再触发这条自动化" : "关闭后在智能页不会执行这个场景";

    public string SceneEditTitle => IsSceneNew
        ? (_editingScene?.IsAutomation == true ? "新建自动化" : "新建场景")
        : (_editingScene is { IsAutomation: false } ? "编辑场景" : "编辑自动化");

    public string SceneEditSubtitle => IsSceneNew
        ? (IsAutomationEditor
            ? "填写名称与触发条件，保存后新建到米家云端"
            : "填写名称并添加动作，保存后新建到米家云端")
        : "改动直接写入米家云端，手机 App 会同步";

    /// <summary>底部主按钮文案：新建时叫「创建」，编辑时叫「保存」。</summary>
    public string SaveSceneButtonText => IsSceneNew ? "创建" : "保存";

    partial void OnIsSceneNewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSceneEditMode));
        OnPropertyChanged(nameof(SceneEditTitle));
        OnPropertyChanged(nameof(SceneEditSubtitle));
        OnPropertyChanged(nameof(SaveSceneButtonText));
        OnPropertyChanged(nameof(SceneEditCanChangeIcon));
        OnPropertyChanged(nameof(SceneEditIconTip));
    }

    /// <summary>打开编辑面板：把整条场景拆成「名称 / 启停 / 触发 / 可改动作」。</summary>
    [RelayCommand]
    private async Task OpenSceneEditorAsync(SceneCardItem? item)
    {
        if (_client == null || item == null) return;

        var scene = item.Scene;
        if (scene.Raw == null)
        {
            Log("这条场景缺少原始报文，先点「刷新」再编辑");
            return;
        }

        // 宿主按入口分：主界面抽屉里点 → 还在主窗口内做浮层；桌面分区的设备弹窗是独立置顶窗，
        // 从它进来就单独弹一个场景详情窗，不把主程序窗口唤到前面（点一下详情不该被拽回主界面）。
        IsSceneEditorInline = !IsDevicePanelOpen;
        if (!IsSceneEditorInline) SceneEditorHost.Open(this);

        // 打开详情面板是异步命令，异常没人接就是进程级未处理异常（已踩过：云端某个触发源
        // value_json 不是对象，DescribeAsync 直接抛 InvalidOperationException → 整个应用闪退）。
        // 这里兜一层：面板关掉、原因写进日志，不把应用带走。
        try
        {
            await OpenSceneEditorCoreAsync(scene, isNew: false);
        }
        catch (Exception ex)
        {
            IsSceneEditorOpen = false;
            Log($"打开场景详情失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>新建：按当前分段造一条空白场景 / 自动化，直接进同一个编辑面板。</summary>
    [RelayCommand]
    private async Task NewSceneAsync(string? kind)
    {
        if (_client == null) { Log("还没登录，先扫码登录再新建"); return; }

        var automation = kind switch
        {
            "automation" => true,
            "scene" => false,
            _ => SceneTab == 1
        };

        // 先切分段：按钮在 tab 行最右，点哪条就立刻跳到哪一分段（别等拉家庭回来才跳）
        SceneTab = automation ? 1 : 0;

        MijiaHome? home;
        try
        {
            var homes = _client.Homes.Count > 0 ? _client.Homes : await _client.GetHomesAsync();
            home = homes.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log($"读取家庭失败：{ex.Message}");
            return;
        }

        if (home == null) { Log("没有可用家庭，无法新建"); return; }

        // 保存后刷新列表就能在当前分段看到它
        var scene = SceneDraftFactory.CreateBlank(
            home, automation, automation ? "新的自动化" : "新的场景");
        await OpenSceneEditorCoreAsync(scene, isNew: true);
    }

    /// <summary>编辑面板的公共入口：编辑已有对象与新建空白对象走同一套界面。</summary>
    private async Task OpenSceneEditorCoreAsync(MijiaScene scene, bool isNew)
    {
        _editingScene = scene;
        SceneEditCustomIcon = string.IsNullOrEmpty(scene.SceneId)
            ? null
            : CustomIconStore.Get(CustomIconStore.SceneScope, scene.SceneId);
        OnPropertyChanged(nameof(SceneEditCanChangeIcon));
        OnPropertyChanged(nameof(SceneEditIconTip));
        OnPropertyChanged(nameof(SceneEditIconGeometry));
        // 编辑模式留一份快照，取消时回滚；新建模式对象本就是临时的，不需要快照
        _editingSceneBackup = isNew || scene.Raw == null
            ? null
            : (JsonObject)scene.Raw.DeepClone();

        IsSceneNew = isNew;
        SceneEditError = "";
        SceneEditName = scene.Name;
        SceneEditEnabled = scene.Enable;
        // 标题必须显式通知：IsSceneNew 常常前后两次都是 false（连续编辑同类型对象），
        // 光靠 OnIsSceneNewChanged 不会重算，标题就会停在上一对象上（比如手动场景显示「编辑自动化」）。
        OnPropertyChanged(nameof(SceneEditTitle));
        OnPropertyChanged(nameof(IsAutomationEditor));
        OnPropertyChanged(nameof(SceneEditSubtitle));
        OnPropertyChanged(nameof(SceneEditNameWatermark));
        OnPropertyChanged(nameof(SceneEditEnableHint));
        SceneEditTrigger = string.IsNullOrWhiteSpace(scene.TriggerSummary) ? "手动触发" : scene.TriggerSummary;
        SceneEditCondition = scene.Condition?.Conditions is { Count: > 0 } conds
            ? string.Join("，", conds.Select(c => c.DisplayText))
            : "无";

        // 触发条件：设备类可编辑（按 MIoT 规格选事件 / 属性），定时与手动只读
        CanEditTrigger = scene.IsAutomation;
        IsTriggerEditorOpen = false;
        _editingTriggerIndex = -1;
        SceneStateConditionText = scene.Condition?.Conditions is { Count: > 0 } stateConditions
            ? "状态条件（只读）：" + string.Join("，", stateConditions
                .Select(c => c.DisplayText)
                .Where(t => !string.IsNullOrWhiteSpace(t)))
            : "";

        SceneEditConfirmDelete = false;
        IsSceneOrderExpanded = true;
        SceneEditRows.Clear();
        SceneEditNote = "";
        SceneEditActionCount = "";
        IsActionEditorOpen = false;
        ResetActionDraft();
        IsSceneEditorOpen = true;

        await RefreshTriggerRowsAsync();

        var rows = await BuildSceneEditRowsAsync(scene);
        var unsupported = rows.Count(r => r.IsUnknown);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplySceneEditRows(rows);
            SceneEditActionCount = rows.Count == 0 ? "" : $"{rows.Count} 项";
            SceneEditNote = isNew
                ? (scene.IsAutomation
                    ? "先加一个触发条件，再点右上角「添加动作」把要做的事加进来"
                    : "点右上角「添加动作」把要做的事加进来")
                : rows.Count == 0
                    ? "这条场景没有动作"
                    : unsupported > 0
                        ? $"另有 {unsupported} 项动作类型本应用暂不支持编辑（已原样保留）"
                        : "";
        });
    }

    /// <summary>
    /// 把 scene_action 拍平成卡片：每个动作一张，同级、可拖拽换顺序。
    /// 米家里「关 · 3 项设备动作」那种合卡在界面上不再出现标题行，成员各自成卡。
    /// 延时是这些动作 payload 上的 delay_time，不是独立动作，所以单独生成一张虚拟卡插在段首。
    /// 所有卡片都直接引用原始 JSON 节点，改动就地生效。
    /// </summary>
    private async Task<List<SceneEditActionRow>> BuildSceneEditRowsAsync(MijiaScene scene)
    {
        var rows = new List<SceneEditActionRow>();
        if (scene.Raw?["scene_action"] is not JsonObject group ||
            group["actions"] is not JsonArray actions) return rows;

        // 场景跳转类动作要给出可选的场景列表（手动场景才能被自动化执行）
        var sceneOptions = _scenes
            .Where(s => !s.IsAutomation && !string.IsNullOrEmpty(s.SceneId))
            .Select(s => (OptionItem)new OptionItem { Label = s.Name, Value = s.SceneId! })
            .ToList();

        foreach (var action in actions.OfType<JsonObject>())
        {
            var row = await BuildSceneEditRowAsync(action, sceneOptions);
            // 行首图标要用的设备对象：按 payload.did 找回，与标题/房间用的是同一台
            row.Device = ResolveRowDevice(row);
            row.MarkReady();
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// 设备类动作按 payload.did 找回设备，供执行顺序卡片行首显示真机图 / 设备类型图标。
    /// 场景、通知、延时这类动作没有 did，交给行图标转换器按动作类型给语义图标。
    /// </summary>
    private MijiaDevice? ResolveRowDevice(SceneEditActionRow row)
    {
        if (row.Kind is not (SceneEditRowKind.DeviceProp
                             or SceneEditRowKind.DeviceAction
                             or SceneEditRowKind.Tts)) return null;

        var did = row.Payload?["did"]?.ToString();
        return string.IsNullOrEmpty(did)
            ? null
            : _allDevices.FirstOrDefault(d => MijiaScene.SameDid(d.Did, did));
    }

    /// <summary>
    /// 按「动作顺序 + 各自的 delay_time」重排界面行：连续且延时相同的一段动作，
    /// 在这段前面插一张延时卡，卡上改秒数就写回这一段的全部 payload。
    /// </summary>
    private void ApplySceneEditRows(IReadOnlyList<SceneEditActionRow> physical)
    {
        var desired = new List<SceneEditActionRow>(physical.Count + 4);

        var i = 0;
        while (i < physical.Count)
        {
            var seconds = DelayOf(physical[i]);
            if (seconds <= 0)
            {
                desired.Add(physical[i]);
                i++;
                continue;
            }

            var end = i + 1;
            while (end < physical.Count && DelayOf(physical[end]) == seconds) end++;

            var targets = new List<JsonObject>(end - i);
            for (var k = i; k < end; k++)
                if (physical[k].Payload is { } p) targets.Add(p);

            var delayRow = new SceneEditActionRow
            {
                Kind = SceneEditRowKind.Delay,
                Title = "延时",
                DelayTargets = targets,
                DelaySeconds = seconds
            };
            delayRow.MarkReady();

            desired.Add(delayRow);
            for (var k = i; k < end; k++) desired.Add(physical[k]);
            i = end;
        }

        SceneEditRows.Clear();
        foreach (var r in desired) SceneEditRows.Add(r);
    }

    private static int DelayOf(SceneEditActionRow row) =>
        row.Payload is { } p ? SceneEditActionRow.IntOf(p["delay_time"]) : 0;

    /// <summary>
    /// 拖拽换顺序：所有卡片（含延时卡）都能拖。延时卡是独立一步，
    /// 拖到哪，它后面（到下一张延时卡之前）的动作就等它这么久；
    /// 前面没有延时卡的动作归 0。改完重排 order 并按新延时重新生成卡片。
    /// </summary>
    public void MoveSceneEditRow(int from, int to)
    {
        var rows = SceneEditRows;
        if (from < 0 || from >= rows.Count) return;

        if (to < 0) to = 0;
        if (to > rows.Count - 1) to = rows.Count - 1;
        if (from == to) return;

        var list = rows.ToList();
        var source = list[from];
        list.RemoveAt(from);
        if (to > list.Count) to = list.Count;
        list.Insert(to, source);

        // 拖的是延时卡：整段的归属变了，按新顺序重算每个动作的 delay_time
        // （拖普通动作时只改顺序，各自原来的延时不动，交给分组逻辑自然合并/拆开）
        if (source.IsDelay)
        {
            var current = 0;
            foreach (var row in list)
            {
                if (row.IsDelay) { current = (int)Math.Round(row.DelaySeconds); continue; }
                if (row.Payload is { } p)
                {
                    try { p["delay_time"] = current; } catch { }
                }
            }
        }

        var physical = list.Where(r => !r.IsVirtual).ToList();
        ResequenceSceneActions(physical);

        // 拖拽刚结束时整表重填容易和拖拽视觉打架，等这一轮输入处理完再刷
        Dispatcher.UIThread.Post(() => ApplySceneEditRows(physical), DispatcherPriority.Background);

        if (source.IsDelay && !list.Skip(list.IndexOf(source) + 1).Any(r => !r.IsVirtual))
            Log("延时卡被拖到最后，后面没有动作可等，这段延时已取消");
        else
            Log($"已调整执行顺序：第 {from + 1} 项 → 第 {to + 1} 项");
    }

    /// <summary>
    /// 按界面顺序重写报文：order 从 1 连续编号；group_id 按「原分组的连续段」重编——
    /// 同一段沿用同一个新 id，被拖散的成员各自成段。这样米家 App 里该合并的仍合并，
    /// 真正被打散的才拆开。没拖过的时候重排结果与原值一致，不会产生副作用。
    /// </summary>
    private void ResequenceSceneActions(IReadOnlyList<SceneEditActionRow>? physical = null)
    {
        if (_editingScene?.Raw?["scene_action"] is not JsonObject group ||
            group["actions"] is not JsonArray arr) return;

        var rows = physical ?? SceneEditRows.Where(r => !r.IsVirtual).ToList();
        var ordered = rows.Select(r => r.Action).OfType<JsonObject>().ToList();
        if (ordered.Count == 0 || ordered.Count != arr.Count) return;

        var newGids = new List<int>(ordered.Count);
        var seq = 0;
        var prevOld = -1;
        var curNew = 0;
        foreach (var a in ordered)
        {
            var old = SceneEditActionRow.IntOf(a["group_id"]);
            if (old <= 0) { newGids.Add(0); prevOld = -1; continue; }
            if (old != prevOld) curNew = ++seq;
            prevOld = old;
            newGids.Add(curNew);
        }

        // Clear 会把子节点的 Parent 置空，之后才能重新 Add 回去
        try
        {
            arr.Clear();
            for (var i = 0; i < ordered.Count; i++)
            {
                var a = ordered[i];
                a["order"] = i + 1;
                a["group_id"] = newGids[i];
                arr.Add(a);
            }
        }
        catch (Exception ex)
        {
            // 重排失败不应该让整个面板崩掉：动作值改动仍在原节点上，只是顺序没写进去
            Log($"重排动作顺序失败：{ex.Message}");
        }
    }

    private async Task<SceneEditActionRow> BuildSceneEditRowAsync(
        JsonObject action, List<OptionItem> sceneOptions)
    {
        var payload = action["payload_json"] as JsonObject;
        var type = SceneEditActionRow.IntOf(action["type"]);
        var name = action["name"]?.ToString() ?? "";
        var deviceName = payload?["device_name"]?.ToString() ?? "";

        // 执行 / 开关另一条场景（含自动化）时，云端会在动作里带一份 nested_scene_info，
        // 那是唯一能认出「自动化 / 跨家庭场景」名字的地方——只查手动场景列表就会显示「未知场景」。
        var nestedName = SceneTriggerCatalog.Sub(action["nested_scene_info"], "scene_name")?.ToString();
        var nestedId = SceneTriggerCatalog.Sub(action["nested_scene_info"], "scene_id")?.ToString() ?? "";

        // 执行另一条场景
        if (type == 2)
        {
            var sid = payload?["scene_id"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(sid)) sid = nestedId;
            var option = sceneOptions.FirstOrDefault(o => Equals(o.Value, sid));
            var display = option?.Label
                          ?? (string.IsNullOrWhiteSpace(nestedName) ? null : nestedName)
                          ?? FindSceneName(sid)
                          ?? QuotedName(name)
                          ?? "未知场景";

            // 目标场景不在当前列表里（跨家庭/已删）：补一个占位项，避免下拉空着
            var options = sceneOptions;
            if (option == null)
            {
                options = new List<OptionItem>(sceneOptions)
                {
                    new() { Label = display, Value = sid }
                };
            }

            var row = new SceneEditActionRow
            {
                Action = action,
                Payload = payload,
                Kind = SceneEditRowKind.SceneRun,
                Title = "执行场景",
                Subtitle = $"顺序执行「{display}」",
                SceneOptions = options,
                SelectedSceneId = sid
            };
            return row;
        }

        // 开启 / 关闭另一条场景（也常用来开关自动化）
        if (type == 3)
        {
            var sid = payload?["scene_id"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(sid)) sid = nestedId;
            var target = FindScene(sid);
            // type=3 的 action.name 直接就是目标场景 / 自动化的名字（不带引号，例如「回家模式夜晚」），
            // 之前只走 QuotedName(name) 抽引号，抽不到就整行落到「未知场景」。
            var rawName = name.Trim();
            var genericName = rawName.StartsWith("开关场景", StringComparison.Ordinal)
                              || rawName.StartsWith("开关自动化", StringComparison.Ordinal);
            var display = sceneOptions.FirstOrDefault(o => Equals(o.Value, sid))?.Label
                          ?? (string.IsNullOrWhiteSpace(nestedName) ? null : nestedName)
                          ?? FindSceneName(sid)
                          ?? (string.IsNullOrWhiteSpace(rawName) || genericName ? null : rawName)
                          ?? QuotedName(name)
                          ?? "未知场景";
            var enable = !IsFalse(payload?["enable"]);
            var kindWord = target?.IsAutomation == true ? "自动化" : "场景";

            var row = new SceneEditActionRow
            {
                Action = action,
                Payload = payload,
                Kind = SceneEditRowKind.SceneToggle,
                Title = $"开关{kindWord}「{display}」",
                Subtitle = enable ? $"执行后开启该{kindWord}" : $"执行后关闭该{kindWord}",
                ToggleNoun = kindWord,
                ToggleEnabled = enable
            };
            return row;
        }

        // 手机通知
        if (type == 1)
        {
            var notifyTitle = payload?["title"]?.ToString() ?? "";
            var notifyDesc = payload?["desc"]?.ToString() ?? "";
            var row = new SceneEditActionRow
            {
                Action = action,
                Payload = payload,
                Kind = SceneEditRowKind.Notify,
                Title = "发送手机通知",
                Subtitle = string.IsNullOrWhiteSpace(notifyTitle) ? "智能通知" : notifyTitle,
                NotifyTitle = notifyTitle,
                NotifyDesc = notifyDesc
            };
            return row;
        }

        // 设备动作：set_properties 可改值，tts 可改文字，command=action 只是设备动作
        if (type == 0)
        {
            var command = payload?["command"]?.ToString() ?? "";
            var device = _allDevices.FirstOrDefault(d =>
                MijiaScene.SameDid(d.Did, payload?["did"]?.ToString()));
            var shown = !string.IsNullOrWhiteSpace(deviceName) ? deviceName : device?.Name ?? "设备";

            if (string.Equals(command, "tts", StringComparison.Ordinal))
            {
                var text = payload?["value"] is JsonArray arr && arr.Count > 0
                    ? arr[0]?.ToString() ?? ""
                    : "";
                var row = new SceneEditActionRow
                {
                    Action = action,
                    Payload = payload,
                    Kind = SceneEditRowKind.Tts,
                    Title = shown,
                    Subtitle = "播报文字",
                    TtsText = text
                };
                return row;
            }

            var values = await BuildValueItemsAsync(action, payload, shown);
            if (values.Count > 0)
            {
                var room = device?.RoomName is { Length: > 0 } r && r != "未分配房间" ? r : "";
                var row = new SceneEditActionRow
                {
                    Action = action,
                    Payload = payload,
                    Kind = SceneEditRowKind.DeviceProp,
                    Title = shown,
                    // 副标题 = 房间 · 这一步把设备调到的状态（关闭 / 打开 / 亮度 50%），
                    // 状态词跟着右侧控件实时变，不再显示「开关」这类属性标识
                    RoomName = room,
                    Subtitle = room.Length > 0 && values[0].DeviceText.Length > 0
                        ? $"{room} · {values[0].ActionText}"
                        : values[0].ActionText,
                    Values = values
                };
                return row;
            }

            var actionRow = new SceneEditActionRow
            {
                Action = action,
                Payload = payload,
                Kind = SceneEditRowKind.DeviceAction,
                Title = shown,
                Subtitle = string.IsNullOrWhiteSpace(name) ? "设备动作" : $"{name} · 设备动作"
            };
            return actionRow;
        }

        // 设备组动作（米家 App 的「打包控制」，例如「全屋照明（灯光和开关）」一键关灯）
        // payload.command = special_command，真正的动作在 value.command_type，成员在 value.filter_dids
        if (type == 4)
        {
            var groupName = payload?["device_name"]?.ToString().Trim();
            if (string.IsNullOrWhiteSpace(groupName))
                groupName = QuotedName(name) ?? "";

            var cmdType = (payload?["value"] as JsonObject)?["command_type"]?.ToString()
                          ?? payload?["command"]?.ToString()
                          ?? "";

            // command_type 形如 light-close / *_open，翻成状态词；认不出就用动作自带的 name（如「关闭」）
            var actText = cmdType switch
            {
                string s when s.EndsWith("-close", StringComparison.OrdinalIgnoreCase) || s.EndsWith("close", StringComparison.OrdinalIgnoreCase) => "关闭",
                string s when s.EndsWith("-open", StringComparison.OrdinalIgnoreCase) || s.EndsWith("open", StringComparison.OrdinalIgnoreCase) => "打开",
                _ => string.IsNullOrWhiteSpace(name) ? "" : name
            };

            // 不显示台数：云端 filter_dids 只记了少部分设备（还可能含未展开的 group.xxx），
            // 与米家 App 的「选中设备数」对不上，写死一个数字反而误导，只说明这是批量开关。
            var subtitle = actText.Length > 0 ? $"批量{actText}设备" : "批量开关设备";
            var groupRow = new SceneEditActionRow
            {
                Action = action,
                Payload = payload,
                Kind = SceneEditRowKind.DeviceGroup,
                Title = string.IsNullOrWhiteSpace(groupName) ? "设备组" : groupName,
                Subtitle = subtitle
            };
            return groupRow;
        }

        var unknown = new SceneEditActionRow
        {
            Action = action,
            Payload = payload,
            Kind = SceneEditRowKind.Unknown,
            Title = string.IsNullOrWhiteSpace(name) ? $"动作 {type}" : name,
            Subtitle = $"类型 {type}，本应用暂不支持编辑"
        };
        return unknown;
    }

    /// <summary>按 scene_id 找到场景本体（含自动化）；找不到返回 null。</summary>
    private MijiaScene? FindScene(string sceneId)
    {
        if (string.IsNullOrEmpty(sceneId)) return null;
        foreach (var s in _scenes)
        {
            if (!string.IsNullOrEmpty(s.SceneId) && string.Equals(s.SceneId, sceneId, StringComparison.Ordinal))
                return s;
        }
        return null;
    }

    /// <summary>目标场景 / 自动化的显示名：先查全部场景，不认识就给 null。</summary>
    private string? FindSceneName(string sceneId)
    {
        var scene = FindScene(sceneId);
        return string.IsNullOrWhiteSpace(scene?.Name) ? null : scene!.Name;
    }

    /// <summary>set_properties 的 value 数组 → 可编辑的属性行（规格给控件类型与量程）。</summary>
    private async Task<List<SceneActionEditItem>> BuildValueItemsAsync(
        JsonObject action, JsonObject? payload, string deviceName)
    {
        var items = new List<SceneActionEditItem>();
        if (payload?["value"] is not JsonArray values) return items;

        var payloadDid = payload["did"]?.ToString() ?? "";
        foreach (var v in values.OfType<JsonObject>())
        {
            if (!int.TryParse(v["siid"]?.ToString(), out var siid) ||
                !int.TryParse(v["piid"]?.ToString(), out var piid)) continue;

            var did = v["did"]?.ToString() is { Length: > 0 } d ? d : payloadDid;
            var device = _allDevices.FirstOrDefault(x => MijiaScene.SameDid(x.Did, did));
            var shown = !string.IsNullOrWhiteSpace(deviceName) ? deviceName : device?.Name ?? "设备";

            DeviceSpec? spec = null;
            if (device != null && _client != null)
            {
                try { spec = await DeviceSpecService.GetAsync(_client, device); } catch { }
            }
            var def = spec?.Properties.FirstOrDefault(x => x.Siid == siid && x.Piid == piid);
            items.Add(SceneActionEditItem.Create(v, shown, def));
        }

        return items;
    }

    // ===================================================================
    // 添加动作（内联小面板：① 类型 → 按类型填写，确认后直接追加到 scene_action）
    // 设备动作只做 set_properties（写属性）；延时不是独立动作，云端把延时写在
    // 「后面那条动作」的 payload.delay_time 上，所以这里用待生效机制承接；
    // 执行场景是 type=2 的动作（payload 里带 scene_id）。
    // ===================================================================

    [ObservableProperty] private bool _isActionEditorOpen;
    [ObservableProperty] private bool _actionEditorBusy;
    [ObservableProperty] private string _actionEditorHint = "";

    /// <summary>① 动作类型：设备 / 延时 / 执行场景。</summary>
    public ObservableCollection<OptionItem> ActionKindOptions { get; } = new();
    [ObservableProperty] private object? _selectedActionKindValue;

    public bool IsActionKindDevice =>
        string.Equals(SelectedActionKindValue?.ToString(), "device", StringComparison.Ordinal);
    public bool IsActionKindDelay =>
        string.Equals(SelectedActionKindValue?.ToString(), "delay", StringComparison.Ordinal);
    public bool IsActionKindScene =>
        string.Equals(SelectedActionKindValue?.ToString(), "scene", StringComparison.Ordinal);

    partial void OnSelectedActionKindValueChanged(object? value)
    {
        OnPropertyChanged(nameof(IsActionKindDevice));
        OnPropertyChanged(nameof(IsActionKindDelay));
        OnPropertyChanged(nameof(IsActionKindScene));
        OnPropertyChanged(nameof(HasActionDraft));
        if (!_fillingAction) UpdateActionEditorHint();
    }

    /// <summary>
    /// 待生效延时（秒）：米家云端的延时是「后面那条动作」的 delay_time，
    /// 添加一条延时后，写在下一次确认添加的动作上；保存时还没落动作就丢弃并提示。
    /// </summary>
    private int _pendingDelaySeconds;

    /// <summary>延时类型的时长输入（秒）。</summary>
    [ObservableProperty] private double _actionDelaySeconds = 60;

    partial void OnActionDelaySecondsChanged(double value)
    {
        OnPropertyChanged(nameof(HasActionDraft));
        if (!_fillingAction) UpdateActionEditorHint();
    }

    /// <summary>执行场景类型的目标场景候选（手动场景才能被自动化执行）。</summary>
    public ObservableCollection<OptionItem> ActionSceneOptions { get; } = new();
    [ObservableProperty] private object? _selectedActionSceneId;

    partial void OnSelectedActionSceneIdChanged(object? value) => OnPropertyChanged(nameof(HasActionDraft));

    /// <summary>① 可选设备（有 did 的都算）。</summary>
    public ObservableCollection<OptionItem> ActionDeviceOptions { get; } = new();
    [ObservableProperty] private object? _selectedActionDeviceValue;

    /// <summary>① 设备行上显示的名字（换成弹窗选设备后只看得见这一行，名字得常显）。</summary>
    [ObservableProperty] private string _actionDeviceLabel = "选择设备";

    /// <summary>② 该设备可写属性（key = "siid.piid"）。</summary>
    public ObservableCollection<OptionItem> ActionCapabilityOptions { get; } = new();
    [ObservableProperty] private object? _selectedActionCapabilityKey;

    /// <summary>③ 值：复用动作卡上那套控件，直接绑一个临时的写入节点。</summary>
    [ObservableProperty] private SceneActionEditItem? _actionDraftValue;

    public bool HasActionCapabilities => ActionCapabilityOptions.Count > 0;

    /// <summary>「添加」按钮的可用性：按动作类型各自判一份。</summary>
    public bool HasActionDraft => SelectedActionKindValue?.ToString() switch
    {
        "delay" => ActionDelaySeconds >= 1,
        "scene" => !string.IsNullOrEmpty(SelectedActionSceneId?.ToString()),
        _ => ActionDraftValue != null
    };
    public bool DraftIsBool => ActionDraftValue?.IsBool == true;
    public bool DraftIsNumber => ActionDraftValue?.IsNumber == true;
    public bool DraftIsOption => ActionDraftValue?.IsOption == true;
    public bool DraftIsText => ActionDraftValue?.IsText == true;
    public double DraftMin => ActionDraftValue?.Min ?? 0;
    public double DraftMax => ActionDraftValue?.Max ?? 100;
    public double DraftStep => ActionDraftValue?.Step ?? 1;
    public IReadOnlyList<string> DraftOptionNames => ActionDraftValue?.OptionNames ?? new List<string>();

    private MijiaDevice? _actionDevice;
    private SpecPropertyDef? _actionProp;
    private DeviceSpec? _actionSpec;

    /// <summary>程序内回填时不要触发「换设备 → 重拉属性」这类联动。</summary>
    private bool _fillingAction;

    partial void OnActionDraftValueChanged(SceneActionEditItem? value) => RaiseActionDraft();

    private void RaiseActionDraft()
    {
        OnPropertyChanged(nameof(HasActionDraft));
        OnPropertyChanged(nameof(DraftIsBool));
        OnPropertyChanged(nameof(DraftIsNumber));
        OnPropertyChanged(nameof(DraftIsOption));
        OnPropertyChanged(nameof(DraftIsText));
        OnPropertyChanged(nameof(DraftMin));
        OnPropertyChanged(nameof(DraftMax));
        OnPropertyChanged(nameof(DraftStep));
        OnPropertyChanged(nameof(DraftOptionNames));
    }

    partial void OnSelectedActionDeviceValueChanged(object? value)
    {
        if (_fillingAction) return;
        _ = LoadActionCapabilitiesAsync(value?.ToString() ?? "");
    }

    partial void OnSelectedActionCapabilityKeyChanged(object? value)
    {
        if (_fillingAction) return;
        BuildActionDraft();
    }

    /// <summary>
    /// 添加动作面板就绪后请求宿主弹出窗口（由视图订阅后 ShowDialog）。
    /// 和触发条件一样，视图模型不直接引用窗口类型。
    /// </summary>
    public event EventHandler? ActionEditorOpened;

    /// <summary>打开「添加动作」弹窗（设备清单每次现场取，家庭切换后也对）。</summary>
    [RelayCommand]
    private void AddSceneAction()
    {
        if (_editingScene?.Raw == null) return;

        if (ActionKindOptions.Count == 0)
        {
            ActionKindOptions.Add(new OptionItem { Label = "设备", Value = "device" });
            ActionKindOptions.Add(new OptionItem { Label = "延时", Value = "delay" });
            ActionKindOptions.Add(new OptionItem { Label = "执行场景", Value = "scene" });
        }

        ActionDeviceOptions.Clear();
        foreach (var d in _allDevices.Where(d => !string.IsNullOrEmpty(d.Did)))
        {
            ActionDeviceOptions.Add(new OptionItem
            {
                Label = d.Name,
                Value = d.Did,
                Meta = string.IsNullOrWhiteSpace(d.RoomName) ? null : d.RoomName
            });
        }

        if (ActionDeviceOptions.Count == 0) { Log("没有可用设备，先刷新设备列表"); return; }

        ActionSceneOptions.Clear();
        foreach (var s in _scenes.Where(s => !s.IsAutomation && !string.IsNullOrEmpty(s.SceneId)))
        {
            ActionSceneOptions.Add(new OptionItem { Label = s.Name, Value = s.SceneId });
        }

        _fillingAction = true;
        SelectedActionKindValue = "device";
        SelectedActionDeviceValue = null;
        SelectedActionCapabilityKey = null;
        SelectedActionSceneId = ActionSceneOptions.FirstOrDefault()?.Value;
        _fillingAction = false;

        _actionDevice = null;
        _actionProp = null;
        _actionSpec = null;
        ActionCapabilityOptions.Clear();
        ActionDraftValue = null;
        ActionDelaySeconds = _pendingDelaySeconds > 0 ? _pendingDelaySeconds : 60;
        ActionDeviceLabel = "选择设备";
        OnPropertyChanged(nameof(HasActionCapabilities));
        UpdateActionEditorHint();

        IsSceneOrderExpanded = true;
        IsActionEditorOpen = true;
        ActionEditorOpened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>弹窗底部的提示行：按当前类型说清下一步。</summary>
    private void UpdateActionEditorHint()
    {
        if (IsActionKindDelay)
        {
            ActionEditorHint = _pendingDelaySeconds > 0
                ? $"已有一条 {_pendingDelaySeconds} 秒的延时待生效，再添加会覆盖它"
                : "延时写在「下一条添加的动作」上（米家云端的结构如此）";
        }
        else if (IsActionKindScene)
        {
            ActionEditorHint = ActionSceneOptions.Count == 0
                ? "还没有可执行的手动场景，先在米家或本应用里建一个"
                : "选一条手动场景，自动化保存后会按顺序执行它";
        }
        else
        {
            ActionEditorHint = "先选设备，再选要改的属性";
        }
    }

    [RelayCommand]
    private void CancelAddAction()
    {
        IsActionEditorOpen = false;
        ResetActionDraft();
    }

    private void ResetActionDraft()
    {
        _actionDevice = null;
        _actionProp = null;
        _actionSpec = null;
        ActionCapabilityOptions.Clear();
        ActionDraftValue = null;
        ActionDeviceLabel = "选择设备";
        _fillingAction = true;
        SelectedActionDeviceValue = null;
        SelectedActionCapabilityKey = null;
        SelectedActionKindValue = "device";
        _fillingAction = false;
        OnPropertyChanged(nameof(HasActionCapabilities));
    }

    /// <summary>选中设备后按 MIoT 规格列出可写属性（只列能选值的：bool / 数值 / 文本）。</summary>
    private async Task LoadActionCapabilitiesAsync(string did)
    {
        _actionDevice = _allDevices.FirstOrDefault(d => d.Did == did);
        _actionProp = null;
        _actionSpec = null;
        ActionCapabilityOptions.Clear();
        ActionDraftValue = null;
        ActionDeviceLabel = _actionDevice?.Name ?? "选择设备";
        OnPropertyChanged(nameof(HasActionCapabilities));

        if (_actionDevice == null || _client == null) return;

        ActionEditorBusy = true;
        try
        {
            _actionSpec = await DeviceSpecService.GetAsync(_client, _actionDevice);
        }
        catch (Exception ex)
        {
            Log($"读取设备能力失败：{ex.Message}");
        }
        finally
        {
            ActionEditorBusy = false;
        }

        if (_actionSpec == null)
        {
            ActionEditorHint = "这台设备的能力暂时取不到，换一台试试";
            return;
        }

        foreach (var p in _actionSpec.Properties
                     .Where(p => p.Writable && (p.IsBool || p.IsNumeric || p.IsText))
                     .OrderBy(p => p.ServiceDesc, StringComparer.CurrentCulture)
                     .ThenBy(p => p.Piid))
        {
            ActionCapabilityOptions.Add(new OptionItem
            {
                Label = string.IsNullOrEmpty(p.Unit) ? p.Desc : $"{p.Desc}（{p.Unit}）",
                Value = $"{p.Siid}.{p.Piid}",
                Meta = p.ServiceDesc
            });
        }

        ActionEditorHint = ActionCapabilityOptions.Count == 0
            ? "这台设备没有可写的属性"
            : "选好属性和值，点「添加」";
        OnPropertyChanged(nameof(HasActionCapabilities));

        _fillingAction = true;
        SelectedActionCapabilityKey = ActionCapabilityOptions.FirstOrDefault()?.Value;
        _fillingAction = false;

        BuildActionDraft();
    }

    /// <summary>按选中的属性造一个写入节点（默认值取「开 / 第一个选项 / 最小值」），交给值控件改。</summary>
    private void BuildActionDraft()
    {
        ActionDraftValue = null;
        _actionProp = null;

        var key = SelectedActionCapabilityKey?.ToString() ?? "";
        if (_actionSpec == null || _actionDevice == null || key.Length == 0) return;

        var seg = key.Split('.');
        if (seg.Length != 2 ||
            !int.TryParse(seg[0], out var siid) ||
            !int.TryParse(seg[1], out var piid)) return;

        var prop = _actionSpec.FindProp(siid, piid);
        if (prop == null) return;
        _actionProp = prop;

        JsonNode? value;
        if (prop.IsBool) value = JsonValue.Create(true);
        else if (prop.Options.Count > 0) value = JsonValue.Create(prop.Options[0].Value);
        else if (prop.IsNumeric) value = JsonValue.Create(prop.HasRange ? Math.Round(prop.Min) : 0);
        else value = JsonValue.Create("");

        var write = new JsonObject
        {
            ["did"] = _actionDevice.Did,
            ["siid"] = siid,
            ["piid"] = piid,
            ["value"] = value
        };

        ActionDraftValue = SceneActionEditItem.Create(write, _actionDevice.Name, prop);
    }

    /// <summary>确认添加：按类型把草稿写进 scene_action.actions，并按当前顺序重排 order / group_id。</summary>
    [RelayCommand]
    private async Task ConfirmAddActionAsync()
    {
        if (_editingScene?.Raw == null) return;

        var kind = SelectedActionKindValue?.ToString();

        // 延时：云端没有独立动作节点，记为待生效，写在下一条确认添加的动作上
        if (kind == "delay")
        {
            var seconds = Math.Clamp((int)Math.Round(ActionDelaySeconds), 1, 86_400);
            _pendingDelaySeconds = seconds;
            IsActionEditorOpen = false;
            ResetActionDraft();
            Log($"已加入延时 {SceneEditActionRow.FormatDelay(seconds)}，将作用于下一条添加的动作");
            return;
        }

        var arr = ActionArray(create: true);
        if (arr == null) return;

        JsonObject node;
        string summary;
        if (kind == "scene")
        {
            var sid = SelectedActionSceneId?.ToString() ?? "";
            var target = _scenes.FirstOrDefault(s => string.Equals(s.SceneId, sid, StringComparison.Ordinal));
            if (target == null) { Log("先选一条要执行的场景"); return; }

            node = SceneDraftFactory.BuildRunSceneAction(target, arr.Count + 1);
            summary = $"执行「{target.Name}」";
        }
        else
        {
            if (ActionDraftValue is not { } draft || _actionDevice is not { } device || _actionProp is not { } prop)
            {
                Log("先选好设备和属性");
                return;
            }

            node = SceneDraftFactory.BuildDevicePropAction(device, prop, draft.Node, arr.Count + 1);
            summary = $"{device.Name} · {prop.Desc}";
        }

        // 待生效延时落到这条新动作上
        if (_pendingDelaySeconds > 0 && node["payload_json"] is JsonObject payload)
        {
            try { payload["delay_time"] = _pendingDelaySeconds; } catch { }
            summary = $"延时 {SceneEditActionRow.FormatDelay(_pendingDelaySeconds)} → {summary}";
            _pendingDelaySeconds = 0;
        }

        arr.Add(node);

        IsActionEditorOpen = false;
        ResetActionDraft();
        await RefreshActionRowsAsync();
        Log($"已添加动作：{summary}");
    }

    /// <summary>
    /// 删除一张动作卡（延时卡等于把这段的延时清零）。
    /// 做成弱确认：第一次点 × 只标红不删，3 秒内再点一次才真删，超时自动复位。
    /// </summary>
    [RelayCommand]
    private async Task RemoveSceneActionAsync(SceneEditActionRow? row)
    {
        if (row == null || _editingScene?.Raw == null) return;

        if (!row.PendingRemove)
        {
            var token = ++row.RemoveConfirmToken;
            row.PendingRemove = true;
            Log("再点一次 × 移除这一项");

            // 5 秒内没再点就自己退回去，避免卡片一直挂着红色待删态
            await Task.Delay(5000);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (row.RemoveConfirmToken == token) row.PendingRemove = false;
            });
            return;
        }

        row.RemoveConfirmToken++;   // 作废上一轮计时
        row.PendingRemove = false;

        if (row.IsDelay)
        {
            foreach (var p in row.DelayTargets)
            {
                try { p["delay_time"] = 0; } catch { }
            }
        }
        else
        {
            var arr = ActionArray(create: false);
            if (arr == null || row.Action is not { } node) return;
            arr.Remove(node);
        }

        await RefreshActionRowsAsync();
    }

    /// <summary>取（必要时创建）场景的动作数组。</summary>
    private JsonArray? ActionArray(bool create)
    {
        if (_editingScene?.Raw == null) return null;
        if (_editingScene.Raw["scene_action"] is JsonObject group && group["actions"] is JsonArray arr)
            return arr;
        if (!create) return null;

        var fresh = new JsonObject
        {
            ["mode"] = 1,
            ["actions"] = new JsonArray()
        };
        _editingScene.Raw["scene_action"] = fresh;
        return (JsonArray)fresh["actions"]!;
    }

    /// <summary>动作列表变过（增 / 删）之后按报文重建卡片，顺序与延时归属一并重排。</summary>
    private async Task RefreshActionRowsAsync()
    {
        if (_editingScene?.Raw == null) return;

        ResequenceSceneActions();
        var rows = await BuildSceneEditRowsAsync(_editingScene);
        var unsupported = rows.Count(r => r.IsUnknown);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplySceneEditRows(rows);
            SceneEditActionCount = rows.Count == 0 ? "" : $"{rows.Count} 项";
            SceneEditNote = rows.Count == 0
                ? "还没有动作：点「＋ 添加动作」加一条"
                : unsupported > 0
                    ? $"另有 {unsupported} 项动作类型本应用暂不支持编辑（已原样保留）"
                    : "";
        });
    }

    /// <summary>从「执行 “欢迎场景”」这类动作名里取出被引号包住的名字。</summary>
    private static string? QuotedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var start = name.IndexOfAny(new[] { '“', '"' });
        if (start < 0) return null;
        var end = name.LastIndexOfAny(new[] { '”', '"' });
        if (end <= start) return null;
        var text = name[(start + 1)..end].Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool IsFalse(JsonNode? node)
    {
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var b)) return !b;
        if (v.TryGetValue<int>(out var i)) return i == 0;
        return false;
    }

    [RelayCommand]
    private void CloseSceneEditor()
    {
        IsSceneEditorOpen = false;
        // 收起后回到「嵌在主窗口」的默认，免得下一次新建 / 抽屉入口因为残留的独立窗模式而看不见面板
        IsSceneEditorInline = true;
        SceneEditConfirmDelete = false;
        SceneEditError = "";
        IsTriggerEditorOpen = false;
        IsActionEditorOpen = false;
        ResetActionDraft();
        _pendingDelaySeconds = 0;
        _editingTriggerIndex = -1;
        SceneTriggerRows.Clear();

        // 取消未保存的改动：把原始报文换回快照（动作行持有的是旧树的节点，随快照一起丢弃）
        if (_editingScene != null && _editingSceneBackup != null)
            _editingScene.Raw = _editingSceneBackup;

        _editingScene = null;
        _editingSceneBackup = null;
        SceneEditRows.Clear();
        IsSceneNew = false;
    }

    /// <summary>保存：名称 + 启停 + 已改的动作值，整条对象回传给 Edit；新建模式即由此创建。</summary>
    [RelayCommand]
    private async Task SaveSceneEditorAsync()
    {
        if (_client == null || _editingScene == null || SceneEditBusy) return;

        var name = SceneEditName.Trim();
        if (name.Length == 0) { SceneEditError = "名称不能为空"; return; }

        // 待生效延时后面没等到动作：云端没地方放它，只能丢弃并说明
        if (_pendingDelaySeconds > 0)
        {
            Log($"有 {SceneEditActionRow.FormatDelay(_pendingDelaySeconds)} 的延时后面没有动作，本次保存不包含它");
            _pendingDelaySeconds = 0;
        }

        var isNew = IsSceneNew;
        if (isNew)
        {
            if (_editingScene.IsAutomation && SceneTriggerRows.Count == 0)
            {
                SceneEditError = "新建自动化至少要有一个触发条件：点「触发条件」右侧的「＋ 添加」";
                Log(SceneEditError);
                return;
            }
            if (SceneEditRows.All(r => r.IsVirtual))
            {
                SceneEditError = "新建场景至少要有一个动作：点「＋ 添加动作」加一条";
                Log(SceneEditError);
                return;
            }
        }

        SceneEditError = "";
        SceneEditBusy = true;
        try
        {
            var scene = _editingScene;
            var enable = SceneEditEnabled;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            ResequenceSceneActions();
            var sceneId = await _client.SaveSceneAsync(scene, body =>
            {
                body["name"] = name;
                body["enable"] = enable;
                if (!isNew) return;

                // 新建：scene_id=0 + 时间戳重写，其余字段沿用整条骨架
                body["scene_id"] = "0";
                body["enable_push"] = false;
                body["common_use"] = false;
                body["create_time"] = now;
                body["update_time"] = now;
            });
            Log(isNew ? $"已新建「{name}」，id={sceneId}" : $"已保存「{name}」");
            IsSceneEditorOpen = false;
            _editingScene = null;
            _editingSceneBackup = null;
            SceneEditRows.Clear();
            IsSceneNew = false;
            await LoadScenesAsync();
        }
        catch (Exception ex)
        {
            // 云端会逐条校验动作里的设备是否属于本家庭，命中时给出人话提示（探针里实测过这个报错）
            var msg = ex.Message;
            SceneEditError = msg.Contains("device home_id invalid", StringComparison.OrdinalIgnoreCase)
                ? "保存失败：这条自动化里有一台设备已不在当前家庭（可能被解绑或换过家庭），"
                  + "请到米家 App 里删掉它对应的那条动作后再保存"
                : $"保存失败：{msg}";
            Log(SceneEditError);
        }
        finally
        {
            SceneEditBusy = false;
        }
    }

    /// <summary>以当前场景为模板新建一条副本（默认关闭，避免刚建完就触发）。</summary>
    [RelayCommand]
    private async Task DuplicateSceneAsync()
    {
        if (_client == null || _editingScene == null || SceneEditBusy || IsSceneNew) return;

        SceneEditBusy = true;
        try
        {
            var newName = $"{SceneEditName.Trim()} 副本";
            var newId = await _client.DuplicateSceneAsync(_editingScene, newName);
            Log($"已新建副本「{newName}」（默认关闭），id={newId}");
            IsSceneEditorOpen = false;
            _editingScene = null;
            _editingSceneBackup = null;
            SceneEditRows.Clear();
            await LoadScenesAsync();
        }
        catch (Exception ex)
        {
            Log($"新建副本失败：{ex.Message}");
        }
        finally
        {
            SceneEditBusy = false;
        }
    }

    /// <summary>删除：两段式确认（第一下只变文案，第二下才真删）。</summary>
    [RelayCommand]
    private async Task DeleteSceneAsync()
    {
        if (_client == null || _editingScene == null || SceneEditBusy || IsSceneNew) return;

        if (!SceneEditConfirmDelete)
        {
            SceneEditConfirmDelete = true;
            return;
        }

        SceneEditBusy = true;
        try
        {
            var scene = _editingScene;
            var name = scene.Name;
            await _client.DeleteSceneAsync(scene);
            Log($"已删除「{name}」");
            IsSceneEditorOpen = false;
            _editingScene = null;
            _editingSceneBackup = null;
            SceneEditRows.Clear();
            await LoadScenesAsync();
        }
        catch (Exception ex)
        {
            Log($"删除失败：{ex.Message}");
        }
        finally
        {
            SceneEditBusy = false;
        }
    }

    /// <summary>did → 设备名，用于日志与提示；找不到时回退为「该设备」。</summary>
    private string DeviceNameOf(string did)
    {
        var device = _allDevices.FirstOrDefault(d => MijiaScene.SameDid(d.Did, did))
                     ?? _allDevices.FirstOrDefault(d => MijiaScene.SameDid(d.TargetDid, did));
        return device?.Name ?? "该设备";
    }

    /// <summary>场景执行后设备状态会变，但整页重载会引起卡片闪动，这里只静默刷新开关态。</summary>
    private async Task RefreshStatesQuietAsync()
    {
        await RefreshStatesAsync(includeMetrics: false);
    }

    /// <summary>日志只保留最近约 80 行，避免常驻托盘时 LogText 无限膨胀拖慢绑定。</summary>
    private const int MaxLogLines = 80;

    /// <summary>关于页版本号：取程序集版本，随 package.cmd -Bump 变化，避免手写常亮。</summary>
    public string AppVersionText { get; } =
        $"版本 {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"} · by Feiyu";

    /// <summary>关于页「开源仓库」链接：Windows 版指向上游仓库；macOS 版指到承载该二进制的 fork
    /// （GPL-3.0 第 6 节要求能拿到与二进制对应的完整源码，macOS 代码只存在于 fork 的移植分支上）。</summary>
    public string RepoUrl { get; } = OperatingSystem.IsMacOS()
        ? "https://github.com/woaiCarlos/MiHomeHub"
        : "https://github.com/Feiyudevs/MiHomeHub";

    /// <summary>关于页「作者网站」链接。</summary>
    public string AuthorUrl { get; } = "https://feiyux.cn/";

    /// <summary>关于页「哔哩哔哩」链接：作者 B 站主页。</summary>
    public string BilibiliUrl { get; } = "https://space.bilibili.com/3546951903611444";

    /// <summary>用系统默认浏览器打开外链（官网 / 开源仓库等）。</summary>
    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"打开链接失败：{url} - {ex.Message}");
        }
    }

    /// <summary>启动时的无感检查：失败不提示；发现新版本用托盘气泡轻提示，强制更新才弹模态框。</summary>
    private async Task AutoCheckUpdateAsync()
    {
        try { ApplyUpdateResult(await UpdateService.CheckAsync(force: false), notifyUser: true); }
        catch { /* 后台检查失败不影响任何功能 */ }
    }

    /// <summary>
    /// 托盘与主窗口就绪后调用：用上次检查的缓存结果补一次提示。
    /// 缓存新鲜时启动不会联网，若不发这一步，发版后重启的机器要等满 24 小时才知道有新版本。
    /// </summary>
    public void NotifyCachedUpdate()
    {
        if (UpdateService.TryGetCachedResult() is { State: UpdateState.Available } cached)
            ApplyUpdateResult(cached, notifyUser: true, trackTelemetry: false);
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (IsUpdateBusy) return;
        IsUpdateBusy = true;
        UpdateNotes = "";
        UpdateNotesPreview = "";
        IsUpdateNotesExpanded = false;
        HasLongUpdateNotes = false;
        UpdateStatusText = "正在检查更新…";
        try
        {
            var result = await UpdateService.CheckAsync(force: true);
            ApplyUpdateResult(result, notifyUser: false);
            // 关于页点「检查更新」查到新版本：直接弹详情弹窗，不用再找下一个入口
            if (IsUpdateAvailable && !IsForceUpdateOpen) OpenUpdateDetails();
        }
        catch (Exception ex)
        {
            UpdateStatusText = "检查更新失败：" + ex.Message;
            Log("检查更新失败：" + ex.Message);
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleUpdateNotes() => IsUpdateNotesExpanded = !IsUpdateNotesExpanded;

    partial void OnIsUpdateNotesExpandedChanged(bool value) => SyncUpdateNotesVisibility();

    private void SyncUpdateNotesVisibility()
    {
        ShowUpdateNotesCollapsed = HasLongUpdateNotes && !IsUpdateNotesExpanded;
        HasShortUpdateNotes = UpdateNotes.Length > 0 && !HasLongUpdateNotes;
        UpdateNotesToggleText = IsUpdateNotesExpanded ? "收起说明" : "查看完整说明";
    }

    private void ApplyUpdateResult(UpdateCheckResult result, bool notifyUser, bool trackTelemetry = true)
    {
        _pendingUpdate = result.Manifest;
        IsUpdateAvailable = result.State == UpdateState.Available && result.Manifest is not null;
        UpdateStatusText = result.Message;
        UpdateNotes = result.Manifest?.Notes?.Trim() ?? "";
        UpdateNotesPreview = UpdateNotes.Length <= UpdateNotesPreviewLimit
            ? UpdateNotes
            : UpdateNotes[..UpdateNotesPreviewLimit].TrimEnd() + "…";
        HasLongUpdateNotes = UpdateNotes.Length > UpdateNotesPreviewLimit;
        HasShortUpdateNotes = UpdateNotes.Length > 0 && !HasLongUpdateNotes;
        IsUpdateNotesExpanded = false;
        SyncUpdateNotesVisibility();
        IsForceUpdateOpen = false;
        if (trackTelemetry)
            TelemetryService.Track("update_check", "检查更新", new Dictionary<string, object?>
            {
                ["channel"] = UpdateService.CurrentChannel,
                ["result"] = result.State switch
                {
                    UpdateState.Available => "available",
                    UpdateState.UpToDate => "latest",
                    _ => "failed",
                },
                ["version"] = result.Manifest?.Version,
            });
        if (IsUpdateAvailable)
        {
            Log($"发现新版本 {result.Manifest?.Version}（当前 {UpdateService.CurrentVersion}，通道 {UpdateService.CurrentChannel}）");
            // 标题栏提示条：X 掉之后本次运行不再弹回，直到重启
            UpdateBannerText = $"检测到新版本 {result.Manifest?.Version}";
            UpdateDialogTitle = $"发现新版本 {result.Manifest?.Version}";
            UpdateDiskUrl = result.Manifest?.DiskUrl?.Trim() ?? "";
            HasUpdateDiskUrl = UpdateDiskUrl.Length > 0;
            if (!_updateBannerDismissed) IsUpdateBannerVisible = true;
        }
        else
        {
            UpdateDiskUrl = "";
            HasUpdateDiskUrl = false;
            IsUpdateBannerVisible = false;
            IsUpdateDetailsOpen = false;
        }

        if (!IsUpdateAvailable) return;

        var version = result.Manifest?.Version ?? "新版本";
        // 后台标了 force：必须升级，弹不可跳过的框（只有这一种情况用模态）
        if (result.Manifest?.Force == true)
        {
            IsForceUpdateOpen = true;
            return;
        }

        // 启动自动检查：托盘气泡轻提示，不抢焦点；手动检查时用户就在卡片前，不再重复打扰
        if (notifyUser && !_startupUpdateNotified)
        {
            _startupUpdateNotified = true;
            NotifyUser?.Invoke($"发现新版本 {version}", "打开「设置 → 关于」查看更新说明并安装");
        }
    }

    [RelayCommand]
    private void CloseForceUpdate() => IsForceUpdateOpen = false;

    /// <summary>标题栏提示条上的 X：本次运行内不再出现（下次启动重新评估）。</summary>
    [RelayCommand]
    private void DismissUpdateBanner()
    {
        _updateBannerDismissed = true;
        IsUpdateBannerVisible = false;
    }

    /// <summary>标题栏「查看详情」：弹出独立的更新详情弹窗（版本说明 + 立即更新 / 稍后）。</summary>
    [RelayCommand]
    private void OpenUpdateDetails()
    {
        UpdateDialogTitle = _pendingUpdate?.Version is { Length: > 0 } v
            ? $"发现新版本 {v}"
            : "发现新版本";
        IsUpdateDetailsOpen = true;
        // 已经看到详情，标题栏那条先收起，避免同一信息占两处
        IsUpdateBannerVisible = false;
    }

    [RelayCommand]
    private void CloseUpdateDetails() => IsUpdateDetailsOpen = false;

    [RelayCommand]
    private void ExitApp() => RequestExit?.Invoke();

    /// <summary>
    /// 下载并校验安装包后交给自研安装器静默更新：先关掉自己，安装器再做目录轮转，
    /// 失败会回滚并重新拉起旧版本（见 SetupService 的事务与回滚）。
    /// </summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is not { } manifest || IsUpdateBusy) return;

        // 便携运行（直接跑 publish\sc-win-x64\MiHomeHub.exe）不自动更新：安装器会装到默认目录并另起一份
        if (!UpdateService.IsRunningFromInstall(out var installRoot))
        {
            UpdateStatusText = OperatingSystem.IsMacOS()
                ? "请从 .app 里启动后再更新；直接跑可执行文件无法自动替换应用"
                : "便携运行请到 Release 页下载安装包；自动更新只对已安装版本生效";
            Log("自动更新已跳过：当前不是安装目录里的实例");
            return;
        }

        IsUpdateBusy = true;
        UpdateProgress = 0;
        UpdateStatusText = $"正在下载 {manifest.Version}…";
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p;
                UpdateStatusText = p >= 1
                    ? "正在校验安装包…"
                    : $"正在下载 {manifest.Version}… {(int)Math.Round(p * 100)}%";
            });

            var setup = await UpdateService.DownloadAsync(manifest, progress);
            TelemetryService.Track("update_download", "下载更新包", new Dictionary<string, object?>
            {
                ["channel"] = UpdateService.CurrentChannel,
                ["ok"] = true,
                ["version"] = manifest.Version,
            });
            UpdateStatusText = OperatingSystem.IsMacOS() ? "正在替换应用并重启…" : "正在启动安装程序…";
            Log($"更新包已就绪：{setup}（目标 {installRoot}）");
            TelemetryService.Track("update_install", "安装更新", new Dictionary<string, object?>
            {
                ["channel"] = UpdateService.CurrentChannel,
                ["version"] = manifest.Version,
                ["stage"] = "launch",
            });
            UpdateService.LaunchInstaller(setup);

            await Task.Delay(400);
            IsSettingsOpen = false;
            RequestExit?.Invoke();
        }
        catch (Exception ex)
        {
            UpdateStatusText = "更新失败：" + ex.Message;
            TelemetryService.Track("update_download", "下载更新包", new Dictionary<string, object?>
            {
                ["channel"] = UpdateService.CurrentChannel,
                ["ok"] = false,
                ["version"] = manifest.Version,
            });
            Log("更新失败：" + ex.Message);
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    /// <summary>设备数分桶：统计只关心量级，不关心具体设备。</summary>
    private static string DeviceCountBucket(int count) => count switch
    {
        <= 0 => "0",
        <= 5 => "1-5",
        <= 20 => "6-20",
        _ => "20+",
    };

    /// <summary>应用内日志面板（弹窗宿主 catch 到的异常也从这里冒出来）。</summary>
    public void Log(string msg)
    {
        var next = $"[{DateTime.Now:HH:mm:ss}] {msg}\n" + LogText;
        if (next.Length > 8000)
        {
            var cut = next.LastIndexOf('\n', Math.Min(next.Length - 1, 6000));
            if (cut > 0) next = next[..(cut + 1)];
        }
        LogText = next;
    }
}