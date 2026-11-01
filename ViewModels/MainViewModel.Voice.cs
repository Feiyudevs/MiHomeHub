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
        OnPropertyChanged(nameof(MediaSpeaker));
    }

    [RelayCommand]
    private void ToggleVoice() => IsVoiceOpen = !IsVoiceOpen;

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

    // ===== 首页「音响播放」媒体区：控制所选音箱（DefaultSpeaker），与音箱下拉同源 =====

    /// <summary>媒体区当前控制的音箱：跟随音箱下拉选择。</summary>
    public MijiaDevice? MediaSpeaker => DefaultSpeaker;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task MediaTogglePlayAsync()
    {
        if (DefaultSpeaker is not { } d) return;
        var play = !d.IsPlaying;
        if (d.PlaySiid > 0 || d.TogglePlaySiid > 0 || d.HasTextDirective)
        {
            var sent = await RunSpeakerPowerAsync(d, play);
            if (!sent) return;
            d.IsPlaying = play;
            Log($"{d.Name} → {(play ? "播放" : "暂停")}");
        }
        else Log($"{d.Name} 不支持播放控制");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task MediaPreviousAsync()
    {
        var d = DefaultSpeaker;
        return SendSpeakerActionAsync(d, "上一首", d?.PreviousSiid ?? 0, d?.PreviousAiid ?? 0);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task MediaNextAsync()
    {
        var d = DefaultSpeaker;
        return SendSpeakerActionAsync(d, "下一首", d?.NextSiid ?? 0, d?.NextAiid ?? 0);
    }

    /// <summary>抽屉里的上一曲/下一曲：优先规格动作，缺失时退化成文本指令。</summary>
    private async Task RunTrackStepAsync(bool next)
    {
        var d = DrawerDevice;
        await SendSpeakerActionAsync(d,
            next ? "下一首" : "上一首",
            next ? d?.NextSiid ?? 0 : d?.PreviousSiid ?? 0,
            next ? d?.NextAiid ?? 0 : d?.PreviousAiid ?? 0);
    }

    /// <summary>带播放控制卡片的动作下发：统一处理离线/忙/异常，返回是否真的发给了设备。</summary>
    private async Task SendSpeakerActionAsync(MijiaDevice? device, string label, int siid, int aiid)
    {
        if (_client is not { } client || device is not { IsSpeaker: true } d) return;
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
}
