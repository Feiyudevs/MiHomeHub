// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MiHome.ViewModels;

namespace MiHome.Services;

/// <summary>
/// 弹窗 XAML 自检（一次性诊断）：不登录、不弹窗，把「触发条件 / 添加动作」两个
/// 弹窗宿主和场景编辑面板直接实例化一遍。任何 InitializeComponent 抛的异常都会
/// 被弹窗宿主的事件处理器吞成「点击没反应」，这里让它落盘。
///
/// 用法：MiHome.exe --dialog-probe
/// 输出：%APPDATA%\Feiyu\MiHome\dialog-probe.txt
/// </summary>
internal static class DialogProbe
{
    public static void Run()
    {
        var outPath = Path.Combine(AppPaths.Dir, "dialog-probe.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"dialog-probe  {DateTime.Now:HH:mm:ss}");

        try
        {
            _ = Program.BuildAvaloniaApp().SetupWithoutStarting();
            sb.AppendLine("App setup OK");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"App setup FAIL: {ex.GetType().Name}: {ex.Message}");
            File.WriteAllText(outPath, sb.ToString());
            return;
        }

        // XAML 实例化必须与 App setup 同线程（Avalonia 对象有线程亲和）
        try
        {
            var vm = new MainViewModel();

            var trigger = new Views.TriggerEditorWindow(vm);
            sb.AppendLine("TriggerEditorWindow InitializeComponent OK");
            trigger.Close();

            var action = new Views.ActionEditorWindow(vm);
            sb.AppendLine("ActionEditorWindow InitializeComponent OK");
            action.Close();

            var editor = new Views.SceneEditorView { DataContext = vm };
            sb.AppendLine("SceneEditorView InitializeComponent OK");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
        }

        File.WriteAllText(outPath, sb.ToString());
    }
}
