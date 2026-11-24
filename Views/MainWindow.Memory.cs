// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia.Controls;
using Avalonia.Threading;
using MiHome.Services;

namespace MiHome.Views;

public partial class MainWindow
{
    private static readonly TimeSpan IdleTrimDelay = TimeSpan.FromSeconds(30);

    private DateTimeOffset _backgroundSince = DateTimeOffset.UtcNow;
    private DispatcherTimer? _idleTrimTimer;
    private bool _memoryTrimmersActive;
    private bool _memoryTrimmersClosed;
    private bool _idleTrimmed;

    /// <summary>
    /// 自动回收只在窗口不可见 / 最小化时进行。
    ///
    /// 可见时做完整 GC（尤其 LOH 压缩）会暂停 UI 线程：实测滚轮停下 1.5s 后触发的那次回收
    /// 连续冻了 58.9ms + 39.1ms，正好落在用户滚完列表、把鼠标移向卡片的瞬间；
    /// 回收末尾的 SetProcessWorkingSetSize 还会清空工作集，让随后的鼠标移动去缺页。
    /// 所以可见期间不再自动回收，内存回落交给 Hide / 最小化时的 RequestReleasedContentTrim。
    /// </summary>
    private bool CanRunAutomaticMemoryTrimmers =>
        !_memoryTrimmersClosed && (!IsVisible || WindowState == WindowState.Minimized);

    private void InitIdleMemoryTrimmer()
    {
        _idleTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _idleTrimTimer.Tick += OnIdleMemoryTrimTick;
        Closed += (_, _) =>
        {
            _memoryTrimmersClosed = true;
            StopAutomaticMemoryTrimmers();
        };
        UpdateAutomaticMemoryTrimmerActivity();
    }

    private void UpdateAutomaticMemoryTrimmerActivity()
    {
        // Avalonia 在基类构造/InitializeComponent 期间也会调用 OnPropertyChanged。
        if (_idleTrimTimer is null) return;
        if (!CanRunAutomaticMemoryTrimmers)
        {
            StopAutomaticMemoryTrimmers();
            return;
        }
        if (_memoryTrimmersActive) return;
        _memoryTrimmersActive = true;
        _idleTrimmed = false;
        _backgroundSince = DateTimeOffset.UtcNow;
        _idleTrimTimer.Start();
    }

    private void StopAutomaticMemoryTrimmers()
    {
        _memoryTrimmersActive = false;
        _idleTrimTimer?.Stop();
    }

    private void OnIdleMemoryTrimTick(object? sender, EventArgs e)
    {
        if (!CanRunAutomaticMemoryTrimmers)
        {
            StopAutomaticMemoryTrimmers();
            return;
        }
        // 每次进入后台只整理一次：Hide / 最小化时已经做过 TrimReleasedUi，
        // 这里再补一次，避免常驻托盘期间每隔 30 秒反复全量 GC。
        if (_idleTrimmed || DateTimeOffset.UtcNow - _backgroundSince < IdleTrimDelay) return;

        if (MemoryTrimmer.TryTrim(out _))
        {
            _idleTrimmed = true;
            _backgroundSince = DateTimeOffset.UtcNow;
        }
    }
}
