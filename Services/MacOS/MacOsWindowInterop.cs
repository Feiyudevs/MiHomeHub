// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// macOS 窗口层级 / 行为操作（纯托管 P/Invoke + objc runtime；不引新 NuGet、不写原生代码）。
///
/// <para>
/// Windows 那套「贴桌面」是靠 Progman + WS_EX_* + SetWindowPos(HWND_BOTTOM) 拼出来的，
/// macOS 没有等价 API，对应关系是：
/// <list type="bullet">
/// <item>HWND_BOTTOM（永远压在普通窗口之下）→ <c>NSWindow.setLevel(kCGDesktopWindowLevel)</c>。</item>
/// <item>HWND_TOPMOST（浮在所有窗口之上）→ <c>NSWindow.setLevel(kCGFloatingWindowLevel)</c>。</item>
/// <item>贴壁纸层还要「跟着所有桌面空间走、不进 Cmd+Tab」→ <c>setCollectionBehavior</c>。</item>
/// <item>WS_EX_NOACTIVATE（点卡片不抢焦点）→ <c>MakeNonActivating</c>（运行时把窗口换成
/// 「不能成为 key window」的子类；<c>ShowActivated=false</c> 只管显示时不要激活，管不了点击）。</item>
/// </list>
/// </para>
///
/// <para>
/// 取 NSWindow 指针有两条路：Avalonia 的 <c>TryGetPlatformHandle()</c>（Window 在 macOS 上
/// 句柄就是 NSWindow；TopLevel 拿到的是 NSView，需要向上问一次 window），
/// 以及反射 <c>PlatformImpl.MacOSTopLevelHandle.NSWindow</c>（该类型是 internal，编译期拿不到）。
/// 两条都失败时返回 false —— 调用方据此降级（卡片退化成普通浮窗），绝不抛异常打断启动。
/// </para>
/// </summary>
internal static class MacOsWindowInterop
{
    /// <summary>
    /// 贴壁纸层：<c>kCGDesktopWindowLevel</c>（= AppKit 的 <c>NSDesktopWindowLevel</c>）。
    /// 桌面壁纸与普通应用窗口都在它之上/之下由系统决定，桌面小部件就停在这一层。
    /// </summary>
    public const long DesktopWindowLevel = -2147483623;

    /// <summary>
    /// 桌面图标层：<c>kCGDesktopIconWindowLevel</c>（= AppKit 的 <c>NSDesktopIconWindowLevel</c>）。
    /// 访达的桌面图标窗就在这一层，而且是<b>铺满整块屏幕</b>的：
    /// 卡片停在 <see cref="DesktopWindowLevel"/> 会被它整个盖住 → 点不动、拖不动
    /// （本机实测访达桌面窗 layer = -2147483603）。抬到图标层后卡片才真正可交互，
    /// 同时仍然低于普通应用窗口（level 0），视觉上依旧「贴在桌面上」。
    /// </summary>
    public const long DesktopIconWindowLevel = -2147483603;

    /// <summary>
    /// 桌面卡片层：比访达桌面图标层（-2147483603）高一层，比普通应用窗口（0）低。
    /// <para>
    /// 用在「贴在桌面上」的卡片窗口：访达的桌面图标窗是<b>铺满整块屏幕</b>的，
    /// 和卡片停在同一层时，谁在前取决于窗口创建顺序与激活顺序 ——
    /// 主窗口一最小化，系统激活访达桌面，访达窗口就排到卡片前面，
    /// 卡片随即变成「看得见但点不动」（用户反馈：主窗口缩小后悬浮组件无法点击和拖动）。
    /// 高一层就彻底避开这个顺序问题，同时仍然低于普通应用窗口。
    /// </para>
    /// </summary>
    public const long DesktopCardWindowLevel = -2147483602;

