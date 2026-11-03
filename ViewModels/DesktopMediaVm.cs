using System.ComponentModel;
using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;
using MiHome.Services;

namespace MiHome.ViewModels;

/// <summary>
/// 桌面媒体分区：一台音箱的身份、播放控制、正在播放与音量入口。
/// 控制的音箱就是全局默认音箱（与首页「音响播放」、设置里的选择同源），
/// 在分区里换音箱等于换默认音箱，两处永远一致，不留第二份偏好。
/// 播放/上一首/下一首直接复用主视图模型的命令，不另写下发逻辑。
/// </summary>
public partial class DesktopMediaVm : DesktopZoneVm
{
    private MijiaDevice? _speaker;
    private MijiaDevice? _subscribed;

    public DesktopMediaVm(MainViewModel main, DesktopGroupModel model)
        : base(main, model)
    {
        main.PropertyChanged += OnMainChanged;
        LoadDevices();
        RefreshAppearance();
    }

    protected override string DangerText => "关闭媒体分区";

    protected override string DangerConfirmText => "确认关闭？";

    public override string EmptyHint => "家里没有可控制的小爱音箱";

    public override bool IsEmpty => Speaker == null;

    // ===== 绑定面：全部从当前音箱派生，音箱换了整组重算 =====

    public MijiaDevice? Speaker => _speaker;

    public bool ShowControls => _speaker != null;

    public string SpeakerName => _speaker?.Name ?? "未选择音箱";

    /// <summary>名称左边的房间标注，与各处音箱下拉同源；没归到房间就不显示。</summary>
    public string? SpeakerRoom => _speaker is { } d && !string.IsNullOrWhiteSpace(d.RoomName) && d.RoomName != "未分配房间"
        ? d.RoomName
        : null;

    /// <summary>副标题只报真实播放状态：房间已经标在名称左边，不再重复一遍。规格里取不到歌名，所以不假装是曲目。</summary>
    public string SpeakerMeta => _speaker is { } d ? StateWord(d) : "先到首页选一台音箱";

    private static string StateWord(MijiaDevice d) => d switch
    {
        { IsOnline: false } => "离线",
        { PlayingState: 1 } => "播放中",
        { PlayingState: 2 } => "已暂停",
        { IsPlaying: true } => "播放中",
        _ => "未在播放"
    };

    public bool IsPlaying => _speaker?.IsPlaying ?? false;

    public int VolumePercent => _speaker?.SpeakerVolume ?? 0;

    /// <summary>音量按钮上的角标文字：紧凑到两三位数字，放不下就只显示图标。</summary>
    public string VolumeBadgeText => _speaker == null ? "" : VolumePercent + "%";

    /// <summary>设备列表换代（切家庭 / 刷新）后重取音箱：did 变了或换了一代实例都要跟着走。</summary>
    public override void LoadDevices()
    {
        var next = Main.MediaSpeaker;
        if (ReferenceEquals(next, _subscribed) && ReferenceEquals(next, _speaker)) return;

        _speaker = next;
        if (!ReferenceEquals(_subscribed, next))
        {
            if (_subscribed != null) _subscribed.PropertyChanged -= OnSpeakerChanged;
            _subscribed = next;
            if (next != null) next.PropertyChanged += OnSpeakerChanged;
        }

        RaiseAll();
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.MediaSpeaker)
            or nameof(MainViewModel.HasSpeaker)
            or nameof(MainViewModel.SelectedSpeakerValue))
        {
            LoadDevices();
        }
    }

    private void OnSpeakerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MijiaDevice.IsPlaying):
            case nameof(MijiaDevice.PlayingState):
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(SpeakerMeta));
                break;
            case nameof(MijiaDevice.IsOnline):
                OnPropertyChanged(nameof(SpeakerMeta));
                break;
            case nameof(MijiaDevice.SpeakerVolume):
                OnPropertyChanged(nameof(VolumePercent));
                OnPropertyChanged(nameof(VolumeBadgeText));
                break;
            case nameof(MijiaDevice.Name):
            case nameof(MijiaDevice.RoomName):
                OnPropertyChanged(nameof(SpeakerName));
                OnPropertyChanged(nameof(SpeakerRoom));
                OnPropertyChanged(nameof(SpeakerMeta));
                break;
        }
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Speaker), nameof(ShowControls), nameof(IsEmpty),
                     nameof(SpeakerName), nameof(SpeakerRoom), nameof(SpeakerMeta), nameof(IsPlaying),
                     nameof(VolumePercent), nameof(VolumeBadgeText)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ===== 命令：转发主视图模型，下发路径与首页完全相同 =====

    [RelayCommand]
    private void TogglePlay() => Main.MediaTogglePlayCommand.Execute(null);

    [RelayCommand]
    private void Previous() => Main.MediaPreviousCommand.Execute(null);

    [RelayCommand]
    private void Next() => Main.MediaNextCommand.Execute(null);

    /// <summary>换音箱：贴桌面窗拿不到浮层，只能开一个独立小窗列出音箱。落点矩形由 View 换算后传进来。</summary>
    public void OpenSpeakerPicker(PixelRect anchor) => DesktopHost.OpenSpeakerPicker(this, anchor);

    /// <summary>音量浮层的落点矩形由 View 换算后传进来。</summary>
    public void OpenVolume(PixelRect anchor) => DesktopHost.OpenVolumeMenu(this, anchor);

    // ===== 换音箱浮层 =====

    public ObservableCollection<SpeakerPickItem> SpeakerPicks { get; } = new();

    /// <summary>每次打开浮层都重建：音箱可能刚被刷新换代，当前项也要重新比。</summary>
    public void ReloadSpeakerPicks()
    {
        var current = Main.SelectedSpeakerValue as string ?? "";
        SpeakerPicks.Clear();
        foreach (var o in Main.SpeakerOptions)
        {
            var did = o.Value as string ?? "";
            SpeakerPicks.Add(new SpeakerPickItem
            {
                Label = o.Label,
                Meta = o.Meta,
                Did = did,
                IsCurrent = did.Length > 0 && did == current
            });
        }
    }

    /// <summary>选中一台：写全局默认音箱（与首页、设置同源），卡片立刻跟着换。</summary>
    public void PickSpeaker(string? did)
    {
        if (string.IsNullOrEmpty(did)) return;
        Main.SelectedSpeakerValue = did;
        LoadDevices();
        ReloadSpeakerPicks();
    }

    protected override void RemoveZone() => DesktopHost.HideMedia();

    /// <summary>分区被随总开关收起时断开订阅，避免旧实例上的事件挂着。</summary>
    public void Detach()
    {
        Main.PropertyChanged -= OnMainChanged;
        if (_subscribed != null) _subscribed.PropertyChanged -= OnSpeakerChanged;
        _subscribed = null;
    }
}

/// <summary>换音箱浮层里的一台音箱。</summary>
public sealed class SpeakerPickItem
{
    public required string Label { get; init; }

    /// <summary>房间标注，与首页/设置的下拉同源；未分配房间时为 null，不占位。</summary>
    public string? Meta { get; init; }

    public required string Did { get; init; }

    /// <summary>是否就是当前正在控制的这台（选中态只用颜色区分，不加勾）。</summary>
    public bool IsCurrent { get; init; }
}
