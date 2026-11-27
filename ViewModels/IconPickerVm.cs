// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiHomeHub.Models;
using MiHomeHub.Services;

namespace MiHomeHub.ViewModels;

/// <summary>
/// 图标选择弹窗的状态：左边能贴 SVG 代码 / 导入 .svg 文件，右边能选 PNG 等位图。
/// 预览与校验都在这里做，窗口只负责开文件选择器和关窗。
/// </summary>
public partial class IconPickerVm : ObservableObject
{
    private readonly CustomIconTarget _target;
    private readonly DispatcherTimer _svgDebounce;

    private byte[]? _pngBytes;
    private string _pngName = "";

    public IconPickerVm(CustomIconTarget target)
    {
        _target = target;
        Title = target.Label;
        Description = target.IsRoom
            ? "房间图标会同时用在侧栏、房间页和首页分组标题上，保存后立即生效"
            : target.IsDevice
                ? "设备图标会同时用在设备卡、房间列表和桌面组件上，保存后立即生效"
                : "场景图标会同时用在智能页卡片、首页快捷场景和桌面分区上，保存后立即生效";

        var existing = CustomIconStore.Get(target.Scope, target.Key);
        HasExisting = existing is not null;
        if (existing is { IsSvg: true, SvgText: not null })
        {
            _svgText = existing.SvgText;
            _svgColor = existing.ColorHex ?? "";
            _preview = existing.Image;
            _modeIndex = 0;
        }
        else if (existing is { Kind: CustomIconKind.Png, FilePath: not null })
        {
            _pngName = Path.GetFileName(existing.FilePath);
            _pngFileName = _pngName;
            try { _pngBytes = File.ReadAllBytes(existing.FilePath); }
            catch { _pngBytes = null; }
            _preview = existing.Image;
            _modeIndex = 1;
        }

        _svgDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _svgDebounce.Tick += (_, _) =>
        {
            _svgDebounce.Stop();
            ValidateSvgPreview();
        };

        UpdateColorSelection();
        UpdateCanApply();
    }

    /// <summary>弹窗标题，直接用「房间图标 · 客厅」这种完整说法。</summary>
    public string Title { get; }

    public string Description { get; }

    /// <summary>这个房间 / 场景现在有没有自定义图标：有才显示「恢复默认」。</summary>
    public bool HasExisting { get; }

    [ObservableProperty] private int _modeIndex;

    public bool IsSvgMode => ModeIndex == 0;

