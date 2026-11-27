// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MiHomeHub.Models;
using MiHomeHub.ViewModels;
using MiHomeHub.Views;

namespace MiHomeHub.Services;

/// <summary>
/// 桌面组件托管：负责分区窗的创建/销毁、布局持久化、外观广播，以及各类弹窗的唯一实例管理。
/// 分区窗本身是贴桌面的顶层窗口，主窗口收起进托盘也照样显示。
/// </summary>
public static class DesktopHost
{
    private static readonly Dictionary<string, IDesktopWindow> Windows = new(StringComparer.Ordinal);
    private static readonly List<DesktopZoneVm> Groups = new();

    private static DesktopLayout _layout = new();
    private static MainViewModel? _main;
    private static bool _initialized;

    /// <summary>
    /// 运行期临时隐藏（双击桌面切换）：与设置里的总开关分开，
    /// 不写回 desktop.json，下次启动仍按 <see cref="DesktopLayout.Enabled"/> 显示。
    /// </summary>
    private static bool _hiddenByUser;

    private static TargetPickerWindow? _picker;
    private static DesktopGroupPickWindow? _groupPicker;
    private static DesktopAppearanceWindow? _appearance;
    private static IDisposable? _appearanceTimer;
    private static DeviceControlWindow? _deviceWindow;
    private static DesktopMenuWindow? _groupMenu;
    private static DesktopVolumeWindow? _volume;
    private static DesktopSpeakerPickWindow? _speakerPicker;
    private static DesktopRenameWindow? _rename;
    private static DesktopAcPickWindow? _acPicker;
    private static CardMenuWindow? _cardMenu;

    public static DesktopLayout Layout => _layout;

    public static DesktopAppearance Look => _layout.Appearance;

    public static bool Enabled => _layout.Enabled;

    /// <summary>双击桌面临时收起中（不代表设置里的总开关）。</summary>
    public static bool HiddenByUser => _hiddenByUser;

    /// <summary>此刻桌面上是否真有分区在显示（总开关开着且没被临时收起）。</summary>
    public static bool WidgetsVisible => _layout.Enabled && !_hiddenByUser;

    public static int GroupCount => Groups.Count;

    public static IReadOnlyList<DesktopZoneVm> GroupList => Groups;

    /// <summary>
    /// 桌面数据卡增删 / 显隐变化：设置页摘要和「数据看板」编辑弹窗据此把开关回写成当前状态。
    /// 只在这些真正改变数据卡在不在桌面的入口触发，尺寸、位置、外观变化不发。
    /// </summary>
    public static event Action? DataCardStateChanged;

