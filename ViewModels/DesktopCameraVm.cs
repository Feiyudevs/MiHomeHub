// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Models;
using MiHomeHub.Services;
using MiHomeHub.Services.CameraMedia;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 桌面监控分区：一张 16:9 的摄像头实时画面。
/// 绑定的就是设置里选中的那台摄像头，解码复用随包内置的画面源与软解（go2rtc + libde265），
/// 不依赖 ffmpeg / 外置播放器 / 系统 HEVC 组件。
///
/// 画面只在分区真正展开、挂上可视树时才取流：折叠 / 收起分区就停流，
/// 免得一块看不见的卡片在后台一直占着解码线程。点画面开详情，详情里能听声音、看大画面。
/// </summary>
public partial class DesktopCameraVm : DesktopZoneVm
{
    /// <summary>解码线程与界面之间只留最新一帧：界面慢就丢帧，不排队。</summary>
    private readonly object _frameLock = new();

    private MijiaDevice? _camera;
    private MijiaDevice? _subscribed;

    private CameraHevcDecoder? _decoder;
    private CancellationTokenSource? _cts;
    private byte[]? _frameBuf;
    private int _frameWidth;
    private int _frameHeight;
    private bool _frameDirty;
    private DispatcherTimer? _uiTimer;
    private WriteableBitmap? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;

    /// <summary>每次取流 +1：换摄像头 / 停止 / 重试时，旧那一路的回调靠它作废。</summary>
    private int _generation;

    /// <summary>面板是否挂在可视树上（折叠会把内容整棵卸掉，那就该停流）。</summary>
    private bool _attached;

    public DesktopCameraVm(MainViewModel main, DesktopGroupModel model)
        : base(main, model)
    {
        main.PropertyChanged += OnMainChanged;
        LoadDevices();
        RefreshAppearance();
    }

    protected override string DangerText => "关闭监控分区";

    protected override string DangerConfirmText => "确认关闭？";

    public override string EmptyHint => "家里没有可查看的摄像头";

    /// <summary>首拉还没结束、摄像头还没认出来：这段是「还没数据」，不是「家里没摄像头」。</summary>
    public bool IsLoading => _camera == null && Main.SyncPending;

    public override bool IsEmpty => !IsLoading && _camera == null;

    /// <summary>右上角兜底的分区菜单：没有可看的摄像头时用；有摄像头时菜单在身份行最右。</summary>
    public bool ShowCornerMenu => !ShowControls && !IsLoading;

    // ===== 绑定面 =====

    public MijiaDevice? Camera => _camera;

    public bool ShowControls => _camera != null;

    public string CameraName => _camera?.Name ?? "未选择摄像头";

    /// <summary>名称左边的房间标注；未分配房间就不显示。</summary>
    public string? CameraRoom => _camera is { } d && !string.IsNullOrWhiteSpace(d.RoomName) && d.RoomName != "未分配房间"
        ? d.RoomName
        : null;

    /// <summary>副标题只报设备在不在线，取流进度另有一行。</summary>
    public string CameraMeta => _camera is { IsOnline: false } ? "离线" : "在线";

    public bool IsOnline => _camera?.IsOnline ?? false;

    // ===== 画面状态 =====

    /// <summary>画面已经解出帧、正在播。</summary>
    [ObservableProperty] private bool _isLive;

    /// <summary>还在等第一帧：画面上转圈，别先给一块黑。</summary>
    [ObservableProperty] private bool _isConnecting;

    [ObservableProperty] private string _liveStatus = "未连接";

    /// <summary>取流失败 / 中断的原因；有值时画面上直接给一句人话 + 重试。</summary>
    [ObservableProperty] private string _liveHint = "";

    [ObservableProperty] private IImage? _liveFrame;

    /// <summary>每换一帧加一，给画面控件发重绘信号（同一个位图赋值不会自己触发重绘）。</summary>
    [ObservableProperty] private int _frameVersion;

    public bool HasLiveHint => LiveHint.Length > 0;

    partial void OnLiveHintChanged(string value) => OnPropertyChanged(nameof(HasLiveHint));

    /// <summary>画面没开也没在连：显示占位与重试。</summary>
    public bool ShowPlaceholder => !IsLive && !IsConnecting;

    partial void OnIsLiveChanged(bool value) => OnPropertyChanged(nameof(ShowPlaceholder));

    partial void OnIsConnectingChanged(bool value) => OnPropertyChanged(nameof(ShowPlaceholder));

    // ===== 取流 =====

    /// <summary>面板挂上可视树（展开）时取流。</summary>
    public void AttachLive()
    {
        _attached = true;
        if (_camera == null) return;
        _ = StartLiveAsync();
    }

