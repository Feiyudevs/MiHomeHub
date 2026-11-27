// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using MiHomeHub.Views;

namespace MiHomeHub.Services;

/// <summary>
/// 窗口尺寸自检（命令行 <c>--self-test</c>）。
///
/// 回归的场景：macOS 上窗口一旦被撑到远超屏幕的大小，AppKit 会把窗口帧裁到平台上限，
/// Avalonia 又把裁回来的物理像素当成逻辑点读回来，尺寸一轮轮放大
/// （实测 1668 → 22248 → 50668 → 61448…），backing store 的分配随之爆炸，
/// 最后一次未处理的 OutOfMemoryException 把进程带走 —— 表现为「点绿键进全屏就闪退」。
///
/// 自检覆盖三件事：
/// 1) 纯逻辑层的夹取；
/// 2) 真实窗口连续「放大 / 还原」若干轮，尺寸不得膨胀、不得超过上限；
/// 3) 人为把窗口撑到离谱大小后，看门狗必须把尺寸拉回上限。
/// 全部通过返回 0，失败返回 1。
/// </summary>
internal static class WindowSelfTest
{
    public static async Task<int> RunAsync()
    {
        var failures = new List<string>();

        // 1) 纯逻辑
        Check(failures, WindowSizeLimits.ClampWidth(219226, 980) <= WindowSizeLimits.MaxExtent,
            "超大宽度应被夹到上限以内");
        Check(failures, WindowSizeLimits.ClampHeight(138724, 620) <= WindowSizeLimits.MaxExtent,
            "超大高度应被夹到上限以内");
        Check(failures, !WindowSizeLimits.IsSane(219226, 138724), "离谱尺寸应被判定为不可用");
        Check(failures, WindowSizeLimits.IsSane(1280, 800), "正常尺寸应判定为可用");

        // 2) 真实窗口：放大 / 还原若干轮，尺寸不得膨胀
        var window = new MainWindow(deferInitialContent: false);
        window.Show();
        await Task.Delay(600);

        var baseline = new Size(window.Width, window.Height);
        Console.WriteLine($"[self-test] baseline {baseline.Width}x{baseline.Height} pos={window.Position}");
        foreach (var s in window.Screens.All)
            Console.WriteLine($"[self-test] screen bounds={s.Bounds} work={s.WorkingArea} scaling={s.Scaling} primary={s.IsPrimary}");

        for (var i = 0; i < 3; i++)
        {
            window.ToggleMaximize();
            await Task.Delay(400);
            var max = new Size(window.Width, window.Height);
            Check(failures, max.Width <= WindowSizeLimits.MaxExtent && max.Height <= WindowSizeLimits.MaxExtent,
                $"第 {i + 1} 轮放大后尺寸越界：{max.Width}x{max.Height}");

            window.ToggleMaximize();
            await Task.Delay(400);
            var back = new Size(window.Width, window.Height);
            Check(failures, back.Width <= WindowSizeLimits.MaxExtent && back.Height <= WindowSizeLimits.MaxExtent,
                $"第 {i + 1} 轮还原后尺寸越界：{back.Width}x{back.Height}");
            Check(failures, Math.Abs(back.Width - baseline.Width) <= 2 && Math.Abs(back.Height - baseline.Height) <= 2,
                $"第 {i + 1} 轮还原后尺寸漂了：{back.Width}x{back.Height}（基准 {baseline.Width}x{baseline.Height}）");
            Console.WriteLine($"[self-test] cycle {i + 1}: max={max.Width}x{max.Height} back={back.Width}x{back.Height}");
        }

        // 3) 人为撑到离谱大小，看门狗必须拉回来
        window.Width = 219226;
        window.Height = 138724;
        await Task.Delay(600);
        Check(failures, window.Width <= WindowSizeLimits.MaxExtent && window.Height <= WindowSizeLimits.MaxExtent,
            $"撑爆后尺寸应被拉回上限内，实际 {window.Width}x{window.Height}");
        Console.WriteLine($"[self-test] clamped after abuse: {window.Width}x{window.Height}");

        window.Close();

        if (failures.Count == 0)
        {
            Console.WriteLine("[self-test] PASS");
            return 0;
        }

        foreach (var f in failures) Console.WriteLine("[self-test] FAIL: " + f);
        Console.WriteLine($"[self-test] FAILED ({failures.Count})");
        return 1;
    }

    private static void Check(List<string> failures, bool ok, string message)
    {
        if (!ok) failures.Add(message);
    }
}
