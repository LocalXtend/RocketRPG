#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// 개발용 명령 (--render-map, --scan-stress 등): 테스트가 아니라 진단할 때 씁니다
public partial class Program
{
    /// <summary>개발용 명령이면 실행하고 종료 코드를, 아니면 null (테스트 실행)</summary>
    static int? RunTool(string[] args)
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
        return null;
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
}
