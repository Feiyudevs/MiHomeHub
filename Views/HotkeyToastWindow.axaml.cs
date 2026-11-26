// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MiHomeHub.Services;

namespace MiHomeHub.Views;

/// <summary>
/// 快捷键触发时的桌面 toast：无边框、置顶、不抢焦点，贴在主屏工作区右下角。
/// 只负责「长什么样 + 摆在哪」，显隐节奏与复用由 <see cref="Services.HotkeyToast"/> 控制。
/// 开关状态靠图标底色、状态徽标（已开启 / 已关闭）、进度条三者同时表达，彩色一目了然。
/// </summary>
public partial class HotkeyToastWindow : Window
{
    /// <summary>离屏幕右下角的留白（DIP）。</summary>
    private const int ScreenMargin = 18;

    /// <summary>入场 / 退场时卡片纵向位移的幅度（DIP）。</summary>
    private const double LiftDip = 10;

    private readonly TranslateTransform _lift = new();
    private readonly ScaleTransform _progressScale = new();

    private bool _repositionQueued;

    public HotkeyToastWindow()
    {
        InitializeComponent();

        Card.RenderTransform = _lift;
        ProgressFill.RenderTransform = _progressScale;

        // SizeToContent=Height：文案行数一变高度就变，窗口会原地「长高」而不再贴底，
        // 所以每次尺寸变化后都要把卡片重新贴回右下角。
        SizeChanged += (_, _) => RepositionSoon();
    }

    /// <summary>写入两行文案与开关状态：第一行是快捷键名，第二行是这次执行结果。</summary>
    public void SetContent(string head, string body, HotkeyToastKind kind)
    {
        HeadText.Text = head;
        BodyText.Text = body;
        StateText.Text = kind switch
        {
            HotkeyToastKind.On => "已开启",
            HotkeyToastKind.Off => "已关闭",
            HotkeyToastKind.Warn => "部分完成",
            HotkeyToastKind.Failed => "未执行",
            _ => "已完成"
        };

        Card.Classes.Set("on", kind == HotkeyToastKind.On);
        Card.Classes.Set("off", kind == HotkeyToastKind.Off);
        Card.Classes.Set("warn", kind == HotkeyToastKind.Warn);
        Card.Classes.Set("failed", kind == HotkeyToastKind.Failed);
    }

    /// <summary>每次新提示开始前把动效复位：卡片下沉待入场、进度条满格。</summary>
    public void ResetMotion()
    {
        _lift.Y = LiftDip;
        _progressScale.ScaleX = 1;
    }

    /// <summary>入场 / 退场位移：<paramref name="t"/> 从 0（下沉）到 1（贴齐）。</summary>
    public void SetLift(double t) => _lift.Y = LiftDip * (1 - Math.Clamp(t, 0, 1));

    /// <summary>停留倒计时进度：<paramref name="remaining"/> 从 1 收到 0。</summary>
    public void SetProgress(double remaining)
        => _progressScale.ScaleX = Math.Clamp(remaining, 0, 1);

    /// <summary>贴到主屏工作区右下角（按布局后的实测尺寸换算成物理像素）。</summary>
    public void PositionBottomRight() => PopupPlacement.BottomRight(this, ScreenMargin);

    /// <summary>布局还没跑完时 Bounds 是 0，定位推到下一轮 UI 循环里做，避免用估算尺寸摆错。</summary>
    private void RepositionSoon()
    {
        if (_repositionQueued) return;
        _repositionQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _repositionQueued = false;
            PositionBottomRight();
        }, DispatcherPriority.Loaded);
    }
}
