// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Services;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

/// <summary>
/// 通用自定义调色盘：HSV 色相条 + 饱和度/明度方块 + HEX 输入 + 常用色。
/// 桌面分区背景色、SVG 图标颜色等地方共用，确认后把 #RRGGBB 交回调用方。
/// </summary>
public partial class ColorPickerWindow : Window
{
    private const string DefaultHex = "#7043ED";

    /// <summary>常用色和桌面外观、图标选色保持同一套，避免两个弹窗像两套产品。</summary>
    private static readonly string[] PaletteHexes =
    [
        "#FFFFFF", "#D8DEEA", "#8A93A6", "#101018", "#7043ED",
        "#7FABFF", "#4ADE80", "#FCD34D", "#F87171", "#ED43BD",
    ];

    private readonly List<ColorSwatch> _presets = new();
    private string _originHex = DefaultHex;
    private double _h;
    private double _s;
    private double _v;
    private bool _dragHue;
    private bool _dragSv;
    private bool _syncHex;

    public ColorPickerWindow() : this(DefaultHex)
    {
    }

    public ColorPickerWindow(string initialHex)
    {
        InitializeComponent();

        foreach (var hex in PaletteHexes) _presets.Add(new ColorSwatch { Hex = hex });
        PresetList.ItemsSource = _presets;
        Dialog.CloseCommand = new RelayCommand(Cancel);
        Load(initialHex);
    }

    /// <summary>以模态窗打开，返回确认的颜色；取消返回 null。</summary>
    public static Task<string?> PickAsync(Window owner, string initialHex)
        => new ColorPickerWindow(initialHex).ShowDialog<string?>(owner);

    private void Load(string initialHex)
    {
        var hex = ExpandHex(initialHex) ?? DefaultHex;
        _originHex = hex;
        var color = Color.Parse(hex);
        RgbToHsv(color.R, color.G, color.B, out _h, out _s, out _v);

        PreviewOld.Background = new SolidColorBrush(color);
        SetHexText(hex);
        UpdateVisuals();

        Opened += (_, _) =>
        {
            // 刚打开时方块可能还没完成布局，补一帧再摆准星
            Dispatcher.UIThread.Post(UpdateVisuals, DispatcherPriority.Loaded);
            HexBox.Focus();
        };
    }

    private void OnHuePointer(object? sender, RoutedEventArgs e)
    {
        if (e is not PointerEventArgs pe) return;

        if (e is PointerPressedEventArgs ppe)
        {
            if (!ppe.GetCurrentPoint(HueTrack).Properties.IsLeftButtonPressed) return;
            _dragHue = true;
            pe.Pointer.Capture(HueTrack);
        }
        else if (e is PointerReleasedEventArgs)
        {
            _dragHue = false;
            pe.Pointer.Capture(null);
        }
        else if (!_dragHue) return;

        var width = HueTrack.Bounds.Width;
        if (width <= 0) return;
        _h = Math.Clamp(pe.GetPosition(HueTrack).X / width, 0.0, 1.0) * 360.0;
        if (_h >= 360) _h = 0;
        UpdateVisuals();
    }

