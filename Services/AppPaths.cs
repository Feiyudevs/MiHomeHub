// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;

namespace MiHome.Services;

/// <summary>
/// 应用数据根目录：%APPDATA%\Feiyu\MiHome。
/// 登录态、设备列表缓存、图标缓存、主题与窗口位置都放在这里。
/// </summary>
public static class AppPaths
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Feiyu", "MiHome");
}
