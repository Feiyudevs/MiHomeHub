// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MiHomeHub.Services.CameraMedia;

/// <summary>
/// 内置解码库的落盘与加载：libde265（LGPL）与它的三个 MinGW 运行时随程序内嵌，
/// 首次用到时解到数据目录，按全路径加载，机器上不需要任何解码组件或外部 exe。
/// </summary>
internal static class CameraNative
{
    private static readonly string[] Files =
    {
        "libde265-0.dll",
        "libgcc_s_seh-1.dll",
        "libstdc++-6.dll",
        "libwinpthread-1.dll",
    };

    private const string Stamp = "libde265-1.0.15";

    private static bool _ready;

    public static string Dir => Path.Combine(AppPaths.Dir, "camera", "native");

    public static bool Ensure()
    {
        if (_ready) return true;
        try
        {
            var dir = Dir;
            Directory.CreateDirectory(dir);
            var stampPath = Path.Combine(dir, "version.txt");
            var stampOk = File.Exists(stampPath) && File.ReadAllText(stampPath).Trim() == Stamp;

            foreach (var name in Files)
            {
                var path = Path.Combine(dir, name);
                if (stampOk && File.Exists(path) && new FileInfo(path).Length > 0) continue;

                var assembly = typeof(CameraNative).Assembly;
                using var stream = assembly.GetManifestResourceStream("MiHomeHub.camera.native." + name);
                if (stream == null) continue; // 资源没打包进来时留给 DllImport 报错
                using var target = File.Create(path);
                stream.CopyTo(target);
            }

            File.WriteAllText(stampPath, Stamp);
            NativeLibrary.SetDllImportResolver(typeof(CameraNative).Assembly, Resolve);
            _ready = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.StartsWith("libde265", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        var path = Path.Combine(Dir, name + ".dll");
        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }
}
