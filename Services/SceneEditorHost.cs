// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using MiHome.ViewModels;
using MiHome.Views;

namespace MiHome.Services;

/// <summary>
/// 场景详情独立弹窗的宿主：全局只留一个窗口实例，反复打开复用，关闭后丢掉。
/// 桌面分区的设备弹窗点「智能场景」时走这里，主程序窗口不需要被唤出。
/// </summary>
public static class SceneEditorHost
{
    private static SceneEditorWindow? _window;

    public static void Open(MainViewModel vm)
    {
        if (_window == null)
        {
            _window = new SceneEditorWindow(vm);
            _window.Closed += (_, _) => _window = null;
        }

        _window.ShowCentered();
    }
}
