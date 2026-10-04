#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace RocketRPG.Models;

/// <summary>
/// RPG Maker RTP(런타임 패키지) 경로 탐색.
/// 원본 RGSS 런타임은 Game.ini의 RTP 이름을 레지스트리(Enterbrain\RGSS*\RTP)에서 찾지만,
/// mkxp-z에는 실제 폴더를 전달하고, EasyRPG의 자체 탐색도 보완합니다.
/// </summary>
public static class RtpResolver
{
    /// <summary>게임 폴더 안의 ini를 원본 인코딩(UTF-8 또는 시스템 ANSI) 그대로 읽습니다.</summary>
    public static string ReadIniText(string path, out Encoding encoding)
    {
        byte[] raw = File.ReadAllBytes(path);
        try
        {
            encoding = new UTF8Encoding(false, true);
            return encoding.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            encoding = AnsiEncoding();
            return encoding.GetString(raw);
        }
    }

    public static Encoding AnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int cp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            return Encoding.GetEncoding(cp);
        }
        catch
        {
            return Encoding.Latin1;
        }
    }

    public static string? IniValue(string iniText, string section, string key)
    {
        bool inSection = false;
        foreach (var rawLine in iniText.Split('\n'))
        {
            string line = rawLine.Trim().TrimStart('﻿');
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim().Trim('"');
        }
        return null;
    }

    static string? LookupRegistry(string rgssKey, string name)
    {
        string[] roots =
        [
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Enterbrain\{rgssKey}\RTP",
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\Enterbrain\{rgssKey}\RTP",
            $@"HKEY_CURRENT_USER\SOFTWARE\Enterbrain\{rgssKey}\RTP"
        ];
        foreach (var r in roots)
        {
            try
            {
                if (Registry.GetValue(r, name, null) is string p && Directory.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 사용자가 설정 &gt; RTP 폴더 지정에서 고른 폴더 ("2000", "2003", "xp", "vx", "vxace" → 폴더). 가장 먼저 씁니다.
    /// MainWindow가 설정을 읽은 뒤와 바꿀 때 넣어 줍니다.
    /// </summary>
    public static Dictionary<string, string> UserPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string EngineKey(int engine) => engine switch
    {
        CoreInterop.Engine2000 => "2000", CoreInterop.Engine2003 => "2003", CoreInterop.EngineXp => "xp",
        CoreInterop.EngineVx => "vx", _ => "vxace",
    };

    static string? UserPath(string key) =>
        UserPaths.TryGetValue(key, out var p) && !string.IsNullOrWhiteSpace(p) && Directory.Exists(p) ? p : null;

    /// <summary>2000/2003 RTP처럼 보이는 폴더 (CharSet·ChipSet·System 등 중 하나라도 있음)</summary>
    public static bool LooksLike2kRtp(string dir) =>
        Directory.Exists(dir) && new[] { "CharSet", "ChipSet", "System", "FaceSet", "Music" }.Any(s => Directory.Exists(Path.Combine(dir, s)));

    /// <summary>XP/VX/Ace RTP처럼 보이는 폴더 (Graphics 또는 Audio가 있음)</summary>
    public static bool LooksLikeRgssRtp(string dir) =>
        Directory.Exists(dir) && (Directory.Exists(Path.Combine(dir, "Graphics")) || Directory.Exists(Path.Combine(dir, "Audio")));

    /// <summary>공식 설치기가 쓰는 기본 폴더 (레지스트리 기록이 지워졌거나 다른 PC에서 복사해 온 경우)</summary>
    static IEnumerable<string> DefaultInstallDirs(string engineKey)
    {
        // KADOKAWA(스팀판) RTP는 사용자 폴더에 설치됩니다
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (engineKey is "2000" or "2003" && appData.Length > 0)
            yield return Path.Combine(appData, "KADOKAWA", "Common", engineKey == "2000" ? "RPG Maker 2000 RTP" : "RPG Maker 2003 RTP");
        var bases = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
            .Where(b => !string.IsNullOrEmpty(b)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var b in bases)
        {
            switch (engineKey)
            {
                case "2000":
                    foreach (var v in new[] { "ASCII", "KADOKAWA", "Enterbrain" }) yield return Path.Combine(b, v, "RPG2000", "RTP");
                    break;
                case "2003":
                    foreach (var v in new[] { "Enterbrain", "KADOKAWA", "ASCII" }) yield return Path.Combine(b, v, "RPG2003", "RTP");
                    break;
                case "xp":
                    yield return Path.Combine(b, "Common Files", "Enterbrain", "RGSS", "Standard");
                    break;
                case "vx":
                    yield return Path.Combine(b, "Common Files", "Enterbrain", "RGSS2", "RPGVX");
                    break;
                case "vxace":
                    yield return Path.Combine(b, "Common Files", "Enterbrain", "RGSS3", "RPGVXAce");
                    break;
            }
        }
    }

    public static List<string> ResolveRgss(string gameDir, int engine)
    {
        var result = new List<string>();
        string ini = Path.Combine(gameDir, "Game.ini");
        string text = "";
        try { if (File.Exists(ini)) text = ReadIniText(ini, out _); } catch { }

        (string key, string[] names) spec = engine switch
        {
            CoreInterop.EngineXp => ("RGSS", new[] { IniValue(text, "Game", "RTP1"), IniValue(text, "Game", "RTP2"), IniValue(text, "Game", "RTP3") }!),
            CoreInterop.EngineVx => ("RGSS2", new[] { IniValue(text, "Game", "RTP") }!),
            _ => ("RGSS3", new[] { IniValue(text, "Game", "RTP") }!)
        };

        // 1) 사용자가 고른 폴더, 2) Game.ini의 RTP 이름으로 레지스트리, 3) 공식 설치 기본 폴더
        if (UserPath(EngineKey(engine)) is { } user) result.Add(user);
        bool wantsRtp = false;
        foreach (var name in spec.names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            wantsRtp = true;
            var p = LookupRegistry(spec.key, name);
            if (p != null) result.Add(p);
        }
        if (result.Count == 0)
        {
            var found = DefaultInstallDirs(EngineKey(engine)).FirstOrDefault(LooksLikeRgssRtp);
            if (found != null) result.Add(found);
            else if (wantsRtp) UiLog.Write($"RtpResolver: RTP '{string.Join(",", spec.names.Where(n => !string.IsNullOrEmpty(n)))}' not found for {gameDir}");
        }
        return result.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>게임이 RTP를 쓰는데(Game.ini RTP 지정) 설치된 것도 지정한 것도 없으면 RTP 이름을 돌려줍니다.</summary>
    public static string? MissingRgssRtp(string gameDir, int engine)
    {
        string text = "";
        try { string ini = Path.Combine(gameDir, "Game.ini"); if (File.Exists(ini)) text = ReadIniText(ini, out _); } catch { }
        string? wanted = engine == CoreInterop.EngineXp
            ? new[] { IniValue(text, "Game", "RTP1"), IniValue(text, "Game", "RTP2"), IniValue(text, "Game", "RTP3") }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
            : IniValue(text, "Game", "RTP");
        if (string.IsNullOrWhiteSpace(wanted)) return null;
        return ResolveRgss(gameDir, engine).Count == 0 ? wanted : null;
    }

    /// <summary>2000/2003 게임이 RTP를 쓰는데(FullPackageFlag가 1이 아님) RTP가 없으면 true.</summary>
    public static bool Missing2kRtp(string gameDir, bool is2003)
    {
        try
        {
            string ini = Path.Combine(gameDir, "RPG_RT.ini");
            if (File.Exists(ini) && IniValue(ReadIniText(ini, out _), "RPG_RT", "FullPackageFlag") == "1") return false;
        }
        catch { }
        return Resolve2k(is2003) == null;
    }

    /// <summary>2000/2003 RTP 폴더 (없으면 null)</summary>
    public static string? Resolve2k(bool is2003) => Resolve2kPaths(is2003).FirstOrDefault();

    /// <summary>2000/2003 RTP 폴더와 찾은 곳 ("user" 사용자 지정, "env" 환경 변수, "registry" 설치 기록, "default" 기본 설치 폴더)</summary>
    public static List<(string Path, string Source)> Resolve2kSources(bool is2003)
    {
        var paths = new List<(string, string)>();
        void Add(string? raw, string source)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var part in raw.Split(';'))
            {
                string p = Environment.ExpandEnvironmentVariables(part.Trim().Trim('"'));
                if (p.Length > 0 && Directory.Exists(p) && !paths.Any(x => string.Equals(x.Item1, p, StringComparison.OrdinalIgnoreCase))) paths.Add((p, source));
            }
        }
        Add(UserPath(is2003 ? "2003" : "2000"), "user");
        Add(Environment.GetEnvironmentVariable(is2003 ? "RPG2K3_RTP_PATH" : "RPG2K_RTP_PATH"), "env");
        string product = is2003 ? "RPG2003" : "RPG2000";
        // 공식 일본어/영어판 및 구판 설치기, 사용자별/전체 설치와 32/64비트 레지스트리를 모두 확인합니다.
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        foreach (var vendor in is2003 ? new[] { "Enterbrain", "KADOKAWA", "ASCII" } : new[] { "ASCII", "KADOKAWA", "Enterbrain" })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($@"SOFTWARE\{vendor}\{product}");
                Add((key?.GetValue("RuntimePackagePath") ?? key?.GetValue("RUNTIMEPACKAGEPATH")) as string, "registry");
            }
            catch { }
        }
        foreach (var d in DefaultInstallDirs(is2003 ? "2003" : "2000")) if (LooksLike2kRtp(d)) Add(d, "default");
        return paths;
    }

    public static List<string> Resolve2kPaths(bool is2003) => Resolve2kSources(is2003).Select(x => x.Path).ToList();

    /// <summary>
    /// EasyRPG Player에 넘길 RTP 폴더: 사용자 지정·기본 설치 폴더만. Player는 설치 기록(레지스트리)을 스스로 읽고 게임에 맞는
    /// RTP(2003 구판/신판 등)를 골라 쓰므로, 같은 경로를 다시 넘겨 그 순서를 흐리지 않습니다.
    /// </summary>
    public static string PlayerRtpPaths(bool is2003) =>
        string.Join(";", Resolve2kSources(is2003).Where(x => x.Source is "user" or "default").Select(x => x.Path));
}
