#nullable enable
using System;
using System.IO;

namespace RocketRPG.Models;

/// <summary>
/// RocketRenderSystem: Unified native rendering system manager and dispatch hub for RocketRPG.
/// Coordinates the 3 specialized hardware-accelerated native backends:
/// 1. MV / MZ: WebViewRenderer (Microsoft.Web.WebView2 / Chromium hardware-accelerated pipeline)
/// 2. XP / VX / VX Ace: RocketRenderMKXP (mkxp-z Ruby 3.x native runtime)
/// 3. 2000 / 2003: RocketRenderEasyRPG (EasyRPG Player / liblcf native runtime)
/// </summary>
public static class RocketRenderSystem
{
    public const string EngineFamilyEasyRpg = "EasyRPG";
    public const string EngineFamilyMkxp = "MKXP";
    public const string EngineFamilyWebView2 = "WebView2";
    public const string EngineFamilyUnknown = "Unknown";

    public static string GetEngineFamily(int engine) => engine switch
    {
        CoreInterop.Engine2000 or CoreInterop.Engine2003 => EngineFamilyEasyRpg,
        CoreInterop.EngineXp or CoreInterop.EngineVx or CoreInterop.EngineAce => EngineFamilyMkxp,
        CoreInterop.EngineMv or CoreInterop.EngineMz => EngineFamilyWebView2,
        _ => EngineFamilyUnknown
    };

    public static string GetEngineDisplayName(int engine) => engine switch
    {
        CoreInterop.Engine2000 => "RPG Maker 2000",
        CoreInterop.Engine2003 => "RPG Maker 2003",
        CoreInterop.EngineXp => "RPG Maker XP",
        CoreInterop.EngineVx => "RPG Maker VX",
        CoreInterop.EngineAce => "RPG Maker VX Ace",
        CoreInterop.EngineMv => "RPG Maker MV",
        CoreInterop.EngineMz => "RPG Maker MZ",
        _ => "알 수 없는 엔진"
    };

    public static (int width, int height) GetDefaultResolution(int engine) => engine switch
    {
        CoreInterop.Engine2000 or CoreInterop.Engine2003 => (320, 240),
        CoreInterop.EngineXp => (640, 480),
        CoreInterop.EngineVx or CoreInterop.EngineAce => (544, 416),
        CoreInterop.EngineMv => (816, 624),
        CoreInterop.EngineMz => (816, 624),
        _ => (640, 480)
    };

    public static int GetDefaultFps(int engine) => engine switch
    {
        CoreInterop.Engine2000 or CoreInterop.Engine2003 => 60,
        CoreInterop.EngineXp => 40,
        CoreInterop.EngineVx or CoreInterop.EngineAce => 60,
        CoreInterop.EngineMv or CoreInterop.EngineMz => 60,
        _ => 60
    };

    public static bool IsNativeSupported(int engine, string gameDir) => engine switch
    {
        CoreInterop.EngineMv or CoreInterop.EngineMz => true, // WebView2 is runtime-hosted
        CoreInterop.EngineXp or CoreInterop.EngineVx or CoreInterop.EngineAce =>
            CoreInterop.rpg_mkxp_is_available_game(gameDir) != 0 || !string.IsNullOrEmpty(RocketRenderMKXP.ResolveExecutable(gameDir)),
        CoreInterop.Engine2000 or CoreInterop.Engine2003 =>
            CoreInterop.rpg_easyrpg_is_available_game(gameDir) != 0 || !string.IsNullOrEmpty(RocketRenderEasyRPG.ResolveExecutable(gameDir)),
        _ => false
    };
}
