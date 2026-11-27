// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MiHomeHub.Models;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

/// <summary>
/// 房间 / 场景图标选择窗：无边框透明窗体里放一张 DialogPopup 卡片。
/// 文件选择器必须由窗口自己开（StorageProvider 只挂在 TopLevel 上），
/// 解析与保存仍交给 <see cref="IconPickerVm"/>，关窗由它发 CloseRequested。
/// </summary>
public partial class IconPickerWindow : Window
{
    private readonly IconPickerVm _vm;

    public IconPickerWindow(CustomIconTarget target)
    {
        InitializeComponent();
        _vm = new IconPickerVm(target);
        DataContext = _vm;
        _vm.CloseRequested = Close;
        Opened += (_, _) => SvgBox.Focus();
    }

    /// <summary>Esc 等同于取消，和右上角关闭、遮罩点击走同一个出口。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _vm.CancelCommand.Execute(null);
    }

    private async void OnPickSvgClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var file = await PickFileAsync("选择 SVG 文件", "SVG 矢量图", "*.svg");
            if (file is null) return;

            var text = await ReadSvgTextAsync(file);
            if (text is null) return;
            _vm.SetSvgFile(text);
        }
        catch (Exception ex)
        {
            _vm.Status = "读取 SVG 失败：" + ex.Message;
        }
    }

    /// <summary>色块在 DataTemplate 里，DataContext 已被换掉；用 Tag 带颜色回 VM，别绕 $parent 命令绑定。</summary>
    private void OnColorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) _vm.SetSvgColorCommand.Execute(hex);
    }

    /// <summary>SVG 素材站链接：用系统默认浏览器打开，失败就写进弹窗状态行。</summary>
    private void OnSvgSiteClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock { Tag: string url }) return;
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _vm.Status = "打开素材站失败：" + ex.Message;
        }
    }

    /// <summary>常用色不够用时，打开完整调色盘挑任意颜色。</summary>
    private async void OnCustomColorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var initial = string.IsNullOrWhiteSpace(_vm.SvgColor) ? "#7E76E0" : _vm.SvgColor;
        var picked = await ColorPickerWindow.PickAsync(this, initial);
        if (!string.IsNullOrWhiteSpace(picked)) _vm.SetSvgColorCommand.Execute(picked);
    }

    private async void OnPickPngClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var file = await PickFileAsync("选择图标图片", "图片", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp");
            if (file is null) return;

            var bytes = await ReadBytesAsync(file);
            if (bytes is null) return;
            _vm.SetPngFile(file.Name, bytes);
        }
        catch (Exception ex)
        {
            _vm.Status = "读取图片失败：" + ex.Message;
        }
    }

    private async Task<IStorageFile?> PickFileAsync(string title, string typeName, params string[] patterns)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return null;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(typeName) { Patterns = patterns }
            }
        });
        return files.Count > 0 ? files[0] : null;
    }

    /// <summary>读 .svg 文本；超过 1 MB 直接拒掉，避免把整页 HTML 导进来。</summary>
    private async Task<string?> ReadSvgTextAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        if (stream.CanSeek && stream.Length > 1_000_000)
        {
            _vm.Status = "SVG 文件太大（超过 1 MB）";
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            _vm.Status = "SVG 文件是空的";
            return null;
        }
        return text;
    }

    /// <summary>读图片字节；PNG 上限 20 MB，先看长度，避免把超大图整个读进内存。</summary>
    private async Task<byte[]?> ReadBytesAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        if (stream.CanSeek && stream.Length > 20 * 1024 * 1024)
        {
            _vm.Status = "图片太大（超过 20 MB），换一张小一点的";
            return null;
        }

        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0) break;
            ms.Write(buffer, 0, read);
            if (ms.Length > 20 * 1024 * 1024)
            {
                _vm.Status = "图片太大（超过 20 MB），换一张小一点的";
                return null;
            }
        }

        if (ms.Length == 0)
        {
            _vm.Status = "图片是空的";
            return null;
        }
        return ms.ToArray();
    }
}
