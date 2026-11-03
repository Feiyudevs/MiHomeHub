using System;
using System.Collections.Generic;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHome.Models;
using MiHome.Services;

namespace MiHome.ViewModels;

/// <summary>
/// 桌面分区外观：背景色 / 背景透明度 / 毛玻璃。
/// 改动即时生效（拖动滑杆就能看到），落盘做 400ms 节流，避免拖动过程反复写文件。
/// </summary>
public partial class DesktopAppearanceVm : ObservableObject
{
    /// <summary>
    /// 背景浓度的滑杆范围与默认值（百分比）。值直接就是卡片底色的 alpha，
    /// 屏幕上的最终浓度还要叠一层 DWM 磨砂染色（DesktopPalette.TintOpacity，随浓度联动）。
    /// 0% = 完全无底色、纯磨砂；72% 已接近实心。
    /// </summary>
    public const double MinOpacityPercent = 0;
    public const double MaxOpacityPercent = 72;

    /// <summary>默认浓度：DeskBox 那一档（= DesktopPalette.DefaultDensity）。</summary>
    public const double DefaultOpacityPercent = 34;

    /// <summary>
    /// 分区底色预设：直接照抄 Zonex（SettingsWindow.FenceColorPresetList）。
    /// 只存 RGB，透明度另由滑杆控制；首位是默认主紫。
    /// </summary>
    public static readonly string[] PresetHexes =
    [
        "#7043ED", "#101018", "#81F467", "#ED4343", "#43ED61", "#4399ED", "#ED43BD",
    ];

    private bool _loading;
    private IDisposable? _saveTimer;

    [ObservableProperty] private bool _glass = true;
    [ObservableProperty] private double _opacityPercent = DefaultOpacityPercent;
    [ObservableProperty] private bool _followTheme = true;
    [ObservableProperty] private string _colorHex = "";

    /// <summary>预设色块（选中态随当前 hex 重算，不是记索引：手输任意 hex 也能对上）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ColorSwatch> Presets { get; } = new();

    /// <summary>关窗回调，由窗口挂上。</summary>
    public Action? CloseRequested { get; set; }

    public DesktopAppearanceVm()
    {
        foreach (var hex in PresetHexes) Presets.Add(new ColorSwatch { Hex = hex });
        RefreshPresets();
    }

    public void Reload()
    {
        _loading = true;
        try
        {
            var look = DesktopHost.Look;
            Glass = look.Glass;
            OpacityPercent = Math.Round(look.ClampedOpacity * 100);
            FollowTheme = string.IsNullOrWhiteSpace(look.BgColor);
            ColorHex = look.BgColor;
            OnPropertyChanged(nameof(OpacityText));
            OnPropertyChanged(nameof(IsCustom));
            RefreshPresets();
        }
        finally
        {
            _loading = false;
        }
    }

    public string OpacityText => $"{OpacityPercent:0}%";

    public bool IsCustom => !FollowTheme;

    partial void OnGlassChanged(bool value) => Push();

    partial void OnOpacityPercentChanged(double value)
    {
        OnPropertyChanged(nameof(OpacityText));
        Push();
    }

    partial void OnFollowThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCustom));
        if (value && ColorHex.Length > 0)
        {
            _loading = true;
            ColorHex = "";
            _loading = false;
        }
        RefreshPresets();
        Push();
    }

    /// <summary>选自定义色：颜色走 hex，跟随主题自动关闭。</summary>
    [RelayCommand]
    private void PickColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        _loading = true;
        ColorHex = hex;
        FollowTheme = false;
        _loading = false;
        OnPropertyChanged(nameof(IsCustom));
        RefreshPresets();
        Push();
    }

    /// <summary>恢复默认：底色跟随主题、透明度回到默认、毛玻璃打开。</summary>
    [RelayCommand]
    private void RestoreDefault()
    {
        _loading = true;
        FollowTheme = true;
        ColorHex = "";
        Glass = true;
        OpacityPercent = DefaultOpacityPercent;
        _loading = false;
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(OpacityText));
        RefreshPresets();
        Push();
    }

    /// <summary>按「当前 hex 是否等于预设色」重算选中态；跟随主题时全部不选中。</summary>
    private void RefreshPresets()
    {
        foreach (var swatch in Presets)
        {
            swatch.IsSelected = !FollowTheme
                && !string.IsNullOrWhiteSpace(ColorHex)
                && string.Equals(swatch.Hex, ColorHex, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    private void Close()
    {
        DesktopHost.Save();
        CloseRequested?.Invoke();
    }

    private void Push()
    {
        if (_loading) return;
        var look = DesktopHost.Look;
        look.Glass = Glass;
        look.Opacity = Math.Clamp(OpacityPercent / 100.0, MinOpacityPercent / 100.0, 1.0);
        look.BgColor = FollowTheme ? "" : ColorHex;
        DesktopHost.ApplyAppearance();

        _saveTimer?.Dispose();
        _saveTimer = DispatcherTimer.RunOnce(() =>
        {
            _saveTimer = null;
            DesktopHost.Save();
        }, TimeSpan.FromMilliseconds(400));
    }
}

/// <summary>一个预设色块：Hex 直接当画刷用，选中态由 VM 按当前 hex 重算。</summary>
public partial class ColorSwatch : ObservableObject
{
    public required string Hex { get; init; }

    public Avalonia.Media.IBrush Brush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Hex));

    [ObservableProperty] private bool _isSelected;
}
