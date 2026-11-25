// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Text;
using MiHomeHub.Models;

namespace MiHomeHub.Services;

/// <summary>
/// 组合键的键名与判定：钩子拿到的是 Win32 虚拟键码，界面/日志需要人类可读的写法。
/// 这里只做映射，不涉及任何窗口操作。
/// </summary>
internal static class HotkeyKeyMap
{
    public static string Describe(HotkeyCombo combo) => Describe(combo.Modifiers, combo.VirtualKey);

    /// <summary>录制进行中显示已按下的修饰键，如「Ctrl + Shift + …」；还没按修饰键时给引导文案。</summary>
    public static string DescribePending(HotkeyModifier modifiers)
    {
        var sb = new StringBuilder();
        if ((modifiers & HotkeyModifier.Ctrl) != 0) Append(sb, "Ctrl");
        if ((modifiers & HotkeyModifier.Alt) != 0) Append(sb, "Alt");
        if ((modifiers & HotkeyModifier.Shift) != 0) Append(sb, "Shift");
        if ((modifiers & HotkeyModifier.Win) != 0) Append(sb, "Win");
        if (sb.Length == 0) return "请按下组合键…（Esc 取消）";
        sb.Append(" + …");
        return sb.ToString();
    }

    public static string Describe(HotkeyModifier modifiers, int virtualKey)
    {
        var sb = new StringBuilder();
        if ((modifiers & HotkeyModifier.Ctrl) != 0) Append(sb, "Ctrl");
        if ((modifiers & HotkeyModifier.Alt) != 0) Append(sb, "Alt");
        if ((modifiers & HotkeyModifier.Shift) != 0) Append(sb, "Shift");
        if ((modifiers & HotkeyModifier.Win) != 0) Append(sb, "Win");
        Append(sb, KeyName(virtualKey));
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string part)
    {
        if (sb.Length > 0) sb.Append(" + ");
        sb.Append(part);
    }

    public static string KeyName(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x30 and <= 0x39) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87) return $"F{virtualKey - 0x6F}";
        if (virtualKey is >= 0x60 and <= 0x69) return $"小键盘{virtualKey - 0x60}";

        return virtualKey switch
        {
            0x08 => "退格",
            0x09 => "Tab",
            0x0D => "回车",
            0x20 => "空格",
            0x21 => "PgUp",
            0x22 => "PgDn",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x2C => "PrintScreen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x6A => "小键盘 *",
            0x6B => "小键盘 +",
            0x6D => "小键盘 -",
            0x6E => "小键盘 .",
            0x6F => "小键盘 /",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"VK{virtualKey:X2}",
        };
    }

    /// <summary>F1–F24 允许单键绑定（不会耽误正常输入）；字母/数字/符号单键不行。</summary>
    public static bool IsFunctionKey(int virtualKey) => virtualKey is >= 0x70 and <= 0x87;

    /// <summary>修饰键本身不参与「主键」判定，捕获时要跳过。</summary>
    public static bool IsModifierKey(int virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public static string ActionLabel(HotkeyActionKind kind) => kind switch
    {
        HotkeyActionKind.ToggleDevice => "设备开关切换",
        HotkeyActionKind.DeviceOn => "打开设备",
        HotkeyActionKind.DeviceOff => "关闭设备",
        HotkeyActionKind.RunScene => "执行场景",
        HotkeyActionKind.ToggleAutomation => "启停自动化",
        HotkeyActionKind.AutomationOn => "开启自动化",
        HotkeyActionKind.AutomationOff => "关闭自动化",
        _ => "未知动作",
    };

    /// <summary>步骤卡片上的短标签。</summary>
    public static string ShortLabel(HotkeyActionKind kind) => kind switch
    {
        HotkeyActionKind.ToggleDevice => "切换",
        HotkeyActionKind.DeviceOn => "打开",
        HotkeyActionKind.DeviceOff => "关闭",
        HotkeyActionKind.RunScene => "执行",
        HotkeyActionKind.ToggleAutomation => "切换",
        HotkeyActionKind.AutomationOn => "开启",
        HotkeyActionKind.AutomationOff => "关闭",
        _ => "执行",
    };

    /// <summary>执行成功后状态条上的动词，比动作名短一些。</summary>
    public static string ActionVerb(HotkeyActionKind kind) => kind switch
    {
        HotkeyActionKind.ToggleDevice => "切换",
        HotkeyActionKind.DeviceOn => "打开",
        HotkeyActionKind.DeviceOff => "关闭",
        HotkeyActionKind.RunScene => "执行场景",
        HotkeyActionKind.ToggleAutomation => "启停",
        HotkeyActionKind.AutomationOn => "开启",
        HotkeyActionKind.AutomationOff => "关闭",
        _ => "执行",
    };

    public static bool IsDeviceAction(HotkeyActionKind kind) => kind
        is HotkeyActionKind.ToggleDevice or HotkeyActionKind.DeviceOn or HotkeyActionKind.DeviceOff;

    public static bool IsAutomationAction(HotkeyActionKind kind) => kind
        is HotkeyActionKind.ToggleAutomation or HotkeyActionKind.AutomationOn or HotkeyActionKind.AutomationOff;
}
