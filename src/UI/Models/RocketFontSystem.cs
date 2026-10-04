using System.Linq;
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace RocketRPG.Models;

public static class RocketFontSystem
{
    public const string DefaultKoreanFont = "Malgun Gothic";

    /// <summary>
    /// Resolves the bundled GeneralUser GS SoundFont path.
    /// Checks runtimes/soundfonts/ across application base directory and development root.
    /// </summary>
    public static string? ResolveSoundFontPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? baseDir;
        string cwd = Directory.GetCurrentDirectory();

        string[] candidates =
        [
            Path.Combine(exeDir, "runtimes", "soundfonts", "GeneralUser_GS.sf2"),
            Path.Combine(baseDir, "runtimes", "soundfonts", "GeneralUser_GS.sf2"),
            Path.Combine(cwd, "runtimes", "soundfonts", "GeneralUser_GS.sf2"),
            Path.Combine(baseDir, "..", "..", "runtimes", "soundfonts", "GeneralUser_GS.sf2"),
            Path.Combine(baseDir, "..", "..", "..", "runtimes", "soundfonts", "GeneralUser_GS.sf2")
        ];

        foreach (var p in candidates)
        {
            try
            {
                string full = Path.GetFullPath(p);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Maps a font family name to an existing Windows font file path (.ttf/.ttc).
    /// </summary>
    public static string ResolveFontPath(string? familyName, bool isBold = false)
    {
        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string name = (familyName ?? "").Trim();

        if (string.Equals(name, "Malgun Gothic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "맑은 고딕", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(name))
        {
            string boldFile = Path.Combine(fontsDir, "malgunbd.ttf");
            string regularFile = Path.Combine(fontsDir, "malgun.ttf");
            if (isBold && File.Exists(boldFile)) return boldFile;
            if (File.Exists(regularFile)) return regularFile;
        }

        if (string.Equals(name, "Gulim", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "굴림", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Dotum", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "돋움", StringComparison.OrdinalIgnoreCase))
        {
            string gulim = Path.Combine(fontsDir, "gulim.ttc");
            if (File.Exists(gulim)) return gulim;
        }

        if (string.Equals(name, "Batang", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "바탕", StringComparison.OrdinalIgnoreCase))
        {
            string batang = Path.Combine(fontsDir, "batang.ttc");
            if (File.Exists(batang)) return batang;
        }

        if (string.Equals(name, "Arial", StringComparison.OrdinalIgnoreCase))
        {
            string arial = isBold ? Path.Combine(fontsDir, "arialbd.ttf") : Path.Combine(fontsDir, "arial.ttf");
            if (File.Exists(arial)) return arial;
        }

        // WPF 글꼴 목록에서 실제 파일 찾기: 사용자 전용으로 설치한 글꼴(%LOCALAPPDATA%\Microsoft\Windows\Fonts)도 포함.
        // (예전에는 C:\Windows\Fonts만 찾아, 사용자 설치 글꼴을 고르면 조용히 맑은 고딕으로 바뀌었음)
        if (TryResolveInstalledFont(name, isBold) is { } installed) return installed;

        // Generic search in C:\Windows\Fonts
        try
        {
            string simple = name.Replace(" ", "");
            string candTtf = Path.Combine(fontsDir, $"{simple}.ttf");
            if (File.Exists(candTtf)) return candTtf;

            var files = Directory.GetFiles(fontsDir, $"*{simple}*.tt*");
            if (files.Length > 0) return files[0];
        }
        catch { }

        // Fallback to malgun.ttf
        UiLog.Write($"RocketFontSystem: font '{name}' not found, using Malgun Gothic");
        string defaultPath = Path.Combine(fontsDir, "malgun.ttf");
        return File.Exists(defaultPath) ? defaultPath : "malgun.ttf";
    }

    /// <summary>설치된 글꼴 이름(한/영 어느 이름이든)으로 글꼴 파일 경로를 찾습니다. 없으면 null.</summary>
    public static string? TryResolveInstalledFont(string name, bool isBold)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var family = new System.Windows.Media.FontFamily(name);
            var typeface = new System.Windows.Media.Typeface(family, System.Windows.FontStyles.Normal,
                isBold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyph) || glyph.FontUri == null || !glyph.FontUri.IsFile) return null;
            // 이름이 없는 글꼴은 WPF가 대체 글꼴을 돌려줄 수 있으므로, 실제 이름이 맞는지 확인합니다.
            bool matches = glyph.FamilyNames.Values.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)) ||
                           glyph.Win32FamilyNames.Values.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase));
            string path = glyph.FontUri.LocalPath;
            return matches && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns mkxp.json 'fontSub' substitutions in "Original>Replacement" syntax.
    /// </summary>
    public static List<string> GetMkxpFontSubstitutions(string? targetFontFamily)
    {
        string target = string.IsNullOrWhiteSpace(targetFontFamily) ? DefaultKoreanFont : targetFontFamily.Trim();
        var list = new List<string>
        {
            $"Arial>{target}",
            $"Liberation Sans>{target}",
            $"Gulim>{target}",
            $"굴림>{target}",
            $"Dotum>{target}",
            $"돋움>{target}",
            $"Batang>{target}",
            $"바탕>{target}",
            $"NanumGothic>{target}",
            $"나눔고딕>{target}",
            $"MS Gothic>{target}",
            $"MS Mincho>{target}",
            $"MS PGothic>{target}",
            $"MS PMincho>{target}",
            $"Times New Roman>{target}",
            $"Tahoma>{target}",
            $"Verdana>{target}"
        };

        if (!string.Equals(target, "맑은 고딕", StringComparison.OrdinalIgnoreCase))
        {
            list.Add($"Arial>맑은 고딕");
            list.Add($"Gulim>맑은 고딕");
            list.Add($"굴림>맑은 고딕");
            list.Add($"Dotum>맑은 고딕");
            list.Add($"돋움>맑은 고딕");
            list.Add($"Batang>맑은 고딕");
            list.Add($"바탕>맑은 고딕");
            list.Add($"MS Gothic>맑은 고딕");
            list.Add($"MS PGothic>맑은 고딕");
            list.Add($"Liberation Sans>맑은 고딕");
        }

        return list;
    }

    /// <summary>
    /// Returns a Ruby snippet to set default font, size, and boldness in mkxp-z RGSS runtime.
    /// </summary>
    public static string GetRubyFontSnippet(string? targetFontFamily, int fontSize = 0, bool isBold = false)
    {
        string target = string.IsNullOrWhiteSpace(targetFontFamily) ? DefaultKoreanFont : targetFontFamily.Trim();
        var lines = new List<string>
        {
            "begin",
            $"  Font.default_name = ['{target}', '맑은 고딕', '{DefaultKoreanFont}', 'Gulim', '굴림', 'Dotum', '돋움', 'Arial']"
        };
        if (fontSize > 0)
        {
            lines.Add($"  Font.default_size = {fontSize}");
        }
        if (isBold)
        {
            lines.Add("  Font.default_bold = true");
        }
        lines.Add("rescue => e");
        lines.Add("end");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Returns CLI arguments for EasyRPG Player font replacement.
    /// </summary>
    public static string GetEasyRpgFontArgs(string? targetFontFamily, int fontSize = 0, bool isBold = false)
    {
        string fontPath = ResolveFontPath(targetFontFamily, isBold).Replace('\\', '/');
        int size = fontSize > 0 ? fontSize : 12;
        return $"--font1 \"{fontPath}\" --font1-size {size} --font2 \"{fontPath}\" --font2-size {size}";
    }

    /// <summary>
    /// Returns JavaScript snippet for MV / MZ WebView2 font replacement.
    /// </summary>
    public static string GetWebViewFontScript(string? targetFontFamily, int fontSize = 0, bool isBold = false)
    {
        string target = string.IsNullOrWhiteSpace(targetFontFamily) ? DefaultKoreanFont : targetFontFamily.Trim();
        string sizeJs = fontSize > 0 ? $"Bitmap.defaultFontSize = {fontSize};" : "";
        string sizeCss = fontSize > 0 ? $"font-size: {fontSize}px !important;" : "";
        string boldCss = isBold ? "font-weight: bold !important;" : "";

        return $@"
(function() {{
  try {{
    if (typeof Bitmap !== 'undefined') {{
      Bitmap.defaultFontFace = ""'{target}', '{DefaultKoreanFont}', 'Malgun Gothic', sans-serif"";
      {sizeJs}
    }}
    var style = document.getElementById('rocket_font_override');
    if (!style) {{
      style = document.createElement('style');
      style.id = 'rocket_font_override';
      document.head.appendChild(style);
    }}
    style.innerHTML = ""* {{ font-family: '{target}', '{DefaultKoreanFont}', 'Malgun Gothic', sans-serif !important; {sizeCss} {boldCss} }}"";
    if (typeof Graphics !== 'undefined' && Graphics._updateRealScale) {{
      Graphics._updateRealScale();
    }}
  }} catch(e) {{}}
}})();";
    }
}
