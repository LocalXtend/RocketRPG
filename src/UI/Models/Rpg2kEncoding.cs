#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RocketRPG.Models;

/// <summary>
/// RPG Maker 2000/2003 데이터(RPG_RT.ldb/lmt/lmu)의 문자 인코딩 추정.
/// EasyRPG의 자동 감지는 한국어(CP949)를 후보로 내지 않아 한국어 게임을 중국어(GBK)로 읽고,
/// 그 결과 파일 이름이 맞지 않아 "Cannot find" 에셋 오류가 납니다.
/// 게임 폴더의 비 ASCII 에셋 파일 이름을 각 인코딩으로 바꿔 데이터 안에 실제로 등장하는지 세어,
/// 파일이 맞게 찾아지는 인코딩을 --encoding으로 넘깁니다. 확신이 없으면 null (EasyRPG 감지에 맡김).
/// </summary>
public static class Rpg2kEncoding
{
    static readonly int[] Candidates = [949, 932, 936, 950];
    static readonly string[] AssetDirs =
    [
        "Backdrop", "Battle", "Battle2", "BattleCharSet", "BattleWeapon", "CharSet", "ChipSet", "FaceSet", "Frame",
        "GameOver", "Monster", "Movie", "Music", "Panorama", "Picture", "Sound", "System", "System2", "Title"
    ];

    public static string? Detect(string gameDir)
    {
        try
        {
            // 게임이 직접 지정한 인코딩이 있으면 그대로 둡니다 (명령줄이 ini보다 우선하므로 넘기지 않음).
            string ini = Path.Combine(gameDir, "RPG_RT.ini");
            if (File.Exists(ini) && RtpResolver.IniValue(RtpResolver.ReadIniText(ini, out _), "EasyRPG", "Encoding") is { Length: > 0 })
                return null;

            var names = AssetNames(gameDir).ToList();
            if (names.Count == 0) return null;
            byte[] data = LoadData(gameDir);
            if (data.Length == 0) return null;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var hits = new Dictionary<int, int>();
            foreach (int cp in Candidates)
            {
                var enc = Encoding.GetEncoding(cp, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                int n = 0;
                foreach (var name in names)
                {
                    byte[] b;
                    try { b = enc.GetBytes(name); } catch (EncoderFallbackException) { continue; }
                    if (data.AsSpan().IndexOf(b) >= 0) n++;
                }
                hits[cp] = n;
            }
            var best = hits.OrderByDescending(kv => kv.Value).First();
            bool unique = hits.Count(kv => kv.Value == best.Value) == 1;
            UiLog.Write($"Rpg2kEncoding: {Path.GetFileName(gameDir)} " + string.Join(", ", hits.Select(kv => $"{kv.Key}={kv.Value}")));
            return best.Value > 0 && unique ? best.Key.ToString() : null;
        }
        catch (Exception ex)
        {
            UiLog.Write($"Rpg2kEncoding: {ex.Message}");
            return null;
        }
    }

    /// <summary>에셋 폴더의 비 ASCII 파일 이름(확장자 제외)</summary>
    static IEnumerable<string> AssetNames(string gameDir)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in AssetDirs)
        {
            string dir = Path.Combine(gameDir, d);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                string n = Path.GetFileNameWithoutExtension(f);
                if (n.Any(c => c > 0x7F) && seen.Add(n)) yield return n;
            }
        }
    }

    /// <summary>데이터베이스 + 맵 트리 + 맵 일부 (큰 게임에서도 빠르도록 상한)</summary>
    static byte[] LoadData(string gameDir)
    {
        using var ms = new MemoryStream();
        foreach (var name in new[] { "RPG_RT.ldb", "RPG_RT.lmt" })
        {
            string p = Path.Combine(gameDir, name);
            if (File.Exists(p)) { var b = File.ReadAllBytes(p); ms.Write(b, 0, b.Length); }
        }
        foreach (var lmu in Directory.EnumerateFiles(gameDir, "Map*.lmu").OrderBy(x => x).Take(80))
        {
            var b = File.ReadAllBytes(lmu);
            ms.Write(b, 0, b.Length);
            if (ms.Length > 48 * 1024 * 1024) break;
        }
        return ms.ToArray();
    }
}