    /// <summary>设备列表变化（切家庭 / 刷新 / 缓存上屏）时通知分区重绑设备实例。</summary>
    public static void Initialize(MainViewModel main)
    {
        if (_initialized) return;
        _initialized = true;
        _main = main;
        _layout = DesktopGroupStore.Load();
        main.DevicesReloaded += SyncDevices;
        DisplayWatcher.Changed += OnDisplayChanged;

        // 双击桌面空白处切换所有分区显隐（设置里可关；钩子常驻，回调里读开关）。
        // 钩子的安装时机由 App 控制：启动早期挂 WH_MOUSE_LL 会让全系统鼠标事件等本进程，见 App/DesktopDoubleClickHook。

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
        => model.IsData ? new DataCardVm(main, model)
        : model.IsMedia ? new DesktopMediaVm(main, model)
        : model.IsAc ? new DesktopAcVm(main, model)
        : model.IsCamera ? new DesktopCameraVm(main, model)
        : new DesktopGroupVm(main, model);

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

        // 媒体 / 空调 / 监控 / 数据卡都是专用卡面（音响播放 / 空调控制 / 实时画面 / 指标卡），
        // 压根没有设备清单：既不能往里塞设备，也不能按 id 给它们套一层设备分区视图模型
        if (model.IsMedia || model.IsAc || model.IsData || model.IsCamera) return null;

        // 分区窗可能还没建（总开关关着、或刚启动），补一个，保证用户点完立刻能看到结果
        var vm = Groups.OfType<DesktopGroupVm>().FirstOrDefault(g => g.Model.Id == groupId);
        if (vm == null)
        {
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
        var hDip = g.Height > 1 ? g.Height : BlankZoneGuessH;

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

        DesktopLog.Write($"[data-card] open begin id={vm.Model.Id} type={vm.GetType().Name} " +
                         $"shown={vm.Model.Shown} x={vm.Model.X:0} y={vm.Model.Y:0} " +
                         $"w={vm.Model.Width:0} h={vm.Model.Height:0}");

        IDesktopWindow win;
        try
        {
            win = vm is DataCardVm card
                ? new DataCardWindow(card)
                : new DesktopGroupWindow(vm);
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"[data-card] 创建卡窗失败 id={vm.Model.Id}：{ex}");
            _main?.AppendLog($"桌面数据卡创建失败：{ex.Message}");
            return;
        }

        Windows[vm.Model.Id] = win;
        win.Window.Closed += (_, _) =>
        {
            if (Windows.TryGetValue(vm.Model.Id, out var cur) && ReferenceEquals(cur, win))
                Windows.Remove(vm.Model.Id);
        };

        try
        {
            win.Window.Show();
            DesktopLog.Write($"[data-card] open ok id={vm.Model.Id} hwnd=0x{win.Handle:X}");
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"[data-card] 显示卡窗失败 id={vm.Model.Id}：{ex}");
            _main?.AppendLog($"桌面数据卡显示失败：{ex.Message}");
            Windows.Remove(vm.Model.Id);
            try { win.Close(); } catch { }
            return;
        }

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
        EnsureAcGroup();
        foreach (var model in _layout.Groups.ToList())
        {
            if (model.IsMedia && _layout.MediaHidden) continue;
            if (model.IsAc && _layout.AcHidden) continue;
            // 监控分区默认关着，只有用户在设置里打开过才上桌面
            if (model.IsCamera && _layout.CameraHidden) continue;
            // 数据卡关闭只收起窗口、保留配置；下次启动也不该自己冒出来
            if (model.IsData && !model.Shown) continue;
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
            if (vm is DesktopAcVm ac) ac.Detach();
            if (vm is DesktopCameraVm camera) camera.Detach();
            if (vm is DesktopGroupVm group) group.Detach();
            if (vm is DataCardVm card) card.Detach();
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

    /// <summary>
    /// 托盘菜单的「显示 / 隐藏桌面组件」：总开关关着时先把桌面组件打开（托盘本身就是快捷入口），
    /// 开着则走和双击桌面同一套运行期切换，不写回配置里的总开关。
    /// </summary>
    public static void ToggleFromTray()
    {
        if (_main == null) return;
        if (!_layout.Enabled)
        {
            _main.DesktopWidgetsEnabled = true;
            return;
        }
        ToggleVisibilityFromDesktop();
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

    /// <summary>桌面数据卡状态变化：设置页摘要与编辑弹窗据此刷新自己的开关。</summary>
    private static void NotifyDataCardStateChanged() => DataCardStateChanged?.Invoke();

    public static void RemoveGroup(DesktopZoneVm vm)
    {
        _layout.Groups.RemoveAll(g => g.Id == vm.Model.Id);
        Groups.Remove(vm);
        if (Windows.Remove(vm.Model.Id, out var win))
        {
            try { win.Close(); } catch { }
        }
        if (vm is DesktopMediaVm media) media.Detach();
        if (vm is DesktopAcVm ac) ac.Detach();
        if (vm is DesktopCameraVm camera) camera.Detach();
        if (vm is DesktopGroupVm group) group.Detach();
        if (vm is DataCardVm card) card.Detach();
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
        _layout.Groups.Add(RestoredMediaModel());
        Save();
    }

    /// <summary>
    /// 开启桌面组件时确保桌面上有一块空调分区：只种一次。
    /// 用户手动「关闭空调分区」后 <see cref="DesktopLayout.AcHidden"/> 置位，不再自动放回，
    /// 想再要只能去设置页重新打开。
    /// </summary>
    private static void EnsureAcGroup()
    {
        if (_main == null || _layout.AcHidden) return;
        if (_layout.Groups.Any(g => g.IsAc)) return;
        _layout.Groups.Add(RestoredAcModel());
        Save();
    }

    /// <summary>
    /// 媒体分区重新出现时的模型：上一次摆过就回原位，从没摆过才按默认落点挑空位。
    /// 位置按模型里的 DIP 坐标（虚拟桌面左上角）还原，换分辨率/多屏也算得回来。
    /// </summary>
    private static DesktopGroupModel RestoredMediaModel()
    {
        var spot = _layout.MediaSpot;
        if (spot == null) return NewMediaModel();
        return new DesktopGroupModel
        {
            Kind = DesktopGroupModel.ZoneMedia,
            Name = "媒体",
            X = spot.X,
            Y = spot.Y,
            Width = SpotWidth(spot.Width),
            ScreenSig = spot.ScreenSig ?? "",
        };
    }

    /// <summary>空调分区重新出现时的模型：规则同媒体分区，上次在哪儿就在哪儿。</summary>
    private static DesktopGroupModel RestoredAcModel()
    {
        var spot = _layout.AcSpot;
        if (spot == null) return NewAcModel();
        return new DesktopGroupModel
        {
            Kind = DesktopGroupModel.ZoneAc,
            Name = "空调",
            X = spot.X,
            Y = spot.Y,
            Width = SpotWidth(spot.Width),
            ScreenSig = spot.ScreenSig ?? "",
        };
    }

    /// <summary>记下来的宽度可能缺失或越界（老数据、手工改过的配置），夹回合法范围。</summary>
    private static double SpotWidth(double width)
        => width <= 0 ? DesktopGroupModel.DefaultWidth
        : Math.Clamp(width, DesktopGroupModel.MinWidth, DesktopGroupModel.MaxWidth);

    /// <summary>空调分区默认落在媒体分区下方；被占了就往下/左挪。</summary>
    private static DesktopGroupModel NewAcModel()
    {
        const double width = 320;
        const double height = 260;
        const double margin = 24;
        var media = _layout.Groups.FirstOrDefault(g => g.IsMedia);
        var (vx, vy, vw, _) = DesktopEmbed.VirtualScreen();
        double x, y;
        if (media != null)
        {
            x = media.X;
            y = media.Y + height + margin;
        }
        else
        {
            x = vx + vw - width - margin;
            y = vy + margin;
        }
        for (var i = 0; i < 6; i++)
        {
            var taken = _layout.Groups.Any(g =>
                Math.Abs(g.Y - y) < height && Math.Abs(g.X - x) < width);
            if (!taken) break;
            y += height + 12;
        }
        return new DesktopGroupModel
        {
            Kind = DesktopGroupModel.ZoneAc,
            Name = "空调",
            Width = width,
            X = Math.Round(x),
            Y = Math.Round(y),
        };
    }

    /// <summary>桌面菜单里的「关闭空调分区」：收掉窗口并记住不再自动放回。</summary>
    public static void HideAc()
    {
        // 模型随关闭被移出清单，眼下在哪儿得先单独记一份：重新打开时回原位，不重新找空位
        CaptureZoneSpots();
        _layout.AcHidden = true;
        var vm = Groups.FirstOrDefault(g => g.IsAc);
        if (vm != null) RemoveGroup(vm);
        else
        {
            // 总开关关着时 VM 已被清空，但模型还留在配置里：一并删掉，
            // 否则下次启动 ShowAll 会把这块分区复活，设置开关却显示「关」
            _layout.Groups.RemoveAll(g => g.IsAc);
            Save();
        }
    }

    /// <summary>设置页重新打开空调分区。</summary>
    public static DesktopAcVm? ShowAc()
    {
        if (_main == null) return null;
        var existing = Groups.FirstOrDefault(g => g.IsAc);
        if (existing != null)
        {
            OpenWindow(existing);
            return existing as DesktopAcVm;
        }
        var model = _layout.Groups.FirstOrDefault(g => g.IsAc) ?? RestoredAcModel();
        if (!_layout.Groups.Contains(model)) _layout.Groups.Add(model);
        _layout.AcHidden = false;
        Save();
        var vm = new DesktopAcVm(_main, model);
        Groups.Add(vm);
        OpenWindow(vm);
        return vm;
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
        // 模型随关闭被移出清单，眼下在哪宽多少得先单独记一份：重新打开时回原位
        CaptureZoneSpots();
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
        var model = _layout.Groups.FirstOrDefault(g => g.IsMedia) ?? RestoredMediaModel();
        if (!_layout.Groups.Contains(model)) _layout.Groups.Add(model);
        _layout.MediaHidden = false;
        Save();
        var vm = CreateVm(_main, model);
        Groups.Add(vm);
        OpenWindow(vm);
        return vm;
    }

    // ================= 监控分区 =================

    /// <summary>监控分区重新出现时的模型：规则同媒体分区，上次在哪儿就在哪儿。</summary>
    private static DesktopGroupModel RestoredCameraModel()
    {
        var spot = _layout.CameraSpot;
        if (spot == null) return NewCameraModel();
        return new DesktopGroupModel
        {
            Kind = DesktopGroupModel.ZoneCamera,
            Name = "监控",
            X = spot.X,
            Y = spot.Y,
            Width = SpotWidth(spot.Width),
            ScreenSig = spot.ScreenSig ?? "",
        };
    }

    /// <summary>监控分区第一次出现：跟媒体分区同列，从这列顶端往下找第一块没被占的位置。</summary>
    private static DesktopGroupModel NewCameraModel()
    {
        const double width = 320;
        const double height = 250;
        const double margin = 24;
        var media = _layout.Groups.FirstOrDefault(g => g.IsMedia);
        var (vx, vy, vw, vh) = DesktopEmbed.VirtualScreen();
        var x = media?.X ?? (vx + vw - width - margin);
        var y = vy + margin;
        for (var i = 0; i < 8 && y + height < vy + vh; i++)
        {
            if (IsFreeSpot(x, y, width, height)) break;
            y += height + 12;
        }
        return new DesktopGroupModel
        {
            Kind = DesktopGroupModel.ZoneCamera,
            Name = "监控",
            Width = width,
            X = Math.Round(x),
            Y = Math.Round(y),
        };
    }

    /// <summary>
    /// 这块矩形有没有压到已经摆出来的分区。窗口已开就按真实窗口尺寸算
    /// （设备分区高度是内容撑出来的，模型里的 Height 对它们是 0），没开就用模型兜底。
    /// </summary>
    private static bool IsFreeSpot(double x, double y, double width, double height)
    {
        foreach (var g in Groups)
        {
            var w = g.Model.Width;
            var h = g.Model.Height;
            if (Windows.TryGetValue(g.Model.Id, out var win))
            {
                if (win.Width > 0) w = win.Width;
                if (win.Height > 0) h = win.Height;
            }
            if (h <= 0) h = 200;
            if (x < g.Model.X + w && x + width > g.Model.X &&
                y < g.Model.Y + h && y + height > g.Model.Y)
                return false;
        }
        // 模型里还留着但窗口没开的（例如总开关忙着重建），按模型位置也算占用，免得叠上去
        foreach (var m in _layout.Groups)
        {
            if (Groups.Any(g => g.Model.Id == m.Id)) continue;
            var h = m.Height > 0 ? m.Height : 200;
            if (x < m.X + m.Width && x + width > m.X && y < m.Y + h && y + height > m.Y)
                return false;
        }
        return true;
    }

    /// <summary>桌面菜单里的「关闭监控分区」：收掉窗口并停流，记住不再自动放回。</summary>
    public static void HideCamera()
    {
        // 模型随关闭被移出清单，眼下在哪儿得先单独记一份：重新打开时回原位
        CaptureZoneSpots();
        _layout.CameraHidden = true;
        var vm = Groups.FirstOrDefault(g => g.IsCamera);
        if (vm != null) RemoveGroup(vm);
        else
        {
            // 总开关关着时 VM 已被清空，但模型还留在配置里：一并删掉，
            // 否则下次启动 ShowAll 会把这块分区复活，设置开关却显示「关」
            _layout.Groups.RemoveAll(g => g.IsCamera);
            Save();
        }
    }

    /// <summary>设置页重新打开监控分区。</summary>
    public static DesktopCameraVm? ShowCamera()
    {
        if (_main == null) return null;
        var existing = Groups.FirstOrDefault(g => g.IsCamera);
        if (existing != null)
        {
            OpenWindow(existing);
            return existing as DesktopCameraVm;
        }
        var model = _layout.Groups.FirstOrDefault(g => g.IsCamera) ?? RestoredCameraModel();
        if (!_layout.Groups.Contains(model)) _layout.Groups.Add(model);
        _layout.CameraHidden = false;
        Save();
        var vm = new DesktopCameraVm(_main, model);
        Groups.Add(vm);
        OpenWindow(vm);
        return vm;
    }

    // ================= 数据卡 =================

    /// <summary>数据卡指标清单（设置页按这个顺序铺「添加」按钮）。</summary>
    public static IReadOnlyList<DataCardDef> DataCards => DataCardCatalog.All;

    /// <summary>某个指标的数据卡当前在不在桌面上（配置存在且 Shown=true）。</summary>
    public static bool IsDataCardShown(string key)
    {
        var model = _layout.Groups.FirstOrDefault(g => g.IsData && g.Metric == key);
        return model is { Shown: true };
    }

    public static DataCardVm? FindDataCard(string key)
        => Groups.OfType<DataCardVm>().FirstOrDefault(vm => vm.MetricKey == key);

    /// <summary>
    /// 添加 / 重新显示一张数据卡。同一个指标 v1 只保留一张：已有配置就把它重新亮出来，
    /// 不另建一张叠在上面。第一次添加时按「右列自上而下、避开所有分区」挑一个空位。
    /// </summary>
    public static DataCardVm? AddDataCard(string key)
    {
        if (_main == null) return null;
        var def = DataCardCatalog.Find(key);
        if (def is null) return null;

        var model = _layout.Groups.FirstOrDefault(g => g.IsData && g.Metric == key);
        if (model is null)
        {
            var (x, y) = NextCardSpot(def);
            model = new DesktopGroupModel
            {
                Kind = DesktopGroupModel.ZoneData,
                Name = def.Title,
                Metric = key,
                Width = def.DefaultWidth,
                Height = def.DefaultHeight,
                X = Math.Round(x),
                Y = Math.Round(y),
            };
            _layout.Groups.Add(model);
            Save();
        }
        else
        {
            model.Shown = true;
            model.Width = model.Width > 1 ? model.Width : def.DefaultWidth;
            model.Height = model.Height > 1 ? model.Height : def.DefaultHeight;
            Save();
        }

        var vm = Groups.OfType<DataCardVm>().FirstOrDefault(v => v.Model.Id == model.Id);
        if (vm is null)
        {
            try
            {
                vm = new DataCardVm(_main, model);
            }
            catch (Exception ex)
            {
                DesktopLog.Write($"[data-card] 创建 VM 失败 key={key}：{ex}");
                _main.AppendLog($"桌面数据卡创建失败：{ex.Message}");
                return null;
            }
            Groups.Add(vm);
        }
        else
        {
            vm.Refresh();
        }

        EnsureEnabled();
        OpenWindow(vm);
        _main.AppendLog($"桌面数据卡「{def.Title}」已显示");
        NotifyDataCardStateChanged();
        return vm;
    }

    /// <summary>设置页开关 / 右键「关闭卡片」：只收起窗口，位置尺寸都保留，下次原样回来。</summary>
    public static void CloseDataCard(DataCardVm vm)
    {
        vm.Model.Shown = false;
        if (Windows.Remove(vm.Model.Id, out var win))
        {
            try { win.Close(); } catch { }
        }
        Groups.Remove(vm);
        vm.Detach();
        CloseGroupDialogs(vm);
        NotifyGroupsChanged();
        Save();
        _main?.AppendLog($"桌面数据卡「{vm.Definition.Title}」已收起");
        NotifyDataCardStateChanged();
    }

    /// <summary>设置页开关：true = 重新显示（没有就新建），false = 收起。</summary>
    public static void SetDataCardShown(string key, bool on)
    {
        if (on)
        {
            AddDataCard(key);
            return;
        }

        var vm = Groups.OfType<DataCardVm>().FirstOrDefault(v => v.MetricKey == key);
        if (vm != null)
        {
            CloseDataCard(vm);
            return;
        }

        // 总开关关着时 VM 已被清空，但配置还留着：把 Shown 改掉，下次启动不再复活
        var model = _layout.Groups.FirstOrDefault(g => g.IsData && g.Metric == key);
        if (model is { Shown: true })
        {
            model.Shown = false;
            Save();
            NotifyDataCardStateChanged();
        }
    }

    /// <summary>「移除数据卡」：删掉配置，下次不会再出现；与关闭（保留配置）区分。</summary>
    public static void RemoveDataCard(DataCardVm vm)
    {
        RemoveGroup(vm);
        NotifyDataCardStateChanged();
    }

    /// <summary>卡窗右键菜单里的「重置大小」：回到该指标的默认档（大卡 400×200，小卡 200×100）。</summary>
    public static void ResetDataCardSize(DataCardVm vm)
    {
        vm.Width = vm.Definition.DefaultWidth;
        vm.Height = vm.Definition.DefaultHeight;
        Save();
    }

    /// <summary>右键菜单直接把卡片切到大 / 小档；尺寸变化由卡窗自己同步到窗口。</summary>
    public static void SetDataCardSize(DataCardVm vm, bool compact)
    {
        vm.Width = compact ? 200 : 400;
        vm.Height = compact ? 100 : 200;
        Save();
    }

    /// <summary>数据卡右键菜单：贴右下角弹出，再点一次同一个卡片就收起。</summary>
    public static void OpenCardMenu(DataCardVm vm, PixelRect anchor)
    {
        if (_cardMenu is { IsVisible: true } open && ReferenceEquals(open.Target, vm))
        {
            open.Close();
            return;
        }

        _cardMenu?.Close();

        var menu = new CardMenuWindow();
        _cardMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_cardMenu, menu)) _cardMenu = null;
        };
        menu.Bind(vm, anchor);
        menu.Show();
    }

    /// <summary>
    /// 新数据卡的默认落点：优先落在本程序桌面窗口最多的那块屏上——用户把组件都摆在哪块屏，
    /// 新卡就跟着放哪块，而不是永远回主屏；在那块屏右侧一列自上而下找空位。
    /// </summary>
    private static (double X, double Y) NextCardSpot(DataCardDef def)
    {
        const double margin = 24;
        var monitor = BusiestMonitor();
        var area = monitor?.WorkArea ?? new PixelRect(0, 0, 1920, 1080);
        var scale = monitor?.Scaling ?? 1;
        var w = def.DefaultWidth * scale;
        var h = def.DefaultHeight * scale;
        var gap = GroupGapDip * scale;
        var x = area.Right - w - margin;

        for (var y = area.Y + margin; y + h <= area.Bottom - margin; y += h + gap)
        {
            if (IsSpotFree(x, y, w, h, gap)) return (x / scale, y / scale);
        }

        return NearestFreeSpotOfSize(w, h, (area.Right - w, area.Y + margin), scale, area);
    }

    /// <summary>
    /// 本程序桌面窗口最多的那块屏：按每个窗口的中心落在哪块屏统计。分区、数据卡、媒体、空调、
    /// 监控都算——用户把组件堆在哪块屏，新卡就跟着去哪儿。一块都数不到时回主屏。
    /// </summary>
    private static MonitorInfo? BusiestMonitor()
    {
        var monitors = MonitorLayout.All();
        if (monitors.Count == 0) return MonitorLayout.Fallback();

        var counts = new int[monitors.Count];
        foreach (var win in Windows.Values)
        {
            var h = win.Handle;
            if (h == nint.Zero || !DesktopNative.GetWindowRect(h, out var rect)) continue;
            var center = new PixelPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            for (var i = 0; i < monitors.Count; i++)
            {
                if (!monitors[i].Bounds.Contains(center)) continue;
                counts[i]++;
                break;
            }
        }

        var best = -1;
        for (var i = 0; i < monitors.Count; i++)
        {
            if (counts[i] == 0) continue;
            // 两块屏一样多时认主屏：更符合「默认落在常用的那块」
            if (best < 0 || counts[i] > counts[best]
                || (counts[i] == counts[best] && monitors[i].IsPrimary)) best = i;
        }
        return best >= 0 ? monitors[best] : MonitorLayout.Fallback();
    }

    /// <summary>在指定屏的工作区里找离参考点最近、且不与任何分区相交的落点（返回 DIP 坐标）。</summary>
    private static (double X, double Y) NearestFreeSpotOfSize(
        double wPx, double hPx, (double X, double Y) fromPx, double scale, PixelRect area)
    {
        const double margin = 24;
        var gap = GroupGapDip * scale;

        var found = false;
        double bestD = double.MaxValue, bestX = area.X + margin, bestY = area.Y + margin;

        for (var y = area.Y + margin; y + hPx <= area.Bottom - margin; y += SpotStepPx)
        {
            for (var x = area.X + margin; x + wPx <= area.Right - margin; x += SpotStepPx)
            {
                if (!IsSpotFree(x, y, wPx, hPx, gap)) continue;

                var d = Math.Abs(x - fromPx.X) + Math.Abs(y - fromPx.Y);
                if (d >= bestD) continue;

                bestD = d;
                bestX = x;
                bestY = y;
                found = true;
            }
        }

        var px = found ? bestX : area.X + margin;
        var py = found ? bestY : area.Y + margin;
        return (px / scale, py / scale);
    }

    public static void Save()
    {
        // 每次落盘顺手刷新媒体 / 空调分区的「上次落位」：等它们被关掉时模型已不在清单里，
        // 那时再读就晚了（拖动、拉伸结束都由调用方走到这里）
        CaptureZoneSpots();
        DesktopGroupStore.Save(_layout);
    }

    /// <summary>把清单里还开着的媒体 / 空调分区的落位记进配置，供隐藏后重新打开时复原。</summary>
    private static void CaptureZoneSpots()
    {
        foreach (var g in _layout.Groups)
        {
            if (g.IsMedia) _layout.MediaSpot = ZoneSpot.From(g);
            else if (g.IsAc) _layout.AcSpot = ZoneSpot.From(g);
            else if (g.IsCamera) _layout.CameraSpot = ZoneSpot.From(g);
        }
    }

    /// <summary>外观变更：重算所有分区的画刷，并在停手后让窗口重打一次磨砂（落盘由调用方按节流决定）。</summary>
    public static void ApplyAppearance()
    {
        var look = Look;
        DesktopLog.Write($"[appearance] glass={look.Glass} opacity={look.Opacity:0.###} " +
                         $"density={DesktopPalette.CardDensity(look):0.###} color={look.BgColor} " +
                         $"windows={Windows.Count} groups={Groups.Count}");

        // 画刷每个 tick 都换：底色 alpha 是纯内容层，Avalonia 自己合并重绘，屏上跟手且不闪。
        foreach (var vm in Groups) vm.RefreshAppearance();

        // 材质不能跟着 tick 走：窗口级 ACCENT 的模糊是 DWM 按窗口 invalidate 取样的快照，
        // 重建一次要清 ACCENT + 重打 + 推一帧，连续做会在拖动时肉眼可见地抖。
        // 所以只做去抖：最后一次改动之后 220ms 没新请求，才真正打一次（松手后屏上一定是最终值）。
        _appearanceTimer?.Dispose();
        _appearanceTimer = DispatcherTimer.RunOnce(() =>
        {
            _appearanceTimer = null;
            ApplyChromeAndMaterial();
        }, TimeSpan.FromMilliseconds(220));
    }

    private static void ApplyChromeAndMaterial()
    {
        foreach (var win in Windows.Values) win.ReapplyAppearance();
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
        var anchor = _layout.Groups.LastOrDefault(g => !g.IsMedia && !g.IsData)
                     ?? _layout.Groups.LastOrDefault(g => !g.IsData)
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
            _picker = new TargetPickerWindow();
            _picker.Closed += (_, _) => _picker = null;
        }
        _picker.ConfigureForGroup(vm);
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
            anchor = groupWin.Window;

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

    /// <summary>空调名：弹一个贴着的空调选择窗，选完即写回全局默认空调。</summary>
    public static void OpenAcPicker(DesktopAcVm vm, PixelRect anchor)
    {
        if (_acPicker is { IsVisible: true } open && ReferenceEquals(open.Target, vm))
        {
            open.Close();
            return;
        }
        _acPicker?.Close();

        var win = new DesktopAcPickWindow();
        _acPicker = win;
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_acPicker, win)) _acPicker = null;
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
        if (_acPicker is { IsVisible: true } a && ReferenceEquals(a.Target, vm)) a.Close();
        if (_rename is { IsVisible: true } r && ReferenceEquals(r.Target, vm)) r.Close();
        if (_cardMenu is { IsVisible: true } c && ReferenceEquals(c.Target, vm)) c.Close();
    }

    /// <summary>切换主题后，分区底色要跟着主题卡片色重算。</summary>
    public static void OnThemeChanged()
    {
        foreach (var vm in Groups) vm.RefreshAppearance();
        foreach (var win in Windows.Values) win.ApplyChrome();
    }
}
