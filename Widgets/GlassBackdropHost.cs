using System;
using System.Runtime.InteropServices;
using MiHomeHub.Widgets;

namespace MiHomeHub.Widgets;

/// <summary>
/// 设置面板的**真·实时**背景毛玻璃：一个独立的分层窗口 + 自持合成层（<c>Zonex.Glass.dll</c>）。
///
/// 为什么不能用系统那套：DWM 的窗口圆角写死 8 DIP（<c>DWMWA_WINDOW_CORNER_PREFERENCE</c> 只能选
/// DEFAULT / ROUND / ROUNDSMALL / DONOTROUND），而窗口级磨砂（ACCENT / <c>DWMWA_SYSTEMBACKDROP_TYPE</c>）
/// 铺满整块窗口矩形，**不吃 <c>SetWindowRgn</c>** —— 想要 &gt;8 的圆角就一定会在四角露出方形磨砂。
///
/// 解法（与 Pane 同一套）：自己拥有合成树。<c>HostBackdropBrush</c> 采样窗口背后的**实时**画面，
/// 高斯模糊后由 <c>CompositionGeometricClip</c> 切出任意半径，投影也用合成的 <c>DropShadow</c> 画。
/// 这个窗口比面板四周各外扩 <see cref="MarginDip"/>，多出来的地方放阴影，且点击穿透（LAYERED + TRANSPARENT）。
/// </summary>
internal sealed class GlassBackdropHost : IDisposable
{
    /// <summary>设置窗用的默认外扩 DIP（放投影）。</summary>
    internal const int MarginDip = 48;

    /// <summary>模糊半径（DIP）。太小看不出磨砂，太大整片糊成纯色。</summary>
    private const float BlurDip = 32f;

    private const string ClassName = "ZonexGlassBackdrop";
    private const int DWMWA_USE_HOSTBACKDROPBRUSH = 17;

    [DllImport("Zonex.Glass.dll")]
    private static extern int ZonexGlassAttach(nint hwnd, float radius, float blur, float margin, out nint handle);

    [DllImport("Zonex.Glass.dll")]
    private static extern int ZonexGlassResize(nint handle, float width, float height);

    [DllImport("Zonex.Glass.dll")]
    private static extern int ZonexGlassCommitSize(nint handle);

    /// <summary>0=正常 1=待重试 2=无裁剪降级（背景偏亮） 3=正常且裁剪走运行时属性</summary>
    [DllImport("Zonex.Glass.dll")]
    private static extern int ZonexGlassMaterialState(nint handle);

    [DllImport("Zonex.Glass.dll")]
    private static extern int ZonexGlassLastMaterialError(nint handle);

    [DllImport("Zonex.Glass.dll")]
    private static extern int ZonexGlassSetRadius(nint handle, float radius);

    [DllImport("Zonex.Glass.dll")]
    private static extern int ZonexGlassSetTint(nint handle, uint tint, uint glow);

    [DllImport("Zonex.Glass.dll")]
    private static extern int ZonexGlassSetShadow(nint handle, float opacity, float blurDip, float offsetYDip);

    [DllImport("Zonex.Glass.dll")]
    private static extern int ZonexGlassRefreshBackdrop(nint handle);

    /// <summary>内容可见性总开关：窗口保持可见，只摘掉磨砂 / 透传 / 投影。见 <see cref="SuppressContent"/>。</summary>
    [DllImport("Zonex.Glass.dll")]
    private static extern int ZonexGlassSetContentVisible(nint handle, int visible);

    [DllImport("Zonex.Glass.dll")]
    private static extern void ZonexGlassDetach(nint handle);

    private static bool _availabilityChecked;
    private static bool _available;
    private static bool _classRegistered;
    private static readonly Win32.WndProcDelegate ClassProc = static (h, m, w, l) => Win32.DefWindowProcW(h, m, w, l);

    private nint _hwnd;
    private nint _handle;
    private nint _owner;
    private int _radiusDip = -1;
    private float _lastWDip = -1f;
    private float _lastHDip = -1f;
    private uint _tint = 0xFFFFFFFF;
    private bool _shown;

    /// <summary>
    /// 「不显示内容、但保留窗口」状态（见 <see cref="SuppressContent"/>）。
    /// 窗口保持可见 → DWM 不会丢掉 HostBackdropBrush 的采样 → 恢复时第一帧就是磨砂，
    /// 而不是「先亮一帧、再闪进模糊」。
    /// </summary>
    private bool _contentSuppressed;

    // 实例级参数（默认值 = 设置窗那套）。桌面分区用小余量 + 不模糊 + 不子类化 owner：
    // 分区窗自己已经装了一层 WndProc（Z 序拦截），再被这里套一层会让它的
    // EnsureWndProcInstalled 把「我们的过程」当成 original 重挂，形成过程链自环 → 死循环。
    private readonly int _marginDip;
    private float _blurDip;   // SetBlur 可改（毛玻璃开关切换时 Detach，下一次 Sync 重建材质）
    private readonly bool _hookOwner;
    /// <summary>true = 用桌面卡片专用 WndProc（<see cref="InstallUnifiedCardWndProc"/>）而不是弹窗宿主那套。
    /// 桌面组件无论接统一层还是独立宿主，都走这一套守卫（拦被动改序 / 拒最小化 / 风暴期落位 / 还原重采）。</summary>
    private readonly bool _cardProc;
    /// <summary>true = 宿主窗口也贴进桌面层（owner=Progman）。桌面分区必须这样：
    /// 普通顶层窗口没法排到「Progman 的 owned window」下方，会被系统重排到它上面去（实测）。</summary>
    private readonly bool _desktopLayer;

    // 方案④：桌面分区接入「统一全屏视觉层」后的转发状态。首次 Sync 才尝试接入，
    // 失败（DLL 缺导出 / 合成器建不起来）自动回退到「每分区一个宿主」的旧路径。
    private bool _unifiedTried;
    private bool _unified;
    private int _panelId = -1;

    // 投影参数（DIP / 0–1）。默认值 = 大影 48 模糊、18 下移、30% 浓度（原硬编码值）。
    private float _shadowOpacity = 0.30f;
    private float _shadowBlur = 48f;
    private float _shadowOffsetY = 18f;
    private bool _shadowDirty = true;

    // 跟随拖动用的 owner 子类化状态（见 TrackOwner）
    private Win32.WndProcDelegate? _ownerProc;
    private nint _ownerOldProc;
    private nint _hookedOwner;
    private Win32.WndProcDelegate? _hostProc;
    private nint _hostOldProc;
    private bool _repinning;
    private long _lastRepinLogMs;
    private int _marginPx = MarginDip;

    // Z 序守卫（2026-11-11）：所有「被动兜底」重挂先合并到一次 UI 线程 Post，
    // 连续 RepinFailureLimit 次落位未达成就熔断，绝不在 WndProc 里同步回写 Z 形成自激环。
    private bool _repinPending;
    private bool _repinCircuitOpen;
    private int _consecutiveRepinFailures;
    private (nint InsertAfter, int X, int Y, int W, int H) _lastZWrite;
    private bool _hasZWrite;
    private const int RepinFailureLimit = 3;

    /// <summary>宿主是否跟随 owner 进了系统置顶带（便签「置顶」）。决定 Z 落位锚点与自检判据。</summary>
    private bool _topmost;

    /// <summary>
    /// 桌面层宿主的落位锚点提供者：返回「Z 序最深的分区窗」。会话期会变（新建/关闭分区），
    /// 一存一份就可能把宿主插到某张卡上面，所以每次现问，由调用方做短缓存。
    /// </summary>
    internal Func<nint>? ZAnchorSource { get; set; }

    /// <summary>
    /// 统一层路径下是否由本宿主维护「全层共享」的圆角/模糊/投影参数。
    /// 桌面组件必须设 false：它只是后加入的一张卡片，外观继承分区当前设置即可；
    /// 否则组件与分区交替 Sync 会把全局参数来回覆盖（每次覆盖都会重设所有 panel 的裁剪与阴影）。
    /// </summary>
    internal bool ApplyGlobalAppearance { get; set; } = true;

    /// <summary>
    /// <paramref name="marginDip"/>：宿主窗口比面板外扩多少（放投影）。
    /// <paramref name="blurDip"/>：&lt;=0 表示**只画投影不画磨砂**（桌面分区卡片用）。
    /// <paramref name="hookOwner"/>：是否由本类子类化 owner 的 WndProc 跟随拖动。
    /// 调用方自己已经装了 WndProc 时必须传 false，改由它转发 <see cref="FollowOwnerMove"/>。
    /// </summary>
    internal GlassBackdropHost(int marginDip = MarginDip, float blurDip = BlurDip, bool hookOwner = true,
        bool desktopLayer = false, bool cardWndProc = false)
    {
        _marginDip = Math.Clamp(marginDip, 0, 160);
        _blurDip = blurDip;
        _hookOwner = hookOwner;
        _desktopLayer = desktopLayer;
        _cardProc = cardWndProc;
        _marginPx = _marginDip;
    }

    /// <summary>
    /// owner 的尺寸/位置**真正生效之后**的回调，在原生 <c>WndProc</c> 里同步执行
    /// （<c>WM_SIZE</c> / <c>WM_WINDOWPOSCHANGED</c> 处理完之后）。
    /// 用来在系统处理完最大化/还原的同一条消息里补回 DWM chrome ——
    /// 拖到 Avalonia 的属性回调或定时器里都会晚一帧，那就是肉眼可见的「闪一下系统圆角/阴影」。
    /// </summary>
    internal Action? OwnerBoundsChanged;

