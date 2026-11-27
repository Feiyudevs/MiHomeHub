// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Svg.Skia;

namespace MiHomeHub.Services;

/// <summary>
/// 房间 / 场景 / 设备的自定义图标：用户可以直接贴 SVG 代码、导入 .svg 文件，或选一张 PNG 图片。
/// 图片文件统一收进 <c>%APPDATA%\Feiyu\MiHomeHub\icons\custom</c>，映射关系写在同目录的 <c>manifest.json</c>，
/// 不引用用户原始路径（原图被移走 / 改名后图标不会突然消失）。
/// </summary>
public static class CustomIconStore
{
    public const string RoomScope = "room";
    public const string SceneScope = "scene";

    /// <summary>设备按 did 记：改名、换房间都不会丢图。</summary>
    public const string DeviceScope = "device";

    /// <summary>图标换了：参数是「作用域 + 名称」，视图模型据此只刷新对应那一枚图标。</summary>
    public static event EventHandler<CustomIconChangedEventArgs>? Changed;

    private static readonly string Dir = Path.Combine(AppPaths.Dir, "icons", "custom");
    private static readonly string ManifestPath = Path.Combine(Dir, "manifest.json");
    private static readonly object Gate = new();

    /// <summary>SVG 文本上限：正常图标几 KB，1 MB 已经非常宽松，挡住误贴整页 HTML 之类。</summary>
    private const int MaxSvgChars = 1_000_000;

    /// <summary>PNG 文件上限：图标用途，20 MB 足够，避免一张超大图把进程内存顶起来。</summary>
    private const int MaxPngBytes = 20 * 1024 * 1024;

