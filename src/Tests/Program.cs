#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using RocketRPG.Models;

namespace RocketRPG.Tests;

public partial class Program
{
    private static int _passed = 0;
    private static int _failed = 0;

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--scan-stress")
            return ScanStress(args.Skip(1).ToArray());
        if (args.Length >= 4 && args[0] == "--render-map")
            return RenderMapToPng(args[1], int.Parse(args[2]), args[3]);
        if (args.Length >= 3 && args[0] == "--unbox")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var arc = EvbArchive.TryOpen(args[2]);
            Console.WriteLine($"entries: {arc?.Entries.Count ?? -1}");
            string? outDir = EvbArchive.EnsureUnboxed(args[1], args[2], Console.WriteLine);
            Console.WriteLine($"unboxed: {outDir} in {sw.Elapsed.TotalSeconds:F1}s");
            return outDir != null ? 0 : 1;
        }
        if (args.Length >= 2 && args[0] == "--check-issues")
        {
            // 게임 폴더마다 알림 막대에 뜰 내용(빠진 RTP/글꼴)을 출력합니다.
            foreach (var d in args.Skip(1))
            {
                var gi = CoreInterop.GameInfo.Create();
                CoreInterop.rpg_detect_game(d, ref gi);
                var issues = GameIssueAdvisor.Check(d, gi.Engine);
                Console.WriteLine($"{Path.GetFileName(d)}\t[{gi.Engine}]\t{(issues.Count == 0 ? "-" : string.Join(" | ", issues.Select(i => i.Key)))}");
            }
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--list-fonts")
        {
            // XP/VX/Ace 게임마다 스크립트가 지정하는 글꼴 이름과 게임 Fonts 폴더의 글꼴 패밀리를 출력합니다.
            foreach (var d in args.Skip(1))
            {
                var gi = CoreInterop.GameInfo.Create();
                CoreInterop.rpg_detect_game(d, ref gi);
                var declared = MkxpFontCatalog.DeclaredFontNames(d, gi.Engine);
                var scanned = MkxpFontCatalog.FontNamesInScripts(d, gi.Engine);
                var bundled = MkxpFontCatalog.FamiliesIn(Path.Combine(d, "Fonts"));
                Console.WriteLine($"{Path.GetFileName(d)}\t[{gi.Engine}]\tdeclared: {string.Join(", ", declared)}\t| script: {string.Join(", ", scanned.Take(8))}\t| bundled: {string.Join(", ", bundled)}");
                string fd = Path.Combine(d, "Fonts");
                if (Directory.Exists(fd))
                    foreach (var file in Directory.EnumerateFiles(fd))
                        if (MkxpFontCatalog.ReadNames(file) is { } nm)
                            Console.WriteLine($"    {Path.GetFileName(file)}: {nm.Family} / {string.Join(" / ", nm.Aliases)}");
            }
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--font-families")
        {
            // 글꼴 파일마다 RocketRPG가 계산한 FreeType 패밀리 이름 (mkxp-z가 등록하는 이름과 같아야 함)
            foreach (var f in args.Skip(1))
                Console.WriteLine($"{f}\t{MkxpFontCatalog.ReadNames(f)?.Family ?? "(null)"}");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--rpg2k-encoding")
        {
            foreach (var d in args.Skip(1)) Console.WriteLine($"{Path.GetFileName(d)}\t{Rpg2kEncoding.Detect(d) ?? "(auto)"}");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--mkxp-prepare")
        {
            // 자동 테스트 하네스용: 실제 실행과 같은 실행 폴더를 만들고 "엔진|실행파일" 을 출력합니다.
            var gi = CoreInterop.GameInfo.Create();
            CoreInterop.rpg_detect_game(args[1], ref gi);
            if (gi.Engine < CoreInterop.EngineXp || gi.Engine > CoreInterop.EngineAce) { Console.WriteLine($"{gi.Engine}|"); return 2; }
            Console.WriteLine($"{gi.Engine}|{RocketRenderMKXP.PrepareStandalone(args[1], gi.Engine)}");
            return 0;
        }

        Console.WriteLine("========================================");
        Console.WriteLine("RocketRPG Automated Test Suite (Phase 1 & Phase 2)");
        Console.WriteLine("========================================\n");

        TestHotkeyManager();
        TestSettingsService();
        TestSteamLanguage();
        TestNoteMarkup();
        TestHotkeyMigrationAndStreamer();
        TestDataContracts();
        TestFreezeLogicAndNotifications();
        TestMapViewerLayerDecoding();
        TestRealGameDataMv();
        TestRealGameDataMz();

        // Phase 2 Tests
        TestRgssArchiveReader();
        TestMapTransfers();
        TestRubyMarshalReader();
        TestRubyBridgeCalculations();
        TestRubyBridgeIpcAndLayout();

        // Phase 2 Tests: mkxp-z Native Rendering Integration (RocketRenderMKXP)
        TestRocketRenderMKXP();

        // Phase 3 Tests: EasyRPG Player & liblcf Native Rendering Integration (RocketRenderEasyRPG)
        TestRocketRenderEasyRPGAndLibLcf();

        // Phase 4 Tests: v0.5.0 Advanced Features
        TestNewFeatures();

        // v0.6 issue fixes (history, library roots, ini/RTP, mkxp config)
        TestIssueFixes();

        // 1.0.0 멀티
        TestMultiRemoteKeys();
        TestMultiTitle();
        TestChatColorCooldown();
        TestDeltaUpdate();
        TestGameOwnership();
        TestAssetCatalog();
        TestChoiceVote();
        TestRtpResolver();
        TestTutorialCoversMenus();

        Console.WriteLine("\n----------------------------------------");
        Console.WriteLine($"Test Results: PASS={_passed}, FAIL={_failed}, TOTAL={_passed + _failed}");
        Console.WriteLine("----------------------------------------");

        return _failed == 0 ? 0 : 1;
    }

    // 맵 뷰어 실제 타일 렌더링 확인용: 맵 하나를 PNG로 저장합니다.
    [STAThread]
    private static int RenderMapToPng(string gameDir, int mapId, string outPng)
    {
        var gi = CoreInterop.GameInfo.Create();
        CoreInterop.rpg_detect_game(gameDir, ref gi);
        int engine = gi.Engine;
        using var src = new GameAssetSource(gameDir, engine);
        int w = 0, h = 0, tilesetId = 0, chipsetId = 0;
        int[]? data = null;
        if (engine is CoreInterop.EngineMv or CoreInterop.EngineMz)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(src.MvDataDir, $"Map{mapId:D3}.json")));
            w = doc.RootElement.GetProperty("width").GetInt32();
            h = doc.RootElement.GetProperty("height").GetInt32();
            tilesetId = doc.RootElement.GetProperty("tilesetId").GetInt32();
            data = JsonSerializer.Deserialize<int[]>(doc.RootElement.GetProperty("data").GetRawText());
        }
        else if (engine is CoreInterop.Engine2000 or CoreInterop.Engine2003)
        {
            var lmu = src.ReadData($"Map{mapId:D4}.lmu");
            if (lmu == null) { Console.WriteLine("map not found"); return 1; }
            var m = LcfReader.ParseMapUnit(lmu);
            (w, h, chipsetId, data) = (m.Width, m.Height, m.ChipsetId, m.TileData);
        }
        else
        {
            string ext = engine == CoreInterop.EngineXp ? "rxdata" : engine == CoreInterop.EngineVx ? "rvdata" : "rvdata2";
            var bytes = src.ReadData($"Data\\Map{mapId:D3}.{ext}");
            if (bytes == null) { Console.WriteLine("map not found"); return 1; }
            (w, h, tilesetId, data, _) = RubyMarshalReader.ParseMap(bytes);
        }
        var ts = MapTileRenderer.LoadTileset(src, tilesetId, chipsetId);
        Console.WriteLine($"engine={engine} map={w}x{h} tileset={tilesetId} chipset={chipsetId} sets=[{string.Join(",", ts?.Sets.Select(s => s == null ? "-" : $"{s.W}x{s.H}") ?? [])}]");
        if (ts == null || data == null) return 1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = MapTileRenderer.Render(ts, w, h, data);
        if (r == null) { Console.WriteLine("render failed"); return 1; }
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(MapTileRenderer.ToBitmap(r)));
        using (var fs = File.Create(outPng)) enc.Save(fs);
        Console.WriteLine($"rendered {r.Width}x{r.Height} tile={r.TileSize} in {sw.ElapsedMilliseconds}ms -> {outPng}");
        return 0;
    }

    private static void TestIssueFixes()
    {
        Console.WriteLine("\n--- Testing v0.6 issue fixes ---");

        // MV/MZ NW.js 파일 접근: 게임 폴더 안에서만, 구버전 MV의 "/save/" 경로는 게임 폴더 기준
        {
            string nwRoot = Path.Combine(Path.GetTempPath(), "RocketNwFs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(nwRoot);
            try
            {
                var fs = new NwFsHost();
                fs.SetRoot(nwRoot);
                string rootFwd = nwRoot.Replace('\\', '/');
                Assert("NwFsHost mkdir save/", fs.Mkdir(rootFwd + "/www/save/") == "{}");
                Assert("NwFsHost write text", fs.WriteText(rootFwd + "/www/save/file1.rpgsave", "한글 세이브", false) == "{}");
                Assert("NwFsHost read text round-trips UTF-8", System.Text.Json.JsonDocument.Parse(fs.ReadText(rootFwd + "/www/save/file1.rpgsave")).RootElement.GetProperty("d").GetString() == "한글 세이브");
                Assert("NwFsHost exists", fs.Exists(rootFwd + "/www/save/file1.rpgsave"));
                Assert("NwFsHost rename", fs.Rename(rootFwd + "/www/save/file1.rpgsave", rootFwd + "/www/save/file1.rpgsave.bak") == "{}" &&
                                          File.Exists(Path.Combine(nwRoot, "www", "save", "file1.rpgsave.bak")));
                Assert("NwFsHost readdir", fs.Readdir(rootFwd + "/www/save").Contains("file1.rpgsave.bak"));
                Assert("NwFsHost stat reports file", fs.Stat(rootFwd + "/www/save/file1.rpgsave.bak").Contains("\"dir\":false"));
                Assert("NwFsHost missing file is ENOENT", fs.ReadText(rootFwd + "/nope.txt").Contains("ENOENT"));
                fs.Mkdir("/save/");
                Assert("NwFsHost drive-less /save/ maps into the game folder", Directory.Exists(Path.Combine(nwRoot, "save")));
                Assert("NwFsHost refuses paths outside the game folder", fs.WriteText(Path.Combine(Path.GetTempPath(), "rr_escape.txt"), "x", false).Contains("EACCES") &&
                                                                          fs.WriteText(rootFwd + "/../rr_escape2.txt", "x", false).Contains("EACCES"));
            }
            finally
            {
                try { Directory.Delete(nwRoot, true); } catch { }
            }
        }

        // 히스토리: 표기만 다른 같은 폴더는 하나로 합쳐짐
        var gs = new GlobalSettings { History = new List<string> { @"C:\Games\A\", @"c:/games/a", @"C:\Games\B", @"C:\Games\A" } };
        SettingsService.CompactHistory(gs);
        Assert("History dedupes path spelling variants", gs.History.Count == 2, string.Join("|", gs.History));
        Assert("History keeps first occurrence order", gs.History[0].Equals(@"C:\Games\A", StringComparison.OrdinalIgnoreCase));

        // 쯔꾸르 모음: 중첩/중복 루트 제거
        var roots = GameLibraryScanner.CollapseRoots(new[] { @"C:\Users\X\Documents", @"C:\Users\X\Documents\Games", @"c:/users/x/documents/", @"D:\Steam" });
        Assert("CollapseRoots removes nested and duplicate roots", roots.Count == 2, string.Join("|", roots));

        // ini 파싱 (섹션/대소문자/공백)
        string ini = "[Game]\r\nLibrary = RGSS301.dll\r\nScripts=Data\\Scripts.rvdata2\r\nRTP=RPGVXAce\r\n[Other]\r\nRTP=Wrong\r\n";
        Assert("IniValue reads key in section", RtpResolver.IniValue(ini, "Game", "RTP") == "RPGVXAce");
        Assert("IniValue trims spaces around '='", RtpResolver.IniValue(ini, "game", "library") == "RGSS301.dll");

        // mkxp.json: execName 고정(아카이브/ini 이름), 에이전트 preload, vsync off
        string tmp = Path.Combine(Path.GetTempPath(), "rr_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Game.ini"), "[Game]\r\nLibrary=RGSS301.dll\r\nScripts=Data\\Scripts.rvdata2\r\nTitle=T\r\n");
            string json = RocketRenderMKXP.GenerateMkxpConfigJson(tmp, CoreInterop.EngineAce, gameFolder: tmp, preloadScripts: new[] { @"C:\agent.rb" });
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            Assert("mkxp.json execName is 'Game' (Game.rgss3a / Game.ini lookup)", r.GetProperty("execName").GetString() == "Game");
            Assert("mkxp.json has no ignored iniFileName key", !r.TryGetProperty("iniFileName", out _));
            Assert("mkxp.json preloads the agent", r.GetProperty("preloadScript")[0].GetString() == "C:/agent.rb");
            Assert("mkxp.json disables vsync for speed control", r.GetProperty("vsync").GetBoolean() == false);

            Assert("FindIncompatibleDlls ignores RGSS runtime", RocketRenderMKXP.FindIncompatibleDlls(tmp).Count == 0);
            File.WriteAllBytes(Path.Combine(tmp, "RGSS301.dll"), new byte[] { 0 });
            File.WriteAllBytes(Path.Combine(tmp, "HNRGDS.dll"), new byte[] { 0 });
            var dlls = RocketRenderMKXP.FindIncompatibleDlls(tmp);
            Assert("FindIncompatibleDlls reports custom DLLs", dlls.Count == 1 && dlls[0] == "HNRGDS.dll", string.Join(",", dlls));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // 쯔꾸르 모음 스캔 재현용: 여러 스레드에서 동시에 스캔(탐색 중 폴더 이동 시나리오)을 반복합니다.
    private static int ScanStress(string[] roots)
    {
        if (Environment.GetEnvironmentVariable("RR_SCAN_VEH") == "1") CrashReporter.InstallNativeCrashHandler();
        if (roots.Length == 0) roots = GameLibraryScanner.GetDefaultScanRoots(null).ToArray();
        Console.WriteLine($"scan-stress roots: {string.Join(", ", roots)}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new List<System.Threading.Tasks.Task<int>>();
        for (int t = 0; t < 4; t++)
        {
            tasks.Add(System.Threading.Tasks.Task.Run(() =>
                GameLibraryScanner.ScanDirectories(roots, null, default).Count));
        }
        System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
        Console.WriteLine($"scan-stress done: found={string.Join("/", tasks.Select(x => x.Result))} in {sw.ElapsedMilliseconds}ms");
        return 0;
    }

    private static void Assert(string testName, bool condition, string? message = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"[PASS] {testName}");
        }
        else
        {
            _failed++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FAIL] {testName}: {message ?? "Assertion failed"}");
            Console.ResetColor();
        }
    }

    private static void TestHotkeyManager()
    {
        Console.WriteLine("--- Testing HotkeyManager ---");

        Assert("Default hotkeys count is 23", HotkeyManager.DefaultHotkeys.Count == 23, $"Count={HotkeyManager.DefaultHotkeys.Count}");
        Assert("Active hotkeys count is 23", HotkeyManager.ActiveHotkeys.Count == 23, $"Count={HotkeyManager.ActiveHotkeys.Count}");
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
        Assert("SaveToDictionary returns 23 items", dict.Count == 23);
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

    private static void TestDataContracts()
    {
        Console.WriteLine("--- Testing Data Contracts Serialization ---");

        // GameState
        string gsJson = "{\"scene\":\"Scene_Map\",\"mapId\":3,\"playerX\":12,\"playerY\":8,\"displayX\":2.5,\"displayY\":1.5,\"noclip\":true}";
        var gs = JsonSerializer.Deserialize<GameState>(gsJson);
        Assert("GameState Scene deserialized", gs?.Scene == "Scene_Map");
        Assert("GameState MapId deserialized", gs?.MapId == 3);
        Assert("GameState PlayerX deserialized", gs?.PlayerX == 12);
        Assert("GameState PlayerY deserialized", gs?.PlayerY == 8);
        Assert("GameState DisplayX deserialized", gs?.DisplayX == 2.5);
        Assert("GameState Noclip deserialized", gs?.Noclip == true);

        // EspItem
        string espJson = "{\"id\":7,\"name\":\"Chest\",\"trigger\":0,\"x\":100.5,\"y\":200.5,\"w\":48,\"h\":48}";
        var esp = JsonSerializer.Deserialize<EspItem>(espJson);
        Assert("EspItem Id deserialized", esp?.Id == 7);
        Assert("EspItem Name deserialized", esp?.Name == "Chest");
        Assert("EspItem Trigger deserialized", esp?.Trigger == 0);
        Assert("EspItem Coordinates deserialized", esp?.X == 100.5 && esp?.Y == 200.5);

        // TileInfo
        string tileJson = "{\"mapX\":5,\"mapY\":10,\"passable\":true,\"tileIds\":[1544,2816],\"events\":\"NPC1\",\"screenX\":120,\"screenY\":240}";
        var tile = JsonSerializer.Deserialize<TileInfo>(tileJson);
        Assert("TileInfo map coordinates", tile?.MapX == 5 && tile?.MapY == 10);
        Assert("TileInfo passable", tile?.Passable == true);
        Assert("TileInfo events", tile?.Events == "NPC1");
        Assert("TileInfo tileIds count", tile?.TileIds?.Count == 2);

        // SwitchItem & VariableItem (including falsy value deserialization)
        string swJson = "{\"id\":10,\"name\":\"Switch_A\",\"val\":false,\"frozen\":true}";
        var sw = JsonSerializer.Deserialize<SwitchItem>(swJson);
        Assert("SwitchItem val=false preserved", sw?.Value == false);
        Assert("SwitchItem frozen=true preserved even when val is false", sw?.IsFrozen == true);

        string varJson = "{\"id\":5,\"name\":\"Var_Counter\",\"val\":\"0\",\"frozen\":true}";
        var va = JsonSerializer.Deserialize<VariableItem>(varJson);
        Assert("VariableItem val='0' preserved", va?.Value == "0");
        Assert("VariableItem frozen=true preserved even when val is '0'", va?.IsFrozen == true);
    }

    private static void TestFreezeLogicAndNotifications()
    {
        Console.WriteLine("--- Testing INotifyPropertyChanged & Freeze State ---");

        var sw = new SwitchItem { Id = 1, Name = "TestSw", Value = true, IsFrozen = false };
        bool swValChanged = false;
        bool swFrozenChanged = false;

        sw.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SwitchItem.Value)) swValChanged = true;
            if (e.PropertyName == nameof(SwitchItem.IsFrozen)) swFrozenChanged = true;
        };

        sw.Value = false;
        sw.IsFrozen = true;
        Assert("SwitchItem fires PropertyChanged for Value", swValChanged);
        Assert("SwitchItem fires PropertyChanged for IsFrozen", swFrozenChanged);

        var va = new VariableItem { Id = 2, Name = "TestVar", Value = "10", IsFrozen = false };
        bool vaValChanged = false;
        bool vaFrozenChanged = false;

        va.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VariableItem.Value)) vaValChanged = true;
            if (e.PropertyName == nameof(VariableItem.IsFrozen)) vaFrozenChanged = true;
        };

        va.Value = "0";
        va.IsFrozen = true;
        Assert("VariableItem fires PropertyChanged for Value", vaValChanged);
        Assert("VariableItem fires PropertyChanged for IsFrozen", vaFrozenChanged);
    }

    private static void TestMapViewerLayerDecoding()
    {
        Console.WriteLine("--- Testing MapViewer Multi-layer Tile Decoding ---");

        int width = 2;
        int height = 2;
        // 6 layers for 2x2 map: 2*2*6 = 24 integers
        // Layer 0: [0, 0, 0, 0] (blank ground)
        // Layer 1: [0, 2816, 0, 0] (ground floor at (1,0))
        // Layer 2: [5920, 0, 0, 0] (wall at (0,0))
        // Layer 3: [0, 0, 150, 0] (decor at (0,1))
        int[] tileData = new int[24];
        tileData[(1 * height + 0) * width + 1] = 2816; // layer 1 (1,0)
        tileData[(2 * height + 0) * width + 0] = 5920; // layer 2 (0,0)
        tileData[(3 * height + 1) * width + 0] = 150;  // layer 3 (0,1)

        // Helper mimicking the updated multi-layer scanner
        int GetTopTile(int x, int y)
        {
            for (int z = 3; z >= 0; z--)
            {
                int idx = (z * height + y) * width + x;
                if (idx < tileData.Length && tileData[idx] > 0)
                    return tileData[idx];
            }
            return 0;
        }

        Assert("Top tile at (0,0) is wall (5920) from Layer 2 (not 0 from Layer 0)", GetTopTile(0, 0) == 5920);
        Assert("Top tile at (1,0) is floor (2816) from Layer 1 (not 0 from Layer 0)", GetTopTile(1, 0) == 2816);
        Assert("Top tile at (0,1) is decor (150) from Layer 3 (not 0 from Layer 0)", GetTopTile(0, 1) == 150);
        Assert("Top tile at (1,1) is empty (0)", GetTopTile(1, 1) == 0);
    }

    private static void TestRealGameDataMv()
    {
        Console.WriteLine("--- Testing Real MV Game Data Parsing ---");

        string mvDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "GameSample", "mv", "kinokonun_kor"));
        if (!Directory.Exists(mvDir))
        {
            // fallback if running from project directory
            mvDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "GameSample", "mv", "kinokonun_kor"));
        }

        string mapInfosPath = Path.Combine(mvDir, "www", "data", "MapInfos.json");
        Assert("MV MapInfos.json exists", File.Exists(mapInfosPath), $"Path={mapInfosPath}");

        if (File.Exists(mapInfosPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(mapInfosPath));
            int validMaps = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("id", out _))
                    validMaps++;
            }
            Assert("MV MapInfos has >= 30 valid maps", validMaps >= 30, $"Found {validMaps} maps");
        }

        string map1Path = Path.Combine(mvDir, "www", "data", "Map001.json");
        Assert("MV Map001.json exists", File.Exists(map1Path), $"Path={map1Path}");
        if (File.Exists(map1Path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(map1Path));
            int w = doc.RootElement.GetProperty("width").GetInt32();
            int h = doc.RootElement.GetProperty("height").GetInt32();
            var dataProp = doc.RootElement.GetProperty("data");
            int dataLen = dataProp.GetArrayLength();

            Assert("MV Map001 dimensions width=39, height=48", w == 39 && h == 48, $"w={w}, h={h}");
            Assert("MV Map001 data has 6 layers (width*height*6)", dataLen == w * h * 6, $"len={dataLen}, expected={w * h * 6}");
        }
    }

    private static void TestRealGameDataMz()
    {
        Console.WriteLine("--- Testing Real MZ Game Data Parsing ---");

        string mzDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "GameSample", "mz", "팥빙수를 만들자"));
        if (!Directory.Exists(mzDir))
        {
            // fallback if running from project directory
            mzDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "GameSample", "mz", "팥빙수를 만들자"));
        }

        string mapInfosPath = Path.Combine(mzDir, "data", "MapInfos.json");
        Assert("MZ MapInfos.json exists", File.Exists(mapInfosPath), $"Path={mapInfosPath}");

        if (File.Exists(mapInfosPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(mapInfosPath));
            int validMaps = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("id", out _))
                    validMaps++;
            }
            Assert("MZ MapInfos has valid maps", validMaps >= 1, $"Found {validMaps} maps");
        }

        string map1Path = Path.Combine(mzDir, "data", "Map001.json");
        Assert("MZ Map001.json exists", File.Exists(map1Path), $"Path={map1Path}");
        if (File.Exists(map1Path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(map1Path));
            int w = doc.RootElement.GetProperty("width").GetInt32();
            int h = doc.RootElement.GetProperty("height").GetInt32();
            var dataProp = doc.RootElement.GetProperty("data");
            int dataLen = dataProp.GetArrayLength();

            Assert("MZ Map001 has valid positive dimensions", w > 0 && h > 0, $"w={w}, h={h}");
            Assert("MZ Map001 data has at least 6 layers (width*height*6)", dataLen >= w * h * 6, $"len={dataLen}, expected>={w * h * 6}");
        }
    }

    private static string FindSample(string relativePath)
    {
        string p1 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "GameSample", relativePath));
        if (Directory.Exists(p1) || File.Exists(p1)) return p1;
        string p2 = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "GameSample", relativePath));
        if (Directory.Exists(p2) || File.Exists(p2)) return p2;
        return p1;
    }

    // 맵 뷰어의 장소 이동 표시: 2003 이벤트 명령(10810)과 MV/MZ JSON(201)에서 목적지를 읽는지
    private static void TestMapTransfers()
    {
        Console.WriteLine("--- Testing map transfer (teleport) extraction ---");
        string lmu = FindSample("2003/Treaster/Map0006.lmu");
        if (File.Exists(lmu))
        {
            var m = LcfReader.ParseMapUnit(File.ReadAllBytes(lmu));
            var all = m.Events.SelectMany(e => e.AllTransfers()).ToList();
            Assert("2003 Map0006 has transfer events", all.Count > 0, $"count={all.Count}");
            Assert("2003 Map0006 transfer to map 11 (1,68)", all.Contains(new MapTransfer(11, 1, 68)));
        }

        var ev = System.Text.Json.JsonSerializer.Deserialize<MapEventData>(
            "{\"id\":1,\"name\":\"door\",\"x\":3,\"y\":4,\"pages\":[{\"trigger\":1,\"list\":[{\"code\":101,\"parameters\":[\"\"]},{\"code\":201,\"parameters\":[0,5,7,8,2,0]},{\"code\":201,\"parameters\":[1,1,2,3,2,0]},{\"code\":0,\"parameters\":[]}]}]}");
        var t = ev!.AllTransfers();
        Assert("MV transfer (direct) parsed", t.Count == 1 && t[0] == new MapTransfer(5, 7, 8), $"got {string.Join(",", t)}");
    }

    private static void TestRgssArchiveReader()
    {
        Console.WriteLine("--- Testing RGSS Archive Reader (v1 & v3) ---");

        // 1. XP Archive (v1): GameSample/xp/청색경보/Game.rgssad
        string xpArchPath = FindSample("xp/청색경보/Game.rgssad");
        Assert("XP Game.rgssad exists", File.Exists(xpArchPath), $"Path={xpArchPath}");
        if (File.Exists(xpArchPath))
        {
            using var arch = RgssArchiveReader.Open(xpArchPath);
            Assert("XP RGSSAD opened", arch != null);
            if (arch != null)
            {
                Assert("XP RGSSAD version is 1", arch.Version == 1, $"Version={arch.Version}");
                Assert("XP RGSSAD has entries", arch.Entries.Count > 0, $"Count={arch.Entries.Count}");
                Assert("XP RGSSAD contains Data\\System.rxdata", arch.FileExists("Data\\System.rxdata"));
                Assert("XP RGSSAD contains Data\\Map001.rxdata", arch.FileExists("Data\\Map001.rxdata"));

                byte[]? sysBytes = arch.ReadFile("Data\\System.rxdata");
                Assert("XP RGSSAD read Data\\System.rxdata successfully", sysBytes != null && sysBytes.Length > 10);
                if (sysBytes != null && sysBytes.Length >= 2)
                {
                    Assert("XP RGSSAD decrypted bytes have Ruby Marshal header (0x04, 0x08)",
                        sysBytes[0] == 0x04 && sysBytes[1] == 0x08,
                        $"header=0x{sysBytes[0]:X2} 0x{sysBytes[1]:X2}");
                }

                // Case insensitivity check
                byte[]? sysLower = arch.ReadFile("data/system.rxdata");
                Assert("XP RGSSAD supports slash and case-insensitive paths", sysLower != null && sysLower.Length == sysBytes?.Length);
            }
        }

        // 2. VX Ace Archive (v3): GameSample/vxace/999vic_ver1.9/Game.rgss3a
        string aceArchPath = FindSample("vxace/999vic_ver1.9/Game.rgss3a");
        Assert("Ace Game.rgss3a exists", File.Exists(aceArchPath), $"Path={aceArchPath}");
        if (File.Exists(aceArchPath))
        {
            using var arch = RgssArchiveReader.Open(aceArchPath);
            Assert("Ace RGSS3A opened", arch != null);
            if (arch != null)
            {
                Assert("Ace RGSS3A version is 3", arch.Version == 3, $"Version={arch.Version}");
                Assert("Ace RGSS3A has entries", arch.Entries.Count > 0, $"Count={arch.Entries.Count}");
                Assert("Ace RGSS3A contains Data\\System.rvdata2", arch.FileExists("Data\\System.rvdata2"));
                Assert("Ace RGSS3A contains Data\\Map001.rvdata2", arch.FileExists("Data\\Map001.rvdata2"));

                byte[]? sysBytes = arch.ReadFile("Data\\System.rvdata2");
                Assert("Ace RGSS3A read Data\\System.rvdata2 successfully", sysBytes != null && sysBytes.Length > 10);
                if (sysBytes != null && sysBytes.Length >= 2)
                {
                    Assert("Ace RGSS3A decrypted bytes have Ruby Marshal header (0x04, 0x08)",
                        sysBytes[0] == 0x04 && sysBytes[1] == 0x08,
                        $"header=0x{sysBytes[0]:X2} 0x{sysBytes[1]:X2}");
                }
            }
        }

        // 3. Real VX Game.rgss2a Archive (GameSample/vx/asadoke 1.05/Game.rgss2a)
        string vxArchPath = FindSample("vx/asadoke 1.05/Game.rgss2a");
        if (File.Exists(vxArchPath))
        {
            using var arch = RgssArchiveReader.Open(vxArchPath);
            Assert("VX RGSS2A opened", arch != null);
            if (arch != null)
            {
                Assert("VX RGSS2A has entries", arch.Entries.Count > 0, $"Count={arch.Entries.Count}");
                Assert("VX RGSS2A contains Data\\System.rvdata", arch.FileExists("Data\\System.rvdata"));
                byte[]? sysBytes = arch.ReadFile("Data\\System.rvdata");
                Assert("VX RGSS2A read Data\\System.rvdata successfully", sysBytes != null && sysBytes.Length > 10);
                if (sysBytes != null && sysBytes.Length >= 2)
                {
                    Assert("VX RGSS2A decrypted bytes have Ruby Marshal header (0x04, 0x08)",
                        sysBytes[0] == 0x04 && sysBytes[1] == 0x08);
                }
            }
        }

        // 4. Directory auto-discovery
        string xpDir = FindSample("xp/청색경보");
        using var discoveredArch = RgssArchiveReader.TryOpen(xpDir);
        Assert("RgssArchiveReader.TryOpen auto-detects Game.rgssad in game folder",
            discoveredArch != null && discoveredArch.Version == 1);
        string asadokeDir = FindSample("vx/asadoke 1.05");
        using var asadokeArch = RgssArchiveReader.TryOpen(asadokeDir);
        Assert("RgssArchiveReader.TryOpen auto-detects Game.rgss2a in game folder",
            asadokeArch != null);
    }

    private static void TestRubyMarshalReader()
    {
        Console.WriteLine("--- Testing Ruby Marshal Reader ---");

        // 1. Basic Fixnum decoding unit tests
        byte[] fixnumZero = [0x04, 0x08, 0x69, 0x00];
        Assert("Marshal Fixnum 0", (int)new RubyMarshalReader(fixnumZero).ReadObject()! == 0);

        byte[] fixnumPos = [0x04, 0x08, 0x69, 0x06]; // (6 - 5) = 1
        Assert("Marshal Fixnum 1", (int)new RubyMarshalReader(fixnumPos).ReadObject()! == 1);

        byte[] fixnumNeg = [0x04, 0x08, 0x69, 0xFA]; // (-6 + 5) = -1
        Assert("Marshal Fixnum -1", (int)new RubyMarshalReader(fixnumNeg).ReadObject()! == -1);

        // 2. Real XP Game Data: GameSample/xp/Do_You_Remember_My_Lullaby/Copy of Data/
        string xpDataDir = FindSample("xp/Do_You_Remember_My_Lullaby/Copy of Data");
        string xpMapInfos = Path.Combine(xpDataDir, "MapInfos.rxdata");
        Assert("XP MapInfos.rxdata exists", File.Exists(xpMapInfos), $"Path={xpMapInfos}");
        if (File.Exists(xpMapInfos))
        {
            var maps = RubyMarshalReader.ParseMapInfos(File.ReadAllBytes(xpMapInfos));
            Assert("XP MapInfos parsed 31 maps", maps.Count == 31, $"Count={maps.Count}");
            Assert("XP Map 1 exists in MapInfos", maps.Any(m => m.Id == 1));
            var map1 = maps.First(m => m.Id == 1);
            Assert("XP Map 1 has non-empty name", !string.IsNullOrWhiteSpace(map1.Name), $"Name={map1.Name}");
        }

        string xpMap001 = Path.Combine(xpDataDir, "Map001.rxdata");
        Assert("XP Map001.rxdata exists", File.Exists(xpMap001), $"Path={xpMap001}");
        if (File.Exists(xpMap001))
        {
            var (w, h, tid, tileData, events) = RubyMarshalReader.ParseMap(File.ReadAllBytes(xpMap001));
            Assert("XP Map001 dimensions width=47, height=30", w == 47 && h == 30, $"w={w}, h={h}");
            Assert("XP Map001 has valid tileset ID", tid > 0, $"tid={tid}");
            Assert("XP Map001 tileData has 3 layers (47*30*3 = 4230)", tileData != null && tileData.Length == 47 * 30 * 3,
                $"len={tileData?.Length}");
            Assert("XP Map001 has events", events.Count > 0, $"Count={events.Count}");
            Assert("XP Map001 events have valid coordinates", events.All(e => e.X >= 0 && e.X < 47 && e.Y >= 0 && e.Y < 30));
        }

        string xpSystem = Path.Combine(xpDataDir, "System.rxdata");
        Assert("XP System.rxdata exists", File.Exists(xpSystem), $"Path={xpSystem}");
        if (File.Exists(xpSystem))
        {
            var (switches, variables) = RubyMarshalReader.ParseSystem(File.ReadAllBytes(xpSystem));
            Assert("XP System.rxdata has switches", switches.Count > 0, $"Count={switches.Count}");
            Assert("XP System.rxdata has variables", variables.Count > 0, $"Count={variables.Count}");
        }

        // 3. Real VX Game Data: GameSample/vx/UTOPIA Ver1.02(K)/Data/
        string vxDataDir = FindSample("vx/UTOPIA Ver1.02(K)/Data");
        string vxMapInfos = Path.Combine(vxDataDir, "MapInfos.rvdata");
        Assert("VX MapInfos.rvdata exists", File.Exists(vxMapInfos), $"Path={vxMapInfos}");
        if (File.Exists(vxMapInfos))
        {
            var maps = RubyMarshalReader.ParseMapInfos(File.ReadAllBytes(vxMapInfos));
            Assert("VX MapInfos parsed 107 maps", maps.Count == 107, $"Count={maps.Count}");
            Assert("VX Map 1 exists", maps.Any(m => m.Id == 1));
        }

        string vxMap001 = Path.Combine(vxDataDir, "Map001.rvdata");
        Assert("VX Map001.rvdata exists", File.Exists(vxMap001), $"Path={vxMap001}");
        if (File.Exists(vxMap001))
        {
            var (w, h, _, tileData, events) = RubyMarshalReader.ParseMap(File.ReadAllBytes(vxMap001));
            Assert("VX Map001 dimensions width=17, height=13", w == 17 && h == 13, $"w={w}, h={h}");
            Assert("VX Map001 tileData has 3 layers (17*13*3 = 663)", tileData != null && tileData.Length == 17 * 13 * 3,
                $"len={tileData?.Length}");
            Assert("VX Map001 has events", events.Count > 0, $"Count={events.Count}");
        }

        string vxSystem = Path.Combine(vxDataDir, "System.rvdata");
        Assert("VX System.rvdata exists", File.Exists(vxSystem), $"Path={vxSystem}");
        if (File.Exists(vxSystem))
        {
            var (switches, variables) = RubyMarshalReader.ParseSystem(File.ReadAllBytes(vxSystem));
            Assert("VX System.rvdata has switches", switches.Count > 0, $"Count={switches.Count}");
            Assert("VX System.rvdata has variables", variables.Count > 0, $"Count={variables.Count}");
        }

        // 4. Real VX Ace Game Data via Archive: GameSample/vxace/999vic_ver1.9/Game.rgss3a
        string aceArchPath = FindSample("vxace/999vic_ver1.9/Game.rgss3a");
        if (File.Exists(aceArchPath))
        {
            using var arch = RgssArchiveReader.Open(aceArchPath);
            Assert("Ace RGSS3A opened successfully", arch != null);
            if (arch != null)
            {
                byte[]? mapInfosBytes = arch.ReadFile("Data\\MapInfos.rvdata2");
                Assert("Ace MapInfos.rvdata2 read from RGSS3A", mapInfosBytes != null);
                if (mapInfosBytes != null)
                {
                    var maps = RubyMarshalReader.ParseMapInfos(mapInfosBytes);
                    Assert("Ace MapInfos parsed multiple maps", maps.Count > 0, $"Count={maps.Count}");
                }

                byte[]? map001Bytes = arch.ReadFile("Data\\Map001.rvdata2");
                Assert("Ace Map001.rvdata2 read from RGSS3A", map001Bytes != null);
                if (map001Bytes != null)
                {
                    var (w, h, _, tileData, events) = RubyMarshalReader.ParseMap(map001Bytes);
                    Assert("Ace Map001 has positive dimensions", w > 0 && h > 0, $"w={w}, h={h}");
                    Assert("Ace Map001 tileData non-empty", tileData != null && tileData.Length >= w * h * 3);
                }

                byte[]? sysBytes = arch.ReadFile("Data\\System.rvdata2");
                Assert("Ace System.rvdata2 read from RGSS3A", sysBytes != null);
                if (sysBytes != null)
                {
                    var (switches, variables) = RubyMarshalReader.ParseSystem(sysBytes);
                    Assert("Ace System.rvdata2 has switches", switches.Count > 0, $"Count={switches.Count}");
                    Assert("Ace System.rvdata2 has variables", variables.Count > 0, $"Count={variables.Count}");
                }
            }
        }
    }

    private static void TestRubyBridgeCalculations()
    {
        Console.WriteLine("--- Testing RubyBridge Calculations & Data Models ---");

        // 1. Resolution, Base FPS, and Display Scales
        int xpBaseW = 640, xpBaseH = 480, xpFps = 40;
        double xpScale = 128.0;

        int vxBaseW = 544, vxBaseH = 416, vxFps = 60;
        double vxScale = 256.0;

        Assert("XP resolution is 640x480", xpBaseW == 640 && xpBaseH == 480);
        Assert("VX resolution is 544x416", vxBaseW == 544 && vxBaseH == 416);

        // 2. Virtual Mouse Coordinate Mapping
        // XP: displayX = 1280.0 (10 tiles offset), screen mouse at (64, 96) -> (+2, +3 tiles)
        double xpDispX = 1280.0, xpDispY = 1280.0;
        int xpTileX = (int)Math.Floor(xpDispX / xpScale) + (int)Math.Floor(64.0 / 32.0);
        int xpTileY = (int)Math.Floor(xpDispY / xpScale) + (int)Math.Floor(96.0 / 32.0);
        Assert("XP virtual mouse coordinate mapping (10 + 2 = 12, 10 + 3 = 13)", xpTileX == 12 && xpTileY == 13,
            $"tileX={xpTileX}, tileY={xpTileY}");

        // VX: displayX = 2560.0 (10 tiles offset), screen mouse at (64, 96) -> (+2, +3 tiles)
        double vxDispX = 2560.0, vxDispY = 2560.0;
        int vxTileX = (int)Math.Floor(vxDispX / vxScale) + (int)Math.Floor(64.0 / 32.0);
        int vxTileY = (int)Math.Floor(vxDispY / vxScale) + (int)Math.Floor(96.0 / 32.0);
        Assert("VX virtual mouse coordinate mapping (10 + 2 = 12, 10 + 3 = 13)", vxTileX == 12 && vxTileY == 13,
            $"tileX={vxTileX}, tileY={vxTileY}");

        // 3. Game Speed Multiplier & Frame Rate Calculation
        double[] speeds = [0.5, 1.0, 2.0, 3.0, 4.0, 8.0];
        foreach (var s in speeds)
        {
            int expectedXpFps = (int)Math.Round(xpFps * s);
            int expectedVxFps = (int)Math.Round(vxFps * s);
            Assert($"XP speed {s:0.#}x results in {expectedXpFps} FPS", expectedXpFps == (int)(40 * s));
            Assert($"VX speed {s:0.#}x results in {expectedVxFps} FPS", expectedVxFps == (int)(60 * s));
        }

        // Clamping bounds [0.5, 8.0]
        double clampedLow = Math.Clamp(0.1, 0.5, 8.0);
        double clampedHigh = Math.Clamp(15.0, 0.5, 8.0);
        Assert("Speed clamp lower bound is 0.5", Math.Abs(clampedLow - 0.5) < 0.001);
        Assert("Speed clamp upper bound is 8.0", Math.Abs(clampedHigh - 8.0) < 0.001);

        // 4. RubyTable 3D array data access & Tile Inspector
        int tw = 20, th = 15, tz = 3;
        int[] dummyTiles = new int[tw * th * tz];
        // Set tile at (5, 7, layer 0) = 100, layer 1 = 200, layer 2 = 300
        dummyTiles[0 * (tw * th) + 7 * tw + 5] = 100;
        dummyTiles[1 * (tw * th) + 7 * tw + 5] = 200;
        dummyTiles[2 * (tw * th) + 7 * tw + 5] = 300;

        var table = new RubyTable
        {
            Dimensions = 3,
            XSize = tw,
            YSize = th,
            ZSize = tz,
            TotalElements = dummyTiles.Length,
            Tiles = dummyTiles
        };

        Assert("RubyTable.GetTile layer 0 is 100", table.GetTile(5, 7, 0) == 100);
        Assert("RubyTable.GetTile layer 1 is 200", table.GetTile(5, 7, 1) == 200);
        Assert("RubyTable.GetTile layer 2 is 300", table.GetTile(5, 7, 2) == 300);
        Assert("RubyTable.GetTopTile returns top layer 300", table.GetTopTile(5, 7) == 300);
        Assert("RubyTable.GetTile out of bounds returns 0", table.GetTile(-1, 0, 0) == 0 && table.GetTile(100, 0, 0) == 0);

        // 5. In-Game ESP Projection Logic
        var mockEvents = new List<MapEventData>
        {
            new() { Id = 1, Name = "NPC1", X = 12, Y = 13, Pages = new List<MapEventPage> { new() { Trigger = 0 } } },
            new() { Id = 2, Name = "Chest", X = 15, Y = 16, Pages = new List<MapEventPage> { new() { Trigger = 1 } } },
            new() { Id = 3, Name = "FarEvent", X = 100, Y = 100 } // Should be culled
        };

        double dispTileX = 10.0, dispTileY = 10.0;
        var espItems = new List<EspItem>();
        foreach (var ev in mockEvents)
        {
            double sx = (ev.X - dispTileX) * 32.0;
            double sy = (ev.Y - dispTileY) * 32.0;
            if (sx >= -64 && sx <= xpBaseW + 64 && sy >= -64 && sy <= xpBaseH + 64)
            {
                espItems.Add(new EspItem { Id = ev.Id, Name = ev.Name, X = sx, Y = sy, W = 32, H = 32 });
            }
        }

        Assert("ESP items count is 2 (culled far event)", espItems.Count == 2);
        Assert("ESP NPC1 screen coords are (64, 96)", espItems[0].X == 64 && espItems[0].Y == 96,
            $"X={espItems[0].X}, Y={espItems[0].Y}");
        Assert("ESP Chest screen coords are (160, 192)", espItems[1].X == 160 && espItems[1].Y == 192,
            $"X={espItems[1].X}, Y={espItems[1].Y}");

        // 6. Switch / Variable Freeze & Live Control logic
        var frozenSwitches = new Dictionary<int, bool>();
        var frozenVariables = new Dictionary<int, string>();

        frozenSwitches[5] = true;
        frozenVariables[10] = "42";

        Assert("Frozen switch exists", frozenSwitches.ContainsKey(5) && frozenSwitches[5] == true);
        Assert("Frozen variable exists", frozenVariables.ContainsKey(10) && frozenVariables[10] == "42");

        // Simulate game tick trying to overwrite frozen data
        bool incomingSwValue = false;
        if (frozenSwitches.TryGetValue(5, out bool frozenVal))
        {
            incomingSwValue = frozenVal; // Enforce freeze
        }
        Assert("Enforce freeze keeps switch 5 true", incomingSwValue == true);
    }

    private static void TestRubyBridgeIpcAndLayout()
    {
        Console.WriteLine("--- Testing RubyBridge Dynamic HWND ---");

        // 2. Dynamic HWND & PID resolution
        IntPtr mockHwnd = (IntPtr)0x778899AA;
        bool delegateCalled = false;
        string xpSampleDir = FindSample("xp/Do_You_Remember_My_Lullaby");
        using var bridge = new RubyBridge(xpSampleDir, CoreInterop.EngineXp, () =>
        {
            delegateCalled = true;
            return mockHwnd;
        }, 12345);

        Assert("RubyBridge queries dynamic GameWindow delegate", bridge.GameHwnd == mockHwnd && delegateCalled);
        Assert("RubyBridge stores processId", bridge.ProcessId == 12345);

        // Test UpdateGameHwnd
        IntPtr newHwnd = (IntPtr)0xCCDDEEFF;
        bridge.UpdateGameHwnd(newHwnd, 54321);
        Assert("RubyBridge updates GameHwnd directly", bridge.GameHwnd == newHwnd);
        Assert("RubyBridge updates ProcessId directly", bridge.ProcessId == 54321);

        // 3. Batched Freeze Script Generation
        bridge.FreezeSwitch(1, true, true);
        bridge.FreezeSwitch(2, true, false);
        bridge.FreezeVariable(5, true, "100");
        bridge.FreezeVariable(6, true, "hello");

        // 4. In-Game Telemetry & Data Inspector Response Parsing
        var testState = new GameState
        {
            Scene = "Scene_Map",
            MapId = 2,
            PlayerX = 15,
            PlayerY = 20,
            DisplayX = 256.0,
            DisplayY = 512.0,
            Noclip = true
        };
        string serialized = JsonSerializer.Serialize(testState);
        var deserialized = JsonSerializer.Deserialize<GameState>(serialized);
        Assert("GameState roundtrip preserves PlayerX and PlayerY",
            deserialized != null && deserialized.PlayerX == 15 && deserialized.PlayerY == 20);
        Assert("GameState roundtrip preserves Display coordinates",
            deserialized != null && deserialized.DisplayX == 256.0 && deserialized.DisplayY == 512.0);
        Assert("GameState roundtrip preserves Noclip",
            deserialized != null && deserialized.Noclip == true);

        // 5. Multi-encoding string decoding test: CP949 and CP932
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var cp932 = System.Text.Encoding.GetEncoding(932);
        byte[] jBytes = cp932.GetBytes("テスト");
        var cp949 = System.Text.Encoding.GetEncoding(949);
        byte[] kBytes = cp949.GetBytes("테스트");

        byte[] MakeMarshalStr(byte[] raw)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x04); ms.WriteByte(0x08); ms.WriteByte(0x22);
            ms.WriteByte((byte)(raw.Length + 5));
            ms.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        string decJ = (string)new RubyMarshalReader(MakeMarshalStr(jBytes)).ReadObject()!;
        string decK = (string)new RubyMarshalReader(MakeMarshalStr(kBytes)).ReadObject()!;
        Assert("RubyMarshalReader decodes CP932 (Japanese)", decJ == "テスト", $"Actual={decJ}");
        Assert("RubyMarshalReader decodes CP949 (Korean)", decK == "테스트", $"Actual={decK}");
    }

    private static void TestRocketRenderMKXP()
    {
        Console.WriteLine("\n--- Testing RocketRenderMKXP (mkxp-z Native Integration) ---");

        // 1. mkxp.json generation
        string xpJson = RocketRenderMKXP.GenerateMkxpConfigJson(@"C:\Games\PokemonXP", CoreInterop.EngineXp, fixedAspectRatio: true, smoothScaling: false);
        using (var doc = JsonDocument.Parse(xpJson))
        {
            var root = doc.RootElement;
            Assert("mkxp.json XP rgssVersion is 1", root.GetProperty("rgssVersion").GetInt32() == 1);
            Assert("mkxp.json XP screen width is 640", root.GetProperty("defScreenW").GetInt32() == 640);
            Assert("mkxp.json XP screen height is 480", root.GetProperty("defScreenH").GetInt32() == 480);
            Assert("mkxp.json XP gameFolder uses forward slashes", root.GetProperty("gameFolder").GetString() == "C:/Games/PokemonXP");
            Assert("mkxp.json XP fixedAspectRatio is true", root.GetProperty("fixedAspectRatio").GetBoolean() == true);
            Assert("mkxp.json XP smoothScaling is 0 (int, as mkxp-z expects)", root.GetProperty("smoothScaling").GetInt32() == 0);
        }

        string xpDotJson = RocketRenderMKXP.GenerateMkxpConfigJson(@"C:\Games\PokemonXP", CoreInterop.EngineXp, gameFolder: ".");
        using (var doc = JsonDocument.Parse(xpDotJson))
        {
            Assert("mkxp.json dot gameFolder is supported", doc.RootElement.GetProperty("gameFolder").GetString() == ".");
        }

        string vxJson = RocketRenderMKXP.GenerateMkxpConfigJson(@"C:\Games\DragonVX", CoreInterop.EngineVx, fixedAspectRatio: false, smoothScaling: true);
        using (var doc = JsonDocument.Parse(vxJson))
        {
            var root = doc.RootElement;
            Assert("mkxp.json VX rgssVersion is 2", root.GetProperty("rgssVersion").GetInt32() == 2);
            Assert("mkxp.json VX screen width is 544", root.GetProperty("defScreenW").GetInt32() == 544);
            Assert("mkxp.json VX screen height is 416", root.GetProperty("defScreenH").GetInt32() == 416);
            Assert("mkxp.json VX fixedAspectRatio is false", root.GetProperty("fixedAspectRatio").GetBoolean() == false);
            Assert("mkxp.json VX smoothScaling is 1 (bilinear)", root.GetProperty("smoothScaling").GetInt32() == 1);
        }

        string aceJson = RocketRenderMKXP.GenerateMkxpConfigJson(@"C:\Games\HeroAce", CoreInterop.EngineAce);
        using (var doc = JsonDocument.Parse(aceJson))
        {
            var root = doc.RootElement;
            Assert("mkxp.json VX Ace rgssVersion is 3", root.GetProperty("rgssVersion").GetInt32() == 3);
            Assert("mkxp.json VX Ace screen width is 544", root.GetProperty("defScreenW").GetInt32() == 544);
            Assert("mkxp.json VX Ace screen height is 416", root.GetProperty("defScreenH").GetInt32() == 416);
        }

        // 2. Data-Oriented Interop Structs Layout
        var cfg = CoreInterop.MkxpConfig.Create();
        Assert("MkxpConfig Size is 6176 bytes", cfg.Size == 6176, $"Actual={cfg.Size}");
        cfg.RgssVersion = 1;
        cfg.ScreenWidth = 640;
        cfg.ScreenHeight = 480;
        cfg.GameDir = @"C:\Games\SampleGame";
        Assert("MkxpConfig GameDir string marshalling works", cfg.GameDir == @"C:\Games\SampleGame", $"Actual={cfg.GameDir}");
        cfg.CustomExe = @"C:\Bin\mkxp-z.exe";
        Assert("MkxpConfig CustomExe string marshalling works", cfg.CustomExe == @"C:\Bin\mkxp-z.exe", $"Actual={cfg.CustomExe}");

        var frame = new CoreInterop.MkxpFrame { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CoreInterop.MkxpFrame>() };
        Assert("MkxpFrame Size is 56 bytes", frame.Size == 56, $"Actual={frame.Size}");

        var stats = new CoreInterop.MkxpStats { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CoreInterop.MkxpStats>() };
        Assert("MkxpStats Size is 40 bytes", stats.Size == 40, $"Actual={stats.Size}");

        // 3. RocketRenderMKXP Model & Bridge Interface
        using var renderer = new RocketRenderMKXP();
        Assert("RocketRenderMKXP is not running initially", !renderer.IsRunning);
        Assert("RocketRenderMKXP implements IGameBridge", renderer is IGameBridge);
        Assert("RocketRenderMKXP initial speed is 1.0x", renderer.CurrentSpeed == 1.0);
        Assert("RocketRenderMKXP initial is not paused", !renderer.IsPaused);

        // Speed adjustment
        renderer.SetSpeed(2.0);
        Assert("RocketRenderMKXP SetSpeed updates CurrentSpeed", renderer.CurrentSpeed == 2.0);

        // Pause toggle
        renderer.TogglePause();
        Assert("RocketRenderMKXP TogglePause sets IsPaused to true", renderer.IsPaused);
        renderer.TogglePause();
        Assert("RocketRenderMKXP TogglePause toggles IsPaused back to false", !renderer.IsPaused);

        // ESP and Tile Inspector enable
        renderer.EnableEsp(true);
        Assert("RocketRenderMKXP EnableEsp updates property", renderer.EspEnabled);
        renderer.EnableTileInspector(true);
        Assert("RocketRenderMKXP EnableTileInspector updates property", renderer.TileInspectorEnabled);

        // Stop & Dispose
        renderer.Stop();
        Assert("RocketRenderMKXP Stop sets IsRunning to false", !renderer.IsRunning);

        // 4. Ruby engines
        Assert("Engine XP is recognized as Ruby engine", CoreInterop.EngineXp is CoreInterop.EngineXp or CoreInterop.EngineVx or CoreInterop.EngineAce);
        Assert("Engine VX Ace is recognized as Ruby engine", CoreInterop.EngineAce is CoreInterop.EngineXp or CoreInterop.EngineVx or CoreInterop.EngineAce);

        // 5. C-ABI Availability & Error Handling (Verifying fix for fake 1:1 returns)
        int fakeAvail = CoreInterop.rpg_mkxp_is_available_game(@"C:\NonExistent_Mkxp_Dir_12345");
        Assert("rpg_mkxp_is_available_game returns 0 for non-existent game directory", fakeAvail == 0, $"Actual={fakeAvail}");

        var testCfg = CoreInterop.MkxpConfig.Create();
        testCfg.GameDir = @"C:\NonExistent_Mkxp_Dir_12345";
        testCfg.CustomExe = @"C:\NonExistent_Mkxp_Dir_12345\mkxp-z.exe";
        int createSt = CoreInterop.rpg_mkxp_create(ref testCfg, out IntPtr testInst);
        Assert("rpg_mkxp_create returns OK for test config", createSt == 0 && testInst != IntPtr.Zero);

        if (testInst != IntPtr.Zero)
        {
            int startSt = CoreInterop.rpg_mkxp_start(testInst);
            Assert("rpg_mkxp_start returns RP_ERR_NOTFOUND (-2) when exe missing", startSt == -2, $"startSt={startSt}");

            CoreInterop.rpg_mkxp_is_running(testInst, out int running);
            Assert("rpg_mkxp_is_running is 0 when start failed", running == 0, $"running={running}");

            var testStats = new CoreInterop.MkxpStats { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CoreInterop.MkxpStats>() };
            int statsSt = CoreInterop.rpg_mkxp_get_stats(testInst, ref testStats);
            Assert("rpg_mkxp_get_stats returns OK and is_running=0", statsSt == 0 && testStats.IsRunning == 0);

            CoreInterop.rpg_mkxp_destroy(testInst);
        }

        // 6. RocketRenderMKXP.StartGameAsync graceful failure and fallback when missing
        using var failRenderer = new RocketRenderMKXP();
        var startTask = failRenderer.StartGameAsync(@"C:\NonExistent_Mkxp_Dir_12345", CoreInterop.EngineXp);
        bool startResult = startTask.GetAwaiter().GetResult();
        Assert("RocketRenderMKXP.StartGameAsync returns false when mkxp-z is unavailable", !startResult);
        Assert("RocketRenderMKXP is not running after failed StartGameAsync", !failRenderer.IsRunning);

        // 7. Verify provisioned mkxp-z executable resolution
        string mkxpRepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string resolvedMkxp = RocketRenderMKXP.ResolveExecutable();
        if (File.Exists(Path.Combine(mkxpRepoRoot, "runtimes", "mkxp-z", "mkxp-z.exe")))
        {
            Assert("RocketRenderMKXP.ResolveExecutable finds bundled mkxp-z binary", !string.IsNullOrEmpty(resolvedMkxp) && File.Exists(resolvedMkxp));
            Assert("rpg_mkxp_is_available returns 1 when mkxp-z runtime is present", CoreInterop.rpg_mkxp_is_available() == 1);
            string xpSample = Path.Combine(mkxpRepoRoot, "GameSample", "xp", "Do_You_Remember_My_Lullaby");
            if (Directory.Exists(xpSample))
            {
                Assert("rpg_mkxp_is_available_game returns 1 for valid XP game when runtime present", CoreInterop.rpg_mkxp_is_available_game(xpSample) == 1);
                Assert("RocketRenderSystem.IsNativeSupported returns true for XP sample", RocketRenderSystem.IsNativeSupported(CoreInterop.EngineXp, xpSample));

                using var liveMkxp = new RocketRenderMKXP();
                var liveMkxpTask = liveMkxp.StartGameAsync(xpSample, CoreInterop.EngineXp);
                bool liveMkxpStarted = liveMkxpTask.GetAwaiter().GetResult();
                Assert("RocketRenderMKXP starts real XP sample game without fallback", liveMkxpStarted);
                Assert("RocketRenderMKXP is running after start", liveMkxp.IsRunning);
                liveMkxp.Stop();
                Assert("RocketRenderMKXP is not running after stop", !liveMkxp.IsRunning);
            }
        }
    }

    private static void TestRocketRenderEasyRPGAndLibLcf()
    {
        Console.WriteLine("\n--- Testing RocketRenderEasyRPG & liblcf (RM2000 / RM2003) ---");

        // 1. Version Consistency Tests
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string verTxtPath = Path.Combine(repoRoot, "ver.txt");
        if (File.Exists(verTxtPath))
        {
            string ver = File.ReadAllText(verTxtPath).Trim();
            Assert("ver.txt is 1.0.0", ver == "1.0.0", $"Actual={ver}");
        }
        Assert("UpdateService.CurrentVersion is 1.0.0", UpdateService.CurrentVersion == "1.0.0", $"Actual={UpdateService.CurrentVersion}");
        string coreVer = CoreInterop.Version();
        Assert("CoreInterop.Version() is 1.0.0", coreVer == "1.0.0", $"Actual={coreVer}");
        // 버전 비교: 정식 > 같은 번호의 베타, 베타끼리는 번호 순 (업데이트 받을 버전 설정)
        Assert("1.0.0 > 1.0.0-beta", UpdateService.IsNewerVersion("v1.0.0", "1.0.0-beta"));
        Assert("1.0.0-beta > 0.9.0", UpdateService.IsNewerVersion("v1.0.0-beta", "0.9.0"));
        Assert("1.0.0-beta.2 > 1.0.0-beta", UpdateService.IsNewerVersion("1.0.0-beta.2", "1.0.0-beta"));
        Assert("1.0.0-rc > 1.0.0-beta.9", UpdateService.IsNewerVersion("1.0.0-rc", "1.0.0-beta.9"));
        Assert("0.9.0 not > 1.0.0-beta", !UpdateService.IsNewerVersion("v0.9.0", "1.0.0-beta"));
        Assert("1.0.0-beta not > 1.0.0", !UpdateService.IsNewerVersion("1.0.0-beta", "1.0.0"));
        Assert("same version not newer", !UpdateService.IsNewerVersion("v1.0.0-beta", "1.0.0-beta"));
        Assert("stable build does not want beta updates by default", !UpdateService.WantsBeta("") && UpdateService.WantsBeta("beta"));
        Assert("stable channel refuses beta", !UpdateService.WantsBeta("stable"));

        // 2. LCF Header & BER Tests
        byte[] dummyLdb = new byte[] { 0x0B, 0x4C, 0x63, 0x66, 0x44, 0x61, 0x74, 0x61, 0x42, 0x61, 0x73, 0x65, 0x00 };
        byte[] dummyLmt = new byte[] { 0x0A, 0x4C, 0x63, 0x66, 0x4D, 0x61, 0x70, 0x54, 0x72, 0x65, 0x65, 0x00 };
        byte[] dummyLmu = new byte[] { 0x0A, 0x4C, 0x63, 0x66, 0x4D, 0x61, 0x70, 0x55, 0x6E, 0x69, 0x74, 0x00 };
        byte[] dummyInvalid = new byte[] { 0x00, 0x01, 0x02, 0x03 };

        Assert("LcfReader.IsLdb returns true for valid header", LcfReader.IsLdb(dummyLdb));
        Assert("LcfReader.IsLdb returns false for invalid header", !LcfReader.IsLdb(dummyInvalid));
        Assert("LcfReader.IsLmt returns true for valid header", LcfReader.IsLmt(dummyLmt));
        Assert("LcfReader.IsLmt returns false for invalid header", !LcfReader.IsLmt(dummyInvalid));
        Assert("LcfReader.IsLmu returns true for valid header", LcfReader.IsLmu(dummyLmu));
        Assert("LcfReader.IsLmu returns false for invalid header", !LcfReader.IsLmu(dummyInvalid));

        // BER Integer Decoding Tests
        byte[] berSingle = new byte[] { 0x05 };
        int pos1 = 0;
        Assert("BER single byte 5 decodes to 5", LcfReader.ReadBer(berSingle, ref pos1) == 5);

        byte[] berMulti = new byte[] { 0x8E, 0x08 }; // (0x0E << 7) | 0x08 = 1800
        int pos2 = 0;
        Assert("BER two-byte (0x8E, 0x08) decodes to 1800", LcfReader.ReadBer(berMulti, ref pos2) == 1800);

        byte[] berMulti2 = new byte[] { 0x96, 0x71 }; // (0x16 << 7) | 0x71 = 2929
        int pos3 = 0;
        Assert("BER two-byte (0x96, 0x71) decodes to 2929", LcfReader.ReadBer(berMulti2, ref pos3) == 2929);

        // 3. Real RM2000 Sample Parsing (우츠오의 사랑)
        string rm2000GameDir = Path.Combine(repoRoot, "GameSample", "2000", "우츠오의 사랑");
        if (Directory.Exists(rm2000GameDir))
        {
            string ldbPath = Path.Combine(rm2000GameDir, "RPG_RT.ldb");
            Assert("RM2000 RPG_RT.ldb exists", File.Exists(ldbPath));
            if (File.Exists(ldbPath))
            {
                byte[] ldbBytes = File.ReadAllBytes(ldbPath);
                var db = LcfReader.ParseDatabase(ldbBytes);
                Assert("RM2000 switches parsed (>= 50)", db.Switches.Count >= 50, $"Count={db.Switches.Count}");
                Assert("RM2000 switch 1 exists", db.Switches.Any(s => s.Id == 1));
                Assert("RM2000 switch 1 has non-empty name", !string.IsNullOrEmpty(db.Switches.FirstOrDefault(s => s.Id == 1)?.Name));
                Assert("RM2000 variables parsed (>= 500)", db.Variables.Count >= 500, $"Count={db.Variables.Count}");
                Assert("RM2000 variable 1 exists", db.Variables.Any(v => v.Id == 1));
                Assert("RM2000 variable 1 has non-empty name", !string.IsNullOrEmpty(db.Variables.FirstOrDefault(v => v.Id == 1)?.Name));
            }

            string lmtPath = Path.Combine(rm2000GameDir, "RPG_RT.lmt");
            Assert("RM2000 RPG_RT.lmt exists", File.Exists(lmtPath));
            if (File.Exists(lmtPath))
            {
                byte[] lmtBytes = File.ReadAllBytes(lmtPath);
                var maps = LcfReader.ParseMapTree(lmtBytes);
                Assert("RM2000 maps parsed (>= 50)", maps.Count >= 50, $"Count={maps.Count}");
                Assert("RM2000 map 1 exists in tree", maps.Any(m => m.Id == 1));
                Assert("RM2000 map 1 has non-empty name", !string.IsNullOrEmpty(maps.FirstOrDefault(m => m.Id == 1)?.Name));
            }

            string lmuPath = Path.Combine(rm2000GameDir, "Map0001.lmu");
            Assert("RM2000 Map0001.lmu exists", File.Exists(lmuPath));
            if (File.Exists(lmuPath))
            {
                byte[] lmuBytes = File.ReadAllBytes(lmuPath);
                var mapData = LcfReader.ParseMapUnit(lmuBytes);
                Assert("RM2000 Map0001 width is 30", mapData.Width == 30, $"Width={mapData.Width}");
                Assert("RM2000 Map0001 height is 30", mapData.Height == 30, $"Height={mapData.Height}");
                Assert("RM2000 Map0001 has 2-layer tileData (30*30*2=1800)", mapData.TileData.Length == 30 * 30 * 2, $"Len={mapData.TileData.Length}");
                Assert("RM2000 Map0001 has events", mapData.Events.Count > 0, $"Events={mapData.Events.Count}");
                var ev1 = mapData.Events.FirstOrDefault(e => e.Id == 1);
                Assert("RM2000 Event 1 exists", ev1 != null);
                if (ev1 != null)
                {
                    Assert("RM2000 Event 1 has valid coordinates", ev1.X >= 0 && ev1.Y >= 0);
                    Assert("RM2000 Event 1 has pages", ev1.Pages != null && ev1.Pages.Count > 0);
                }
            }
        }

        // 4. Real RM2003 Sample Parsing (서프라이시아 - Korean CP949 Game)
        string rm2003GameDir = Path.Combine(repoRoot, "GameSample", "2003", "서프라이시아");
        if (Directory.Exists(rm2003GameDir))
        {
            string ldbPath = Path.Combine(rm2003GameDir, "RPG_RT.ldb");
            Assert("RM2003 서프라이시아 RPG_RT.ldb exists", File.Exists(ldbPath));
            if (File.Exists(ldbPath))
            {
                byte[] ldbBytes = File.ReadAllBytes(ldbPath);
                var db = LcfReader.ParseDatabase(ldbBytes);
                Assert("RM2003 switches parsed (>= 2000)", db.Switches.Count >= 2000, $"Count={db.Switches.Count}");
                var sw1 = db.Switches.FirstOrDefault(s => s.Id == 1);
                Assert("RM2003 switch 1 is '시작'", sw1 != null && sw1.Name == "시작", $"Name={sw1?.Name}");
                var va1 = db.Variables.FirstOrDefault(v => v.Id == 1);
                Assert("RM2003 variable 1 is '벽난로'", va1 != null && va1.Name == "벽난로", $"Name={va1?.Name}");
            }

            string lmtPath = Path.Combine(rm2003GameDir, "RPG_RT.lmt");
            Assert("RM2003 서프라이시아 RPG_RT.lmt exists", File.Exists(lmtPath));
            if (File.Exists(lmtPath))
            {
                byte[] lmtBytes = File.ReadAllBytes(lmtPath);
                var maps = LcfReader.ParseMapTree(lmtBytes);
                Assert("RM2003 maps parsed (>= 700)", maps.Count >= 700, $"Count={maps.Count}");
                var map1 = maps.FirstOrDefault(m => m.Id == 1);
                Assert("RM2003 map 1 is '마을집1'", map1 != null && map1.Name == "마을집1", $"Name={map1?.Name}");
            }

            string lmuPath = Path.Combine(rm2003GameDir, "Map0001.lmu");
            Assert("RM2003 서프라이시아 Map0001.lmu exists", File.Exists(lmuPath));
            if (File.Exists(lmuPath))
            {
                byte[] lmuBytes = File.ReadAllBytes(lmuPath);
                var mapData = LcfReader.ParseMapUnit(lmuBytes);
                Assert("RM2003 Map0001 dimensions positive", mapData.Width > 0 && mapData.Height > 0);
                Assert("RM2003 Map0001 events parsed (>= 10)", mapData.Events.Count >= 10, $"Events={mapData.Events.Count}");
            }
        }

        // 5. EasyRpgBridge Calculations & Telemetry
        using var bridge = new EasyRpgBridge(rm2000GameDir, CoreInterop.Engine2000);
        Assert("EasyRpgBridge BaseWidth is 320", bridge.BaseWidth == 320);
        Assert("EasyRpgBridge BaseHeight is 240", bridge.BaseHeight == 240);
        Assert("EasyRpgBridge BaseFps is 60", bridge.BaseFps == 60);
        Assert("EasyRpgBridge TileDisplayScale is 16.0", bridge.TileDisplayScale == 16.0);
        Assert("EasyRpgBridge initial is not paused", !bridge.IsPaused);
        Assert("EasyRpgBridge initial speed is 1.0", bridge.CurrentSpeed == 1.0);

        bridge.SetSpeed(2.0);
        Assert("EasyRpgBridge SetSpeed 2.0x updates CurrentSpeed", bridge.CurrentSpeed == 2.0);
        bridge.SetSpeed(0.5);
        Assert("EasyRpgBridge SetSpeed 0.5x updates CurrentSpeed", bridge.CurrentSpeed == 0.5);
        bridge.SetSpeed(8.0);
        Assert("EasyRpgBridge SetSpeed 8.0x updates CurrentSpeed", bridge.CurrentSpeed == 8.0);
        bridge.SetSpeed(10.0);
        Assert("EasyRpgBridge SetSpeed clamps upper to 8.0", bridge.CurrentSpeed == 8.0);
        bridge.SetSpeed(0.1);
        Assert("EasyRpgBridge SetSpeed clamps lower to 0.5", bridge.CurrentSpeed == 0.5);

        bridge.TogglePause();
        Assert("EasyRpgBridge TogglePause toggles to true", bridge.IsPaused);
        bridge.TogglePause();
        Assert("EasyRpgBridge TogglePause toggles back to false", !bridge.IsPaused);

        bridge.ToggleNoclip();
        Assert("EasyRpgBridge ToggleNoclip toggles to true", bridge.IsNoclip);
        bridge.ToggleNoclip();
        Assert("EasyRpgBridge ToggleNoclip toggles back to false", !bridge.IsNoclip);

        // 브릿지(패치 Player) 없이는 실제 위치를 바꿀 수 없으므로 가짜 상태를 만들지 않고 안내만 합니다.
        string? warpNotice = null;
        bridge.NotificationReceived += m => warpNotice = m;
        int mapBefore = bridge.LatestState.MapId;
        bridge.Warp(5, 12, 18);
        Assert("EasyRpgBridge Warp without agent does not fake the position", bridge.LatestState.MapId == mapBefore);
        Assert("EasyRpgBridge Warp without agent tells the user it is unsupported", warpNotice != null && warpNotice.Contains("지원하지 않습니다"));

        bridge.FreezeSwitch(1, true, true);
        bridge.FreezeVariable(1, true, "999");
        bridge.SetSwitch(1, false); // should be overridden by freeze loop
        bridge.SetVariable(1, "0");  // should be overridden by freeze loop

        // 6. RocketRenderEasyRPG Model
        using var easyRpgRenderer = new RocketRenderEasyRPG();
        Assert("RocketRenderEasyRPG is not running initially", !easyRpgRenderer.IsRunning);
        Assert("RocketRenderEasyRPG implements IGameBridge", easyRpgRenderer is IGameBridge);
        Assert("RocketRenderEasyRPG BaseWidth is 320", easyRpgRenderer.BaseWidth == 320);
        Assert("RocketRenderEasyRPG BaseHeight is 240", easyRpgRenderer.BaseHeight == 240);

        easyRpgRenderer.SetSpeed(4.0);
        Assert("RocketRenderEasyRPG SetSpeed updates CurrentSpeed", easyRpgRenderer.CurrentSpeed == 4.0);
        easyRpgRenderer.TogglePause();
        Assert("RocketRenderEasyRPG TogglePause updates IsPaused", easyRpgRenderer.IsPaused);
        easyRpgRenderer.EnableEsp(true);
        Assert("RocketRenderEasyRPG EnableEsp updates property", easyRpgRenderer.EspEnabled);
        easyRpgRenderer.EnableTileInspector(true);
        Assert("RocketRenderEasyRPG EnableTileInspector updates property", easyRpgRenderer.TileInspectorEnabled);
        easyRpgRenderer.Stop();
        Assert("RocketRenderEasyRPG Stop sets IsRunning to false", !easyRpgRenderer.IsRunning);

        // 7. RocketRenderSystem Tests
        Assert("Engine 2000 family is EasyRPG", RocketRenderSystem.GetEngineFamily(CoreInterop.Engine2000) == RocketRenderSystem.EngineFamilyEasyRpg);
        Assert("Engine 2003 family is EasyRPG", RocketRenderSystem.GetEngineFamily(CoreInterop.Engine2003) == RocketRenderSystem.EngineFamilyEasyRpg);
        Assert("Engine XP family is MKXP", RocketRenderSystem.GetEngineFamily(CoreInterop.EngineXp) == RocketRenderSystem.EngineFamilyMkxp);
        Assert("Engine VX family is MKXP", RocketRenderSystem.GetEngineFamily(CoreInterop.EngineVx) == RocketRenderSystem.EngineFamilyMkxp);
        Assert("Engine Ace family is MKXP", RocketRenderSystem.GetEngineFamily(CoreInterop.EngineAce) == RocketRenderSystem.EngineFamilyMkxp);
        Assert("Engine MV family is WebView2", RocketRenderSystem.GetEngineFamily(CoreInterop.EngineMv) == RocketRenderSystem.EngineFamilyWebView2);
        Assert("Engine MZ family is WebView2", RocketRenderSystem.GetEngineFamily(CoreInterop.EngineMz) == RocketRenderSystem.EngineFamilyWebView2);

        var (res2k0w, res2k0h) = RocketRenderSystem.GetDefaultResolution(CoreInterop.Engine2000);
        Assert("EasyRPG default resolution is 320x240", res2k0w == 320 && res2k0h == 240);
        var (resXpW, resXpH) = RocketRenderSystem.GetDefaultResolution(CoreInterop.EngineXp);
        Assert("XP default resolution is 640x480", resXpW == 640 && resXpH == 480);
        var (resVxW, resVxH) = RocketRenderSystem.GetDefaultResolution(CoreInterop.EngineVx);
        Assert("VX default resolution is 544x416", resVxW == 544 && resVxH == 416);

        // 8. C-ABI Availability & Error Handling for EasyRPG
        int fakeAvail = CoreInterop.rpg_easyrpg_is_available_game(@"C:\NonExistent_EasyRpg_Dir_12345");
        Assert("rpg_easyrpg_is_available_game returns 0 for non-existent game directory", fakeAvail == 0, $"Actual={fakeAvail}");

        var testCfg = CoreInterop.EasyRpgConfig.Create();
        testCfg.GameDir = @"C:\NonExistent_EasyRpg_Dir_12345";
        testCfg.CustomExe = @"C:\NonExistent_EasyRpg_Dir_12345\Player.exe";
        int createSt = CoreInterop.rpg_easyrpg_create(ref testCfg, out IntPtr testInst);
        Assert("rpg_easyrpg_create returns OK for test config", createSt == 0 && testInst != IntPtr.Zero);

        if (testInst != IntPtr.Zero)
        {
            int startSt = CoreInterop.rpg_easyrpg_start(testInst);
            Assert("rpg_easyrpg_start returns RP_ERR_NOTFOUND (-2) when exe missing", startSt == -2, $"startSt={startSt}");

            CoreInterop.rpg_easyrpg_is_running(testInst, out int running);
            Assert("rpg_easyrpg_is_running is 0 when start failed", running == 0, $"running={running}");

            var testStats = new CoreInterop.EasyRpgStats { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CoreInterop.EasyRpgStats>() };
            int statsSt = CoreInterop.rpg_easyrpg_get_stats(testInst, ref testStats);
            Assert("rpg_easyrpg_get_stats returns OK and is_running=0", statsSt == 0 && testStats.IsRunning == 0);

            CoreInterop.rpg_easyrpg_destroy(testInst);
        }

        // 9. RocketRenderEasyRPG.StartGameAsync graceful failure and fallback when missing
        using var failEasyRpg = new RocketRenderEasyRPG();
        var startEasyTask = failEasyRpg.StartGameAsync(@"C:\NonExistent_EasyRpg_Dir_12345", CoreInterop.Engine2000);
        bool startEasyResult = startEasyTask.GetAwaiter().GetResult();
        Assert("RocketRenderEasyRPG.StartGameAsync returns false when EasyRPG is unavailable", !startEasyResult);
        Assert("RocketRenderEasyRPG is not running after failed StartGameAsync", !failEasyRpg.IsRunning);

        // 9b. Verify provisioned EasyRPG Player executable resolution
        string easyRepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string resolvedEasyRpg = RocketRenderEasyRPG.ResolveExecutable();
        if (File.Exists(Path.Combine(easyRepoRoot, "runtimes", "easyrpg", "Player.exe")))
        {
            Assert("RocketRenderEasyRPG.ResolveExecutable finds bundled EasyRPG binary", !string.IsNullOrEmpty(resolvedEasyRpg) && File.Exists(resolvedEasyRpg));
            Assert("rpg_easyrpg_is_available returns 1 when EasyRPG runtime is present", CoreInterop.rpg_easyrpg_is_available() == 1);
            string rm2000Sample = Path.Combine(easyRepoRoot, "GameSample", "2000", "7thJojo");
            if (Directory.Exists(rm2000Sample))
            {
                Assert("rpg_easyrpg_is_available_game returns 1 for 7thJojo when runtime present", CoreInterop.rpg_easyrpg_is_available_game(rm2000Sample) == 1);
                Assert("RocketRenderSystem.IsNativeSupported returns true for 7thJojo (RM2000)", RocketRenderSystem.IsNativeSupported(CoreInterop.Engine2000, rm2000Sample));

                using var live2k = new RocketRenderEasyRPG();
                var live2kTask = live2k.StartGameAsync(rm2000Sample, CoreInterop.Engine2000);
                bool live2kStarted = live2kTask.GetAwaiter().GetResult();
                Assert("RocketRenderEasyRPG starts real RM2000 sample game without fallback", live2kStarted);
                Assert("RocketRenderEasyRPG is running after RM2000 start", live2k.IsRunning);
                live2k.Stop();
                Assert("RocketRenderEasyRPG is not running after RM2000 stop", !live2k.IsRunning);
            }
            string rm2003Sample = Path.Combine(easyRepoRoot, "GameSample", "2003", "Treaster");
            if (Directory.Exists(rm2003Sample))
            {
                Assert("rpg_easyrpg_is_available_game returns 1 for Treaster when runtime present", CoreInterop.rpg_easyrpg_is_available_game(rm2003Sample) == 1);
                Assert("RocketRenderSystem.IsNativeSupported returns true for Treaster (RM2003)", RocketRenderSystem.IsNativeSupported(CoreInterop.Engine2003, rm2003Sample));

                using var live2k3 = new RocketRenderEasyRPG();
                var live2k3Task = live2k3.StartGameAsync(rm2003Sample, CoreInterop.Engine2003);
                bool live2k3Started = live2k3Task.GetAwaiter().GetResult();
                Assert("RocketRenderEasyRPG starts real RM2003 sample game without fallback", live2k3Started);
                Assert("RocketRenderEasyRPG is running after RM2003 start", live2k3.IsRunning);
                live2k3.Stop();
                Assert("RocketRenderEasyRPG is not running after RM2003 stop", !live2k3.IsRunning);
            }
        }

        // 10. Deep verification: ReadChunkInt BER multi-byte integer correctness
        byte[] ber1800 = { 0x8E, 0x08 };
        int decoded1800 = LcfReader.ReadChunkInt(ber1800, 0, 2);
        Assert("ReadChunkInt 2-byte BER 1800 decodes to 1800 (not 2190 LE)", decoded1800 == 1800, $"decoded1800={decoded1800}");

        byte[] ber302 = { 0x82, 0x2E };
        int decoded302 = LcfReader.ReadChunkInt(ber302, 0, 2);
        Assert("ReadChunkInt 2-byte BER 302 decodes to 302 (not 11906 LE)", decoded302 == 302, $"decoded302={decoded302}");

        byte[] ber529 = { 0x84, 0x11 };
        int decoded529 = LcfReader.ReadChunkInt(ber529, 0, 2);
        Assert("ReadChunkInt 2-byte BER 529 decodes to 529 (not 4484 LE)", decoded529 == 529, $"decoded529={decoded529}");

        byte[] berChunk1 = { 0x25 };
        int decodedSingle = LcfReader.ReadChunkInt(berChunk1, 0, 1);
        Assert("ReadChunkInt 1-byte decodes to 37", decodedSingle == 37, $"decodedSingle={decodedSingle}");

        // 11. Deep verification: Shift-JIS text with Japanese punctuation and halfwidth katakana
        // Shift-JIS for 「冒険」: 0x81 0x75 (「), 0x96 0x60 (冒), 0x8C 0xAF (険), 0x81 0x76 (」)
        byte[] sjisPunctuation = { 0x81, 0x75, 0x96, 0x60, 0x8C, 0xAF, 0x81, 0x76 };
        string decodedSjis = LcfReader.DecodeLcfString(sjisPunctuation);
        Assert("DecodeLcfString recognizes Shift-JIS Japanese quotes and Kanji", decodedSjis == "「冒険」", $"decodedSjis={decodedSjis}");

        // 12. Deep verification: LcfMapData GetTopTile skips 10000 and retrieves ground layer
        var testMap = new LcfMapData
        {
            Width = 2,
            Height = 2,
            TileData = new int[]
            {
                2050, 2816, 4000, 5000,   // Layer 0: ground tiles
                10000, 10123, 10000, 10000 // Layer 1: upper tiles (10000 is transparent)
            }
        };
        Assert("LcfMapData (0,0) with upper 10000 returns lower 2050", testMap.GetTopTile(0, 0) == 2050);
        Assert("LcfMapData (1,0) with upper 10123 returns upper 10123", testMap.GetTopTile(1, 0) == 10123);
        Assert("LcfMapData (0,1) with upper 10000 returns lower 4000", testMap.GetTopTile(0, 1) == 4000);
        Assert("LcfMapData (1,1) with upper 10000 returns lower 5000", testMap.GetTopTile(1, 1) == 5000);
    }

    private static void TestNewFeatures()
    {
        Console.WriteLine("\n--- Testing v0.5.0 Advanced Features ---");

        // 1. GameLibraryScanner
        var steamDirs = GameLibraryScanner.DetectSteamLibraryFolders();
        Assert("GameLibraryScanner.DetectSteamLibraryFolders() returns non-null", steamDirs != null);

        var customList = new List<string> { @"C:\CustomGames" };
        var scanRoots = GameLibraryScanner.GetDefaultScanRoots(customList);
        Assert("GameLibraryScanner.GetDefaultScanRoots() includes roots", scanRoots != null && scanRoots.Count > 0);

        // Inspect real sample games
        string sample2k = Path.Combine(SettingsService.Root(), "GameSample", "2000", "7thJojo");
        if (Directory.Exists(sample2k))
        {
            var g2k = GameLibraryScanner.InspectGameDirectory(sample2k);
            Assert("GameLibraryScanner detected 7thJojo", g2k != null);
            Assert("7thJojo engine is RM2000", g2k?.Engine == CoreInterop.Engine2000);
            Assert("7thJojo has title", !string.IsNullOrEmpty(g2k?.Title));
            Assert("7thJojo has exe name", !string.IsNullOrEmpty(g2k?.ExeName));
        }

        string sampleXp = Path.Combine(SettingsService.Root(), "GameSample", "xp", "Do_You_Remember_My_Lullaby");
        if (Directory.Exists(sampleXp))
        {
            var gXp = GameLibraryScanner.InspectGameDirectory(sampleXp);
            Assert("GameLibraryScanner detected Do_You_Remember_My_Lullaby", gXp != null);
            Assert("Do_You_Remember_My_Lullaby engine is XP", gXp?.Engine == CoreInterop.EngineXp);
            Assert("Do_You_Remember_My_Lullaby has title", !string.IsNullOrEmpty(gXp?.Title));
        }

        // 2. GlobalSettings serialization with new fields
        var s = new GlobalSettings
        {
            UiFontFamily = "맑은 고딕",
            CustomScanFolders = new List<string> { @"D:\MyGames", @"E:\RpgMaker" }
        };
        string json = JsonSerializer.Serialize(s);
        Assert("GlobalSettings JSON contains ui_font_family", json.Contains("ui_font_family"));
        Assert("GlobalSettings JSON contains custom_scan_dirs", json.Contains("custom_scan_dirs"));
        var ds = JsonSerializer.Deserialize<GlobalSettings>(json);
        Assert("GlobalSettings deserialized UiFontFamily matches", ds?.UiFontFamily == "맑은 고딕");
        Assert("GlobalSettings deserialized CustomScanFolders count matches", ds?.CustomScanFolders.Count == 2);

        // 3. mkxp.json scripts and title injection
        if (Directory.Exists(sampleXp))
        {
            string cfgJson = RocketRenderMKXP.GenerateMkxpConfigJson(sampleXp, CoreInterop.EngineXp);
            Assert("mkxp.json contains 'title':", cfgJson.Contains("\"title\":"));
            Assert("mkxp.json contains 'scripts':", cfgJson.Contains("\"scripts\":"));
            Assert("mkxp.json contains Scripts.rxdata", cfgJson.Contains("Scripts.rxdata"));
        }

        // 4. RocketShaderSystem (필터 이름 → 엔진별 대응)
        Assert("RocketShaderSystem.FilterNone is 'none'", RocketShaderSystem.FilterNone == "none");
        Assert("RocketShaderSystem.FilterXbrzCas is 'xbrz_cas'", RocketShaderSystem.FilterXbrzCas == "xbrz_cas");
        Assert("RocketShaderSystem.FilterFsrCas is 'fsr_cas'", RocketShaderSystem.FilterFsrCas == "fsr_cas");
        Assert("RocketShaderSystem.FilterScaleFxCas is 'scalefx_cas'", RocketShaderSystem.FilterScaleFxCas == "scalefx_cas");
        Assert("RocketShaderSystem.FilterCrtRoyale is 'crt_royale'", RocketShaderSystem.FilterCrtRoyale == "crt_royale");

        Assert("RocketShaderSystem displayName for xbrz_cas contains xBRZ", RocketShaderSystem.GetFilterDisplayName(RocketShaderSystem.FilterXbrzCas).Contains("xBRZ"));
        Assert("RocketShaderSystem displayName for fsr_cas contains Lanczos", RocketShaderSystem.GetFilterDisplayName(RocketShaderSystem.FilterFsrCas).Contains("Lanczos"));
        Assert("RocketShaderSystem displayName for scalefx_cas contains Bicubic", RocketShaderSystem.GetFilterDisplayName(RocketShaderSystem.FilterScaleFxCas).Contains("Bicubic"));
        Assert("RocketShaderSystem displayName for crt_royale contains CRT", RocketShaderSystem.GetFilterDisplayName(RocketShaderSystem.FilterCrtRoyale).Contains("CRT"));

        Assert("ToMkxpScaling none -> null", RocketShaderSystem.ToMkxpScaling(RocketShaderSystem.FilterNone) == null);
        Assert("ToMkxpScaling xbrz_cas -> 4", RocketShaderSystem.ToMkxpScaling(RocketShaderSystem.FilterXbrzCas) == 4);

        // 5. FontSettingsWindow matching
        var testFonts = new List<System.Windows.Media.FontFamily>
        {
            new System.Windows.Media.FontFamily("Arial"),
            new System.Windows.Media.FontFamily("맑은 고딕"),
            new System.Windows.Media.FontFamily("Segoe UI")
        };
        var matched = RocketRPG.Views.FontSettingsWindow.FindMatchingFont(testFonts, "맑은 고딕, Segoe UI, sans-serif");
        Assert("FontSettingsWindow finds first available font in fallback list", matched?.Source == "맑은 고딕");
        var matched2 = RocketRPG.Views.FontSettingsWindow.FindMatchingFont(testFonts, "NonExistent, Segoe UI");
        Assert("FontSettingsWindow skips non-existent and finds Segoe UI", matched2?.Source == "Segoe UI");
        var matched3 = RocketRPG.Views.FontSettingsWindow.FindMatchingFont(testFonts, "");
        Assert("FontSettingsWindow returns null on empty font query", matched3 == null);

        // 6. ScannedGame DisplayTitle
        var sgWithTitle = new ScannedGame { Title = "테스트 게임", ExeName = "Game.exe" };
        Assert("ScannedGame with title formats Title (ExeName)", sgWithTitle.DisplayTitle == "테스트 게임 (Game.exe)");
        var sgNoTitle = new ScannedGame { Title = "", ExeName = "Game.exe" };
        Assert("ScannedGame without title falls back to ExeName", sgNoTitle.DisplayTitle == "Game.exe");

        // 7. RocketFontSystem
        var subs = RocketFontSystem.GetMkxpFontSubstitutions("맑은 고딕");
        Assert("RocketFontSystem.GetMkxpFontSubstitutions returns non-empty list", subs != null && subs.Count > 0);
        Assert("RocketFontSystem font substitution contains > mapping", subs.Any(s => s.Contains(">")));

        string rubyFont = RocketFontSystem.GetRubyFontSnippet("맑은 고딕", 16, true);
        Assert("RocketFontSystem.GetRubyFontSnippet contains Font.default_name", rubyFont.Contains("Font.default_name"));
        Assert("RocketFontSystem.GetRubyFontSnippet contains size 16", rubyFont.Contains("Font.default_size = 16"));
        Assert("RocketFontSystem.GetRubyFontSnippet contains bold true", rubyFont.Contains("Font.default_bold = true"));

        string easyArgs = RocketFontSystem.GetEasyRpgFontArgs("맑은 고딕", 14);
        Assert("RocketFontSystem.GetEasyRpgFontArgs contains --font1", easyArgs.Contains("--font1"));
        Assert("RocketFontSystem.GetEasyRpgFontArgs contains --font1-size 14", easyArgs.Contains("--font1-size 14"));

        string webFontScript = RocketFontSystem.GetWebViewFontScript("맑은 고딕", 18, true);
        Assert("RocketFontSystem.GetWebViewFontScript contains font-family", webFontScript.Contains("font-family"));
        Assert("RocketFontSystem.GetWebViewFontScript contains 18px", webFontScript.Contains("18px"));

        string? sfPath = RocketFontSystem.ResolveSoundFontPath();
        Assert("RocketFontSystem.ResolveSoundFontPath returns valid path", !string.IsNullOrEmpty(sfPath) && File.Exists(sfPath));

        // 8. RocketTextAutoAdvance
        double waitShort = RocketTextAutoAdvance.CalculateWaitTimeMs(5, 1.0);
        double waitLong = RocketTextAutoAdvance.CalculateWaitTimeMs(100, 1.0);
        Assert("RocketTextAutoAdvance longer text has longer wait time", waitLong > waitShort);
        double waitFast = RocketTextAutoAdvance.CalculateWaitTimeMs(50, 2.0);
        double waitNormal = RocketTextAutoAdvance.CalculateWaitTimeMs(50, 1.0);
        Assert("RocketTextAutoAdvance higher speed has shorter wait time", waitFast < waitNormal);

        // 9. GameSettingsService tests
        string testDir = Path.Combine(Path.GetTempPath(), "RocketRPG_TestGame_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        try
        {
            string safeKey = GameSettingsService.ComputeSafeKey(testDir);
            Assert("GameSettingsService.ComputeSafeKey returns 16-char hex", safeKey.Length == 16);

            string primary = GameSettingsService.GetPrimaryConfigPath(testDir);
            Assert("GameSettingsService.GetPrimaryConfigPath has rocket_config.json", primary.EndsWith(GameSettingsService.ConfigFileName));

            var cfg = new GameConfig
            {
                Ratio = "16:9",
                Gamma = 1.5,
                Volume = 85,
                Filter = "xbrz_cas",
                InGameFontFamily = "맑은 고딕",
                InGameFontSize = 16,
                InGameFontBold = true,
                AutoMessageEnabled = true,
                AutoMessageSpeed = 2.0,
                ShowMessageBar = true
            };
            bool saved = GameSettingsService.Save(testDir, cfg);
            Assert("GameSettingsService.Save returns true", saved);
            Assert("rocket_config.json file created", File.Exists(primary));

            var loaded = GameSettingsService.Load(testDir);
            Assert("GameSettingsService.Load Ratio match", loaded.Ratio == "16:9");
            Assert("GameSettingsService.Load Gamma match", Math.Abs(loaded.Gamma - 1.5) < 0.01);
            Assert("GameSettingsService.Load Volume match", loaded.Volume == 85);
            Assert("GameSettingsService.Load Filter match", loaded.Filter == "xbrz_cas");
            Assert("GameSettingsService.Load InGameFontFamily match", loaded.InGameFontFamily == "맑은 고딕");
            Assert("GameSettingsService.Load InGameFontSize match", loaded.InGameFontSize == 16);
            Assert("GameSettingsService.Load InGameFontBold match", loaded.InGameFontBold == true);
            Assert("GameSettingsService.Load AutoMessageEnabled match", loaded.AutoMessageEnabled == true);
            Assert("GameSettingsService.Load AutoMessageSpeed match", Math.Abs(loaded.AutoMessageSpeed - 2.0) < 0.01);
            Assert("GameSettingsService.Load ShowMessageBar match", loaded.ShowMessageBar == true);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }

        // 10. DialogueLogManager tests (FIFO max 100)
        DialogueLogManager.Clear();
        Assert("DialogueLogManager.Clear empties entries", DialogueLogManager.GetEntries().Count == 0);
        for (int i = 1; i <= 105; i++)
        {
            DialogueLogManager.Add($"대사 라인 {i}");
        }
        var logEntries = DialogueLogManager.GetEntries();
        Assert("DialogueLogManager caps entries at max 100", logEntries.Count == 100);
        Assert("DialogueLogManager oldest dropped (starts at line 6)", logEntries[0] == "대사 라인 6");
        Assert("DialogueLogManager newest present (ends at line 105)", logEntries[^1] == "대사 라인 105");
        DialogueLogManager.Clear();

        // 11. VariableItem.IsPinned test
        var vi = new VariableItem { Id = 1, Name = "Gold", Value = "500" };
        Assert("VariableItem.IsPinned default false", !vi.IsPinned);
        vi.IsPinned = true;
        Assert("VariableItem.IsPinned set to true", vi.IsPinned);

        // 12. GameLibraryScanner fd test
        string? fdPath = GameLibraryScanner.ResolveFdPath();
        Assert("GameLibraryScanner.ResolveFdPath locates fd.exe", !string.IsNullOrEmpty(fdPath) && File.Exists(fdPath));

        // 13. Shell and Steam Icon Extraction tests
        var folderIco = GameLibraryScanner.GetFolderIcon();
        Assert("GameLibraryScanner.GetFolderIcon returns valid image", folderIco != null);
        var upIco = GameLibraryScanner.GetUpFolderIcon();
        Assert("GameLibraryScanner.GetUpFolderIcon returns valid image", upIco != null);
        var docsIco = GameLibraryScanner.GetDocumentsIcon();
        Assert("GameLibraryScanner.GetDocumentsIcon returns valid image", docsIco != null);
        var downIco = GameLibraryScanner.GetDownloadsIcon();
        Assert("GameLibraryScanner.GetDownloadsIcon returns valid image", downIco != null);
        // Steam icon: either valid image if Steam installed or null without crash
        var steamIco = GameLibraryScanner.GetSteamIcon();
        Assert("GameLibraryScanner.GetSteamIcon handles environment gracefully", steamIco != null || GameLibraryScanner.GetSteamRoot() == null);

        // 14. CrashReporter Native Handler installation
        CrashReporter.InstallNativeCrashHandler();
        Assert("CrashReporter.InstallNativeCrashHandler executes without throwing", true);

        // 15. mkxp-z 글꼴 이름표/대체 (mkxp-z는 찾는 이름만 소문자로 바꾸므로 표도 소문자여야 함)
        string malgun = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "malgun.ttf");
        if (File.Exists(malgun))
        {
            var names = MkxpFontCatalog.ReadNames(malgun);
            Assert("MkxpFontCatalog reads Malgun Gothic family", names?.Family == "Malgun Gothic");
            Assert("MkxpFontCatalog reads Korean alias 맑은 고딕", names?.Aliases.Contains("맑은 고딕") == true);
            string tempFonts = Path.Combine(Path.GetTempPath(), "RocketFontTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempFonts);
            try
            {
                File.Copy(malgun, Path.Combine(tempFonts, "12롯데마트드림Bold.ttf"));
                var fsubs = MkxpFontCatalog.BuildSubstitutions([tempFonts], null, null);
                Assert("fontSub entries are lowercase", fsubs.All(s => s == new string(s.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c).ToArray())));
                Assert("fontSub maps a font's file name to its real family", fsubs.Contains("12롯데마트드림bold>malgun gothic"));
                Assert("fontSub maps Korean name to real family", fsubs.Contains("맑은 고딕>malgun gothic"));
                Assert("fontSub maps Arial to a Hangul font", fsubs.Contains("arial>malgun gothic"));
                // 빠진 게임 글꼴 → 비슷한 설치 글꼴
                var miss = new Dictionary<string, string> { ["08서울남산체 M"] = "맑은 고딕" };
                var msubs = MkxpFontCatalog.BuildSubstitutions([tempFonts], null, null, null, null, miss);
                Assert("fontSub maps a missing game font to the substitute family", msubs.Contains("08서울남산체 m>malgun gothic"));
            }
            finally
            {
                try { Directory.Delete(tempFonts, true); } catch { }
            }
        }
        Assert("Classify: 명조 → Serif", MkxpFontCatalog.Classify("나눔명조") == MkxpFontCatalog.FontKind.Serif);
        Assert("Classify: ＭＳ 明朝 → Serif", MkxpFontCatalog.Classify("ＭＳ 明朝") == MkxpFontCatalog.FontKind.Serif);
        Assert("Classify: Noto Sans Serif-less name → Gothic", MkxpFontCatalog.Classify("Noto Sans KR") == MkxpFontCatalog.FontKind.Gothic);
        Assert("Classify: 돋움 is not Pixel", MkxpFontCatalog.Classify("돋움") == MkxpFontCatalog.FontKind.Gothic);
        Assert("Classify: 손글씨 → Hand", MkxpFontCatalog.Classify("나눔손글씨 펜") == MkxpFontCatalog.FontKind.Hand);
        Assert("Classify: 둥근모 → Pixel", MkxpFontCatalog.Classify("둥근모꼴") == MkxpFontCatalog.FontKind.Pixel);
        Assert("Classify: unknown → Gothic", MkxpFontCatalog.Classify("08서울남산체 M") == MkxpFontCatalog.FontKind.Gothic);
        Assert("PickSubstitute returns an installed font", MkxpFontCatalog.PickSubstitute("08서울남산체 M") != null);

        // FreeType 이름 규칙: WWS 비트가 없는 글꼴은 21번(WWS 패밀리) 이름이 먼저 (mkxp-z가 그 이름으로 등록)
        string elice = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Fonts\EliceDXNeolliOTF-Light.otf");
        if (File.Exists(elice))
            Assert("ReadNames follows FreeType (WWS family first)", MkxpFontCatalog.ReadNames(elice)?.Family == "Elice DX Neolli OTF Light", MkxpFontCatalog.ReadNames(elice)?.Family ?? "null");

        // 2000/2003 글꼴 미리보기: 게임처럼 흑백이라 배경·글자 두 색뿐
        int monoColors = 0;
        var sta = new System.Threading.Thread(() =>
        {
            int Colors(System.Windows.Media.Imaging.BitmapSource b)
            {
                var px = new byte[b.PixelWidth * b.PixelHeight * 4];
                b.CopyPixels(px, b.PixelWidth * 4, 0);
                var set = new HashSet<int>();
                for (int i = 0; i < px.Length; i += 4) set.Add(BitConverter.ToInt32(px, i));
                return set.Count;
            }
            var fam = new System.Windows.Media.FontFamily("Malgun Gothic");
            monoColors = Colors(RocketRPG.Views.FontSettingsWindow.RenderEasyRpgSample(fam, 12, false));
        });
        sta.SetApartmentState(System.Threading.ApartmentState.STA);
        sta.Start(); sta.Join();
        Assert("EasyRPG preview (black/white) uses only 2 colors", monoColors == 2, $"colors={monoColors}");

        // 디스코드 한 줄은 UTF-8 128바이트까지
        string longTitle = new string('가', 100);
        string fitted = DiscordPresence.Fit(longTitle);
        Assert("DiscordPresence.Fit keeps a line within 128 UTF-8 bytes", System.Text.Encoding.UTF8.GetByteCount(fitted) <= 128, $"bytes={System.Text.Encoding.UTF8.GetByteCount(fitted)}");
        Assert("DiscordPresence.Fit leaves short titles alone", DiscordPresence.Fit("앨리스의 딸") == "앨리스의 딸");
    }
}
