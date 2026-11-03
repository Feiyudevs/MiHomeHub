using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;
using MiHome.Services;

namespace MiHome.ViewModels;

/// <summary>
/// 一个桌面设备分区的视图模型：持有模型、当前设备/场景实例与外观画刷。
/// 设备实例直接来自主视图模型的列表，开关状态跟随轮询自动刷新，不需要另一套同步逻辑。
/// 分区里的设备卡与主窗口列表是同一张（<see cref="IDeviceCardHost"/>），只是点卡片弹控制窗；
/// 场景卡只在分区里出现，点整卡即执行，下发逻辑同样复用主视图模型。
/// </summary>
public partial class DesktopGroupVm : DesktopZoneVm, IDeviceCardHost
{
    public ObservableCollection<MijiaDevice> Devices { get; } = new();

    public ObservableCollection<SceneCardItem> Scenes { get; } = new();

    // ===== 设备卡宿主契约 =====

    /// <summary>点卡片 → 弹窗里展示这台设备的控制内容。</summary>
    public ICommand OpenCardCommand => OpenDeviceCommand;

    /// <summary>拨开关 → 直接复用主视图模型的下发逻辑，状态跟着轮询回填。</summary>
    public ICommand ToggleSwitchCommand => Main.ToggleDeviceCommand;

    /// <summary>分区里右键「从分区移除」：只把设备移出本分区，设备本身与桌面组件配置都不动。</summary>
    public ICommand? CardMenuCommand => RemoveFromGroupCommand;

    public string CardMenuText => "从分区移除";

    /// <summary>减号图标：这是「移除」动作。</summary>
    public Geometry CardMenuIcon => CardMenuIcons.Remove;

    /// <summary>点场景卡 → 直接执行，与场景页那颗播放按钮同一个命令。</summary>
    public ICommand RunSceneCommand => Main.RunSceneCommand;

    public DesktopGroupVm(MainViewModel main, DesktopGroupModel model)
        : base(main, model)
    {
        Devices.CollectionChanged += OnContentChanged;
        Scenes.CollectionChanged += OnContentChanged;
        RefreshAppearance();
        LoadDevices();
    }

    public override bool ShowCount => true;

    public override string CountText => (Devices.Count + Scenes.Count).ToString();

    protected override string DangerText => "解散分区";

    protected override string DangerConfirmText => "确认解散？";

    public override string EmptyHint => "还没有内容，点击上方按钮添加设备或场景";

    public override bool IsEmpty => Devices.Count == 0 && Scenes.Count == 0;

    /// <summary>两块宫格各自决定显不显示：只有场景的分区不该看到一排空设备格。</summary>
    public bool HasDevices => Devices.Count > 0;

    public bool HasScenes => Scenes.Count > 0;

