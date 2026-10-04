#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RocketRPG.Models;

/// <summary>
/// 쯔꾸르 기본 에셋 (RTP, MV/MZ 새 프로젝트에 들어 있는 그림·소리) 알아보기. 에셋 보기에서 숨기는 데 씁니다.
/// 목록(Resources/default_assets.json)은 "경로|크기"만 담습니다 (scripts/make_default_assets.py로 만듦).
/// 이름이 같아도 크기가 다르면(직접 고친 그림) 기본 에셋이 아닙니다.
/// </summary>
public static class DefaultAssets
{
    static readonly Lazy<Dictionary<string, HashSet<string>>> Lists = new(Load);

    static Dictionary<string, HashSet<string>> Load()
    {
        var map = new Dictionary<string, HashSet<string>>();
        try
        {
            using var s = typeof(DefaultAssets).Assembly.GetManifestResourceStream("RocketRPG.Resources.default_assets.json");
            if (s == null) return map;
            using var doc = JsonDocument.Parse(s);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array) continue;
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in p.Value.EnumerateArray()) if (e.GetString() is { } v) set.Add(v);
                map[p.Name] = set;
            }
        }
        catch (Exception ex) { UiLog.Write($"assets: default list failed {ex.Message}"); }
        return map;
    }

    /// <summary>엔진 → 목록 이름 (2000과 2003은 RTP 그림을 함께 씀)</summary>
    public static string? Family(int engine) => engine switch
    {
        CoreInterop.Engine2000 or CoreInterop.Engine2003 => "2k",
        CoreInterop.EngineXp => "xp",
        CoreInterop.EngineVx => "vx",
        CoreInterop.EngineAce => "vxace",
        CoreInterop.EngineMv => "mv",
        CoreInterop.EngineMz => "mz",
        _ => null,
    };

    /// <summary>
    /// 기본 에셋인지. relPath는 게임 기준 상대 경로 (2000/2003·XP~Ace: "CharSet/a.png", "Graphics/Characters/a.png",
    /// MV/MZ: www 기준 "img/characters/Actor1.png"). 암호화(MV/MZ)면 원래 확장자와 크기(앞 16바이트 머리 뺌)로 봅니다.
    /// </summary>
    public static bool IsDefault(int engine, string relPath, long size, bool encrypted = false)
    {
        if (Family(engine) is not { } fam || !Lists.Value.TryGetValue(fam, out var set)) return false;
        string p = relPath.Replace('\\', '/');
        if (encrypted)
        {
            p = Path.ChangeExtension(p, AssetCatalog.PlainExtension(p));
            size -= 16;
        }
        return set.Contains($"{p.ToLowerInvariant()}|{size}");
    }
}
