// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using MiHomeHub.ViewModels;
using MiHomeHub.Views;

namespace MiHomeHub.Services;

/// <summary>
/// 摄像头放大窗的宿主：全局只留一个，关掉就丢掉；和详情抽屉共用同一个视图模型，
/// 所以放大窗里看到的就是抽屉里那一帧，不会重复去连摄像头。
/// </summary>
public static class CameraPlayerHost
{
    private static CameraPlayerWindow? _window;

    public static void Open(MainViewModel vm)
    {
        if (_window == null)
        {
            _window = new CameraPlayerWindow { DataContext = vm };
            _window.Closed += (_, _) => _window = null;
        }

        _window.ShowCentered();
    }

    public static void Close()
    {
        var window = _window;
        _window = null;
        window?.Close();
    }
}
