using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    [ObservableProperty] private string _statusText = "未登录";
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

            // 设备到手就先渲染，开关状态和场景随后补，不让用户干等
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allDevices = devices;
                RebuildRooms();
                ApplyFilter();
                RebuildHomeGroups();
                UpdateStats();
            });

            var online = devices.Where(d => d.IsOnline).ToList();
            await Task.WhenAll(LoadOnStatesAsync(online), LoadScenesAsync());

            // 真机图按型号去米家产品库取，取到一张换一张，不挡主流程
            _ = LoadIconsAsync(devices);

            Log($"已加载 {_allDevices.Count} 个设备、{SceneItems.Count} 个场景");
        }
        catch (Exception ex)
        {
            Log($"拉取设备失败：{ex.Message}");
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
        var list = _allDevices.Where(d =>
            (_selectedRoom == "全部" || d.RoomName == _selectedRoom) && MatchesSearch(d)).ToList();
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
            var items = g.Where(MatchesSearch).ToList();
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
        device.IsBusy = true;
        try
        {
            if (device.Control == MijiaControl.Curtain)
                await _client.SetCurtainAsync(device.Did, device.IsOn);
            else
                await _client.ToggleLightAsync(device.Did, device.IsOn);
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
