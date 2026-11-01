using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;
using MiHome.Services;

namespace MiHome.ViewModels;

/// <summary>
/// 一个桌面分区（设备分组）的视图模型：持有模型、当前设备实例与外观画刷。
/// 设备实例直接来自主视图模型的列表，开关状态跟随轮询自动刷新，不需要另一套同步逻辑。
/// 分区里的设备卡与主窗口列表是同一张（<see cref="IDeviceCardHost"/>），只是点卡片弹控制窗。
/// </summary>
public partial class DesktopGroupVm : ObservableObject, IDeviceCardHost
{
    public MainViewModel Main { get; }

    public DesktopGroupModel Model { get; }

    public ObservableCollection<MijiaDevice> Devices { get; } = new();

    // ===== 设备卡宿主契约 =====

    /// <summary>点卡片 → 弹窗里展示这台设备的控制内容。</summary>
    public ICommand OpenCardCommand => OpenDeviceCommand;

    /// <summary>拨开关 → 直接复用主视图模型的下发逻辑，状态跟着轮询回填。</summary>
    public ICommand ToggleSwitchCommand => Main.ToggleDeviceCommand;

    /// <summary>分区里右键「从分区移除」：只把设备移出本分区，设备本身与桌面组件配置都不动。</summary>
    public ICommand? CardMenuCommand => RemoveFromGroupCommand;

    public string CardMenuText => "从分区移除";

    /// <summary>减号图标：这是「移除」动作。</summary>
    public Geometry CardMenuIcon => CardMenuIcons.Remove;

    [ObservableProperty] private string _name = "新分区";
    [ObservableProperty] private bool _collapsed;

    /// <summary>
    /// 内容区是否参与布局：折叠时先让 <see cref="ContentOpacity"/> 淡出（80ms），
    /// 淡完再收掉 —— 直接隐藏会让窗口高度先跳变，看起来像闪了一下。
    /// </summary>
    [ObservableProperty] private bool _contentVisible = true;

    [ObservableProperty] private double _width = DesktopGroupModel.DefaultWidth;
    [ObservableProperty] private IBrush _cardBrush = Brushes.Transparent;
    [ObservableProperty] private IBrush _hoverBrush = Brushes.Transparent;

    /// <summary>解散二次确认：第一次点只改文案，再点一次才真解散。</summary>
    [ObservableProperty] private string _dissolveLabel = "解散分区";

    private IDisposable? _dissolveTimer;

    public DesktopGroupVm(MainViewModel main, DesktopGroupModel model)
    {
        Main = main;
        Model = model;
        _name = model.Name;
        _collapsed = model.Collapsed;
        _contentVisible = !model.Collapsed;
        _width = Math.Clamp(model.Width, DesktopGroupModel.MinWidth, DesktopGroupModel.MaxWidth);
        Devices.CollectionChanged += OnDevicesChanged;
        RefreshAppearance();
        LoadDevices();
    }

    public string CountText => Devices.Count == 0 ? "0" : Devices.Count.ToString();

    public bool IsEmpty => Devices.Count == 0;

    /// <summary>内容区透明度：展开 1、折叠 0。折叠时内容已被裁掉，纯做淡出。</summary>
    public double ContentOpacity => Collapsed ? 0 : 1;

    /// <summary>折叠箭头角度：折叠时朝下（点了会展开），展开时旋 180° 朝上。</summary>
    public double CollapseArrowAngle => Collapsed ? 0 : 180;

    /// <summary>折叠只留标题栏：宽度不变，高度由内容收掉后由 SizeToContent 自然收紧。</summary>
    private IDisposable? _fadeTimer;

