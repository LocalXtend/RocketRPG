#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace RocketRPG.Models;

// mkxp-z 실행 준비: Game.ini 보정, mkxp.json 설정 만들기, 실행 파일 찾기 (모두 정적)
public partial class RocketRenderMKXP
{
    /// <summary>
    /// Ensures that Game.ini exists in the game directory and contains a valid [Game] section
    /// with correct Library, Scripts, and Title entries, preventing the fatal
    /// "No script file has been specified. Check the game's INI and try again" error.
    /// </summary>
    public static void EnsureValidGameIni(string gameDir, int engine)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return;

        try
        {
            int rgssVersion = engine switch
            {
                CoreInterop.EngineXp => 1,
                CoreInterop.EngineVx => 2,
                CoreInterop.EngineAce => 3,
                _ => 1
            };

            string defaultDll = rgssVersion switch
            {
                1 => "RGSS104E.dll",
                2 => "RGSS202E.dll",
                3 => "RGSS301.dll",
                _ => "RGSS104E.dll"
            };

            // Detect actual script file on disk
            string scriptRelPath = "";
            if (File.Exists(Path.Combine(gameDir, "Data", "Scripts.rxdata")))
                scriptRelPath = @"Data\Scripts.rxdata";
            else if (File.Exists(Path.Combine(gameDir, "Data", "Scripts.rvdata2")))
                scriptRelPath = @"Data\Scripts.rvdata2";
            else if (File.Exists(Path.Combine(gameDir, "Data", "Scripts.rvdata")))
                scriptRelPath = @"Data\Scripts.rvdata";
            else
            {
                string dataDir = Path.Combine(gameDir, "Data");
                if (Directory.Exists(dataDir))
                {
                    var cands = Directory.GetFiles(dataDir, "Scripts.*");
                    if (cands.Length > 0)
                    {
                        scriptRelPath = @"Data\" + Path.GetFileName(cands[0]);
                    }
                    else
                    {
                        var anyData = Directory.GetFiles(dataDir, "*.rxdata")
                            .Concat(Directory.GetFiles(dataDir, "*.rvdata*")).ToArray();
                        if (anyData.Length > 0)
                            scriptRelPath = @"Data\" + Path.GetFileName(anyData[0]);
                    }
                }
                if (string.IsNullOrEmpty(scriptRelPath))
                {
                    scriptRelPath = rgssVersion switch
                    {
                        1 => @"Data\Scripts.rxdata",
                        2 => @"Data\Scripts.rvdata",
                        3 => @"Data\Scripts.rvdata2",
                        _ => @"Data\Scripts.rxdata"
                    };
                }
            }

            string iniPath = Path.Combine(gameDir, "Game.ini");
            string title = Path.GetFileName(gameDir.TrimEnd('\\', '/'));

            if (!File.Exists(iniPath))
            {
                string content = $"[Game]\r\nLibrary={defaultDll}\r\nScripts={scriptRelPath}\r\nTitle={title}\r\nRTP1=\r\nRTP2=\r\nRTP3=\r\n";
                File.WriteAllText(iniPath, content, new System.Text.UTF8Encoding(false));
                UiLog.Write($"RocketRenderMKXP: Created missing Game.ini -> Scripts={scriptRelPath}, Library={defaultDll}");
                return;
            }

            // Existing Game.ini: verify and fix Scripts and Library safely.
            // 원본 인코딩(ANSI/UTF-8)을 유지해야 원래 Game.exe에서도 제목이 깨지지 않습니다.
            string iniText = RtpResolver.ReadIniText(iniPath, out var iniEncoding);

            var lines = iniText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
            bool hasGameSection = false;
            int gameSectionIndex = -1;
            int scriptsLineIndex = -1;
            int libraryLineIndex = -1;
            int titleLineIndex = -1;
            string existingScript = "";

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Equals("[Game]", StringComparison.OrdinalIgnoreCase))
                {
                    hasGameSection = true;
                    gameSectionIndex = i;
                }
                else if (hasGameSection && trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    break;
                }
                else if (hasGameSection)
                {
                    if (trimmed.StartsWith("Scripts=", StringComparison.OrdinalIgnoreCase))
                    {
                        scriptsLineIndex = i;
                        existingScript = trimmed.Substring(8).Trim().Trim('"');
                    }
                    else if (trimmed.StartsWith("Library=", StringComparison.OrdinalIgnoreCase))
                    {
                        libraryLineIndex = i;
                    }
                    else if (trimmed.StartsWith("Title=", StringComparison.OrdinalIgnoreCase))
                    {
                        titleLineIndex = i;
                    }
                }
            }

            bool modified = false;

            if (!hasGameSection)
            {
                lines.Insert(0, "[Game]");
                gameSectionIndex = 0;
                modified = true;
            }

            bool scriptNeedsFix = false;
            if (scriptsLineIndex == -1 || string.IsNullOrWhiteSpace(existingScript))
            {
                scriptNeedsFix = true;
            }
            else
            {
                string norm = existingScript.Replace('/', '\\');
                if (!File.Exists(Path.Combine(gameDir, norm)) && File.Exists(Path.Combine(gameDir, scriptRelPath)))
                {
                    scriptNeedsFix = true;
                }
            }

            if (scriptNeedsFix)
            {
                string scriptLine = $"Scripts={scriptRelPath}";
                if (scriptsLineIndex != -1)
                {
                    lines[scriptsLineIndex] = scriptLine;
                }
                else
                {
                    lines.Insert(gameSectionIndex + 1, scriptLine);
                }
                modified = true;
                UiLog.Write($"RocketRenderMKXP: Repaired Game.ini -> {scriptLine}");
            }

            if (libraryLineIndex == -1)
            {
                lines.Insert(gameSectionIndex + 1, $"Library={defaultDll}");
                modified = true;
            }

            if (titleLineIndex == -1)
            {
                lines.Insert(gameSectionIndex + 1, $"Title={title}");
                modified = true;
            }

            if (modified)
            {
                File.WriteAllText(iniPath, string.Join("\r\n", lines), iniEncoding is System.Text.UTF8Encoding ? new System.Text.UTF8Encoding(false) : iniEncoding);
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"RocketRenderMKXP: EnsureValidGameIni warning: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates mkxp.json content with the requested engine settings.
    /// </summary>
    public static string GenerateMkxpConfigJson(
        string gameDir,
        int engine,
        bool fixedAspectRatio = true,
        bool smoothScaling = false,
        string? gameFolder = null,
        string? targetFontFamily = null,
        string? soundFontPath = null,
        IEnumerable<string>? rtpDirs = null,
        IEnumerable<string>? preloadScripts = null,
        int? scalingMode = null,
        bool vsync = false)
    {
        int rgssVersion = engine switch
        {
            CoreInterop.EngineXp => 1,
            CoreInterop.EngineVx => 2,
            CoreInterop.EngineAce => 3,
            _ => 1
        };
        int w = (engine == CoreInterop.EngineXp) ? 640 : 544;
        int h = (engine == CoreInterop.EngineXp) ? 480 : 416;

        string iniFileName = "Game.ini";
        try
        {
            var inis = Directory.GetFiles(gameDir, "*.ini");
            if (inis.Length > 0)
            {
                var preferred = inis.FirstOrDefault(i => Path.GetFileName(i).Equals("Game.ini", StringComparison.OrdinalIgnoreCase))
                             ?? inis[0];
                iniFileName = Path.GetFileName(preferred);
            }
        }
        catch { }

        string title = Path.GetFileName(gameDir.TrimEnd('\\', '/'));
        string iniPath = Path.Combine(gameDir, iniFileName);
        if (File.Exists(iniPath))
        {
            try
            {
                // ini는 대개 ANSI(CP932/CP949)이므로 원본 인코딩으로 읽어야 제목이 깨지지 않습니다.
                var t = RtpResolver.IniValue(RtpResolver.ReadIniText(iniPath, out _), "Game", "Title");
                if (!string.IsNullOrWhiteSpace(t)) title = t;
            }
            catch { }
        }

        string targetFolder = string.IsNullOrEmpty(gameFolder) ? gameDir : gameFolder;
        string? sf = soundFontPath ?? RocketFontSystem.ResolveSoundFontPath();

        // RTP: 게임이 요구하는 실제 RTP 폴더(레지스트리/번들) + 한글 글꼴이 든 mkxp-z 런타임 폴더(Fonts/)
        var rtpList = new List<string>();
        void AddRtp(string r)
        {
            if (!Directory.Exists(r)) return;
            string rNorm = Path.GetFullPath(r).Replace('\\', '/');
            if (!rtpList.Contains(rNorm, StringComparer.OrdinalIgnoreCase)) rtpList.Add(rNorm);
        }
        if (rtpDirs != null) foreach (var r in rtpDirs) AddRtp(r);
        string baseDir = AppContext.BaseDirectory;
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? baseDir;
        AddRtp(Path.Combine(exeDir, "runtimes", "mkxp-z"));
        AddRtp(Path.Combine(baseDir, "runtimes", "mkxp-z"));
        // 사용자 PC의 시스템 글꼴 사본(한글 대체용). 설치 파일에 MS 글꼴을 넣지 않기 위해 실행 시 복사합니다.
        var scriptFontNames = MkxpFontCatalog.FontNamesInScripts(gameDir, engine);
        // 게임 글꼴이 PC에 없으면 비슷한 설치 글꼴로 대신 보여 줍니다 (사용자가 인게임 글꼴을 고르지 않았을 때)
        var missingSubs = string.IsNullOrWhiteSpace(targetFontFamily)
            ? MkxpFontCatalog.MissingFontSubstitutes(gameDir, engine, new[] { Path.Combine(gameDir, "Fonts") }.Concat(rtpList.Select(r => Path.Combine(r, "Fonts"))))
            : new Dictionary<string, string>();
        foreach (var (from, to) in missingSubs) UiLog.Write($"RocketRenderMKXP: missing font '{from}' -> '{to}'");
        AddRtp(MkxpFontCatalog.EnsureSystemFontRoot(targetFontFamily, scriptFontNames.Concat(missingSubs.Values)));

        // 게임에 동봉된 mkxp.json이 있으면 그 설정을 기본값으로 존중합니다 (주석/후행 쉼표 허용).
        var obj = new Dictionary<string, object?>();
        string ownJson = Path.Combine(gameDir, "mkxp.json");
        if (File.Exists(ownJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ownJson),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var p in doc.RootElement.EnumerateObject()) obj[p.Name] = p.Value.Clone();
            }
            catch (Exception ex)
            {
                UiLog.Write($"RocketRenderMKXP: game mkxp.json ignored ({ex.Message})");
            }
        }

        if (obj.TryGetValue("RTP", out var ownRtp) && ownRtp is JsonElement ownRtpEl && ownRtpEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in ownRtpEl.EnumerateArray())
            {
                string? rs = r.GetString();
                if (!string.IsNullOrEmpty(rs)) AddRtp(Path.IsPathRooted(rs) ? rs : Path.Combine(gameDir, rs));
            }
        }

        var ownSubs = new List<string>();
        if (obj.TryGetValue("fontSub", out var ownSub) && ownSub is JsonElement ownSubEl && ownSubEl.ValueKind == JsonValueKind.Array)
            foreach (var x in ownSubEl.EnumerateArray()) if (x.GetString() is { Length: > 0 } xs) ownSubs.Add(xs);
        string gameFonts = Path.Combine(gameDir, "Fonts");
        MkxpFontCatalog.SplitCollectionsFrom(gameFonts, scriptFontNames, targetFontFamily);   // 게임 동봉 .ttc도 mkxp-z가 읽을 수 있게 (캐시에)
        var fontSubs = MkxpFontCatalog.BuildSubstitutions(
            new[] { gameFonts }.Concat(rtpList.Select(r => Path.Combine(r, "Fonts"))), gameFonts, targetFontFamily, ownSubs, scriptFontNames, missingSubs);

        var preload = new List<string>();
        if (obj.TryGetValue("preloadScript", out var ownPre) && ownPre is JsonElement ownPreEl && ownPreEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in ownPreEl.EnumerateArray())
            {
                string? ss = s.GetString();
                if (!string.IsNullOrEmpty(ss)) preload.Add((Path.IsPathRooted(ss) ? ss : Path.Combine(gameDir, ss)).Replace('\\', '/'));
            }
        }
        // 게임이 같은 호환 스크립트를 이미 쓰면 다시 넣지 않습니다 (win32_wrap 등은 두 번 불러오면 alias가 자기 자신을 호출해 멈춥니다).
        if (preloadScripts != null)
        {
            foreach (var p in preloadScripts)
            {
                string name = Path.GetFileName(p);
                if (preload.Any(x => Path.GetFileName(x).Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                preload.Add(p.Replace('\\', '/'));
            }
        }

        obj["gameFolder"] = targetFolder.Replace('\\', '/');
        // mkxp-z는 ini(execName.ini)와 암호화 아카이브(execName.rgssad/rgss2a/rgss3a) 이름을 실행 파일 이름에서 가져옵니다.
        // 실행 파일이 mkxp-z.exe라 mkxp-z.rgssad를 찾다가 스크립트를 못 읽고 멈추던 문제를 막기 위해 게임 이름으로 고정합니다.
        obj["execName"] = Path.GetFileNameWithoutExtension(iniFileName);
        obj.Remove("iniFileName");
        obj["RTP"] = rtpList.ToArray();
        obj["useScriptNames"] = true;   // bool (정수 1은 "Invalid variable"로 무시됨)
        obj["defScreenW"] = w;
        obj["defScreenH"] = h;
        obj["windowTitle"] = title;
        obj["fixedAspectRatio"] = fixedAspectRatio;
        // 화면 필터 → mkxp-z 내장 보간 모드(정수). 지정이 없으면 기존처럼 켜기/끄기.
        // mkxp-z는 smoothScaling을 정수(0=최근접…4=xBRZ)로만 받습니다 (bool을 주면 "Invalid variable"로 무시됨).
        obj["smoothScaling"] = scalingMode ?? (smoothScaling ? 1 : 0);
        // 기본은 끔: 켜면 배속(Graphics.frame_rate 상향)이 모니터 주사율에 묶입니다.
        obj["vsync"] = vsync;   // 화면 > 프레임 > 수직 동기화
        obj["syncToRefreshrate"] = false;
        obj["frameSkip"] = true;
        obj["winResizable"] = true;
        obj["rgssVersion"] = rgssVersion;
        obj["fontSub"] = fontSubs;
        obj["enableReset"] = true;
        if (preload.Count > 0) obj["preloadScript"] = preload.ToArray();

        if (!string.IsNullOrEmpty(sf) && File.Exists(sf))
        {
            obj["midiSoundFont"] = sf.Replace('\\', '/');
        }

        return JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// Resolves the mkxp-z executable path matching the project's standard runtime hierarchy.
    /// Checks AppContext.BaseDirectory/runtimes/mkxp-z/, process directory, game directory, and fallbacks.
    /// </summary>
    public static string ResolveExecutable(string? gameDir = null)
    {
        // 1. Game directory
        if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
        {
            string[] gameCands = ["mkxp-z.exe", "RocketRenderMKXP.exe", "mkxp.exe"];
            foreach (var name in gameCands)
            {
                var cand = Path.Combine(gameDir, name);
                if (File.Exists(cand)) return Path.GetFullPath(cand);
            }
        }

        string appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string baseDir = AppContext.BaseDirectory;

        // 2. Portable and runtime standard paths
        var searchDirs = new List<string>
        {
            Path.Combine(baseDir, "runtimes", "mkxp-z"),
            Path.Combine(appDir, "runtimes", "mkxp-z"),
            baseDir,
            appDir,
            Path.Combine(Directory.GetCurrentDirectory(), "dist", "portable", "runtimes", "mkxp-z"),
            Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "mkxp-z"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RocketRPG", "runtimes", "mkxp-z"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RocketRPG", "runtimes", "mkxp-z")
        };

        // Walk up directory tree to support dev/test environments (up to 7 parent levels)
        string? cur = baseDir;
        for (int i = 0; i < 7 && !string.IsNullOrEmpty(cur); i++)
        {
            searchDirs.Add(Path.Combine(cur, "runtimes", "mkxp-z"));
            cur = Path.GetDirectoryName(cur);
        }
        cur = appDir;
        for (int i = 0; i < 7 && !string.IsNullOrEmpty(cur); i++)
        {
            searchDirs.Add(Path.Combine(cur, "runtimes", "mkxp-z"));
            cur = Path.GetDirectoryName(cur);
        }

        string[] exeNames = ["mkxp-z.exe", "RocketRenderMKXP.exe", "mkxp.exe"];
        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var name in exeNames)
            {
                var cand = Path.Combine(dir, name);
                if (File.Exists(cand)) return Path.GetFullPath(cand);
            }
        }

        return "";
    }
}