    private void OnSvPointer(object? sender, RoutedEventArgs e)
    {
        if (e is not PointerEventArgs pe) return;

        if (e is PointerPressedEventArgs ppe)
        {
            if (!ppe.GetCurrentPoint(SvArea).Properties.IsLeftButtonPressed) return;
            _dragSv = true;
            pe.Pointer.Capture(SvArea);
        }
        else if (e is PointerReleasedEventArgs)
        {
            _dragSv = false;
            pe.Pointer.Capture(null);
        }
        else if (!_dragSv) return;

        var width = SvArea.Bounds.Width;
        var height = SvArea.Bounds.Height;
        if (width <= 0 || height <= 0) return;
        var pos = pe.GetPosition(SvArea);
        _s = Math.Clamp(pos.X / width, 0.0, 1.0);
        _v = 1.0 - Math.Clamp(pos.Y / height, 0.0, 1.0);
        UpdateVisuals();
    }

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex }) return;
        var expanded = ExpandHex(hex);
        if (expanded is null) return;

        var color = Color.Parse(expanded);
        RgbToHsv(color.R, color.G, color.B, out _h, out _s, out _v);
        StatusText.Text = "";
        UpdateVisuals();
    }

    private void OnHexTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncHex) return;

        var text = HexBox.Text?.Trim() ?? "";
        var hex = ExpandHex(text);
        if (hex is null || hex.Length == 0) return;

        var color = Color.Parse(hex);
        RgbToHsv(color.R, color.G, color.B, out _h, out _s, out _v);
        StatusText.Text = "";

        // UpdateVisuals 会把输入框规范成 #RRGGBB，用 _syncHex 挡住这层回写再次触发解析
        _syncHex = true;
        try { UpdateVisuals(); }
        finally { _syncHex = false; }
    }

    private void OnHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyAndClose();
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e) => ApplyAndClose();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Cancel();

    private void ApplyAndClose()
    {
        var text = HexBox.Text?.Trim() ?? "";
        if (text.Length > 0)
        {
            var hex = ExpandHex(text);
            if (hex is null)
            {
                StatusText.Text = "颜色格式不对，用 #RGB 或 #RRGGBB";
                return;
            }
            Close(hex);
            return;
        }

        Close(HexFromHsv());
    }

    private void Cancel() => Close(null);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Cancel();
    }

    /// <summary>按 HSV 只改纯色 / 准星位置，不改布局尺寸，拖动时不闪。</summary>
    private void UpdateVisuals()
    {
        var hex = HexFromHsv();
        var color = Color.Parse(hex);

        PreviewNew.Background = new SolidColorBrush(color);
        HexLabel.Text = hex;
        RgbLabel.Text = $"{color.R}, {color.G}, {color.B}";
        SetHexText(hex);
        UpdatePresetSelection(hex);

        var trackWidth = HueTrack.Bounds.Width;
        var hueX = trackWidth <= 0 ? 0 : (_h / 360.0) * trackWidth;
        HueKnob.RenderTransform = new TranslateTransform(hueX - 7, 0);
        HsvToRgb(_h, 1.0, 1.0, out var hr, out var hg, out var hb);
        HueKnob.Fill = new SolidColorBrush(Color.FromArgb(255, hr, hg, hb));

        HsvToRgb(_h, 1.0, 1.0, out var sr, out var sg, out var sb);
        SvArea.Background = new SolidColorBrush(Color.FromArgb(255, sr, sg, sb));

        var innerW = Math.Max(0, SvArea.Bounds.Width
            - SvArea.BorderThickness.Left - SvArea.BorderThickness.Right);
        var innerH = Math.Max(0, SvArea.Bounds.Height
            - SvArea.BorderThickness.Top - SvArea.BorderThickness.Bottom);
        SvCursor.RenderTransform = new TranslateTransform(
            innerW * _s - 7, innerH * (1.0 - _v) - 7);

        // 浅色区域里的白色准星会看不见，暗处换深色描边
        SvCursor.BorderBrush = new SolidColorBrush(
            _v > 0.55 && _s < 0.45 ? Color.Parse("#404048") : Colors.White);
    }

    private void SetHexText(string hex)
    {
        _syncHex = true;
        try { HexBox.Text = hex; }
        finally { _syncHex = false; }
    }

    private void UpdatePresetSelection(string currentHex)
    {
        foreach (var preset in _presets)
            preset.IsSelected = string.Equals(preset.Hex, currentHex, StringComparison.OrdinalIgnoreCase);
    }

    private string HexFromHsv()
    {
        HsvToRgb(_h, _s, _v, out var r, out var g, out var b);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>把 #RGB 补成 #RRGGBB；空 / 非法返回 null。</summary>
    private static string? ExpandHex(string? raw)
    {
        var hex = CustomIconStore.NormalizeColor(raw);
        if (string.IsNullOrEmpty(hex)) return null;
        if (hex.Length == 4)
            return $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}".ToUpperInvariant();
        return hex.ToUpperInvariant();
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0) { h = 0; return; }
        if (max == rd) h = 60 * (((gd - bd) / delta) % 6);
        else if (max == gd) h = 60 * (((bd - rd) / delta) + 2);
        else h = 60 * (((rd - gd) / delta) + 4);
        if (h < 0) h += 360;
    }

    private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;

        double rd, gd, bd;
        if (h < 60) { rd = c; gd = x; bd = 0; }
        else if (h < 120) { rd = x; gd = c; bd = 0; }
        else if (h < 180) { rd = 0; gd = c; bd = x; }
        else if (h < 240) { rd = 0; gd = x; bd = c; }
        else if (h < 300) { rd = x; gd = 0; bd = c; }
        else { rd = c; gd = 0; bd = x; }

        r = (byte)Math.Round(Math.Clamp(rd + m, 0, 1) * 255);
        g = (byte)Math.Round(Math.Clamp(gd + m, 0, 1) * 255);
        b = (byte)Math.Round(Math.Clamp(bd + m, 0, 1) * 255);
    }
}