    /// <summary>
    /// owner 从「最小化 / 被隐藏」恢复可见时回调（在原生 <c>WndProc</c> 里同步触发）。
    ///
    /// 为什么要这个：Win + D / 任务栏还原之后 DWM 会丢掉窗口的内容表面，
    /// 而 Avalonia 自己不知道要重绘，屏幕上就只剩底板的模糊背景。
    /// 靠 250ms 定时器轮询「刚才是不是被藏了」有时序漏洞（连按两次 Win+D 太快就采样不到），
    /// 这里在系统真正还原窗口的同一条消息里补一次通知，调用方负责转回 UI 线程做强制重绘。
    /// 回调方**不要**在原生路径上碰 Avalonia 属性 API，必须 <c>Dispatcher.UIThread.Post</c>。
    /// </summary>
    internal Action? OwnerRestored;

    /// <summary>上一轮 owner 是否处于最小化，用来把「最小化 → 还原」这一次变化挑出来。</summary>
    private bool _ownerIconic;

    private const uint WM_ACTIVATE = 0x0006;

    /// <summary>原生合成层是否可用（DLL 缺失/加载失败时退回旧窗口级 ACCENT 通道）。</summary>
    internal static bool Available
    {
        get
        {
            if (_availabilityChecked) return _available;
            _availabilityChecked = true;
            try
            {
                // 真调一次导出函数：单文件发布时原生库是运行时解压加载的，只有真解析才算数。
                // 传空 handle 必得 E_INVALIDARG，能拿到这个返回值就说明库和入口点都在。
                _available = ZonexGlassResize(nint.Zero, 1f, 1f) == unchecked((int)0x80070057);
            }
            catch (DllNotFoundException)
            {
                _available = false;
            }
            catch (EntryPointNotFoundException)
            {
                _available = false;
            }
            catch
            {
                _available = false;
            }
            return _available;
        }
    }

    /// <summary>
    /// 跟随面板同步位置/尺寸/圆角/染色，并在 <paramref name="owner"/> 正下方重排 Z 序。
    /// 参数都是物理像素（<paramref name="scale"/> 为 DPI 缩放，合成层需要 DIP）。
    /// </summary>
    internal bool Sync(nint owner, int x, int y, int width, int height, double scale, int radiusDip, uint tint)
    {
        // 方案④：桌面分区在第一次同步时注册到全局单例；注册失败就整体走旧路径
        if (_desktopLayer && DesktopGlassLayer.Enabled && !_unifiedTried)
        {
            _unifiedTried = true;
            var layer = DesktopGlassLayer.Instance;
            if (layer.TryCreate())
            {
                _panelId = layer.Register();
                _unified = _panelId >= 0;
            }
            if (!_unified)
                AppLog.Warn("fenceglass", "[GlassLayer] 分区注册失败，改用独立宿主窗口");
        }
        if (_unified) return SyncUnified(owner, x, y, width, height, radiusDip);

        if (owner == nint.Zero || width <= 0 || height <= 0) { Hide(); return false; }
        if (scale <= 0.1) scale = 1.0;
        // 从「收起」变回「显示」：DWM 在隐藏期间丢了 backdrop 采样，显示后必须重采一次，
        // 否则玻璃停在隐藏前的最后一帧（隐藏前可能正被别的窗口盖着 → 背景里是别人的画面）。
        var wasHidden = !_shown;

        var groupOwner = _desktopLayer
            ? Win32.FindWindowW("Progman", null)
            : Win32.GetWindowLongPtrW(owner, Win32.GWL_HWNDPARENT);
        // 弹窗绝不能把宿主挂到桌面带（Progman）里：那样宿主会被系统按「桌面层」重排，
        // Win+D 还原时直接排到弹窗上面，屏幕只剩一块模糊背景。弹窗继承到的 Progman owner
        // 一律当「无 owner」处理（正常弹窗的 owner 是它的父弹窗，不会是 Progman）。
        if (!_desktopLayer && groupOwner != nint.Zero
            && groupOwner == Win32.FindWindowW("Progman", null))
            groupOwner = nint.Zero;
        // ⚠️ 不要退化成「owned by 弹窗」：owned 窗口被系统强制排在 owner **上方**，
        // 底板就会盖住弹窗内容（表现是「只剩一块模糊背景，内容没了」）。
        // 弹窗无 owner 时宿主保持独立顶层窗，靠 owner 的 WndProc + 看门狗同步显隐与 Z 序。
        EnsureWindow(groupOwner);
        if (_hwnd == nint.Zero) return false;

        var margin = (int)Math.Round(_marginDip * scale);
        _marginPx = margin;

        // 拖动/缩放时靠原生侧跟随（见 TrackOwner），不指望 Avalonia 的 Position 属性及时更新；
        // 已经自装 WndProc 的窗口（分区）由调用方转发 FollowOwnerMove
        if (_hookOwner) TrackOwner(owner);

        // ⭐ 几何去重：矩形没变就绝不再 SetWindowPos。
        // 每次 SetWindowPos 都会让 DWM 丢掉这个窗口的 HostBackdropBrush 采样并重新采一次；
        // 若重采正好落在别的窗口最大化/还原的过渡帧上，那一帧就被永久冻进分区背景——
        // 用户看到的就是「我最大化一个软件，根本没碰桌面，某个分区背景却糊成了它的画面」。
        // Sync 被位置事件/让位动画/落位补拍反复调用，以前每次都无条件 SetWindowPos，
        // 等于每分钟上百次强制重采，撞上过渡帧几乎是必然。
        var wantX = x - margin;
        var wantY = y - margin;
        var wantW = width + margin * 2;
        var wantH = height + margin * 2;
        bool geomSame = Win32.GetWindowRect(_hwnd, out var curRect)
            && curRect.Left == wantX && curRect.Top == wantY
            && curRect.Right - curRect.Left == wantW
            && curRect.Bottom - curRect.Top == wantH;

        if (wasHidden || !geomSame)
        {
            // 弹窗宿主：owner 正下方，跟它一起进退。
            // 桌面分区宿主：全部卡片之下的锚点正下方（见 InsertAfterOf）——宿主背后只允许是壁纸。
            // NOOWNERZORDER：只改宿主自己的 Z，禁止系统联动抬/压整条 owner 组（Pane 实证）
            Win32.SetWindowPos(_hwnd, InsertAfterOf(owner), wantX, wantY, wantW, wantH,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW | Win32.SWP_NOOWNERZORDER);
            // 几何或显隐变了，旧的落位目标已过期 → 守卫可以重新尝试
            ResetRepinGuard();
        }
        _shown = true;

        // 从「保留窗口、摘掉内容」的状态恢复：窗口一直可见，采样没丢，直接打开内容即可，
        // 千万不要在这里再 Reassert/重采（那正是「先亮一帧再闪进模糊」的来源）。
        if (_contentSuppressed)
        {
            _contentSuppressed = false;
            if (_handle != nint.Zero) try { ZonexGlassSetContentVisible(_handle, 1); } catch { }
        }

        var wDip = (float)(width / scale);
        var hDip = (float)(height / scale);

        bool sizeChanged = Math.Abs(wDip - _lastWDip) > 0.5f || Math.Abs(hDip - _lastHDip) > 0.5f;

        if (_handle == nint.Zero)
        {
            var hr = ZonexGlassAttach(_hwnd, radiusDip, _blurDip, _marginDip, out _handle);
            if (hr < 0 || _handle == nint.Zero)
            {
                _handle = nint.Zero;
                // 静默失败太坑（症状是「阴影/毛玻璃整个没了」却查不到原因），必须留痕
                AppLog.Warn("embed", $"[GlassHost] 合成层挂载失败 hr=0x{hr:X8} blur={_blurDip} margin={_marginDip}");
                return false;
            }
            _radiusDip = radiusDip;
            _lastWDip = wDip;
            _lastHDip = hDip;
            ZonexGlassResize(_handle, wDip, hDip);
        }
        else
        {
            if (radiusDip != _radiusDip)
            {
                _radiusDip = radiusDip;
                ZonexGlassSetRadius(_handle, radiusDip);
            }
            // 位置每帧都会同步（拖动/让位动画），尺寸没变就别再打扰合成层
            if (sizeChanged)
            {
                _lastWDip = wDip;
                _lastHDip = hDip;
                ZonexGlassResize(_handle, wDip, hDip);
            }
        }

        if (tint != _tint)
        {
            _tint = tint;
            ZonexGlassSetTint(_handle, tint, 0);
        }

        // 投影参数：第一次挂上、或用户改了滑条之后才写一次（SetShadow 里已做去重）
        if (_shadowDirty)
        {
            _shadowDirty = false;
            ZonexGlassSetShadow(_handle, _shadowOpacity, _shadowBlur, _shadowOffsetY);
        }

        _owner = owner;

        // 放在最后：此时合成层已按新尺寸/参数挂好，重采才是对最终矩形采的
        if (wasHidden) RefreshBackdropSampling("resync");
        LogMaterialMode();
        return true;
    }

    /// <summary>
    /// 方案④路径：不建自己的 HWND、不做任何 SetWindowPos，只把矩形/外观参数交给统一层。
    /// 这样拖动、折叠、让位动画全程只改合成属性，DWM 不会重采 backdrop。
    /// </summary>
    private bool SyncUnified(nint owner, int x, int y, int width, int height, int radiusDip)
    {
        if (owner == nint.Zero || width <= 0 || height <= 0) { Hide(); return false; }
        var layer = DesktopGlassLayer.Instance;
        layer.ZAnchorSource ??= ZAnchorSource;
        // 统一层路径没有自己的 HWND，Sync() 前半段的 TrackOwner 走不到；
        // 这里补上，让 hookOwner:true 的窗口（桌面组件）拖动时由 FollowOwnerMove 只改合成属性。
        // 分区是 hookOwner:false，自带 WndProc 转发，不受影响。
        if (_hookOwner) TrackOwner(owner);
        // owner 传下去做可见性对账：卡片窗被系统最小化/cloak 时玻璃必须跟着收，
        // 否则就是用户看到的「只剩一块模糊空卡」
        var ok = layer.SyncPanel(_panelId, owner, x, y, width, height, radiusDip,
            _blurDip, _marginDip, _shadowOpacity, _shadowBlur, _shadowOffsetY,
            applyGlobal: ApplyGlobalAppearance);
        _owner = owner;
        _shown = ok;
        _shadowDirty = false;
        return ok;
    }

