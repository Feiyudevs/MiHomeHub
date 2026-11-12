// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiHome.Services;

/// <summary>窗口行为偏好持久化在 %APPDATA%\Feiyu\MiHome\app.json，与 theme.json 同目录。</summary>
public static class AppSettingsStore
{
    private static readonly string Dir = AppPaths.Dir;
    private static readonly string FilePath = Path.Combine(Dir, "app.json");

    public static bool AutoStart { get; private set; } = true;

    /// <summary>true = 点关闭收进托盘继续运行；false = 直接退出。</summary>
    public static bool CloseToTray { get; private set; } = true;

    /// <summary>false = 启动后直接静默驻留托盘，不弹主界面。</summary>
    public static bool ShowMainWindowOnStartup { get; private set; }

    /// <summary>小爱语音指令默认下发的音箱 did；空串表示还没选过。</summary>
    public static string DefaultSpeakerDid { get; private set; } = "";

    /// <summary>true = 卡片带投影；false = 扁平无投影。</summary>
    public static bool CardShadow { get; private set; } = true;

    /// <summary>双击桌面空白处：显示 / 隐藏所有桌面分区卡片（运行期状态，不写回分区总开关）。</summary>
    public static bool DoubleClickDesktopToggles { get; private set; } = true;

    /// <summary>鼠标移入折叠的桌面分区时自动展开；移出后自动收起。</summary>
    public static bool HoverExpandEnabled { get; private set; } = true;

    static AppSettingsStore()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("autoStart", out var a))
                AutoStart = a.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("closeToTray", out var c))
                CloseToTray = c.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("showMainWindowOnStartup", out var w))
                ShowMainWindowOnStartup = w.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("defaultSpeakerDid", out var s) && s.ValueKind == JsonValueKind.String)
                DefaultSpeakerDid = s.GetString() ?? "";
            if (root.TryGetProperty("cardShadow", out var d))
                CardShadow = d.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("doubleClickDesktopToggles", out var dc))
                DoubleClickDesktopToggles = dc.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("hoverExpand", out var he))
                HoverExpandEnabled = he.ValueKind != JsonValueKind.False;
        }
        catch
        {
            // 偏好文件损坏时静默回落默认值
        }
    }

    public static void SetAutoStart(bool on)
    {
        AutoStart = on;
        Save();
    }

    public static void SetCloseToTray(bool on)
    {
        CloseToTray = on;
        Save();
    }

    public static void SetShowMainWindowOnStartup(bool on)
    {
        ShowMainWindowOnStartup = on;
        Save();
    }

    public static void SetDefaultSpeakerDid(string did)
    {
        DefaultSpeakerDid = did;
        Save();
    }

    public static void SetCardShadow(bool on)
    {
        CardShadow = on;
        Save();
    }

    public static void SetDoubleClickDesktopToggles(bool on)
    {
        DoubleClickDesktopToggles = on;
        Save();
    }

    public static void SetHoverExpandEnabled(bool on)
    {
        HoverExpandEnabled = on;
        Save();
    }

    private static void Save()
    {
        var payload = JsonSerializer.Serialize(new
        {
            autoStart = AutoStart,
            closeToTray = CloseToTray,
            showMainWindowOnStartup = ShowMainWindowOnStartup,
            defaultSpeakerDid = DefaultSpeakerDid,
            cardShadow = CardShadow,
            doubleClickDesktopToggles = DoubleClickDesktopToggles,
            hoverExpand = HoverExpandEnabled
        });
        // 写盘出 UI 线程；进程被强杀时可能丢最后一次偏好，可接受
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, payload);
            }
            catch
            {
                // 写入失败不影响本次运行
            }
        });
    }
}
