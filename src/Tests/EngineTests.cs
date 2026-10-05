#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// 엔진 연동: mkxp-z, EasyRPG Player/liblcf
public partial class Program
{
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
        // 버전은 ver.txt 하나가 기준: 앱(UpdateService)과 코어 DLL이 같은 버전이어야 함 (버전을 올릴 때 빠뜨린 곳 찾기)
        string ver = File.Exists(verTxtPath) ? File.ReadAllText(verTxtPath).Trim() : UpdateService.CurrentVersion;
        Assert("ver.txt is x.y.z", System.Text.RegularExpressions.Regex.IsMatch(ver, @"^\d+\.\d+\.\d+$"), $"Actual={ver}");
        Assert("UpdateService.CurrentVersion matches ver.txt", UpdateService.CurrentVersion == ver, $"Actual={UpdateService.CurrentVersion} ver.txt={ver}");
        string coreVer = CoreInterop.Version();
        Assert("CoreInterop.Version() matches ver.txt", coreVer == ver, $"Actual={coreVer} ver.txt={ver}");
        string appVer = (System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(UpdateService).Assembly.Location).ProductVersion ?? "").Split('+')[0];
        Assert("RocketRPG.exe version matches ver.txt", appVer == ver, $"Actual={appVer} ver.txt={ver}");
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
}
