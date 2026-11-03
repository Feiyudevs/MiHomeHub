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
    private const int PollSeconds = 4;
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
    public bool ShowHomeEmpty => !SyncPending && TotalCount == 0 && !HasScenes;

    /// <summary>场景页空态：加载结束后仍没有场景。</summary>
    public bool ShowSceneEmpty => !SyncPending && !HasScenes;

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

    /// <summary>轮询一拍：在途请求未回时直接跳过，避免网络抖动时请求叠加。</summary>
    private async Task PollTickAsync()
    {
        if (_client == null || !IsLoggedIn || _polling) return;
        _polling = true;
        try
        {
            _pollTick++;
            await RefreshStatesAsync(includeMetrics: _pollTick % 4 == 1);
        }
        finally
        {
            _polling = false;
        }
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        SyncRoomChips();
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

    partial void OnCloseToTrayChanged(bool value)
    {
        AppSettingsStore.SetCloseToTray(value);
        Log(value ? "关闭窗口后收进托盘" : "关闭窗口即退出应用");
    }

    partial void OnSearchTextChanged(string value)
    {
        // 高频键入只改搜索框，列表重建压到防抖尾部（Zonex：防抖收口）
        _searchDebounce?.Dispose();
        _searchDebounce = DispatcherTimer.RunOnce(() =>
        {
            _searchDebounce = null;
            ApplyFilter();
            RebuildHomeGroups();
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
            scenes = await _client.GetScenesAsync();
        }
        catch (Exception ex)
        {
            Log($"拉取场景失败：{ex.Message}");
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SceneItems.Clear();
            var i = 0;
            foreach (var s in scenes) SceneItems.Add(SceneCardItem.Wrap(s, i++));
            UpdateStats();
        });
    }

    private bool MatchesSearch(MijiaDevice d)
    {
        var q = SearchText.Trim();
        if (q.Length == 0) return true;
        return d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || d.RoomName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>两组设备是否同一批实例、同样顺序（用于跳过无意义的重建）。</summary>
    private static bool SameSequence(IReadOnlyList<MijiaDevice> a, IReadOnlyList<MijiaDevice> b)
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

    private void Log(string msg)
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