using System;
using System.IO;
using System.Linq;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// 1.0.0 도구 > 에셋 보기: 엔진별 목록과 읽기 (게임 폴더, 암호화 아카이브, MV/MZ 암호화)
public partial class Program
{
    private static void TestAssetCatalog()
    {
        Console.WriteLine("--- Testing AssetCatalog (asset viewer) ---");
        Assert("Kind: encrypted MV image is an image", AssetCatalog.KindOf("Actor1.rpgmvp") == AssetKind.Image && AssetCatalog.KindOf("a.png_") == AssetKind.Image);
        Assert("Kind: encrypted MZ sound is audio", AssetCatalog.KindOf("Battle1.ogg_") == AssetKind.Audio && AssetCatalog.KindOf("x.rpgmvo") == AssetKind.Audio);
        Assert("Kind: scripts and data", AssetCatalog.KindOf("plugins.js") == AssetKind.Script && AssetCatalog.KindOf("Map001.lmu") == AssetKind.Data);
        Assert("Plain extension of encrypted files", AssetCatalog.PlainExtension("a.rpgmvo") == ".ogg" && AssetCatalog.PlainExtension("b.m4a_") == ".m4a");

        CheckAssets("vxace/kohaku_1.05(K)", CoreInterop.EngineAce, archive: true);
        CheckAssets("xp/Do_You_Remember_My_Lullaby", CoreInterop.EngineXp, archive: true);
        CheckAssets("mv/돈갚아! 친구들!", CoreInterop.EngineMv, encrypted: true);
        CheckAssets("mz/Haven", CoreInterop.EngineMz, encrypted: true);
        CheckAssets("mz/팥빙수를 만들자", CoreInterop.EngineMz);
        CheckAssets("2000/7thJojo", CoreInterop.Engine2000);
    }

    static void CheckAssets(string sample, int engine, bool archive = false, bool encrypted = false)
    {
        string dir = FindSample(sample);
        if (!Directory.Exists(dir)) { Console.WriteLine($"[SKIP] assets {sample} (no sample)"); return; }
        using var cat = new AssetCatalog(dir, engine);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var list = cat.Enumerate();
        sw.Stop();
        var images = list.Where(e => e.Kind == AssetKind.Image && !e.Rtp).ToList();
        Assert($"Assets {sample}: lists images ({images.Count} of {list.Count}, {sw.ElapsedMilliseconds} ms)", images.Count > 0);
        Assert($"Assets {sample}: no duplicate paths", list.Where(e => !e.Rtp).GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).All(g => g.Count() == 1));
        if (archive) Assert($"Assets {sample}: reads entries inside the archive", list.Any(e => e.InArchive));
        if (encrypted) Assert($"Assets {sample}: finds encrypted files", list.Any(e => e.Encrypted));
        // 그림 하나를 실제로 풀어 봄 (암호화·압축 안 포함)
        var pick = images.FirstOrDefault(e => encrypted ? e.Encrypted : archive ? e.InArchive : true) ?? images[0];
        var bytes = cat.Read(pick);
        bool ok = false;
        if (bytes != null)
        {
            try
            {
                ok = AssetCatalog.PlainExtension(pick.Name) == ".xyz"
                    ? GameAssetSource.DecodeXyzImage(bytes) is { W: > 0 }
                    : GameAssetSource.DecodeImage(bytes, false) is { W: > 0 };
            }
            catch (Exception ex) { Console.WriteLine($"  decode {pick.Path}: {ex.Message}"); }
        }
        Assert($"Assets {sample}: decodes {pick.Path}", ok);
    }
}
