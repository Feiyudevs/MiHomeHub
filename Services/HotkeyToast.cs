// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MiHome.Views;

namespace MiHome.Services;

/// <summary>
/// 快捷键 toast 的单例：复用同一个无边框窗口，连续触发只替换文案并重置计时，
/// 不会叠出一串窗口。淡入 → 停留 2.3s → 淡出 → 隐藏。
/// </summary>
public static class HotkeyToast
{
    private static HotkeyToastWindow? _window;
    private static CancellationTokenSource? _cts;

    /// <summary>显示一条快捷键执行结果；任意线程可调，内部会切到 UI 线程。</summary>
    public static void Show(string head, string body)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(head, body));
            return;
        }

        ShowCore(head, body);
    }

    private static async void ShowCore(string head, string body)
    {
        try
        {
            var window = EnsureWindow();

            // 上一条还在淡入/停留/淡出：直接取消，让位给新文案
            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;

            window.SetContent(head, body);
            window.Opacity = 0;
            if (!window.IsVisible) window.Show();
            window.Topmost = true;
            window.PositionBottomRight();

            for (var i = 1; i <= 6; i++)
            {
                if (cts.IsCancellationRequested) return;
                window.Opacity = i / 6d;
                await Task.Delay(22, cts.Token);
            }

            await Task.Delay(2300, cts.Token);

            for (var i = 5; i >= 0; i--)
            {
                if (cts.IsCancellationRequested) return;
                window.Opacity = i / 6d;
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
