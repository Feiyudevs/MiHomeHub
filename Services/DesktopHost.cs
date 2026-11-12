// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MiHome.Models;
using MiHome.ViewModels;
using MiHome.Views;

namespace MiHome.Services;

/// <summary>
/// 桌面组件托管：负责分区窗的创建/销毁、布局持久化、外观广播，以及各类弹窗的唯一实例管理。
/// 分区窗本身是贴桌面的顶层窗口，主窗口收起进托盘也照样显示。
/// </summary>
public static class DesktopHost
{
    private static readonly Dictionary<string, DesktopGroupWindow> Windows = new(StringComparer.Ordinal);
    private static readonly List<DesktopZoneVm> Groups = new();

    private static DesktopLayout _layout = new();
    private static MainViewModel? _main;
    private static bool _initialized;

    /// <summary>
    /// 运行期临时隐藏（双击桌面切换）：与设置里的总开关分开，
    /// 不写回 desktop.json，下次启动仍按 <see cref="DesktopLayout.Enabled"/> 显示。
    /// </summary>
    private static bool _hiddenByUser;

    private static DesktopPickerWindow? _picker;
    private static DesktopGroupPickWindow? _groupPicker;
    private static DesktopAppearanceWindow? _appearance;
    private static DeviceControlWindow? _deviceWindow;
    private static DesktopMenuWindow? _groupMenu;
    private static DesktopVolumeWindow? _volume;
    private static DesktopSpeakerPickWindow? _speakerPicker;
    private static DesktopRenameWindow? _rename;

    public static DesktopLayout Layout => _layout;

    public static DesktopAppearance Look => _layout.Appearance;

    public static bool Enabled => _layout.Enabled;

    /// <summary>双击桌面临时收起中（不代表设置里的总开关）。</summary>
    public static bool HiddenByUser => _hiddenByUser;

    public static int GroupCount => Groups.Count;

    public static IReadOnlyList<DesktopZoneVm> GroupList => Groups;

    /// <summary>设备列表变化（切家庭 / 刷新 / 缓存上屏）时通知分区重绑设备实例。</summary>
    public static void Initialize(MainViewModel main)
    {
        if (_initialized) return;
        _initialized = true;
        _main = main;
        _layout = DesktopGroupStore.Load();
        main.DevicesReloaded += SyncDevices;
        DisplayWatcher.Changed += OnDisplayChanged;

        // 双击桌面空白处切换所有分区显隐（设置里可关；钩子常驻，回调里读开关）
        DesktopDoubleClickHook.Install();

        // 上次退出时开着的分区，这次启动直接贴回桌面
        if (_layout.Enabled)
            ShowAll();

        // 开机自启赶在显示器全部就绪之前落位时（屏还没被枚举到，或那次广播根本没收到），
        // 过几秒按当下的屏幕重新判定一次：家屏确实没开就留在备用屏，开了就落回原位。
        DispatcherTimer.RunOnce(() => DisplayWatcher.Notify(), TimeSpan.FromSeconds(5));
    }

    /// <summary>屏幕增减：每块分区重新按家屏落位（家屏回来落回原位，家屏没了搬到备用屏）。</summary>
    private static void OnDisplayChanged()
    {
        foreach (var win in Windows.Values.ToList())
            win.ReapplyPlacement();
    }

    private static void SyncDevices()
    {
        foreach (var vm in Groups) vm.LoadDevices();
    }

    /// <summary>按模型里的 Kind 造对应类型的分区视图模型。</summary>
    private static DesktopZoneVm CreateVm(MainViewModel main, DesktopGroupModel model)
        => model.IsMedia ? new DesktopMediaVm(main, model) : new DesktopGroupVm(main, model);

    /// <summary>
    /// 新建分区并放入设备（「添加到桌面组件」弹窗里的「新建并添加」）。
    /// <paramref name="name"/> 为空时按房间名/设备名自动命名。
    /// </summary>
    public static DesktopGroupVm? CreateGroupWithDevice(MijiaDevice? device, string? name = null)
    {
        if (_main == null || device == null) return null;

        var (x, y) = NextGroupSpot();
        var model = new DesktopGroupModel
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? (string.IsNullOrWhiteSpace(device.RoomName) || device.RoomName == "未分配房间"
                    ? device.Name
                    : device.RoomName)
                : name.Trim(),
            X = x,
            Y = y
        };
        model.Dids.Add(device.Did);

