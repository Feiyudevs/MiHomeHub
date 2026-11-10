using System;
using System.ComponentModel;
using Avalonia.Controls;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 场景详情独立弹窗的窗口壳：里面就是 <see cref="SceneEditorView"/>，
/// 数据与保存 / 删除 / 新建等命令仍挂在主视图模型上，和主窗口里那份完全一致。
/// 打开 / 关闭由 <see cref="MainViewModel.IsSceneEditorOpen"/> 驱动，用户直接关窗时反向收编辑态。
/// </summary>
public partial class SceneEditorWindow : Window
{
    private readonly MainViewModel _vm;

    public SceneEditorWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) => _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    /// <summary>按屏幕居中显示（不依赖父窗口，桌面弹窗发起时也能弹在视野中间）。</summary>
    public void ShowCentered()
    {
        PopupPlacement.CenterOnScreen(this, Width, Height);
        if (!IsVisible) Show();
        Activate();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSceneEditorOpen) && !_vm.IsSceneEditorOpen)
            Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // 用户自己关掉窗口（Esc / Alt+F4）：把编辑态一起收掉，免得下次打开残留半个面板
        if (_vm.IsSceneEditorOpen) _vm.CloseSceneEditorCommand.Execute(null);
    }
}