    /// <summary>
    /// 卡片窗从最小化 / Win+D 还原时由 FenceWindow 的 WndProc 转发进来。
    /// 统一层模式：还原层窗口 + 强制重采 + 强制 Z 落位（旧路径走 OwnerRestored 事件）。
    /// </summary>
    internal void OnCardWindowRestored()
    {
        if (_unified) { DesktopGlassLayer.Instance.NotifyOwnerRestored(_panelId); return; }
        NotifyOwnerRestored();
    }

    /// <summary>
    /// 设置投影参数：浓度 0–1、模糊半径（DIP，0–64）、纵向偏移（DIP，0–32）。
    /// 只改已有 <c>DropShadow</c> 的属性，不重建合成树，下一帧就生效。
    ///
    /// ⚠️ 投影画在宿主窗口四周外扩的 <see cref="MarginDip"/> 里：模糊 + 偏移超过这个余量时，
    /// 投影会在宿主窗口边缘被硬切。要到极大值只能同时加大 MarginDip（窗口会跟着变大）。
    /// </summary>
    internal void SetShadow(float opacity, float blurDip, float offsetYDip)
    {
        opacity = Math.Clamp(opacity, 0f, 1f);
        blurDip = Math.Clamp(blurDip, 0f, 64f);
        offsetYDip = Math.Clamp(offsetYDip, 0f, 32f);
        if (Math.Abs(opacity - _shadowOpacity) < 0.005f
            && Math.Abs(blurDip - _shadowBlur) < 0.5f
            && Math.Abs(offsetYDip - _shadowOffsetY) < 0.5f)
            return;

        _shadowOpacity = opacity;
        _shadowBlur = blurDip;
        _shadowOffsetY = offsetYDip;
        _shadowDirty = true;

        if (_handle != nint.Zero)
        {
            _shadowDirty = false;
            try { ZonexGlassSetShadow(_handle, _shadowOpacity, _shadowBlur, _shadowOffsetY); }
            catch { _shadowDirty = true; }
        }
    }

    /// <summary>
    /// 设置磨砂材质的模糊半径（DIP，0 = 不画磨砂只画投影）。
    /// 材质在 <c>Attach</c> 时按 blurDip 决定建不建：改值只能销毁合成层，
    /// 下一次 <see cref="Sync"/> 会按新值重新挂上（宿主窗口/Z 序/位置都不动）。
    /// </summary>
    internal void SetBlur(float blurDip)
    {
        blurDip = Math.Clamp(blurDip, 0f, 64f);
        if (Math.Abs(blurDip - _blurDip) < 0.5f) return;
        _blurDip = blurDip;
        if (_handle != nint.Zero)
        {
            ZonexGlassDetach(_handle);
            _handle = nint.Zero;
            // 新合成树的投影/染色是原生默认值：强制下一次 Sync 重写，别让缓存状态把参数弄丢
            _shadowDirty = true;
            _tint = 0xFFFFFFFF;
        }
    }

    /// <summary>
    /// 重新向 DWM 申请「宿主背景采样」，**不销毁合成树**。
    ///
    /// 窗口失活、<c>SC_MOVE</c>/<c>SC_SIZE</c> 系统模态循环结束、或 guard 修样式后跑了
    /// <c>FRAMECHANGED</c>，DWM 都会停掉宿主窗口的 host backdrop 采样：合成层还在，
    /// 但画刷采不到背景 → 毛玻璃整块消失（窗口「能看穿但不模糊」）。
    /// 这个属性只能写不能读，没生效时读回也看不出，只能重写一次逼 DWM 重新评估；
    /// 0→1 在同一调用里完成、DWM 不会在中途插帧，所以不闪。
    /// （不要「销毁重挂合成树」——那等于把整块玻璃拆掉重建，肉眼会看到一次黑闪。）
    /// </summary>
    internal void ReassertHostBackdropBrush()
    {
        if (_unified) { DesktopGlassLayer.Instance.Reassert(); return; }
        if (_hwnd == nint.Zero || _handle == nint.Zero) return;
        SetDwm(_hwnd, DWMWA_USE_HOSTBACKDROPBRUSH, 0);
        SetDwm(_hwnd, DWMWA_USE_HOSTBACKDROPBRUSH, 1);
    }

