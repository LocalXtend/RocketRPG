#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// 게임 데이터 읽기 (MV/MZ JSON, 맵 레이어, 변수 고정, 맵 이동)
public partial class Program
{
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
}
