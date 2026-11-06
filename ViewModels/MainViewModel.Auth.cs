using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
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
        _client?.Dispose();
        _client = null;
        // 退出登录时暂停轮询，避免 _client=null 后 timer 仍每秒唤醒
        PausePolling();
        // 退出登录时释放跨会话常驻缓存：Skia 原生像素不计入托管堆，不 Dispose 会一直占内存
        MijiaIconService.Reset();
        DeviceSpecService.Clear();
        // 桌面组件窗口持有 MijiaDevice 实例（进而持有 Icon Bitmap），不清空它们设备就回收不掉
        DesktopHost.HideAll();
        // 断开每台设备对产品图 Bitmap 的引用，避免 Skia 原生像素残留
        foreach (var d in _allDevices)
            d.Icon = null;
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
        StatDevices.Clear();
        SpeakerOptions.Clear();
        HasEnvironment = false;
        HasConsumables = false;
        HasConsumableWarning = false;
        ConsumableLowCount = 0;
        IsEnvDetailOpen = false;
        IsStatListOpen = false;
        HasStatDevices = false;
        StatListSubtitle = null;
        StatListEmptyText = "";
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
        IsMusicOpen = false;
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
        LightOnCount = 0;
        LightTotalText = "";
        CurtainOnCount = 0;
        CurtainTotalText = "";
        AcOnCount = 0;
        AcTotalText = "";
        HasOffline = false;
        OfflineCount = 0;
        RoomCount = 0;
        TotalText = "";
        OfflineText = "";
        BannerTitle = "全屋设备";
        BannerDesc = "统一管理家中所有智能设备";
        BannerTotal = 0;
        BannerOn = 0;
        BannerOffline = 0;

        // 退出登录后立即强制回收：压缩 LOH、执行完整 GC，并请示 Windows 收缩工作集，
        // 让内存立即回落，不再等待 30 秒空闲自动回收或下一次 GC。
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        try { SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); } catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
