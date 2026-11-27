// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using MiHomeHub.Services.MacOS;

namespace MiHomeHub.Services;

/// <summary>托盘后端抽象：Windows 走 Shell_NotifyIcon，macOS 走 NSStatusItem。</summary>
internal interface ITrayIconBackend : IDisposable
{
    event Action? LeftClicked;
    event Action? RightClicked;
    event Action? BalloonClicked;
    void Notify(string title, string text);
    PixelRect? IconRect();
}

/// <summary>
/// 托盘菜单命令（目前只有 macOS 的原生菜单实现）。
/// Windows 侧继续用自绘的 TrayMenuWindow 弹层菜单，不走这里。
/// </summary>
internal interface ITrayMenuCommands
{
    event Action? ShowMainRequested;
    event Action? ToggleDesktopWidgetsRequested;
    event Action? ToggleThemeRequested;
    event Action? OpenSettingsRequested;
    event Action? RestartRequested;
    event Action? ExitRequested;
}

/// <summary>
/// 托盘图标门面：构造时**先按平台分派**，再碰任何平台 API。
///
/// Windows：自己托管 Shell_NotifyIcon（实现见 TrayIconHost.Windows.cs），
///           左键开主窗口、右键弹菜单、气泡提示。
/// macOS：  Avalonia 的 TrayIcon 在 macOS 上不抛 Clicked（原生层从不设置 OnClicked），
///           交互统一走状态栏图标自带的原生菜单（实现见 Services/MacOS/MacOsTrayIcon.cs）。
///
/// 重要：macOS 分支一行 user32/shell32 的 P/Invoke 都不会碰到——这两个库在 macOS 上根本不存在，
/// 早先版本的构造函数直接调 RegisterWindowMessage，macOS 真机启动就是在这里崩的。
///
/// 托盘建不起来时（缺权限、平台不支持、窗口句柄缺失）一律静默降级：不抛异常，
/// <see cref="IsAvailable"/> 置 false，调用方据此改成「直接退出」，
/// 避免窗口收进托盘后用户再也找不回应用。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    /// <summary>
    /// 托盘图标是否真的建起来了。macOS 上如果状态栏图标创建失败，窗口「关闭时收进托盘」
    /// 就会把应用藏起来且无处找回，所以调用方要据此改成直接退出。
    /// </summary>
    public static bool IsAvailable { get; private set; } = true;

    private readonly ITrayIconBackend? _backend;

    /// <summary>左键点击托盘图标（仅 Windows；macOS 左键直接弹原生菜单，不回调）。</summary>
    public event Action? LeftClicked;

    /// <summary>右键点击托盘图标（仅 Windows）。</summary>
    public event Action? RightClicked;

    /// <summary>用户点了气泡本体（仅 Windows）。</summary>
    public event Action? BalloonClicked;

    /// <summary>原生菜单命令「显示主界面」（macOS）。</summary>
    public event Action? ShowMainRequested;

    /// <summary>原生菜单命令「显示-隐藏桌面组件」（macOS）。</summary>
    public event Action? ToggleDesktopWidgetsRequested;

    /// <summary>原生菜单命令「切换主题」（macOS）。</summary>
    public event Action? ToggleThemeRequested;

    /// <summary>原生菜单命令「设置」（macOS）。</summary>
    public event Action? OpenSettingsRequested;

    /// <summary>原生菜单命令「重新启动」（macOS）。</summary>
    public event Action? RestartRequested;

    /// <summary>原生菜单命令「退出」（macOS）。</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// 创建托盘图标。构造函数里先分派平台再干活：
    /// Windows 走 Shell_NotifyIcon，macOS 走 Avalonia TrayIcon + 原生菜单。
    /// 创建失败不抛异常，只把 <see cref="IsAvailable"/> 置 false（App 不能起不来）。
    /// </summary>
    public TrayIconHost(TopLevel owner, string tooltip)
    {
        ITrayIconBackend? backend = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                backend = new WindowsTrayIconBackend(owner, tooltip);
            }
            else if (OperatingSystem.IsMacOS())
            {
                backend = new MacOsTrayIcon(tooltip);
            }
        }
        catch (Exception ex)
        {
            // 建托盘失败不是致命错误：静默降级，留一行日志便于排查
            DesktopLog.Write($"tray icon create failed: {ex.GetType().Name}: {ex.Message}");
            backend = null;
        }

        _backend = backend;
        IsAvailable = backend is not null;
        if (backend is null) return;

        backend.LeftClicked += () => LeftClicked?.Invoke();
        backend.RightClicked += () => RightClicked?.Invoke();
        backend.BalloonClicked += () => BalloonClicked?.Invoke();

        if (backend is ITrayMenuCommands commands)
        {
            commands.ShowMainRequested += () => ShowMainRequested?.Invoke();
            commands.ToggleDesktopWidgetsRequested += () => ToggleDesktopWidgetsRequested?.Invoke();
            commands.ToggleThemeRequested += () => ToggleThemeRequested?.Invoke();
            commands.OpenSettingsRequested += () => OpenSettingsRequested?.Invoke();
            commands.RestartRequested += () => RestartRequested?.Invoke();
            commands.ExitRequested += () => ExitRequested?.Invoke();
        }
    }

    /// <summary>
    /// 创建托盘图标的推荐入口：成功返回实例，失败返回 null 并把 <see cref="IsAvailable"/> 置 false。
    /// 调用方据此决定「关窗口收进托盘」还是「直接退出」——图标没建起来时收进托盘就再也找不回来了。
    /// </summary>
    public static TrayIconHost? Create(TopLevel owner, string tooltip)
    {
        var host = new TrayIconHost(owner, tooltip);
        return IsAvailable ? host : null;
    }

    /// <summary>
    /// 弹一条托盘气泡（非模态，不抢焦点）。
    /// Windows 走系统通知中心；macOS 没有等价物，退化成通知中心的横幅。
    /// </summary>
    public void Notify(string title, string text) => _backend?.Notify(title, text);

    /// <summary>托盘图标在屏幕上的实际位置；取不到返回 null。</summary>
    public PixelRect? IconRect() => _backend?.IconRect();

    public void Dispose() => _backend?.Dispose();
}
