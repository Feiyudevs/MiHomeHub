using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
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

        // 上次退出时开着的分区，这次启动直接贴回桌面
        if (_layout.Enabled)
            ShowAll();
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

        var (x, y) = NextFreeSpot();
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
    /// 优先右→下→左→上找不与现有分区重叠的落点，都放不下退回全局错位摆放。
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

    /// <summary>空白分区尺寸未知，按一列卡片的典型大小参与占位判断。</summary>
    private const double BlankZoneGuessW = 320;
    private const double BlankZoneGuessH = 240;

    /// <summary>在参考分区附近找空位：右、下、左、上依次试，全被占用退回全局错位摆放。</summary>
    private static (double X, double Y) NextSpotNear(DesktopGroupModel refModel)
    {
        const double gap = 12;
        var w = refModel.Width;
        var candidates = new (double X, double Y)[]
        {
            (refModel.X + w + gap, refModel.Y),          // 右侧
            (refModel.X, refModel.Y + BlankZoneGuessH + gap),   // 下方
            (refModel.X - BlankZoneGuessW - gap, refModel.Y),   // 左侧
            (refModel.X, refModel.Y - BlankZoneGuessH - gap),   // 上方
        };

        foreach (var (x, y) in candidates)
        {
            if (IsFree(x, y, BlankZoneGuessW, BlankZoneGuessH)) return (x, y);
        }
        return NextFreeSpot();
    }

    /// <summary>矩形 (x, y, w, h) 是否不与任何现有分区重叠（分区没有存高度，按估算值参与判断）。</summary>
    private static bool IsFree(double x, double y, double w, double h)
    {
        foreach (var g in _layout.Groups)
        {
            if (x < g.X + g.Width && g.X < x + w &&
                y < g.Y + BlankZoneGuessH && g.Y < y + h)
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
    }

    public static void ShowAll()
    {
        if (_main == null) return;
        EnsureMediaGroup();
        foreach (var model in _layout.Groups.ToList())
        {
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
            CloseGroupDialogs(vm);
        }
        Groups.Clear();
    }

    /// <summary>总开关：关掉只收起窗口，配置与分区内容保留。</summary>
    public static void SetEnabled(bool on)
    {
        _layout.Enabled = on;
        if (on) ShowAll();
        else HideAll();
        Save();
    }

    public static void RemoveGroup(DesktopZoneVm vm)
    {
        _layout.Groups.RemoveAll(g => g.Id == vm.Model.Id);
        Groups.Remove(vm);
        if (Windows.Remove(vm.Model.Id, out var win))
        {
            try { win.Close(); } catch { }
        }
        if (vm is DesktopMediaVm media) media.Detach();
        // 这个分区开着的弹窗一起收掉，避免对着已解散的分区操作
        CloseGroupDialogs(vm);
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
        else Save();
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

    /// <summary>新建分区时的错位摆放：沿对角线找没被占用的位置，超出屏幕就回到左上角排队。</summary>
    private static (double X, double Y) NextFreeSpot()
    {
        var (vx, vy, vw, vh) = DesktopEmbed.VirtualScreen();
        const int step = 30;
        const int margin = 56;
        const int guessW = 340;
        const int guessH = 240;

        for (var i = 0; i < 40; i++)
        {
            var x = vx + margin + i * step;
            var y = vy + margin + i * step;
            if (x + guessW > vx + vw || y + guessH > vy + vh) break;
            var taken = _layout.Groups.Any(g => Math.Abs(g.X - x) < 22 && Math.Abs(g.Y - y) < 22);
            if (!taken) return (x, y);
        }

        var fallback = _layout.Groups.Count % 8;
        return (vx + margin, vy + margin + fallback * step);
    }

    // ================= 弹窗 =================

    /// <summary>设备卡「添加到桌面组件」：弹窗选择要加入的分区，或新建分区。</summary>
    public static void OpenGroupPicker(MijiaDevice device)
    {
        if (_groupPicker == null)
        {
            _groupPicker = new DesktopGroupPickWindow();
            _groupPicker.Closed += (_, _) => _groupPicker = null;
        }
        _groupPicker.Bind(device);
        PopupPlacement.CenterOnScreen(_groupPicker, _groupPicker.Width, _groupPicker.Height);
        if (!_groupPicker.IsVisible) _groupPicker.Show();
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
