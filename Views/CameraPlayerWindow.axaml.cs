// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MiHomeHub.Services;

namespace MiHomeHub.Views;

/// <summary>放大看画面的独立窗：只负责摆放和打开，帧与解码都还在主视图模型里。</summary>
public partial class CameraPlayerWindow : Window
{
    private const string MaxData = "M0,0 L10,0 L10,10 L0,10 Z";
    private const string RestoreData = "M0,3 L7,3 L7,10 L0,10 Z M3,3 L3,0 L10,0 L10,7 L7,7";

    public CameraPlayerWindow()
    {
        InitializeComponent();
        // 系统标题栏去掉了，最大化后仍要贴满工作区，并把按钮图标换成「还原」
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            UpdateMaxGlyph();
        };
    }

    public void ShowCentered()
    {
        if (!IsVisible)
        {
            PopupPlacement.CenterOnScreen(this, Width, Height);
            Show();
        }
        else
        {
            Activate();
        }
    }

    private void UpdateMaxGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (MaxGlyph is not null)
        {
            MaxGlyph.Data = Avalonia.Media.Geometry.Parse(maximized ? RestoreData : MaxData);
        }
    }

    /// <summary>顶栏空白处按下就能拽着窗口走（按钮自己会吃掉点击，不会误拖）。</summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaxGlyph();
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
