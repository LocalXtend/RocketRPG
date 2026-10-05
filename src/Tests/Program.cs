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

    /// <summary>
    /// 테스트 목록: (이름, 분야, 실행). 분야로 묶어 골라 돌릴 수 있습니다.
    ///   Phase1Tests.exe               모두
    ///   Phase1Tests.exe multi rgss    이름이나 분야에 'multi' 또는 'rgss'가 들어간 것만 (대소문자 무시)
    ///   Phase1Tests.exe --list        목록만 출력
    /// </summary>
    static readonly (string Name, string Area, Action Run)[] Tests =
    [
        ("HotkeyManager", "settings", TestHotkeyManager),
        ("SettingsService", "settings", TestSettingsService),
        ("SteamLanguage", "settings", TestSteamLanguage),
        ("NoteMarkup", "notes", TestNoteMarkup),
        ("HotkeyMigrationAndStreamer", "settings", TestHotkeyMigrationAndStreamer),
        ("DataContracts", "gamedata", TestDataContracts),
        ("FreezeLogicAndNotifications", "gamedata", TestFreezeLogicAndNotifications),
        ("MapViewerLayerDecoding", "gamedata", TestMapViewerLayerDecoding),
        ("RealGameDataMv", "gamedata", TestRealGameDataMv),
        ("RealGameDataMz", "gamedata", TestRealGameDataMz),
        ("RgssArchiveReader", "rgss", TestRgssArchiveReader),
        ("MapTransfers", "gamedata", TestMapTransfers),
        ("RubyMarshalReader", "rgss", TestRubyMarshalReader),
        ("RubyBridgeCalculations", "rgss", TestRubyBridgeCalculations),
        ("RubyBridgeIpcAndLayout", "rgss", TestRubyBridgeIpcAndLayout),
        ("RocketRenderMKXP", "engine", TestRocketRenderMKXP),
        ("RocketRenderEasyRPGAndLibLcf", "engine", TestRocketRenderEasyRPGAndLibLcf),
        ("NewFeatures", "feature", TestNewFeatures),
        ("IssueFixes", "feature", TestIssueFixes),
        ("MultiRemoteKeys", "multi", TestMultiRemoteKeys),
        ("MultiTitle", "multi", TestMultiTitle),
        ("ChatColorCooldown", "multi", TestChatColorCooldown),
        ("MultiPolicy", "multi", TestMultiPolicy),
        ("DeltaUpdate", "update", TestDeltaUpdate),
        ("GameOwnership", "multi", TestGameOwnership),
        ("AssetCatalog", "assets", TestAssetCatalog),
        ("ChoiceVote", "multi", TestChoiceVote),
        ("RtpResolver", "rtp", TestRtpResolver),
        ("TutorialCoversMenus", "tutorial", TestTutorialCoversMenus),
    ];

    public static int Main(string[] args)
    {
        if (RunTool(args) is int toolExit) return toolExit;

        if (args.Contains("--list"))
        {
            foreach (var group in Tests.GroupBy(t => t.Area))
                Console.WriteLine($"{group.Key}: {string.Join(", ", group.Select(t => t.Name))}");
            return 0;
        }
        var filters = args.Where(a => !a.StartsWith("--")).ToArray();
        var picked = Tests.Where(t => filters.Length == 0 || filters.Any(f =>
            t.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || t.Area.Equals(f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (picked.Count == 0)
        {
            Console.WriteLine($"no test matches: {string.Join(" ", filters)} (--list shows the names)");
            return 2;
        }

        Console.WriteLine("========================================");
        Console.WriteLine($"RocketRPG tests ({picked.Count}/{Tests.Length})");
        Console.WriteLine("========================================\n");
        foreach (var t in picked) t.Run();

        Console.WriteLine("\n----------------------------------------");
        Console.WriteLine($"Test Results: PASS={_passed}, FAIL={_failed}, TOTAL={_passed + _failed}");
        Console.WriteLine("----------------------------------------");

        return _failed == 0 ? 0 : 1;
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

}
