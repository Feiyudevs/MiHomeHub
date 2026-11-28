// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// macOS 托盘图标：状态栏图标（NSStatusItem）+ 原生菜单（NSMenu）。
///
/// Avalonia 11.3 的 TrayIcon 在 macOS 上不抛 Clicked——原生层的 TrayIconImpl 只实现了
/// SetIcon / SetToolTipText / SetIsVisible / SetMenu / SetIsTemplateIcon，从来不设置 OnClicked
/// （源码注释原文："this is only supported on Win32 and some Linux DEs, on OSX this event is not raised"），
/// 所以左键点击没有回调可用。
///
/// 但原生后端会把 TrayIcon.Menu 挂成 NSStatusItem 的菜单：点状态栏图标直接弹出该菜单，
/// 菜单项点击会正常回调到托管的 NativeMenuItem.Click。
/// 所以 macOS 上的交互统一走原生菜单：显示主界面 / 显示-隐藏桌面组件 / 切换主题 / 设置 / 重新启动 / 退出。
///
/// 图标用 PNG（logo-256.png）而不是 .ico：macOS 的 Skia 解 ICO 不稳。
/// 托盘图标必须在 Avalonia 应用初始化之后创建；建不出来时上层（TrayIconHost）静默降级。
/// </summary>
internal sealed class MacOsTrayIcon : ITrayIconBackend, ITrayMenuCommands
{
    /// <summary>状态栏图标用 PNG；.ico 在 macOS 上解不稳。</summary>
    private const string IconUri = "avares://MiHomeHub/Assets/logo-256.png";

    private readonly TrayIcon _tray;
    private bool _disposed;

    /// <summary>macOS 上左键直接弹原生菜单，不回调；保留事件只为接口一致。</summary>
    public event Action? LeftClicked { add { } remove { } }

    /// <summary>macOS 上左键直接弹原生菜单，不回调；保留事件只为接口一致。</summary>
    public event Action? RightClicked { add { } remove { } }

    /// <summary>macOS 没有托盘气泡，不回调；保留事件只为接口一致。</summary>
    public event Action? BalloonClicked { add { } remove { } }

    /// <summary>菜单「显示主界面」。</summary>
    public event Action? ShowMainRequested;

    /// <summary>菜单「显示-隐藏桌面组件」。</summary>
    public event Action? ToggleDesktopWidgetsRequested;

    /// <summary>菜单「切换主题」。</summary>
    public event Action? ToggleThemeRequested;

    /// <summary>菜单「设置」。</summary>
    public event Action? OpenSettingsRequested;

    /// <summary>菜单「重新启动」。</summary>
    public event Action? RestartRequested;

    /// <summary>菜单「退出」。</summary>
    public event Action? ExitRequested;

    public MacOsTrayIcon(string tooltip)
    {
        _tray = new TrayIcon();

        // 图标：PNG 字节流，原生层用 NSImage initWithData: 读（WindowIcon.Save 会原样吐回 PNG）
        using (var stream = AssetLoader.Open(new Uri(IconUri)))
        {
            _tray.Icon = new WindowIcon(stream);
        }

        _tray.ToolTipText = tooltip;

        // 菜单要先挂好再显示：状态栏图标一出现就该能点开菜单
        _tray.Menu = BuildMenu();
        _tray.IsVisible = true;

        DesktopLog.Write("macOS tray icon created (NSStatusItem + native menu)");
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var showMain = new NativeMenuItem("显示主界面");
        showMain.Click += (_, _) => ShowMainRequested?.Invoke();
        menu.Add(showMain);

        var toggleWidgets = new NativeMenuItem("显示-隐藏桌面组件");
        toggleWidgets.Click += (_, _) => ToggleDesktopWidgetsRequested?.Invoke();
        menu.Add(toggleWidgets);

        var toggleTheme = new NativeMenuItem("切换主题");
        toggleTheme.Click += (_, _) => ToggleThemeRequested?.Invoke();
        menu.Add(toggleTheme);

        // 设置：spec 的托盘菜单要求有「设置」入口，Windows 版自绘菜单同样补上，两端保持一致。
        var openSettings = new NativeMenuItem("设置");
        openSettings.Click += (_, _) => OpenSettingsRequested?.Invoke();
        menu.Add(openSettings);

        menu.Add(new NativeMenuItemSeparator());

        var restart = new NativeMenuItem("重新启动");
        restart.Click += (_, _) => RestartRequested?.Invoke();
        menu.Add(restart);

        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Add(exit);

        return menu;
    }

    /// <summary>
    /// macOS 没有 Shell_NotifyIcon 的气泡，这里用 osascript 发一条通知中心横幅；
    /// 发不出去（未授权、脚本受限）就只记一行日志，不影响主流程。
    /// </summary>
    public void Notify(string title, string text)
    {
        try
        {
            var script = $"display notification \"{Escape(text)}\" with title \"{Escape(title)}\"";
            var startInfo = new ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(script);

            using var process = Process.Start(startInfo);
            process?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"macOS notify failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // AppleScript 字符串里不能有裸换行，否则整条脚本语法错误
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", " ")
            .Replace("\n", " ");
    }

    /// <summary>macOS 没有公开 API 拿状态栏图标的位置；菜单由系统弹出，App 也不需要锚点。</summary>
    public PixelRect? IconRect() => null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _tray.IsVisible = false;
            _tray.Dispose();
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"macOS tray dispose failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