    partial void OnCollapsedChanged(bool value)
    {
        Model.Collapsed = value;
        OnPropertyChanged(nameof(ContentOpacity));
        OnPropertyChanged(nameof(CollapseArrowAngle));
        DesktopHost.Save();

        _fadeTimer?.Dispose();
        if (value)
        {
            // 折叠：先淡出，淡完再收起内容（窗口随后收紧到标题栏高度）
            _fadeTimer = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                _fadeTimer = null;
                ContentVisible = false;
            }, TimeSpan.FromMilliseconds(90));
        }
        else
        {
            // 展开：内容立刻回来，由 Opacity 过渡做淡入
            ContentVisible = true;
        }
    }

    partial void OnNameChanged(string value)
    {
        Model.Name = value;
        DesktopHost.Save();
    }

    partial void OnWidthChanged(double value)
    {
        Model.Width = value;
        DesktopHost.Save();
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>外观（主题 / 背景色 / 透明度 / 毛玻璃）变更后重算画刷。</summary>
    public void RefreshAppearance()
    {
        var look = DesktopHost.Look;
        CardBrush = DesktopPalette.CardBrush(look);
        HoverBrush = DesktopPalette.HoverBrush(look);
    }

    /// <summary>按模型里的 did 顺序重建设备列表；did 已在列表里则复用原实例（不重绑卡片）。</summary>
    public void LoadDevices()
    {
        var pool = Main.AllDevices;
        var next = Model.Dids
            .Select(did => pool.FirstOrDefault(d => string.Equals(d.Did, did, StringComparison.Ordinal)))
            .Where(d => d != null)
            .Select(d => d!)
            .ToList();

        if (next.Count == Devices.Count)
        {
            var same = true;
            for (var i = 0; i < next.Count; i++)
            {
                if (!ReferenceEquals(next[i], Devices[i]))
                {
                    same = false;
                    break;
                }
            }
            if (same) return;
        }

        Devices.Clear();
        foreach (var d in next) Devices.Add(d);
    }

    public void AddDevice(MijiaDevice device)
    {
        if (device == null || string.IsNullOrEmpty(device.Did)) return;
        if (Model.Dids.Contains(device.Did)) return;
        Model.Dids.Add(device.Did);
        if (!Devices.Contains(device)) Devices.Add(device);
        DesktopHost.Save();
    }

    public void RemoveDevice(MijiaDevice device)
    {
        if (device == null) return;
        Model.Dids.Remove(device.Did);
        Devices.Remove(device);
        DesktopHost.Save();
    }

    public bool Contains(string did) => Model.Dids.Contains(did);

    [RelayCommand]
    private void AddDevice() => DesktopHost.OpenPicker(this);

    [RelayCommand]
    private void ToggleCollapse() => Collapsed = !Collapsed;

    [RelayCommand]
    private void OpenDevice(MijiaDevice? device)
    {
        if (device == null) return;
        // 带上自己：控制弹窗要落在点它的那块屏幕上
        DesktopHost.OpenDevicePanel(Main, device, this);
    }

    /// <summary>右键菜单「从分区移除」：只断开关联，设备仍在首页与「所有设备」里。</summary>
    [RelayCommand]
    private void RemoveFromGroup(MijiaDevice? device)
    {
        if (device == null || !Contains(device.Did)) return;
        var name = device.Name;
        RemoveDevice(device);
        Main.AppendLog($"「{name}」已从分区「{Name}」移除");
    }

    [RelayCommand]
    private void OpenAppearance() => DesktopHost.OpenAppearance();

    [RelayCommand]
    private void Rename() => DesktopHost.RequestInlineRename(this);

    /// <summary>解散分区：首次点击只进入确认态（3 秒后自动撤回），再点才真正移除。</summary>
    [RelayCommand]
    private void Dissolve()
    {
        if (DissolveLabel == "解散分区")
        {
            DissolveLabel = "确认解散？";
            _dissolveTimer?.Dispose();
            _dissolveTimer = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                _dissolveTimer = null;
                DissolveLabel = "解散分区";
            }, TimeSpan.FromSeconds(3));
            return;
        }

        _dissolveTimer?.Dispose();
        _dissolveTimer = null;
        DesktopHost.RemoveGroup(this);
    }
}
