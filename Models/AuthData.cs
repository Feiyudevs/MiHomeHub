// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace MiHome.Models;

/// <summary>登录凭证，持久化到 %APPDATA%/Feiyu/MiHome/auth.json（DPAPI 加密，仅当前 Windows 用户可解密）</summary>
public class AuthData
{
    [JsonPropertyName("ssecurity")] public string SSecurity { get; set; } = "";
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    [JsonPropertyName("cUserId")] public string CUserId { get; set; } = "";
    [JsonPropertyName("serviceToken")] public string ServiceToken { get; set; } = "";
    [JsonPropertyName("passToken")] public string PassToken { get; set; } = "";
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("ua")] public string UA { get; set; } = "";
    [JsonPropertyName("pass_o")] public string PassO { get; set; } = "";
    [JsonPropertyName("expireTime")] public long ExpireTime { get; set; }
}