    /// <summary>
    /// 「不显示，但保留窗口与 backdrop 采样」：只关掉合成层的内容（磨砂 / 透传 / 投影），
    /// **不 SW_HIDE**。
    ///
    /// 为什么不能直接 Hide：窗口一隐藏，DWM 就丢掉这个窗口的 HostBackdropBrush 采样，
    /// 再显示要 1~2 帧才重新采到背景 —— 组件/分区在显隐联动里回来时就是「先亮、再闪出模糊」。
    /// 窗口留着、内容摘掉，用户看不到任何残留；恢复时打开内容即带磨砂。
    /// 宿主窗口本身是 LAYERED + TRANSPARENT，无内容时完全不可见，也不吃点击。
    /// </summary>
    /// <summary>
    /// 卡窗还 cloaked 时先把宿主摆好、采好背景，再把内容压住。
    ///
    /// 之后「解除 cloak + PrimeForShow」在同一拍里发生：磨砂和卡面一起上屏。
    /// 不这么做的话，第一次 Sync 要等到卡解除 cloak 之后才跑，DWM 那时才开始采背景，
    /// 顺序就成了「卡先亮一帧（没有磨砂）→ 下一帧才糊上」，也就是用户看到的闪一下。
    /// 内容先压住是为了避免反过来：卡还不可见时，宿主带着磨砂单独在桌面上亮一块。
    /// </summary>
    internal bool SeatBehind(nint owner, double scale, double radiusDip)
    {
        if (owner == nint.Zero) return false;
        try
        {
            if (_unified) return false;
            if (!Win32.GetWindowRect(owner, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return false;
            var ok = Sync(owner, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, scale,
                (int)Math.Round(radiusDip), 0u);
            if (ok) SuppressContent();
            return ok;
        }
        catch { return false; }
    }

    /// <summary>
    /// 「显示桌面」预热：在卡片窗解除 cloak **之前**把磨砂打开。
    ///
    /// 独立宿主路径：只把内容开回来（窗口一直在屏上、采样没丢），绝不 SetWindowPos / 重采。
    /// 统一层路径：直接把 panel 打开（cloak 期间卡片 rect 仍可取）。
    /// 否则显示顺序就是「卡片先上屏（无磨砂）→ 下一次 Sync 才开磨砂」= 用户看到的一下闪。
    /// </summary>
    internal void PrimeForShow(nint owner)
    {
        if (owner == nint.Zero) return;
        try
        {
            if (_unified)
            {
                if (!Win32.GetWindowRect(owner, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return;
                DesktopGlassLayer.Instance.PrimePanelForShow(_panelId, owner,
                    r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                return;
            }
            if (_contentSuppressed)
            {
                _contentSuppressed = false;
                if (_handle != nint.Zero) ZonexGlassSetContentVisible(_handle, 1);
            }
        }
        catch { }
    }

    /// <summary>
    /// 无条件把内容打开（比 <see cref="PrimeForShow"/> 干脆：不看抑制标记、不需要 owner 句柄）。
    ///
    /// 拖动降载等「临时摘内容」的收尾走这里：中途万一撞上 60s 回收把宿主真隐藏过，
    /// 抑制标记已被清掉，PrimeForShow 就什么也不做，玻璃会一直停在「没有内容」的状态。
    /// 这里直接写一次原生开关，窗口可见时同一拍生效。
    /// </summary>
    internal void EnsureContentVisible()
    {
        if (_unified) return;
        _contentSuppressed = false;
        if (_handle == nint.Zero) return;
        try { ZonexGlassSetContentVisible(_handle, 1); } catch { }
    }

    internal void SuppressContent()
    {
        if (_unified) { DesktopGlassLayer.Instance.HidePanel(_panelId); _shown = false; return; }
        if (_hwnd == nint.Zero || _handle == nint.Zero || !_shown)
        {
            Hide();
            return;
        }
        if (_contentSuppressed) return;
        try { ZonexGlassSetContentVisible(_handle, 0); } catch { }
        _contentSuppressed = true;
        // 刻意不动 _shown：窗口确实还是可见的。下一次 Sync 不该再 SetWindowPos + SWP_SHOWWINDOW
        // （那会逼 DWM 重采，正好是要避免的那一帧）。
        ScheduleSuppressReclaim();
    }

    /// <summary>「保留窗口」太久（长时间隐藏桌面 / 全屏游戏）就真隐藏，别让空宿主一直占合成层。</summary>
    private const int SuppressReclaimMs = 60_000;

    private int _suppressGen;

    private void ScheduleSuppressReclaim()
    {
        var gen = ++_suppressGen;
        _ = System.Threading.Tasks.Task.Delay(SuppressReclaimMs).ContinueWith(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (gen != _suppressGen || !_contentSuppressed || _hwnd == nint.Zero) return;
                AppLog.Info("fenceglass",
                    $"[GlassHost] 收起满 {SuppressReclaimMs / 1000}s → 真正隐藏（释放合成层，下次显示重采）");
                Hide();
            });
        });
    }

    internal void Hide()
    {
        if (_unified) { DesktopGlassLayer.Instance.HidePanel(_panelId); _shown = false; return; }
        if (_hwnd != nint.Zero) Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
        _shown = false;
        _contentSuppressed = false;
        ResetRepinGuard();
    }

    /// <summary>
    /// 宿主跟随 owner 一起进 / 出**系统置顶带**（便签「置顶」用）。
    ///
    /// 为什么宿主必须跟着：宿主是独立顶层窗（owner 被有意归零，见 Sync 里 groupOwner 的注释）。
    /// 卡片进了置顶带、宿主还留在普通带，任何普通应用窗口都能插进两者之间 →
    /// 玻璃立刻被应用画面盖住（表现是「置顶的便签没磨砂 / 没投影了」）。
    ///
    /// 记住状态：<see cref="RepinBehindOwner"/> 之后要按 <c>_topmost</c> 重新选锚点，
    /// 否则一次普通落位（SetWindowPos(host, owner) 里 owner 是非置顶的）就会把宿主
    /// 从置顶带里踢出来。幂等：已达标时零写入（每次 SetWindowPos 都逼 DWM 重采 backdrop）。
    /// </summary>
    internal void SetTopmost(bool on)
    {
        if (_unified) return;
        _topmost = on;
        if (_hwnd == nint.Zero) return;

        var ex = Win32.GetWindowLongPtrW(_hwnd, Win32.GWL_EXSTYLE);
        bool flagged = ((nuint)ex & Win32.WS_EX_TOPMOST) != 0;

        if (!on)
        {
            if (!flagged) return;
            Win32.SetWindowLongPtrW(_hwnd, Win32.GWL_EXSTYLE,
                (nint)((nuint)ex & ~(nuint)Win32.WS_EX_TOPMOST));
            Win32.SetWindowPos(_hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
            ResetRepinGuard();
            return;
        }

        if (flagged) return;
        Win32.SetWindowLongPtrW(_hwnd, Win32.GWL_EXSTYLE,
            (nint)((nuint)ex | Win32.WS_EX_TOPMOST));
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        ResetRepinGuard();
    }

    /// <summary>置顶态查询（便签取消置顶回滚时要判断宿主是否还在置顶带里）。</summary>
    internal bool IsTopmost => _topmost;

    /// <summary>
    /// 强制重新采样「宿主背后的实时画面」—— 被别的窗口完全盖住过之后必须调。
    ///
    /// 窗口被最大化程序/全屏游戏完全遮挡时，DWM 会停掉 host backdrop 的采样；重新露出来后
    /// 它**不保证**重新采，玻璃就冻结在「被盖住那一刻」的画面。症状是「某个分区背景莫名其妙
    /// 变亮/变花，像糊了别的窗口的内容」——而且只有个别分区（各窗口被降级的时机不同），
    /// 再拖一下窗口又好了，所以很难抓。
    ///
    /// 做法：重建材质 effect 图（= 换一张新的 <c>HostBackdropBrush</c>，逼 DWM 丢掉旧采样）+
    /// 重申 <c>DWMWA_USE_HOSTBACKDROPBRUSH</c>（这个属性只能写不能读，重写一次逼 DWM 重新评估）。
    /// 两者都不销毁合成树，同一帧合成，不会黑闪。
    /// </summary>
    internal void RefreshBackdropSampling(string reason)
    {
        // 统一层路径：这层窗口一直在屏上（双击显隐只关 panel），补采就是整层闪一下 →
        // 「双击显示先没磨砂、闪一下才磨砂」。只有层真被隐藏过才允许采（见 RefreshBackdropIfLayerHidden）。
        if (_unified) { DesktopGlassLayer.Instance.RefreshBackdropIfLayerHidden(reason); return; }
        if (_hwnd == nint.Zero || _handle == nint.Zero || !_shown) return;
        try
        {
            var hr = ZonexGlassRefreshBackdrop(_handle);
            ReassertHostBackdropBrush();
            var now = Environment.TickCount64;
            if (now - _lastRefreshLogMs > 1000)
            {
                _lastRefreshLogMs = now;
                AppLog.Info("fenceglass", $"[GlassHost] 重采背景 ({reason}) hr=0x{hr:X8}");
            }
        }
        catch
        {
            // 重采失败最多背景停一帧，绝不能把调用方的显示/恢复流程搞崩
        }
    }

    private long _lastRefreshLogMs;
    private bool _degradedLogged;
    private bool _modeLogged;

    /// <summary>
    /// 布局定稿：折叠/展开动画走完、尺寸不再变化之后调一次。
    ///
    /// 兜住两件事：
    /// ① C# 侧尺寸同步带 0.5 DIP 去抖，动画最后一帧的微小变化会被吃掉，材质的裁剪矩形可能停在
    ///    比卡片略大的值 → 采样区域变大 → 卡片外的亮壁纸被平均进背景。
    ///    表现就是「一键折叠后，某一条背景明显比邻居亮」，而且它自己不会恢复。
    /// ② 动画期间偶发一次 effect 图创建失败（原生侧留了脏标记），这里补一次重建。
    ///
    /// 只重建效果图、不换采样源，所以不会闪。
    /// </summary>
    /// <summary>
    /// 材质状态自查（每次只记一次）：『背景莫名比邻居亮』= 材质退化成无裁剪，采样域比卡片大一圈。
    /// 0 正常（裁剪靠重建图）/ 3 正常（裁剪走运行时属性，动画期零工厂创建）/ 1 待重试 / 2 无裁剪降级。
    /// </summary>
    private void LogMaterialMode()
    {
        if (_handle == nint.Zero || _modeLogged) return;
        _modeLogged = true;
        var state = ZonexGlassMaterialState(_handle);
        var err = (uint)ZonexGlassLastMaterialError(_handle);
        // 外扩为 0 时「没有裁剪」是正确的：采样域 = 窗口 = 卡片，本来就不需要裁。
        // 只有外扩 > 0（要画投影）才说明材质可能按更大的区域采样（背景偏亮）。
        if (_marginDip <= 0)
        {
            if (state == 4)
                AppLog.Warn("fenceglass", $"[GlassHost] 没有材质（毛玻璃未建出来）lastErr=0x{err:X8}");
            else if (state == 1)
                AppLog.Warn("fenceglass", $"[GlassHost] 材质重建待重试 lastErr=0x{err:X8}");
            else
                AppLog.Info("fenceglass", $"[GlassHost] 材质正常（窗口=卡片，采样域即卡片）state={state}");
            return;
        }
        var desc = state switch
        {
            0 => "正常/裁剪靠重建图",
            1 => "待重试（重建失败，沿用旧裁剪）",
            2 => "无裁剪（合成器不支持单节点以外的图，背景会偏亮）",
            3 => "正常/裁剪走运行时属性",
            _ => "未知",
        };
        var line = $"[GlassHost] 材质状态 {state} {desc} lastErr=0x{err:X8}";
        if (state == 2 || state == 1) AppLog.Warn("fenceglass", line);
        else AppLog.Info("fenceglass", line);
    }

    internal void CommitLayout()
    {
        // 统一层没有「尺寸定稿后重建材质」这一步：位置/尺寸是直接改 visual 属性的，
        // 不存在 Crop 矩形滞后一帧的问题，也不需要补拍。
        if (_unified) return;
        if (_hwnd == nint.Zero || _handle == nint.Zero || !_shown) return;
        try
        {
            ZonexGlassCommitSize(_handle);
            LogMaterialMode();
        }
        catch (Exception ex) { AppLog.Warn("fenceglass", $"[GlassHost] 布局定稿失败 {ex.Message}"); }
    }

    /// <summary>
    /// owner 重新可见（最小化还原 / Win+D 还原 / 从隐藏变可见）时把底板放回来：
    /// 重新压到 owner 下方并重申 host backdrop 采样。底板隐藏过再显示时，
    /// DWM 的合成状态和 Z 序都可能被系统重排过，只靠 <c>ShowWindow</c> 会留下
    /// 「底板在上面 / 采不到背景」的残局。
    /// </summary>
    private void RestoreWithOwner()
    {
        if (_hwnd == nint.Zero || _owner == nint.Zero) return;
        // 最小化/还原动画进行中 owner 还在 iconic 矩形（-32000）：这时候摆底板会把它扔到屏外，
        // 等真正还原后的 Position/Size 同步再摆。
        if (Win32.IsIconic(_owner)) return;
        if (!Win32.GetWindowRect(_owner, out var r) || r.Left <= -30000) return;
        // 注意：这里**不能**用 `if (_shown) return` 早退。Avalonia 的 Activated 常常先跑一遍
        // Sync 把 _shown 置回 true（那时系统还没重排完 Z 序），随后真正的最小化→还原通知到这里
        // 就被早退吃掉 —— 宿主留在系统重排后的位置（弹窗上面），永远回不来。
        // 重挂 + 重申采样都是幂等操作，每次还原都做一遍才可靠。
        _shown = true;
        _repinning = false;
        ResetRepinGuard();
        RequestRepin();
        ReassertHostBackdropBrush();
    }

    /// <summary>
    /// 把宿主重绑到新的 owner 组（传 <c>nint.Zero</c> = 变成独立顶层窗）。
    ///
    /// 弹窗的 owner 是在 <c>Show()</c> **之后**才被解绑的（<see cref="WindowFrontHelper.DetachOwner"/>），
    /// 而宿主窗口在 <c>Show()</c> 里（Opened → Sync）就建好了，必然继承了那个旧 owner。
    /// 旧 owner 一旦是本进程的桌面层窗口，宿主就被钉在桌面带里：Win + D 还原时系统按组重排 Z 序，
    /// 宿主被排到弹窗**上面** —— 用户看到的就是「只剩一块模糊背景，内容没了」。
    /// 弹窗解绑 owner 之后必须调一次这里。
    /// </summary>
    /// <returns>true = 真的改了 owner（调用方应补一次位置同步）。</returns>
    internal bool RebindGroupOwner(nint groupOwner)
    {
        if (_unified) return false;   // 统一层的 owner 由单例自己管
        if (_hwnd == nint.Zero) return false;
        var cur = Win32.GetWindowLongPtrW(_hwnd, Win32.GWL_HWNDPARENT);
        if (cur == groupOwner) return false;

        Win32.SetWindowLongPtrW(_hwnd, Win32.GWL_HWNDPARENT, groupOwner);
        AppLog.Info("chrome", $"[GlassHost] 宿主 owner 重绑 0x{cur:X} → 0x{groupOwner:X}");
        // 换 owner 组之后系统会重排一次 Z：立刻压回弹窗正下方
        _repinning = false;
        ResetRepinGuard();
        RequestRepin();
        ReassertHostBackdropBrush();
        return true;
    }

    /// <summary>
    /// 让宿主窗口跟随 owner 的**每一次**窗口位置更新。
    ///
    /// 只靠 Avalonia 的 <c>Position</c> 属性是不够的：拖动标题栏走的是系统 modal move loop（<c>SC_MOVE</c>），
    /// 位移由 DefWindowProc 的内部循环驱动，Avalonia 要等消息回到 UI 线程才更新 <c>Position</c>，
    /// 我们再拿它做一次 <c>SetWindowPos</c> 又要跨一帧 —— 玻璃窗口明显落后，
    /// 表现就是「毛玻璃和窗体分离」。所以直接在 owner 的 WndProc 里截 <c>WM_WINDOWPOSCHANGING</c>：
    /// 那里拿到的是**即将生效**的矩形，先把宿主窗口挪过去，再放行主窗口自己的移动，
    /// 两者在同一条消息里完成 → 同一帧合成，不差帧。
    /// </summary>
    internal unsafe void TrackOwner(nint owner)
    {
        // 分区（hookOwner:false）自带 WndProc，统一层位置跟随由它转发 FollowOwnerMove。
        if (_unified && !_hookOwner) return;
        if (_hookedOwner == owner && _ownerOldProc != nint.Zero)
        {
            // ⭐⭐ Avalonia 合成会反复把 GWL_WNDPROC 换回自己的过程 —— 钩子名义上还在、
            // 实现已被换掉时这里以前直接 return，拦截从此**静默失效**：组件拖动时玻璃矩形
            // 不跟随（旧位置留下模糊残影）、被动改序无人拦（拖拽中 Z 序被写乱，落点点击
            // 交互被分区吃掉）。分区侧由 zGuard 的 EnsureWndProcInstalled 兜底，这里补同款自愈。
            var mine = Marshal.GetFunctionPointerForDelegate(_ownerProc!);
            var cur = Win32.GetWindowLongPtrW(owner, Win32.GWL_WNDPROC);
            if (cur == mine) return;
            if (cur != nint.Zero)
            {
                _ownerOldProc = cur;   // 当前的实现当作新的 original 重挂
                Win32.SetWindowLongPtrW(owner, Win32.GWL_WNDPROC, mine);
                AppLog.Info("fenceglass", $"[GlassHost] owner WndProc 被重置，已重挂 hwnd=0x{owner:X}");
                return;
            }
            UnhookOwner();   // 链断了：走下面的完整重挂
        }
        else
        {
            UnhookOwner();
        }
        if (owner == nint.Zero) return;

        // ⭐⭐⭐ 桌面组件（卡片 WndProc）走专用消息链。
        // 不能复用下面那套为**独立弹窗宿主**写的消息链：Hide / RestoreWithOwner /
        // ReassertHostBackdropBrush 会去动弹窗宿主的 HWND，语义不对。
        // 也不能像旧版那样 `if (_unified) return` 直接跳过 —— 那样组件拖动时玻璃矩形
        // 不跟随、Avalonia 每帧写 Position 附带的被动改序无人拦、Win+D 归位无人接。
        if (_unified || _cardProc)
        {
            InstallUnifiedCardWndProc(owner);
            return;
        }

        _ownerProc = (h, m, w, l) =>
        {
            try
            {
                // 系统非客户区一律不画：任务栏最小化→还原时 Avalonia 会把 CAPTION/BORDER 样式位
                // 算回来，系统就按样式画一圈边框叠在自绘圆角上（「双边框 / 双圆角 / 像叠了系统窗体」）。
                // 样式位由 UiSurfaceService.EnsureNoSystemChrome 清；这里再截住 NC 绘制做双保险。
                if (m == Win32.WM_NCPAINT) return 0;
                if (m == Win32.WM_NCACTIVATE) return 1;

                // 底板跟随 owner 的可见状态：最小化 / 被隐藏时一起收起来，
                // 否则窗口藏了、底板还画着 → 屏幕上只剩一块模糊背景（内容没了）。
                if (m == Win32.WM_SIZE)
                {
                    var sizeType = (int)((ulong)w & 0xFFFF);
                    if (sizeType == Win32.SIZE_MINIMIZED)
                    {
                        Hide();
                        _ownerIconic = true;
                    }
                    else
                    {
                        var wasIconic = _ownerIconic;
                        _ownerIconic = false;
                        RestoreWithOwner();
                        // 最小化 → 还原：内容表面已被 DWM 丢掉，通知调用方强制重绘一次
                        if (wasIconic) NotifyOwnerRestored();
                    }
                }
                else if (m == Win32.WM_SHOWWINDOW)
                {
                    if (w == 0) Hide();
                    else RestoreWithOwner();
                }

                // 全窗口 = 客户区。窗口带着 WS_THICKFRAME（SC_SIZE 缩放要用），任务栏最小化→还原时
                // 系统会重算 frame，把客户区内缩一圈（NC 没画出来、几何却缩了）——内容整体缩进，
                // 缝隙露出底下模糊带，看起来就是「叠了系统窗体 + 双边框」。这里直接回 0，
                // 让 frame 内缩永远要不回来；SC_SIZE 不受影响（拖边缩放走的是 SC_SIZE 消息循环）。
                if (m == Win32.WM_NCCALCSIZE && w != (nuint)nint.Zero)
                {
                    if (l != nint.Zero)
                    {
                        try
                        {
                            var p = Marshal.PtrToStructure<Win32.NCCALCSIZE_PARAMS>(l);
                            var win = p.rgrc0;
                            var cli = p.rgrc1;
                            var dw = win.Right - win.Left - (cli.Right - cli.Left);
                            var dh = win.Bottom - win.Top - (cli.Bottom - cli.Top);
                            // 系统提议的客户区比窗口小 → 有 frame 内缩（我们要拦的就是它）
                            if (dw > 0 || dh > 0)
                                AppLog.Info("chrome",
                                    $"NCCALCSIZE 提议内缩 dw={dw} dh={dh} win=({win.Left},{win.Top},{win.Right - win.Left},{win.Bottom - win.Top}) " +
                                    $"cli=({cli.Left},{cli.Top},{cli.Right - cli.Left},{cli.Bottom - cli.Top})");
                        }
                        catch { }
                    }

                    // ⚠️ 最小化时必须交回系统，不能回 0。
                    // 最小化时系统传的 rrgc1 是 (-32000,-32000,160,28)（最小化占位客户区），
                    // 回 0 等于把「最小化客户区」硬改成整窗尺寸 —— 系统记录的窗口状态与客户区几何
                    // 就此错位，还原（Win + D 连按第二次）时窗口内容表面拿不到正确的尺寸，
                    // Avalonia 也不认为布局发生了变化、不会重新提交内容 →
                    // 屏幕上看就只剩底板那层模糊，弹窗内容一片空白。
                    if (!Win32.IsIconic(h)) return 0;
                }

                if (m == Win32.WM_WINDOWPOSCHANGING && l != nint.Zero)
                {
                    // 统一层：只把新矩形推给合成层（不碰 HWND、不重采）
                    if (_unified) FollowOwnerMove(l);
                    else if (_hwnd != nint.Zero && _shown) FollowPendingMove(h, l);
                }
            }
            catch
            {
                // 跟随失败最多是位置不同步，绝不能把 owner 的消息处理搞崩
            }

            var result = _ownerOldProc != nint.Zero
                ? Win32.CallWindowProcW(_ownerOldProc, h, m, w, l)
                : Win32.DefWindowProcW(h, m, w, l);

            // 必须排在原过程之后：系统是在处理最大化/还原的过程中动 chrome 的，早于它就等于白写。
            // 这里与窗口真正生效处于同一条消息，合成时属性已是最终值 → 不闪。
            if (m == Win32.WM_SIZE || m == Win32.WM_WINDOWPOSCHANGED)
            {
                try { OwnerBoundsChanged?.Invoke(); }
                catch
                {
                    // 补 chrome 失败最多多一圈系统圆角，绝不能把 owner 的消息处理搞崩
                }

                try
                {
                    // 记录「窗口矩形 vs 客户区」的实际差值：差值 >0 就是 frame 把客户区内缩了
                    // （内容整体缩进、缝隙露出毛玻璃底板 → 用户看到的「双边框 + 叠了系统窗体」）。
                    if (Win32.GetWindowRect(h, out var wr) && Win32.GetClientRect(h, out var cr))
                    {
                        var tl = new Win32.POINT { X = cr.Left, Y = cr.Top };
                        var br = new Win32.POINT { X = cr.Right, Y = cr.Bottom };
                        Win32.ClientToScreen(h, ref tl);
                        Win32.ClientToScreen(h, ref br);
                        var insL = tl.X - wr.Left;
                        var insT = tl.Y - wr.Top;
                        var insR = wr.Right - br.X;
                        var insB = wr.Bottom - br.Y;
                        if (insL > 0 || insT > 0 || insR > 0 || insB > 0)
                            AppLog.Info("chrome",
                                $"{m:X} 客户区内缩 L={insL} T={insT} R={insR} B={insB} " +
                                $"win={wr.Right - wr.Left}x{wr.Bottom - wr.Top} client={br.X - tl.X}x{br.Y - tl.Y} " +
                                $"style=0x{Win32.GetWindowLongW(h, -16):X}");
                    }
                }
                catch { }
            }

            // 激活/失活：必须排在原过程之后。DWM 是在系统处理 WM_ACTIVATE（含 Avalonia 自己那层）
            // 的过程中/之后才停掉 host backdrop 采样，早于它就等于白写。
            // 这里只做 DWM 属性重写（不碰任何 Avalonia API，见下方 OwnerBoundsChanged 的警告）。
            if (m == WM_ACTIVATE)
            {
                try
                {
                    // 失活时系统会把 owner 置顶，宿主可能被插到它下面 → 先补重挂再重申采样
                    if (!IsBehindAllCards()) RequestRepin();
                    ReassertHostBackdropBrush();
                }
                catch
                {
                    // 重申失败最多是毛玻璃慢一拍接回来，绝不能把 owner 的消息处理搞崩
                }
            }

            return result;
        };

        // 先 Get 再 Set：Win32.SetWindowLongPtrW 在本项目的声明被 [MarshalAs(Bool)] 化成了 bool 返回，
        // 拿不到旧过程地址（与 FenceWindow / MainView 的用法一致）
        var oldProc = Win32.GetWindowLongPtrW(owner, Win32.GWL_WNDPROC);
        Win32.SetWindowLongPtrW(owner, Win32.GWL_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_ownerProc));
        _ownerOldProc = oldProc;
        _hookedOwner = oldProc != nint.Zero ? owner : nint.Zero;
    }

    /// <summary>
    /// 桌面卡片（桌面组件）专用 WndProc：把分区 FenceWindow 那套
    /// Z 序 / 显隐守卫按桌面卡片语义复刻一遍。统一层模式下全程不碰任何独立宿主 HWND；
    /// 独立宿主模式下额外守一条「玻璃宿主必须紧贴本窗正下方」。
    ///
    /// 为什么必须单独一条：分区自己装了 WndProc（hookOwner:false），组件没有；少了这层，
    /// 组件就不在玻璃层同一个守卫体系里 —— 系统一归位它，玻璃就被熄灭或采进脏缓存，
    /// 用户看到的就是「背景模糊老是丢、和分区不一致」。
    /// </summary>
    private void InstallUnifiedCardWndProc(nint owner)
    {
        const int MA_ACTIVATE = 1;
        const int MA_NOACTIVATE = 3;
        _ownerProc = (h, m, w, l) =>
        {
            try
            {
                // 系统 NC 一律不画：窗口级圆角/描边回来就会和合成层圆角叠成双圆角
                if (m == Win32.WM_NCPAINT) return nint.Zero;
                if (m == Win32.WM_NCACTIVATE) return (nint)1;

                // Win+D：拒绝最小化（组件是桌面的一部分，最小化会引发整条桌面带重排）
                if (m == Win32.WM_SYSCOMMAND
                    && (int)((long)w & Win32.SC_MASK) == Win32.SC_MINIMIZE)
                    return nint.Zero;

                // 平时点击绝不激活：激活会把整条 owner=Progman 的窗口组抬到普通应用之上。
                // 例外：正在输入文字（有 TextBox 拿着焦点）时放行 MA_ACTIVATE —— 那时用户就是要键盘，
                // 点击必须走正常激活，否则窗口停在「前台但非 active」，IME 候选窗会掉到屏幕右上角。
                if (m == Win32.WM_MOUSEACTIVATE)
                    return (nint)(DesktopEmbedService.IsTextEditing(h) ? MA_ACTIVATE : MA_NOACTIVATE);

                // 程序化激活兜底（仅统一层）：压回「玻璃层正上方」那一格，绝不 HWND_BOTTOM / 传层句柄
                if (m == Win32.WM_SETFOCUS && _unified)
                {
                    var anchor = DesktopGlassLayer.Instance.AboveLayerInsertAfter();
                    if (anchor != nint.Zero)
                    {
                        using (DesktopEmbedService.AllowZOrderChange())
                            Win32.SetWindowPos(h, anchor, 0, 0, 0, 0,
                                Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE
                                | Win32.SWP_NOOWNERZORDER);
                    }
                    return nint.Zero;
                }

                // 玻璃矩形同帧跟随 + 拦掉 Avalonia 写 Position 时附带的被动改序
                if (m == Win32.WM_WINDOWPOSCHANGING && l != nint.Zero)
                {
                    try { FollowOwnerMove(l); } catch { }
                    if (!DesktopEmbedService.ZOrderChangeAllowed)
                        DesktopEmbedService.SuppressZOrderChange(l);
                }

                if (m == Win32.WM_WINDOWPOSCHANGED)
                {
                    try
                    {
                        if (_unified)
                        {
                            var layer = DesktopGlassLayer.Instance;
                            // Win+D 风暴期：3s 巡检来不及，必须在 DWM 合成之前同步抬回
                            if (layer.ShellFloodRecent() && layer.IsCardBelowLayer(h))
                            {
                                var anchor = layer.AboveLayerInsertAfter();
                                if (anchor != nint.Zero)
                                    using (DesktopEmbedService.AllowZOrderChange())
                                        Win32.SetWindowPos(h, anchor, 0, 0, 0, 0,
                                            Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE
                                            | Win32.SWP_NOOWNERZORDER);
                            }
                            layer.RequestZWatch();
                        }
                        else if (_shown && !_repinning && _owner != nint.Zero)
                        {
                            // 独立宿主：玻璃必须始终紧贴本窗正下方，宿主被翻上来 = 玻璃盖住卡片本体。
                            // FollowOwnerMove 在 CHANGING 里已同帧挪过宿主，正常拖动这里判真、零写入。
                            if (!IsDirectlyBelowOwner)
                                RequestRepin();
                        }
                    }
                    catch { }
                }

                // 最小化 / 隐藏 → 还原：玻璃要重采 + 强制落位，组件再跑一次几何对账。
                // ⚠️ WM_SIZE(SIZE_RESTORED) 在普通尺寸变化时也会发，所以 Sync 对账只在
                // 「确有隐藏史」时做；NotifyOwnerRestored 内部另有 10s 隐藏史闸门 + 500ms 去重。
                if (m == Win32.WM_SIZE)
                {
                    var sizeType = (int)((ulong)w & 0xFFFF);
                    if (sizeType == Win32.SIZE_MINIMIZED) _ownerIconic = true;
                    else
                    {
                        var wasIconic = _ownerIconic;
                        _ownerIconic = false;
                        try { OnCardWindowRestored(); } catch { }
                        if (wasIconic) PostOwnerRestored();
                    }
                }
                else if (m == Win32.WM_SHOWWINDOW)
                {
                    if (w == 0) Hide();
                    else
                    {
                        try { OnCardWindowRestored(); } catch { }
                        PostOwnerRestored();
                    }
                }
            }
            catch
            {
                // 守卫失败最多是玻璃慢一拍，绝不能把 owner 的消息链搞崩
            }

            return _ownerOldProc != nint.Zero
                ? Win32.CallWindowProcW(_ownerOldProc, h, m, w, l)
                : Win32.DefWindowProcW(h, m, w, l);
        };

        var oldProc = Win32.GetWindowLongPtrW(owner, Win32.GWL_WNDPROC);
        Win32.SetWindowLongPtrW(owner, Win32.GWL_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_ownerProc));
        _ownerOldProc = oldProc;
        _hookedOwner = oldProc != nint.Zero ? owner : nint.Zero;
    }

    /// <summary>
    /// 周期性自愈入口：确认 owner 的卡片 WndProc 还挂在链上。
    /// Avalonia 合成会把 GWL_WNDPROC 换回自己的过程（分区 zGuard 同款问题），
    /// 组件没有 zGuard，靠自己的 tick 定时器低频调这里；被换掉时 TrackOwner 会重挂。
    /// </summary>
    internal void EnsureOwnerHook()
    {
        if (!_hookOwner || _owner == nint.Zero) return;
        TrackOwner(_owner);
    }

    /// <summary>还原通知回到 UI 线程：原生 WndProc 路径上不许碰 Avalonia 属性 API。</summary>
    private void PostOwnerRestored()
    {
        var cb = OwnerRestored;
        if (cb == null) return;
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try { cb(); } catch { }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch { }
    }

    /// <summary>
    /// 按 owner 的 <c>WM_WINDOWPOSCHANGING</c> 里的 WINDOWPOS 预先把宿主窗口挪到「即将生效」的位置。
    /// **公开给自装 WndProc 的窗口调用**（<c>hookOwner:false</c> 时本类不子类化 owner，由它转发）。
    /// </summary>
    internal unsafe void FollowOwnerMove(nint lParam)
    {
        if (_unified)
        {
            if (lParam == nint.Zero || _panelId < 0) return;
            var wp = (Win32.WINDOWPOS*)lParam;
            int x = wp->x, y = wp->y, cx = wp->cx, cy = wp->cy;
            if ((wp->flags & (Win32.SWP_NOMOVE | Win32.SWP_NOSIZE)) != 0)
            {
                if (!Win32.GetWindowRect(_owner, out var cur)) return;
                if ((wp->flags & Win32.SWP_NOMOVE) != 0) { x = cur.Left; y = cur.Top; }
                if ((wp->flags & Win32.SWP_NOSIZE) != 0) { cx = cur.Right - cur.Left; cy = cur.Bottom - cur.Top; }
            }
            if (cx > 0 && cy > 0) DesktopGlassLayer.Instance.MovePanel(_panelId, x, y, cx, cy);
            return;
        }
        if (_hwnd == nint.Zero || !_shown || lParam == nint.Zero) return;
        FollowPendingMove(_owner, lParam);
    }

    /// <summary>
    /// 重置 Z 序守卫熔断：几何 / 显隐 / owner 组变化后，目标落位可能重新可达，允许再试。
    /// 没有这一步，一次「达不成目标」就会让宿主永久停在错误位置。
    /// </summary>
    private void ResetRepinGuard()
    {
        _consecutiveRepinFailures = 0;
        _repinCircuitOpen = false;
        _hasZWrite = false;
    }

    /// <summary>
    /// 请求一次 Z 落位：**只置脏**，把写入合并到一次 UI 线程 Post。
    ///
    /// 绝不在 WndProc 里同步回写 Z：SetWindowPos 会立刻产生一条 WM_WINDOWPOSCHANGED，
    /// 判据一旦因任何原因恒为 false，就形成「写 Z → 收消息 → 判 false → 再写 Z」的自激环，
    /// UI 线程被打满就是「未响应」（2026-11-11 owner 链回归的实测成因）。
    /// Post 到下一轮消息循环打断这个同步环；_repinPending 把期间所有请求合并成一次。
    /// </summary>
    private void RequestRepin()
    {
        if (_unified) { DesktopGlassLayer.Instance.EnsureZOrder(); return; }
        if (_repinCircuitOpen || _repinPending) return;
        _repinPending = true;
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _repinPending = false;
                try { DrainRepin(); }
                catch (Exception ex) { AppLog.Warn("fenceglass", $"[GlassHost] Z 守卫落位异常 {ex.Message}"); }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch { _repinPending = false; }
    }

    /// <summary>UI 线程里唯一执行「守卫型」Z 重挂的地方：写完复核，连续未达成目标就熔断。</summary>
    private void DrainRepin()
    {
        if (_hwnd == nint.Zero || _owner == nint.Zero || !_shown || _repinCircuitOpen) return;
        if (IsBehindAllCards()) { ResetRepinGuard(); return; }

        RepinBehindOwner();

        if (IsBehindAllCards()) { ResetRepinGuard(); return; }
        if (++_consecutiveRepinFailures < RepinFailureLimit) return;

        _repinCircuitOpen = true;
        AppLog.Warn("fenceglass",
            $"[GlassHost] Z 序守卫熔断：连续 {RepinFailureLimit} 次落位未达成，停止自动重挂" +
            $"（owner=0x{_owner:X} desktop={_desktopLayer} anchor=0x{ZAnchor:X}）");
    }

    /// <summary>
    /// 把宿主窗口重新插回正确的 Z 位置并铺满（含 margin）：
    /// 弹窗宿主 = owner 正下方；桌面分区宿主 = 全部卡片之下（锚点正下方）。
    /// owner 的 Z 序被外部改过（分区每 50ms 一次的压底、拖拽抬升）之后必须调一次，
    /// 否则宿主会翻到面板上面 —— 那块投影就画到卡片自己身上了。
    /// </summary>
    internal void RepinBehindOwner()
    {
        if (_unified) { DesktopGlassLayer.Instance.EnsureZOrder(); return; }
        if (_repinCircuitOpen) return;
        if (_hwnd == nint.Zero || _owner == nint.Zero || !_shown || _repinning) return;
        if (!Win32.GetWindowRect(_owner, out var r)) return;

        var margin = _marginPx;
        // 几何去重：矩形一致且落位正确就绝不再 SetWindowPos。
        // 每次 SetWindowPos 都逼 DWM 丢掉 HostBackdropBrush 采样重采，动画/系统过渡帧上
        // 重采会把错误画面冻进背景；FollowOwnerMove 已同帧对齐，这里再挪一次纯属强制重采。
        var geomSame = Win32.GetWindowRect(_hwnd, out var cur)
            && cur.Left == r.Left - margin && cur.Top == r.Top - margin
            && cur.Right == r.Right + margin && cur.Bottom == r.Bottom + margin;
        if (geomSame && IsBehindAllCards()) return;

        var insertAfter = InsertAfterOf(_owner);
        int px = r.Left - margin, py = r.Top - margin;
        int pw = (r.Right - r.Left) + margin * 2, ph = (r.Bottom - r.Top) + margin * 2;
        var sig = (insertAfter, px, py, pw, ph);
        // 状态签名去重：同一落位目标刚写过却仍未达标，再写一次只会逼 DWM 重采并可能
        // 触发又一轮 WM_WINDOWPOSCHANGED；不重复写，交给 DrainRepin 累计熔断。
        if (_hasZWrite && sig == _lastZWrite && _consecutiveRepinFailures > 0) return;

        _repinning = true;
        try
        {
            _lastZWrite = sig;
            _hasZWrite = true;
            // NOOWNERZORDER：禁止系统因本次定位联动调整 owner 组的 Z 序
            Win32.SetWindowPos(_hwnd, insertAfter, px, py, pw, ph,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW | Win32.SWP_NOOWNERZORDER);
        }
        finally { _repinning = false; }
    }

    /// <summary>
    /// 落位自检：桌面层宿主必须插在锚点（Z 序最深的分区窗）**正下方**，
    /// 也就是所有分区卡都盖着它。合规时完全不动；不合规只排队一次异步落位
    /// —— 重复写 Z 会逼 DWM 重采 backdrop，同步写更可能自激。
    /// </summary>
    internal void EnsureBelowAllCards()
    {
        if (_unified) { DesktopGlassLayer.Instance.EnsureZOrder(); return; }
        if (!_desktopLayer || !_shown || _hwnd == nint.Zero) return;
        if (IsBehindAllCards()) return;
        RequestRepin();
    }

    /// <summary>
    /// 宿主窗自己的 WndProc：兜住「宿主被外部重排到 owner 上面」。
    ///
    /// 只靠 owner 的 <c>WM_WINDOWPOSCHANGED</c> 会漏：系统重排桌面带、Explorer 重启、
    /// 别的窗口插进两者之间时，owner 自己没变，收不到通知，宿主就留在上面把卡片内容整个糊掉
    /// —— 用户看到的是「只剩一张模糊背景的空卡」。
    /// ⚠️ 这里**只校验 + 置脏**，绝不 SetWindowPos：写 Z 会立刻再产生一条 WM_WINDOWPOSCHANGED，
    /// 判据恒 false 时 UI 线程会被打满（2026-11-11 实测）。落位统一由 RequestRepin → DrainRepin 执行。
    /// </summary>
    private nint HostWndProc(nint h, uint msg, nuint wParam, nint lParam)
    {
        if (msg == Win32.WM_WINDOWPOSCHANGED && _shown && !_repinning && _owner != nint.Zero
            && !_repinPending && !_repinCircuitOpen && !IsBehindAllCards())
        {
            // 留痕：这是「只剩模糊背景的空卡」的确切成因，后续再出现时直接从日志定位
            var now = Environment.TickCount64;
            if (now - _lastRepinLogMs > 1000)
            {
                _lastRepinLogMs = now;
                AppLog.Warn("fenceglass",
                    $"宿主脱离落位（owner=0x{_owner:X} desktop={_desktopLayer}），已排队落位");
            }
            RequestRepin();
        }

        return _hostOldProc != nint.Zero
            ? Win32.CallWindowProcW(_hostOldProc, h, msg, wParam, lParam)
            : Win32.DefWindowProcW(h, msg, wParam, lParam);
    }

    /// <summary>按 WM_WINDOWPOSCHANGING 里的 WINDOWPOS 预先把宿主窗口挪到「即将生效」的位置。</summary>
    private unsafe void FollowPendingMove(nint owner, nint lParam)
    {
        var wp = (Win32.WINDOWPOS*)lParam;

        int x = wp->x, y = wp->y, cx = wp->cx, cy = wp->cy;
        if ((wp->flags & (Win32.SWP_NOMOVE | Win32.SWP_NOSIZE)) != 0)
        {
            if (!Win32.GetWindowRect(owner, out var cur)) return;
            if ((wp->flags & Win32.SWP_NOMOVE) != 0) { x = cur.Left; y = cur.Top; }
            if ((wp->flags & Win32.SWP_NOSIZE) != 0) { cx = cur.Right - cur.Left; cy = cur.Bottom - cur.Top; }
        }
        if (cx <= 0 || cy <= 0) return;

        // 只挪位置/尺寸，不动 Z 序：owner 关系由 Sync 里的 insertAfter 一次性排好，
        // 这里再插手会把拖动中的面板顶掉（也避免 owner 链重排）。
        var margin = _marginPx;
        // 几何去重：矩形已一致就绝不 SetWindowPos（每次都逼 DWM 重采 HostBackdropBrush，
        // 过渡帧上重采会把错误画面冻进背景）。Avalonia 延迟落尺寸会重复发同矩形消息。
        if (Win32.GetWindowRect(_hwnd, out var curHost)
            && curHost.Left == x - margin && curHost.Top == y - margin
            && curHost.Right - curHost.Left == cx + margin * 2 && curHost.Bottom - curHost.Top == cy + margin * 2)
            return;
        Win32.SetWindowPos(_hwnd, nint.Zero,
            x - margin, y - margin, cx + margin * 2, cy + margin * 2,
            Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER | Win32.SWP_NOACTIVATE);
    }

    /// <summary>调 <see cref="OwnerRestored"/>，异常一律吞掉 —— 绝不能让重绘失败把 owner 的消息循环搞崩。</summary>
    private void NotifyOwnerRestored()
    {
        try { OwnerRestored?.Invoke(); }
        catch { }
    }

    private void UnhookOwner()
    {
        if (_hookedOwner != nint.Zero && _ownerOldProc != nint.Zero)
        {
            try { Win32.SetWindowLongPtrW(_hookedOwner, Win32.GWL_WNDPROC, _ownerOldProc); }
            catch { }
        }

        _hookedOwner = nint.Zero;
        _ownerOldProc = nint.Zero;
        // 摘链之后再放引用：委托还在链上时被 GC 会直接崩进程
        _ownerProc = null;
    }

    private void EnsureWindow(nint groupOwner)
    {
        if (_hwnd != nint.Zero) return;

        var instance = Win32.GetModuleHandleW(null);
        if (!_classRegistered)
        {
            var wc = new Win32.WNDCLASSW
            {
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ClassProc),
                hInstance = instance,
                lpszClassName = ClassName
            };
            Win32.RegisterClassW(in wc);
            _classRegistered = true;
        }

        // LAYERED + TRANSPARENT：面板外的阴影区点击穿透；NOREDIRECTIONBITMAP：内容全由合成层画
        // groupOwner = 目标窗口自己的 owner（弹窗 → 它的宿主窗）。必须和弹窗同属一个 owned 组：
        // 否则宿主是个「无主顶层窗」，设置窗被激活/置顶时系统会把它排到宿主上面，
        // 玻璃直接被盖住 —— 表现就是「点弹窗外面，毛玻璃闪一下」。
        // 桌面分区传 Progman（桌面层）：只有同属 Progman 的 owned window 之间才能互相排序。
        _hwnd = Win32.CreateWindowExW(
            Win32.WS_EX_LAYERED | Win32.WS_EX_NOREDIRECTIONBITMAP | Win32.WS_EX_NOACTIVATE
                | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TRANSPARENT,
            ClassName, string.Empty, Win32.WS_POPUP, 0, 0, 1, 1,
            groupOwner, nint.Zero, instance, nint.Zero);
        if (_hwnd == nint.Zero) return;

        // 只要「不插手」：不要 DWM 圆角、不要系统描边、不要过渡动画
        SetDwm(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, Win32.DWMWCP_DONOTROUND);
        SetDwm(_hwnd, Win32.DWMWA_BORDER_COLOR, Win32.DWMWA_COLOR_NONE);
        SetDwm(_hwnd, Win32.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        // 关键：不开这个，HostBackdropBrush 采不到宿主背景（实测画面全黑）
        SetDwm(_hwnd, DWMWA_USE_HOSTBACKDROPBRUSH, 1);

        // 子类化宿主窗口：校验「宿主紧贴 owner 正下方」，见 HostWndProc
        _hostProc = HostWndProc;
        var hostOldProc = Win32.GetWindowLongPtrW(_hwnd, Win32.GWL_WNDPROC);
        Win32.SetWindowLongPtrW(_hwnd, Win32.GWL_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_hostProc));
        _hostOldProc = hostOldProc != nint.Zero ? hostOldProc : nint.Zero;
    }

