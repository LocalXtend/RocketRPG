#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RocketRPG.Models;

public class GlobalSettings
{
    public List<string> History { get; set; } = new();
    public double Gamma { get; set; } = 1.0;
    public int Volume { get; set; } = 100;
    public string Ratio { get; set; } = "none";
    public string Filter { get; set; } = "none";
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    // 멀티: 내 이름(겹쳐도 됨), 이 PC를 구분하는 id(내보내기용, 다른 사람에게는 보이지 않음), 받는 핑/채팅 숨기기
    [System.Text.Json.Serialization.JsonPropertyName("multi_name")] public string MultiName { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("multi_client_id")] public string MultiClientId { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("multi_hide_pings")] public bool MultiHidePings { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("multi_hide_chat")] public bool MultiHideChat { get; set; }

    // 업데이트 받을 버전: "stable" = 정식만, "beta" = 베타도, "" = 정하지 않음 (지금 버전이 베타면 베타도)
    [System.Text.Json.Serialization.JsonPropertyName("update_channel")] public string UpdateChannel { get; set; } = "";

    // 메모 설정 (설정 > 메모 설정)
    [System.Text.Json.Serialization.JsonPropertyName("memo_font_family")] public string MemoFontFamily { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("memo_font_size")] public double MemoFontSize { get; set; } = 13;
    [System.Text.Json.Serialization.JsonPropertyName("memo_color")] public string MemoColor { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("notes_background")] public string NotesBackground { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("label_color")] public string LabelColor { get; set; } = "";

    /// <summary>스트리머 모드: 노트 말고 도구·단축키를 끄고, 메시지 바의 퀵 세이브/로드를 숨깁니다.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("streamer_mode")]
    public bool StreamerMode { get; set; }

    /// <summary>단축키 기본값 변경을 옮겨 적용한 단계 (1: 스크린샷 F12 → Ctrl+Shift+A)</summary>
    [System.Text.Json.Serialization.JsonPropertyName("hotkey_schema")]
    public int HotkeySchema { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("ui_font_family")]
    public string UiFontFamily { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("in_game_font")]
    public string InGameFontFamily { get; set; } = ""; // 비어 있으면 게임 기본 글꼴

    [System.Text.Json.Serialization.JsonPropertyName("in_game_font_size")]
    public int InGameFontSize { get; set; } = 0;

    [System.Text.Json.Serialization.JsonPropertyName("in_game_font_bold")]
    public bool InGameFontBold { get; set; } = false;

    // 게임 글꼴이 PC에 없을 때 대신 쓸 글꼴 (비어 있으면 이름으로 짐작한 비슷한 글꼴, XP/VX/Ace)
    [System.Text.Json.Serialization.JsonPropertyName("missing_font_fallback")]
    public string MissingFontFallback { get; set; } = "";

    // 처음 실행 때 사용법을 볼지 한 번 물었는지
    [System.Text.Json.Serialization.JsonPropertyName("tutorial_offered")]
    public bool TutorialOffered { get; set; }

    // 사용자가 고른 RTP 폴더 ("2000", "2003", "xp", "vx", "vxace" → 폴더, 설정 > RTP 폴더 지정)
    [System.Text.Json.Serialization.JsonPropertyName("rtp_paths")]
    public Dictionary<string, string> RtpPaths { get; set; } = new();

    // 디스코드 프로필에 플레이 중인 게임 표시 (Rich Presence)
    [System.Text.Json.Serialization.JsonPropertyName("discord_presence")]
    public bool DiscordPresence { get; set; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("auto_message_enabled")]
    public bool AutoMessageEnabled { get; set; } = false;

    [System.Text.Json.Serialization.JsonPropertyName("auto_message_speed")]
    public double AutoMessageSpeed { get; set; } = 1.0;

    // 대화 시 Ren'Py 퀵 메뉴 자동 표시. (구 키 show_message_bar는 '현재 표시 상태'가 저장되던 값이라 이관하지 않음)
    [System.Text.Json.Serialization.JsonPropertyName("renpy_quick_menu")]
    public bool ShowMessageBar { get; set; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("notes_width")]
    public double NotesWidth { get; set; } = 360;

    // 노트를 따로 창으로 띄울지와 그 창의 위치·크기 (0 = 아직 없음)
    [System.Text.Json.Serialization.JsonPropertyName("notes_detached")]
    public bool NotesDetached { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("notes_window")]
    public double[] NotesWindowBounds { get; set; } = [0, 0, 0, 0];

    [System.Text.Json.Serialization.JsonPropertyName("custom_scan_dirs")]
    public List<string> CustomScanFolders { get; set; } = new();

    public bool IsDefault()
    {
        return (History == null || History.Count == 0) &&
               Math.Abs(Gamma - 1.0) < 0.001 &&
               Volume == 100 &&
               (string.IsNullOrEmpty(Ratio) || Ratio.Equals("none", StringComparison.OrdinalIgnoreCase)) &&
               (string.IsNullOrEmpty(Filter) || Filter.Equals("none", StringComparison.OrdinalIgnoreCase)) &&
               (Hotkeys == null || Hotkeys.Count == 0) &&
               string.IsNullOrEmpty(UiFontFamily) &&
               (CustomScanFolders == null || CustomScanFolders.Count == 0);
    }
}