    /// <summary>面板离开可视树（折叠 / 关闭 / 总开关收起）时停流。</summary>
    public void StopLive()
    {
        _attached = false;
        StopLiveCore("已暂停");
    }

    /// <summary>画面上的「重试」：清掉上一路，重新连一次。</summary>
    [RelayCommand]
    private void Retry()
    {
        _attached = true;
        _ = StartLiveAsync();
    }

    private async Task StartLiveAsync()
    {
        var gen = ++_generation;
        StopLiveCore("正在连接…", keepGeneration: true);

        if (_camera is not { IsCamera: true } cam)
        {
            LiveStatus = "未连接";
            return;
        }

        IsConnecting = true;
        IsLive = false;
        LiveFrame = null;
        LiveHint = "";
        LiveStatus = "正在连接…";

        if (!cam.IsOnline)
        {
            IsConnecting = false;
            LiveStatus = "离线";
            LiveHint = "摄像机离线；它恢复联网后会自动重连。";
            return;
        }

        try
        {
            var ensure = await CameraGo2Rtc.EnsureAsync(cam).ConfigureAwait(true);
            if (gen != _generation) return;

            if (!ensure.ok)
            {
                IsConnecting = false;
                LiveStatus = "未连接";
                LiveHint = ensure.error;
                Main.AppendLog($"{cam.Name} 桌面画面失败：{ensure.error}");
                return;
            }

            var url = $"{ensure.baseUrl}/api/stream.mp4?src={Uri.EscapeDataString(cam.Did)}";
            var cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;

            var thread = new Thread(() => DecodeLoop(url, token))
            {
                IsBackground = true,
                Name = "CameraZoneDecode",
            };
            // Media Foundation / 解码调用统一放 MTA 线程，别占主线程的消息泵
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            Main.AppendLog($"{cam.Name} 桌面画面开始连接");
        }
        catch (Exception ex)
        {
            if (gen != _generation) return;
            IsConnecting = false;
            LiveStatus = "未连接";
            LiveHint = $"连接画面出错：{ex.GetBaseException().Message}";
            Main.AppendLog($"{cam.Name} 桌面画面出错：{ex.GetBaseException().Message}");
        }
    }

    /// <summary>停掉现在这一路：取消解码、清帧、清位图，状态行按调用方给的文案落。</summary>
    private void StopLiveCore(string status, bool keepGeneration = false)
    {
        if (!keepGeneration) _generation++;

        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;

        _uiTimer?.Stop();
        _uiTimer = null;
        _decoder = null;

        IsLive = false;
        IsConnecting = false;
        LiveFrame = null;
        LiveHint = "";
        LiveStatus = status;

        _bitmap = null;
        _bitmapWidth = 0;
        _bitmapHeight = 0;
        lock (_frameLock)
        {
            _frameBuf = null;
            _frameDirty = false;
        }
    }

