using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>桌面组件布局持久化：%APPDATA%\Feiyu\MiHome\desktop.json（与 theme.json / app.json 同目录）。</summary>
public static class DesktopGroupStore
{
    private static readonly string Dir = AppPaths.Dir;
    private static readonly string FilePath = Path.Combine(Dir, "desktop.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static DesktopLayout Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new DesktopLayout();
            var text = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(text)) return new DesktopLayout();
            return JsonSerializer.Deserialize<DesktopLayout>(text, Options) ?? new DesktopLayout();
        }
        catch
        {
            // 布局文件损坏时静默回落空布局，不阻塞启动
            return new DesktopLayout();
        }
    }

    public static void Save(DesktopLayout layout)
    {
        try
        {
            var payload = JsonSerializer.Serialize(layout, Options);
            // 写盘出 UI 线程：拖动只改内存，松手才落盘
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllText(FilePath, payload);
                }
                catch
                {
                    // 写失败不影响本次运行
                }
            });
        }
        catch
        {
            // 序列化失败忽略
        }
    }
}
