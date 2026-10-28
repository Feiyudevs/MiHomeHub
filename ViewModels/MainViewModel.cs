using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MijiaPanel.Models;
using MijiaPanel.Services;
using QRCoder;

namespace MijiaPanel.ViewModels;

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

    /// <summary>当前详情设备及其属性订阅，用于把亮度/色温变化提交回设备。</summary>
    private MijiaDevice? _detailDevice;
    private PropertyChangedEventHandler? _detailHandler;

    /// <summary>正在回填详情值，期间忽略属性变化，避免把刚读到的值又当成用户改动提交一次。</summary>
    private bool _loadingDetail;

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
    [ObservableProperty] private int _selectedNavIndex;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private MijiaDevice? _drawerDevice;
    [ObservableProperty] private bool _isDrawerOpen;
    [ObservableProperty] private bool _isLoginOpen;
    [ObservableProperty] private bool _isSettingsOpen;

    [ObservableProperty] private bool _isLoggedIn;
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

    public bool IsHome => SelectedNavIndex == 0;
    public bool IsRooms => SelectedNavIndex == 1;
    public bool IsScenes => SelectedNavIndex == 2;
    public string ThemeLabel => IsDark ? "深色主题 · 点击切换浅色" : "浅色主题 · 点击切换深色";

    public MainViewModel()
    {
        _auth = MijiaAuth.LoadOrEmpty();
        if (_auth.HasValidAuth())
        {
            IsLoggedIn = true;
            StatusText = "已登录";
            _ = InitClientAsync();
        }
        else
        {
            IsLoginOpen = true;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => TickClock();
        timer.Start();
        TickClock();
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
        OnPropertyChanged(nameof(ThemeLabel));
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        RebuildHomeGroups();
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

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowEmptyHint));
    }

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(ShowSkeleton));

    partial void OnHasDevicesChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyHint));

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
    private async Task InitClientAsync()
    {
        // 先铺上次缓存的设备，联网成功后整表替换，避免开屏干等
        ApplyCachedDevices();
        try
        {
            if (!await _auth.RefreshServiceTokenAsync()) Log("刷新 serviceToken 失败，请重新扫码登录");
        }
        catch (Exception ex)
        {
            Log($"刷新 serviceToken 出错：{ex.Message}");
        }
        _client = new MijiaClient(_auth);
        await LoadDevicesAsync();
    }

    /// <summary>读 %APPDATA% 下的设备缓存直接上屏；没有缓存则静默跳过。</summary>
    private void ApplyCachedDevices()
    {
        var cached = DeviceCacheStore.Load();
        if (cached == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            _allDevices = cached;
            RebuildRooms();
            ApplyFilter();
            RebuildHomeGroups();
            UpdateStats();
        });
        Log($"已加载本地缓存的 {cached.Count} 个设备");
        _ = LoadIconsAsync(cached);
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        // 从设置弹窗点「扫码登录」时，先收起设置，避免遮罩挡住登录抽屉
        IsSettingsOpen = false;
        IsLoginOpen = true;
        QrHint = "正在获取二维码…";
        try
        {
            var data = await _auth.GetQrLoginDataAsync();
            if (data == null)
            {
                QrHint = "获取二维码失败，请重试";
                return;
            }

            using var qr = new QRCodeGenerator();
            using var data1 = qr.CreateQrCode(data.Value.loginUrl, QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(data1);
            var bytes = png.GetGraphic(480);
            QrImage = new Bitmap(new MemoryStream(bytes));

            QrHint = "请用米家 App 扫描上方二维码";
            Log("二维码已生成，等待扫码…");

            var ok = await _auth.WaitForScanAsync(data.Value.lp);
            if (ok)
            {
                IsLoggedIn = true;
                StatusText = "已登录";
                QrHint = "登录成功";
                QrImage = null;
                IsLoginOpen = false;
                Log("登录成功");
                await InitClientAsync();
            }
            else
            {
                QrHint = "扫码超时或失败，请重试";
                QrImage = null;
                Log($"扫码失败：{_auth.LastScanError ?? "无响应"}");
            }
        }
        catch (Exception ex)
        {
            QrHint = $"登录出错：{ex.Message}";
            Log($"错误：{ex.Message}");
        }
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

            // 设备到手就先渲染并落盘，开关状态和场景随后补，不让用户干等
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SyncHomeOptions(homes);
                _allDevices = devices;
                RebuildRooms();
                ApplyFilter();
                RebuildHomeGroups();
                UpdateStats();
            });
            DeviceCacheStore.Save(devices);

            var online = devices.Where(d => d.IsOnline).ToList();
            await Task.WhenAll(LoadOnStatesAsync(online), LoadScenesAsync());

            // 真机图按型号取：磁盘缓存命中秒出，过期后对比产品库再决定是否重下
            _ = LoadIconsAsync(devices);

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
            var devices = await _client.GetDevicesAsync(home);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                HomeName = string.IsNullOrWhiteSpace(home.Name) ? "我的家" : home.Name.Trim();
                _allDevices = devices;
                RebuildRooms();
                ApplyFilter();
                RebuildHomeGroups();
                UpdateStats();
            });
            DeviceCacheStore.Save(devices);

            var online = devices.Where(d => d.IsOnline).ToList();
            await Task.WhenAll(LoadOnStatesAsync(online), LoadScenesAsync());
            _ = LoadIconsAsync(devices);
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

    private async Task LoadOnStatesAsync(List<MijiaDevice> online)
    {
        if (_client == null) return;
        try
        {
            var states = await _client.GetOnStatesAsync(online);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var d in _allDevices)
                    d.IsOn = states.TryGetValue(d.Did, out var on) && on;
                UpdateStats();
            });
        }
        catch (Exception ex)
        {
            Log($"读取开关状态失败：{ex.Message}");
        }
    }

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
        AttachDrawerDetail(device);
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseOverlays()
    {
        IsDrawerOpen = false;
        IsLoginOpen = false;
        IsSettingsOpen = false;
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
        try
        {
            if (device.Control == MijiaControl.Curtain)
                await _client.SetCurtainAsync(device.Did, device.IsOn);
            else
                await _client.SetPowerAsync(device, device.IsOn);
            Log($"{device.Name} → {(device.IsOn ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            Log($"控制失败：{ex.Message}");
            device.IsOn = !device.IsOn;
        }
        finally
        {
            device.IsBusy = false;
            UpdateStats();
        }
    }

    /// <summary>抽屉「打开」：把当前电源类设备置为开并下发。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task PowerOnAsync() => SetPowerAsync(true);

    /// <summary>抽屉「关闭」：把当前电源类设备置为关并下发。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task PowerOffAsync() => SetPowerAsync(false);

    /// <summary>电源类（灯/插座/空调）显式置位。</summary>
    private async Task SetPowerAsync(bool on)
    {
        if (_client == null || DrawerDevice is not { IsPower: true } d) return;
        if (!d.IsOnline)
        {
            Log($"{d.Name} 离线，指令未下发");
            d.IsOn = on;
            return;
        }
        if (d.IsBusy) return;
        d.IsBusy = true;
        var prev = d.IsOn;
        d.IsOn = on;
        try
        {
            await _client.SetPowerAsync(d, on);
            Log($"{d.Name} → {(on ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            Log($"控制失败：{ex.Message}");
            d.IsOn = prev;
        }
        finally
        {
            d.IsBusy = false;
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
            if (_loadingDetail) return;
            if (e.PropertyName is nameof(MijiaDevice.Brightness) or nameof(MijiaDevice.ColorTemp))
                ScheduleLightCommit(device);
        };
        device.PropertyChanged += _detailHandler;
        _ = LoadDetailAsync(device);
    }

    /// <summary>读取灯/空调的可控属性回填抽屉，让滑杆与按钮反映设备当前真实值。</summary>
    private async Task LoadDetailAsync(MijiaDevice device)
    {
        if (_client == null || !device.IsOnline) return;
        try
        {
            _loadingDetail = true;
            if (device.IsLight)
            {
                var st = await _client.GetLightStateAsync(device.Did);
                if (st != null)
                {
                    device.Brightness = st.Brightness;
                    device.ColorTemp = st.ColorTemp;
                }
            }
            else if (device.IsCurtain)
            {
                var pos = await _client.GetCurtainPositionAsync(device.Did);
                if (pos.HasValue) device.CurtainPosition = pos.Value;
            }
            else if (device.IsAc)
            {
                var schema = await MijiaSpecService.GetAcSchemaAsync(device.SpecType);
                device.AcSchema = schema;
                if (schema is { Valid: true })
                {
                    var st = await _client.GetAcStateAsync(device.Did, schema);
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
            await _client.SetLightBrightnessAsync(device.Did, device.Brightness);
            await _client.SetLightColorTempAsync(device.Did, device.ColorTemp);
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
        try
        {
            await _client.SetAcModeAsync(d.Did, s.ModeSiid, s.ModePiid, mode);
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
        try
        {
            await _client.SetAcTargetTempAsync(d.Did, s.TempSiid, s.TempPiid, next, s);
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
        try
        {
            await _client.SetAcFanLevelAsync(d.Did, s.FanSiid, s.FanPiid, level);
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
        try
        {
            await _client.SetCurtainMotorAsync(d.Did, motor);
            Log($"{d.Name} 窗帘 → {label}");
            // 开/关会让开合度持续变化；稍等再读一次，把抽屉里的百分比跟上
            if (motor != MijiaClient.CurtainPause)
            {
                await Task.Delay(1200);
                var pos = await _client.GetCurtainPositionAsync(d.Did);
                if (pos.HasValue) d.CurtainPosition = pos.Value;
            }
            else
            {
                var pos = await _client.GetCurtainPositionAsync(d.Did);
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
    private async Task RefreshStatesQuietAsync()    {
        if (_client == null) return;
        try
        {
            var online = _allDevices.Where(d => d.IsOnline).ToList();
            var states = await _client.GetOnStatesAsync(online);
            foreach (var d in _allDevices)
                d.IsOn = states.TryGetValue(d.Did, out var on) && on;
            UpdateStats();
        }
        catch (Exception ex)
        {
            Log($"刷新状态失败：{ex.Message}");
        }
    }

    private void Log(string msg)
    {
        LogText = $"[{DateTime.Now:HH:mm:ss}] {msg}\n" + LogText;
    }
}
