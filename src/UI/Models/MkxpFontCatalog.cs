#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RocketRPG.Models;

/// <summary>
/// mkxp-z 글꼴 환경.
/// mkxp-z는 게임/RTP 폴더의 Fonts\ 에 있는 글꼴만 쓰고, 글꼴을 "파일 속 패밀리 이름(소문자)"으로 찾습니다.
/// 그래서 (1) 파일 이름이나 한글 이름으로 글꼴을 부르는 게임, (2) 윈도우 글꼴 대체(한글 fallback)에 기대는 게임은
/// 글자가 비거나 "글꼴이 설치되어 있지 않습니다"로 멈춥니다. 여기서 실제 이름표를 읽어 올바른 fontSub를 만들고,
/// 필요한 시스템 글꼴은 사용자 PC의 C:\Windows\Fonts 에서 캐시로 복사해 씁니다(재배포하지 않음).
/// </summary>
public static class MkxpFontCatalog
{
    /// <summary>한글이 없는 RGSS 기본/일본어 글꼴 이름 → 한글 글꼴로 대체 (원래 윈도우의 글꼴 대체 동작 재현)</summary>
    static readonly string[] CommonNames =
    [
        "arial", "liberation sans", "verdana", "tahoma", "times new roman", "courier new",
        "ms gothic", "ms pgothic", "ms mincho", "ms pmincho", "ms ui gothic",
        "ｍｓ ゴシック", "ｍｓ ｐゴシック", "ｍｓ 明朝", "ｍｓ ｐ明朝", "ＭＳ ゴシック", "ＭＳ Ｐゴシック", "ＭＳ 明朝", "ＭＳ Ｐ明朝",
        "umeplus gothic", "umeplus p gothic", "vl gothic", "vl pgothic", "meiryo", "メイリオ",
        "gulim", "gulimche", "dotum", "dotumche", "batang", "batangche", "gungsuh",
        "굴림", "굴림체", "돋움", "돋움체", "바탕", "바탕체", "궁서",
        "malgun gothic", "맑은 고딕", "nanumgothic", "나눔고딕"
    ];

    /// <summary>사용자 PC에서 복사해 둘 글꼴 파일 (있는 것만)</summary>
    static readonly string[] SystemFontFiles =
    [
        "malgun.ttf", "malgunbd.ttf", "gulim.ttc", "batang.ttc",
        "arial.ttf", "arialbd.ttf", "times.ttf", "cour.ttf", "tahoma.ttf", "verdana.ttf",
        "msgothic.ttc", "msmincho.ttc", "meiryo.ttc"
    ];

    public const string FallbackFamily = "malgun gothic";

