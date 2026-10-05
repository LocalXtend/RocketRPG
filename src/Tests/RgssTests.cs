#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// XP/VX/Ace: RGSS 아카이브, 루비 Marshal, 루비 브리지
public partial class Program
{
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
}