    /// <summary>
    /// 浮层：<c>kCGFloatingWindowLevel</c>（= <c>NSWindowLevelForKey(.floating)</c>），
    /// 对应 Windows 的 HWND_TOPMOST（标题栏图钉态）。
    /// </summary>
    public const long FloatingWindowLevel = 3;

    /// <summary>
    /// 桌面浮层专用层级：<c>NSWindowLevelForKey(.statusPanel)</c>（= 20），比浮层级（3）高一层。
    /// 菜单 / 音量 / 重命名 / 音箱 / 空调这些浮层在 Windows 上是 Topmost（压在置顶分区卡片之上），
    /// macOS 上「浮层」和「钉住的卡片」是同一个 level（3），谁在前看激活顺序 → 浮层可能被卡片盖住。
    /// </summary>
    public const long PopupWindowLevel = 20;

    /// <summary>
    /// <c>NSWindowCollectionBehavior</c>：CanJoinAllSpaces(1) | Stationary(16) |
    /// IgnoresCycle(64) | FullScreenAuxiliary(256)。
    /// CanJoinAllSpaces = 所有「桌面空间」都显示（对应 Windows 桌面组件跟着桌面走）；
    /// IgnoresCycle = 不参与 Cmd+Tab 轮换（对应 Windows 上属于 Explorer 的桌面窗口，
    /// 不出现在 Alt+Tab 里）；FullScreenAuxiliary = 全屏应用时也留在桌面上。
    /// </summary>
    public const long DesktopCollectionBehavior = 1 | 16 | 64 | 256;

    private const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>返回指针的 objc 消息（取 NSView 的 window 等）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_ptr(nint self, nint sel);

    /// <summary>无返回值的 objc 消息（displayIfNeeded 等无参 setter）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(nint self, nint sel);

    /// <summary>参数为 64 位整数的 objc 消息（setLevel: / setCollectionBehavior:）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_long(nint self, nint sel, long arg);

    /// <summary>参数为 BOOL（1 字节）的 objc 消息（setIgnoresMouseEvents: / setOpaque:）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_bool(nint self, nint sel, byte arg);

    /// <summary>返回 BOOL（1 字节）、不带参数的 objc 消息（isMiniaturized）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte objc_msgSend_bool0(nint self, nint sel);

    /// <summary>参数为对象指针的 objc 消息（setBackgroundColor:）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_obj(nint self, nint sel, nint arg);

    /// <summary>参数为 double（CGFloat）的 objc 消息（setCornerRadius:）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_double(nint self, nint sel, double arg);

    /// <summary>返回 double（CGFloat / NSTimeInterval）的 objc 消息（doubleClickInterval）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern double objc_msgSend_double_ret(nint self, nint sel);

    /// <summary>取 objc 类对象（NSColor 等）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>取实例的真实类对象。</summary>
    [DllImport(ObjC, EntryPoint = "object_getClass")]
    private static extern nint object_getClass(nint obj);

    /// <summary>取实例类名的 C 字符串。</summary>
    [DllImport(ObjC, EntryPoint = "object_getClassName")]
    private static extern nint object_getClassName(nint obj);

    /// <summary>运行时建一个子类（不写原生代码、不引新依赖）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")]
    private static extern nint objc_allocateClassPair(nint superClass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint extraBytes);

    /// <summary>注册运行时建的类。</summary>
    [DllImport(ObjC, EntryPoint = "objc_registerClassPair")]
    private static extern void objc_registerClassPair(nint cls);

