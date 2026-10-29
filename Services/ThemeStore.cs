using System;
using System.IO;
using System.Text.Json;
using Avalonia.Styling;

namespace MiHome.Services;

/// <summary>主题偏好持久化在 %APPDATA%\Feiyu\MiHome\theme.json，与 auth.json 同目录。</summary>
public static class ThemeStore
{
    private static readonly string Dir = AppPaths.Dir;
    private static readonly string FilePath = Path.Combine(Dir, "theme.json");

    public static bool IsLight { get; private set; }

    static ThemeStore()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                IsLight = doc.RootElement.TryGetProperty("theme", out var t) && t.GetString() == "light";
            }
        }
        catch
        {
            // 偏好文件损坏时静默回落深色
        }
    }

    public static ThemeVariant CurrentVariant => IsLight ? ThemeVariant.Light : ThemeVariant.Dark;

    public static void Set(bool light)
    {
        IsLight = light;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new { theme = light ? "light" : "dark" }));
        }
        catch
        {
            // 写入失败不影响本次运行
        }
    }
}
