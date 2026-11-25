// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MiHomeHub.Services;
using MiHomeHub.Services.Setup;
using MiHomeHub.Services.Telemetry;

namespace MiHomeHub.Views;

/// <summary>
/// 安装/更新/卸载界面：单窗口五态（准备 → 进度 → 完成 / 失败 / 卸载确认）。
/// 复用 MiHomeHub 的主题画刷、BtnPrimary/BtnGhost 与 ToggleButton.Switch，不另造一套视觉。
/// </summary>
public partial class SetupWindow : Window
{
    private enum Phase
    {
        Ready,
        Progress,
        Done,
        Error,
        UninstallReady,
    }

    private readonly SetupOptions _options;
    private readonly SetupService _service;
    private Phase _phase = Phase.Ready;
    private bool _running;
    private bool _uninstalling;
    private double _progress;
    private SetupPreview _preview;
    private SetupResult? _result;

    /// <summary>设计器/兜底用途；正式入口走带参数的构造函数。</summary>
    public SetupWindow() : this(new SetupOptions())
    {
    }

    public SetupWindow(SetupOptions options)
    {
        _options = options;
        _service = new SetupService(options);
        _preview = _service.Preview();

        InitializeComponent();

        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Title = SetupProtocol.ProductName;

        Closing += OnClosing;
        ConfigureForMode();
        Opened += OnOpened;
        WindowIconHelper.Apply(this);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        PositionPreview();
        SetupEntry.SignalShown();
    }

