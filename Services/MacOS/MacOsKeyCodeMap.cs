// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using MiHomeHub.Models;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// 键位对照表：<b>Win32 虚拟键码 ↔ Carbon 虚拟键码（kVK_*）</b>。
///
/// <para>
/// 绑定数据（<see cref="HotkeyBinding"/>）里存的是 Win32 虚拟键码 —— 这套数据是
/// Windows / macOS 共用的同一个 <c>hotkeys.json</c>。而 macOS 侧要注册全局热键
/// （Carbon <c>RegisterEventHotKey</c>）得用 Carbon 的 kVK_* 键码，
/// CGEventTap 那边给的又是同一套 kVK_*，所以两张表必须能互相换算。
/// </para>
///
/// <para>
/// 修饰键的对应关系：Command↔Win、Option↔Alt、Control↔Ctrl、Shift↔Shift。
/// </para>
/// </summary>
internal static class MacOsKeyCodeMap
{
    // ===== Carbon 修饰键掩码 =====
    private const uint CarbonCommand = 0x0100;
    private const uint CarbonShift = 0x0200;
    private const uint CarbonOption = 0x0800;
    private const uint CarbonControl = 0x1000;

    /// <summary>(Win32 虚拟键码, Carbon kVK 键码) 对照表。</summary>
    private static readonly (int Win32, uint Carbon)[] Pairs =
    {
        // 字母
        (0x41, 0x00), // A
        (0x53, 0x01), // S
        (0x44, 0x02), // D
        (0x46, 0x03), // F
        (0x48, 0x04), // H
        (0x47, 0x05), // G
        (0x5A, 0x06), // Z
        (0x58, 0x07), // X
        (0x43, 0x08), // C
        (0x56, 0x09), // V
        (0x42, 0x0B), // B
        (0x51, 0x0C), // Q
        (0x57, 0x0D), // W
        (0x45, 0x0E), // E
        (0x52, 0x0F), // R
        (0x59, 0x10), // Y
        (0x54, 0x11), // T
        (0x4F, 0x1F), // O
        (0x55, 0x20), // U
        (0x49, 0x22), // I
        (0x50, 0x23), // P
        (0x4C, 0x25), // L
        (0x4A, 0x26), // J
        (0x4B, 0x28), // K
        (0x4E, 0x2D), // N
        (0x4D, 0x2E), // M
        // 数字
        (0x31, 0x12), // 1
        (0x32, 0x13), // 2
        (0x33, 0x14), // 3
        (0x34, 0x15), // 4
        (0x36, 0x16), // 6
        (0x35, 0x17), // 5
        (0x39, 0x19), // 9
        (0x37, 0x1A), // 7
        (0x38, 0x1C), // 8
        (0x30, 0x1D), // 0
        // 符号
        (0xBB, 0x18), // =
        (0xBD, 0x1B), // -
        (0xDD, 0x1E), // ]
        (0xDB, 0x21), // [
        (0xDE, 0x27), // '
        (0xBA, 0x29), // ;
        (0xDC, 0x2A), // \
        (0xBC, 0x2B), // ,
        (0xBF, 0x2C), // /
        (0xBE, 0x2F), // .
        (0xC0, 0x32), // `
        // 控制键
        (0x0D, 0x24), // Return
        (0x09, 0x30), // Tab
        (0x20, 0x31), // Space
        (0x08, 0x33), // Delete（退格）
        (0x1B, 0x35), // Escape
        (0x14, 0x39), // CapsLock
        (0x2D, 0x72), // Help（Insert）
        (0x24, 0x73), // Home
        (0x21, 0x74), // PageUp
        (0x2E, 0x75), // Forward Delete
        (0x23, 0x77), // End
        (0x22, 0x79), // PageDown
        // 方向键
        (0x25, 0x7B), // ←
        (0x27, 0x7C), // →
        (0x28, 0x7D), // ↓
        (0x26, 0x7E), // ↑
        // 功能键
        (0x70, 0x7A), // F1
        (0x71, 0x78), // F2
        (0x72, 0x63), // F3
        (0x73, 0x76), // F4
        (0x74, 0x60), // F5
        (0x75, 0x61), // F6
        (0x76, 0x62), // F7
        (0x77, 0x64), // F8
        (0x78, 0x65), // F9
        (0x79, 0x6D), // F10
        (0x7A, 0x67), // F11
        (0x7B, 0x6F), // F12
        (0x7C, 0x69), // F13
        (0x7D, 0x6B), // F14
        (0x7E, 0x71), // F15
        (0x7F, 0x6A), // F16
        (0x80, 0x40), // F17
        (0x81, 0x4F), // F18
        (0x82, 0x50), // F19
        (0x83, 0x5A), // F20
        // 修饰键（作为主键时也能对上 Win32 的左右修饰键码）
        (0x5C, 0x36), // 右 Command
        (0x5B, 0x37), // 左 Command
        (0x10, 0x38), // 左 Shift
        (0xA1, 0x3C), // 右 Shift
        (0x12, 0x3A), // 左 Option
        (0xA5, 0x3D), // 右 Option
        (0x11, 0x3B), // 左 Control
        (0xA3, 0x3E), // 右 Control
        // 小键盘
        (0x60, 0x52), (0x61, 0x53), (0x62, 0x54), (0x63, 0x55), (0x64, 0x56),
        (0x65, 0x57), (0x66, 0x58), (0x67, 0x59), (0x68, 0x5B), (0x69, 0x5C),
        (0x6E, 0x41), // 小键盘 .
        (0x6A, 0x43), // 小键盘 *
        (0x6B, 0x45), // 小键盘 +
        (0x6F, 0x4B), // 小键盘 /
        (0x6D, 0x4E), // 小键盘 -
    };