    /// <summary>往类上挂一个方法实现（imp 是原生函数指针）。</summary>
    [DllImport(ObjC, EntryPoint = "class_addMethod")]
    private static extern nint class_addMethod(nint cls, nint sel, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    /// <summary>取某个方法的实现指针（imp）。</summary>
    [DllImport(ObjC, EntryPoint = "class_getMethodImplementation")]
    private static extern nint class_getMethodImplementation(nint cls, nint sel);

    /// <summary>把实例换到另一个类上（子类才允许）。</summary>
    [DllImport(ObjC, EntryPoint = "object_setClass")]
    private static extern nint object_setClass(nint obj, nint cls);

    /// <summary>
    /// 取 TopLevel 对应的 NSWindow 指针。取不到返回 false，调用方走降级路径。
    /// </summary>
    public static bool TryGetNativeWindow(TopLevel top, out nint nsWindow)
    {
        nsWindow = nint.Zero;
        if (top is null) return false;

        try
        {
            // 1) 平台句柄：Window 在 macOS 上的 Handle 就是 NSWindow
            var platform = top.TryGetPlatformHandle();
            if (platform is not null && platform.Handle != nint.Zero)
            {
                if (string.Equals(platform.HandleDescriptor, "NSWindow", StringComparison.Ordinal))
                {
                    nsWindow = platform.Handle;
                    return true;
                }

                // TopLevel（非 Window）拿到的是 NSView，向上问一次它的 window
                var fromView = WindowFromView(platform.Handle);
                if (fromView != nint.Zero)
                {
                    nsWindow = fromView;
                    return true;
                }
            }

            // 2) 反射 PlatformImpl → MacOSTopLevelHandle.NSWindow（internal 类型，只能反射取）
            //    注意：Window 上 TopLevel 与 Window 各有一个同名 PlatformImpl 属性，
            //    用 GetProperty 查会抛 AmbiguousMatchException，所以这里遍历同名属性逐个试。
            foreach (var implProp in top.GetType()
                         .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!string.Equals(implProp.Name, "PlatformImpl", StringComparison.Ordinal)) continue;
                var impl = implProp.GetValue(top);
                if (impl is null) continue;

                var nsWindowProp = impl.GetType()
                    .GetProperty("NSWindow", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var value = nsWindowProp?.GetValue(impl);
                if (value is IntPtr handle && handle != nint.Zero)
                {
                    nsWindow = handle;
                    return true;
                }

                // 形状不同时（不同 Avalonia 版本 NSWindow 可能是带 Handle 的对象）
                var handleProp = impl.GetType()
                    .GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (handleProp?.GetValue(impl) is IntPtr nested && nested != nint.Zero)
                {
                    nsWindow = nested;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] 取 NSWindow 失败: {ex.Message}");
        }

        nsWindow = nint.Zero;
        return false;
    }

    /// <summary>设置 NSWindow 层级（level）。拿不到窗口指针就什么都不做。</summary>
    public static void SetLevel(TopLevel top, long level)
    {
        if (!TryGetNativeWindow(top, out var win)) return;
        try
        {
            objc_msgSend_long(win, sel_registerName("setLevel:"), level);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] setLevel 失败: {ex.Message}");
        }
    }

    /// <summary>贴到壁纸层（kCGDesktopWindowLevel）。</summary>
    public static void SetDesktopLevel(TopLevel top) => SetLevel(top, DesktopWindowLevel);

    /// <summary>
    /// 贴到桌面图标层（kCGDesktopIconWindowLevel）：比壁纸层高一层，刚好压过铺满全屏的访达桌面窗，
    /// 卡片才能真正收到点击与拖拽；又仍然低于普通应用窗口，不会盖住正在用的 App。
    /// </summary>
    public static void SetDesktopIconLevel(TopLevel top) => SetLevel(top, DesktopIconWindowLevel);

    /// <summary>
    /// 贴到「桌面卡片层」（比访达桌面图标层高一层）：卡片永远压在访达桌面窗之上，
    /// 主窗口最小化、系统激活访达桌面时也不会把卡片盖住 → 卡片始终可点、可拖。
    /// </summary>
    public static void SetDesktopCardLevel(TopLevel top) => SetLevel(top, DesktopCardWindowLevel);

    /// <summary>
    /// 把原生窗口背景改成「透明 + 非不透明」。
    /// Avalonia 只保证自己的绘制层透明，NSWindow 自身的背景色默认仍可能画出一块不透明底
    /// （自绘圆角卡片四周会露出黑边）。这里显式 setBackgroundColor: clearColor + setOpaque: NO。
    /// </summary>
    public static void MakeTransparent(TopLevel top)
    {
        if (!TryGetNativeWindow(top, out var win)) return;
        try
        {
            var colorClass = objc_getClass("NSColor");
            if (colorClass != nint.Zero)
            {
                var clear = objc_msgSend_ptr(colorClass, sel_registerName("clearColor"));
                if (clear != nint.Zero)
                    objc_msgSend_obj(win, sel_registerName("setBackgroundColor:"), clear);
            }

            objc_msgSend_bool(win, sel_registerName("setOpaque:"), 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] MakeTransparent 失败: {ex.Message}");
        }
    }

    /// <summary>浮到普通窗口之上（kCGFloatingWindowLevel，对应标题栏图钉）。</summary>
    public static void SetFloatingLevel(TopLevel top) => SetLevel(top, FloatingWindowLevel);

    /// <summary>
    /// 把窗口内容按圆角裁剪（对应 Windows 的 DWM 圆角）。
    /// <para>
    /// macOS 的窗口级毛玻璃是 NSVisualEffectView，它<b>铺满整个窗口矩形</b>：
    /// 自绘圆角收不住它，四角会露出方块的模糊边（用户反馈「四个角显示黑边」）。
    /// 把 contentView 的图层设成 cornerRadius + masksToBounds，
    /// 模糊层和 Avalonia 画的卡片一起被裁成圆角，形状就和 Windows 版一致了。
    /// </para>
    /// </summary>
    public static bool RoundContentCorners(TopLevel top, double radius)
    {
        if (!TryGetNativeWindow(top, out var win)) return false;
        try
        {
            var content = objc_msgSend_ptr(win, sel_registerName("contentView"));
            if (content == nint.Zero) return false;
            var layer = objc_msgSend_ptr(content, sel_registerName("layer"));
            if (layer == nint.Zero) return false;
            objc_msgSend_double(layer, sel_registerName("setCornerRadius:"), radius);
            objc_msgSend_bool(layer, sel_registerName("setMasksToBounds:"), 1);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] RoundContentCorners 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 系统双击间隔（毫秒）：macOS 对应 <c>[NSEvent doubleClickInterval]</c>。
    /// Windows 版用 <c>GetDoubleClickTime()</c>，两端读的都是系统设置里的那一个值。
    /// </summary>
    public static int DoubleClickIntervalMs()
    {
        try
        {
            var evtClass = objc_getClass("NSEvent");
            if (evtClass == nint.Zero) return 0;
            var seconds = objc_msgSend_double_ret(evtClass, sel_registerName("doubleClickInterval"));
            if (seconds <= 0) return 0;
            return (int)Math.Round(seconds * 1000);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] DoubleClickInterval 失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>自建效果层的类名（区别于 Avalonia 自己那两个 NSVisualEffectView）。</summary>
    private const string EffectViewClassName = "MiHomeHubEffectView";

    /// <summary>NSSize（两个 double，按值传参）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize
    {
        public double Width;
        public double Height;
    }

    /// <summary>参数为 NSSize 的 objc 消息（setFrameSize:）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_size(nint self, nint sel, NSSize size);

    /// <summary>返回 int 的 objc 消息（NSArray.count）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern int objc_msgSend_int(nint self, nint sel);

    /// <summary>参数为 int 的 objc 消息（NSArray.objectAtIndex:）。</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_ptr_int(nint self, nint sel, int index);

    // 注意：三个参数的 objc_msgSend（addSubview:positioned:relativeTo:）在本机 arm64 上
    // 经 .NET P/Invoke 调用会直接把进程打死（SIGKILL，纯 ObjC 里同一句没问题）。
    // 所以只用 1 个参数的 addSubview:，再用「把 Avalonia 的绘制视图重新 addSubview 一次」
    // 的方式把效果层挪到它下面。

    /// <summary>
    /// 给窗口装一层<b>真毛玻璃</b>（macOS 的 NSVisualEffectView）。
    /// <para>
    /// 为什么不用 Avalonia 的 <c>WindowTransparencyLevel.AcrylicBlur</c>：
    /// Avalonia 11.3.9 在 macOS 上确实会建 NSVisualEffectView，但它一直停在
    /// hidden=1 / state=FollowsWindowActiveState / material=Titlebar（本机探针实测），
    /// 也就是说那层模糊<b>根本没生效</b>，拿不到 Windows 版 DWM 亚克力的观感。
    /// 这里自己建一个 <c>NSVisualEffectView</c> 子类：BehindWindow 混合 + HUDWindow 材质 + Active 状态，
    /// 插在 contentView 的最底层（Avalonia 的绘制层画在它上面），
    /// 再靠 contentView 的圆角裁剪（见 <see cref="RoundContentCorners"/>）收成和 Windows 版一样的圆角。
    /// </para>
    /// </summary>
    /// <param name="top">目标窗口。</param>
    /// <param name="enabled">true=开毛玻璃；false=关（隐藏效果层，回到纯色半透）。</param>
    /// <param name="radius">圆角半径（点），与 Windows 版的 DWM 圆角一致（8）。</param>
    /// <param name="width">内容宽度（点）。</param>
    /// <param name="height">内容高度（点）。</param>
    /// <returns>效果层是否就绪。</returns>
    public static bool SetDesktopBlur(TopLevel top, bool enabled, double radius, double width, double height)
    {
        if (!TryGetNativeWindow(top, out var win)) return false;
        try
        {
            var content = objc_msgSend_ptr(win, sel_registerName("contentView"));
            if (content == nint.Zero) return false;

            var view = FindEffectView(content);
            if (!enabled)
            {
                if (view != nint.Zero) objc_msgSend_bool(view, sel_registerName("setHidden:"), 1);
                RoundContentCorners(top, radius);
                return false;
            }

            if (view == nint.Zero)
            {
                view = CreateEffectView();
                if (view == nint.Zero) return false;
                // 插进去，再把 Avalonia 的绘制视图重新 addSubview 一次挪回最上层：
                // 效果层于是落在「卡片底色」的下面（BehindWindow 混合 + HUDWindow 材质 = 磨砂玻璃）。
                objc_msgSend_obj(content, sel_registerName("addSubview:"), view);
                var renderView = FindRenderView(content);
                if (renderView != nint.Zero)
                    objc_msgSend_obj(content, sel_registerName("addSubview:"), renderView);
            }

            objc_msgSend_long(view, sel_registerName("setBlendingMode:"), 0);   // BehindWindow
            objc_msgSend_long(view, sel_registerName("setMaterial:"), 11);      // HUDWindow
            objc_msgSend_long(view, sel_registerName("setState:"), 1);          // Active（窗口不激活也要有磨砂）
            objc_msgSend_size(view, sel_registerName("setFrameSize:"), new NSSize { Width = width, Height = height });
            objc_msgSend_long(view, sel_registerName("setAutoresizingMask:"), 2 | 16); // 宽高自适应
            objc_msgSend_bool(view, sel_registerName("setHidden:"), 0);

            RoundContentCorners(top, radius);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] SetDesktopBlur 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>找 Avalonia 自己的绘制视图（类名带 Avn），用来把它挪回最上层。</summary>
    private static nint FindRenderView(nint content)
    {
        try
        {
            var subs = objc_msgSend_ptr(content, sel_registerName("subviews"));
            if (subs == nint.Zero) return nint.Zero;
            var count = objc_msgSend_int(subs, sel_registerName("count"));
            for (var i = count - 1; i >= 0; i--)
            {
                var item = objc_msgSend_ptr_int(subs, sel_registerName("objectAtIndex:"), i);
                if (item == nint.Zero) continue;
                var name = Marshal.PtrToStringAnsi(object_getClassName(object_getClass(item))) ?? string.Empty;
                if (name.Contains("Avn", StringComparison.Ordinal))
                    return item;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] FindRenderView 失败: {ex.Message}");
        }

        return nint.Zero;
    }

    /// <summary>在 contentView 的子视图里找我们自建的效果层。</summary>
    private static nint FindEffectView(nint content)
    {
        try
        {
            var subs = objc_msgSend_ptr(content, sel_registerName("subviews"));
            if (subs == nint.Zero) return nint.Zero;
            var count = objc_msgSend_int(subs, sel_registerName("count"));
            for (var i = 0; i < count; i++)
            {
                var item = objc_msgSend_ptr_int(subs, sel_registerName("objectAtIndex:"), i);
                if (item == nint.Zero) continue;
                var name = Marshal.PtrToStringAnsi(object_getClassName(object_getClass(item)));
                if (string.Equals(name, EffectViewClassName, StringComparison.Ordinal))
                    return item;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] FindEffectView 失败: {ex.Message}");
        }

        return nint.Zero;
    }

    /// <summary>建一个 NSVisualEffectView 子类实例（BehindWindow + HUDWindow + Active）。</summary>
    private static nint CreateEffectView()
    {
        var superClass = objc_getClass("NSVisualEffectView");
        if (superClass == nint.Zero) return nint.Zero;

        var cls = objc_getClass(EffectViewClassName);
        if (cls == nint.Zero)
        {
            cls = objc_allocateClassPair(superClass, EffectViewClassName, nint.Zero);
            if (cls == nint.Zero) cls = objc_getClass(EffectViewClassName);
            else objc_registerClassPair(cls);
        }

        if (cls == nint.Zero) return nint.Zero;
        var alloc = objc_msgSend_ptr(cls, sel_registerName("alloc"));
        if (alloc == nint.Zero) return nint.Zero;
        return objc_msgSend_ptr(alloc, sel_registerName("init"));
    }

    /// <summary>
    /// 让窗口点击时<b>不激活本 App</b>（对应 Windows 的 WS_EX_NOACTIVATE）。
    /// <para>
    /// NSWindowStyleMaskNonactivatingPanel 对普通 NSWindow 无效：AppKit 直接把这一位丢掉
    /// （本机实测 styleMask 加 1&lt;&lt;7 后读回原值）。
    /// 真正管用的是「这个窗口不能成为 key window」—— 这里在运行时给它的类建一个子类，
    /// 把 canBecomeKeyWindow / canBecomeMainWindow 覆写成 NO，再把窗口实例换到该子类上。
    /// 影响面只限这一个窗口实例：主窗口照旧能正常接收键盘输入。
    /// </para>
    /// </summary>
    public static bool MakeNonActivating(TopLevel top)
    {
        if (!TryGetNativeWindow(top, out var win)) return false;
        try
        {
            var current = object_getClass(win);
            if (current == nint.Zero) return false;

            var name = Marshal.PtrToStringAnsi(object_getClassName(current)) ?? string.Empty;
            if (name.StartsWith("MiHomeHubNonactivating", StringComparison.Ordinal))
                return true;   // 已经换过，别再套一层

            if (!_nonActivatingClasses.TryGetValue(current, out var subclass))
            {
                subclass = objc_allocateClassPair(current, "MiHomeHubNonactivatingWindow", nint.Zero);
                if (subclass == nint.Zero) return false;

                // 借用 NSWindow 自带的 isOpaque 实现当「返回 NO 的函数」：
                // 它的签名正是 BOOL (id, SEL)，而卡片窗是透明的 → isOpaque 为 NO。
                // 这样不用写任何原生回调，绕开托管委托被非托管线程调用时的崩溃风险。
                var imp = class_getMethodImplementation(objc_getClass("NSWindow"), sel_registerName("isOpaque"));
                if (imp != nint.Zero)
                {
                    class_addMethod(subclass, sel_registerName("canBecomeKeyWindow"), imp, "B@:");
                    class_addMethod(subclass, sel_registerName("canBecomeMainWindow"), imp, "B@:");
                }
                objc_registerClassPair(subclass);
                _nonActivatingClasses[current] = subclass;
            }

            if (subclass == nint.Zero) return false;
            object_setClass(win, subclass);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] MakeNonActivating 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>每个原生窗口类只建一次非激活子类。</summary>
    private static readonly Dictionary<nint, nint> _nonActivatingClasses = new();

    /// <summary>设置窗口行为：跟随所有桌面空间、不参与 Cmd+Tab。</summary>
    public static void SetCollectionBehavior(TopLevel top)
    {
        if (!TryGetNativeWindow(top, out var win)) return;
        try
        {
            objc_msgSend_long(win, sel_registerName("setCollectionBehavior:"), DesktopCollectionBehavior);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] setCollectionBehavior 失败: {ex.Message}");
        }
    }

    /// <summary>窗口是否忽略鼠标点击（true = 鼠标穿透，false = 正常接收）。</summary>
    public static void SetIgnoresMouseEvents(TopLevel top, bool ignore)
    {
        if (!TryGetNativeWindow(top, out var win)) return;
        try
        {
            objc_msgSend_bool(win, sel_registerName("setIgnoresMouseEvents:"), ignore ? (byte)1 : (byte)0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] setIgnoresMouseEvents 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 原生最小化（收进 Dock）：<c>miniaturize:</c>。
    /// </summary>
    public static void Miniaturize(TopLevel top)
    {
        if (!TryGetNativeWindow(top, out var win)) return;
        try
        {
            objc_msgSend_void(win, sel_registerName("miniaturize:"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] miniaturize 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 原生还原（从 Dock 展开）：<c>deminiaturize:</c>。
    /// <para>
    /// 为什么不能只靠 Avalonia 的 <c>WindowState = Normal</c>：窗口隐藏时
    /// （托盘启动、点红点收起）后端不会把状态推给 NSWindow，用户点 Dock 图标时
    /// 系统把 NSWindow 展开，Avalonia 那边却还记着 Minimized，
    /// 于是「点了没反应 / 展开成空白」。这里直接对 NSWindow 发原生消息把两边对齐。
    /// </para>
    /// </summary>
    public static void Deminiaturize(TopLevel top)
    {
        if (!TryGetNativeWindow(top, out var win)) return;
        try
        {
            objc_msgSend_void(win, sel_registerName("deminiaturize:"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] deminiaturize 失败: {ex.Message}");
        }
    }

    /// <summary>NSWindow 是否处于最小化（收在 Dock 里）。取不到窗口指针时返回 false。</summary>
    public static bool IsMiniaturized(TopLevel top)
    {
        if (!TryGetNativeWindow(top, out var win)) return false;
        try
        {
            return objc_msgSend_bool0(win, sel_registerName("isMiniaturized")) != 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] isMiniaturized 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 强制窗口重绘一次：改完 level / collectionBehavior 后系统不一定立刻重新合成，
    /// 这里显式 displayIfNeeded 一次，等价于 Windows 侧的 ForcePresent。
    /// </summary>
    public static void Redraw(TopLevel top)
    {
        if (!TryGetNativeWindow(top, out var win)) return;
        try
        {
            objc_msgSend_void(win, sel_registerName("displayIfNeeded"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mac] displayIfNeeded 失败: {ex.Message}");
        }
    }

    /// <summary>NSView → 它的 NSWindow（objc 消息 <c>window</c>）。</summary>
    private static nint WindowFromView(nint view)
    {
        if (view == nint.Zero) return nint.Zero;
        try
        {
            return objc_msgSend_ptr(view, sel_registerName("window"));
        }
        catch
        {
            return nint.Zero;
        }
    }
}
