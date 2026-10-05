#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// 기능·이슈 수정 회귀 테스트 (0.5 ~ 0.6)
public partial class Program
{
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
}
