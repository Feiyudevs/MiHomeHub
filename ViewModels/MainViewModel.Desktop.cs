// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Models;
using MiHomeHub.Services;

namespace MiHomeHub.ViewModels;

/// <summary>桌面组件：设备卡加号、分区窗、控制弹窗与主视图模型的对接层。</summary>
public partial class MainViewModel : IDeviceCardHost
{
    /// <summary>当前家庭上屏的设备清单（未按房间/搜索过滤；已并组、并键、隐藏开关），桌面分区按 did 从这里取实例。</summary>
    public IReadOnlyList<MijiaDevice> AllDevices => _visibleDevices;

    // ===== 设备卡宿主契约：主窗口这一侧沿用原有抽屉/开关/加号三套命令 =====

    /// <summary>点卡片 → 右侧详情抽屉。</summary>
    public ICommand OpenCardCommand => OpenDrawerCommand;

    /// <summary>拨开关 → 下发指令（含多路聚合态）。</summary>
    public ICommand ToggleSwitchCommand => ToggleDeviceCommand;

    /// <summary>右键菜单 → 弹窗选择要加入的桌面分区（或新建分区）。</summary>
    public ICommand? CardMenuCommand => AddToDesktopCommand;

    /// <summary>主窗口列表里的右键菜单文案。</summary>
    public string CardMenuText => "添加到桌面组件";

    /// <summary>加号图标：这是「加入」动作。</summary>
    public Geometry CardMenuIcon => CardMenuIcons.Add;

    /// <summary>主列表右键第三项：隐藏 / 恢复这台设备（只影响主列表，控制、场景、统计照旧）。</summary>
    public ICommand? HideCardCommand => HideDeviceCommand;

    /// <summary>
    /// 单独隐藏一台设备：从所在房间宫格里拿掉，折进列表末尾的「已隐藏」区；
    /// 再点一次恢复原位。状态按 did 写回 app.json，重启后仍然生效。
    /// </summary>
    [RelayCommand]
    private void HideDevice(MijiaDevice? device)
    {
        if (device == null || string.IsNullOrEmpty(device.Did)) return;

        var hide = !device.IsHidden;
        if (hide) _hiddenDids.Add(device.Did);
        else _hiddenDids.Remove(device.Did);
        device.IsHidden = hide;
        AppSettingsStore.SetHiddenDids(_hiddenDids);

        ApplyFilter();
        RebuildHomeGroups();
        Log(hide
            ? $"已隐藏设备「{device.Name}」，可在列表末尾的「已隐藏」里找回"
            : $"已取消隐藏「{device.Name}」");
    }

    /// <summary>展开 / 收起列表末尾的「已隐藏」区。</summary>
    [RelayCommand]
    private void ToggleHiddenSection() => HiddenSectionExpanded = !HiddenSectionExpanded;

    /// <summary>供分区视图模型写操作日志用：Log 只在 MainViewModel 内部可见。</summary>
    public void AppendLog(string message) => Log(message);

    /// <summary>设备列表换代（缓存上屏 / 切家庭 / 刷新合并）后通知桌面分区重绑。</summary>
    public event Action? DevicesReloaded;

    /// <summary>桌面分区弹窗是否开着（关掉时宿主窗口自行收起）。</summary>
    [ObservableProperty] private bool _isDevicePanelOpen;

    /// <summary>桌面组件总开关：关掉只收起窗口，分区与外观配置都保留。</summary>
    [ObservableProperty] private bool _desktopWidgetsEnabled = true;

    partial void OnDesktopWidgetsEnabledChanged(bool value)
    {
        DesktopHost.SetEnabled(value);
        Log(value ? "桌面组件已显示" : "桌面组件已收起");
    }

    /// <summary>
    /// 桌面媒体分区开关：与总开关彼此独立。
    /// 关掉 = 收起这块分区并记住「不再自动放回」；打开 = 重新放一块回桌面。
    /// </summary>
    public bool DesktopMediaZone
    {
        get => !DesktopHost.Layout.MediaHidden;
        set
        {
            if (DesktopHost.Layout.MediaHidden == !value) return;
            if (value) DesktopHost.ShowMedia();
            else DesktopHost.HideMedia();
            OnPropertyChanged(nameof(DesktopMediaZone));
            Log(value ? "桌面媒体分区已显示" : "桌面媒体分区已关闭");
        }
    }

    /// <summary>
    /// 桌面空调分区开关：与总开关彼此独立。
    /// 关掉 = 收起这块分区并记住「不再自动放回」；打开 = 重新放一块回桌面。
    /// </summary>
    public bool DesktopAcZone
    {
        get => !DesktopHost.Layout.AcHidden;
        set
        {
            if (DesktopHost.Layout.AcHidden == !value) return;
            if (value) DesktopHost.ShowAc();
            else DesktopHost.HideAc();
            OnPropertyChanged(nameof(DesktopAcZone));
            Log(value ? "桌面空调分区已显示" : "桌面空调分区已关闭");
        }
    }

    /// <summary>分区外观编辑器（背景色 / 浓度 / 毛玻璃），设置页内联复用同一份 VM。</summary>
    public DesktopAppearanceVm DesktopAppearance { get; } = new();

    /// <summary>设置弹窗每次打开都按当前配置刷一次媒体分区开关的显示状态，并回到第一个 Tab。</summary>
    partial void OnIsSettingsOpenChanged(bool value)
    {
        if (value)
        {
            SettingsTab = 0;
            OnPropertyChanged(nameof(DesktopMediaZone));
            OnPropertyChanged(nameof(DesktopAcZone));
            DesktopAppearance.Reload();
        }
    }

    /// <summary>点分区里的设备卡片：复用抽屉那套设备详情（规格面板 + 属性回填），只是换到弹窗里展示。</summary>
    public void ShowDevicePanel(MijiaDevice device)
    {
        DrawerDevice = device;
        IsDevicePanelOpen = true;
        IsSmartSceneExpanded = false;
        BuildSpecPanel(device);
        AttachDrawerDetail(device);
        // 「智能场景」读的是内存索引：桌面弹窗不走 OpenDrawer，
        // 少了这一步会一直显示上一次（或空）的清单，必须点一次主界面抽屉才有内容
        UpdateDrawerScenes();
        // 还没拉过场景（例如只在桌面分区里操作）就先补一次，索引回来后会自行重填清单
        if (!_scenesLoadedOnce && _client != null) _ = LoadScenesAsync();
    }

    [RelayCommand]
    private void CloseDevicePanel() => IsDevicePanelOpen = false;

    /// <summary>
    /// 设备卡右上角的「＋」/ 右键「添加到桌面组件」：弹窗选择要加入的分区，或新建分区。
    /// 真正加入/新建后才由 DesktopHost 打开桌面组件总开关。
    /// </summary>
    [RelayCommand]
    private void AddToDesktop(MijiaDevice? device)
    {
        if (device == null) return;
        DesktopHost.OpenGroupPicker(device);
    }

    /// <summary>设备列表换代后触发一次（由 UpdateStats 统一出口调用）。</summary>
    private void RaiseDevicesReloaded()
    {
        // 快捷键行里的设备名 / 在线态 / 开关态跟着设备列表一起刷新
        RefreshHotkeyViews();
        DevicesReloaded?.Invoke();
    }
}
