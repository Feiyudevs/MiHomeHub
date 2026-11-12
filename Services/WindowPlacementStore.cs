// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text.Json;

namespace MiHome.Services;

/// <summary>窗口位置与大小持久化在 %APPDATA%\Feiyu\MiHome\window.json，与 theme.json 同目录。</summary>
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
            return p is { Width: > 0, Height: > 0 } ? p : null;
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