        _layout.Groups.Add(model);
        Save();

        var vm = new DesktopGroupVm(_main, model);
        Groups.Add(vm);
        EnsureEnabled();
        OpenWindow(vm);
        _main.AppendLog($"「{device.Name}」已加入新建分区「{vm.Name}」");
        return vm;
    }

    /// <summary>
    /// 把设备加入已有分区（「添加到桌面组件」弹窗里点分区行）。
    /// 已在该分区里就不重复加，只把分区亮出来提示它在哪。
    /// </summary>
    public static DesktopGroupVm? AddDeviceToGroup(string groupId, MijiaDevice? device)
    {
        if (_main == null || device == null) return null;

        var model = _layout.Groups.FirstOrDefault(g => g.Id == groupId);
        if (model == null) return null;

        // 分区窗可能还没建（总开关关着、或刚启动），补一个，保证用户点完立刻能看到结果。
        // 媒体分区不能塞设备，按 id 找到的是它就当没有，另建一个设备分区。
        var vm = Groups.OfType<DesktopGroupVm>().FirstOrDefault(g => g.Model.Id == groupId);
        if (vm == null)
        {
            if (model.IsMedia) return null;
            vm = new DesktopGroupVm(_main, model);
            Groups.Add(vm);
        }

        if (!vm.Contains(device.Did))
        {
            vm.AddDevice(device);
            _main.AppendLog($"「{device.Name}」已加入桌面分区「{vm.Name}」");
        }

        EnsureEnabled();
        Save();

        OpenWindow(vm);
        if (Windows.TryGetValue(groupId, out var w)) w.BringToFrontTemporarily();
        return vm;
    }

    /// <summary>
    /// 分区菜单里的「添加分区」：在 <paramref name="near"/> 附近放一块空白设备分区。
    /// 优先右→下→左→上逐圈找不与现有分区重叠的落点，都放不下退到整屏里离它最近的空位。
    /// </summary>
    public static DesktopGroupVm? CreateBlankGroup(DesktopZoneVm near)
    {
        if (_main == null) return null;

        var (x, y) = NextSpotNear(near.Model);
        var model = new DesktopGroupModel { Name = "新分区", X = x, Y = y };

        _layout.Groups.Add(model);
        Save();

        var vm = new DesktopGroupVm(_main, model);
        Groups.Add(vm);
        EnsureEnabled();
        OpenWindow(vm);
        _main.AppendLog("已添加空白分区「新分区」");
        return vm;
    }

    /// <summary>空白分区尺寸未知，按一列卡片的典型大小参与占位判断（DIP）。</summary>
    private const double BlankZoneGuessW = 320;
    private const double BlankZoneGuessH = 240;

    /// <summary>分区之间的最小间距（DIP），避免贴死互相压住边框。</summary>
    private const double GroupGapDip = 12;

    /// <summary>整屏找空位时的搜索步长（物理像素）。</summary>
    private const double SpotStepPx = 20;

    /// <summary>分区所在屏幕的缩放；没开窗时按主屏缩放近似。</summary>
    private static double GroupScale(string id)
    {
        if (Windows.TryGetValue(id, out var win) && win.RenderScaling > 0) return win.RenderScaling;
        return MonitorLayout.Fallback()?.Scaling ?? 1;
    }

    /// <summary>
    /// 分区在桌面上的实际矩形（物理像素）：开了窗就量窗口，
    /// 没开窗只能按模型宽度 + 一列卡片的典型高度估，宁可估大不估小。
    /// </summary>
    private static (double X, double Y, double W, double H) OccupiedRect(DesktopGroupModel g)
    {
        var scale = GroupScale(g.Id);
        var wDip = g.Width > 0 ? g.Width : BlankZoneGuessW;
        var hDip = BlankZoneGuessH;

        if (Windows.TryGetValue(g.Id, out var win))
        {
            if (win.Width > 0) wDip = win.Width;
            if (win.Height > 0) hDip = win.Height;
        }

        return (g.X, g.Y, wDip * scale, hDip * scale);
    }

    /// <summary>一枚新分区的占位尺寸（物理像素）。</summary>
    private static (double W, double H) NewGroupSize(double scale)
        => (BlankZoneGuessW * scale, BlankZoneGuessH * scale);

    /// <summary>在参考分区附近找空位：右→下→左→上逐圈外扩，都挤不下再在整屏里找离它最近的空位。</summary>
    private static (double X, double Y) NextSpotNear(DesktopGroupModel refModel)
    {
        var (rx, ry, rw, rh) = OccupiedRect(refModel);
        var scale = GroupScale(refModel.Id);
        var gap = GroupGapDip * scale;
        var (w, h) = NewGroupSize(scale);

        for (var ring = 1; ring <= 3; ring++)
        {
            var candidates = new (double X, double Y)[]
            {
                (rx + rw + gap * ring, ry),                   // 右侧
                (rx, ry + rh + gap * ring),                   // 下方
                (rx - w - gap * ring, ry),                    // 左侧
                (rx, ry - h - gap * ring),                    // 上方
            };

            foreach (var (x, y) in candidates)
            {
                if (IsSpotFree(x, y, w, h, gap)) return (x, y);
            }
        }

        return NearestFreeSpot((rx + rw / 2, ry + rh / 2));
    }

    /// <summary>
    /// 矩形 (x, y, w, h) 与所有现有分区都不相交（四周再留 gap）：
    /// 新分区既不压住别人，也不会被别人盖住。
    /// </summary>
    private static bool IsSpotFree(double x, double y, double w, double h, double gap)
    {
        foreach (var g in _layout.Groups)
        {
            var (gx, gy, gw, gh) = OccupiedRect(g);
            if (x < gx + gw + gap && gx < x + w + gap &&
                y < gy + gh + gap && gy < y + h + gap)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 加入/新建分区后把桌面组件总开关打开（走主视图模型属性，保证设置页的开关状态同步）。
    /// </summary>
    private static void EnsureEnabled()
    {
        if (_main == null) return;
        if (!_main.DesktopWidgetsEnabled) _main.DesktopWidgetsEnabled = true;
        else if (!_layout.Enabled) SetEnabled(true);
    }

    private static void OpenWindow(DesktopZoneVm vm)
    {
        if (Windows.ContainsKey(vm.Model.Id)) return;
        var win = new DesktopGroupWindow(vm);
        Windows[vm.Model.Id] = win;
        win.Closed += (_, _) =>
        {
            if (Windows.TryGetValue(vm.Model.Id, out var cur) && ReferenceEquals(cur, win))
                Windows.Remove(vm.Model.Id);
        };
        win.Show();

        // 新建的分区、以及没有签名记录的老数据：把眼下这块屏记成它的家。
        // 借用别的屏期间（IsExiled）不记，否则临时落点会被当成家，屏回来也就不回去了。
        if (vm.Model.ScreenSig.Length == 0 && !win.IsExiled)
        {
            var sig = win.CurrentScreenSignature();
            if (sig.Length > 0)
            {
                vm.Model.ScreenSig = sig;
                Save();
            }
        }
    }

    public static void ShowAll()
    {
        if (_main == null) return;
        EnsureMediaGroup();
        foreach (var model in _layout.Groups.ToList())
        {
            if (model.IsMedia && _layout.MediaHidden) continue;
            if (Groups.Any(g => g.Model.Id == model.Id)) continue;
            var vm = CreateVm(_main, model);
            Groups.Add(vm);
            OpenWindow(vm);
        }
    }

    public static void HideAll()
    {
        foreach (var win in Windows.Values.ToList())
        {
            try { win.Close(); } catch { /* 关窗失败不影响后续 */ }
        }
        Windows.Clear();
        foreach (var vm in Groups)
        {
            if (vm is DesktopMediaVm media) media.Detach();
            if (vm is DesktopGroupVm group) group.Detach();
            CloseGroupDialogs(vm);
        }
        Groups.Clear();
        NotifyGroupsChanged();
    }

    /// <summary>总开关：关掉只收起窗口，配置与分区内容保留。</summary>
    public static void SetEnabled(bool on)
    {
        // 设置页重新打开时清掉「双击临时隐藏」，让开关与真实可见性重新对齐
        _hiddenByUser = false;
        _layout.Enabled = on;
        if (on) ShowAll();
        else HideAll();
        NotifyGroupsChanged();
        Save();
    }

    /// <summary>
    /// 双击桌面空白：显示 / 隐藏所有分区。
    /// 只改运行期状态，不动配置里的总开关；总开关关着时没有可切换的内容。
    /// </summary>
    public static void ToggleVisibilityFromDesktop()
    {
        if (_main == null || !_layout.Enabled) return;

        if (_hiddenByUser)
        {
            _hiddenByUser = false;
            ShowAll();
            _main.AppendLog("桌面组件已显示");
        }
        else
        {
            _hiddenByUser = true;
            HideAll();
            _main.AppendLog("桌面组件已隐藏");
        }
    }

    /// <summary>光标是否落在任意一块桌面分区窗里（双击桌面的空白判定要排除它们）。</summary>
    public static bool IsPointOnDesktopWidget(int x, int y)
    {
        foreach (var win in Windows.Values)
        {
            var h = win.Handle;
            if (h == nint.Zero) continue;
            if (DesktopNative.GetWindowRect(h, out var rect)
                && x >= rect.Left && x < rect.Right
                && y >= rect.Top && y < rect.Bottom)
                return true;
        }
        return false;
    }

    /// <summary>桌面上分区增减后回调主视图模型：后台降频轮询要不要继续由它重新判定。</summary>
    private static void NotifyGroupsChanged() => _main?.RecheckBackgroundPolling();

    public static void RemoveGroup(DesktopZoneVm vm)
    {
        _layout.Groups.RemoveAll(g => g.Id == vm.Model.Id);
        Groups.Remove(vm);
        if (Windows.Remove(vm.Model.Id, out var win))
        {
            try { win.Close(); } catch { }
        }
        if (vm is DesktopMediaVm media) media.Detach();
        if (vm is DesktopGroupVm group) group.Detach();
        // 这个分区开着的弹窗一起收掉，避免对着已解散的分区操作
        CloseGroupDialogs(vm);
        NotifyGroupsChanged();
        Save();
    }

    /// <summary>
    /// 开启桌面组件时确保桌面上有一块媒体分区：只种一次。
    /// 用户手动「关闭媒体分区」后 <see cref="DesktopLayout.MediaHidden"/> 置位，不再自动放回，
    /// 想再要只能去设置页重新打开。
    /// </summary>
    private static void EnsureMediaGroup()
    {
        if (_main == null || _layout.MediaHidden) return;
        if (_layout.Groups.Any(g => g.IsMedia)) return;
        _layout.Groups.Add(NewMediaModel());
        Save();
    }

    /// <summary>媒体分区默认落右上角，被占了就往左挪一列。</summary>
    private static DesktopGroupModel NewMediaModel()
    {
        const double width = 300;
        const double margin = 24;
        var (vx, vy, vw, _) = DesktopEmbed.VirtualScreen();
        var x = vx + vw - width - margin;
        for (var i = 0; i < 6; i++)
        {
            var taken = _layout.Groups.Any(g =>
                Math.Abs(g.Y - (vy + margin)) < 80 && Math.Abs(g.X - x) < width);
            if (!taken) break;
            x -= width + 12;
        }
        return new DesktopGroupModel
        {
            Kind = DesktopGroupModel.ZoneMedia,
            Name = "媒体",
            Width = width,
            X = Math.Round(x),
            Y = Math.Round(vy + margin),
        };
    }

    /// <summary>桌面菜单里的「关闭媒体分区」：收掉窗口并记住不再自动放回。</summary>
    public static void HideMedia()
    {
        _layout.MediaHidden = true;
        var vm = Groups.FirstOrDefault(g => g.IsMedia);
        if (vm != null) RemoveGroup(vm);
        else
        {
            // 总开关关着时 VM 已被清空，但模型还留在配置里：一并删掉，
            // 否则下次启动 ShowAll 会把这块分区复活，设置开关却显示「关」
            _layout.Groups.RemoveAll(g => g.IsMedia);
            Save();
        }
    }

    /// <summary>设置页重新打开媒体分区。</summary>
    public static DesktopZoneVm? ShowMedia()
    {
        if (_main == null) return null;
        var existing = Groups.FirstOrDefault(g => g.IsMedia);
        if (existing != null)
        {
            OpenWindow(existing);
            return existing;
        }
        var model = _layout.Groups.FirstOrDefault(g => g.IsMedia) ?? NewMediaModel();
        if (!_layout.Groups.Contains(model)) _layout.Groups.Add(model);
        _layout.MediaHidden = false;
        Save();
        var vm = CreateVm(_main, model);
        Groups.Add(vm);
        OpenWindow(vm);
        return vm;
    }

    public static void Save() => DesktopGroupStore.Save(_layout);

    /// <summary>外观变更：重算所有分区的画刷，并让窗口重打一次磨砂/圆角（落盘由调用方按节流决定）。</summary>
    public static void ApplyAppearance()
    {
        foreach (var vm in Groups) vm.RefreshAppearance();
        foreach (var win in Windows.Values) win.ApplyChrome();
    }

    /// <summary>分区位置/尺寸变化后落盘（拖动、缩放结束时调用）。</summary>
    public static void PersistGroup(DesktopZoneVm vm)
    {
        vm.Model.X = Math.Round(vm.Model.X);
        vm.Model.Y = Math.Round(vm.Model.Y);
        Save();
    }

    /// <summary>
    /// 新建分区的落点：已有分区就贴着最后一个设备分区放（右→下→左→上找空位），
    /// 一个分区都没有时排到主屏右侧、自上而下。
    /// </summary>
    private static (double X, double Y) NextGroupSpot()
    {
        var anchor = _layout.Groups.LastOrDefault(g => !g.IsMedia)
                     ?? _layout.Groups.LastOrDefault();
        return anchor != null ? NextSpotNear(anchor) : PrimaryRightColumnSpot();
    }

    /// <summary>空桌面：主屏工作区右侧贴边，自上而下找第一处不被占用的位置。</summary>
    private static (double X, double Y) PrimaryRightColumnSpot()
    {
        const double margin = 24;
        var monitor = MonitorLayout.Fallback();
        var area = monitor?.WorkArea ?? new PixelRect(0, 0, 1920, 1080);
        var scale = monitor?.Scaling ?? 1;
        var gap = GroupGapDip * scale;
        var (w, h) = NewGroupSize(scale);
        var x = area.Right - w - margin;

        for (var y = area.Y + margin; y + h <= area.Bottom - margin; y += SpotStepPx)
        {
            if (IsSpotFree(x, y, w, h, gap)) return (x, y);
        }

        return NearestFreeSpot((area.Right - w, area.Y + h));
    }

    /// <summary>整屏扫描：在虚拟桌面里找离参考点最近、且不与任何分区相交的落点。</summary>
    private static (double X, double Y) NearestFreeSpot((double X, double Y) from)
    {
        const double margin = 24;
        var (vx, vy, vw, vh) = DesktopEmbed.VirtualScreen();
        var scale = MonitorLayout.Fallback()?.Scaling ?? 1;
        var gap = GroupGapDip * scale;
        var (w, h) = NewGroupSize(scale);

        var found = false;
        double bestD = double.MaxValue, bestX = vx + margin, bestY = vy + margin;

        for (var y = vy + margin; y + h <= vy + vh - margin; y += SpotStepPx)
        {
            for (var x = vx + margin; x + w <= vx + vw - margin; x += SpotStepPx)
            {
                if (!IsSpotFree(x, y, w, h, gap)) continue;

                var d = Math.Abs(x - from.X) + Math.Abs(y - from.Y);
                if (d >= bestD) continue;

                bestD = d;
                bestX = x;
                bestY = y;
                found = true;
            }
        }

        // 桌面实在挤满时也返回一个静止落点，至少不会叠在参考分区上
        return found ? (bestX, bestY) : (vx + margin, vy + margin);
    }

    // ================= 弹窗 =================

    /// <summary>设备卡「添加到桌面组件」：以软件为中心弹窗，多选已有分区或按需新建。</summary>
    public static void OpenGroupPicker(MijiaDevice device)
    {
        if (_groupPicker == null)
        {
            _groupPicker = new DesktopGroupPickWindow();
            _groupPicker.Closed += (_, _) => _groupPicker = null;
        }
        _groupPicker.Bind(device);

        // 铺满主窗口客户区，遮罩压暗软件本体；主窗口不在（托盘里）才退回屏内居中
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is { IsVisible: true })
        {
            _groupPicker.FitTo(owner);
            if (!_groupPicker.IsVisible) _groupPicker.Show(owner);
        }
        else
        {
            if (!_groupPicker.IsVisible)
            {
                PopupPlacement.CenterOnScreen(_groupPicker, 520, 616);
                _groupPicker.Show();
            }
        }

        _groupPicker.Activate();
    }

    /// <summary>分区菜单里的「添加设备」：全量设备与场景 + 搜索 + 勾选，勾选即时生效。</summary>
    public static void OpenPicker(DesktopGroupVm vm)
    {
        if (_picker == null)
        {
            _picker = new DesktopPickerWindow();
            _picker.Closed += (_, _) => _picker = null;
        }
        _picker.Bind(vm);
        // 先按工作区收尺寸（小屏 / 高缩放下 956×736 会顶出屏幕，底部内容就滚不到），再定位
        _picker.FitToWorkArea();
        // 在当前屏幕工作区居中弹出（多屏时按光标所在的那块屏）
        PopupPlacement.CenterOnScreen(_picker, _picker.Width, _picker.Height);
        if (!_picker.IsVisible) _picker.Show();
        _picker.Activate();
    }

    /// <summary>点分区里的设备卡片：走弹窗基类弹出这台设备的控制内容。</summary>
    /// <param name="source">点开它的分区（用来把弹窗放到同一块屏幕上）；为空则居中于主屏。</param>
    public static void OpenDevicePanel(MainViewModel main, MijiaDevice device, DesktopZoneVm? source)
    {
        main.ShowDevicePanel(device);
        if (_deviceWindow == null)
        {
            _deviceWindow = new DeviceControlWindow(main);
            _deviceWindow.Closed += (_, _) => _deviceWindow = null;
        }

        Window? anchor = null;
        if (source != null && Windows.TryGetValue(source.Model.Id, out var groupWin))
            anchor = groupWin;

        _deviceWindow.ShowFor(anchor);
    }

    /// <summary>
    /// 这块分区的自绘菜单是否正开着。菜单浮层在分区窗口矩形之外，
    /// 悬停展开的看门狗会把「光标移到菜单上」误判成移出，必须在菜单关闭前按住不收起
    ///（对齐 Zonex 的 _fenceMenuOpen 保护）。
    /// </summary>
    public static bool IsMenuOpenFor(DesktopZoneVm vm)
        => _groupMenu is { IsVisible: true } m && ReferenceEquals(m.Target, vm);

    /// <summary>分区「⋯」菜单：贴在按钮下方弹出（下方放不下才翻到上方），再点一次（或失焦）收起。</summary>
    public static void OpenGroupMenu(DesktopZoneVm vm, PixelRect anchor)
    {
        if (_groupMenu is { IsVisible: true } open && ReferenceEquals(open.Target, vm))
        {
            open.Close();
            return;
        }

        _groupMenu?.Close();

        var menu = new DesktopMenuWindow();
        _groupMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_groupMenu, menu)) _groupMenu = null;
        };
        menu.Bind(vm, anchor);
        menu.Show();
        DesktopLog.Write($"[menu] Show anchor={anchor} visible={menu.IsVisible}");
    }

    public static void OpenAppearance()
    {
        if (_appearance == null)
        {
            _appearance = new DesktopAppearanceWindow();
            _appearance.Closed += (_, _) => _appearance = null;
        }
        _appearance.Reload();
        PopupPlacement.CenterOnScreen(_appearance, _appearance.Width, _appearance.Height);
        if (!_appearance.IsVisible) _appearance.Show();
        _appearance.Activate();
    }

    /// <summary>菜单里的「重命名」：与双击名称同一套入口，浮层落在标题名称的位置上。</summary>
    public static void RequestRename(DesktopZoneVm vm)
    {
        if (Windows.TryGetValue(vm.Model.Id, out var win))
            win.BeginRename();
    }

    /// <summary>这块分区是否正开着重命名浮层（宿主据此拦掉「点标题折叠」）。</summary>
    public static bool IsRenaming(DesktopZoneVm vm)
        => _rename is { IsVisible: true } w && ReferenceEquals(w.Target, vm);

    /// <summary>
    /// 重命名浮层：独立普通窗口。分区窗本身是 Progman 的从属窗口，绝不能为了拿键盘焦点去激活它，
    /// 否则整个桌面窗口集会被抬到普通软件之上。
    /// </summary>
    public static void OpenRename(DesktopZoneVm vm, PixelRect anchor)
    {
        if (_rename is { IsVisible: true } open && ReferenceEquals(open.Target, vm))
        {
            open.Activate();
            return;
        }

        _rename?.Close();

        var win = new DesktopRenameWindow();
        _rename = win;
        vm.IsRenaming = true;
        win.Closed += (_, _) =>
        {
            vm.IsRenaming = false;
            if (ReferenceEquals(_rename, win)) _rename = null;
        };
        win.Bind(vm, anchor);
        win.Show();
    }

    /// <summary>音量按钮：贴着按钮弹一枚滑条窗（桌面窗是 NOACTIVATE，Avalonia 的 Popup 会被埋，只能独立窗口）。</summary>
    public static void OpenVolumeMenu(DesktopMediaVm vm, PixelRect anchor)
    {
        if (_volume is { IsVisible: true } open && ReferenceEquals(open.Target, vm))
        {
            open.Close();
            return;
        }
        _volume?.Close();

        var win = new DesktopVolumeWindow();
        _volume = win;
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_volume, win)) _volume = null;
        };
        win.Bind(vm, anchor);
        win.Show();
    }

    /// <summary>音箱名：弹一个贴着的音箱选择窗，选完即写回全局默认音箱。</summary>
    public static void OpenSpeakerPicker(DesktopMediaVm vm, PixelRect anchor)
    {
        if (_speakerPicker is { IsVisible: true } open && ReferenceEquals(open.Target, vm))
        {
            open.Close();
            return;
        }
        _speakerPicker?.Close();

        var win = new DesktopSpeakerPickWindow();
        _speakerPicker = win;
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_speakerPicker, win)) _speakerPicker = null;
        };
        win.Bind(vm, anchor);
        win.Show();
    }

    private static void CloseGroupDialogs(DesktopZoneVm vm)
    {
        if (_picker is { IsVisible: true } p && ReferenceEquals(p.Target, vm)) p.Close();
        if (_groupMenu is { IsVisible: true } m && ReferenceEquals(m.Target, vm)) m.Close();
        if (_volume is { IsVisible: true } v && ReferenceEquals(v.Target, vm)) v.Close();
        if (_speakerPicker is { IsVisible: true } s && ReferenceEquals(s.Target, vm)) s.Close();
        if (_rename is { IsVisible: true } r && ReferenceEquals(r.Target, vm)) r.Close();
    }

    /// <summary>切换主题后，分区底色要跟着主题卡片色重算。</summary>
    public static void OnThemeChanged()
    {
        foreach (var vm in Groups) vm.RefreshAppearance();
        foreach (var win in Windows.Values) win.ApplyChrome();
    }
}
