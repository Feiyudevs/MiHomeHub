using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;
using MiHome.Services;
using QRCoder;

namespace MiHome.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly MijiaAuth _auth;
    private MijiaClient? _client;
    private List<MijiaDevice> _allDevices = new();
    private string _selectedRoom = "全部";

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

    /// <summary>状态轮询：4 秒一拍，每 4 拍顺带刷新温湿度读数。</summary>
    private DispatcherTimer? _pollTimer;
    private bool _polling;
    private int _pollTick;
    private long _lastPollErrorLogAt;
    private const int PollSeconds = 10;
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
    [ObservableProperty] private bool _showMainWindowOnStartup = AppSettingsStore.ShowMainWindowOnStartup;
    [ObservableProperty] private bool _closeToTray = AppSettingsStore.CloseToTray;

    /// <summary>设置弹窗当前 Tab：0 通用 / 1 桌面组件 / 2 账户 / 3 关于。</summary>
    [ObservableProperty] private int _settingsTab;

    /// <summary>写注册表失败回滚开关时置位，避免回滚本身又触发一次写入。</summary>
    private bool _revertingAutoStart;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _onlineCount;
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
    [ObservableProperty] private int _bannerTotal;
    [ObservableProperty] private int _bannerOn;
    [ObservableProperty] private int _bannerOffline;

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
    public ObservableCollection<HomeOption> HomeOptions { get; } = new();
    public ObservableCollection<MijiaDevice> Devices { get; } = new();
    public ObservableCollection<DeviceGroup> HomeGroups { get; } = new();
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

    /// <summary>设备详情抽屉里的「相关场景」：会控制本设备的场景 + 被本设备触发的自动化。</summary>
    public ObservableCollection<SceneCardItem> DrawerScenes { get; } = new();

    /// <summary>场景页分段：0 场景 / 1 自动化。</summary>
    [ObservableProperty] private int _sceneTab;

    /// <summary>设备 → 场景 反查表，随场景列表重建，抽屉打开时零网络开销。</summary>
    private readonly SceneIndexService _sceneIndex = new();

    /// <summary>最近一次拉到的场景全集（手动 + 自动化）。</summary>
    private List<MijiaScene> _scenes = new();

    public bool HasAutomations => AutomationItems.Count > 0;
    public bool HasDrawerScenes => DrawerScenes.Count > 0;

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
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowSceneSkeleton));
        OnPropertyChanged(nameof(ShowQuickScenes));
        OnPropertyChanged(nameof(ShowEnvSkeleton));
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

    /// <summary>概览统计卡的骨架槽位：真实统计卡固定四张（在线/灯光/窗帘/空调），占位数量与之一致。</summary>
    public IReadOnlyList<string> StatSkeletonSlots { get; } =
        Enumerable.Range(0, 4).Select(i => i.ToString()).ToArray();

    /// <summary>环境胶囊的骨架槽位：温度/湿度/耗材三枚。</summary>
    public IReadOnlyList<string> EnvSkeletonSlots { get; } =
        Enumerable.Range(0, 3).Select(i => i.ToString()).ToArray();

    /// <summary>列表还空着时铺骨架；已有设备只是刷新状态，不动内容。</summary>
    public bool ShowSkeleton => SyncPending && TotalCount == 0;

    /// <summary>还在首拉且环境读数没到货时铺胶囊占位；读数到了立刻换真胶囊。</summary>
    public bool ShowEnvSkeleton => SyncPending && !ShowEnvOverview;

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
    public string SearchWatermark => SelectedNavIndex == 2 ? "搜索场景、自动化…" : "搜索设备、房间…";

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
    }

    /// <summary>暂停状态轮询（窗口最小化/收托盘时调用，减少后台网络与分配）。</summary>
    public void PausePolling()
    {
        _pollTimer?.Stop();
    }

    /// <summary>恢复状态轮询（窗口从托盘/最小化恢复时调用）。</summary>
    public void ResumePolling()
    {
        if (_pollTimer is { IsEnabled: false }) _pollTimer.Start();
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
        }
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        SyncRoomChips();
        // 水印跟着页走：首页/房间搜设备，智能页搜场景与自动化
        OnPropertyChanged(nameof(SearchWatermark));
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

    partial void OnAutoStartChanged(bool value)
    {
        if (_revertingAutoStart) return;
        if (StartupRegistration.Apply(value))
        {
            AppSettingsStore.SetAutoStart(value);
            Log(value ? "已开启开机启动" : "已关闭开机启动");
            return;
        }

        _revertingAutoStart = true;
        AutoStart = !value;
        _revertingAutoStart = false;
        Log("开机启动设置失败：当前用户注册表不可写");
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
        foreach (var d in cached) d.ApplyLegacyDefaults();
        Dispatcher.UIThread.Post(() =>
        {
            _allDevices = cached;
            RebuildRooms();
            ApplyFilter();
            RebuildHomeGroups();
            UpdateStats();
            RefreshSpeakers();
        });
        Log($"已加载本地缓存的 {cached.Count} 个设备");
        _ = LoadIconsAsync(cached);
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        if (_client == null) return;
        IsLoading = true;
        try
        {
            Log("正在拉取设备列表…");
            // 网络请求的 await 会在线程池回来，ObservableCollection 的增删必须切回 UI 线程，
            // 否则 ItemsControl 收不到集合通知，页面上就一个卡片都不显示。
            var fresh = await _client.GetAllDevicesAsync();
            var homes = _client.Homes.ToList();
            foreach (var d in fresh) d.ApplyLegacyDefaults();
            var gen = ++_loadGeneration;

            // 设备到手就先渲染并落盘，开关状态和场景随后补，不让用户干等
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SyncHomeOptions(homes);
                // 复用已有实例（按 did 匹配），避免每轮刷新把整屏卡片重新绑定一遍
                _allDevices = MergeDevices(_allDevices, fresh);
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

            Log($"已加载 {_allDevices.Count} 个设备、{SceneItems.Count} 个场景");

            // 设备加载完成后立即收缩原生渲染资源：大量卡片渲染会产生 Avalonia 合成层与 GPU 纹理，
            // GC 不会自动回收这些原生内存，需主动调用 SetProcessWorkingSetSize 让 Windows 收缩工作集。
            Services.MemoryTrimmer.TrimForce();
        }
        catch (Exception ex)
        {
            Log($"拉取设备失败：{ex.Message}");
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

    /// <summary>把 homelist 同步成标题栏下拉项；选中项尽量保持用户当前选择。</summary>
    private void SyncHomeOptions(List<MijiaHome> homes)
    {
        var prevId = SelectedHome?.Id;
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

    /// <summary>按 SelectedHome 拉该家庭下的设备。</summary>
    private async Task LoadDevicesForSelectedHomeAsync()
    {
        if (_client == null || SelectedHome == null) return;
        BeginInitialSync();
        IsLoading = true;
        try
        {
            var home = _client.FindHome(SelectedHome.Id);
            if (home == null)
            {
                // 极端情况：本地选中项还在，云端列表已刷过 — 强刷 homelist 再找一次
                var homes = await _client.GetHomesAsync(forceRefresh: true);
                await Dispatcher.UIThread.InvokeAsync(() => SyncHomeOptions(homes.ToList()));
                home = _client.FindHome(SelectedHome.Id);
                if (home == null)
                {
                    FinishInitialSync();
                    return;
                }
            }

            Log($"切换到家庭「{home.Name}」，正在拉取设备…");
            var fresh = await LoadHomeDevicesAsync(home);
            foreach (var d in fresh) d.ApplyLegacyDefaults();
            var gen = ++_loadGeneration;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                HomeName = string.IsNullOrWhiteSpace(home.Name) ? "我的家" : home.Name.Trim();
                // 同一 did 复用旧实例；换家庭时 did 不同，旧实例自然被丢弃
                _allDevices = MergeDevices(_allDevices, fresh);
                RebuildRooms();
                ApplyFilter();
                RebuildHomeGroups();
                UpdateStats();
                RefreshSpeakers();
            });

            var devices = _allDevices;
            _ = Task.Run(() => DeviceCacheStore.Save(devices));

            await Task.WhenAll(LoadScenesAsync(), RefreshStatesAsync(includeMetrics: false));
            _ = LoadIconsAsync(devices);
            _ = ResolveSpecsThenRefreshAsync(devices, gen);
            Log($"「{HomeName}」已加载 {_allDevices.Count} 个设备、{SceneItems.Count} 个场景");

            // 设备加载完成后立即收缩原生渲染资源
            Services.MemoryTrimmer.TrimForce();
        }
        catch (Exception ex)
        {
            Log($"切换家庭失败：{ex.Message}");
            // 拉不到设备就没有后台读数链，骨架要在这里收尾
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
        if (generation != _loadGeneration) return;

        // 规格解析后才知道哪些是音箱、哪些有耗材；这里统一刷新这两块的候选与汇总
        RefreshSpeakers();
        RebuildConsumables();
        RecomputeEnvironment();
    }

    /// <summary>
    /// 读在线设备的开关/通道/窗帘状态；includeMetrics 时顺带读温湿度。
    /// 刚下发过指令的设备（抑制窗口内）直接跳过，避免本地乐观态被旧状态覆盖。
    /// </summary>
    private async Task RefreshStatesAsync(bool includeMetrics)
    {
        if (_client is not { } client) return;
        var now = Environment.TickCount64;
        var query = new List<(string Did, int Siid, int Piid)>();
        var keys = new HashSet<(string, int, int)>();

        foreach (var d in _allDevices)
        {
            if (!d.IsOnline || d.PendingUntilMs > now) continue;
            var did = d.TargetDid;

            // 音箱音量单独读，不依赖有没有电源属性（播放类设备常常没有 on/off）
            if (d.IsSpeaker && d.SpeakerVolumeSiid > 0) Add((did, d.SpeakerVolumeSiid, d.SpeakerVolumePiid));
            // 播放状态：有 playing-state 属性时回读，播放/暂停图标跟着真实状态走
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

            if (includeMetrics)
            {
                if (d.Spec?.FindMetric("temperature") is { } t) Add((did, t.Siid, t.Piid));
                if (d.Spec?.FindMetric("relative-humidity") is { } h) Add((did, h.Siid, h.Piid));
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

                if (includeMetrics)
                {
                    if (ApplyMetrics(d, map)) changed = true;
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

    /// <summary>卡片副标题读数：温度/湿度；部分传感器用 0.1 为单位，超阈值时缩回。</summary>
    private static bool ApplyMetrics(MijiaDevice d,
        Dictionary<(string Did, int Siid, int Piid), object?> map)
    {
        var spec = d.Spec;
        if (spec == null) return false;
        var parts = new List<string>(2);
        d.TemperatureC = null;
        d.Humidity = null;

        if (spec.FindMetric("temperature") is { } t &&
            map.TryGetValue((d.TargetDid, t.Siid, t.Piid), out var tv) && tv != null)
        {
            var temp = ToDouble(tv);
            if (temp > 60) temp /= 10;
            d.TemperatureC = temp;
            parts.Add($"{temp:0.#}℃");
        }
        if (spec.FindMetric("relative-humidity") is { } h &&
            map.TryGetValue((d.TargetDid, h.Siid, h.Piid), out var hv) && hv != null)
        {
            var hum = ToDouble(hv);
            if (hum > 100) hum /= 10;
            d.Humidity = hum;
            parts.Add($"{hum:0.#}%");
        }

        var text = string.Join(" · ", parts);
        if (d.MetricsText == text) return false;
        d.MetricsText = text;
        return true;
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
        using var gate = new SemaphoreSlim(5);
        var ok = 0;
        try
        {
            await Task.WhenAll(groups.Select(async g =>
            {
                await gate.WaitAsync();
                try
                {
                    var bmp = await MijiaIconService.GetAsync(g.Key);
                    if (bmp == null) return;
                    ok++;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var d in g) d.Icon = bmp;
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
        });
    }

    /// <summary>排序键：创建时间（unix 秒字符串），解析失败按 0（沉底）。</summary>
    private static long Newness(MijiaScene s) =>
        long.TryParse(s.CreateTime, out var t) ? t : 0;

    /// <summary>抽屉里的「相关场景」：命中内存索引，不发网络请求。</summary>
    private void UpdateDrawerScenes()    {
        DrawerScenes.Clear();
        if (DrawerDevice is { } device)
        {
            var seed = 0;
            foreach (var r in _sceneIndex.ForDevice(device.Did))
            {
                var card = SceneCardItem.Wrap(r.Scene, seed++);
                card.RoleText = r.RoleText;
                card.TargetDid = device.TargetDid;
                DrawerScenes.Add(card);
            }
        }
        OnPropertyChanged(nameof(HasDrawerScenes));
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
        var names = _allDevices.Select(d => d.RoomName)
            .Where(n => !string.IsNullOrEmpty(n))
            .GroupBy(n => n)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();
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
                return;
            }
        }

        Rooms.Clear();
        Rooms.Add(new RoomChip("全部"));
        foreach (var n in names)
            Rooms.Add(new RoomChip(n));
        RoomCount = names.Count;
        SyncRoomChips();
    }

    /// <summary>侧栏只允许一处高亮：只有停在「房间」页时，当前房间才显示选中态。</summary>
    private void SyncRoomChips()
    {
        foreach (var c in Rooms)
            c.IsSelected = SelectedNavIndex == 1 && c.Name == _selectedRoom;
    }

    private void ApplyFilter()
    {
        var list = OrderDevices(_allDevices
            .Where(d => (_selectedRoom == "全部" || d.RoomName == _selectedRoom) && MatchesSearch(d))).ToList();

        // 集合没变就别重建：Clear+Add 会让每张卡片重新绑定数据、重排文字，
        // 而重排会在 Avalonia 的字体缓存里不断新建字体对象（只增不减），
        // 每轮刷新都整批重建的话内存会随刷新次数一直涨。
        if (!SameSequence(Devices, list))
        {
            Devices.Clear();
            foreach (var d in list) Devices.Add(d);
        }

        HasDevices = Devices.Count > 0;
        UpdateBanner();
    }

    private void RebuildHomeGroups()
    {
        var groups = new List<DeviceGroup>();
        foreach (var g in _allDevices
                     .GroupBy(d => d.RoomName)
                     .OrderByDescending(g => g.Count()))
        {
            var items = OrderDevices(g.Where(MatchesSearch)).ToList();
            if (items.Count == 0) continue;
            groups.Add(new DeviceGroup { Name = g.Key, Total = g.Count(), Items = items });
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

    private void UpdateStats()
    {
        TotalCount = _allDevices.Count;
        OnlineCount = _allDevices.Count(d => d.IsOnline);
        OfflineCount = TotalCount - OnlineCount;
        HasScenes = SceneItems.Count > 0;
        TotalText = $"/ {TotalCount}";
        OfflineText = $"离线 {OfflineCount}";
        HasOffline = OfflineCount > 0;
        // 三张类型卡：数字是「在线且开」，斜杠后是该类型总数，与弹窗清单同一口径
        var lights = _allDevices.Where(d => d.IconKey == "light").ToList();
        var curtains = _allDevices.Where(d => d.IconKey == "curtain").ToList();
        var acs = _allDevices.Where(d => d.IconKey == "ac").ToList();
        LightOnCount = lights.Count(d => d.IsOnline && d.IsOn);
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
    /// 同步统计卡弹窗的清单（弹窗开着时轮询与开关动作都会走到这里，设备状态变了列表跟着变）。
    /// 同实例同顺序时不重建：Clear+Add 会让每张卡重新绑定并重排文字，
    /// 而重排在字体缓存里只增不减，每轮刷新整批重建会把内存一直顶上去。
    /// </summary>
    private void RefreshStatList()
    {
        var kind = _statListKind;
        var list = kind switch
        {
            StatListKind.Lights => OnFirst("light", _allDevices),
            StatListKind.Curtains => OnFirst("curtain", _allDevices),
            StatListKind.Ac => OnFirst("ac", _allDevices),
            _ => OrderDevices(_allDevices.Where(d => !d.IsOnline)).ToList(),
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
            ? _allDevices
            : _allDevices.Where(d => d.RoomName == _selectedRoom).ToList();
        BannerTitle = _selectedRoom == "全部" ? "全屋设备" : _selectedRoom;
        BannerDesc = _selectedRoom == "全部"
            ? "统一管理家中所有智能设备与传感器"
            : $"{_selectedRoom} 内的设备与状态";
        BannerIconKey = Rooms.FirstOrDefault(c => c.Name == _selectedRoom)?.IconKey ?? "house";
        BannerTotal = list.Count;
        BannerOn = list.Count(d => d.IsOn);
        BannerOffline = list.Count(d => !d.IsOnline);
    }

    /// <summary>
    /// 环境概览：优先取一台同时有温湿度的在线设备作为展示来源，否则对全屋读数取平均。
    /// 明细里列出每台有读数的设备，耗材单独在 <see cref="RebuildConsumables"/> 汇总。
    /// </summary>
    private void RecomputeEnvironment()
    {
        var readings = _allDevices.Where(d => !string.IsNullOrEmpty(d.MetricsText)).ToList();
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

        var source = _allDevices.FirstOrDefault(d => d.IsOnline && d.TemperatureC.HasValue && d.Humidity.HasValue);
        double? temp = source?.TemperatureC;
        double? hum = source?.Humidity;
        if (source == null)
        {
            var temps = _allDevices.Where(d => d.TemperatureC.HasValue).Select(d => d.TemperatureC!.Value).ToList();
            var hums = _allDevices.Where(d => d.Humidity.HasValue).Select(d => d.Humidity!.Value).ToList();
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

    [RelayCommand]
    private void PickRoom(RoomChip? chip)
    {
        if (chip == null) return;
        _selectedRoom = chip.Name;
        SelectedNavIndex = 1;
        SyncRoomChips();
        ApplyFilter();
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void OpenLog() => IsLogOpen = true;

    [RelayCommand]
    private void CloseOverlays()
    {
        // 关掉登录面板就打断长轮询，否则命令一直执行中，再点登录会「无反应」
        if (IsLoginOpen) CancelAuth();
        if (IsDrawerOpen) ClearSpecPanel();
        IsDrawerOpen = false;
        IsLoginOpen = false;
        IsSettingsOpen = false;
        IsEnvDetailOpen = false;
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
                foreach (var card in SceneItems.Concat(AutomationItems).Concat(DrawerScenes))
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
    [ObservableProperty] private string _sceneEditName = "";
    [ObservableProperty] private bool _sceneEditEnabled;
    [ObservableProperty] private string _sceneEditTrigger = "";
    [ObservableProperty] private string _sceneEditCondition = "";
    [ObservableProperty] private string _sceneEditActionCount = "";
    [ObservableProperty] private string _sceneEditNote = "";
    [ObservableProperty] private bool _sceneEditBusy;
    [ObservableProperty] private bool _sceneEditConfirmDelete;

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
                var did = node["extra_json"]?["did"]?.ToString() ?? "";

                if (src == "device")
                {
                    var dev = _allDevices.FirstOrDefault(d => MijiaScene.SameDid(d.Did, did));
                    var key = node["key"]?.ToString() ?? "";
                    var desc = await SceneTriggerCatalog.DescribeAsync(_client, dev, node);
                    SceneTriggerRows.Add(new SceneTriggerRow
                    {
                        Index = i,
                        Title = string.IsNullOrWhiteSpace(name) ? desc : name,
                        Subtitle = $"{dev?.Name ?? node["extra_json"]?["device_name"]?.ToString() ?? "设备"} · {desc}",
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

        await OpenSceneEditorCoreAsync(scene, isNew: false);
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
            row.MarkReady();
            rows.Add(row);
        }

        return rows;
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

        // 执行另一条场景
        if (type == 2)
        {
            var sid = payload?["scene_id"]?.ToString() ?? "";
            var option = sceneOptions.FirstOrDefault(o => Equals(o.Value, sid));
            var display = option?.Label ?? QuotedName(name) ?? "未知场景";

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

        // 开启 / 关闭另一条场景
        if (type == 3)
        {
            var sid = payload?["scene_id"]?.ToString() ?? "";
            var display = sceneOptions.FirstOrDefault(o => Equals(o.Value, sid))?.Label
                          ?? QuotedName(name) ?? "未知场景";
            var enable = !IsFalse(payload?["enable"]);

            var row = new SceneEditActionRow
            {
                Action = action,
                Payload = payload,
                Kind = SceneEditRowKind.SceneToggle,
                Title = $"开关场景「{display}」",
                Subtitle = enable ? "执行后开启该场景" : "执行后关闭该场景",
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
                var row = new SceneEditActionRow
                {
                    Action = action,
                    Payload = payload,
                    Kind = SceneEditRowKind.DeviceProp,
                    Title = shown,
                    // 副标题把房间和属性名并成一行，值控件就能跟标题同一行，省掉一整行高度
                    Subtitle = string.Join(" · ", new[]
                    {
                        device?.RoomName is { Length: > 0 } r && r != "未分配房间" ? r : null,
                        values[0].Title
                    }.Where(x => !string.IsNullOrWhiteSpace(x))),
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

    /// <summary>关于页「官网」按钮占位链接，替换为真实地址即可生效。</summary>
    public string OfficialSiteUrl { get; } = "https://example.com";

    /// <summary>关于页「开源仓库」按钮占位链接，替换为真实地址即可生效。</summary>
    public string RepoUrl { get; } = "https://github.com/";

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