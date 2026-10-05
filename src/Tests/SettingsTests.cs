#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// 설정·단축키·노트 표기·스팀 언어
public partial class Program
{
    private static void TestHotkeyManager()
    {
        Console.WriteLine("--- Testing HotkeyManager ---");

        Assert("Default hotkeys count is 24", HotkeyManager.DefaultHotkeys.Count == 24, $"Count={HotkeyManager.DefaultHotkeys.Count}");
        Assert("Active hotkeys count is 24", HotkeyManager.ActiveHotkeys.Count == 24, $"Count={HotkeyManager.ActiveHotkeys.Count}");
        Assert("Menu toggle defaults to Alt+H", HotkeyManager.GestureOf("ToggleMenuBar") == "Alt + H");
        Assert("Menu text drops spaces", HotkeyManager.MenuText("Ctrl + Shift + S") == "Ctrl+Shift+S" && HotkeyManager.MenuText("") == "");
        Assert("Alt+Tab is refused", HotkeyManager.SystemConflict("Alt + Tab") != null && HotkeyManager.SystemConflict("Alt + F4") != null);
        Assert("Alt+H is not a system combination", HotkeyManager.SystemConflict("Alt + H") == null && !HotkeyManager.MenuAccessConflict("Alt + H"));
        Assert("Alt+F conflicts with the File menu", HotkeyManager.MenuAccessConflict("Alt + F"));
        Assert("Menu toggle works in streamer mode", HotkeyManager.IsAlwaysAvailable("ToggleMenuBar"));

        var expectedIds = new[]
        {
            "QuickSave", "QuickLoad", "ForceSaveMenu", "ForceLoadMenu", "RestartGame", "ExitGame",
            "ToggleNoclip", "SpeedUp", "SpeedDown", "SpeedReset", "TogglePause", "ToggleDataInspector",
            "ToggleMapViewer", "ToggleEspOverlay", "ToggleTileInspector",
            "ToggleAutoMessage", "ToggleSkipMessage", "ToggleMessageBar"
        };

        foreach (var id in expectedIds)
        {
            bool found = HotkeyManager.ActiveHotkeys.Any(h => h.Id == id);
            Assert($"Action '{id}' is registered in ActiveHotkeys", found);
        }

        // Test MatchWebKey
        Assert("WebKey F5 -> QuickSave", HotkeyManager.MatchWebKey("F5", false, false, false) == "QuickSave");
        Assert("WebKey F8 -> QuickLoad", HotkeyManager.MatchWebKey("F8", false, false, false) == "QuickLoad");
        Assert("WebKey Ctrl+S -> ForceSaveMenu", HotkeyManager.MatchWebKey("s", true, false, false) == "ForceSaveMenu");
        Assert("WebKey Ctrl+L -> ForceLoadMenu", HotkeyManager.MatchWebKey("l", true, false, false) == "ForceLoadMenu");
        Assert("WebKey Ctrl+R -> RestartGame", HotkeyManager.MatchWebKey("r", true, false, false) == "RestartGame");
        Assert("WebKey Ctrl+Q -> ExitGame", HotkeyManager.MatchWebKey("q", true, false, false) == "ExitGame");
        Assert("WebKey Ctrl+P -> ToggleNoclip", HotkeyManager.MatchWebKey("p", true, false, false) == "ToggleNoclip");
        Assert("WebKey Ctrl+ArrowUp -> SpeedUp", HotkeyManager.MatchWebKey("ArrowUp", true, false, false) == "SpeedUp");
        Assert("WebKey Ctrl+ArrowDown -> SpeedDown", HotkeyManager.MatchWebKey("ArrowDown", true, false, false) == "SpeedDown");
        Assert("WebKey Ctrl+0 -> SpeedReset", HotkeyManager.MatchWebKey("0", true, false, false) == "SpeedReset");
        Assert("WebKey Pause -> TogglePause", HotkeyManager.MatchWebKey("Pause", false, false, false) == "TogglePause");
        Assert("WebKey Ctrl+V -> ToggleDataInspector", HotkeyManager.MatchWebKey("v", true, false, false) == "ToggleDataInspector");
        Assert("WebKey Ctrl+M -> ToggleMapViewer", HotkeyManager.MatchWebKey("m", true, false, false) == "ToggleMapViewer");
        Assert("WebKey F3 -> ToggleEspOverlay", HotkeyManager.MatchWebKey("F3", false, false, false) == "ToggleEspOverlay");
        Assert("WebKey F2 -> ToggleTileInspector", HotkeyManager.MatchWebKey("F2", false, false, false) == "ToggleTileInspector");
        Assert("WebKey F4 -> ToggleAutoMessage", HotkeyManager.MatchWebKey("F4", false, false, false) == "ToggleAutoMessage");
        Assert("WebKey Ctrl+K -> ToggleSkipMessage", HotkeyManager.MatchWebKey("k", true, false, false) == "ToggleSkipMessage");
        Assert("WebKey Ctrl+J -> ToggleMessageBar", HotkeyManager.MatchWebKey("j", true, false, false) == "ToggleMessageBar");

        // Dictionary Save & Load roundtrip
        var dict = HotkeyManager.SaveToDictionary();
        Assert("SaveToDictionary returns 24 items", dict.Count == 24);
        Assert("Summon hotkey defaults to Ctrl + G", HotkeyManager.DefaultHotkeys.Any(h => h.Id == "MultiSummon" && h.DefaultGesture == "Ctrl + G"));
        Assert("Default hotkeys do not share a key", HotkeyManager.DefaultHotkeys.GroupBy(h => h.DefaultGesture).All(g => g.Count() == 1));
        dict["QuickSave"] = "F6";
        HotkeyManager.Load(dict);
        Assert("Custom binding QuickSave=F6 applied", HotkeyManager.MatchWebKey("F6", false, false, false) == "QuickSave");
        HotkeyManager.ResetToDefaults();
        Assert("ResetToDefaults restores QuickSave=F5", HotkeyManager.MatchWebKey("F5", false, false, false) == "QuickSave");
    }

