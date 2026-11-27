// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
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
using MiHomeHub.Services;
using MiHomeHub.Services.CameraMedia;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 摄像头内置画面：用程序自带的画面源取流、显卡硬解，直接把帧画进详情页。
/// 不弹浏览器、不调外部播放器，用户机器上不用预装任何东西。
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _isCameraLive;
    /// <summary>还在等第一帧：界面上转圈，别先给一块黑。</summary>
    [ObservableProperty] private bool _isCameraConnecting;
    [ObservableProperty] private string _cameraWindowTitle = "实时画面";
    /// <summary>当前画面流里有没有音轨；没有时声音开关置灰，不假装能响。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraSoundLabel))]
    private bool _cameraSoundAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraSoundLabel))]
    private bool _cameraSoundOn;

    /// <summary>声音接上了，但采到的几乎全是静音（多半是摄像机麦克风没开）。</summary>
    [ObservableProperty]
    private bool _cameraSoundSilent;

    /// <summary>声音按钮上的字：接上了显示开 / 静音，没接上就还是「声音」。</summary>
    public string CameraSoundLabel => CameraSoundAvailable ? (CameraSoundOn ? "声音" : "静音") : "声音";
    [ObservableProperty] private string _cameraLiveStatus = "未打开";
    [ObservableProperty] private string _cameraLiveBadge = "";
    [ObservableProperty] private string _cameraLiveHint = "";
    [ObservableProperty] private IImage? _cameraLiveFrame;
    [ObservableProperty] private bool _cameraNeedHevc;
    /// <summary>每换一帧加一，给画面控件发重绘信号（同一个位图赋值不会自己触发重绘）。</summary>
    [ObservableProperty] private int _cameraFrameVersion;

    private CameraHevcDecoder? _cameraDecoder;
    private CameraAudioSource? _cameraAudio;
    private CameraAudioPlayer? _audioPlayer;
    private string _cameraStreamDid = "";
    private CancellationTokenSource? _cameraCts;
    private WriteableBitmap? _cameraBitmap;
    private int _cameraBitmapWidth;
    private int _cameraBitmapHeight;

    // 解码线程与界面之间只留最新一帧：界面慢就丢帧，不排队。
    private readonly object _frameLock = new();
    private byte[]? _frameBuf;
    private int _frameWidth;
    private int _frameHeight;
    private bool _frameDirty;
    private DispatcherTimer? _uiTimer;
    private int _drawnFrames;

    /// <summary>在内置页面里直接播放当前抽屉里的摄像头。</summary>
    [RelayCommand]
    private async Task StartCameraLive()
    {
        if (DrawerDevice is not { IsCamera: true } d) return;
        if (IsCameraLive) return;

        CameraNeedHevc = false;
        IsCameraLive = true;
        IsCameraConnecting = true;
        CameraLiveStatus = "正在连接…";
        CameraLiveBadge = "";
        CameraLiveHint = "";
        Log($"{d.Name} 开始连接内置画面");

        var ensure = await CameraGo2Rtc.EnsureAsync(d).ConfigureAwait(true);
        if (!ensure.ok)
        {
            CameraLiveHint = ensure.error;
            IsCameraLive = false;
            Log($"{d.Name} 内置画面失败：{ensure.error}");
            return;
        }

        _cameraStreamDid = d.Did;
        var url = $"{ensure.baseUrl}/api/stream.mp4?src={Uri.EscapeDataString(d.Did)}";
        _cameraCts = new CancellationTokenSource();
        var token = _cameraCts.Token;

        var thread = new Thread(() => DecodeLoop(url, token))
        {
            IsBackground = true,
            Name = "CameraLiveDecode",
        };
        // Media Foundation 必须在 MTA 线程上跑，主线程是 STA 会卡在消息泵上。
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    [RelayCommand]
    private void StopCameraLive()
    {
        StopCameraLiveCore("");
    }

    /// <summary>放到独立窗口里看：画面还是这一路，不重新连、不重复解码。</summary>
    [RelayCommand]
    private void OpenCameraPlayerWindow()
    {
        CameraWindowTitle = DrawerDevice is { IsCamera: true } ? $"{DrawerDevice.Name} · 实时画面" : "实时画面";
        CameraPlayerHost.Open(this);
    }

    /// <summary>
    /// 声音单独走内置画面源的 RTSP：mp4 里只有视频，音频是那条 PCMA/16000 轨。
    /// 起不来就静默降级，画面不受影响。
    /// </summary>
    private void StartCameraAudio()
    {
        var rtsp = CameraGo2Rtc.RtspUrl;
        var name = _cameraStreamDid;
        if (string.IsNullOrEmpty(rtsp) || string.IsNullOrEmpty(name)) return;

        var player = new CameraAudioPlayer();
        if (!player.Available)
        {
            player.Dispose();
            return;
        }

        // 画面源在流刚起来的一小段时间里会把 RTSP 连接掐掉，重试几次基本就能接上
        CameraAudioSource? source = null;
        var error = "";
        long silentSamples = 0;
        var silentPeak = 0;
        var silentChecked = false;
        var silentCleared = true;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) Thread.Sleep(1500);
            var candidate = new CameraAudioSource();
            candidate.Pcm += pcm =>
            {
                player.Add(pcm);
                silentSamples += pcm.Length / 2;
                for (var i = 0; i + 1 < pcm.Length; i += 2)
                {
                    var v = Math.Abs(BitConverter.ToInt16(pcm, i));
                    if (v > silentPeak) silentPeak = v;
                }
                // 一直盯着峰值：先静、后来又出声的（刚接上时头两秒常是空的），提示要自己撤掉
                if (silentCleared && silentChecked && silentPeak > 900)
                {
                    silentCleared = false;
                    Dispatcher.UIThread.Post(() => CameraSoundSilent = false);
                }
                if (silentChecked || silentSamples < 16000 * 5) return;
                silentChecked = true;
                // 5 秒几乎全静音：多半是摄像头那头麦克风没开，不是我们没解出来
                if (silentPeak < 400) Dispatcher.UIThread.Post(() => CameraSoundSilent = true);
            };
            if (candidate.Start(rtsp, name, out error))
            {
                source = candidate;
                break;
            }
            candidate.Dispose();
            Log($"声音第 {attempt + 1} 次没接上：{error}");
        }

        if (source == null)
        {
            player.Dispose();
            return;
        }

        _cameraAudio = source;
        _audioPlayer = player;
        Dispatcher.UIThread.Post(() =>
        {
            CameraSoundAvailable = true;
            // 默认静音：谁也不想打开监控就被屋里说话声顶一下；想听就点一下「声音」
            CameraSoundOn = !CameraMuteOnOpen;
            player.Muted = CameraMuteOnOpen;
        });
    }

    /// <summary>「打开时静音」这个偏好本身：只影响下次起声音，不打断正在播的现场。</summary>
    [RelayCommand]
    private void ToggleCameraMuteOnOpen()
    {
        CameraMuteOnOpen = !CameraMuteOnOpen;
        Log(CameraMuteOnOpen ? "打开画面时默认静音" : "打开画面时直接出声");
    }
    /// <summary>声音开关：接上了就真出声 / 真静音，没接上就置灰。</summary>
    [RelayCommand]
    private void ToggleCameraSound()
    {
        if (!CameraSoundAvailable || _audioPlayer == null) return;
        CameraSoundOn = !CameraSoundOn;
        _audioPlayer.Muted = !CameraSoundOn;
        Log(CameraSoundOn ? "摄像机声音：开" : "摄像机声音：静音");
    }

    /// <summary>缺组件时把用户带到商店那一页，装完回来自动按新状态重新连。</summary>
    [RelayCommand]
    private void OpenCameraHevcInstall()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-windows-store://search/?query=HEVC%20video%20extensions")
            {
                UseShellExecute = true,
            });
            CameraLiveHint = "已打开商店的 HEVC 组件页。装完回到这里点「重试」，会自动重新检测。";
            Log("已打开商店的 HEVC 组件页");
        }
        catch (Exception ex)
        {
            CameraLiveHint = $"打开商店失败（{ex.GetBaseException().Message}）：可在商店里搜「HEVC 视频扩展」手动安装。";
            Log($"打开商店失败：{ex.GetBaseException().Message}");
        }
    }

    private void StopCameraLiveCore(string hint)
    {
        try { _cameraCts?.Cancel(); } catch { }
        try { _cameraAudio?.Dispose(); } catch { }
        try { _audioPlayer?.Dispose(); } catch { }
        _cameraAudio = null;
        _audioPlayer = null;
        CameraSoundAvailable = false;
        CameraSoundOn = false;
        CameraSoundSilent = false;
        _cameraCts?.Dispose();
        _cameraCts = null;
        _uiTimer?.Stop();
        _uiTimer = null;
        IsCameraLive = false;
        IsCameraConnecting = false;
        CameraLiveStatus = "未打开";
        CameraLiveBadge = "";
        CameraLiveFrame = null;
        CameraPlayerHost.Close();
        _cameraBitmap = null;
        _cameraBitmapWidth = 0;
        _cameraBitmapHeight = 0;
        lock (_frameLock)
        {
            _frameBuf = null;
            _frameDirty = false;
        }
        CameraLiveHint = hint;
    }

    /// <summary>
    /// 解码线程：解出来的帧先放进一块暂存，再由界面按自己的节奏取最新的那一帧。
    /// 界面慢的时候直接丢中间帧，不会把过期的帧堆在消息队列里越积越久。
    /// </summary>
    private void DecodeLoop(string url, CancellationToken token)
    {
        var decoder = new CameraHevcDecoder(url);
        if (!decoder.Open(out var error))
        {
            Dispatcher.UIThread.Post(() =>
            {
                CameraLiveHint = $"打开画面失败：{error}";
                IsCameraLive = false;
                Log($"内置画面解码失败：{error}");
            });
            decoder.Dispose();
            return;
        }

        _cameraDecoder = decoder;
        int w = decoder.Width, h = decoder.Height;
        var bgra = new byte[w * h * 4];
        var frames = 0;
        var audioStarted = false;
        var fpsWindow = DateTime.Now;

        Dispatcher.UIThread.Post(StartFramePump);

        while (!token.IsCancellationRequested)
        {
            if (!decoder.TryReadFrame(bgra, token)) break;

            // 第一帧出来说明画面源这边的流已经真跑起来了，这时再接声音才不会被打回
            if (!audioStarted) { audioStarted = true; StartCameraAudio(); }

            // 参数集里是编码高度（1088），真正解出来的画面往往裁到 1080：
            // 按实际尺寸裁掉下面那几行空数据，否则预览底部会挂一条杂色。
            var fw = decoder.Width;
            var fh = decoder.Height;
            if (fw > 0 && fh > 0 && (fw != w || fh != h))
            {
                w = fw;
                h = fh;
                Dispatcher.UIThread.Post(() => CameraLiveBadge = $"{w}×{h}");
            }

            lock (_frameLock)
            {
                var need = w * h * 4;
                if (_frameBuf == null || _frameBuf.Length != need) _frameBuf = new byte[need];
                Buffer.BlockCopy(bgra, 0, _frameBuf, 0, need);
                _frameWidth = w;
                _frameHeight = h;
                _frameDirty = true;
            }

            frames++;
            if ((DateTime.Now - fpsWindow).TotalSeconds >= 2)
            {
                var fps = frames / (DateTime.Now - fpsWindow).TotalSeconds;
                frames = 0;
                fpsWindow = DateTime.Now;
                var shown = $"{fps:F1}";
                var drawn = Volatile.Read(ref _drawnFrames);
                Dispatcher.UIThread.Post(() =>
                {
                    CameraLiveBadge = $"{w}×{h} · {shown} fps";
                    CameraLiveStatus = "播放中";
                });
            }
        }

        decoder.Dispose();
        if (ReferenceEquals(_cameraDecoder, decoder)) _cameraDecoder = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested)
            {
                if (IsCameraLive) StopCameraLiveCore("");
            }
            else
            {
                CameraLiveHint = "画面中断：摄像机可能离线或网络断开。";
                IsCameraLive = false;
                _uiTimer?.Stop();
            }
        });
    }

    /// <summary>界面侧 25fps 取帧：只画最新一帧，解码再快也不会把消息队列灌满。</summary>
    private void StartFramePump()
    {
        _uiTimer?.Stop();
        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => PumpFrame());
        _uiTimer.Start();
    }

    private void PumpFrame()
    {
        if (!IsCameraLive) return;
        lock (_frameLock)
        {
            if (!_frameDirty || _frameBuf == null) return;
            _frameDirty = false;
            BlitCore(_frameBuf, _frameWidth, _frameHeight);
            _drawnFrames++;
            if (IsCameraConnecting) IsCameraConnecting = false;
        }
    }

    /// <summary>把一帧 BGRA 写进复用的 WriteableBitmap，再抖给 Image 重绘。</summary>
    private void BlitCore(byte[] bgra, int w, int h)
    {
        if (_cameraBitmap == null || _cameraBitmapWidth != w || _cameraBitmapHeight != h)
        {
            _cameraBitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            _cameraBitmapWidth = w;
            _cameraBitmapHeight = h;
        }

        using (var fb = _cameraBitmap.Lock())
        {
            var rowBytes = w * 4;
            if (fb.RowBytes == rowBytes)
            {
                // 行宽正好对齐时一次拷完，省掉逐行调用
                Marshal.Copy(bgra, 0, fb.Address, rowBytes * h);
            }
            else
            {
                for (var y = 0; y < h; y++)
                {
                    Marshal.Copy(bgra, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
                }
            }
        }

        // 位图内容变了但实例没变：控件靠版本号知道要重画
        CameraLiveFrame = _cameraBitmap;
        CameraFrameVersion++;
    }
}
