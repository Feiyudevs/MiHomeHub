// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MiHome.Services.Setup;

/// <summary>安装前后需要成组回滚的「登记现场」快照。</summary>
internal sealed class SetupSnapshot
{
    public bool ArpExisted { get; set; }
    public List<SetupRegistryValue> ArpValues { get; } = new();
    public string? RunCommand { get; set; }
    public byte[]? StartMenuLnk { get; set; }
    public byte[]? DesktopLnk { get; set; }
}

internal sealed record SetupApplyInfo(
    string Root,
    string Version,
    string Publisher,
    bool DesktopShortcut);

/// <summary>
/// 集成层：卸载注册（ARP）、开始菜单/桌面快捷方式、开机自启的清理。
/// 隔离自测时全部换成 <c>--self-test-root</c> 下的文件替身，绝不碰真实注册表。
/// </summary>
internal sealed class SetupIntegration
{
    private const string PublisherName = "Feiyu";

    private readonly string? _isolatedRoot;

    private SetupIntegration(string? isolatedRoot) => _isolatedRoot = isolatedRoot;

    public static SetupIntegration Create(string? isolatedRoot)
        => new(isolatedRoot is null ? null : SetupFiles.Normalize(isolatedRoot));

    public bool IsIsolated => _isolatedRoot is not null;

