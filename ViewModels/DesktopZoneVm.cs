using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;
using MiHome.Services;

namespace MiHome.ViewModels;

/// <summary>
/// 桌面分区视图模型的公共部分：模型、位置/尺寸/折叠、外观画刷与危险操作二次确认。
/// 设备分区（<see cref="DesktopGroupVm"/>）与媒体分区（<see cref="DesktopMediaVm"/>）共用这一份，
/// 贴桌面窗的拖动、拉伸、磨砂重打都只认这里的成员，两种分区因此行为完全一致。
/// </summary>
public abstract partial class DesktopZoneVm : ObservableObject
{
    public MainViewModel Main { get; }

    public DesktopGroupModel Model { get; }

    [ObservableProperty] private string _name = "新分区";
    [ObservableProperty] private bool _collapsed;

    /// <summary>标题栏图钉：true 时这块分区脱离桌面层、浮在所有窗口之上。</summary>
    [ObservableProperty] private bool _isTopmost;

    /// <summary>重命名浮层是否正盖在名称上：为真时隐藏分区窗自己的名称文字，避免半透明浮层透出双重名称。</summary>
    [ObservableProperty] private bool _isRenaming;

    /// <summary>
    /// 内容区是否参与布局：折叠时先让 <see cref="ContentOpacity"/> 淡出（80ms），
    /// 淡完再收掉 —— 直接隐藏会让窗口高度先跳变，看起来像闪了一下。
    /// </summary>
    [ObservableProperty] private bool _contentVisible = true;

    [ObservableProperty] private double _width = DesktopGroupModel.DefaultWidth;
    [ObservableProperty] private IBrush _cardBrush = Brushes.Transparent;
    [ObservableProperty] private IBrush _hoverBrush = Brushes.Transparent;

    /// <summary>危险操作按钮当前文案：第一次点只改文案，再点一次才真执行。</summary>
    [ObservableProperty] private string _dissolveLabel = "";

    private IDisposable? _dissolveTimer;
    private IDisposable? _fadeTimer;

    protected DesktopZoneVm(MainViewModel main, DesktopGroupModel model)
    {
        Main = main;
        Model = model;
        _name = model.Name;
        _collapsed = model.Collapsed;
        _isTopmost = model.Topmost;
        _contentVisible = !model.Collapsed;
        _width = Math.Clamp(model.Width, DesktopGroupModel.MinWidth, DesktopGroupModel.MaxWidth);
        _dissolveLabel = DangerText;
    }

    /// <summary>这块分区是不是媒体（音响播放）分区。</summary>
    public bool IsMedia => Model.IsMedia;

    /// <summary>改名浮层覆盖的这块标题区（名称文字 + 设备数徽标）透明度：浮层盖上来时置 0（保留占位不回流），半透明浮层就不会透出双重名称。</summary>
    public double TitleOpacity => IsRenaming ? 0 : 1;

    partial void OnIsRenamingChanged(bool value) => OnPropertyChanged(nameof(TitleOpacity));

    /// <summary>标题栏右侧的计数徽标：媒体分区没有「几台设备」可数，不显示。</summary>
    public virtual bool ShowCount => false;

    public virtual string CountText => "";

    /// <summary>危险操作的原始文案（设备分区=解散分区，媒体分区=关闭媒体分区）。</summary>
    protected abstract string DangerText { get; }

    /// <summary>第一次点击后的确认文案。</summary>
    protected abstract string DangerConfirmText { get; }

    /// <summary>危险操作是否已进入「再点一次确认」态：菜单据此决定是留着还是关掉。</summary>
    public bool IsAwaitingDangerConfirm => DissolveLabel == DangerConfirmText;

    /// <summary>内容透明度：展开 1、折叠 0。折叠时内容已被裁掉，纯做淡出。</summary>
    public double ContentOpacity => Collapsed ? 0 : 1;

    /// <summary>折叠箭头角度：折叠时朝下（点了会展开），展开时旋 180° 朝上。</summary>
    public double CollapseArrowAngle => Collapsed ? 0 : 180;

    /// <summary>没有可用内容时的提示文案；与 <see cref="IsEmpty"/> 同时生效。</summary>
    public abstract string EmptyHint { get; }

    public abstract bool IsEmpty { get; }

    partial void OnCollapsedChanged(bool value)
    {
        Model.Collapsed = value;
        OnPropertyChanged(nameof(ContentOpacity));
        OnPropertyChanged(nameof(CollapseArrowAngle));
        DesktopHost.Save();

        _fadeTimer?.Dispose();
        if (value)
        {
            // 折叠：先淡出，淡完再收起内容（窗口随后收紧到标题栏高度）
            _fadeTimer = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                _fadeTimer = null;
                ContentVisible = false;
            }, TimeSpan.FromMilliseconds(90));
        }
        else
        {
            // 展开：内容立刻回来，由 Opacity 过渡做淡入
            ContentVisible = true;
            // 展开即对齐一次真实状态，避免看到最多 30 秒前的旧读数（含温湿度）
            Main.RequestZoneRefresh(includeMetrics: true);
        }
    }

    /// <summary>
    /// 鼠标移入分区（视图层转发进来）：只在展开状态下补一拍状态。
    /// 折叠的分区只剩标题栏、看不到设备，没必要为它发请求。
    /// </summary>
    public void OnPointerEnteredZone()
    {
        if (Collapsed) return;
        Main.RequestZoneRefresh(includeMetrics: false);
    }

    partial void OnNameChanged(string value)
    {
        Model.Name = value;
        DesktopHost.Save();
    }

    partial void OnWidthChanged(double value)
    {
        Model.Width = value;
        DesktopHost.Save();
    }

    partial void OnIsTopmostChanged(bool value)
    {
        Model.Topmost = value;
        DesktopHost.Save();
    }

    /// <summary>
    /// 外观（主题 / 背景色 / 透明度 / 毛玻璃）变更后重算画刷。
    /// 子类若还有依赖设备的显示内容，在这里一并重算。
    /// </summary>
    public virtual void RefreshAppearance()
    {
        var look = DesktopHost.Look;
        CardBrush = DesktopPalette.CardBrush(look);
        HoverBrush = DesktopPalette.HoverBrush(look);
    }

    /// <summary>设备列表换代（切家庭 / 刷新 / 缓存上屏）后重绑内容，由 DesktopHost 统一广播。</summary>
    public virtual void LoadDevices() { }

    [RelayCommand]
    protected void ToggleCollapse() => Collapsed = !Collapsed;

    [RelayCommand]
    private void OpenAppearance() => DesktopHost.OpenAppearance();

    [RelayCommand]
    private void Rename() => DesktopHost.RequestRename(this);

    /// <summary>危险操作：首次点击只进入确认态（3 秒后自动撤回），再点才真正移除。</summary>
    [RelayCommand]
    private void Dissolve()
    {
        if (DissolveLabel != DangerText)
        {
            _dissolveTimer?.Dispose();
            _dissolveTimer = null;
            RemoveZone();
            return;
        }

        DissolveLabel = DangerConfirmText;
        _dissolveTimer?.Dispose();
        _dissolveTimer = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            _dissolveTimer = null;
            DissolveLabel = DangerText;
        }, TimeSpan.FromSeconds(3));
    }

    /// <summary>确认之后到底做什么：解散分区或关闭媒体分区。</summary>
    protected abstract void RemoveZone();
}
