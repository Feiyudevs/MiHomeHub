// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text.Json;

namespace MiHomeHub.Services;

/// <summary>窗口位置与大小持久化在 %APPDATA%\Feiyu\MiHomeHub\window.json，与 theme.json 同目录。</summary>
public static class WindowPlacementStore
{
    private static readonly string Dir = AppPaths.Dir;
    private static readonly string FilePath = Path.Combine(Dir, "window.json");

    public sealed class Placement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        /// <summary>保存时所在显示器的签名；这块屏被关掉时坐标先留着，等它回来落回原位。</summary>
        public string ScreenSig { get; set; } = "";
    }

    public static Placement? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var p = JsonSerializer.Deserialize<Placement>(File.ReadAllText(FilePath));
            if (p is null) return null;
            // 尺寸离谱的历史布局直接丢弃：macOS 上把这种尺寸喂给平台会一路放大，
            // 最后 backing store 分配失败把进程带走（用户报「点绿键进全屏就闪退」）。
            if (!WindowSizeLimits.IsSane(p.Width, p.Height))
            {
                CrashLog.Write("window-placement", new InvalidOperationException(
                    $"丢弃离谱的窗口布局：{p.Width}x{p.Height}"));
                return null;
            }
            return p;
        }
        catch
        {
            // 文件损坏时回落到默认尺寸
            return null;
        }
    }

    public static void Save(int x, int y, double width, double height, string screenSig)
    {
        if (double.IsNaN(width) || width <= 0 || double.IsNaN(height) || height <= 0) return;
        // 离谱的尺寸不落盘：否则下次启动会被原样喂回平台，重新滚雪球
        if (!WindowSizeLimits.IsSane(width, height)) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Placement
            {
                X = x,
                Y = y,
                Width = width,
                Height = height,
                ScreenSig = screenSig
            }));
        }
        catch
        {
            // 写入失败不影响本次运行
        }
    }
}