    /// <summary>预览时把安装窗和卸载窗左右排开，方便一次看两种界面。</summary>
    private void PositionPreview()
    {
        if (!_options.Preview) return;
        var screen = Screens.Primary;
        if (screen is null) return;

        var area = screen.WorkingArea;
        var w = (int)(Bounds.Width > 0 ? Bounds.Width : Width);
        var h = (int)(Bounds.Height > 0 ? Bounds.Height : Height);
        var y = area.Y + (area.Height - h) / 2;

        int x;
        if (area.Width >= w * 2 + 48)
            x = _options.Mode == SetupMode.Install
                ? area.X + (area.Width - w * 2) / 4
                : area.X + area.Width - w - (area.Width - w * 2) / 4;
        else
            x = _options.Mode == SetupMode.Install ? area.X + 16 : area.X + area.Width - w - 16;

        Position = new PixelPoint(x, y);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_running) e.Cancel = true;
    }

    // ────────────────────────────── 准备态文案 ──────────────────────────────

    private void ConfigureForMode()
    {
        if (_options.Mode == SetupMode.Uninstall)
        {
            TxtTitle.Text = "卸载 MiHomeHub";
            TxtSubtitle.Text = _preview.InstalledVersion is { } installedVersion
                ? $"当前版本 {installedVersion}"
                : "移除本机安装";

            _phase = Phase.UninstallReady;
            ShowPanel(Phase.UninstallReady);
            TxtUninstallLead.Text =
                "程序文件会被完整删除；桌面布局、登录态与设置默认保留，方便以后重新安装后继续使用。";
            SetHint("确认后会先让正在运行的 MiHomeHub 退出");
            BtnPrimary.Content = "卸载";
            return;
        }

        var package = _preview.PackageVersion;
        var installed = _preview.InstalledVersion;
        _phase = Phase.Ready;
        ShowPanel(Phase.Ready);

        TxtTitle.Text = "MiHomeHub";
        TxtSubtitle.Text = package is null ? "版本 1.0.0" : $"版本 {package}";
        TxtInstallPath.Text = _preview.Root;
        BtnSecondary.Content = "取消";

        if (installed is null)
        {
            SetHint("安装不会改动你已有的桌面布局与设置");
            BtnPrimary.Content = "安装";
        }
        else if (package is not null && SetupFiles.CompareVersions(package, installed) > 0)
        {
            SetHint("设置和数据都会保留；会先让正在运行的 MiHomeHub 退出");
            BtnPrimary.Content = $"更新到 {package}";
        }
        else
        {
            SetHint("设置和数据都会保留；会先让正在运行的 MiHomeHub 退出");
            BtnPrimary.Content = "重新安装";
        }

        SwitchDesktop.IsChecked = true;
        // 匿名统计：首次安装默认勾选；本机已有 telemetry.json（更新/重装）说明用户已做过选择，
        // 安装器不会改它，这一行就不显示，避免出现「勾了却不生效」
        SwitchTelemetry.IsChecked = true;
        TelemetryRow.IsVisible = installed is null && !TelemetryService.HasSavedChoice;
        BtnChangePath.IsVisible = installed is null;
    }

    private void SetHint(string? text)
    {
        TxtHint.Text = text ?? string.Empty;
        TxtHint.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>已安装时锁定原目录；首次安装允许选一个空文件夹。</summary>
    private async void OnChangePathClick(object? sender, RoutedEventArgs e)
    {
        if (_options.Mode != SetupMode.Install || _preview.InstalledVersion is not null) return;

        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null) return;

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择 MiHomeHub 安装位置",
                AllowMultiple = false,
            });
            if (folders.Count == 0) return;

            var path = folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                SetHint("只支持选择本机文件夹");
                return;
            }

            var normalized = SetupFiles.Normalize(path);
            try
            {
                SetupPaths.ValidateTargetRoot(normalized);
            }
            catch (Exception) when (!IsDriveRoot(normalized))
            {
                // 用户往往直接选 D:\Apps 这种已有目录：自动落到其下的 MiHomeHub 子目录。
                var child = SetupFiles.Normalize(Path.Combine(normalized, "MiHomeHub"));
                SetupPaths.ValidateTargetRoot(child);
                normalized = child;
            }

            _service.TargetRootOverride = normalized;
            TxtInstallPath.Text = normalized;
            SetHint("安装不会改动你已有的桌面布局与设置");
        }
        catch (Exception ex)
        {
            SetHint("该目录不可用：" + ex.Message);
            SetupLog.Fail("choose-target-root", ex);
        }
    }

    private static bool IsDriveRoot(string path)
    {
        var volumeRoot = Path.GetPathRoot(path) ?? string.Empty;
        return string.Equals(path, volumeRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, volumeRoot, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────── 安装 ──────────────────────────────

    private async Task StartInstallAsync()
    {
        if (_running) return;
        _running = true;
        _uninstalling = false;
        _phase = Phase.Progress;
        ShowPanel(Phase.Progress);
        TxtProgressStep.Text = "正在准备安装…";
        TxtProgressHint.Text = "安装过程中请勿关闭窗口";
        SetHint("正在安装，请勿关闭此窗口");
        SetProgress(0);
        BtnPrimary.Content = "正在安装…";
        BtnPrimary.IsEnabled = false;

        var progress = new Progress<SetupProgress>(p =>
        {
            TxtProgressStep.Text = p.Step;
            SetProgress(p.Percent);
        });

        _result = await _service.InstallAsync(
            SwitchDesktop.IsChecked == true,
            TelemetryRow.IsVisible && SwitchTelemetry.IsChecked == true,
            progress);
        _running = false;
        BtnClose.IsEnabled = true;

        if (_result.Success) ShowInstallDone(_result);
        else ShowError(_result, "安装未完成");
    }

    private void ShowInstallDone(SetupResult result)
    {
        _phase = Phase.Done;
        ShowPanel(Phase.Done);
        if (_options.Preview)
        {
            TxtSubtitle.Text = "预览 · 未实际安装";
            TxtDoneTitle.Text = "安装界面预览完成";
            TxtDoneDetail.Text = "这是界面预览，系统未发生任何改动；没有创建文件、快捷方式或卸载登记。";
            SetHint(null);
            LaunchRow.IsVisible = false;
            BtnPrimary.Content = "关闭";
            BtnPrimary.IsEnabled = true;
            return;
        }

        var updated = result.PreviousVersion is not null && result.Version is not null
                      && result.PreviousVersion != result.Version;
        TxtSubtitle.Text = result.Version is null ? "已安装到当前用户目录" : $"已安装 · 版本 {result.Version}";
        TxtDoneTitle.Text = updated ? "MiHomeHub 已更新到最新版本" : "MiHomeHub 已安装完成";
        TxtDoneDetail.Text = $"安装位置：{result.Root}";
        SetHint(null);
        LaunchRow.IsVisible = true;
        SwitchLaunch.IsChecked = true;
        BtnPrimary.Content = "完成";
        BtnPrimary.IsEnabled = true;
    }

    // ────────────────────────────── 卸载 ──────────────────────────────

    private async Task StartUninstallAsync()
    {
        if (_running) return;
        _running = true;
        _uninstalling = true;
        _phase = Phase.Progress;
        ShowPanel(Phase.Progress);
        TxtProgressStep.Text = "正在准备卸载…";
        TxtProgressHint.Text = "卸载过程中请勿关闭窗口";
        SetHint("正在卸载，请勿关闭此窗口");
        SetProgress(0);
        BtnPrimary.Content = "正在卸载…";
        BtnPrimary.IsEnabled = false;

        var progress = new Progress<SetupProgress>(p =>
        {
            TxtProgressStep.Text = p.Step;
            SetProgress(p.Percent);
        });

        _result = await _service.UninstallAsync(SwitchRemoveData.IsChecked == true, progress);
        _running = false;
        BtnClose.IsEnabled = true;

        if (_result.Success)
        {
            _phase = Phase.Done;
            ShowPanel(Phase.Done);
            if (_options.Preview)
            {
                TxtSubtitle.Text = "预览 · 未实际卸载";
                TxtDoneTitle.Text = "卸载界面预览完成";
                TxtDoneDetail.Text = "这是界面预览，系统未发生任何改动；程序、数据和快捷方式都保持原样。";
                SetHint(null);
                LaunchRow.IsVisible = false;
                BtnPrimary.Content = "关闭";
                BtnPrimary.IsEnabled = true;
                return;
            }

            TxtSubtitle.Text = _preview.InstalledVersion is { } version
                ? $"版本 {version} 已移除"
                : "已从本机移除";
            TxtDoneTitle.Text = "MiHomeHub 已卸载";
            TxtDoneDetail.Text = SwitchRemoveData.IsChecked == true
                ? "程序文件与布局、登录、设置均已删除。"
                : "程序文件已删除，布局与设置已保留。";
            SetHint(null);
            LaunchRow.IsVisible = false;
            BtnPrimary.Content = "关闭";
            BtnPrimary.IsEnabled = true;
        }
        else
        {
            ShowError(_result, "卸载未完成");
        }
    }

    // ────────────────────────────── 失败 ──────────────────────────────

    private void ShowError(SetupResult result, string title)
    {
        _phase = Phase.Error;
        ShowPanel(Phase.Error);
        TxtErrorTitle.Text = title;
        SetHint(null);
        TxtErrorMessage.Text = result.Step is { } step
            ? $"{step}：{result.Error}"
            : result.Error ?? "未知错误";
        TxtErrorHint.Text = result.Hint ?? "请重试；若反复失败，请把下方日志发给开发者。";
        TxtErrorLog.Text = result.LogPath;
        BtnPrimary.Content = "重试";
        BtnPrimary.IsEnabled = true;
        BtnSecondary.IsEnabled = true;
        BtnSecondary.Content = "查看日志";
    }

    // ────────────────────────────── 交互 ──────────────────────────────

    private void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        switch (_phase)
        {
            case Phase.Ready:
                _ = StartInstallAsync();
                break;
            case Phase.UninstallReady:
                _ = StartUninstallAsync();
                break;
            case Phase.Done:
                if (!_uninstalling) LaunchInstalled();
                Close();
                break;
            case Phase.Error:
                if (_uninstalling) _ = StartUninstallAsync();
                else _ = StartInstallAsync();
                break;
        }
    }

    private void OnSecondaryClick(object? sender, RoutedEventArgs e)
    {
        if (_phase == Phase.Error)
        {
            OpenLog();
            return;
        }
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (!_running) Close();
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        // 标题栏空白处拖动窗口；关闭按钮自己处理点击
        if (e.Source is Visual source && source.GetVisualAncestors().OfType<Button>().Any()) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void LaunchInstalled()
    {
        if (_options.Preview || _uninstalling || _result?.Root is not { } root) return;
        if (_result.Success && SwitchLaunch.IsChecked == false) return;
        try { SetupProcess.Launch(SetupPaths.AppExe(root)); }
        catch (Exception ex) { SetupLog.Fail("launch-app", ex, path: root); }
    }

    private void OpenLog()
    {
        try
        {
            var path = SetupLog.FilePath;
            if (!File.Exists(path)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    // ────────────────────────────── 视图切换 ──────────────────────────────

    private void ShowPanel(Phase phase)
    {
        ReadyPanel.IsVisible = phase == Phase.Ready;
        ProgressPanel.IsVisible = phase == Phase.Progress;
        DonePanel.IsVisible = phase == Phase.Done;
        ErrorPanel.IsVisible = phase == Phase.Error;
        UninstallPanel.IsVisible = phase == Phase.UninstallReady;
        BtnPrimary.IsVisible = true;
        // 准备/完成/进度态只保留主按钮加右上角关闭；只有失败态才并排“查看日志”
        BtnSecondary.IsVisible = phase == Phase.Error;
        BtnClose.IsEnabled = !_running;
    }

    private void SetProgress(double value)
    {
        _progress = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
        var width = ProgressTrack.Bounds.Width * _progress;
        ProgressFill.Width = width;
        TxtPercent.Text = $"{(int)Math.Round(_progress * 100)}%";
    }

    private void OnProgressTrackSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SetProgress(_progress);
    }
}
