// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;
using MiHomeHub.Models;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// macOS 全局快捷键：<b>Carbon RegisterEventHotKey（触发）</b> + CGEventTap（仅录制）。
///
/// Windows 那边是 WH_KEYBOARD_LL 低层钩子，macOS 没有等价物。能「全局触发」的免授权方案只有
/// Carbon 的系统热键注册（<see cref="MacOsCarbonHotkey"/>），它一条一条注册，
/// 支持同时挂多条绑定，和 Windows 版「一条组合对应一组设备」的行为对齐。
///
/// <para>
/// 为什么不用 CGEventTap 触发：它挂在 HID 层，必须在「系统设置 → 隐私与安全性 → 辅助功能」
/// 里给程序授权，而本项目是自签（ad-hoc）分发，每次重编译签名都会变 → 系统当成另一个应用 →
/// 授权被重置 → 用户还得再点一次；授权没给到时 tap 建不出来，绑定就静默失灵，
/// 界面还会一直显示「未监听」。Carbon 不需要任何授权，注册完就能用。
/// </para>
///
/// <para>
/// CGEventTap 仍然留着，但只用来「录制」组合键：录制时用户按的键可能在别的窗口里。
/// 没有辅助功能权限时退回窗口内录制（HotkeyEditorWindow 的 KeyDown 兜底）。
/// </para>
///
/// <para>
/// 键位对齐：绑定里存的是 Win32 虚拟键码（Ctrl/Alt/Shift/Win + 主键），这套数据是跨平台共用的，
/// 注册 Carbon 热键时要换成 Carbon 的 kVK_* 键码（见 <see cref="MacOsKeyCodeMap"/>），
/// 修饰键也是同一套：Command↔Win、Option↔Alt、Control↔Ctrl、Shift↔Shift。
/// </para>
/// </summary>
internal sealed class MacOsHotkeyService : IHotkeyBackend
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    /// <summary>长按重复的判定窗口：同一键在这段时间内再次 KeyDown 视为系统自动重复，不重复触发。</summary>
    private const long RepeatWindowMs = 1200;

    /// <summary>录制超时：误开录制后不至于一直吞掉全系统按键。</summary>
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);

    /// <summary>macOS 侧 modifier 掩码（CGEventFlags）。</summary>
    private const ulong FlagShift = 0x00020000;
    private const ulong FlagControl = 0x00040000;
    private const ulong FlagAlternate = 0x00080000;
    private const ulong FlagCommand = 0x00100000;

    /// <summary>CGEventSourceStateID：读当前系统级修饰键状态。</summary>
    private const int SourceStateHidSystem = 1;

    /// <summary>macOS virtual keycode：Esc。</summary>
    private const int KeyCodeEscape = 0x35;

    /// <summary>绑定命中（已在 UI 线程上投递）。</summary>
    public event Action<HotkeyBinding>? Triggered;

    /// <summary>录制完成（已在 UI 线程上投递）。</summary>
    public event Action<HotkeyCombo>? Captured;

    /// <summary>录制过程中修饰键变化（已在 UI 线程上投递）：界面据此「按一个显示一个」。</summary>
    public event Action<HotkeyModifier>? CaptureProgress;

    /// <summary>录制被取消（Esc / 超时）。</summary>
    public event Action? CaptureCancelled;

    private readonly object Gate = new();
    private readonly TapCallback _tapCallback;
    private List<HotkeyBinding> _bindings = new();
    private bool _enabled = true;
    private bool _capturing;

    /// <summary>录制期间自己记的修饰键状态。</summary>
    private HotkeyModifier _captureModifiers = HotkeyModifier.None;

    private IDisposable? _captureTimeout;

    /// <summary>同一主键最近一次按下的时刻：用来识别长按自动重复。</summary>
    private readonly Dictionary<int, long> _downAt = new();

    /// <summary>已吞掉按下事件的主键：对应的抬键也要吞，避免前台程序只收到半次按键。</summary>
    private readonly HashSet<int> _swallowed = new();

    private nint _tap;

    /// <summary>tap 当前是否在收事件。句柄可以留着复用，但关掉时不该占着监听。</summary>
    private volatile bool _tapEnabled;

    /// <summary>
    /// Carbon 全局热键通道是否就绪（事件处理器装上了）。
    /// 这是 macOS 上「全局快捷键能不能用」的真实判据：不需要任何权限。
    /// </summary>
    private volatile bool _carbonChannelReady;

    /// <summary>某条绑定在 Carbon 上的注册情况。</summary>
    private sealed class CarbonRegistration
    {
        public int ComboId;
        public uint KeyCode;
        public uint Modifiers;
    }

    /// <summary>绑定 Id → Carbon 注册信息。</summary>
    private readonly Dictionary<string, CarbonRegistration> _carbonRegistrations = new();

    /// <summary>Carbon 组合号 → 绑定：按下时据此分发。</summary>
    private readonly Dictionary<int, HotkeyBinding> _carbonBindings = new();

    /// <summary>下一个可用的 Carbon 组合号。</summary>
    private int _nextCarbonId = 1000;

    /// <summary>最近一次注册失败的原因：界面据此提示（macOS 上最常见是组合被别的程序占用）。</summary>
    private string? _lastRegistrationError;

    public MacOsHotkeyService()
    {
        // 委托必须被强引用住：交给原生后它自己不会帮我们保活
        _tapCallback = OnEvent;

        // Carbon 热键按下：按组合号分发到对应的绑定
        MacOsCarbonHotkey.ComboPressed += OnCarbonHotKey;
    }

    /// <summary>
    /// 全局触发通道是否可用：Carbon 通道就绪即可（不需要辅助功能权限）。
    /// 录制用的 tap 也算一条通道（它能吞键，但需要权限）。
    /// </summary>
    public bool Available => _carbonChannelReady || _tapEnabled;

    /// <summary>最近一次注册失败的原因（没有失败时为 null）。</summary>
    public string? LastRegistrationError
    {
        get
        {
            lock (Gate) return _lastRegistrationError;
        }
    }

    public bool Capturing
    {
        get
        {
            lock (Gate) return _capturing;
        }
    }

    public void Initialize(bool enabled, IEnumerable<HotkeyBinding> bindings)
    {
        SetBindings(enabled, bindings);
    }

    /// <summary>
    /// 装好全局热键通道并把当前启用的绑定注册进去。
    /// 通道走 Carbon（免授权），所以这里不需要「有绑定才装」——
    /// 跟 Windows 那边「钩子一装上就一直待命」的行为一致，界面状态才准。
    /// </summary>
    public void InstallHook()
    {
        EnsureCarbonChannel();
        SyncCarbonHotkeys();

        int registered;
        lock (Gate) registered = _carbonRegistrations.Count;
        DesktopLog.Write($"macOS 全局快捷键通道 → {(_carbonChannelReady ? "Carbon ready" : "failed")}，已注册 {registered} 条");
    }

    /// <summary>
    /// 装 Carbon 事件处理器（幂等）。这是 macOS 上全局热键能不能用的开关。
    /// </summary>
    private bool EnsureCarbonChannel()
    {
        var ok = MacOsCarbonHotkey.EnsureHandler();
        _carbonChannelReady = ok;
        return ok;
    }

    /// <summary>
    /// 把当前启用的绑定同步到 Carbon：新增的注册掉、删掉的注销掉、改了组合键的重注册。
    /// 全局快捷键「新增后按了没反应」多半就是这里没跑，所以每次改绑定都必须调它。
    /// </summary>
    private void SyncCarbonHotkeys()
    {
        if (!EnsureCarbonChannel()) return;

        List<HotkeyBinding> wanted;
        lock (Gate)
        {
            wanted = _enabled
                ? _bindings.Where(b => b.Enabled).ToList()
                : new List<HotkeyBinding>();
        }

        string? error = null;

        // 1) 不该在的先注销（绑定被删 / 被停用 / 整体关闭）
        List<string> staleIds;
        lock (Gate)
        {
            staleIds = _carbonRegistrations.Keys
                .Where(id => wanted.All(b => b.Id != id))
                .ToList();
        }

        foreach (var id in staleIds)
        {
            int comboId;
            lock (Gate)
            {
                if (!_carbonRegistrations.TryGetValue(id, out var reg)) continue;
                comboId = reg.ComboId;
                _carbonRegistrations.Remove(id);
                _carbonBindings.Remove(comboId);
            }

            MacOsCarbonHotkey.Unregister(comboId);
        }

        // 2) 缺的补上、改了组合键的重注册
        foreach (var binding in wanted)
        {
            if (!MacOsKeyCodeMap.TryCarbonKeyCode(binding.VirtualKey, out var keyCode))
            {
                error ??= $"快捷键 {HotkeyKeyMap.Describe((HotkeyModifier)binding.Modifiers, binding.VirtualKey)} 的按键在这台 Mac 上没有对应键码，注册失败";
                continue;
            }

            var modifiers = MacOsKeyCodeMap.CarbonModifiers((HotkeyModifier)binding.Modifiers);
            if (modifiers == 0)
            {
                error ??= $"快捷键 {HotkeyKeyMap.Describe((HotkeyModifier)binding.Modifiers, binding.VirtualKey)} 没有修饰键，macOS 不接受这种全局热键";
                continue;
            }

            bool already;
            lock (Gate)
            {
                already = _carbonRegistrations.TryGetValue(binding.Id, out var existing)
                           && existing.KeyCode == keyCode
                           && existing.Modifiers == modifiers;
            }

            if (already) continue;

            // 组合键改过了：先把旧的注销
            int oldComboId = 0;
            lock (Gate)
            {
                if (_carbonRegistrations.TryGetValue(binding.Id, out var old))
                {
                    oldComboId = old.ComboId;
                    _carbonRegistrations.Remove(binding.Id);
                    _carbonBindings.Remove(old.ComboId);
                }
            }

            if (oldComboId != 0) MacOsCarbonHotkey.Unregister(oldComboId);

            int comboId;
            lock (Gate) comboId = _nextCarbonId++;

            if (!MacOsCarbonHotkey.TryRegister(comboId, keyCode, modifiers))
            {
                error ??= $"快捷键 {HotkeyKeyMap.Describe((HotkeyModifier)binding.Modifiers, binding.VirtualKey)} 注册失败：这个组合已被其它程序占用，换一个试试";
                continue;
            }

            lock (Gate)
            {
                _carbonRegistrations[binding.Id] = new CarbonRegistration { ComboId = comboId, KeyCode = keyCode, Modifiers = modifiers };
                _carbonBindings[comboId] = binding;
            }
        }

        lock (Gate) _lastRegistrationError = error;
        if (error is not null) DesktopLog.Write($"macOS 全局快捷键注册失败: {error}");
    }

    /// <summary>Carbon 热键被按下：按组合号找到绑定再投递（投递在 UI 线程上）。</summary>
    private void OnCarbonHotKey(int comboId)
    {
        HotkeyBinding? binding;
        lock (Gate)
        {
            if (!_carbonBindings.TryGetValue(comboId, out binding)) return;
        }

        if (binding is null) return;
        DesktopLog.Write($"macOS 快捷键触发 combo={HotkeyKeyMap.Describe((HotkeyModifier)binding.Modifiers, binding.VirtualKey)}");
        Post(() => Triggered?.Invoke(binding));
    }

    /// <summary>
    /// 为「录制组合键」准备全局监听：录制时用户按的键可能在别的窗口里，
    /// 窗口内兜底只能兜住本窗口，所以这里尽力把 tap 装上（哪怕当前一条绑定都没有）。
    /// 装不上（没有辅助功能权限）时返回 false，界面退回窗口内录制。
    /// </summary>
    public bool EnsureCaptureReady()
    {
        lock (Gate)
        {
            if (_tapEnabled) return true;

            if (!IsProcessTrusted())
            {
                DesktopLog.Write("macOS hotkey tap skip（录制需要辅助功能权限）");
                return false;
            }

            return EnsureTap();
        }
    }

    /// <summary>
    /// 建 tap（必要时）并打开它。必须在 Gate 内调用。
    /// 句柄留着复用：关掉只是 CGEventTapEnable(0)，下次再开不用重建。
    /// </summary>
    private bool EnsureTap()
    {
        if (_tap == nint.Zero)
        {
            try
            {
                var mask = Mask(CgEventType.KeyDown) | Mask(CgEventType.KeyUp) | Mask(CgEventType.FlagsChanged);
                _tap = CGEventTapCreate(
                    CgEventTapLocation.Hid,
                    CgEventTapPlacement.HeadInsert,
                    CgEventTapOptions.Default,
                    mask,
                    _tapCallback,
                    nint.Zero);
            }
            catch (Exception ex)
            {
                DesktopLog.Write($"macOS hotkey tap create failed: {ex.GetType().Name}: {ex.Message}");
                _tap = nint.Zero;
            }

            if (_tap == nint.Zero)
            {
                _tapEnabled = false;
                DesktopLog.Write("macOS hotkey tap unavailable（需要「系统设置 → 隐私与安全性 → 辅助功能」授权）");
                return false;
            }

            var thread = new Thread(RunTap)
            {
                IsBackground = true,
                Name = "MiHomeHub.MacHotkeyTap"
            };
            thread.Start();
        }

        try
        {
            CGEventTapEnable(_tap, 1);
            _tapEnabled = true;
        }
        catch (Exception ex)
        {
            _tapEnabled = false;
            DesktopLog.Write($"macOS hotkey tap enable failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 没有启用中的绑定时把 tap 关掉：run loop 线程留着（复用），但不再收事件、不再占监听。
    /// </summary>
    private void ReleaseTapIfIdle()
    {
        lock (Gate)
        {
            if (!_tapEnabled || _tap == nint.Zero) return;
            if (_enabled && HasEnabledBinding()) return;

            try
            {
                CGEventTapEnable(_tap, 0);
            }
            catch (Exception ex)
            {
                DesktopLog.Write($"macOS hotkey tap disable failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _tapEnabled = false;
            DesktopLog.Write("macOS hotkey tap 已关闭（没有启用中的设备级热键绑定）");
        }
    }

    public void SetBindings(bool enabled, IEnumerable<HotkeyBinding> bindings)
    {
        lock (Gate)
        {
            _enabled = enabled;
            _bindings = new List<HotkeyBinding>(bindings);
        }

        // 新增 / 删除 / 改组合键之后必须重新注册，否则新绑定的快捷键按了没反应。
        SyncCarbonHotkeys();
    }

    public void SetEnabled(bool enabled)
    {
        lock (Gate)
        {
            _enabled = enabled;
        }

        SyncCarbonHotkeys();
    }

    /// <summary>当前有没有真正启用（Enabled）的绑定。</summary>
    private bool HasEnabledBinding()
    {
        foreach (var binding in _bindings)
        {
            if (binding.Enabled) return true;
        }

        return false;
    }

    /// <summary>开始录制：接下来按下的第一个非修饰键就是目标组合键，期间所有按键都不传给系统。</summary>
    public void BeginCapture()
    {
        // 录制要收的是「按下什么键」，此时还没有任何绑定，InstallHook 会跳过；
        // 这里单独把 tap 装上（装不上就退回窗口内录制）。
        var ready = EnsureCaptureReady();

        lock (Gate)
        {
            _capturing = true;
            // 录制开始前就按住的修饰键（比如先按住 Cmd 再点录制）也要算进去
            _captureModifiers = ModifiersOf(CGEventSourceFlagsState(SourceStateHidSystem));
            _downAt.Clear();
            _swallowed.Clear();
        }

        DesktopLog.Write($"hotkey capture start (mods={_captureModifiers}, globalTap={ready})");
        PostCaptureProgress();

        _captureTimeout?.Dispose();
        _captureTimeout = DispatcherTimer.RunOnce(() =>
        {
            lock (Gate)
            {
                if (!_capturing) return;
                _capturing = false;
            }

            Post(() => CaptureCancelled?.Invoke());
        }, CaptureTimeout);
    }

    public void EndCapture()
    {
        lock (Gate) _capturing = false;
        _captureTimeout?.Dispose();
        _captureTimeout = null;

        // 录完就没人用监听器了：没有启用中的绑定时把它关掉（线程留着复用）。
        ReleaseTapIfIdle();
    }

    /// <summary>后台线程：把 tap 的 mach port 挂进自己的 run loop，然后一直跑。</summary>
    private void RunTap()
    {
        try
        {
            var runLoop = CFRunLoopGetCurrent();

            var source = CFMachPortCreateRunLoopSource(nint.Zero, _tap, nint.Zero);
            if (source == nint.Zero)
            {
                DesktopLog.Write("macOS hotkey tap source 创建失败");
                return;
            }

            // 取当前 run loop 的 mode 当作注册 mode。
            // 注意：新起的线程上 run loop 还没跑过，CFRunLoopCopyCurrentMode 返回的是 NULL；
            // 把 NULL 交给 CFRunLoopAddSource，CoreFoundation 会拿它去 mode 集合里查，
            // CFHash 对 NULL 抛异常后自旋，整进程常驻吃掉一个核（实测 95% CPU）。
            // 所以这里必须兜底成默认 mode。
            var mode = CFRunLoopCopyCurrentMode(runLoop);
            var modeOwned = mode != nint.Zero; // Copy* 返回的是 +1 引用，要自己放
            if (mode == nint.Zero)
            {
                mode = DefaultRunLoopMode();
                modeOwned = false; // 全局常量不是我们持有的引用，不能 release
            }

            if (mode == nint.Zero)
            {
                DesktopLog.Write("macOS hotkey tap source 注册失败（拿不到默认 run loop mode）");
                return;
            }

            try
            {
                CFRunLoopAddSource(runLoop, source, mode);
            }
            catch (Exception ex)
            {
                DesktopLog.Write($"macOS hotkey tap add source failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            finally
            {
                if (modeOwned && mode != nint.Zero) CFRelease(mode);
            }

            // source 挂好之后才 enable：早 enable 会漏掉最初几毫秒的按键
            CGEventTapEnable(_tap, 1);
            CFRunLoopRun();
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"macOS hotkey tap loop error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// tap 回调：返回事件指针表示放行，返回 NULL 表示吞掉这次按键。
    /// 回调跑在 tap 自己的线程上，这里只做「取键 + 比对 + 投递」，不做任何 UI / IO。
    /// </summary>
    private nint OnEvent(nint proxy, CgEventType type, nint cgEvent, nint userInfo)
    {
        try
        {
            switch (type)
            {
                case CgEventType.KeyDown:
                    return HandleKeyDown(cgEvent);
                case CgEventType.KeyUp:
                    return HandleKeyUp(cgEvent);
                case CgEventType.FlagsChanged:
                    HandleFlagsChanged();
                    return cgEvent;
                case CgEventType.TapDisabledByTimeout:
                case CgEventType.TapDisabledByUserInput:
                    // 系统把 tap 摘了：重新 enable，不然快捷键会静默失灵
                    try
                    {
                        CGEventTapEnable(_tap, 1);
                    }
                    catch
                    {
                        // 忽略：enable 失败就等下次
                    }

                    return cgEvent;
                default:
                    return cgEvent;
            }
        }
        catch
        {
            // 回调里绝不能抛异常，否则 tap 会被系统摘掉
            return cgEvent;
        }
    }

    private nint HandleKeyDown(nint cgEvent)
    {
        var keyCode = (int)CGEventGetIntegerValueField(cgEvent, CgEventField.KeyboardEventKeycode);
        var modifiers = ModifiersOf(CGEventGetFlags(cgEvent));

        if (Capturing)
        {
            if (keyCode == KeyCodeEscape)
            {
                EndCapture();
                DesktopLog.Write("hotkey capture cancelled (Esc)");
                Post(() => CaptureCancelled?.Invoke());
                return nint.Zero;
            }

            if (IsModifierKeyCode(keyCode))
            {
                lock (Gate) _captureModifiers = modifiers;
                PostCaptureProgress();
                return nint.Zero;
            }

            var virtualKey = VirtualKeyOf(keyCode);
            if (virtualKey <= 0)
            {
                // 键码表里没有的键（少见，但系统/外接键盘可能带来）只吞掉，不结束录制
                return nint.Zero;
            }

            var combo = new HotkeyCombo(modifiers, virtualKey);
            EndCapture();
            DesktopLog.Write($"hotkey captured → {HotkeyKeyMap.Describe(combo)} (mac keycode={keyCode})");
            Post(() => Captured?.Invoke(combo));
            return nint.Zero;
        }

        if (!IsEnabled()) return cgEvent;

        var binding = TryMatch(keyCode, modifiers);
        if (binding is null) return cgEvent;

        // 长按只吞键、不再重复触发：否则按住组合键会把按键漏进前台程序
        if (!IsRestarted(keyCode))
        {
            lock (Gate) _swallowed.Add(keyCode);
            Post(() => Triggered?.Invoke(binding));
        }

        return nint.Zero;
    }

    private nint HandleKeyUp(nint cgEvent)
    {
        var keyCode = (int)CGEventGetIntegerValueField(cgEvent, CgEventField.KeyboardEventKeycode);
        lock (Gate)
        {
            _downAt.Remove(keyCode);
            // 被吞掉的那次按下，抬键也吞掉，前台程序不会收到「只有抬键」的残影
            if (_swallowed.Remove(keyCode)) return nint.Zero;
        }

        return cgEvent;
    }

    /// <summary>修饰键变化：录制时用来实时刷新「已按下的键」提示。</summary>
    private void HandleFlagsChanged()
    {
        if (!Capturing) return;
        var modifiers = ModifiersOf(CGEventSourceFlagsState(SourceStateHidSystem));
        lock (Gate)
        {
            if (!_capturing) return;
            _captureModifiers = modifiers;
        }

        PostCaptureProgress();
    }

    /// <summary>同一主键的连续 KeyDown（系统自动重复）返回 true；正常按下会登记时间戳。</summary>
    private bool IsRestarted(int keyCode)
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (_downAt.TryGetValue(keyCode, out var last) && now - last < RepeatWindowMs)
            {
                _downAt[keyCode] = now;
                return true;
            }

            _downAt[keyCode] = now;
            return false;
        }
    }

    /// <summary>按当前修饰键状态找一条启用中的绑定；没有就返回 null。</summary>
    private HotkeyBinding? TryMatch(int keyCode, HotkeyModifier modifiers)
    {
        lock (Gate)
        {
            if (!_enabled) return null;
            foreach (var binding in _bindings)
            {
                if (!binding.Enabled) continue;
                // Carbon 已经注册过的绑定由 Carbon 触发，这里跳过，避免同一次按键触发两遍。
                if (_carbonRegistrations.ContainsKey(binding.Id)) continue;
                if (binding.VirtualKey != VirtualKeyOf(keyCode)) continue;
                if ((HotkeyModifier)binding.Modifiers != modifiers) continue;
                return binding;
            }
        }

        return null;
    }

    private bool IsEnabled()
    {
        lock (Gate) return _enabled;
    }

    /// <summary>CGEventFlags → 修饰键位：Command↔Win、Option↔Alt、Control↔Ctrl、Shift↔Shift。</summary>
    private static HotkeyModifier ModifiersOf(ulong flags)
    {
        var modifiers = HotkeyModifier.None;
        if ((flags & FlagShift) != 0) modifiers |= HotkeyModifier.Shift;
        if ((flags & FlagControl) != 0) modifiers |= HotkeyModifier.Ctrl;
        if ((flags & FlagAlternate) != 0) modifiers |= HotkeyModifier.Alt;
        if ((flags & FlagCommand) != 0) modifiers |= HotkeyModifier.Win;
        return modifiers;
    }

    /// <summary>是不是修饰键本身（这些键不作为组合键的主键）。</summary>
    private static bool IsModifierKeyCode(int keyCode) => keyCode is
        0x36 or 0x37 or   // Command
        0x38 or 0x3C or   // Shift
        0x39 or           // CapsLock
        0x3A or 0x3D or   // Option
        0x3B or 0x3E or   // Control
        0x3F;             // Fn

    private void PostCaptureProgress()
    {
        HotkeyModifier modifiers;
        lock (Gate) modifiers = _captureModifiers;
        Post(() => CaptureProgress?.Invoke(modifiers));
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Input);

    /// <summary>macOS virtual keycode（Carbon kVK_*）→ Win32 虚拟键码。</summary>
    private static int VirtualKeyOf(int keyCode)
        => MacOsKeyCodeMap.TryWin32VirtualKey((uint)keyCode, out var vk) ? vk : 0;

    // ===== CoreGraphics / CoreFoundation =====

    private enum CgEventTapLocation
    {
        Hid = 0,
        Session = 1,
        AnnotatedSession = 2
    }

    private enum CgEventTapPlacement
    {
        HeadInsert = 0,
        TailAppend = 1
    }

    private enum CgEventTapOptions
    {
        Default = 0,
        ListenOnly = 1
    }

    private enum CgEventType
    {
        KeyDown = 10,
        KeyUp = 11,
        FlagsChanged = 12,

        /// <summary>tap 因为超时被系统摘掉（回调超时或用户切换了输入源）——需要自己重新 enable。</summary>
        TapDisabledByTimeout = 0xFE,

        /// <summary>tap 被用户输入事件摘掉——同样要重新 enable。</summary>
        TapDisabledByUserInput = 0xFF
    }

    private enum CgEventField
    {
        KeyboardEventKeycode = 9
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint TapCallback(nint proxy, CgEventType type, nint cgEvent, nint userInfo);

    [DllImport(CoreGraphics)]
    private static extern nint CGEventTapCreate(
        CgEventTapLocation tap,
        CgEventTapPlacement place,
        CgEventTapOptions options,
        ulong eventsOfInterest,
        TapCallback callback,
        nint userInfo);

    [DllImport(CoreGraphics)]
    private static extern void CGEventTapEnable(nint tap, byte enable);

    [DllImport(CoreGraphics)]
    private static extern long CGEventGetIntegerValueField(nint cgEvent, CgEventField field);

    [DllImport(CoreGraphics)]
    private static extern ulong CGEventGetFlags(nint cgEvent);

    [DllImport(CoreGraphics)]
    private static extern ulong CGEventSourceFlagsState(int stateId);

    [DllImport(CoreFoundation)]
    private static extern nint CFRunLoopGetCurrent();

    [DllImport(CoreFoundation)]
    private static extern nint CFRunLoopCopyCurrentMode(nint runLoop);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopAddSource(nint runLoop, nint source, nint mode);

    [DllImport(CoreFoundation)]
    private static extern nint CFMachPortCreateRunLoopSource(nint allocator, nint machPort, nint order);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopRun();

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, byte[] cStr, int encoding);

    /// <summary>kCFStringEncodingUTF8。</summary>
    private const int CFStringEncodingUtf8 = 0x08000100;

    /// <summary>
    /// 取 CoreFoundation 的默认 run loop mode（kCFRunLoopDefaultMode）。
    /// 它是一个全局 CFStringRef 常量，先用 dlsym 拿符号地址再读出指针值；
    /// 拿不到就按同样的内容新建一个（CFString 的相等与哈希都按内容算，集合查找照样命中）。
    /// </summary>
    private static nint DefaultRunLoopMode()
    {
        try
        {
            var handle = NativeLibrary.Load(CoreFoundation);
            if (NativeLibrary.TryGetExport(handle, "kCFRunLoopDefaultMode", out var symbol) &&
                symbol != nint.Zero)
            {
                var value = Marshal.ReadIntPtr(symbol);
                if (value != nint.Zero) return value;
            }
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"macOS default run loop mode via dlsym failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("kCFRunLoopDefaultMode\0");
            return CFStringCreateWithCString(nint.Zero, bytes, CFStringEncodingUtf8);
        }
        catch
        {
            return nint.Zero;
        }
    }

    [DllImport(ApplicationServices)]
    private static extern byte AXIsProcessTrusted();

    private static ulong Mask(CgEventType type) => 1UL << (int)type;

    /// <summary>当前进程有没有辅助功能权限：没有的话 tap 建了也收不到按键。</summary>
    private static bool IsProcessTrusted()
    {
        try
        {
            return AXIsProcessTrusted() != 0;
        }
        catch
        {
            return true;
        }
    }
}