public class SettingsService
{
    public static string Root()
    {
        if (Environment.GetEnvironmentVariable("RR_PROFILE_ROOT") is { Length: > 0 } testRoot) return Path.GetFullPath(testRoot);
        // BaseDirectory can be the temp extraction dir under single-file publish; anchor on the real exe
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(exeDir, "config"))) return exeDir;   // dist\portable layout
        var portable = Path.GetFullPath(Path.Combine(exeDir, "..", ".."));
        if (Directory.Exists(Path.Combine(portable, "config"))) return portable;
        var dev = Path.GetFullPath(Path.Combine(exeDir, "..", "..", ".."));
        return Directory.Exists(Path.Combine(dev, "GameSample")) ? dev : exeDir;
    }
    static string CfgDir => Path.Combine(Root(), "config");
    static string CfgFile => Path.Combine(CfgDir, "global.json");

    static string AppDataCfgDir => Environment.GetEnvironmentVariable("RR_PROFILE_ROOT") is { Length: > 0 }
        ? Path.Combine(Root(), "config") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RocketRPG");
    static string AppDataCfgFile => Path.Combine(AppDataCfgDir, "global.json");

    static JsonSerializerOptions Opts => new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public GlobalSettings Load()
    {
        GlobalSettings? fromCfg = null;
        GlobalSettings? fromAppData = null;
        DateTime cfgMtime = DateTime.MinValue;
        DateTime appDataMtime = DateTime.MinValue;

        try
        {
            if (File.Exists(CfgFile))
            {
                fromCfg = JsonSerializer.Deserialize<GlobalSettings>(File.ReadAllText(CfgFile));
                cfgMtime = File.GetLastWriteTimeUtc(CfgFile);
            }
        }
        catch { }

        try
        {
            if (File.Exists(AppDataCfgFile))
            {
                fromAppData = JsonSerializer.Deserialize<GlobalSettings>(File.ReadAllText(AppDataCfgFile));
                appDataMtime = File.GetLastWriteTimeUtc(AppDataCfgFile);
            }
        }
        catch { }

        GlobalSettings result;

        if (fromCfg == null && fromAppData == null)
        {
            result = new GlobalSettings();
        }
        else if (fromCfg == null)
        {
            result = fromAppData!;
        }
        else if (fromAppData == null)
        {
            result = fromCfg;
        }
        else
        {
            bool cfgDefault = fromCfg.IsDefault();
            bool appDataDefault = fromAppData.IsDefault();

            if (cfgDefault && !appDataDefault)
            {
                result = fromAppData;
            }
            else if (!cfgDefault && appDataDefault)
            {
                result = fromCfg;
            }
            else if (!cfgDefault && !appDataDefault)
            {
                result = cfgMtime >= appDataMtime ? fromCfg : fromAppData;

                var mergedHistory = new List<string>();
                void AddHist(List<string>? list)
                {
                    if (list == null) return;
                    foreach (var h in list)
                    {
                        if (!string.IsNullOrWhiteSpace(h) && !mergedHistory.Contains(h))
                            mergedHistory.Add(h);
                    }
                }
                if (cfgMtime >= appDataMtime)
                {
                    AddHist(fromCfg.History);
                    AddHist(fromAppData.History);
                }
                else
                {
                    AddHist(fromAppData.History);
                    AddHist(fromCfg.History);
                }
                result.History = mergedHistory;
            }
            else
            {
                result = fromCfg;
            }
        }

        CompactHistory(result);
        Save(result);
        return result;
    }

    public void Save(GlobalSettings s)
    {
        SaveTo(CfgDir, CfgFile, s);
        SaveTo(AppDataCfgDir, AppDataCfgFile, s);
    }

    static void SaveTo(string dir, string file, GlobalSettings s)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, JsonSerializer.Serialize(s, Opts));
        }
        catch { }
    }

    public const int MaxHistory = 10;

    /// <summary>경로 표기 차이(대소문자, 슬래시, 끝 구분자, 상대 경로)를 없앤 비교용 경로.</summary>
    public static string NormalizeGameDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        string p = dir.Trim().Trim('"');
        try { p = Path.GetFullPath(p); } catch { }
        p = p.Replace('/', '\\');
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }

    public static bool SameGameDir(string a, string b) =>
        string.Equals(NormalizeGameDir(a), NormalizeGameDir(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>중복 제거 + 최대 개수 유지. 변경되었으면 true.</summary>
    public static bool CompactHistory(GlobalSettings s)
    {
        var result = new List<string>();
        foreach (var h in s.History ?? new List<string>())
        {
            string n = NormalizeGameDir(h);
            if (n.Length == 0 || result.Exists(r => string.Equals(r, n, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(n);
            if (result.Count >= MaxHistory) break;
        }
        bool changed = s.History == null || result.Count != s.History.Count;
        if (!changed)
            for (int i = 0; i < result.Count; i++)
                if (!string.Equals(result[i], s.History![i], StringComparison.Ordinal)) { changed = true; break; }
        s.History = result;
        return changed;
    }

    public void PushHistory(GlobalSettings s, string gameDir)
    {
        string n = NormalizeGameDir(gameDir);
        if (n.Length == 0) return;
        s.History.RemoveAll(h => SameGameDir(h, n));
        s.History.Insert(0, n);
        CompactHistory(s);
        Save(s);
    }

    public void ClearHistory(GlobalSettings s)
    {
        s.History.Clear();
        Save(s);
    }
}