    private sealed class Record
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("svg")] public string? Svg { get; set; }
        [JsonPropertyName("file")] public string? File { get; set; }

        /// <summary>SVG 统一换色用的 #RRGGBB；空 = 保留原图颜色。</summary>
        [JsonPropertyName("color")] public string? Color { get; set; }
    }

    private static readonly Dictionary<string, Record> Records = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CustomIcon> Cache = new(StringComparer.Ordinal);
    private static bool _loaded;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static CustomIconStore()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            foreach (var item in doc.RootElement.EnumerateObject())
            {
                var rec = item.Value.Deserialize<Record>(Json);
                if (rec is null) continue;
                if (rec.Kind == "svg" && string.IsNullOrWhiteSpace(rec.Svg)) continue;
                if (rec.Kind == "png" && string.IsNullOrWhiteSpace(rec.File)) continue;
                Records[item.Name] = rec;
            }
        }
        catch
        {
            // 清单损坏就当没有自定义图标，不让设置文件把应用拖住
        }
        _loaded = true;
    }

    private static string KeyOf(string scope, string key) => scope + ":" + key;

    /// <summary>取某个房间 / 场景当前生效的自定义图标；没有或者文件读不出来返回 null（回落默认图标）。</summary>
    public static CustomIcon? Get(string scope, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        EnsureLoaded();

        var id = KeyOf(scope, key);
        if (Cache.TryGetValue(id, out var cached)) return cached;

        Record? rec;
        lock (Gate)
        {
            if (!Records.TryGetValue(id, out rec)) return null;
        }

        var icon = Build(scope, key, rec);
        if (icon is null) return null;
        Cache[id] = icon;
        return icon;
    }

    public static bool Has(string scope, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        lock (Gate)
        {
            return Records.ContainsKey(KeyOf(scope, key));
        }
    }

    /// <summary>校验并落盘一段 SVG 代码；<paramref name="colorHex"/> 为空表示保留原图颜色，否则统一换成这一色。</summary>
    public static bool SetSvg(string scope, string key, string svg, string? colorHex, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            error = "先选中一个房间或场景";
            return false;
        }

        var text = (svg ?? "").Trim();
        if (text.Length == 0)
        {
            error = "SVG 代码是空的";
            return false;
        }
        if (text.Length > MaxSvgChars)
        {
            error = "SVG 代码太长（超过 1 MB）";
            return false;
        }

        var color = NormalizeColor(colorHex);
        if (color is null)
        {
            error = "颜色格式不对，用 #RGB 或 #RRGGBB";
            return false;
        }

        var image = RenderSvg(text, color, out var why);
        if (image is null)
        {
            error = why ?? "SVG 解析失败，检查代码是否完整";
            return false;
        }

        var id = KeyOf(scope, key);
        lock (Gate)
        {
            EnsureLoaded();
            // 这一枚原先存的是 PNG：换回 SVG 后把旧图删掉，别在 icons 目录里留垃圾
            if (Records.TryGetValue(id, out var old) && old.Kind == "png" && old.File is { Length: > 0 })
                TryDelete(Path.Combine(Dir, old.File));

            Records[id] = new Record
            {
                Kind = "svg",
                Svg = text,
                Color = color.Length == 0 ? null : color,
            };
            Save();
        }

        Cache[id] = new CustomIcon
        {
            Scope = scope,
            Key = key,
            Kind = CustomIconKind.Svg,
            SvgText = text,
            ColorHex = color.Length == 0 ? null : color,
            Image = image,
        };
        Changed?.Invoke(null, new CustomIconChangedEventArgs(scope, key));
        return true;
    }

    /// <summary>
    /// 校验并保存一张位图（PNG 为主，顺带支持 jpg / webp 这类 Skia 解得了的格式）。
    /// 文件会复制一份进 icons 目录，用户之后删掉原图也不影响。
    /// </summary>
    public static bool SetPng(string scope, string key, string sourceFileName, byte[] bytes, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            error = "先选中一个房间或场景";
            return false;
        }
        if (bytes is null || bytes.Length == 0)
        {
            error = "图片是空的";
            return false;
        }
        if (bytes.Length > MaxPngBytes)
        {
            error = "图片太大（超过 20 MB），换一张小一点的";
            return false;
        }

        var image = RenderBitmap(bytes, out var why);
        if (image is null)
        {
            error = why ?? "图片解析失败，换一张 PNG 试试";
            return false;
        }

        var id = KeyOf(scope, key);
        var ext = NormalizeExtension(sourceFileName);
        var file = $"{SafeName(scope)}-{SafeName(key)}-{ShortHash(id)}{ext}";
        var fullPath = Path.Combine(Dir, file);

        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(fullPath, bytes);
        }
        catch (Exception ex)
        {
            error = "图片保存失败：" + ex.Message;
            return false;
        }

        lock (Gate)
        {
            EnsureLoaded();
            if (Records.TryGetValue(id, out var old))
            {
                if (old.Kind == "png" && old.File is { Length: > 0 } oldFile &&
                    !string.Equals(oldFile, file, StringComparison.OrdinalIgnoreCase))
                    TryDelete(Path.Combine(Dir, oldFile));
            }

            Records[id] = new Record
            {
                Kind = "png",
                File = file,
            };
            Save();
        }

        Cache[id] = new CustomIcon
        {
            Scope = scope,
            Key = key,
            Kind = CustomIconKind.Png,
            FilePath = fullPath,
            Image = image,
        };
        Changed?.Invoke(null, new CustomIconChangedEventArgs(scope, key));
        return true;
    }

    /// <summary>恢复默认图标（删掉记录和对应的图片文件）。</summary>
    public static bool Clear(string scope, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        var id = KeyOf(scope, key);
        var removed = false;
        lock (Gate)
        {
            EnsureLoaded();
            if (Records.TryGetValue(id, out var old))
            {
                if (old.Kind == "png" && old.File is { Length: > 0 })
                    TryDelete(Path.Combine(Dir, old.File));
                Records.Remove(id);
                Save();
                removed = true;
            }
        }

        Cache.TryRemove(id, out _);
        if (removed) Changed?.Invoke(null, new CustomIconChangedEventArgs(scope, key));
        return removed;
    }

    /// <summary>弹窗预览用：解析一段 SVG 代码（可带统一颜色），失败时返回原因。</summary>
    public static IImage? PreviewSvg(string svg, string? colorHex, out string? error)
    {
        var color = NormalizeColor(colorHex);
        if (color is null)
        {
            error = "颜色格式不对，用 #RGB 或 #RRGGBB";
            return null;
        }
        return RenderSvg((svg ?? "").Trim(), color, out error);
    }

    /// <summary>弹窗预览用：解析一张图片的字节，失败时返回原因。</summary>
    public static IImage? PreviewBitmap(byte[] bytes, out string? error) => RenderBitmap(bytes, out error);

    private static CustomIcon? Build(string scope, string key, Record rec)
    {
        try
        {
            if (rec.Kind == "svg")
            {
                var svgImage = RenderSvg(rec.Svg ?? "", rec.Color, out _);
                if (svgImage is null) return null;
                return new CustomIcon
                {
                    Scope = scope,
                    Key = key,
                    Kind = CustomIconKind.Svg,
                    SvgText = rec.Svg,
                    ColorHex = rec.Color,
                    Image = svgImage,
                };
            }

            var path = Path.Combine(Dir, rec.File ?? "");
            if (!File.Exists(path)) return null;
            var image = RenderBitmap(File.ReadAllBytes(path), out _);
            if (image is null) return null;
            return new CustomIcon
            {
                Scope = scope,
                Key = key,
                Kind = CustomIconKind.Png,
                FilePath = path,
                Image = image,
            };
        }
        catch
        {
            return null;
        }
    }

    private static IImage? RenderSvg(string text, string? color, out string? error)
    {
        error = null;
        if (text.Length == 0 || text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) < 0)
        {
            error = "没找到 <svg> 标签，确认贴的是 SVG 代码";
            return null;
        }

        try
        {
            // 选了颜色就先在源码层统一换色：不同 SVG 的写法差别太大，靠渲染后再染色不现实
            var renderText = string.IsNullOrEmpty(color) ? text : TintSvg(text, color);
            var source = SvgSource.LoadFromSvg(renderText);
            if (source?.Picture is null)
            {
                error = "SVG 里没有可绘制内容";
                return null;
            }
            return new SvgImage { Source = source };
        }
        catch (Exception ex)
        {
            error = "SVG 解析失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>把 #RGB / #RRGGBB（允许不带 #）规整成统一写法；空串 = 保留原色，格式不对返回 null。</summary>
    public static string? NormalizeColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Trim();
        if (text[0] != '#') text = "#" + text;
        return Regex.IsMatch(text, @"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$") ? text : null;
    }

    private static readonly Regex ColorAttrRegex = new(
        @"(?<![-\w])(?<name>stroke|fill)\s*=\s*(?<quote>[""'])(?<value>[^""']*)\k<quote>",
        RegexOptions.IgnoreCase);

    private static readonly Regex StyleColorRegex = new(
        @"(?<![-\w])(?<name>fill|stroke)\s*:\s*(?<value>[^;""'}]+)",
        RegexOptions.IgnoreCase);

    private static readonly Regex StopColorRegex = new(
        @"\bstop-color\s*=\s*(?<quote>[""'])(?<value>[^""']*)\k<quote>",
        RegexOptions.IgnoreCase);

    private static readonly Regex ShapeTagRegex = new(
        @"<(?<tag>path|rect|circle|ellipse|polygon|polyline|line|use|text)\b(?<attrs>[^>]*)>",
        RegexOptions.IgnoreCase);

    private static readonly Regex StyleAttrRegex = new(
        @"style\s*=\s*(?<quote>[""'])(?<value>[^""']*)\k<quote>",
        RegexOptions.IgnoreCase);

    private static bool IsUncolorable(string value)
    {
        if (value.Length == 0) return true;
        return value.Equals("none", StringComparison.OrdinalIgnoreCase)
            || value.Equals("transparent", StringComparison.OrdinalIgnoreCase)
            || value.Equals("inherit", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("url(", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("context-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在 SVG 源码里统一换成指定颜色：
    /// 1) 显式 stroke / fill（含 style 内写法、渐变 stop）里非 none / 非 url 的颜色全部替换；
    /// 2) 整段 SVG 一个颜色都没写、只能吃默认黑填充的图标，给没有 fill 的形状补一条 inline fill；
    /// 3) 纯描边图标（只有 stroke）不补 fill，避免被填成一坨实心色块。
    /// </summary>
    private static string TintSvg(string svg, string color)
    {
        var hasStroke = false;
        var hasFill = false;

        var text = ColorAttrRegex.Replace(svg, m =>
        {
            var name = m.Groups["name"].Value;
            var value = m.Groups["value"].Value.Trim();
            if (IsUncolorable(value)) return m.Value;
            if (name.Equals("stroke", StringComparison.OrdinalIgnoreCase)) hasStroke = true;
            else hasFill = true;
            return $"{name}=\"{color}\"";
        });

        text = StyleColorRegex.Replace(text, m =>
        {
            var name = m.Groups["name"].Value;
            var value = m.Groups["value"].Value.Trim();
            if (IsUncolorable(value)) return m.Value;
            if (name.Equals("stroke", StringComparison.OrdinalIgnoreCase)) hasStroke = true;
            else hasFill = true;
            return $"{name}:{color}";
        });

        // 渐变图标：把每个 stop 也压成同一个色，选色后就是一个纯色图标，不会一半旧色一半新色
        text = StopColorRegex.Replace(text, m =>
        {
            var value = m.Groups["value"].Value.Trim();
            if (value.Length == 0) return m.Value;
            hasFill = true;
            return $"stop-color=\"{color}\"";
        });

        // 原图完全没写颜色：给形状补 inline fill；写了 fill（哪怕是 none）就尊重原样
        if (!hasFill && !hasStroke)
            text = AddFillToUncoloredShapes(text, color);

        return text;
    }

    private static string AddFillToUncoloredShapes(string svg, string color)
    {
        return ShapeTagRegex.Replace(svg, m =>
        {
            var attrs = m.Groups["attrs"].Value;
            if (Regex.IsMatch(attrs, @"\bfill\s*=", RegexOptions.IgnoreCase)) return m.Value;
            if (Regex.IsMatch(attrs, @"\bfill\s*:", RegexOptions.IgnoreCase)) return m.Value;

            var tag = m.Value;
            var closing = tag.EndsWith("/>", StringComparison.Ordinal) ? "/>" : ">";
            var body = tag[..^closing.Length];

            if (StyleAttrRegex.IsMatch(body))
            {
                body = StyleAttrRegex.Replace(body, sm =>
                {
                    var existing = sm.Groups["value"].Value.TrimEnd(';', ' ');
                    return $"style=\"{existing};fill:{color}\"";
                }, 1);
            }
            else
            {
                body += $" style=\"fill:{color}\"";
            }

            return body + closing;
        });
    }

    private static IImage? RenderBitmap(byte[] bytes, out string? error)
    {
        error = null;
        if (bytes is null || bytes.Length == 0)
        {
            error = "图片是空的";
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch (Exception ex)
        {
            error = "图片解析失败：" + ex.Message;
            return null;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(Records, Json), Encoding.UTF8);
        }
        catch
        {
            // 写盘失败不影响本次会话，下次修改时再试
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 文件被别的进程占着就留着，不影响图标记录本身
        }
    }

    private static string SafeName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
            else if (char.IsWhiteSpace(c)) sb.Append('-');
        }
        var text = sb.ToString().Trim('-', '_');
        return text.Length == 0 ? "item" : text[..Math.Min(36, text.Length)];
    }

    private static string ShortHash(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private static string NormalizeExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => ".jpg",
            ".webp" => ".webp",
            ".bmp" => ".bmp",
            _ => ".png",
        };
    }
}

public enum CustomIconKind
{
    Svg,
    Png,
}

/// <summary>已解码好的一枚自定义图标：给界面直接绑 <see cref="Image"/>，给弹窗回显原始来源。</summary>
public sealed class CustomIcon
{
    public required string Scope { get; init; }
    public required string Key { get; init; }
    public required CustomIconKind Kind { get; init; }
    public string? SvgText { get; init; }

    /// <summary>SVG 的统一颜色；null / 空 = 保留原图颜色。</summary>
    public string? ColorHex { get; init; }
    public string? FilePath { get; init; }
    public required IImage Image { get; init; }

    public bool IsSvg => Kind == CustomIconKind.Svg;
}

public sealed class CustomIconChangedEventArgs : EventArgs
{
    public CustomIconChangedEventArgs(string scope, string key)
    {
        Scope = scope;
        Key = key;
    }

    public string Scope { get; }
    public string Key { get; }
}
