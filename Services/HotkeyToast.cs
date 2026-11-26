// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MiHomeHub.Views;

namespace MiHomeHub.Services;

/// <summary>toast 表达的结果类型：决定图标、状态徽标与进度条的配色。</summary>
public enum HotkeyToastKind
{
    /// <summary>设备 / 自动化被打开。</summary>
    On,
    /// <summary>设备 / 自动化被关闭。</summary>
    Off,
    /// <summary>场景已执行、多步全部完成。</summary>
    Done,
    /// <summary>只完成了一部分（有离线 / 目标不可用 / 失败）。</summary>
    Warn,
    /// <summary>压根没执行（未登录、没有控制卡片、全部失败）。</summary>
    Failed
}

/// <summary>
/// 快捷键 toast 的单例：复用同一个无边框窗口，连续触发只替换文案并重置计时，
/// 不会叠出一串窗口。上浮淡入 → 停留 2.3s（底部进度条同步收回）→ 下沉淡出 → 隐藏。
/// </summary>
public static class HotkeyToast
{
    /// <summary>停留时长，也是底部进度条走完的时间。</summary>
    private const int HoldMs = 2300;

    /// <summary>进度条刷新间隔。</summary>
    private const int TickMs = 40;

    private static HotkeyToastWindow? _window;
    private static CancellationTokenSource? _cts;

    /// <summary>显示一条快捷键执行结果；任意线程可调，内部会切到 UI 线程。</summary>
    public static void Show(string head, string body, HotkeyToastKind kind = HotkeyToastKind.Done)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(head, body, kind));
            return;
        }

        ShowCore(head, body, kind);
    }

    private static async void ShowCore(string head, string body, HotkeyToastKind kind)
    {
        try
        {
            var window = EnsureWindow();

            // 上一条还在淡入/停留/淡出：直接取消，让位给新文案
            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;

            window.SetContent(head, body, kind);
            window.ResetMotion();
            window.Opacity = 0;
            if (!window.IsVisible) window.Show();
            window.Topmost = true;

            // 先按实测尺寸贴到右下角，再开始淡入：SizeToContent 的高度要等布局跑完才是真的，
            // 用估算值摆会让卡片第一帧落在偏上 / 偏中的位置，等淡入到一半再跳回角落。
            window.PositionBottomRight();
            await Dispatcher.UIThread.InvokeAsync(window.PositionBottomRight, DispatcherPriority.Loaded);

            const int enterSteps = 7;
            for (var i = 1; i <= enterSteps; i++)
            {
                if (cts.IsCancellationRequested) return;
                var t = i / (double)enterSteps;
                window.Opacity = t;
                window.SetLift(t);
                await Task.Delay(22, cts.Token);
            }

            var elapsed = 0;
            while (elapsed < HoldMs)
            {
                await Task.Delay(TickMs, cts.Token);
                elapsed += TickMs;
                window.SetProgress(1 - elapsed / (double)HoldMs);
            }

            const int exitSteps = 6;
            for (var i = exitSteps - 1; i >= 0; i--)
            {
                if (cts.IsCancellationRequested) return;
                var t = i / (double)exitSteps;
                window.Opacity = t;
                window.SetLift(t);
                await Task.Delay(28, cts.Token);
            }

            if (!cts.IsCancellationRequested) window.Hide();
        }
        catch (OperationCanceledException)
        {
            // 被下一条 toast 顶掉，属于正常路径
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"快捷键 toast 显示失败：{ex.Message}");
        }
    }

    private static HotkeyToastWindow EnsureWindow()
    {
        if (_window != null) return _window;

        _window = new HotkeyToastWindow();
        _window.Closed += (_, _) => _window = null;
        return _window;
    }
}
