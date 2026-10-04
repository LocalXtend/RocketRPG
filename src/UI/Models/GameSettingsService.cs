#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RocketRPG.Models;

public class GameConfig
{
    [JsonPropertyName("ratio")]
    public string Ratio { get; set; } = "none";

    [JsonPropertyName("gamma")]
    public double Gamma { get; set; } = 1.0;

    [JsonPropertyName("volume")]
    public int Volume { get; set; } = 100;

    [JsonPropertyName("filter")]
    public string Filter { get; set; } = "none";

    [JsonPropertyName("in_game_font")]
    public string InGameFontFamily { get; set; } = ""; // 비어 있으면 게임 기본 글꼴

    [JsonPropertyName("in_game_font_size")]
    public int InGameFontSize { get; set; } = 0;

    [JsonPropertyName("in_game_font_bold")]
    public bool InGameFontBold { get; set; } = false;

    [JsonPropertyName("auto_message_enabled")]
    public bool AutoMessageEnabled { get; set; } = false;

    [JsonPropertyName("auto_message_speed")]
    public double AutoMessageSpeed { get; set; } = 1.0;

    [JsonPropertyName("show_message_bar")]
    public bool ShowMessageBar { get; set; } = false;

    /// <summary>화면 프레임 상한 (0 = 무제한). 게임 진행 속도는 그대로입니다.</summary>
    [JsonPropertyName("frame_rate")]
    public int FrameRate { get; set; } = 60;

    [JsonPropertyName("vsync")]
    public bool VSync { get; set; } = false;

    /// <summary>변수 HUD 위치(게임 화면 안의 비율 0~1). null이면 기본 위치(왼쪽 위).</summary>
    [JsonPropertyName("hud_x")]
    public double? HudX { get; set; }

    [JsonPropertyName("hud_y")]
    public double? HudY { get; set; }

    [JsonPropertyName("hud_locked")]
    public bool HudLocked { get; set; } = false;

    /// <summary>'이 게임에서 다시 보지 않기'를 누른 알림 (GameIssue.Key)</summary>
    [JsonPropertyName("dismissed_notices")]
    public List<string> DismissedNotices { get; set; } = new();
}

public static class GameSettingsService
{
    public const string ConfigFileName = "rocket_config.json";
    /// <summary>0.6.0까지 따로 쓰던(읽는 곳 없는) 게임별 파일</summary>
    public const string LegacyFileName = "RocketRPGSettings.json";

    public static string ComputeSafeKey(string gameDir)
    {
        using var sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(gameDir.ToLowerInvariant()));
        return Convert.ToHexString(bytes).Substring(0, 16);
    }

    public static string GetFallbackConfigPath(string gameDir)
    {
        string fallbackDir = Path.Combine(SettingsService.Root(), "config", "games");
        try { Directory.CreateDirectory(fallbackDir); } catch { }
        return Path.Combine(fallbackDir, $"{ComputeSafeKey(gameDir)}.json");
    }

    public static string GetPrimaryConfigPath(string gameDir)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RR_PROFILE_ROOT"))) return GetFallbackConfigPath(gameDir);
        return Path.Combine(gameDir, ConfigFileName);
    }

    public static GameConfig Load(string gameDir, GlobalSettings? defaultGlobal = null)
    {
        string primaryPath = GetPrimaryConfigPath(gameDir);
        string fallbackPath = GetFallbackConfigPath(gameDir);

        string? loadedJson = null;

        if (File.Exists(primaryPath))
        {
            try
            {
                loadedJson = File.ReadAllText(primaryPath);
            }
            catch { }
        }

        if (string.IsNullOrEmpty(loadedJson) && File.Exists(fallbackPath))
        {
            try
            {
                loadedJson = File.ReadAllText(fallbackPath);
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(loadedJson))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<GameConfig>(loadedJson);
                if (cfg != null) return cfg;
            }
            catch { }
        }

        // 처음 실행하는 게임은 기본값에서 시작합니다. (예전에는 직전에 한 다른 게임의 밝기·필터·글꼴이 따라왔음)
        return new GameConfig();
    }

    /// <summary>이 게임의 설정 파일을 지우고 기본값으로 다시 씁니다 ('게임 설정 리셋').</summary>
    public static GameConfig Reset(string gameDir)
    {
        foreach (var p in new[] { GetPrimaryConfigPath(gameDir), GetFallbackConfigPath(gameDir), Path.Combine(gameDir, LegacyFileName) })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
        var cfg = new GameConfig();
        Save(gameDir, cfg);
        return cfg;
    }

    public static bool Save(string gameDir, GameConfig config)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return false;

        string primaryPath = GetPrimaryConfigPath(gameDir);
        string fallbackPath = GetFallbackConfigPath(gameDir);
        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        // 게임별 설정 파일은 rocket_config.json 하나만 씁니다. 구버전이 만들던 파일은 지웁니다.
        try
        {
            string legacy = Path.Combine(gameDir, LegacyFileName);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch { }

        // 1. Try primary path in game folder
        try
        {
            File.WriteAllText(primaryPath, json, Encoding.UTF8);
            return true;
        }
        catch
        {
            // 2. Fallback to app config directory if game folder is read-only
            try
            {
                File.WriteAllText(fallbackPath, json, Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