    public string StartMenuLnkPath => IsIsolated
        ? Path.Combine(_isolatedRoot!, "menu", SetupProtocol.ProductName + ".lnk")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            SetupProtocol.ProductName + ".lnk");

    public string DesktopLnkPath => IsIsolated
        ? Path.Combine(_isolatedRoot!, "desktop", SetupProtocol.ProductName + ".lnk")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            SetupProtocol.ProductName + ".lnk");

    private string ArpJsonPath => Path.Combine(_isolatedRoot!, "uninstall.json");

    // ────────────────────────────── 快照 ──────────────────────────────

    public SetupSnapshot Capture()
    {
        var snapshot = new SetupSnapshot();
        if (IsIsolated)
        {
            snapshot.RunCommand = null;
            snapshot.StartMenuLnk = ReadBytesOrNull(StartMenuLnkPath);
            snapshot.DesktopLnk = ReadBytesOrNull(DesktopLnkPath);
            return snapshot;
        }

        try
        {
            snapshot.ArpExisted = SetupRegistry.KeyExists(SetupProtocol.UninstallKeyPath);
            if (snapshot.ArpExisted)
                snapshot.ArpValues.AddRange(SetupRegistry.ReadValues(SetupProtocol.UninstallKeyPath));
        }
        catch (Exception ex)
        {
            throw new SetupFailureException("备份登记", "读取卸载注册表失败", ex);
        }

        snapshot.RunCommand = ReadRunValue();
        snapshot.StartMenuLnk = ReadBytesOrNull(StartMenuLnkPath);
        snapshot.DesktopLnk = ReadBytesOrNull(DesktopLnkPath);
        return snapshot;
    }

    public void Restore(SetupSnapshot snapshot)
    {
        if (IsIsolated)
        {
            RestoreFile(StartMenuLnkPath, snapshot.StartMenuLnk);
            RestoreFile(DesktopLnkPath, snapshot.DesktopLnk);
            return;
        }

        try
        {
            SetupRegistry.DeleteKey(SetupProtocol.UninstallKeyPath);
            if (snapshot.ArpExisted)
            {
                foreach (var value in snapshot.ArpValues)
                    SetupRegistry.WriteRaw(SetupProtocol.UninstallKeyPath, value.Name, value.Type, value.Data);
            }
        }
        catch (Exception ex)
        {
            throw new SetupFailureException("恢复登记", "恢复卸载注册表失败", ex);
        }

        WriteRunValue(snapshot.RunCommand);
        RestoreFile(StartMenuLnkPath, snapshot.StartMenuLnk);
        RestoreFile(DesktopLnkPath, snapshot.DesktopLnk);
    }

    // ────────────────────────────── 应用 / 移除 ──────────────────────────────

    public void Apply(SetupApplyInfo info)
    {
        if (IsIsolated)
        {
            ApplyIsolated(info);
            return;
        }

        WriteArp(info);
        ApplyShortcuts(info);
    }

    public void Remove()
    {
        if (IsIsolated)
        {
            TryDelete(ArpJsonPath);
            TryDelete(StartMenuLnkPath);
            TryDelete(DesktopLnkPath);
            return;
        }

        try { SetupRegistry.DeleteKey(SetupProtocol.UninstallKeyPath); }
        catch (Exception ex) { SetupLog.Fail("remove-arp", ex); }

        TryDelete(StartMenuLnkPath);
        TryDelete(DesktopLnkPath);

        // 清掉 Run 值，避免卸载后系统还在拉起不存在的路径；应用内的 autoStart 偏好随数据目录保留
        try { SetupRegistry.DeleteValue(SetupProtocol.RunKeyPath, SetupProtocol.RunValueName); }
        catch (Exception ex) { SetupLog.Fail("remove-startup", ex); }
    }

    private void ApplyIsolated(SetupApplyInfo info)
    {
        Directory.CreateDirectory(_isolatedRoot!);
        var estimated = (int)Math.Min(int.MaxValue, SetupFiles.DirectorySize(SetupPaths.AppDir(info.Root)) / 1024);
        var json = new StringBuilder();
        json.AppendLine("{");
        AppendJson(json, "DisplayName", SetupProtocol.ProductName, comma: true);
        AppendJson(json, "DisplayVersion", info.Version, comma: true);
        AppendJson(json, "Publisher", info.Publisher, comma: true);
        AppendJson(json, "DisplayIcon", SetupPaths.AppExe(info.Root), comma: true);
        AppendJson(json, "InstallLocation", SetupFiles.Normalize(info.Root), comma: true);
        json.AppendLine($"  \"EstimatedSize\": {estimated},");
        AppendJson(json, "UninstallString", "\"" + SetupPaths.AppExe(info.Root) + "\" --uninstall", comma: true);
        json.AppendLine("  \"NoModify\": 1,");
        json.AppendLine("  \"NoRepair\": 1");
        json.AppendLine("}");
        File.WriteAllText(ArpJsonPath, json.ToString(), new UTF8Encoding(false));

        Directory.CreateDirectory(Path.GetDirectoryName(StartMenuLnkPath)!);
        File.WriteAllText(StartMenuLnkPath, SetupPaths.AppExe(info.Root), new UTF8Encoding(false));

        var desktopDir = Path.GetDirectoryName(DesktopLnkPath)!;
        if (info.DesktopShortcut)
        {
            Directory.CreateDirectory(desktopDir);
            File.WriteAllText(DesktopLnkPath, SetupPaths.AppExe(info.Root), new UTF8Encoding(false));
        }
        else
        {
            TryDelete(DesktopLnkPath);
        }
    }

    private static void AppendJson(StringBuilder sb, string name, string value, bool comma)
    {
        sb.Append("  \"").Append(name).Append("\": \"")
          .Append(value.Replace("\\", "\\\\").Replace("\"", "\\\""))
          .Append('"');
        sb.AppendLine(comma ? "," : string.Empty);
    }

    private void WriteArp(SetupApplyInfo info)
    {
        try
        {
            // 整键删除重建，保证字段集合没有历史残留
            SetupRegistry.DeleteKey(SetupProtocol.UninstallKeyPath);
            SetupRegistry.SetString(SetupProtocol.UninstallKeyPath, "DisplayName", SetupProtocol.ProductName);
            SetupRegistry.SetString(SetupProtocol.UninstallKeyPath, "DisplayVersion", info.Version);
            SetupRegistry.SetString(SetupProtocol.UninstallKeyPath, "Publisher", info.Publisher);
            SetupRegistry.SetString(SetupProtocol.UninstallKeyPath, "DisplayIcon", SetupPaths.AppExe(info.Root));
            SetupRegistry.SetString(SetupProtocol.UninstallKeyPath, "InstallLocation", SetupFiles.Normalize(info.Root));
            SetupRegistry.SetDword(SetupProtocol.UninstallKeyPath, "EstimatedSize",
                (int)Math.Min(int.MaxValue, SetupFiles.DirectorySize(SetupPaths.AppDir(info.Root)) / 1024));
            SetupRegistry.SetString(SetupProtocol.UninstallKeyPath, "UninstallString",
                "\"" + SetupPaths.AppExe(info.Root) + "\" --uninstall");
            SetupRegistry.SetDword(SetupProtocol.UninstallKeyPath, "NoModify", 1);
            SetupRegistry.SetDword(SetupProtocol.UninstallKeyPath, "NoRepair", 1);
        }
        catch (Exception ex)
        {
            throw new SetupFailureException("登记安装", "写入卸载注册表失败", ex,
                fixHint: "请确认当前用户有注册表写权限");
        }
    }

    private void ApplyShortcuts(SetupApplyInfo info)
    {
        var exe = SetupPaths.AppExe(info.Root);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartMenuLnkPath)!);
            if (!SetupShortcut.Create(exe, StartMenuLnkPath))
                throw new SetupFailureException("创建快捷方式", $"开始菜单快捷方式创建失败：{StartMenuLnkPath}");
        }
        catch (SetupFailureException) { throw; }
        catch (Exception ex)
        {
            throw new SetupFailureException("创建快捷方式", "开始菜单快捷方式创建失败", ex);
        }

        try
        {
            if (info.DesktopShortcut)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DesktopLnkPath)!);
                if (!SetupShortcut.Create(exe, DesktopLnkPath))
                    throw new IOException($"桌面快捷方式创建失败：{DesktopLnkPath}");
            }
            else
            {
                TryDelete(DesktopLnkPath);
            }
        }
        catch (Exception ex)
        {
            SetupLog.Fail("desktop-shortcut", ex, path: DesktopLnkPath);
        }
    }

    // ────────────────────────────── 注册表读写 ──────────────────────────────

    private static string? ReadRunValue()
    {
        try { return SetupRegistry.ReadString(SetupProtocol.RunKeyPath, SetupProtocol.RunValueName); }
        catch { return null; }
    }

    private static void WriteRunValue(string? command)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command))
                SetupRegistry.DeleteValue(SetupProtocol.RunKeyPath, SetupProtocol.RunValueName);
            else
                SetupRegistry.SetString(SetupProtocol.RunKeyPath, SetupProtocol.RunValueName, command);
        }
        catch { }
    }

    // ────────────────────────────── 文件工具 ──────────────────────────────

    private static byte[]? ReadBytesOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    private static void RestoreFile(string path, byte[]? bytes)
    {
        try
        {
            if (bytes is null)
            {
                TryDelete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex)
        {
            throw new SetupFailureException("恢复文件", $"恢复失败：{path}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