    static string Show(List<NoteMarkup.Segment> segs) =>
        string.Join("|", segs.Select(s => s.Style == NoteStyle.None ? s.Text : $"{s.Text}[{s.Style}]"));

    private static void TestNoteMarkup()
    {
        Console.WriteLine("--- Testing NoteMarkup ---");
        void Check(string input, string expected)
        {
            string got = Show(NoteMarkup.Parse(input));
            Assert($"markup '{input}'", got == expected, $"got={got} expected={expected}");
        }
        Check("plain", "plain");
        Check("a *it* b", "a |it[Italic]| b");
        Check("**bold**", "bold[Bold]");
        Check("***both***", "both[Bold, Italic]");
        Check("_under_ x", "under[Underline]| x");
        Check("~~gone~~", "gone[Strike]");
        Check("**굵게 *기울임* 끝**", "굵게 [Bold]|기울임[Bold, Italic]| 끝[Bold]");
        Check("snake_case_name", "snake_case_name");
        Check("2 * 3 * 4", "2 * 3 * 4");
        Check("RPG-Maker - 목록", "RPG-Maker - 목록");
        Check("\\*literal\\*", "*literal*");
        Check("*unclosed", "*unclosed");
        Check("*line\nbreak*", "*line\nbreak*");
        Check("~single~", "~single~");
    }

