using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

public partial class MainWindow
{
    private Control? _mainContentRoot;
    private INameScope? _mainContentScope;
    private MainViewModel? _observedMainViewModel;
    private DispatcherTimer? _releasedContentTrimTimer;
    private bool _deferInitialContent;
    private bool _presentingHiddenFrame;
    private bool _changingMainContent;

    private bool IsMainContentLoaded => _mainContentRoot is not null;
    private T? FindMainControl<T>(string name) where T : Control => _mainContentScope?.Find<T>(name);

    // 不保存旧树中的命名控件；隐藏后只保留当前模板scope的空引用。
    private Border SearchBox => FindMainControl<Border>("SearchBox")!;
    private TextBox SearchInput => FindMainControl<TextBox>("SearchInput")!;
    private Button XiaoAiBtn => FindMainControl<Button>("XiaoAiBtn")!;
    private Button MusicBtn => FindMainControl<Button>("MusicBtn")!;
    private Border? DeviceMask => FindMainControl<Border>("DeviceMask");
    private Border? DeviceMaskLight => FindMainControl<Border>("DeviceMaskLight");
    private DeviceDrawerView? DeviceDrawerView => FindMainControl<DeviceDrawerView>("DeviceDrawerView");
    private VoicePanelView? VoicePanelView => FindMainControl<VoicePanelView>("VoicePanelView");
    private MusicPanelView? MusicPanelView => FindMainControl<MusicPanelView>("MusicPanelView");

    public override void Show()
    {
        if (!_deferInitialContent && !_presentingHiddenFrame && WindowState != WindowState.Minimized)
            EnsureMainContent();
        base.Show();
    }

    internal void CompleteSilentStartup() => _deferInitialContent = false;

    private void UpdateMainContentLifetime()
    {
        if (_changingMainContent) return;
        if (!IsVisible || WindowState == WindowState.Minimized)
            ReleaseMainContent();
        else if (!_deferInitialContent && !_presentingHiddenFrame)
            EnsureMainContent();
    }

    private void EnsureMainContent()
    {
        if (_mainContentRoot is not null || _changingMainContent || _memoryTrimmersClosed) return;
        _releasedContentTrimTimer?.Stop();
        if (!Resources.TryGetResource("MainWindowUiTemplate", ActualThemeVariant, out var resource)
            || resource is not DataTemplate template) return;
        _changingMainContent = true;
        try
        {
            var built = TemplateContent.Load(template.Content)
                ?? throw new InvalidOperationException("Main window content could not be constructed.");
            var root = built.Result;
            root.DataContext = DataContext;
            _mainContentRoot = root;
            _mainContentScope = built.NameScope;
            Content = root;
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_mainContentRoot, root) || Vm is not { } vm) return;
                if (vm.IsDrawerOpen) AnimateDeviceDrawer(true);
                if (vm.IsVoiceOpen) { AlignVoicePanel(); VoicePanelView?.Animate(true); }
                if (vm.IsMusicOpen) { AlignMusicPanel(); MusicPanelView?.Animate(true); }
            }, DispatcherPriority.Loaded);
        }
        finally { _changingMainContent = false; }
    }

    private void ReleaseMainContent()
    {
        if (_mainContentRoot is not { } root || _changingMainContent) return;
        _changingMainContent = true;
        try
        {
            _searchOpen = false;
            if (FocusManager?.GetFocusedElement() is Avalonia.Visual focused && IsWithin(focused, root))
                FocusManager.ClearFocus();
            // Popup拥有独立顶层；先关闭，避免PlacementTarget把旧主界面保活。
            foreach (var control in root.GetVisualDescendants().OfType<Control>().Prepend(root).ToArray())
            {
                if (control.ContextMenu is { IsOpen: true } menu) menu.Close();
                control.ContextFlyout?.Hide();
                if (control is ComboBox combo) combo.IsDropDownOpen = false;
                if (control is Button button) button.Flyout?.Hide();
            }
            _mainContentRoot = null;
            _mainContentScope = null;
            Content = null;
            if (Vm is { } vm)
            {
                vm.CloseOverlaysCommand.Execute(null);
                vm.SearchText = string.Empty;
            }
            foreach (var items in root.GetVisualDescendants().OfType<ItemsControl>().ToArray())
                items.ItemsSource = null;
            root.DataContext = null;
            RequestReleasedContentTrim();
        }
        finally { _changingMainContent = false; }
    }

    private void RequestReleasedContentTrim()
    {
        if (_memoryTrimmersClosed) return;
        // 抽屉/遮罩最多约300ms才释放动画引用，等它们结束后再收旧树。
        _releasedContentTrimTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _releasedContentTrimTimer.Tick -= OnReleasedContentTrim;
        _releasedContentTrimTimer.Tick += OnReleasedContentTrim;
        _releasedContentTrimTimer.Stop();
        _releasedContentTrimTimer.Start();
    }

    private void OnReleasedContentTrim(object? sender, EventArgs e)
    {
        _releasedContentTrimTimer?.Stop();
        if (!IsMainContentLoaded) MemoryTrimmer.TrimReleasedUi();
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsMainContentLoaded) return;
        if (e.PropertyName is not (nameof(MainViewModel.IsDrawerOpen)
            or nameof(MainViewModel.IsVoiceOpen) or nameof(MainViewModel.IsMusicOpen))) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsMainContentLoaded || Vm is not { } vm) return;
            if (e.PropertyName == nameof(MainViewModel.IsDrawerOpen)) AnimateDeviceDrawer(vm.IsDrawerOpen);
            if (e.PropertyName == nameof(MainViewModel.IsVoiceOpen))
            {
                if (vm.IsVoiceOpen) AlignVoicePanel();
                VoicePanelView?.Animate(vm.IsVoiceOpen);
            }
            if (e.PropertyName == nameof(MainViewModel.IsMusicOpen))
            {
                if (vm.IsMusicOpen) AlignMusicPanel();
                MusicPanelView?.Animate(vm.IsMusicOpen);
            }
        });
    }

}
