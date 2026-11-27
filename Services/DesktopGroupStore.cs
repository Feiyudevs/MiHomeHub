// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MiHomeHub.Models;

namespace MiHomeHub.Services;

/// <summary>桌面组件布局持久化：%APPDATA%\Feiyu\MiHomeHub\desktop.json（与 theme.json / app.json 同目录）。</summary>
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
            // 毛玻璃开关已从界面移除：卡片恒为磨砂玻璃，旧配置里的 false 一律归位，
            // 免得升级后卡面停在「实心底」却再也找不到地方打开。
            layout.Appearance.Glass = true;
            MigrateDensity(layout);
            CleanZoneContents(layout);
            NormalizeDataCards(layout);
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

    /// <summary>
    /// 媒体 / 空调 / 监控分区是专用卡面（音响播放 / 空调控制 / 实时画面），本来就不吃 Dids / SceneIds。
    /// 老版本曾把设备写进这些分区的清单里（选择分区的弹窗因此把它们列成「N 台设备」），
    /// 读到就顺手清干净，改完直接落盘。
    /// </summary>
    private static void CleanZoneContents(DesktopLayout layout)
    {
        var cleaned = false;
        foreach (var g in layout.Groups)
        {
            // 数据卡也没有 Dids/SceneIds，不能按媒体/空调那套清（清内容是清空，不是删除）
            if (g.IsData || (!g.IsMedia && !g.IsAc && !g.IsCamera)) continue;
            if (g.Dids.Count == 0 && g.SceneIds.Count == 0) continue;
            g.Dids.Clear();
            g.SceneIds.Clear();
            cleaned = true;
        }

        if (cleaned) Save(layout);
    }

    /// <summary>
    /// 数据卡读档归一：指标 key 必须合法；宽高落回该指标的默认档位与缩放范围；
    /// v1 一个指标只留一张（老配置里如果手工塞了重复项，后来的收回配置，不显示）。
    /// </summary>
    private static void NormalizeDataCards(DesktopLayout layout)
    {
        var changed = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var g in layout.Groups.ToList())
        {
            if (!g.IsData) continue;

            var def = DataCardCatalog.Find(g.Metric);
            if (def is null)
            {
                layout.Groups.Remove(g);
                changed = true;
                continue;
            }

            if (!seen.Add(def.Key))
            {
                // 同 key 的第二张：保留配置（用户可能只是暂时关掉），但默认收起，避免叠在桌面上
                if (g.Shown)
                {
                    g.Shown = false;
                    changed = true;
                }
                continue;
            }

            var minW = 200d;
            var maxW = def.Compact ? 400d : 800d;
            var minH = 100d;
            var maxH = def.Compact ? 200d : 400d;
            var w = double.IsFinite(g.Width) && g.Width > 0
                ? Math.Clamp(g.Width, minW, maxW)
                : def.DefaultWidth;
            var h = double.IsFinite(g.Height) && g.Height > 0
                ? Math.Clamp(g.Height, minH, maxH)
                : def.DefaultHeight;

            if (Math.Abs(g.Width - w) > 0.5)
            {
                g.Width = w;
                changed = true;
            }
            if (Math.Abs(g.Height - h) > 0.5)
            {
                g.Height = h;
                changed = true;
            }
            if (g.Name.Length == 0)
            {
                g.Name = def.Title;
                changed = true;
            }
            if (g.Dids.Count > 0 || g.SceneIds.Count > 0)
            {
                g.Dids.Clear();
                g.SceneIds.Clear();
                changed = true;
            }
        }

        if (changed) Save(layout);
    }

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