    /// <summary>
    /// Explorer 重启后 Progman 换了句柄：桌面层宿主要重新挂到新 Progman，
    /// 否则会掉出桌面带（跟着分区压在壁纸下面 / 翻到分区上面糊掉内容）。
    /// </summary>
    internal void ReattachDesktopOwner()
    {
        if (_unified) { DesktopGlassLayer.Instance.ReattachOwner(); return; }
        if (_hwnd == nint.Zero || !_desktopLayer) return;
        var progman = Win32.FindWindowW("Progman", null);
        if (progman == nint.Zero) return;
        Win32.SetWindowLongPtrW(_hwnd, Win32.GWL_HWNDPARENT, progman);
    }

    private static void SetDwm(nint hwnd, int attribute, int value)
    {
        try { Win32.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)); }
        catch { }
    }

    public void Dispose()
    {
        // 统一层：只注销自己的 panel，绝不销毁共用的全屏窗口
        if (_unified)
        {
            _repinCircuitOpen = true;
            UnhookOwner();
            DesktopGlassLayer.Instance.Unregister(_panelId);
            _unified = false;
            _panelId = -1;
            _shown = false;
            _owner = nint.Zero;
            OwnerBoundsChanged = null;
            OwnerRestored = null;
            return;
        }
        // 先掐断 Z 守卫：可能有已排队的 UI 线程 Post，别让它在窗口销毁后还去写 Z
        _repinCircuitOpen = true;
        // 先摘钩子：owner 还活着时必须把原 WndProc 装回去，否则我们被 GC 后它再收消息就是野指针
        UnhookOwner();

        if (_hwnd != nint.Zero && _hostOldProc != nint.Zero)
        {
            try { Win32.SetWindowLongPtrW(_hwnd, Win32.GWL_WNDPROC, _hostOldProc); } catch { }
            _hostOldProc = nint.Zero;
            _hostProc = null;
        }

        try
        {
            if (_handle != nint.Zero) ZonexGlassDetach(_handle);
        }
        catch { }
        _handle = nint.Zero;
        _owner = nint.Zero;
        if (_hwnd != nint.Zero)
        {
            try { Win32.DestroyWindow(_hwnd); } catch { }
            _hwnd = nint.Zero;
        }
        _shown = false;
    }

    internal bool IsShown => _shown;

    /// <summary>宿主此刻是否仍然紧贴 owner 正下方（被别的窗口插到中间时为 false）。</summary>
    internal bool IsDirectlyBelowOwner
        => _hwnd != nint.Zero && _owner != nint.Zero
           && Win32.GetWindow(_owner, Win32.GW_HWNDNEXT) == _hwnd;

    /// <summary>
    /// 宿主是否被排到了 owner **上面**。Win+D / shell 还原 Z 序之后会这样：
    /// 宿主盖住弹窗，用户看到的就是「只剩一块模糊背景，内容没了」。
    /// </summary>
    internal bool IsAboveOwner
    {
        get
        {
            if (_hwnd == nint.Zero || _owner == nint.Zero) return false;
            var w = Win32.GetWindow(_owner, Win32.GW_HWNDPREV); // owner 上面那一个
            for (var i = 0; i < 64 && w != nint.Zero; i++)
            {
                if (w == _hwnd) return true;
                w = Win32.GetWindow(w, Win32.GW_HWNDPREV);
            }
            return false;
        }
    }

    private nint ZAnchor => ZAnchorSource?.Invoke() ?? nint.Zero;

    /// <summary>
    /// 桌面层宿主落到「全部卡片之下」的锚点正下方；其余宿主落到 owner 正下方。
    ///
    /// 为什么不能只挂 owner 正下方：宿主采样域 = 自身窗口矩形，主机插在 owner 正下方时，
    /// Z 序就是「卡A 宿主A 卡B 宿主B …」交错排列 —— 宿主A 的**背后**正压着卡B。
    /// 折叠/展开动画里邻卡滑过本区矩形的那几帧被 HostBackdropBrush 采进背景，
    /// 动画结束后 DWM 不再重采，那块画面就永久冻在背景里（折叠后某条卡片上多出一条亮的模糊横带）。
    /// 把全部宿主压到所有卡片之下，背后就只剩壁纸，卡片内容永远进不了采样域。
    /// 锚点必须是**最深的在册分区窗**（不是 owner）：插在它正下方才是「所有卡片之下」。
    /// </summary>
    private nint InsertAfterOf(nint owner)
    {
        if (!_desktopLayer) return owner;
        var anchor = ZAnchor;
        if (anchor == nint.Zero || anchor == owner || anchor == _hwnd || !Win32.IsWindow(anchor)) return owner;
        // 锚点自己必须在 owner 之下：否则插到它下面反而把宿主抬到 owner 上面
        return IsBelow(anchor, owner) ? anchor : owner;
    }

    /// <summary>
    /// 宿主此刻是否落在正确的 Z 位置：
    /// 桌面层 = 全部卡片之下（锚点正上方走不到锚点就算违规）；其余 = owner 正下方。
    /// 每次 SetWindowPos 之前都先问这里，命中就绝不重复写 Z（重写会逼 DWM 重采 backdrop）。
    /// </summary>
    private bool IsBehindAllCards()
    {
        if (_hwnd == nint.Zero || _owner == nint.Zero) return false;
        if (!_desktopLayer) return Win32.GetWindow(_owner, Win32.GW_HWNDNEXT) == _hwnd;
        var anchor = ZAnchor;
        if (anchor == nint.Zero || anchor == _hwnd) return IsBelow(_hwnd, _owner);
        return IsAbove(anchor, _hwnd);
    }

    /// <summary>沿 GW_HWNDNEXT（Z 向下）从 <paramref name="reference"/> 能否走到 <paramref name="target"/>。</summary>
    private static bool IsBelow(nint target, nint reference)
    {
        if (target == nint.Zero || reference == nint.Zero) return false;
        var cur = Win32.GetWindow(reference, Win32.GW_HWNDNEXT);
        for (int i = 0; cur != nint.Zero && i < 2048; i++)
        {
            if (cur == target) return true;
            cur = Win32.GetWindow(cur, Win32.GW_HWNDNEXT);
        }
        return false;
    }

    /// <summary>沿 GW_HWNDPREV（Z 向上）从 <paramref name="reference"/> 能否走到 <paramref name="target"/>。</summary>
    private static bool IsAbove(nint target, nint reference)
    {
        if (target == nint.Zero || reference == nint.Zero) return false;
        var cur = Win32.GetWindow(reference, Win32.GW_HWNDPREV);
        for (int i = 0; cur != nint.Zero && i < 2048; i++)
        {
            if (cur == target) return true;
            cur = Win32.GetWindow(cur, Win32.GW_HWNDPREV);
        }
        return false;
    }

    /// <summary>
    /// 诊断用：宿主窗口此刻的可见 / 披风 / Z 序状态。
    /// 「玻璃闪一下」的两种可能都能在这里区分：被盖住（nextIsHost=false）/ 被披风（cloaked≠0）/ 没画（vis=false）。
    /// </summary>
    internal string DebugState()
    {
        if (_unified) return DesktopGlassLayer.Instance.DebugState();
        if (_hwnd == nint.Zero) return "host=0";
        var vis = Win32.IsWindowVisible(_hwnd);
        var cloaked = Win32.DwmGetWindowAttribute(_hwnd, Win32.DWMWA_CLOAKED, out var c, sizeof(int)) == 0 ? c : -1;
        var next = _owner != nint.Zero ? Win32.GetWindow(_owner, Win32.GW_HWNDNEXT) : nint.Zero;
        var hostOwner = Win32.GetWindowLongPtrW(_hwnd, Win32.GWL_HWNDPARENT);
        var ownerParent = _owner != nint.Zero ? Win32.GetWindowLongPtrW(_owner, Win32.GWL_HWNDPARENT) : nint.Zero;
        return $"vis={vis} cloaked={cloaked} nextIsHost={next == _hwnd} hostOwner=0x{hostOwner:X} ownerParent=0x{ownerParent:X}";
    }
}

