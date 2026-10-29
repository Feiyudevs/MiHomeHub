using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiHome.Services;

/// <summary>侧栏图标样式偏好持久化在 %APPDATA%\Feiyu\MiHome\iconstyle.json，与 theme.json 同目录。</summary>
public static class IconStyleStore
{
    private static readonly string Dir = AppPaths.Dir;
    private static readonly string FilePath = Path.Combine(Dir, "iconstyle.json");

    /// <summary>true = 厚涂玻璃 SVG；false = 细线描边。</summary>
    public static bool Glass { get; private set; } = true;

    static IconStyleStore()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                Glass = !doc.RootElement.TryGetProperty("style", out var s) || s.GetString() != "line";
            }
        }
        catch
        {
            // 偏好文件损坏时静默回落玻璃样式
        }
    }

    public static void Set(bool glass)
    {
        Glass = glass;
        var payload = JsonSerializer.Serialize(new { style = glass ? "glass" : "line" });
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, payload);
            }
            catch
            {
                // 写入失败不影响本次运行
            }
        });
    }
}
