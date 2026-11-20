// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>
/// 全局快捷键：低层键盘钩子（WH_KEYBOARD_LL）常驻 UI 线程，命中绑定后把动作 Post 回 UI 线程执行。
/// 钩子回调里只做「取键 + 比对修饰键」这一件事，绝不发网络请求、绝不碰控件。
/// 命中时返回 1 把按键吞掉：否则组合键还会落进前台程序（比如 Ctrl+Alt+L 会继续触发表情/输入法）。
/// </summary>
internal static class GlobalHotkeyService
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_ESCAPE = 0x1B;

    /// <summary>长按重复的判定窗口：同一键在这段时间内再次 KeyDown 视为系统自动重复，不触发。</summary>
    private const long RepeatWindowMs = 1200;

    /// <summary>录制超时：误开录制后不至于一直吞掉全系统按键。</summary>
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);

    private static nint _hook;
    private static LowLevelKeyboardProc? _proc;
    private static List<HotkeyBinding> _bindings = new();
    private static bool _enabled = true;
    private static bool _capturing;

    /// <summary>
    /// 录制期间自己记的修饰键状态。不能靠 GetAsyncKeyState：录制时修饰键的按下也被钩子吞掉了，
    /// 系统那边的异步键状态经常读不到，Ctrl+K 会被误判成单键 K。
    /// </summary>
    private static HotkeyModifier _captureModifiers = HotkeyModifier.None;

    private static IDisposable? _captureTimeout;
    private static readonly Dictionary<int, long> DownAt = new();

    /// <summary>已吞掉按下事件的主键：对应的抬键也要吞，避免前台程序只收到半次按键。</summary>
    private static readonly HashSet<int> Swallowed = new();

    private static readonly object Gate = new();

    /// <summary>绑定命中（已在 UI 线程上投递）。</summary>
    public static event Action<HotkeyBinding>? Triggered;

    /// <summary>录制完成（已在 UI 线程上投递）。</summary>
    public static event Action<HotkeyCombo>? Captured;

    /// <summary>录制过程中修饰键变化（已在 UI 线程上投递）：界面据此「按一个显示一个」。</summary>
    public static event Action<HotkeyModifier>? CaptureProgress;

    /// <summary>录制被取消（Esc / 超时）。</summary>
    public static event Action? CaptureCancelled;

    /// <summary>钩子是否装上：装不上时快捷键整体不可用，界面据此给提示。</summary>
    public static bool Available => _hook != nint.Zero;

    public static bool Capturing => _capturing;

    /// <summary>启动时装载绑定并安装钩子（幂等，重复调用只更新绑定）。</summary>
    public static void Initialize(bool enabled, IEnumerable<HotkeyBinding> bindings)
    {
        SetBindings(enabled, bindings);
        if (_hook != nint.Zero) return;
        try
        {
            _proc = OnKeyboard;
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, nint.Zero, 0);
        }
        catch
        {
            _hook = nint.Zero;
        }

        DesktopLog.Write($"hotkey hook install → {(_hook == nint.Zero ? "failed" : "ok")}");
    }

    public static void SetBindings(bool enabled, IEnumerable<HotkeyBinding> bindings)
    {
        lock (Gate)
        {
            _enabled = enabled;
            _bindings = bindings.ToList();
        }
    }

    public static void SetEnabled(bool enabled)
    {
        lock (Gate) _enabled = enabled;
    }

    /// <summary>开始录制：接下来按下的第一个非修饰键就是目标组合键，期间所有按键都不传给系统。</summary>
    public static void BeginCapture()
    {
        lock (Gate)
        {
            _capturing = true;
            // 录制开始前就按住的修饰键（比如先按住 Ctrl 再点录制）也要算进去
            _captureModifiers = CurrentModifiers();
            DownAt.Clear();
            Swallowed.Clear();
        }

        DesktopLog.Write($"hotkey capture start (mods={_captureModifiers})");
        PostCaptureProgress();

        _captureTimeout?.Dispose();
        _captureTimeout = DispatcherTimer.RunOnce(() =>
        {
            if (!_capturing) return;
            _capturing = false;
            Post(() => CaptureCancelled?.Invoke());
        }, CaptureTimeout);
    }

    public static void EndCapture()
    {
        lock (Gate) _capturing = false;
        _captureTimeout?.Dispose();
        _captureTimeout = null;
    }

    private static nint OnKeyboard(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var vk = (int)data.vkCode;
            var msg = (int)wParam;

            if (_capturing)
            {
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    lock (Gate) Swallowed.Add(vk);

                    var mod = ModifierOf(vk);
                    if (vk == VK_ESCAPE)
                    {
                        EndCapture();
                        DesktopLog.Write("hotkey capture cancelled (Esc)");
                        Post(() => CaptureCancelled?.Invoke());
                    }
                    else if (mod != HotkeyModifier.None)
                    {
                        // 修饰键：记下状态，继续等主键；界面实时把已按下的键显示出来
                        lock (Gate) _captureModifiers |= mod;
                        PostCaptureProgress();
                    }
                    else
                    {
                        HotkeyModifier mods;
                        lock (Gate) mods = _captureModifiers;
                        var combo = new HotkeyCombo(mods, vk);
                        EndCapture();
                        DesktopLog.Write($"hotkey captured → {HotkeyKeyMap.Describe(combo)} (vk={vk}, mods={mods})");
                        Post(() => Captured?.Invoke(combo));
                    }
                }
                else if (msg is WM_KEYUP or WM_SYSKEYUP)
                {
                    var mod = ModifierOf(vk);
                    if (mod != HotkeyModifier.None)
                    {
                        lock (Gate) _captureModifiers &= ~mod;
                        PostCaptureProgress();
                    }
                    lock (Gate) Swallowed.Remove(vk);
                }

                // 录制期间按键一律吞掉：只吞主键的话，Ctrl / Alt 的按下仍会被系统当前台程序处理
                return 1;
            }

            if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                lock (Gate)
                {
                    DownAt.Remove(vk);
                    // 被吞掉的那次按下，抬键也吞掉，前台程序不会收到「只有抬键」的残影
                    if (Swallowed.Remove(vk)) return 1;
                }
            }
            else if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var repeat = IsRestarted(vk);
                if (TryMatch(vk) is { } binding)
                {
                    // 长按只吞键、不再重复触发：否则按住组合键会把按键漏进前台程序
                    if (!repeat)
                    {
                        lock (Gate) Swallowed.Add(vk);
                        Post(() => Triggered?.Invoke(binding));
                    }

                    // 吞键：组合键不再传给前台程序 / 输入法
                    return 1;
                }
            }
        }
        catch
        {
            // 钩子回调里绝不能抛异常，否则系统会静默卸载钩子
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>同一主键的连续 KeyDown（系统自动重复）返回 true；正常按下会登记时间戳。</summary>
    private static bool IsRestarted(int vk)
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (DownAt.TryGetValue(vk, out var last) && now - last < RepeatWindowMs)
            {
                DownAt[vk] = now;
                return true;
            }

            DownAt[vk] = now;
            return false;
        }
    }

    /// <summary>按当前修饰键状态找一条启用中的绑定；没有就返回 null。</summary>
    private static HotkeyBinding? TryMatch(int vk)
    {
        lock (Gate)
        {
            if (!_enabled) return null;
            var modifiers = CurrentModifiers();
            foreach (var binding in _bindings)
            {
                if (!binding.Enabled) continue;
                if (binding.VirtualKey != vk) continue;
                if ((HotkeyModifier)binding.Modifiers != modifiers) continue;
                return binding;
            }
        }

        return null;
    }

    /// <summary>从系统键盘状态读修饰键；不依赖钩子事件自身的顺序。</summary>
    private static HotkeyModifier CurrentModifiers()
    {
        var m = HotkeyModifier.None;
        if (IsDown(0x11)) m |= HotkeyModifier.Ctrl;   // VK_CONTROL
        if (IsDown(0x12)) m |= HotkeyModifier.Alt;    // VK_MENU
        if (IsDown(0x10)) m |= HotkeyModifier.Shift;  // VK_SHIFT
        if (IsDown(0x5B) || IsDown(0x5C)) m |= HotkeyModifier.Win; // VK_LWIN / VK_RWIN
        return m;
    }

    /// <summary>虚拟键码 → 修饰键位；主键返回 <see cref="HotkeyModifier.None"/>。左右键与通用码都要认。</summary>
    private static HotkeyModifier ModifierOf(int vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => HotkeyModifier.Ctrl,
        0x12 or 0xA4 or 0xA5 => HotkeyModifier.Alt,
        0x10 or 0xA0 or 0xA1 => HotkeyModifier.Shift,
        0x5B or 0x5C => HotkeyModifier.Win,
        _ => HotkeyModifier.None,
    };

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Input);

    private static void PostCaptureProgress()
    {
        HotkeyModifier mods;
        lock (Gate) mods = _captureModifiers;
        Post(() => CaptureProgress?.Invoke(mods));
    }

    // ===== Win32 =====

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