    /// <summary>
    /// 解码线程：解出来的帧先放暂存，界面按自己的节奏只取最新一帧。
    /// 界面慢时直接丢中间帧，不会把过期帧堆在消息队列里越积越久。
    /// </summary>
    private void DecodeLoop(string url, CancellationToken token)
    {
        var decoder = new CameraHevcDecoder(url);
        if (!decoder.Open(out var error))
        {
            decoder.Dispose();
            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                IsConnecting = false;
                IsLive = false;
                LiveStatus = "未连接";
                LiveHint = $"打开画面失败：{error}";
                Main.AppendLog($"桌面画面解码失败：{error}");
            });
            return;
        }

        _decoder = decoder;
        var w = decoder.Width;
        var h = decoder.Height;
        // 参数集读不出尺寸时按 1080p 先备一块，别拿 1 字节的缓冲去接第一帧
        var frameBytes = w > 0 && h > 0 ? w * h * 4 : 1920 * 1080 * 4;
        var bgra = new byte[frameBytes];

        Dispatcher.UIThread.Post(StartFramePump);

        while (!token.IsCancellationRequested)
        {
            if (!decoder.TryReadFrame(bgra, token)) break;

            // 参数集里是编码高度（1088），真解出来的画面往往裁到 1080：
            // 按实际尺寸裁掉下面那几行空数据，否则预览底部会挂一条杂色。
            var fw = decoder.Width;
            var fh = decoder.Height;
            if (fw > 0 && fh > 0 && fw * fh * 4 > bgra.Length) bgra = new byte[fw * fh * 4];
            if (fw > 0 && fh > 0)
            {
                w = fw;
                h = fh;
            }

            lock (_frameLock)
            {
                var need = w * h * 4;
                if (need <= 0) continue;
                if (_frameBuf == null || _frameBuf.Length != need) _frameBuf = new byte[need];
                Buffer.BlockCopy(bgra, 0, _frameBuf, 0, need);
                _frameWidth = w;
                _frameHeight = h;
                _frameDirty = true;
            }
        }

        decoder.Dispose();
        if (ReferenceEquals(_decoder, decoder)) _decoder = null;

        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;   // 主动停止：状态已由 StopLiveCore 落好
            _uiTimer?.Stop();
            _uiTimer = null;
            IsLive = false;
            IsConnecting = false;
            LiveStatus = "未连接";
            LiveHint = "画面中断：摄像机可能离线或网络断开。";
        });
    }

    /// <summary>界面侧 25fps 取帧：只画最新一帧，解码再快也不会把消息队列灌满。</summary>
    private void StartFramePump()
    {
        _uiTimer?.Stop();
        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render,
            (_, _) => PumpFrame());
        _uiTimer.Start();
    }

    private void PumpFrame()
    {
        if (!IsLive && !IsConnecting) return;
        lock (_frameLock)
        {
            if (!_frameDirty || _frameBuf == null) return;
            _frameDirty = false;
            BlitFrame(_frameBuf, _frameWidth, _frameHeight);
            if (IsConnecting) IsConnecting = false;
            if (!IsLive)
            {
                IsLive = true;
                LiveStatus = "播放中";
            }
        }
    }

    /// <summary>把一帧 BGRA 写进复用的 WriteableBitmap，再抖给画面控件重绘。</summary>
    private void BlitFrame(byte[] bgra, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (_bitmap == null || _bitmapWidth != w || _bitmapHeight != h)
        {
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            _bitmapWidth = w;
            _bitmapHeight = h;
        }

        using (var fb = _bitmap.Lock())
        {
            var rowBytes = w * 4;
            if (fb.RowBytes == rowBytes)
            {
                Marshal.Copy(bgra, 0, fb.Address, rowBytes * h);
            }
            else
            {
                for (var y = 0; y < h; y++)
                    Marshal.Copy(bgra, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
            }
        }

        LiveFrame = _bitmap;
        FrameVersion++;
    }

    // ===== 设备绑定 =====

    /// <summary>设备列表换代（切家庭 / 刷新）后重取摄像头：did 变了或换了一代实例都要跟着走。</summary>
    public override void LoadDevices()
    {
        var next = Main.DesktopCamera;
        if (ReferenceEquals(next, _subscribed) && ReferenceEquals(next, _camera)) return;

        var changedDevice = !ReferenceEquals(next, _camera);
        _camera = next;
        if (!ReferenceEquals(_subscribed, next))
        {
            if (_subscribed != null) _subscribed.PropertyChanged -= OnCameraChanged;
            _subscribed = next;
            if (next != null) next.PropertyChanged += OnCameraChanged;
        }

        RaiseAll();

        // 换成另一台摄像头（设置里改了选中项）或这台没了：正在播就按新的那台重连 / 停流
        if (changedDevice)
        {
            if (_camera == null) StopLiveCore("已暂停");
            else if (_attached) _ = StartLiveAsync();
        }
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.DesktopCamera)
            or nameof(MainViewModel.HasDesktopCamera)
            or nameof(MainViewModel.SelectedCameraValue))
        {
            LoadDevices();
        }
        else if (e.PropertyName == nameof(MainViewModel.SyncPending))
        {
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShowCornerMenu));
        }
    }

    private void OnCameraChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MijiaDevice.IsOnline):
                OnPropertyChanged(nameof(CameraMeta));
                OnPropertyChanged(nameof(IsOnline));
                // 重新上线时自动补一次连接，不用用户手动点重试
                if (_camera is { IsOnline: true } && _attached && !IsLive && !IsConnecting)
                    _ = StartLiveAsync();
                break;
            case nameof(MijiaDevice.Name):
            case nameof(MijiaDevice.RoomName):
                OnPropertyChanged(nameof(CameraName));
                OnPropertyChanged(nameof(CameraRoom));
                break;
        }
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Camera), nameof(ShowControls), nameof(IsLoading), nameof(IsEmpty),
                     nameof(ShowCornerMenu), nameof(CameraName), nameof(CameraRoom), nameof(CameraMeta),
                     nameof(IsOnline)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>点画面 / 身份行：打开这台摄像头的详情，听声音、看大画面都在那里。</summary>
    [RelayCommand]
    private void OpenPanel()
    {
        if (_camera is { } cam) DesktopHost.OpenDevicePanel(Main, cam, this);
    }

    protected override void RemoveZone() => DesktopHost.HideCamera();

    /// <summary>分区被随总开关收起时断开订阅、停掉解码线程，避免旧实例上的事件与线程挂着。</summary>
    public void Detach()
    {
        StopLiveCore("已暂停");
        Main.PropertyChanged -= OnMainChanged;
        if (_subscribed != null) _subscribed.PropertyChanged -= OnCameraChanged;
        _subscribed = null;
    }
}
