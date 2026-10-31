using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

    /// <summary>详情抽屉里灯亮度/色温的防抖提交定时器（拖动过程中不连发请求）。</summary>
    private IDisposable? _lightCommitTimer;

    /// <summary>抽屉里音箱音量滑杆的防抖提交定时器。</summary>
    private IDisposable? _speakerVolumeTimer;

    /// <summary>轮询回填进行中：此时属性变化来自服务端，不应触发反向提交。</summary>
    private bool _applyingStates;

    /// <summary>搜索防抖：连续输入只在静默 250ms 后重滤一次，避免每键全量 Clear+Add。</summary>
    private IDisposable? _searchDebounce;

    /// <summary>当前详情设备及其属性订阅，用于把亮度/色温变化提交回设备。</summary>
    private MijiaDevice? _detailDevice;
    private PropertyChangedEventHandler? _detailHandler;

    /// <summary>正在回填详情值，期间忽略属性变化，避免把刚读到的值又当成用户改动提交一次。</summary>
    private bool _loadingDetail;

    /// <summary>状态轮询：4 秒一拍，每 4 拍顺带刷新温湿度读数。</summary>
    private DispatcherTimer? _pollTimer;
    private bool _polling;
    private int _pollTick;
    private long _lastPollErrorLogAt;
    private const int PollSeconds = 4;
    private const int SuppressMs = 5000;

    /// <summary>设备列表代次：切家庭/重载时递增，过期的规格解析结果直接丢弃。</summary>
    private int _loadGeneration;

    /// <summary>高级设置：抽屉侧边栏只留常用控制卡，其余规格属性（按 MIoT 服务分组）与无入参动作全部收进这里。</summary>
    public ObservableCollection<SpecGroupVm> DrawerSpecAdvancedGroups { get; } = new();
    public ObservableCollection<SpecActionVm> DrawerSpecAdvancedActions { get; } = new();

    [ObservableProperty] private bool _hasAdvancedPanel;
    [ObservableProperty] private bool _hasAdvancedActions;
    [ObservableProperty] private bool _isAdvancedExpanded;

    /// <summary>高级设置区是否有内容（属性组或动作任一存在）。</summary>
    public bool HasAdvanced => HasAdvancedPanel || HasAdvancedActions;

    /// <summary>高级设置里属性组与动作之间那条分割线：两段都有内容才画。</summary>
    public bool HasAdvancedDivider => HasAdvancedPanel && HasAdvancedActions;

    partial void OnHasAdvancedPanelChanged(bool value)
    {
        OnPropertyChanged(nameof(HasAdvanced));
        OnPropertyChanged(nameof(HasAdvancedDivider));
    }

    partial void OnHasAdvancedActionsChanged(bool value)
    {
        OnPropertyChanged(nameof(HasAdvanced));
        OnPropertyChanged(nameof(HasAdvancedDivider));
    }

    /// <summary>滑杆防抖提交定时器（每个属性一份）与动作二次确认超时。</summary>
    private readonly Dictionary<SpecPropVm, IDisposable> _specCommitTimers = new();
    private IDisposable? _actionConfirmTimer;

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

    /// <summary>写注册表失败回滚开关时置位，避免回滚本身又触发一次写入。</summary>
    private bool _revertingAutoStart;
    [ObservableProperty] private int _selectedNavIndex;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private MijiaDevice? _drawerDevice;
    [ObservableProperty] private bool _isDrawerOpen;
    [ObservableProperty] private bool _isLoginOpen;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private bool _isLogOpen;

    [ObservableProperty] private bool _isLoggedIn;
    /// <summary>本地还留着 passToken，可一键登录、不必再扫码。</summary>
    [ObservableProperty] private bool _canQuickLogin;
    /// <summary>设置·账户：未登录且本机有凭证时显示「一键登录」。</summary>
    public bool ShowQuickLoginAction => !IsLoggedIn && CanQuickLogin;
    /// <summary>设置·账户：未登录且无凭证时显示「扫码登录」。</summary>
    public bool ShowQrLoginAction => !IsLoggedIn && !CanQuickLogin;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private int _onCount;
    [ObservableProperty] private int _offlineCount;
    [ObservableProperty] private int _sceneCount;
    [ObservableProperty] private int _roomCount;
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
    partial void OnHasEnvironmentChanged(bool value) => OnPropertyChanged(nameof(ShowEnvOverview));
    partial void OnHasConsumablesChanged(bool value) => OnPropertyChanged(nameof(ShowEnvOverview));

    partial void OnHasConsumableWarningChanged(bool value)
    {
        OnPropertyChanged(nameof(ConsumablePillText));
        OnPropertyChanged(nameof(ConsumableStatusText));
        OnPropertyChanged(nameof(ShowEnvOverview));
    }

    // ===== 小爱音箱：默认音箱与语音指令 =====

    /// <summary>设置里的默认音箱候选（did 作为 Value）。</summary>
    public ObservableCollection<OptionItem> SpeakerOptions { get; } = new();
    [ObservableProperty] private object? _selectedSpeakerValue;
    [ObservableProperty] private bool _hasSpeaker;
    [ObservableProperty] private string _defaultSpeakerName = "";
    [ObservableProperty] private string _voiceText = "";
    [ObservableProperty] private bool _isVoiceOpen;
    [ObservableProperty] private bool _isVoiceSending;
    /// <summary>浮层内的执行结果提示：成功/失败原因，不再只写隐藏日志。</summary>
    [ObservableProperty] private string _voiceHint = "";
    [ObservableProperty] private bool _voiceHintIsError;

    public bool HasVoiceHint => VoiceHint.Length > 0;
    partial void OnVoiceHintChanged(string value) => OnPropertyChanged(nameof(HasVoiceHint));

    /// <summary>重建音箱下拉时置位，避免程序性改动触发偏好落盘。</summary>
    private bool _suppressSpeakerSelect;

    /// <summary>右下角悬浮球：有音箱且抽屉没开时才出现，避免压住抽屉内容。</summary>
    public bool ShowVoiceFab => HasSpeaker && !IsDrawerOpen;

    partial void OnHasSpeakerChanged(bool value) => OnPropertyChanged(nameof(ShowVoiceFab));

    partial void OnIsDrawerOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowVoiceFab));
        if (value) IsVoiceOpen = false;
    }

    partial void OnSelectedSpeakerValueChanged(object? value)
    {
        if (_suppressSpeakerSelect) return;
        AppSettingsStore.SetDefaultSpeakerDid(value as string ?? "");
        UpdateDefaultSpeakerName();
    }

    private MijiaDevice? DefaultSpeaker =>
        SelectedSpeakerValue is string did
            ? _allDevices.FirstOrDefault(d => d.IsSpeaker && d.Did == did)
            : null;

    public ObservableCollection<RoomChip> Rooms { get; } = new();
    public ObservableCollection<HomeOption> HomeOptions { get; } = new();
    public ObservableCollection<MijiaDevice> Devices { get; } = new();
    public ObservableCollection<DeviceGroup> HomeGroups { get; } = new();
    public ObservableCollection<SceneCardItem> SceneItems { get; } = new();

    /// <summary>首次拉取期间的骨架格数量，只用来给 ItemsControl 供位。</summary>
    public IReadOnlyList<string> SkeletonSlots { get; } = new string[10];

    /// <summary>列表还空着时铺骨架；已有设备只是刷新状态，不动内容。</summary>
    public bool ShowSkeleton => IsLoading && TotalCount == 0;

    /// <summary>空房间提示要等加载结束再判定，否则骨架期间就冒出来。</summary>
    public bool ShowEmptyHint => !IsLoading && !HasDevices;

    /// <summary>首页空态：加载结束、既没有设备也没有场景时，整块换成空提示。</summary>
    public bool ShowHomeEmpty => !IsLoading && TotalCount == 0 && !HasScenes;

    /// <summary>场景页空态：加载结束后仍没有场景。</summary>
    public bool ShowSceneEmpty => !IsLoading && !HasScenes;

    public bool IsHome => SelectedNavIndex == 0;
    public bool IsRooms => SelectedNavIndex == 1;
    public bool IsScenes => SelectedNavIndex == 2;

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
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(IsRooms));
        OnPropertyChanged(nameof(IsScenes));
        SyncRoomChips();
    }

    partial void OnIsDarkChanged(bool value)
    {
        ThemeStore.Set(!value);
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
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

    partial void OnDrawerDeviceChanged(MijiaDevice? value)
    {
        if (value != null) IsDrawerOpen = true;
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
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ShowHomeEmpty));
        OnPropertyChanged(nameof(ShowSceneEmpty));
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

    /// <summary>登录/扫码进行中：拦重复点，避免二次请求把抽屉状态打乱。</summary>
    private bool _authBusy;

    /// <summary>取消在途的扫码长轮询 / 换 token；关掉登录抽屉时打断，避免命令一直占着。</summary>
    private CancellationTokenSource? _authCts;

    private CancellationToken BeginAuth()
    {
        _authCts?.Cancel();
        _authCts?.Dispose();
        _authCts = new CancellationTokenSource();
        _authBusy = true;
        return _authCts.Token;
    }

    private void CancelAuth()
    {
        try { _authCts?.Cancel(); }
        catch { /* 取消失败不影响关抽屉 */ }
        try { _authCts?.Dispose(); }
        catch { }
        _authCts = null;
        _authBusy = false;
    }

    /// <summary>清空并释放二维码位图；Skia 像素在原生堆，光置 null 不 Dispose 会一直占内存。</summary>
    private void ClearQrImage()
    {
        var old = QrImage;
        QrImage = null;
        old?.Dispose();
    }

    /// <summary>
    /// 登录入口：只打开登录面板并露出可点的按钮，不自动发网络请求。
    /// 有 passToken 显示「一键登录」；没有则显示「获取二维码」。
    /// 此前一进来就生成二维码、长轮询又占住命令，关掉后再点会点不动。
    /// </summary>
    [RelayCommand]
    private void Login()
    {
        IsSettingsOpen = false;
        IsLoginOpen = true;
        ClearQrImage();
        CanQuickLogin = _auth.CanQuickLogin();
        QrHint = CanQuickLogin
            ? "本机已有登录凭证，点击一键登录即可"
            : "本机无保存凭证，请获取二维码后用米家 App 扫码";
    }

    /// <summary>
    /// 启动自动登录：last 会话过期也直接用 passToken 换新 serviceToken，
    /// 只有换不到才把登录面板露出来让用户决定（一键登录 / 扫码）。
    /// </summary>
    private async Task AutoLoginAsync()
    {
        var ct = BeginAuth();
        try
        {
            if (await _auth.RefreshServiceTokenAsync(ct))
            {
                FinishLogin();
                Log("自动登录成功");
                await InitClientAsync(refreshToken: false);
                return;
            }
            Log($"自动登录失败：{_auth.LastRefreshError ?? "未知错误"}");
        }
        catch (Exception ex)
        {
            Log($"自动登录出错：{ex.Message}");
        }
        finally
        {
            if (_authCts is null || _authCts.Token == ct) _authBusy = false;
        }

        if (_auth.HasValidAuth()) return;
        CanQuickLogin = _auth.CanQuickLogin();
        IsLoginOpen = true;
        QrHint = CanQuickLogin
            ? "自动登录失败，可一键登录或扫码"
            : "请获取二维码后用米家 App 扫码登录";
    }

    /// <summary>一键登录：用已保存的 passToken 换 serviceToken，免扫码。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task QuickLoginAsync()
    {
        IsSettingsOpen = false;
        IsLoginOpen = true;
        if (_authBusy)
        {
            QrHint = "正在登录，请稍候…";
            return;
        }
        if (!_auth.CanQuickLogin())
        {
            CanQuickLogin = false;
            QrHint = "本机无登录凭证，请扫码登录";
            return;
        }

        var ct = BeginAuth();
        QrHint = "正在一键登录…";
        try
        {
            if (!await _auth.RefreshServiceTokenAsync(ct))
            {
                var detail = _auth.LastRefreshError ?? "未知错误";
                QrHint = ct.IsCancellationRequested ? "已取消" : "一键登录失败，可改用扫码登录";
                if (!ct.IsCancellationRequested) Log($"一键登录失败：{detail}");
                return;
            }

            FinishLogin();
            Log("一键登录成功");
            await InitClientAsync();
        }
        catch (OperationCanceledException)
        {
            QrHint = "已取消";
        }
        catch (Exception ex)
        {
            QrHint = $"一键登录出错：{ex.Message}";
            Log($"一键登录出错：{ex.Message}");
        }
        finally
        {
            if (_authCts is null || _authCts.Token == ct) _authBusy = false;
        }
    }

    /// <summary>强制走扫码（登录抽屉里「获取二维码 / 改用扫码」）。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task QrLoginAsync()
    {
        IsSettingsOpen = false;
        IsLoginOpen = true;
        if (_authBusy)
        {
            QrHint = "正在登录，请稍候…";
            return;
        }

        var ct = BeginAuth();
        QrHint = "正在获取二维码…";
        try
        {
            var data = await _auth.GetQrLoginDataAsync(ct);
            if (data == null)
            {
                QrHint = ct.IsCancellationRequested ? "已取消" : "获取二维码失败，请重试";
                return;
            }

            using var qr = new QRCodeGenerator();
            using var data1 = qr.CreateQrCode(data.Value.loginUrl, QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(data1);
            // QRCoder 的参数是 pixelsPerModule（每模块像素），不是总边长。
            // 误传 480 时，长 URL 的 QR（约 40–70 模块）会生成上万像素位图，
            // 解码/编码裸缓冲轻松吃掉约 1GB（实测打开登录点一下就涨）。
            // 取 10：成图约 400–700px，对 240px 展示框（含高分屏）足够清晰。
            var bytes = png.GetGraphic(10);
            var oldQr = QrImage;
            QrImage = new Bitmap(new MemoryStream(bytes));
            oldQr?.Dispose();

            QrHint = "请用米家 App 扫描上方二维码";
            Log("二维码已生成，等待扫码…");

            var ok = await _auth.WaitForScanAsync(data.Value.lp, ct);
            if (ct.IsCancellationRequested)
            {
                QrHint = "已取消";
                ClearQrImage();
                return;
            }
            if (ok)
            {
                FinishLogin();
                Log("登录成功");
                await InitClientAsync();
            }
            else
            {
                QrHint = "扫码超时或失败，请重试";
                ClearQrImage();
                Log($"扫码失败：{_auth.LastScanError ?? "无响应"}");
            }
        }
        catch (OperationCanceledException)
        {
            QrHint = "已取消";
            ClearQrImage();
        }
        catch (Exception ex)
        {
            QrHint = $"登录出错：{ex.Message}";
            Log($"错误：{ex.Message}");
        }
        finally
        {
            if (_authCts is null || _authCts.Token == ct) _authBusy = false;
        }
    }

    private void FinishLogin()
    {
        CancelAuth();
        CanQuickLogin = _auth.CanQuickLogin();
        IsLoggedIn = true;
        StatusText = "已登录";
        QrHint = "登录成功";
        ClearQrImage();
        IsLoginOpen = false;
    }

    /// <summary>
    /// 一键退出登录：清会话与本地设备缓存，页面设备全部重置；
    /// 保留 passToken，下次可一键登录、不必再扫码。
    /// </summary>
    [RelayCommand]
    private void Logout()
    {
        if (_authBusy) return;
        _auth.Logout();
        DeviceCacheStore.Clear();
        ResetSessionUi();
        CanQuickLogin = _auth.CanQuickLogin();
        IsLoggedIn = false;
        StatusText = "未登录";
        IsDrawerOpen = false;
        IsSettingsOpen = false;
        IsLoginOpen = true;
        ClearQrImage();
        QrHint = CanQuickLogin
            ? "已退出登录，本机仍可一键登录"
            : "已退出登录，请重新扫码";
        Log("已退出登录，设备列表已重置");
    }

    /// <summary>断开客户端并清空页面上的设备/房间/场景/家庭，回到未登录空态。</summary>
    private void ResetSessionUi()
    {
        _client = null;
        _allDevices.Clear();
        _selectedRoom = "全部";
        _detailDevice = null;
        _lightCommitTimer?.Dispose();
        _lightCommitTimer = null;
        _speakerVolumeTimer?.Dispose();
        _speakerVolumeTimer = null;
        _loadGeneration++;
        _sharedDevices = null;
        _pollTick = 0;
        ClearSpecPanel();

        Devices.Clear();
        HomeGroups.Clear();
        Rooms.Clear();
        SceneItems.Clear();
        HomeOptions.Clear();
        EnvReadings.Clear();
        ConsumableItems.Clear();
        SpeakerOptions.Clear();
        HasEnvironment = false;
        HasConsumables = false;
        HasConsumableWarning = false;
        ConsumableLowCount = 0;
        IsEnvDetailOpen = false;
        EnvTemperatureText = "—";
        EnvHumidityText = "—";
        EnvTemperatureValue = "—";
        EnvTemperatureUnit = "";
        EnvHumidityValue = "—";
        EnvHumidityUnit = "";
        EnvReadingsCountText = "";
        EnvSourceText = "";
        HasSpeaker = false;
        _suppressSpeakerSelect = true;
        SelectedSpeakerValue = null;
        _suppressSpeakerSelect = false;
        DefaultSpeakerName = "";
        VoiceText = "";
        IsVoiceOpen = false;
        IsVoiceSending = false;
        VoiceHint = "";
        VoiceHintIsError = false;
        SelectedHome = null;
        CanSwitchHome = false;
        HomeName = "我的家";
        HasDevices = false;
        HasScenes = false;
        TotalCount = 0;
        OnlineCount = 0;
        OnCount = 0;
        OfflineCount = 0;
        SceneCount = 0;
        RoomCount = 0;
        TotalText = "";
        OfflineText = "";
        BannerTitle = "全屋设备";
        BannerDesc = "统一管理家中所有智能设备";
        BannerTotal = 0;
        BannerOn = 0;
        BannerOffline = 0;
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
            var devices = await _client.GetAllDevicesAsync();
            var homes = _client.Homes.ToList();
            foreach (var d in devices) d.ApplyLegacyDefaults();
            var gen = ++_loadGeneration;

            // 设备到手就先渲染并落盘，开关状态和场景随后补，不让用户干等
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SyncHomeOptions(homes);
                _allDevices = devices;
                RebuildRooms();
                ApplyFilter();
                RebuildHomeGroups();
                UpdateStats();
                RefreshSpeakers();
            });
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
                if (home == null) return;
            }

            Log($"切换到家庭「{home.Name}」，正在拉取设备…");
            var devices = await LoadHomeDevicesAsync(home);
            foreach (var d in devices) d.ApplyLegacyDefaults();
            var gen = ++_loadGeneration;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                HomeName = string.IsNullOrWhiteSpace(home.Name) ? "我的家" : home.Name.Trim();
                _allDevices = devices;
                RebuildRooms();
                ApplyFilter();
                RebuildHomeGroups();
                UpdateStats();
                RefreshSpeakers();
            });
            _ = Task.Run(() => DeviceCacheStore.Save(devices));

            await Task.WhenAll(LoadScenesAsync(), RefreshStatesAsync(includeMetrics: false));
            _ = LoadIconsAsync(devices);
            _ = ResolveSpecsThenRefreshAsync(devices, gen);
            Log($"「{HomeName}」已加载 {_allDevices.Count} 个设备、{SceneItems.Count} 个场景");
        }
        catch (Exception ex)
        {
            Log($"切换家庭失败：{ex.Message}");
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

                // 播放状态回读：1 播放中、0 停止、2 暂停
                if (d.IsSpeaker && d.PlayingStateSiid > 0 &&
                    map.TryGetValue((d.TargetDid, d.PlayingStateSiid, d.PlayingStatePiid), out var ps) && ps != null)
                {
                    var playing = ToInt(ps) == 1;
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
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: false);
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
            .Where(d => (_selectedRoom == "全部" || d.RoomName == _selectedRoom) && MatchesSearch(d)));
        Devices.Clear();
        foreach (var d in list) Devices.Add(d);
        HasDevices = Devices.Count > 0;
        UpdateBanner();
    }

    private void RebuildHomeGroups()
    {
        HomeGroups.Clear();
        foreach (var g in _allDevices
                     .GroupBy(d => d.RoomName)
                     .OrderByDescending(g => g.Count()))
        {
            var items = OrderDevices(g.Where(MatchesSearch)).ToList();
            if (items.Count == 0) continue;
            HomeGroups.Add(new DeviceGroup { Name = g.Key, Total = g.Count(), Items = items });
        }
    }

    private void UpdateStats()
    {
        TotalCount = _allDevices.Count;
        OnlineCount = _allDevices.Count(d => d.IsOnline);
        OnCount = _allDevices.Count(d => d.IsOnline && d.IsOn);
        OfflineCount = TotalCount - OnlineCount;
        SceneCount = SceneItems.Count;
        HasScenes = SceneItems.Count > 0;
        TotalText = $"/ {TotalCount}";
        OfflineText = $"离线 {OfflineCount}";
        UpdateBanner();
    }

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

    /// <summary>重建默认音箱候选：优先保留上次的 did，没有则取第一台并落盘。</summary>
    private void RefreshSpeakers()
    {
        var speakers = OrderDevices(_allDevices.Where(d => d.IsSpeaker)).ToList();
        HasSpeaker = speakers.Count > 0;
        var wanted = AppSettingsStore.DefaultSpeakerDid;

        _suppressSpeakerSelect = true;
        try
        {
            SpeakerOptions.Clear();
            foreach (var s in speakers)
                SpeakerOptions.Add(new OptionItem { Label = s.Name, Value = s.Did });

            var pick = SpeakerOptions.FirstOrDefault(o => Equals(o.Value, wanted))
                       ?? SpeakerOptions.FirstOrDefault();
            SelectedSpeakerValue = pick?.Value;
            foreach (var o in SpeakerOptions) o.IsSelected = o == pick;
            if (pick != null && !Equals(pick.Value, wanted))
                AppSettingsStore.SetDefaultSpeakerDid(pick.Value as string ?? "");
        }
        finally
        {
            _suppressSpeakerSelect = false;
        }

        UpdateDefaultSpeakerName();
        if (!HasSpeaker) IsVoiceOpen = false;
    }

    private void UpdateDefaultSpeakerName()
    {
        var d = DefaultSpeaker;
        DefaultSpeakerName = d?.Name ?? (HasSpeaker ? "未选择音箱" : "");
    }

    [RelayCommand]
    private void ToggleVoice() => IsVoiceOpen = !IsVoiceOpen;

    /// <summary>环境概览「查看明细」：用弹窗展示逐设备读数与耗材，避免把首页撑高。</summary>
    [RelayCommand]
    private void OpenEnvDetail() => IsEnvDetailOpen = true;

    [RelayCommand]
    private void CloseEnvDetail() => IsEnvDetailOpen = false;

    /// <summary>把输入框里的话交给默认音箱的 execute-text-directive 动作。</summary>
    [RelayCommand]
    private async Task SendVoiceAsync()
    {
        if (_client is not { } client)
        {
            SetVoiceHint("正在连接米家，稍后再试", true);
            return;
        }
        if (IsVoiceSending) return;

        var text = VoiceText.Trim();
        if (text.Length == 0)
        {
            SetVoiceHint("先输入要执行的指令", true);
            return;
        }

        var d = DefaultSpeaker;
        if (d == null)
        {
            SetVoiceHint("还没有默认音箱，先到设置里选一台小爱音箱", true);
            Log("还没有默认音箱，先到设置里选一台小爱音箱");
            return;
        }
        if (!d.IsOnline)
        {
            SetVoiceHint($"{d.Name} 离线，指令未下发", true);
            Log($"{d.Name} 离线，指令未下发");
            return;
        }
        if (!d.HasTextDirective)
        {
            SetVoiceHint($"{d.Name} 不支持文本指令", true);
            Log($"{d.Name} 不支持文本指令");
            return;
        }

        IsVoiceSending = true;
        SetVoiceHint("发送中…", false);
        try
        {
            await client.SendTextDirectiveAsync(d.TargetDid, d.TextDirectiveSiid, d.TextDirectiveAiid, text);
            SetVoiceHint($"已发送给 {d.Name}：{text}", false);
            Log($"已让小爱执行：{text}");
            VoiceText = "";
        }
        catch (Exception ex)
        {
            SetVoiceHint($"发送失败：{ex.Message}", true);
            Log($"语音指令失败：{ex.Message}");
        }
        finally
        {
            IsVoiceSending = false;
        }
    }

    private void SetVoiceHint(string text, bool isError)
    {
        VoiceHint = text;
        VoiceHintIsError = isError;
    }

    /// <summary>抽屉里的播放/暂停：优先专用动作，其次 toggle-play，最后退化成文本指令。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleSpeakerPlayAsync()
    {
        if (DrawerDevice is not { IsSpeaker: true } d) return;
        var play = !d.IsPlaying;

        if (d.PlaySiid > 0 || d.TogglePlaySiid > 0 || d.HasTextDirective)
        {
            var sent = await RunSpeakerPowerAsync(d, play);
            if (!sent) return;
            d.IsPlaying = play;
            Log($"{d.Name} → {(play ? "播放" : "暂停")}");
        }
        else
        {
            Log($"{d.Name} 不支持播放控制");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task PreviousTrackAsync() => RunTrackStepAsync(false);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task NextTrackAsync() => RunTrackStepAsync(true);

    /// <summary>上一曲/下一曲：优先规格动作，缺失时退化成文本指令。</summary>
    private async Task RunTrackStepAsync(bool next)
        => await SendSpeakerActionAsync(
            next ? "下一首" : "上一首",
            next ? DrawerDevice?.NextSiid ?? 0 : DrawerDevice?.PreviousSiid ?? 0,
            next ? DrawerDevice?.NextAiid ?? 0 : DrawerDevice?.PreviousAiid ?? 0);

    /// <summary>带播放控制卡片的动作下发：统一处理离线/忙/异常，返回是否真的发给了设备。</summary>
    private async Task SendSpeakerActionAsync(string label, int siid, int aiid)
    {
        if (_client is not { } client || DrawerDevice is not { IsSpeaker: true } d) return;
        if (!d.IsOnline)
        {
            Log($"{d.Name} 离线，指令未下发");
            return;
        }
        if (d.IsBusy) return;

        d.IsBusy = true;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            if (siid > 0) await client.RunActionAsync(d.TargetDid, siid, aiid);
            else if (d.HasTextDirective) await client.SendTextDirectiveAsync(d.TargetDid, d.TextDirectiveSiid, d.TextDirectiveAiid, label);
            else
            {
                Log($"{d.Name} 不支持{label}");
                return;
            }
            Log($"{d.Name} → {label}");
        }
        catch (Exception ex)
        {
            Log($"{d.Name} {label}失败：{ex.Message}");
        }
        finally
        {
            d.IsBusy = false;
        }
    }

    /// <summary>播放/暂停动作下发，失败时保持本地状态不变。</summary>
    private async Task<bool> RunSpeakerPowerAsync(MijiaDevice d, bool play)
    {
        if (_client is not { } client) return false;
        if (!d.IsOnline)
        {
            Log($"{d.Name} 离线，指令未下发");
            return false;
        }
        if (d.IsBusy) return false;

        d.IsBusy = true;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            if (play)
            {
                if (d.PlaySiid > 0) await client.RunActionAsync(d.TargetDid, d.PlaySiid, d.PlayAiid);
                else if (d.TogglePlaySiid > 0) await client.RunActionAsync(d.TargetDid, d.TogglePlaySiid, d.TogglePlayAiid);
                else if (d.HasTextDirective) await client.SendTextDirectiveAsync(d.TargetDid, d.TextDirectiveSiid, d.TextDirectiveAiid, "继续播放");
                else return false;
            }
            else
            {
                if (d.PauseSiid > 0) await client.RunActionAsync(d.TargetDid, d.PauseSiid, d.PauseAiid);
                else if (d.TogglePlaySiid > 0) await client.RunActionAsync(d.TargetDid, d.TogglePlaySiid, d.TogglePlayAiid);
                else if (d.HasTextDirective) await client.SendTextDirectiveAsync(d.TargetDid, d.TextDirectiveSiid, d.TextDirectiveAiid, "暂停播放");
                else return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"{d.Name} 播放控制失败：{ex.Message}");
            return false;
        }
        finally
        {
            d.IsBusy = false;
        }
    }

    /// <summary>音量滑杆停 600ms 再提交；期间把设备放进抑制窗口，避免轮询把滑杆拽回去。</summary>
    private void ScheduleSpeakerVolumeCommit(MijiaDevice device)
    {
        if (!device.IsSpeaker || device.SpeakerVolumeSiid <= 0) return;
        _speakerVolumeTimer?.Dispose();
        device.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        _speakerVolumeTimer = DispatcherTimer.RunOnce(() =>
        {
            _speakerVolumeTimer = null;
            _ = CommitSpeakerVolumeAsync(device);
        }, TimeSpan.FromMilliseconds(600));
    }

    private async Task CommitSpeakerVolumeAsync(MijiaDevice device)
    {
        if (_client == null || !device.IsOnline) return;
        var volume = device.SpeakerVolume;
        device.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetSpeakerVolumeAsync(device.TargetDid, device.SpeakerVolumeSiid, device.SpeakerVolumePiid, volume);
            Log($"{device.Name} 音量 {volume}%");
        }
        catch (Exception ex)
        {
            Log($"{device.Name} 音量设置失败：{ex.Message}");
        }
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
    private void OpenDrawer(MijiaDevice? device)
    {
        if (device == null) return;
        DrawerDevice = device;
        IsDrawerOpen = true;
        BuildSpecPanel(device);
        AttachDrawerDetail(device);
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
        IsVoiceOpen = false;
        IsLogOpen = false;
    }

    /// <summary>抽屉关闭时释放面板 VM 与拖拽/确认定时器，避免后台轮询继续写隐藏控件。</summary>
    private void ClearSpecPanel()
    {
        foreach (var timer in _specCommitTimers.Values) timer.Dispose();
        _specCommitTimers.Clear();
        _actionConfirmTimer?.Dispose();
        _actionConfirmTimer = null;
        DrawerSpecAdvancedGroups.Clear();
        DrawerSpecAdvancedActions.Clear();
        HasAdvancedPanel = false;
        HasAdvancedActions = false;
        IsAdvancedExpanded = false;
    }

    [RelayCommand]
    private void ToggleTheme() => IsDark = !IsDark;

    /// <summary>
    /// 开关命令在卡片和抽屉里共用同一个实例。Avalonia 的 Button 会按 Command.CanExecute
    /// 决定自身可用性，异步命令执行期间默认 CanExecute 为 false，会让所有绑定该命令的
    /// 开关一起变灰闪一下；所以这里允许并发，单个设备的连点由各自的 IsBusy 拦截。
    /// 卡片 ToggleButton 的 TwoWay 绑定会先把 IsOn 翻好，这里直接按当前 IsOn 下发。
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleDeviceAsync(MijiaDevice? device)
    {
        if (_client == null || device == null) return;
        if (!device.IsOnline)
        {
            Log($"{device.Name} 离线，指令未下发");
            device.IsOn = !device.IsOn;
            return;
        }
        if (device.IsBusy) return;
        device.IsBusy = true;
        device.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            if (device.Channels.Count == 1)
            {
                var ch = device.Channels[0];
                await _client.SetChannelAsync(ch, device.IsOn);
                ch.IsOn = device.IsOn;
                device.SyncOnFromChannels();
            }
            else if (device.IsCurtain)
            {
                await _client.SetCurtainAsync(device.TargetDid, device.IsOn);
            }
            else
            {
                await _client.SetPowerAsync(device, device.IsOn);
            }
            Log($"{device.Name} → {(device.IsOn ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            Log($"控制失败：{ex.Message}");
            device.IsOn = !device.IsOn;
            device.SyncOnFromChannels();
        }
        finally
        {
            device.IsBusy = false;
            UpdateStats();
        }
    }

    /// <summary>多路开关逐路下发：先翻本地态（按钮 TwoWay 绑定），失败再翻回来。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleChannelAsync(SwitchChannelState? channel)
    {
        if (_client == null || channel?.Device is not { } device) return;
        if (!device.IsOnline)
        {
            channel.IsOn = !channel.IsOn;
            device.SyncOnFromChannels();
            Log($"{device.Name} 离线，指令未下发");
            return;
        }
        if (channel.IsBusy) return;
        channel.IsBusy = true;
        device.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetChannelAsync(channel, channel.IsOn);
            device.SyncOnFromChannels();
            Log($"{device.Name} · {channel.Label} → {(channel.IsOn ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            channel.IsOn = !channel.IsOn;
            device.SyncOnFromChannels();
            Log($"控制失败：{ex.Message}");
        }
        finally
        {
            channel.IsBusy = false;
            UpdateStats();
        }
    }

    /// <summary>
    /// 抽屉打开时订阅当前设备的亮度/色温变化（滑动防抖后提交），并异步回填设备可控属性。
    /// 用同一实例反复打开也会重新订阅，避免 ObservableProperty 相同值不触发而漏刷新。
    /// </summary>
    private void AttachDrawerDetail(MijiaDevice device)
    {
        if (_detailDevice != null && _detailHandler != null)
            _detailDevice.PropertyChanged -= _detailHandler;
        _detailDevice = device;
        _detailHandler = (_, e) =>
        {
            if (_loadingDetail || _applyingStates) return;
            if (e.PropertyName is nameof(MijiaDevice.Brightness) or nameof(MijiaDevice.ColorTemp))
                ScheduleLightCommit(device);
            else if (e.PropertyName is nameof(MijiaDevice.SpeakerVolume))
                ScheduleSpeakerVolumeCommit(device);
        };
        device.PropertyChanged += _detailHandler;
        _ = LoadDetailAsync(device);
    }

    /// <summary>读取灯/空调的可控属性回填抽屉，让滑杆与按钮反映设备当前真实值。</summary>
    private async Task LoadDetailAsync(MijiaDevice device)
    {
        if (_client is not { } client) return;
        try
        {
            _loadingDetail = true;

            // 规格可能还在后台解析：打开抽屉时就地补一次，面板立刻可用
            if (device.Spec == null)
            {
                var spec = await DeviceSpecService.GetAsync(client, device);
                if (spec != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        device.ApplySpec(spec);
                        if (ReferenceEquals(DrawerDevice, device)) BuildSpecPanel(device);
                    });
                }
            }
            else if (ReferenceEquals(DrawerDevice, device))
            {
                BuildSpecPanel(device);
            }

            await LoadSpecPanelValuesAsync(device);
            if (!device.IsOnline) return;

            if (device.IsLight)
            {
                var st = await client.GetLightStateAsync(device.TargetDid);
                if (st != null)
                {
                    device.Brightness = st.Brightness;
                    device.ColorTemp = st.ColorTemp;
                }
            }
            else if (device.IsCurtain)
            {
                var pos = await client.GetCurtainPositionAsync(device.TargetDid);
                if (pos.HasValue) device.CurtainPosition = pos.Value;
            }
            else if (device.IsAc)
            {
                var schema = await MijiaSpecService.GetAcSchemaAsync(device.SpecType);
                device.AcSchema = schema;
                if (schema is { Valid: true })
                {
                    var st = await client.GetAcStateAsync(device.TargetDid, schema);
                    if (st != null)
                    {
                        device.AcMode = st.Mode;
                        device.AcTargetTemp = st.TargetTemp;
                        device.AcFanLevel = st.FanLevel;
                    }
                }
                else
                {
                    Log($"未获取到 {device.Name} 的空调控制参数，暂时无法下发");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"读取 {device.Name} 详情失败：{ex.Message}");
        }
        finally
        {
            _loadingDetail = false;
        }
    }

    /// <summary>按规格把抽屉里的通用属性/通道/电源读一遍，填进对应的行。</summary>
    private async Task LoadSpecPanelValuesAsync(MijiaDevice device)
    {
        if (_client is not { } client || !device.IsOnline) return;
        var query = new List<(string Did, int Siid, int Piid)>();
        foreach (var vm in DrawerSpecAdvancedGroups.SelectMany(g => g.Items))
            query.Add((device.TargetDid, vm.Def.Siid, vm.Def.Piid));
        foreach (var ch in device.Channels)
            query.Add((device.TargetDid, ch.Siid, ch.Piid));
        if (device.HasPower) query.Add((device.TargetDid, device.PowerSiid, device.PowerPiid));
        if (device.IsCurtain) query.Add((device.TargetDid, 2, 3));
        if (query.Count == 0) return;

        var map = await client.ReadPropsAsync(query.Distinct().ToList());
        if (_client == null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(DrawerDevice, device)) return;
            ApplySpecValues(device, map);
            foreach (var ch in device.Channels)
                if (map.TryGetValue((device.TargetDid, ch.Siid, ch.Piid), out var v))
                    ch.IsOn = v is true;
            device.SyncOnFromChannels();
            if (device.HasPower &&
                map.TryGetValue((device.TargetDid, device.PowerSiid, device.PowerPiid), out var pv))
                device.IsOn = pv is true;
            if (device.IsCurtain &&
                map.TryGetValue((device.TargetDid, 2, 3), out var cv) && cv != null)
                device.CurtainPosition = Math.Clamp(ToInt(cv), 0, 100);
            UpdateStats();
        });
    }

    /// <summary>只读属性只有在有信息量时才列出：布尔/枚举状态、温湿度等读数、电源类指标。</summary>
    private static readonly HashSet<string> MetricPropNames = new(StringComparer.Ordinal)
    {
        "temperature", "relative-humidity", "humidity", "pm2.5", "pm10", "illuminance",
        "battery-level", "battery", "co2", "tvoc", "tds-in", "air-quality", "water-level",
        "power-consumption", "voltage", "electric-current", "motion-detected",
        "contact-state", "occupancy", "smoke-state", "leak", "door-state", "submersion-state"
    };

    /// <summary>
    /// 按规格重建抽屉的「高级设置」：侧边栏主体只放常用控制卡，
    /// 其余规格属性与无入参动作全部收进折叠区；专门卡片已覆盖的属性不再重复列出。
    /// </summary>
    private void BuildSpecPanel(MijiaDevice device)
    {
        DrawerSpecAdvancedGroups.Clear();
        DrawerSpecAdvancedActions.Clear();
        var spec = device.Spec;
        if (spec == null)
        {
            HasAdvancedPanel = false;
            HasAdvancedActions = false;
            return;
        }

        // 按 siid 分组：多路面板的规格里会有多个同名服务（Light 1/2/3、Curtain 1/2），
        // 按描述分组会把它们并成一张卡，出现多行同名的滑杆。
        var groups = spec.Properties
            .Where(p => ShouldShowProp(device, p))
            .GroupBy(p => p.Siid)
            .ToList();
        var descCounts = groups
            .GroupBy(g => g.First().ServiceDesc)
            .ToDictionary(g => g.Key, g => g.Count());
        var descSeq = new Dictionary<string, int>(StringComparer.Ordinal);
        var groupTitles = new Dictionary<int, string>();
        foreach (var group in groups)
        {
            var desc = group.First().ServiceDesc;
            var title = desc;
            if (descCounts.TryGetValue(desc, out var total) && total > 1)
            {
                descSeq[desc] = descSeq.GetValueOrDefault(desc) + 1;
                title = $"{desc} {descSeq[desc]}";
            }

            var groupTitle = string.IsNullOrWhiteSpace(title) ? "属性" : title;
            groupTitles[group.Key] = groupTitle;
            DrawerSpecAdvancedGroups.Add(new SpecGroupVm
            {
                Title = groupTitle,
                Items = BuildPropVms(device, group.ToList(), ScheduleSpecCommit)
            });
        }

        var actions = spec.Actions
            .Where(a => a.InputCount == 0 && ShouldShowAction(device, spec, a))
            .ToList();
        var actionCounts = actions.GroupBy(a => a.Desc).ToDictionary(g => g.Key, g => g.Count());
        foreach (var action in actions)
        {
            var title = action.Desc;
            if (actionCounts[action.Desc] > 1 && groupTitles.TryGetValue(action.Siid, out var serviceTitle))
                title = $"{serviceTitle} · {action.Desc}";

            DrawerSpecAdvancedActions.Add(new SpecActionVm
            {
                Device = device,
                Def = action,
                TitleOverride = title
            });
        }

        HasAdvancedPanel = DrawerSpecAdvancedGroups.Count > 0;
        HasAdvancedActions = DrawerSpecAdvancedActions.Count > 0;
    }

    private List<SpecPropVm> BuildPropVms(MijiaDevice device, List<SpecPropertyDef> defs, Action<SpecPropVm> commit)
    {
        var items = new List<SpecPropVm>(defs.Count);
        foreach (var def in defs)
        {
            var vm = new SpecPropVm
            {
                Device = device,
                Def = def,
                SliderCommit = commit,
                ChoiceCommit = (p, v) =>
                {
                    var opt = p.Options.FirstOrDefault(o => o.Value == v);
                    if (opt is not null) _ = SetSpecChoiceAsync(opt);
                }
            };
            foreach (var opt in def.Options)
                vm.Options.Add(new SpecOptionVm { Prop = vm, Def = opt });
            items.Add(vm);
        }
        return items;
    }

    private static bool ShouldShowProp(MijiaDevice device, SpecPropertyDef p)
    {
        // 虚拟子设备只是父设备的一路通道，别把父设备的灯/窗帘服务也铺到它身上
        if (device.IsVirtual && p.Siid != device.ChannelSiid) return false;

        // 整机电源/多路通道已在专门卡片里展示
        if (device.HasPower && p.Siid == device.PowerSiid && p.Piid == device.PowerPiid) return false;
        if (p.InSwitchService) return false;

        // 灯/空调/窗帘的专门滑杆与按钮已覆盖这些属性
        if (device.IsLight && p.ServiceName == "light" &&
            p.Name is "brightness" or "color-temperature") return false;
        if (device.IsAc && p.Name is "mode" or "target-temperature" or "fan-level") return false;
        if (device.IsCurtain && p.Name is "motor-control" or "current-position" or "target-position")
            return false;

        // 规格没给描述（Desc 退化成短名）的多是厂商内部参数（lock-state、light-mode-one），不铺面板
        if (p.Desc == p.Name) return false;

        // 可写但没有范围/选项的可写项（配置字符串等）P0 没有合适的控件，不铺只读行
        if (p.Writable && p.Kind == SpecControlKind.Value) return false;
        if (p.Writable) return true;
        return p.IsBool || p.Options.Count > 0 || MetricPropNames.Contains(p.Name);
    }

    /// <summary>同一服务里已有开关/滑杆的动作不重复铺按钮（toggle、亮度增减）。</summary>
    private static bool ShouldShowAction(MijiaDevice device, DeviceSpec spec, SpecActionDef a)
    {
        if (device.IsVirtual && a.Siid != device.ChannelSiid) return false;
        if (a.Desc == a.Name) return false;
        if (device.IsCurtain && a.Name == "motion-state-toggle") return false;

        var coveredBy = a.Name switch
        {
            "toggle" => "on",
            "brightness-up" or "brightness-down" => "brightness",
            _ => null
        };
        return coveredBy == null ||
               !spec.Properties.Any(p => p.Siid == a.Siid && p.Writable && p.Name == coveredBy);
    }

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleSpecAsync(SpecPropVm? prop)
    {
        if (_client == null || prop == null) return;
        var d = prop.Device;
        if (!d.IsOnline)
        {
            prop.IsOn = !prop.IsOn;
            Log($"{d.Name} 离线，指令未下发");
            return;
        }
        if (prop.IsBusy) return;
        prop.IsBusy = true;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetPropAsync(d.TargetDid, prop.Def.Siid, prop.Def.Piid, prop.IsOn);
            prop.ValueText = prop.IsOn ? "是" : "否";
            Log($"{d.Name} · {prop.Title} → {(prop.IsOn ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            prop.IsOn = !prop.IsOn;
            Log($"设置 {d.Name} · {prop.Title} 失败：{ex.Message}");
        }
        finally
        {
            prop.IsBusy = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetSpecChoiceAsync(SpecOptionVm? option)
    {
        if (option == null) return;
        var prop = option.Prop;
        var d = prop.Device;
        // 下拉已先改了显示值、SyncSelection 也挪走了 IsSelected，回滚基准取设备侧已生效的 Number
        var previous = (object?)(int)Math.Round(prop.Number);
        if (_client == null)
        {
            prop.SetPickedQuiet(previous);
            return;
        }
        if (!d.IsOnline)
        {
            prop.SetPickedQuiet(previous);
            Log($"{d.Name} 离线，指令未下发");
            return;
        }
        if (prop.IsBusy)
        {
            prop.SetPickedQuiet(previous);
            return;
        }
        prop.IsBusy = true;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetPropAsync(d.TargetDid, prop.Def.Siid, prop.Def.Piid, option.Value);
            foreach (var o in prop.Options) o.IsSelected = o == option;
            prop.SetNumberQuiet(option.Value);
            prop.SetPickedQuiet(option.Value);
            Log($"{d.Name} · {prop.Title} → {option.Label}");
        }
        catch (Exception ex)
        {
            prop.SetPickedQuiet(previous);
            Log($"设置 {d.Name} · {prop.Title} 失败：{ex.Message}");
        }
        finally
        {
            prop.IsBusy = false;
        }
    }

    /// <summary>拖动结束后 500ms 再提交一次，避免滑杆每帧发请求。</summary>
    private void ScheduleSpecCommit(SpecPropVm prop)
    {
        if (_specCommitTimers.Remove(prop, out var old)) old.Dispose();
        _specCommitTimers[prop] = DispatcherTimer.RunOnce(() =>
        {
            _specCommitTimers.Remove(prop);
            _ = CommitSpecNumberAsync(prop);
        }, TimeSpan.FromMilliseconds(500));
    }

    private async Task CommitSpecNumberAsync(SpecPropVm prop)
    {
        if (_client == null) return;
        var d = prop.Device;
        if (!d.IsOnline)
        {
            Log($"{d.Name} 离线，指令未下发");
            return;
        }
        object value = prop.Def.Format is "float" or "double"
            ? Math.Round(prop.Number, 1)
            : (int)Math.Round(prop.Number);
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetPropAsync(d.TargetDid, prop.Def.Siid, prop.Def.Piid, value);
            Log($"{d.Name} · {prop.Title} → {prop.SliderText}");
        }
        catch (Exception ex)
        {
            Log($"设置 {d.Name} · {prop.Title} 失败：{ex.Message}");
        }
    }

    /// <summary>无入参动作先要点一次「确认执行」，4 秒内不确认自动撤回。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunSpecActionAsync(SpecActionVm? action)
    {
        if (_client == null || action == null) return;
        var d = action.Device;
        if (!d.IsOnline)
        {
            Log($"{d.Name} 离线，指令未下发");
            return;
        }

        if (!action.IsConfirming)
        {
            action.IsConfirming = true;
            _actionConfirmTimer?.Dispose();
            _actionConfirmTimer = DispatcherTimer.RunOnce(() => action.IsConfirming = false,
                TimeSpan.FromSeconds(4));
            return;
        }

        _actionConfirmTimer?.Dispose();
        action.IsConfirming = false;
        if (action.IsBusy) return;
        action.IsBusy = true;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.RunActionAsync(d.TargetDid, action.Def.Siid, action.Def.Aiid);
            Log($"{d.Name} · {action.Title} 已执行");
            await RefreshStatesAsync(includeMetrics: false);
        }
        catch (Exception ex)
        {
            Log($"{d.Name} · {action.Title} 执行失败：{ex.Message}");
        }
        finally
        {
            action.IsBusy = false;
        }
    }

    /// <summary>拖动结束后停 600ms 再提交一次亮度/色温，避免连发请求。</summary>
    private void ScheduleLightCommit(MijiaDevice device)
    {
        _lightCommitTimer?.Dispose();
        _lightCommitTimer = DispatcherTimer.RunOnce(
            () => _ = CommitLightAsync(device), TimeSpan.FromMilliseconds(600));
    }

    private async Task CommitLightAsync(MijiaDevice device)
    {
        if (_client == null || !device.IsOnline) return;
        try
        {
            await _client.SetLightBrightnessAsync(device.TargetDid, device.Brightness);
            await _client.SetLightColorTempAsync(device.TargetDid, device.ColorTemp);
            Log($"{device.Name} 亮度 {device.Brightness} · 色温 {device.ColorTemp}K");
        }
        catch (Exception ex)
        {
            Log($"灯光设置失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SetAcMode(int mode)
    {
        if (_client == null || DrawerDevice is not { IsAc: true } d || !d.IsOnline)
        {
            Log("空调离线或未选中，指令未下发");
            return;
        }
        if (d.AcSchema is not { Valid: true } s)
        {
            Log($"{d.Name} 缺少控制参数，模式未下发");
            return;
        }
        d.AcMode = mode;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetAcModeAsync(d.TargetDid, s.ModeSiid, s.ModePiid, mode);
            Log($"{d.Name} 模式 → {AcOptionLabel(s.Modes, mode, "模式")}");
        }
        catch (Exception ex)
        {
            Log($"设置空调模式失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AcTempUp() => await AdjustAcTempAsync(1);

    [RelayCommand]
    private async Task AcTempDown() => await AdjustAcTempAsync(-1);

    /// <summary>空调目标温度加减（步长与范围取自设备规格），改完立即下发。</summary>
    private async Task AdjustAcTempAsync(double dir)
    {
        if (_client == null || DrawerDevice is not { IsAc: true } d || !d.IsOnline)
        {
            Log("空调离线或未选中，指令未下发");
            return;
        }
        if (d.AcSchema is not { Valid: true } s)
        {
            Log($"{d.Name} 缺少控制参数，温度未下发");
            return;
        }
        var next = Math.Round((d.AcTargetTemp + s.TempStep * dir) / s.TempStep) * s.TempStep;
        next = Math.Clamp(next, s.TempMin, s.TempMax);
        d.AcTargetTemp = next;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetAcTargetTempAsync(d.TargetDid, s.TempSiid, s.TempPiid, next, s);
            Log($"{d.Name} 温度 → {next:0.#}℃");
        }
        catch (Exception ex)
        {
            Log($"设置空调温度失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SetAcFan(int level)
    {
        if (_client == null || DrawerDevice is not { IsAc: true } d || !d.IsOnline)
        {
            Log("空调离线或未选中，指令未下发");
            return;
        }
        if (d.AcSchema is not { Valid: true } s)
        {
            Log($"{d.Name} 缺少控制参数，风速未下发");
            return;
        }
        d.AcFanLevel = level;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetAcFanLevelAsync(d.TargetDid, s.FanSiid, s.FanPiid, level);
            Log($"{d.Name} 风速 → {AcOptionLabel(s.Fans, level, "档")}");
        }
        catch (Exception ex)
        {
            Log($"设置空调风速失败：{ex.Message}");
        }
    }

    private static string AcOptionLabel(List<AcOptionItem> opts, int value, string fallback) =>
        opts.FirstOrDefault(o => o.Value == value)?.Label ?? $"{fallback}{value}";

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task CurtainOpenAsync() => SendCurtainMotorAsync(MijiaClient.CurtainOpen, "打开");

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task CurtainPauseAsync() => SendCurtainMotorAsync(MijiaClient.CurtainPause, "暂停");

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task CurtainCloseAsync() => SendCurtainMotorAsync(MijiaClient.CurtainClose, "关闭");

    /// <summary>窗帘电机三键共用：0 停 / 1 开 / 2 关，同一设备由 IsBusy 拦连点。</summary>
    private async Task SendCurtainMotorAsync(int motor, string label)
    {
        if (_client == null || DrawerDevice is not { IsCurtain: true } d || !d.IsOnline)
        {
            Log("窗帘离线或未选中，指令未下发");
            return;
        }
        if (d.IsBusy) return;
        d.IsBusy = true;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetCurtainMotorAsync(d.TargetDid, motor);
            Log($"{d.Name} 窗帘 → {label}");
            // 开/关会让开合度持续变化；稍等再读一次，把抽屉里的百分比跟上
            if (motor != MijiaClient.CurtainPause)
            {
                await Task.Delay(1200);
                var pos = await _client.GetCurtainPositionAsync(d.TargetDid);
                if (pos.HasValue) d.CurtainPosition = pos.Value;
            }
            else
            {
                var pos = await _client.GetCurtainPositionAsync(d.TargetDid);
                if (pos.HasValue) d.CurtainPosition = pos.Value;
            }
        }
        catch (Exception ex)
        {
            Log($"窗帘控制失败：{ex.Message}");
        }
        finally
        {
            d.IsBusy = false;
            UpdateStats();
        }
    }

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