    private static void TestHotkeyMigrationAndStreamer()
    {
        Console.WriteLine("--- Testing hotkey migration / streamer mode ---");
        var saved = new Dictionary<string, string> { ["Screenshot"] = "F12", ["QuickSave"] = "F6" };
        Assert("Migration drops the old F12 screenshot default", HotkeyManager.MigrateSaved(saved, 0) && !saved.ContainsKey("Screenshot"));
        Assert("Migration keeps other custom bindings", saved["QuickSave"] == "F6");
        var custom = new Dictionary<string, string> { ["Screenshot"] = "Ctrl + F9" };
        Assert("Migration keeps a custom screenshot key", !HotkeyManager.MigrateSaved(custom, 0) && custom["Screenshot"] == "Ctrl + F9");
        var later = new Dictionary<string, string> { ["Screenshot"] = "F12" };
        Assert("After migration, F12 chosen by the user is kept", !HotkeyManager.MigrateSaved(later, HotkeyManager.CurrentSchema) && later["Screenshot"] == "F12");
        HotkeyManager.Load(null);
        Assert("Screenshot default is Ctrl + Shift + A", HotkeyManager.ActiveHotkeys.First(h => h.Id == "Screenshot").CurrentGesture == "Ctrl + Shift + A");

        HotkeyManager.StreamerMode = true;
        Assert("Streamer mode blocks tool hotkeys", !HotkeyManager.IsAllowed("ToggleEspOverlay") && !HotkeyManager.IsAllowed("QuickSave"));
        Assert("Streamer mode keeps the notes hotkey", HotkeyManager.IsAllowed("ToggleNotes"));
        Assert("Streamer mode: web key match returns null for tools", HotkeyManager.MatchWebKey("F5", false, false, false) == null);
        HotkeyManager.StreamerMode = false;
        Assert("Normal mode: web key F5 is quick save", HotkeyManager.MatchWebKey("F5", false, false, false) == "QuickSave");

        Assert("Discord state: engine", DiscordPresence.StateLine("게임", "RPG Maker 2003", false) == "RPG Maker 2003");
        Assert("Discord state: engine + streaming", DiscordPresence.StateLine("게임", "RPG Maker 2003", true) == "RPG Maker 2003 · 방송 중");
        Assert("Discord state: idle", DiscordPresence.StateLine(null, null, false) == null);
    }

    private static void TestSteamLanguage()
    {
        Console.WriteLine("--- Testing SteamLanguage ---");

        string acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"1901370\"\n\t\"installdir\"\t\t\"Ib\"\n" +
                     "\t\"UserConfig\"\n\t{\n\t\t\"language\"\t\t\"koreana\"\n\t}\n" +
                     "\t\"MountedConfig\"\n\t{\n\t\t\"language\"\t\t\"japanese\"\n\t}\n}";
        Assert("appmanifest UserConfig language wins", SteamLanguage.GameLanguage(acf) == "koreana", $"Actual={SteamLanguage.GameLanguage(acf)}");
        string mounted = "\"AppState\"\n{\n\t\"MountedConfig\"\n\t{\n\t\t\"language\"\t\t\"schinese\"\n\t}\n}";
        Assert("appmanifest MountedConfig language fallback", SteamLanguage.GameLanguage(mounted) == "schinese");
        Assert("appmanifest without language -> null", SteamLanguage.GameLanguage("\"AppState\"\n{\n}") == null);

        Func<string, System.Globalization.CultureInfo> ci = System.Globalization.CultureInfo.GetCultureInfo;
        Assert("ko-KR -> koreana", SteamLanguage.FromCulture(ci("ko-KR")) == "koreana");
        Assert("ja-JP -> japanese", SteamLanguage.FromCulture(ci("ja-JP")) == "japanese");
        Assert("zh-TW -> tchinese", SteamLanguage.FromCulture(ci("zh-TW")) == "tchinese");
        Assert("zh-CN -> schinese", SteamLanguage.FromCulture(ci("zh-CN")) == "schinese");
        Assert("pt-BR -> brazilian", SteamLanguage.FromCulture(ci("pt-BR")) == "brazilian");
        Assert("en-US -> english", SteamLanguage.FromCulture(ci("en-US")) == "english");

        Assert("folder outside a Steam library -> null", SteamLanguage.ForGame(Path.GetTempPath()) == null);
    }

    private static void TestSettingsService()
    {
        Console.WriteLine("--- Testing SettingsService & GlobalSettings ---");

        var settings = new GlobalSettings();
        Assert("Default Volume is 100", settings.Volume == 100);
        Assert("Default Gamma is 1.0", Math.Abs(settings.Gamma - 1.0) < 0.001);
        Assert("Default Ratio is 'none'", settings.Ratio == "none");
        Assert("Default Filter is 'none'", settings.Filter == "none");

        string json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<GlobalSettings>(json);
        Assert("GlobalSettings roundtrip JSON serialization preserves Ratio", deserialized?.Ratio == "none");
        var legacy = JsonSerializer.Deserialize<GlobalSettings>("{\"RenderMode\":\"capture\",\"Volume\":40}");
        Assert("Old settings with RenderMode still load", legacy?.Volume == 40);
    }
}
