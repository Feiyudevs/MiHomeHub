// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>桌面组件布局持久化：%APPDATA%\Feiyu\MiHome\desktop.json（与 theme.json / app.json 同目录）。</summary>
public static class DesktopGroupStore
{
    private static readonly string Dir = AppPaths.Dir;
    private static readonly string FilePath = Path.Combine(Dir, "desktop.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static DesktopLayout Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new DesktopLayout();
            var text = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(text)) return new DesktopLayout();
            var layout = JsonSerializer.Deserialize<DesktopLayout>(text, Options) ?? new DesktopLayout();
            MigrateDensity(layout);
            return layout;
        }
        catch
        {
            // 布局文件损坏时静默回落空布局，不阻塞启动
            return new DesktopLayout();
        }
    }

    /// <summary>
    /// 浓度语义迁移。只在 <see cref="DesktopLayout.AppearanceVersion"/> 落后时跑一次，
    /// 否则用户手动拉的浓度每次启动都会被改写。
    /// <para>
    /// v0 → v1：滑杆 38%~82%、默认 46% → 20%~72%、默认 34%（等效遮罩从约 0.6 降到 0.33，
    /// 壁纸明暗才透得出来）。
    /// v1 → v2：下限 20% → 0%（文字已有灰度抗锯齿兜底，允许纯磨砂无底色）；
    /// 贴着旧下限的值直接放到新下限，其余不动。
    /// </para>
    /// </summary>
    private static void MigrateDensity(DesktopLayout layout)
    {
        var look = layout.Appearance;

        // v0：Opacity 是乘在玻璃系数上的百分比（默认 10% → 实际 alpha 4.6%），
        // 那种值没有参考意义，一律抬到默认档。
        if (layout.AppearanceVersion < 1)
        {
            if (look.Opacity < 0.30)
            {
                look.Opacity = DesktopPalette.DefaultDensity;
                layout.AppearanceVersion = CurrentAppearanceVersion;
                return;
            }

            // v0 后期档位（贴着 38% / 46%）跟着新默认走。
            if (Math.Abs(look.Opacity - 0.38) < 0.005 || Math.Abs(look.Opacity - 0.46) < 0.005)
                look.Opacity = DesktopPalette.DefaultDensity;
        }

        if (layout.AppearanceVersion >= CurrentAppearanceVersion) return;

        // v1 → v2：贴着旧下限（20%）说明用户拉到最低还想更透，直接放到新下限 0。
        if (layout.AppearanceVersion < 2 && Math.Abs(look.Opacity - 0.20) < 0.005)
            look.Opacity = DesktopPalette.MinDensity;

        layout.AppearanceVersion = CurrentAppearanceVersion;
    }

    /// <summary>当前外观迁移版本，语义变档时 +1（见 <see cref="MigrateDensity"/>）。</summary>
    private const int CurrentAppearanceVersion = 2;

    public static void Save(DesktopLayout layout)
    {
        try
        {
            var payload = JsonSerializer.Serialize(layout, Options);
            // 写盘出 UI 线程：拖动只改内存，松手才落盘
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllText(FilePath, payload);
                }
                catch
                {
                    // 写失败不影响本次运行
                }
            });
        }
        catch
        {
            // 序列化失败忽略
        }
    }
}
