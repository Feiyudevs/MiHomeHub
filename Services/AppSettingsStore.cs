using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiHome.Services;

/// <summary>窗口行为偏好持久化在 %APPDATA%\Feiyu\MiHome\app.json，与 theme.json 同目录。</summary>
public static class AppSettingsStore
{
    private static readonly string Dir = AppPaths.Dir;
    private static readonly string FilePath = Path.Combine(Dir, "app.json");

    public static bool AutoStart { get; private set; } = true;

    /// <summary>true = 点关闭收进托盘继续运行；false = 直接退出。</summary>
    public static bool CloseToTray { get; private set; } = true;

    /// <summary>小爱语音指令默认下发的音箱 did；空串表示还没选过。</summary>
    public static string DefaultSpeakerDid { get; private set; } = "";

    static AppSettingsStore()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("autoStart", out var a))
                AutoStart = a.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("closeToTray", out var c))
                CloseToTray = c.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("defaultSpeakerDid", out var s) && s.ValueKind == JsonValueKind.String)
                DefaultSpeakerDid = s.GetString() ?? "";
        }
        catch
        {
            // 偏好文件损坏时静默回落默认值
        }
    }

    public static void SetAutoStart(bool on)
    {
        AutoStart = on;
        Save();
    }

    public static void SetCloseToTray(bool on)
    {
        CloseToTray = on;
        Save();
    }

    public static void SetDefaultSpeakerDid(string did)
    {
        DefaultSpeakerDid = did;
        Save();
    }

    private static void Save()
    {
        var payload = JsonSerializer.Serialize(new
        {
            autoStart = AutoStart,
            closeToTray = CloseToTray,
            defaultSpeakerDid = DefaultSpeakerDid
        });
        // 写盘出 UI 线程；进程被强杀时可能丢最后一次偏好，可接受
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