    private void OnContentChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasScenes));
        OnPropertyChanged(nameof(CanReorderDevices));
        OnPropertyChanged(nameof(CanReorderScenes));
    }

    /// <summary>按模型里的 did / scene_id 顺序重建列表；实例已在列表里则复用原实例（不重绑卡片）。</summary>
    public override void LoadDevices()
    {
        LoadDeviceList();
        LoadSceneList();
    }

    private void LoadDeviceList()
    {
        var pool = Main.AllDevices;
        var next = Model.Dids
            .Select(did => pool.FirstOrDefault(d => string.Equals(d.Did, did, StringComparison.Ordinal)))
            .Where(d => d != null)
            .Select(d => d!)
            .ToList();

        if (next.Count == Devices.Count)
        {
            var same = true;
            for (var i = 0; i < next.Count; i++)
            {
                if (!ReferenceEquals(next[i], Devices[i]))
                {
                    same = false;
                    break;
                }
            }
            if (same) return;
        }

        Devices.Clear();
        foreach (var d in next) Devices.Add(d);
    }

    /// <summary>场景每次刷新都是新实例，只有 id 能用来认人。</summary>
    private void LoadSceneList()
    {
        var pool = Main.SceneItems;
        var next = Model.SceneIds
            .Select(id => pool.FirstOrDefault(s => string.Equals(s.Scene.SceneId, id, StringComparison.Ordinal)))
            .Where(s => s != null)
            .Select(s => s!)
            .ToList();

        if (next.Count == Scenes.Count && next.SequenceEqual(Scenes)) return;

        Scenes.Clear();
        foreach (var s in next) Scenes.Add(s);
    }

    public void AddDevice(MijiaDevice device)
    {
        if (device == null || string.IsNullOrEmpty(device.Did)) return;
        if (Model.Dids.Contains(device.Did)) return;
        Model.Dids.Add(device.Did);
        if (!Devices.Contains(device)) Devices.Add(device);
        DesktopHost.Save();
    }

    public void RemoveDevice(MijiaDevice device)
    {
        if (device == null) return;
        Model.Dids.Remove(device.Did);
        Devices.Remove(device);
        DesktopHost.Save();
    }

    public bool Contains(string did) => Model.Dids.Contains(did);

    public void AddScene(SceneCardItem scene)
    {
        if (string.IsNullOrEmpty(scene?.Scene.SceneId)) return;
        var id = scene.Scene.SceneId;
        if (Model.SceneIds.Contains(id)) return;
        Model.SceneIds.Add(id);
        if (!Scenes.Contains(scene)) Scenes.Add(scene);
        DesktopHost.Save();
    }

    public void RemoveScene(SceneCardItem scene)
    {
        if (scene == null) return;
        Model.SceneIds.Remove(scene.Scene.SceneId);
        Scenes.Remove(scene);
        DesktopHost.Save();
    }

    public bool ContainsScene(string sceneId) => Model.SceneIds.Contains(sceneId);

    /// <summary>一面宫格要有两张以上卡才谈得上排序：只有一张时不亮拖拽辅助样式（亮了也拖不动，是骗人）。</summary>
    public bool CanReorderDevices => Devices.Count > 1;

    public bool CanReorderScenes => Scenes.Count > 1;

    /// <summary>
    /// 拖拽排序进行中：换顺序是先摘一张再插回原位，宿主窗口的「加内容自动加宽」
    /// 不能把这次 Insert 当成真的新增（条目数没变）。
    /// </summary>
    public bool IsReordering { get; private set; }

    /// <summary>拖拽排序落点：换显示顺序并把顺序写回模型。</summary>
    public void MoveDevice(int from, int to)
    {
        if (!Reorder(Devices, from, to)) return;
        Model.Dids = RemergeOrder(Model.Dids, Devices.Select(d => d.Did));
        DesktopHost.Save();
    }

    public void MoveScene(int from, int to)
    {
        if (!Reorder(Scenes, from, to)) return;
        Model.SceneIds = RemergeOrder(Model.SceneIds, Scenes.Select(s => s.Scene.SceneId));
        DesktopHost.Save();
    }

    /// <summary>
    /// 顺序回写：宫格里的 id 按新的显示顺序排回去，模型中解析不出实例的那些 id
    /// （切家庭后设备还没重新加载）不在宫格上，留在原槽位，免得一次拖拽把它们抹掉。
    /// </summary>
    private static List<string> RemergeOrder(List<string> model, IEnumerable<string> shown)
    {
        var order = shown.ToList();
        if (model.Count == order.Count) return order;

        var onGrid = order.ToHashSet(StringComparer.Ordinal);
        var next = new List<string>(model.Count);
        var i = 0;
        foreach (var id in model)
            next.Add(onGrid.Contains(id) ? order[i++] : id);
        return next;
    }

    /// <summary>先摘再插，不用 ObservableCollection.Move：ItemsControl 对 Move 通知的容器回收并不可以靠。</summary>
    private bool Reorder<T>(ObservableCollection<T> items, int from, int to)
    {
        IsReordering = true;
        try
        {
            return Move(items, from, to);
        }
        finally
        {
            IsReordering = false;
        }
    }

    private static bool Move<T>(ObservableCollection<T> items, int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= items.Count || to >= items.Count) return false;
        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
        return true;
    }

    [RelayCommand]
    private void AddDevice() => DesktopHost.OpenPicker(this);

    /// <summary>场景卡右键「从分区移除」：与设备同一套语义，只断开关联。</summary>
    [RelayCommand]
    private void RemoveSceneFromZone(SceneCardItem? scene)
    {
        if (scene == null || !ContainsScene(scene.Scene.SceneId)) return;
        var name = scene.Name;
        RemoveScene(scene);
        Main.AppendLog($"场景「{name}」已从分区「{Name}」移除");
    }

    [RelayCommand]
    private void OpenDevice(MijiaDevice? device)
    {
        if (device == null) return;
        // 带上自己：控制弹窗要落在点它的那块屏幕上
        DesktopHost.OpenDevicePanel(Main, device, this);
    }

    /// <summary>右键菜单「从分区移除」：只断开关联，设备仍在首页与「所有设备」里。</summary>
    [RelayCommand]
    private void RemoveFromGroup(MijiaDevice? device)
    {
        if (device == null || !Contains(device.Did)) return;
        var name = device.Name;
        RemoveDevice(device);
        Main.AppendLog($"「{name}」已从分区「{Name}」移除");
    }

    protected override void RemoveZone()
    {
        DesktopHost.RemoveGroup(this);
    }
}
