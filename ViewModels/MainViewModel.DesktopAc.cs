// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Models;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 桌面空调组件：绑定 <see cref="MainViewModel.DesktopAc"/> 这台空调，
/// 电源 / 模式 / 温度 / 风速命令直接按指定设备下发，不依赖详情抽屉当前选中的是哪台。
/// 与抽屉空调命令共用同一套 client 方法与抑制窗口，规格缺失或离线时静默提示。
/// </summary>
public partial class MainViewModel
{
    /// <summary>桌面空调切换：电源开关。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DesktopAcTogglePower()
    {
        var d = DesktopAc;
        if (_client == null || d is not { IsOnline: true })
        {
            Log("空调离线或未选中，指令未下发");
            return;
        }
        if (!d.HasPower)
        {
            Log($"{d.Name} 规格里没有整机开关");
            return;
        }

        var on = !d.IsOn;
        d.IsOn = on;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetPowerAsync(d, on);
            Log($"{d.Name} {(on ? "已开启" : "已关闭")}");
            // 开机后云端模式/温度/风速可能尚未回填，补一拍状态对齐
            MainRequestRefresh();
        }
        catch (Exception ex)
        {
            Log($"开关空调失败：{ex.Message}");
            d.IsOn = !on;
        }
    }

    /// <summary>桌面空调：运行模式（制冷/除湿/送风/制热…），取值来自设备规格 value-list。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DesktopAcSetMode(int mode)
    {
        var d = DesktopAc;
        if (_client == null || d is not { IsOnline: true })
        {
            Log("空调离线或未选中，指令未下发");
            return;
        }
        if (d.AcSchema is not { Valid: true } s)
        {
            Log($"{d.Name} 缺少控制参数，模式未下发");
            return;
        }
        d.AcMode = mode;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetAcModeAsync(d.TargetDid, s.ModeSiid, s.ModePiid, mode);
            Log($"{d.Name} 模式 → {AcOptionLabel(s.Modes, mode, "模式")}");
        }
        catch (Exception ex)
        {
            Log($"设置空调模式失败：{ex.Message}");
        }
    }

    /// <summary>桌面空调：目标温度直接设到指定值（滑杆松手提交）。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DesktopAcSetTemp(double temp)
    {
        var d = DesktopAc;
        if (_client == null || d is not { IsOnline: true }) return;
        if (d.AcSchema is not { Valid: true } s)
        {
            Log($"{d.Name} 缺少控制参数，温度未下发");
            return;
        }
        var next = Math.Round(Math.Clamp(temp, s.TempMin, s.TempMax) / s.TempStep) * s.TempStep;
        if (Math.Abs(next - d.AcTargetTemp) < s.TempStep / 2) return;

        d.AcTargetTemp = next;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetAcTargetTempAsync(d.TargetDid, s.TempSiid, s.TempPiid, next, s);
            Log($"{d.Name} 温度 → {next:0.#}℃");
        }
        catch (Exception ex)
        {
            Log($"设置空调温度失败：{ex.Message}");
        }
    }

    /// <summary>桌面空调：风速档位（自动/一档…），取值来自设备规格 value-list。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DesktopAcSetFan(int level)
    {
        var d = DesktopAc;
        if (_client == null || d is not { IsOnline: true })
        {
            Log("空调离线或未选中，指令未下发");
            return;
        }
        if (d.AcSchema is not { Valid: true } s)
        {
            Log($"{d.Name} 缺少控制参数，风速未下发");
            return;
        }
        d.AcFanLevel = level;
        d.PendingUntilMs = Environment.TickCount64 + SuppressMs;
        try
        {
            await _client.SetAcFanLevelAsync(d.TargetDid, s.FanSiid, s.FanPiid, level);
            Log($"{d.Name} 风速 → {AcOptionLabel(s.Fans, level, "档")}");
        }
        catch (Exception ex)
        {
            Log($"设置空调风速失败：{ex.Message}");
        }
    }

    /// <summary>桌面空调组件首次绑定 / 开机后补一拍状态（模式、温度、风速对齐云端）。</summary>
    internal void MainRequestRefresh() => RequestZoneRefresh(includeMetrics: false);
}
