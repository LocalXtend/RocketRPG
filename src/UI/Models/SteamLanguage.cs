#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace RocketRPG.Models;

/// <summary>
/// 스팀으로 설치한 게임의 언어 (스팀 API 이름: "koreana", "japanese", "english" …).
/// 스팀판 MV/MZ 게임 일부(예: Ib)는 greenworks로 스팀에 설정된 게임 언어를 물어 처음 언어를 정하는데,
/// WebView2에서는 greenworks(네이티브 모듈)를 쓸 수 없어 늘 기본 언어(일본어 등)로 시작했습니다.
/// </summary>
public static class SteamLanguage
{
    /// <summary>
    /// 게임 폴더가 스팀 라이브러리(steamapps\common\...)에 있고 appmanifest가 있으면 그 게임의 언어를, 아니면 null.
    /// 게임별 언어가 없으면 스팀 클라이언트 언어, 그것도 없으면 Windows 표시 언어로 정합니다.
    /// </summary>
    public static string? ForGame(string gameDir)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(gameDir));
            DirectoryInfo? installDir = null;
            for (var d = dir; d?.Parent != null; d = d.Parent)
            {
                if (d.Parent.Name.Equals("common", StringComparison.OrdinalIgnoreCase) &&
                    d.Parent.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true)
                {
                    installDir = d;
                    break;
                }
            }
            if (installDir == null) return null;

            string steamapps = installDir.Parent!.Parent!.FullName;
            foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(acf);
                var m = Regex.Match(text, "\"installdir\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
                if (!m.Success || !m.Groups[1].Value.Equals(installDir.Name, StringComparison.OrdinalIgnoreCase)) continue;
                return GameLanguage(text) ?? ClientLanguage() ?? FromCulture(CultureInfo.CurrentUICulture);
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"SteamLanguage: {ex.Message}");
        }
        return null;
    }

    /// <summary>appmanifest의 "UserConfig"(없으면 "MountedConfig") 안 "language"</summary>
    public static string? GameLanguage(string acf)
    {
        foreach (var block in new[] { "UserConfig", "MountedConfig" })
        {
            var m = Regex.Match(acf, "\"" + block + "\"\\s*\\{[^}]*?\"language\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    static string? ClientLanguage()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("Language") is string s && s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    /// <summary>Windows 언어 → 스팀 API 언어 이름</summary>
    public static string FromCulture(CultureInfo c)
    {
        string name = c.Name.ToLowerInvariant();
        if (name.StartsWith("zh")) return name.Contains("hant") || name is "zh-tw" or "zh-hk" or "zh-mo" ? "tchinese" : "schinese";
        if (name == "pt-br") return "brazilian";
        if (name == "es-419" || (name.StartsWith("es-") && name != "es-es")) return "latam";
        return c.TwoLetterISOLanguageName switch
        {
            "ko" => "koreana",
            "ja" => "japanese",
            "fr" => "french",
            "de" => "german",
            "es" => "spanish",
            "it" => "italian",
            "pt" => "portuguese",
            "ru" => "russian",
            "pl" => "polish",
            "th" => "thai",
            "tr" => "turkish",
            "uk" => "ukrainian",
            "vi" => "vietnamese",
            "nl" => "dutch",
            "sv" => "swedish",
            "da" => "danish",
            "fi" => "finnish",
            "nb" or "nn" or "no" => "norwegian",
            "cs" => "czech",
            "hu" => "hungarian",
            "ro" => "romanian",
            "bg" => "bulgarian",
            "el" => "greek",
            "ar" => "arabic",
            "id" => "indonesian",
            _ => "english"
        };
    }
}