    public bool IsPngMode => ModeIndex == 1;

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSvgMode));
        OnPropertyChanged(nameof(IsPngMode));
        UpdateCanApply();
    }

    [ObservableProperty] private string _svgText = "";

    partial void OnSvgTextChanged(string value)
    {
        // 连续输入 / 粘贴时别每个字符都跑一遍 SVG 解析，静默 260ms 再出预览
        _svgDebounce.Stop();
        _svgDebounce.Start();
        UpdateCanApply();
    }

    /// <summary>SVG 统一颜色（#RRGGBB）；空 = 保留原图颜色。</summary>
    [ObservableProperty] private string _svgColor = "";

    public bool IsOriginalColor => string.IsNullOrWhiteSpace(SvgColor);

    /// <summary>
    /// 色板末尾的「自定义颜色」圆点：选了非常用色就显示这个颜色，
    /// 没选 / 选了常用色则显示一圈彩色渐变，提示这里能打开调色盘。
    /// </summary>
    public IBrush CustomColorBrush
    {
        get
        {
            var hex = ExpandHex(SvgColor);
            if (hex is not null)
            {
                try { return new SolidColorBrush(Color.Parse(hex)); }
                catch { /* 颜色坏了就用渐变兜底 */ }
            }
            return CustomColorGradient;
        }
    }

    /// <summary>当前颜色不是预设里的任何一枚时，自定义圆点才带选中环。</summary>
    public bool IsCustomColorSelected => ExpandHex(SvgColor) is not null
        && !ColorOptions.Any(o => string.Equals(o.Hex, ExpandHex(SvgColor), StringComparison.OrdinalIgnoreCase));

    private static readonly IBrush CustomColorGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#F87171"), 0),
            new GradientStop(Color.Parse("#FCD34D"), 0.3),
            new GradientStop(Color.Parse("#4ADE80"), 0.55),
            new GradientStop(Color.Parse("#7FABFF"), 0.8),
            new GradientStop(Color.Parse("#ED43BD"), 1),
        }
    };

    private static string? ExpandHex(string? raw)
    {
        var hex = CustomIconStore.NormalizeColor(raw);
        if (string.IsNullOrEmpty(hex)) return null;
        return hex.Length == 4
            ? $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}".ToUpperInvariant()
            : hex.ToUpperInvariant();
    }

    public ObservableCollection<IconColorOption> ColorOptions { get; } = new()
    {
        new("#FFFFFF", "白色"),
        new("#D8DEEA", "浅灰"),
        new("#8A93A6", "灰色"),
        new("#7E76E0", "靛紫"),
        new("#7FABFF", "蓝色"),
        new("#4ADE80", "绿色"),
        new("#FCD34D", "琥珀"),
        new("#F87171", "红色"),
        new("#111827", "近黑"),
    };

    partial void OnSvgColorChanged(string value)
    {
        OnPropertyChanged(nameof(IsOriginalColor));
        OnPropertyChanged(nameof(CustomColorBrush));
        OnPropertyChanged(nameof(IsCustomColorSelected));
        UpdateColorSelection();
        _svgDebounce.Stop();
        _svgDebounce.Start();
    }

    [RelayCommand]
    private void SetSvgColor(string? hex) => SvgColor = (hex ?? "").Trim();

    [RelayCommand]
    private void ResetSvgColor() => SvgColor = "";

    private void UpdateColorSelection()
    {
        var current = string.IsNullOrWhiteSpace(SvgColor) ? "" : "#" + SvgColor.Trim().TrimStart('#');
        foreach (var option in ColorOptions)
            option.IsSelected = !string.IsNullOrEmpty(current) &&
                                string.Equals(current, option.Hex, StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty] private string _pngFileName = "";

    public bool HasPngFile => !string.IsNullOrEmpty(PngFileName);

    partial void OnPngFileNameChanged(string value) => OnPropertyChanged(nameof(HasPngFile));

    [ObservableProperty] private IImage? _preview;

    public bool HasPreview => Preview is not null;

    partial void OnPreviewChanged(IImage? value) => OnPropertyChanged(nameof(HasPreview));

    [ObservableProperty] private string _status = "";

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    [ObservableProperty] private bool _canApply;

    public Action? CloseRequested { get; set; }

    /// <summary>窗口选完 PNG 后回填：解析成功才切到 PNG 分段并更新预览。</summary>
    public void SetPngFile(string fileName, byte[] bytes)
    {
        var image = CustomIconStore.PreviewBitmap(bytes, out var error);
        if (image is null)
        {
            Status = error ?? "图片读不出来，换一张 PNG 试试";
            return;
        }

        _pngBytes = bytes;
        _pngName = fileName;
        PngFileName = fileName;
        Preview = image;
        Status = "";
        ModeIndex = 1;
        UpdateCanApply();
    }

    /// <summary>窗口选完 .svg 文件后回填：直接验证并预览，不等防抖。</summary>
    public void SetSvgFile(string text)
    {
        _svgDebounce.Stop();
        ModeIndex = 0;
        SvgText = text.Trim();
        _svgDebounce.Stop();
        ValidateSvgPreview();
    }

    [RelayCommand]
    private void ShowSvg() => ModeIndex = 0;

    [RelayCommand]
    private void ShowPng() => ModeIndex = 1;

    [RelayCommand]
    private void Apply()
    {
        if (IsSvgMode)
        {
            if (string.IsNullOrWhiteSpace(SvgText))
            {
                Status = "先粘贴 SVG 代码，或从 .svg 文件导入";
                return;
            }

            if (!CustomIconStore.SetSvg(_target.Scope, _target.Key, SvgText, SvgColor, out var error))
            {
                Status = error ?? "SVG 保存失败";
                return;
            }
        }
        else
        {
            if (_pngBytes is null)
            {
                Status = "先选一张 PNG 图片";
                return;
            }

            if (!CustomIconStore.SetPng(_target.Scope, _target.Key, _pngName, _pngBytes, out var error))
            {
                Status = error ?? "图片保存失败";
                return;
            }
        }

        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Clear()
    {
        if (!CustomIconStore.Clear(_target.Scope, _target.Key))
        {
            Status = "这个对象本来就没有自定义图标";
            return;
        }
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    private void ValidateSvgPreview()
    {
        if (string.IsNullOrWhiteSpace(SvgText))
        {
            Preview = null;
            Status = "";
            UpdateCanApply();
            return;
        }

        var color = CustomIconStore.NormalizeColor(SvgColor);
        if (color is null)
        {
            Preview = null;
            Status = "颜色格式不对，用 #RGB 或 #RRGGBB";
            UpdateCanApply();
            return;
        }

        var image = CustomIconStore.PreviewSvg(SvgText, SvgColor, out var error);
        if (image is null)
        {
            Preview = null;
            Status = error ?? "SVG 解析失败";
        }
        else
        {
            Preview = image;
            Status = "";
        }
        UpdateCanApply();
    }

    private void UpdateCanApply()
    {
        CanApply = IsSvgMode
            ? !string.IsNullOrWhiteSpace(SvgText) && CustomIconStore.NormalizeColor(SvgColor) is not null
            : _pngBytes is not null;
    }
}

/// <summary>弹窗里的一枚预设色块。</summary>
public partial class IconColorOption : ObservableObject
{
    public IconColorOption(string hex, string label)
    {
        Hex = hex;
        Label = label;
        Brush = new SolidColorBrush(Color.Parse(hex));
    }

    public string Hex { get; }

    public string Label { get; }

    public IBrush Brush { get; }

    [ObservableProperty] private bool _isSelected;
}
