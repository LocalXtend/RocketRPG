#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RocketRPG.Models;

/// <summary>보관함의 스크린샷 한 장</summary>
public sealed record ScreenshotInfo(string Path, string Game, DateTime Taken, long Bytes)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 스크린샷 보관함: RocketRPG\screenshots\&lt;게임명&gt;\&lt;게임명&gt;-YYYYMMDD-NNN.png
/// 번호는 게임·날짜마다 001부터 (이름순 정렬 = 찍은 순서).
/// </summary>
public static class ScreenshotStore
{
    public static string Root => System.IO.Path.Combine(SettingsService.Root(), "screenshots");

    /// <summary>파일·폴더 이름에 쓸 수 있게 게임명을 다듬습니다.</summary>
    public static string SafeName(string? title)
    {
        string s = (title ?? "").Trim();
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
        if (s.Length > 60) s = s[..60].Trim();
        return s.Length == 0 ? "게임" : s;
    }

    /// <summary>PNG를 저장하고 경로를 돌려줍니다.</summary>
    public static string Save(string gameTitle, byte[] png, DateTime? when = null)
    {
        string game = SafeName(gameTitle);
        string dir = System.IO.Path.Combine(Root, game);
        Directory.CreateDirectory(dir);
        string prefix = $"{game}-{(when ?? DateTime.Now):yyyyMMdd}-";
        int next = NextNumber(dir, prefix);
        string path;
        // 같은 순간에 두 번 찍어도 겹치지 않게
        while (File.Exists(path = System.IO.Path.Combine(dir, $"{prefix}{next:D3}.png"))) next++;
        File.WriteAllBytes(path, png);
        return path;
    }

    static int NextNumber(string dir, string prefix)
    {
        int max = 0;
        foreach (var f in Directory.EnumerateFiles(dir, prefix + "*.png"))
        {
            string num = System.IO.Path.GetFileNameWithoutExtension(f)[prefix.Length..];
            if (int.TryParse(num, out int n) && n > max) max = n;
        }
        return max + 1;
    }

    /// <summary>보관함의 게임 폴더와 장수</summary>
    public static List<(string Game, int Count)> Games()
    {
        if (!Directory.Exists(Root)) return new();
        return Directory.EnumerateDirectories(Root)
            .Select(d => (System.IO.Path.GetFileName(d), Directory.EnumerateFiles(d, "*.png").Count()))
            .Where(g => g.Item2 > 0)
            .OrderBy(g => g.Item1, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>스크린샷 목록 (최근 것 먼저). game이 null이면 전체.</summary>
    public static List<ScreenshotInfo> List(string? game = null)
    {
        var list = new List<ScreenshotInfo>();
        if (!Directory.Exists(Root)) return list;
        var dirs = game == null ? Directory.EnumerateDirectories(Root) : new[] { System.IO.Path.Combine(Root, game) }.Where(Directory.Exists);
        foreach (var d in dirs)
        {
            string g = System.IO.Path.GetFileName(d);
            foreach (var f in Directory.EnumerateFiles(d, "*.png"))
            {
                var fi = new FileInfo(f);
                list.Add(new ScreenshotInfo(f, g, fi.LastWriteTime, fi.Length));
            }
        }
        return list.OrderByDescending(s => s.Taken).ThenByDescending(s => s.FileName, StringComparer.Ordinal).ToList();
    }

    public static void Delete(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            try { if (IsInside(p)) File.Delete(p); }
            catch (Exception ex) { UiLog.Write($"screenshots: delete failed {ex.Message}"); }
        }
        // 빈 게임 폴더 정리
        try
        {
            if (Directory.Exists(Root))
                foreach (var d in Directory.EnumerateDirectories(Root))
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
        }
        catch { }
    }

    /// <summary>보관함 밖의 파일은 절대 지우지 않습니다.</summary>
    static bool IsInside(string path) =>
        System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(Root) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
