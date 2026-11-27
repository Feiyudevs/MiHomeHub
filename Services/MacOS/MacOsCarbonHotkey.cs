// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// macOS 全局快捷键（Carbon <c>RegisterEventHotKey</c>）—— <b>不需要「辅助功能」权限</b>。
///
/// <para>
/// 为什么要 Carbon 而不是 CGEventTap：CGEventTap 挂在 HID 层，macOS 规定必须先在
/// 「系统设置 → 隐私与安全性 → 辅助功能」里给这个程序授权。而本项目是自签（ad-hoc）分发，
/// 每次重新编译签名都会变，系统会把新程序当成「另一个应用」→ 授权被重置 → 用户还得再点一次。
/// 桌面双击、全局热键这类功能因此一直不稳定。
/// Carbon 的热键是系统注册的全局快捷键，授权都不需要，稳定性也更好。
/// </para>
///
/// <para>
/// <b>支持同时注册多条</b>：每条绑定拿一个自己的 <c>comboId</c>，按下时按 comboId 分发。
/// 这样「多条快捷键、每条各自绑定不同设备」在 macOS 上和 Windows 版对齐
/// （Windows 是 WH_KEYBOARD_LL 一次装上、匹配 N 条）。
/// </para>
///
/// <para>
/// 代价：Carbon 不能像 CGEventTap 那样监听任意按键去「录制」组合，所以组合要先录好
/// （录制走 CGEventTap，没有辅助功能权限时退回窗口内录制）再注册；
/// 另外 Carbon 热键<b>不会吞掉按键</b>——按下组合键时前台程序照样能收到这组键。
/// </para>
///
/// </summary>
internal static class MacOsCarbonHotkey
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // ===== Carbon 修饰键掩码 =====
    private const uint CarbonCommand = 0x0100;
    private const uint CarbonShift = 0x0200;
    private const uint CarbonOption = 0x0800;
    private const uint CarbonControl = 0x1000;

    // ===== Carbon 事件常量 =====
    private const uint EventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint EventHotKeyPressed = 5;
    private const uint ParamDirectObject = 0x2D2D2D2D; // '----'
    private const uint TypeEventHotKeyId = 0x686B6964; // 'hkid'
    private const uint HotKeySignature = 0x6B484B59; // 'kHKY'

    /// <summary>旧接口（桌面组件快捷键）用的组合号。</summary>
    public const int LegacyComboId = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventHandlerProc(nint handlerCall, nint evt, nint userData);

    [DllImport(Carbon)]
    private static extern nint GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(
        nint target, EventHandlerProc handler, uint numTypes,
        EventTypeSpec[] list, nint userData, out nint handlerRef);

    [DllImport(Carbon)]
    private static extern int RemoveEventHandler(nint handlerRef);

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(
        uint hotKeyCode, uint hotKeyModifiers, EventHotKeyId id,
        nint target, uint options, out nint hotKeyRef);

    [DllImport(Carbon)]
    private static extern int UnregisterEventHotKey(nint hotKeyRef);

    [DllImport(Carbon)]
    private static extern int GetEventParameter(
        nint evt, uint name, uint desiredType, out uint actualType,
        uint size, out uint actualSize, ref EventHotKeyId data);

    // ===== 运行期状态 =====
    private static readonly object Gate = new();
    private static EventHandlerProc? _handlerProc;   // 委托必须被强引用：交给原生后它不会帮我们保活
    private static nint _handlerRef;

    /// <summary>组合号 → 原生热键引用。</summary>
    private static readonly Dictionary<int, nint> HotKeyRefs = new();

    /// <summary>组合号 → 组合字符串（给界面显示用）。</summary>
    private static readonly Dictionary<int, string> Combos = new();

    /// <summary>快捷键被按下（已在 UI 线程上投递）。参数是组合字符串——只有 <see cref="LegacyComboId"/> 会走这里。</summary>
    public static event Action<string>? Pressed;

    /// <summary>快捷键被按下（已在 UI 线程上投递）。参数是组合号，每条注册各有各的号。</summary>
    public static event Action<int>? ComboPressed;

    /// <summary>当前已注册的组合（没有注册时为空串）。</summary>
    public static string CurrentCombo
    {
        get
        {
            lock (Gate)
            {
                return Combos.TryGetValue(LegacyComboId, out var combo) ? combo : "";
            }
        }
    }

    /// <summary>
    /// 装好 Carbon 热键事件处理器（幂等）。返回 false 表示装不上 —— 这时全局热键完全不可用。
    /// 不需要任何权限，只是把一个事件处理器挂到应用事件目标上。
    /// </summary>
    public static bool EnsureHandler()
    {
        lock (Gate)
        {
            if (_handlerRef != nint.Zero) return true;

            try
            {
                _handlerProc = OnHotKey;
                var types = new[] { new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed } };
                var status = InstallEventHandler(GetApplicationEventTarget(), _handlerProc, 1, types, nint.Zero, out _handlerRef);
                if (status != 0 || _handlerRef == nint.Zero)
                {
                    DesktopLog.Write($"[mac] 安装 Carbon 热键事件处理器失败 status={status}");
                    _handlerProc = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                DesktopLog.Write($"[mac] 安装 Carbon 热键事件处理器异常: {ex.GetType().Name}: {ex.Message}");
                _handlerProc = null;
                return false;
            }
        }
    }

    /// <summary>
    /// 注册一条全局快捷键。组合号由调用方分配（同一条绑定重注册时沿用同一个号）。
    /// 返回 false 表示注册失败（组合非法 / 已被别的程序占用）。
    /// </summary>
    public static bool TryRegister(int comboId, uint keyCode, uint modifiers)
    {
        if (!EnsureHandler()) return false;

        lock (Gate)
        {
            try
            {
                // 同一个组合号重复注册时先注销旧的
                if (HotKeyRefs.TryGetValue(comboId, out var oldRef))
                {
                    try { UnregisterEventHotKey(oldRef); } catch { /* 注销失败无所谓 */ }
                    HotKeyRefs.Remove(comboId);
                }

                var id = new EventHotKeyId { Signature = HotKeySignature, Id = (uint)comboId };
                var reg = RegisterEventHotKey(keyCode, modifiers, id, GetApplicationEventTarget(), 0, out var hotKeyRef);
                if (reg != 0 || hotKeyRef == nint.Zero)
                {
                    DesktopLog.Write($"[mac] 注册全局快捷键失败 status={reg} comboId={comboId}");
                    return false;
                }

                HotKeyRefs[comboId] = hotKeyRef;
                Combos[comboId] = DescribeCombo(modifiers, keyCode);
                DesktopLog.Write($"[mac] 已注册全局快捷键 comboId={comboId} combo={Combos[comboId]}（Carbon，免辅助功能权限）");
                return true;
            }
            catch (Exception ex)
            {
                DesktopLog.Write($"[mac] 注册全局快捷键异常: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 按组合字符串注册（旧接口，桌面组件快捷键在用）：会先注销这个组合号上的旧注册。
    /// 组合写法：<c>Alt+Cmd+D</c>、<c>Ctrl+Shift+F12</c>、<c>Cmd+1</c>……
    /// </summary>
    public static bool TryRegister(string combo)
    {
        Unregister(LegacyComboId);

        if (string.IsNullOrWhiteSpace(combo)) return true;
        if (!TryParse(combo, out var keyCode, out var modifiers))
        {
            DesktopLog.Write($"[mac] 全局快捷键组合无法识别: {combo}");
            return false;
        }

        return TryRegister(LegacyComboId, keyCode, modifiers);
    }

    /// <summary>注销某个组合号上的快捷键。</summary>
    public static void Unregister(int comboId)
    {
        lock (Gate)
        {
            if (HotKeyRefs.TryGetValue(comboId, out var hotKeyRef))
            {
                try { UnregisterEventHotKey(hotKeyRef); } catch { /* 注销失败无所谓：进程退出时系统自己会收 */ }
                HotKeyRefs.Remove(comboId);
            }

            Combos.Remove(comboId);
        }
    }

    /// <summary>注销当前注册的快捷键（全部）。</summary>
    public static void Unregister()
    {
        lock (Gate)
        {
            foreach (var hotKeyRef in HotKeyRefs.Values)
            {
                try { UnregisterEventHotKey(hotKeyRef); } catch { /* 同上 */ }
            }

            HotKeyRefs.Clear();
            Combos.Clear();

            if (_handlerRef != nint.Zero)
            {
                try { RemoveEventHandler(_handlerRef); } catch { /* 同上 */ }
                _handlerRef = nint.Zero;
            }

            _handlerProc = null;
        }
    }

    private static int OnHotKey(nint handlerCall, nint evt, nint userData)
    {
        try
        {
            var id = default(EventHotKeyId);
            uint actualType = 0, actualSize = 0;
            var status = GetEventParameter(
                evt, ParamDirectObject, TypeEventHotKeyId, out actualType,
                (uint)Marshal.SizeOf<EventHotKeyId>(), out actualSize, ref id);
            if (status != 0) return 0;

            var comboId = (int)id.Id;
            string combo;
            lock (Gate)
            {
                if (!Combos.TryGetValue(comboId, out combo!)) return 0;
            }

            // Carbon 回调跑在主线程的事件循环上，界面动作一律 Post 回 UI 线程再做。
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // 旧接口只认「桌面组件快捷键」那个组合号，别让别的快捷键误触发它。
                    if (comboId == LegacyComboId) Pressed?.Invoke(combo);
                    ComboPressed?.Invoke(comboId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[mac] 热键回调异常: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] 热键回调异常: {ex.Message}");
        }

        return 0;
    }

    // ===== 组合解析 =====

    /// <summary>把 "Alt+Cmd+D" 这样的组合解析成 (Carbon 键码, Carbon 修饰键掩码)。</summary>
    public static bool TryParse(string combo, out uint keyCode, out uint modifiers)
    {
        keyCode = 0;
        modifiers = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keyFound = false;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "cmd":
                case "command":
                case "meta":
                case "win":
                    modifiers |= CarbonCommand;
                    continue;
                case "ctrl":
                case "control":
                    modifiers |= CarbonControl;
                    continue;
                case "alt":
                case "option":
                case "opt":
                    modifiers |= CarbonOption;
                    continue;
                case "shift":
                    modifiers |= CarbonShift;
                    continue;
            }

            if (!KeyCodes.TryGetValue(part.ToUpperInvariant(), out keyCode)) return false;
            keyFound = true;
        }

        // 光按修饰键没有意义
        return keyFound && modifiers != 0;
    }

    /// <summary>把 (修饰键掩码, 键码) 拼成给人看的组合串。</summary>
    private static string DescribeCombo(uint modifiers, uint keyCode)
    {
        var parts = new List<string>();
        if ((modifiers & CarbonControl) != 0) parts.Add("Ctrl");
        if ((modifiers & CarbonOption) != 0) parts.Add("Alt");
        if ((modifiers & CarbonShift) != 0) parts.Add("Shift");
        if ((modifiers & CarbonCommand) != 0) parts.Add("Cmd");
        parts.Add(keyCode.ToString("X2"));
        return string.Join("+", parts);
    }

    /// <summary>
    /// Carbon 虚拟键码（kVK_*）表：只覆盖常用键，够设置页的预设组合用。
    /// </summary>
    private static readonly Dictionary<string, uint> KeyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 0x00, ["S"] = 0x01, ["D"] = 0x02, ["F"] = 0x03, ["H"] = 0x04, ["G"] = 0x05,
        ["Z"] = 0x06, ["X"] = 0x07, ["C"] = 0x08, ["V"] = 0x09, ["B"] = 0x0B, ["Q"] = 0x0C,
        ["W"] = 0x0D, ["E"] = 0x0E, ["R"] = 0x0F, ["Y"] = 0x10, ["T"] = 0x11,
        ["1"] = 0x12, ["2"] = 0x13, ["3"] = 0x14, ["4"] = 0x15, ["6"] = 0x16, ["5"] = 0x17,
        ["9"] = 0x19, ["7"] = 0x1A, ["8"] = 0x1C, ["0"] = 0x1D,
        ["O"] = 0x1F, ["U"] = 0x20, ["I"] = 0x22, ["P"] = 0x23, ["L"] = 0x25, ["J"] = 0x26,
        ["K"] = 0x28, ["N"] = 0x2D, ["M"] = 0x2E,
        ["RETURN"] = 0x24, ["ENTER"] = 0x24, ["TAB"] = 0x30, ["SPACE"] = 0x31, ["ESC"] = 0x35, ["ESCAPE"] = 0x35,
        ["LEFT"] = 0x7B, ["RIGHT"] = 0x7C, ["DOWN"] = 0x7D, ["UP"] = 0x7E,
        ["F1"] = 0x7A, ["F2"] = 0x78, ["F3"] = 0x63, ["F4"] = 0x76, ["F5"] = 0x60, ["F6"] = 0x61,
        ["F7"] = 0x62, ["F8"] = 0x64, ["F9"] = 0x65, ["F10"] = 0x6D, ["F11"] = 0x67, ["F12"] = 0x6F
    };
}