    private static readonly Dictionary<int, uint> CarbonByWin32 = new();
    private static readonly Dictionary<uint, int> Win32ByCarbon = new();

    static MacOsKeyCodeMap()
    {
        foreach (var (win32, carbon) in Pairs)
        {
            // 同一个 Win32 键码可能对应多个 Carbon 键码（小键盘 Enter 与 Return 同号）：
            // 反查时保留第一个，正查时以最后一个为准 —— 两边都是常用键，撞车概率极低。
            if (!CarbonByWin32.ContainsKey(win32)) CarbonByWin32[win32] = carbon;
            Win32ByCarbon[carbon] = win32;
        }
    }

    /// <summary>Win32 虚拟键码 → Carbon 键码。</summary>
    public static bool TryCarbonKeyCode(int win32VirtualKey, out uint carbonKeyCode)
        => CarbonByWin32.TryGetValue(win32VirtualKey, out carbonKeyCode);

    /// <summary>Carbon 键码 → Win32 虚拟键码。</summary>
    public static bool TryWin32VirtualKey(uint carbonKeyCode, out int win32VirtualKey)
        => Win32ByCarbon.TryGetValue(carbonKeyCode, out win32VirtualKey);

    /// <summary>
    /// 修饰键 → Carbon 修饰键掩码：Command↔Win、Option↔Alt、Control↔Ctrl、Shift↔Shift。
    /// 返回 0 表示一个修饰键都没有 —— Carbon 不接受「光按一个主键」的热键。
    /// </summary>
    public static uint CarbonModifiers(HotkeyModifier modifiers)
    {
        uint mask = 0;
        if ((modifiers & HotkeyModifier.Win) != 0) mask |= CarbonCommand;
        if ((modifiers & HotkeyModifier.Shift) != 0) mask |= CarbonShift;
        if ((modifiers & HotkeyModifier.Alt) != 0) mask |= CarbonOption;
        if ((modifiers & HotkeyModifier.Ctrl) != 0) mask |= CarbonControl;
        return mask;
    }
}