    /// <summary>
    /// %LOCALAPPDATA%\RocketRPG\mkxp\_fonts (RTP로 추가) 를 준비합니다. 그 안의 Fonts\ 에 시스템 글꼴 사본을 둡니다.
    /// </summary>
    public static string EnsureSystemFontRoot(string? extraFamily, IEnumerable<string>? wantedFamilies = null)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RocketRPG", "mkxp", "_fonts");
        string fonts = Path.Combine(root, "Fonts");
        Directory.CreateDirectory(fonts);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var files = new List<string>(SystemFontFiles);
        var want = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fam in (wantedFamilies ?? []).Append(extraFamily ?? "")) if (!string.IsNullOrWhiteSpace(fam)) want.Add(Lower(fam));
        foreach (var fam in (wantedFamilies ?? []).Append(extraFamily ?? ""))
        {
            if (string.IsNullOrWhiteSpace(fam)) continue;
            string? f = FindSystemFontFile(fam);
            if (f != null) files.Add(f);
        }
        // mkxp-z는 글꼴 모음(.ttc)을 읽지 못해(굴림·바탕·MS 고딕 등) 글자가 네모로 나옵니다. 예전 사본은 지우고 낱개 .ttf로 나눠 둡니다.
        foreach (var old in Directory.EnumerateFiles(fonts, "*.ttc")) { try { File.Delete(old); } catch { } }
        foreach (var name in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string src = Path.IsPathRooted(name) ? name : Path.Combine(win, name);
            string dst = Path.Combine(fonts, Path.GetFileName(src));
            try
            {
                var si = new FileInfo(src);
                if (!si.Exists) continue;
                if (si.Extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase)) { SplitCollection(src, fonts, want); continue; }
                var di = new FileInfo(dst);
                if (di.Exists && di.Length == si.Length) continue;
                File.Copy(src, dst, true);
            }
            catch (Exception ex) { UiLog.Write($"MkxpFontCatalog: copy {name} failed: {ex.Message}"); }
        }
        return root;
    }

    /// <summary>
    /// 게임 스크립트가 실제로 글꼴로 지정하는 이름들 (Font.default_name = "...", xxx.font.name = "...", Font.new("...")).
    /// 배열이면 첫 번째 이름(원래 RGSS가 가장 먼저 찾는 글꼴)만. 설치 안내에 쓰므로 FontNamesInScripts보다 엄격합니다.
    /// </summary>
    public static List<string> DeclaredFontNames(string gameDir, int engine)
    {
        var result = new List<string>();
        try
        {
            string ext = engine == CoreInterop.EngineXp ? "rxdata" : engine == CoreInterop.EngineVx ? "rvdata" : "rvdata2";
            string rel = $"Data\\Scripts.{ext}";
            string ini = Path.Combine(gameDir, "Game.ini");
            if (File.Exists(ini) && RtpResolver.IniValue(RtpResolver.ReadIniText(ini, out _), "Game", "Scripts") is { Length: > 0 } s) rel = s;
            using var src = new GameAssetSource(gameDir, engine);
            byte[]? data = src.ReadData(rel);
            if (data == null) return result;
            var re = new System.Text.RegularExpressions.Regex(
                "(?:default_name|font\\.name)\\s*=\\s*\\[?\\s*[\"']([^\"'\\\\#/]{2,40})[\"']|Font\\.new\\(\\s*[\"']([^\"'\\\\#/]{2,40})[\"']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in ScriptTexts(data))
                foreach (var line in code.Split('\n'))
                {
                    if (line.TrimStart().StartsWith("#")) continue;   // 주석 줄
                    foreach (System.Text.RegularExpressions.Match m in re.Matches(line))
                    {
                        string n = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                        if (n.Length >= 2 && seen.Add(n)) result.Add(n);
                    }
                }
        }
        catch (Exception ex) { UiLog.Write($"MkxpFontCatalog: declared font scan failed: {ex.Message}"); }
        return result;
    }

    /// <summary>글꼴 이름이 설치되어 있거나(레지스트리/WPF), 주어진 폴더들의 글꼴 파일에 있으면 true.</summary>
    public static bool IsFontAvailable(string family, IEnumerable<string> fontDirs)
    {
        if (FindSystemFontFile(family) != null) return true;
        // 게임 동봉 글꼴은 영어 패밀리 이름으로 들어 있고 스크립트는 한글 이름을 쓰는 경우가 많습니다
        // (12LotteMartDreamBold = 12롯데마트드림Bold). 별칭과 파일 이름까지 봐야 합니다 — mkxp-z도 그 이름으로 찾습니다.
        string want = Lower(family);
        foreach (var d in fontDirs)
            if (Directory.Exists(d) && NamesIn(d).Contains(want)) return true;
        return false;
    }

    static readonly Dictionary<string, (DateTime Time, HashSet<string> Names)> _namesCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>폴더 글꼴들의 모든 이름(패밀리, 다른 언어 이름, 파일 이름) — 소문자. 파일 목록이 같으면 캐시.</summary>
    static HashSet<string> NamesIn(string dir)
    {
        var time = Directory.GetLastWriteTimeUtc(dir);
        lock (_namesCache)
            if (_namesCache.TryGetValue(dir, out var c) && c.Time == time) return c.Names;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".ttf" or ".otf" or ".ttc")) continue;
            set.Add(Lower(Path.GetFileNameWithoutExtension(file)));
            if (ReadNames(file) is not { } n) continue;
            set.Add(Lower(n.Family));
            foreach (var a in n.Aliases) set.Add(Lower(a));
        }
        lock (_namesCache) _namesCache[dir] = (time, set);
        return set;
    }

    /// <summary>
    /// 게임 스크립트가 부르는 글꼴 이름 후보 (Font.exist?("..."), Font.default_name = "..." 등이 있는 줄의 문자열).
    /// 원래 RGSS는 윈도우에 설치된 글꼴을 쓰므로, 설치돼 있으면 캐시로 복사해 mkxp-z도 찾게 합니다.
    /// </summary>
    public static List<string> FontNamesInScripts(string gameDir, int engine)
    {
        var result = new List<string>();
        try
        {
            string ext = engine == CoreInterop.EngineXp ? "rxdata" : engine == CoreInterop.EngineVx ? "rvdata" : "rvdata2";
            string rel = $"Data\\Scripts.{ext}";
            string ini = Path.Combine(gameDir, "Game.ini");
            if (File.Exists(ini) && RtpResolver.IniValue(RtpResolver.ReadIniText(ini, out _), "Game", "Scripts") is { Length: > 0 } s) rel = s;
            using var src = new GameAssetSource(gameDir, engine);
            byte[]? data = src.ReadData(rel);
            if (data == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lit = new System.Text.RegularExpressions.Regex("[\"']([^\"'\\\\#/]{2,40})[\"']");
            foreach (var code in ScriptTexts(data))
            {
                foreach (var line in code.Split('\n'))
                {
                    if (line.IndexOf("font", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (System.Text.RegularExpressions.Match m in lit.Matches(line))
                    {
                        string n = m.Groups[1].Value.Trim();
                        if (n.Length >= 2 && !n.All(char.IsDigit) && seen.Add(n)) result.Add(n);
                    }
                }
            }
        }
        catch (Exception ex) { UiLog.Write($"MkxpFontCatalog: script scan failed: {ex.Message}"); }
        return result;
    }

    /// <summary>Scripts.rxdata/rvdata/rvdata2 (Marshal 배열 [[id, 이름, zlib 본문], ...]) 의 본문들</summary>
    static IEnumerable<string> ScriptTexts(byte[] b)
    {
        int pos = 2;
        var bodies = new List<byte[]>();
        int RInt()
        {
            int c = (sbyte)b[pos++];
            if (c == 0) return 0;
            if (c >= 5) return c - 5;
            if (c <= -5) return c + 5;
            int n = Math.Abs(c), v = 0;
            for (int i = 0; i < n; i++) v |= b[pos++] << (8 * i);
            return c > 0 ? v : v - (1 << (8 * n));
        }
        object? Read()
        {
            char t = (char)b[pos++];
            switch (t)
            {
                case '[': { int n = RInt(); var l = new List<object?>(n); for (int i = 0; i < n; i++) l.Add(Read()); return l; }
                case 'i': return RInt();
                case '"': { int n = RInt(); var v = b.AsSpan(pos, n).ToArray(); pos += n; return v; }
                case 'I': { var v = Read(); int n = RInt(); for (int i = 0; i < n; i++) { Read(); Read(); } return v; }
                case ':': { int n = RInt(); pos += n; return null; }
                case ';': RInt(); return null;
                case 'T': case 'F': case '0': return null;
                default: throw new InvalidDataException($"marshal tag {t}");
            }
        }
        if (Read() is List<object?> top)
            foreach (var e in top)
                if (e is List<object?> { Count: >= 3 } entry && entry[2] is byte[] z) bodies.Add(z);
        foreach (var z in bodies)
        {
            string text;
            try
            {
                using var zs = new System.IO.Compression.ZLibStream(new MemoryStream(z), System.IO.Compression.CompressionMode.Decompress);
                using var sr = new StreamReader(zs, Encoding.UTF8);
                text = sr.ReadToEnd();
            }
            catch { continue; }
            yield return text;
        }
    }

    /// <summary>레지스트리의 설치 글꼴 목록에서 패밀리 이름에 맞는 파일을 찾습니다.</summary>
    static string? FindSystemFontFile(string family)
    {
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key == null) continue;
                foreach (var v in key.GetValueNames())
                {
                    string name = v.Split('(')[0].Trim();
                    if (!name.Split('&').Select(s => s.Trim()).Contains(family, StringComparer.OrdinalIgnoreCase)) continue;
                    string? file = key.GetValue(v) as string;
                    if (string.IsNullOrEmpty(file)) continue;
                    return Path.IsPathRooted(file) ? file : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);
                }
            }
            catch { }
        }
        // 레지스트리에는 영어 이름만 있는 경우가 많아, 한글 이름 등은 WPF 글꼴 목록으로 찾습니다.
        return RocketFontSystem.TryResolveInstalledFont(family, false);
    }

    /// <summary>
    /// fontSub 목록("원래이름>실제패밀리", 모두 소문자 — mkxp-z는 찾을 이름만 소문자로 바꾸고 표는 그대로 비교합니다).
    /// fontDirs: mkxp-z가 훑는 모든 Fonts 폴더(게임, RTP들, 캐시).
    /// </summary>
    /// <param name="gameFontNames">게임 스크립트가 쓰는 글꼴 이름들. 사용자가 글꼴을 직접 골랐으면(targetFamily) 이 이름들과
    /// 게임에 동봉된 글꼴까지 모두 그 글꼴로 바꿉니다 (게임이 자기 글꼴을 지정해도 사용자 선택이 이김).</param>
    /// <param name="missingSubs">게임 글꼴 중 없는 것 → 대신 쓸 글꼴 (MissingFontSubstitutes). 사용자가 글꼴을 고르지 않았을 때만 씁니다.</param>
    public static List<string> BuildSubstitutions(IEnumerable<string> fontDirs, string? gameFontDir, string? targetFamily, IEnumerable<string>? gameOwnSubs = null,
        IEnumerable<string>? gameFontNames = null, IReadOnlyDictionary<string, string>? missingSubs = null)
    {
        var families = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in fontDirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".ttf" or ".otf" or ".ttc")) continue;
                var info = ReadNames(file);
                if (info == null) continue;
                string fam = Lower(info.Value.Family);
                families.Add(fam);
                foreach (var a in info.Value.Aliases.Append(Path.GetFileNameWithoutExtension(file)))
                {
                    string al = Lower(a);
                    if (al.Length == 0 || al == fam) continue;
                    // 여러 굵기가 같은 이름을 쓰면(Elice DX Neolli OTF → Light/Medium/Bold) 보통 굵기를 고릅니다
                    if (!aliases.TryGetValue(al, out var had) || WeightRank(fam) < WeightRank(had)) aliases[al] = fam;
                }
            }
        }

        var gameFamilies = gameFontDir != null ? FamiliesIn(gameFontDir) : new HashSet<string>();
        var subs = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Add(string from, string to)
        {
            from = Lower(from); to = Lower(to);
            if (from.Length == 0 || from == to || !used.Add(from)) return;
            subs.Add($"{from}>{to}");
        }
        string target = Lower(string.IsNullOrWhiteSpace(targetFamily) ? FallbackFamily : targetFamily!);
        if (!families.Contains(target) && !aliases.ContainsKey(target))
            target = families.Contains(FallbackFamily) ? FallbackFamily : (families.Contains("nanumgothic") ? "nanumgothic" : target);
        else if (aliases.TryGetValue(target, out var real)) target = real;

        // 사용자가 고른 글꼴: 게임이 쓰는 이름, 게임 동봉 글꼴(과 그 별칭), 게임 자체 대체의 양쪽 이름을 모두 사용자 글꼴로
        bool userChose = !string.IsNullOrWhiteSpace(targetFamily) && families.Contains(target);
        if (userChose)
        {
            foreach (var n in gameFontNames ?? []) Add(n, target);
            foreach (var fam in gameFamilies)
            {
                Add(fam, target);
                foreach (var kv in aliases) if (kv.Value == fam) Add(kv.Key, target);
            }
            foreach (var s in gameOwnSubs ?? [])
            {
                int i = s.IndexOf('>');
                if (i > 0) { Add(s[..i], target); Add(s[(i + 1)..], target); }
            }
            // 흔한 기본 글꼴 이름도 별칭(굴림>gulim 등)보다 먼저 사용자 글꼴로
            foreach (var n in CommonNames) if (Lower(n) != target) Add(n, target);
        }
        else if (missingSubs != null)
        {
            // 게임 글꼴이 PC에 없으면 비슷한 설치 글꼴로 (없으면 mkxp-z 내장 글꼴로 넘어가 한글이 네모로 나옴)
            foreach (var (from, sub) in missingSubs)
            {
                string to = Lower(sub);
                if (aliases.TryGetValue(to, out var realSub)) to = realSub;
                if (families.Contains(to)) Add(from, to);
            }
        }

        // 게임이 직접 지정한 대체가 가장 우선
        if (gameOwnSubs != null)
        {
            foreach (var s in gameOwnSubs)
            {
                int i = s.IndexOf('>');
                if (i > 0) Add(s[..i], s[(i + 1)..]);
            }
        }
        // 파일 이름/한글 이름 → 실제 패밀리 (실제 패밀리로 이미 있는 이름은 건드리지 않음)
        foreach (var kv in aliases)
            if (!families.Contains(kv.Key)) Add(kv.Key, kv.Value);

        // 한글이 없는 흔한 기본 글꼴 → 한글 글꼴 (게임 폴더에 그 글꼴이 직접 들어 있으면 존중)
        foreach (var n in CommonNames)
        {
            string ln = Lower(n);
            if (ln == target) continue;
            if (!userChose && gameFamilies.Contains(ln)) continue;
            Add(ln, target);
        }
        return subs;
    }

    /// <summary>패밀리 이름 끝의 굵기로 매긴 순위 (작을수록 본문용 보통 굵기).</summary>
    static int WeightRank(string family)
    {
        string f = family.ToLowerInvariant();
        string[] order = ["regular", "normal", "book", "medium", "light", "semibold", "bold", "thin", "extralight", "extrabold", "black", "heavy"];
        for (int i = 0; i < order.Length; i++)
            if (f.EndsWith(" " + order[i], StringComparison.Ordinal) || f.EndsWith("-" + order[i], StringComparison.Ordinal)) return i;
        return -1;   // 굵기가 이름에 없음 = 그 자체가 기본
    }

    /// <summary>게임 폴더 Fonts에 있는 글꼴 패밀리(소문자) — 이 이름들은 대체하지 않습니다.</summary>
    public static HashSet<string> FamiliesIn(string dir)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return set;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var info = ReadNames(file);
            if (info != null) set.Add(Lower(info.Value.Family));
        }
        return set;
    }

    /// <summary>mkxp-z(C tolower)와 같은 방식: ASCII 대문자만 소문자로.</summary>
    static string Lower(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Trim()) sb.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
        return sb.ToString();
    }

    /// <summary>
    /// TTF/OTF/TTC(첫 글꼴)의 name 표를 읽어 FreeType(SDL_ttf)과 같은 규칙의 패밀리 이름과 모든 언어의 별칭을 돌려줍니다.
    /// </summary>
    public static (string Family, List<string> Aliases)? ReadNames(string file)
    {
        try
        {
            byte[] d = File.ReadAllBytes(file);
            int dir = 0;
            if (d.Length > 16 && d[0] == 't' && d[1] == 't' && d[2] == 'c' && d[3] == 'f') dir = (int)U32(d, 12);
            return ReadNames(d, dir);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>글꼴 자료 d에서 dir 위치의 표 목록(글꼴 모음이면 그 안의 한 글꼴)의 이름들.</summary>
    static (string Family, List<string> Aliases)? ReadNames(byte[] d, int dir)
    {
        try
        {
            int numTables = U16(d, dir + 4);
            int nameOff = -1;
            for (int i = 0; i < numTables; i++)
            {
                int r = dir + 12 + i * 16;
                if (d[r] == 'n' && d[r + 1] == 'a' && d[r + 2] == 'm' && d[r + 3] == 'e') { nameOff = (int)U32(d, r + 8); break; }
            }
            if (nameOff < 0) return null;
            int os2Off = -1;
            for (int i = 0; i < numTables; i++)
            {
                int r = dir + 12 + i * 16;
                if (d[r] == 'O' && d[r + 1] == 'S' && d[r + 2] == '/' && d[r + 3] == '2') { os2Off = (int)U32(d, r + 8); break; }
            }
            int count = U16(d, nameOff + 2);
            int strBase = nameOff + U16(d, nameOff + 4);
            var raw = new List<(int plat, int enc, int lang, int id, int off, int len)>();
            var recs = new List<(int plat, int enc, int lang, int id, string text)>();
            for (int i = 0; i < count; i++)
            {
                int r = nameOff + 6 + i * 12;
                int plat = U16(d, r), enc = U16(d, r + 2), lang = U16(d, r + 4), id = U16(d, r + 6), len = U16(d, r + 8), off = U16(d, r + 10);
                if (id is not (1 or 4 or 16 or 21) || len == 0 || strBase + off + len > d.Length) continue;
                raw.Add((plat, enc, lang, id, strBase + off, len));
                string text = plat is 0 or 3
                    ? Encoding.BigEndianUnicode.GetString(d, strBase + off, len)
                    : (plat == 1 && enc == 0 ? Encoding.Latin1.GetString(d, strBase + off, len) : "");
                text = text.TrimEnd('\0').Trim();
                if (text.Length > 0) recs.Add((plat, enc, lang, id, text));
            }

            // FreeType(mkxp-z가 쓰는 SDL_ttf)의 tt_face_get_name을 그대로 따라 합니다: 영어 윈도우 기록 우선,
            // 맥 영어/로마, 유니코드 순. 글자는 ASCII로 바꾸고 나머지는 '?'. (한글만 있는 이름은 "???"가 됨)
            string? FtName(int id)
            {
                int apple = -1, appleRoman = -1, appleEnglish = -1, win = -1, uni = -1;
                bool isEnglish = false;
                for (int n = 0; n < raw.Count; n++)
                {
                    var x = raw[n];
                    if (x.id != id) continue;
                    switch (x.plat)
                    {
                        case 0 or 2: uni = n; break;
                        case 1:
                            if (x.lang == 0) appleEnglish = n;
                            else if (x.enc == 0) appleRoman = n;
                            break;
                        case 3:
                            if ((win == -1 || (x.lang & 0x3FF) == 0x009) && x.enc is 0 or 1 or 10)
                            {
                                isEnglish = (x.lang & 0x3FF) == 0x009;
                                win = n;
                            }
                            break;
                    }
                }
                apple = appleEnglish >= 0 ? appleEnglish : appleRoman;
                string Ascii16(int at, int len)
                {
                    var sb = new StringBuilder();
                    for (int k = 0; k + 1 < len; k += 2)
                    {
                        int c = (d[at + k] << 8) | d[at + k + 1];
                        if (c == 0) break;
                        sb.Append(c is < 32 or > 127 ? '?' : (char)c);
                    }
                    return sb.ToString();
                }
                string Ascii8(int at, int len)
                {
                    var sb = new StringBuilder();
                    for (int k = 0; k < len; k++)
                    {
                        int c = d[at + k];
                        if (c == 0) break;
                        sb.Append(c is < 32 or > 127 ? '?' : (char)c);
                    }
                    return sb.ToString();
                }
                if (win >= 0 && !(apple >= 0 && !isEnglish)) return Ascii16(raw[win].off, raw[win].len);
                if (apple >= 0) return Ascii8(raw[apple].off, raw[apple].len);
                if (uni >= 0) return Ascii16(raw[uni].off, raw[uni].len);
                return null;
            }
            // sfnt_load_face: OS/2 fsSelection의 WWS 비트(256)가 있으면 16→1, 없으면 21(WWS)→16→1.
            // 예: Elice DX Neolli OTF는 WWS 비트가 없고 21번 이름이 'Elice DX Neolli OTF Light'라 그 이름으로 등록됩니다.
            bool wws = os2Off >= 0 && os2Off + 64 <= d.Length && U16(d, os2Off) != 0xFFFF && (U16(d, os2Off + 62) & 256) != 0;
            string? family = wws ? FtName(16) ?? FtName(1) : FtName(21) ?? FtName(16) ?? FtName(1);
            if (string.IsNullOrWhiteSpace(family)) return null;
            var aliases = recs.Where(x => x.id is 1 or 16 or 21).Select(x => x.text).Distinct().ToList();
            return (family, aliases);
        }
        catch
        {
            return null;
        }
    }

    // ── 빠진 글꼴 대체: 게임이 쓰는 글꼴이 PC에 없으면, 이름으로 종류를 짐작해 비슷한 설치 글꼴로 보여 줍니다 ──

    public enum FontKind { Gothic, Serif, Hand, Pixel }

    /// <summary>사용자가 정한 "빠진 글꼴 대신 쓸 글꼴" (전역 설정, 비어 있으면 자동으로 비슷한 글꼴).</summary>
    public static string MissingFontFallback { get; set; } = "";

    // 순서가 중요합니다: 픽셀/손글씨 → 고딕("Sans Serif"는 고딕) → 명조
    static readonly (FontKind Kind, string[] Words)[] KindWords =
    [
        (FontKind.Pixel, ["둥근모", "도트", "픽셀", "갈무리", "pixel", "dotfont", "galmuri", "bitmap", "8bit", "retro", "레트로"]),
        (FontKind.Hand, ["손글씨", "손편지", "필기", "붓", "연필", "펜글씨", "크레파스", "분필", "캘리", "마카", "메모", "handwriting", "hand", "brush", "pencil", "script", "memo", "marker"]),
        (FontKind.Gothic, ["고딕", "돋움", "굴림", "산스", "gothic", "ゴシック", "sans", "dotum", "gulim", "arial", "helvetica", "verdana", "tahoma", "meiryo", "メイリオ"]),
        (FontKind.Serif, ["명조", "바탕", "궁서", "mincho", "明朝", "serif", "myeongjo", "batang", "gungsuh", "roman", "times", "georgia", "garamond", "palatino", "century"]),
    ];

    static readonly Dictionary<FontKind, string[]> Preferred = new()
    {
        [FontKind.Gothic] = ["나눔고딕", "NanumGothic", "맑은 고딕", "Malgun Gothic"],
        [FontKind.Serif] = ["나눔명조", "NanumMyeongjo", "바탕", "Batang", "MS Mincho"],
        [FontKind.Pixel] = ["Galmuri11", "Galmuri9", "DungGeunMo", "둥근모꼴", "Neo둥근모", "NeoDunggeunmo"],
        [FontKind.Hand] = ["나눔손글씨 펜", "Nanum Pen Script", "나눔손글씨 붓", "Nanum Brush Script"],
    };

    static readonly Dictionary<FontKind, string?> _foundByKind = new();

    /// <summary>글꼴 이름으로 종류를 짐작합니다 (모르면 고딕).</summary>
    public static FontKind Classify(string name)
    {
        string n = name.ToLowerInvariant();
        foreach (var (kind, words) in KindWords)
            if (words.Any(w => n.Contains(w, StringComparison.Ordinal))) return kind;
        return FontKind.Gothic;
    }

    /// <summary>빠진 글꼴 대신 쓸 설치된 글꼴 이름 (사용자 지정 → 같은 종류의 추천 글꼴 → 같은 종류의 설치 글꼴 → 고딕).</summary>
    public static string? PickSubstitute(string missingName)
    {
        if (!string.IsNullOrWhiteSpace(MissingFontFallback) && FindSystemFontFile(MissingFontFallback) != null) return MissingFontFallback;
        var kind = Classify(missingName);
        return FindKind(kind) ?? (kind != FontKind.Gothic ? FindKind(FontKind.Gothic) : null);
    }

    static string? FindKind(FontKind kind)
    {
        lock (_foundByKind)
        {
            if (_foundByKind.TryGetValue(kind, out var cached)) return cached;
            string? found = Preferred[kind].FirstOrDefault(n => FindSystemFontFile(n) != null);
            if (found == null && kind is FontKind.Hand or FontKind.Pixel)
            {
                // 추천 글꼴이 없으면 사용자가 설치한 글꼴 중 같은 종류이면서 한글이 있는 것
                var words = KindWords.First(k => k.Kind == kind).Words;
                try
                {
                    foreach (var fam in System.Windows.Media.Fonts.SystemFontFamilies)
                    {
                        string? name = fam.FamilyNames.Values.FirstOrDefault(v => words.Any(w => v.Contains(w, StringComparison.OrdinalIgnoreCase)));
                        if (name == null || !HasHangul(fam)) continue;
                        if (FindSystemFontFile(fam.Source) != null) { found = fam.Source; break; }
                    }
                }
                catch (Exception ex) { UiLog.Write($"MkxpFontCatalog: font search failed: {ex.Message}"); }
            }
            _foundByKind[kind] = found;
            return found;
        }
    }

    static bool HasHangul(System.Windows.Media.FontFamily fam)
    {
        try
        {
            foreach (var tf in fam.GetTypefaces())
                if (tf.TryGetGlyphTypeface(out var g)) return g.CharacterToGlyphMap.ContainsKey('가');
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 게임 스크립트가 지정한 글꼴 중 PC와 게임/RTP 폴더 어디에도 없는 것 → 대신 쓸 글꼴.
    /// (Arial·MS 고딕 같은 흔한 이름은 BuildSubstitutions가 따로 한글 글꼴로 바꾸므로 제외)
    /// </summary>
    public static Dictionary<string, string> MissingFontSubstitutes(string gameDir, int engine, IEnumerable<string> fontDirs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dirs = fontDirs.ToList();
        foreach (var name in DeclaredFontNames(gameDir, engine))
        {
            if (CommonNames.Contains(Lower(name)) || IsFontAvailable(name, dirs)) continue;
            if (PickSubstitute(name) is { } sub) result[name] = sub;
        }
        return result;
    }

    /// <summary>게임/RTP 폴더의 .ttc를 캐시에 낱개 .ttf로 나눕니다 (원본 폴더에는 쓰지 않음).</summary>
    public static void SplitCollectionsFrom(string dir, IEnumerable<string> wantedFamilies, string? extraFamily)
    {
        if (!Directory.Exists(dir)) return;
        string fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RocketRPG", "mkxp", "_fonts", "Fonts");
        Directory.CreateDirectory(fonts);
        var want = new HashSet<string>(wantedFamilies.Append(extraFamily ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).Select(Lower), StringComparer.Ordinal);
        foreach (var ttc in Directory.EnumerateFiles(dir, "*.ttc")) SplitCollection(ttc, fonts, want);
    }

    /// <summary>
    /// 글꼴 모음(.ttc)의 각 글꼴을 "이름_번호.ttf"로 꺼냅니다. 표 자료는 그대로 복사하고 위치만 새로 매깁니다.
    /// 한 글꼴이 16MB쯤 되므로 이름이 want에 있는 글꼴만 꺼냅니다. 이미 꺼낸 파일이 원본보다 새로우면 건너뜁니다.
    /// </summary>
    static void SplitCollection(string ttc, string outDir, IReadOnlySet<string> want)
    {
        try
        {
            string stem = Path.GetFileNameWithoutExtension(ttc);
            var srcTime = File.GetLastWriteTimeUtc(ttc);
            if (want.Count == 0) return;
            byte[]? d = null;
            for (int f = 0; f < 16; f++)
            {
                string outFile = Path.Combine(outDir, $"{stem}_{f}.ttf");
                if (File.Exists(outFile) && File.GetLastWriteTimeUtc(outFile) >= srcTime) continue;
                d ??= File.ReadAllBytes(ttc);
                if (d.Length < 16 || d[0] != 't' || d[1] != 't' || d[2] != 'c' || d[3] != 'f' || f >= (int)U32(d, 8)) return;
                int dir = (int)U32(d, 12 + f * 4);
                if (ReadNames(d, dir) is not { } names || !names.Aliases.Append(names.Family).Any(n => want.Contains(Lower(n)))) continue;
                int numTables = U16(d, dir + 4);
                int headerLen = 12 + numTables * 16;
                var tables = new List<(int rec, int off, int len)>();
                int total = headerLen;
                for (int i = 0; i < numTables; i++)
                {
                    int r = dir + 12 + i * 16;
                    int off = (int)U32(d, r + 8), len = (int)U32(d, r + 12);
                    if (off < 0 || len < 0 || off + len > d.Length) throw new InvalidDataException("table out of range");
                    tables.Add((r, off, len));
                    total += (len + 3) & ~3;
                }
                var o = new byte[total];
                Buffer.BlockCopy(d, dir, o, 0, headerLen);
                int pos = headerLen;
                for (int i = 0; i < tables.Count; i++)
                {
                    var (_, off, len) = tables[i];
                    Buffer.BlockCopy(d, off, o, pos, len);
                    int r = 12 + i * 16 + 8;
                    o[r] = (byte)(pos >> 24); o[r + 1] = (byte)(pos >> 16); o[r + 2] = (byte)(pos >> 8); o[r + 3] = (byte)pos;
                    pos += (len + 3) & ~3;
                }
                File.WriteAllBytes(outFile, o);
            }
        }
        catch (Exception ex) { UiLog.Write($"MkxpFontCatalog: split {Path.GetFileName(ttc)} failed: {ex.Message}"); }
    }

    static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
}
