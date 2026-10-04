#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RocketRPG.Models;

/// <summary>게임 실행에 필요한데 PC에 없는 것 (알림 막대에 표시)</summary>
public sealed record GameIssue(string Key, string Message, string? ActionText = null, string? ActionUrl = null);

/// <summary>
/// 게임을 제대로 돌리는 데 필요한 것이 빠졌는지 미리 살펴 쉬운 말로 알려 줍니다 (RTP, 게임이 쓰는 글꼴).
/// 실행 중 오류 문장도 원인을 짐작할 수 있으면 풀어서 설명합니다.
/// </summary>
public static class GameIssueAdvisor
{
    public const string RtpUrl = "https://www.rpgmakerweb.com/run-time-package";

    // RocketRPG가 알아서 비슷한 글꼴로 바꿔 주는 기본 글꼴 (설치 안내 불필요)
    static readonly HashSet<string> Substituted = new(StringComparer.OrdinalIgnoreCase)
    {
        "Arial", "Arial Black", "Verdana", "Tahoma", "Times New Roman", "Courier New", "Georgia",
        "MS Gothic", "MS PGothic", "MS UI Gothic", "MS Mincho", "MS PMincho", "Meiryo", "UmePlus Gothic", "VL Gothic", "VL PGothic",
        "Gulim", "GulimChe", "Dotum", "DotumChe", "Batang", "BatangChe", "Gungsuh", "Malgun Gothic",
        "굴림", "굴림체", "돋움", "돋움체", "바탕", "바탕체", "궁서", "맑은 고딕", "나눔고딕", "NanumGothic",
        "ＭＳ ゴシック", "ＭＳ Ｐゴシック", "ＭＳ 明朝", "ＭＳ Ｐ明朝", "メイリオ"
    };

    public static List<GameIssue> Check(string gameDir, int engine)
    {
        var issues = new List<GameIssue>();
        try
        {
            switch (engine)
            {
                case CoreInterop.EngineXp or CoreInterop.EngineVx or CoreInterop.EngineAce:
                    if (RtpResolver.MissingRgssRtp(gameDir, engine) is { } rtp)
                        issues.Add(new GameIssue("rtp", $"이 게임은 {EngineName(engine)} RTP('{rtp}')가 필요한데 찾지 못했습니다. RTP를 설치하거나, 이미 있으면 설정 > RTP 폴더 지정에서 골라 주세요.",
                            "RTP 받기", RtpUrl));
                    var dirs = new List<string> { Path.Combine(gameDir, "Fonts") };
                    dirs.AddRange(RtpResolver.ResolveRgss(gameDir, engine).Select(r => Path.Combine(r, "Fonts")));
                    string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                    dirs.Add(Path.Combine(exeDir, "runtimes", "mkxp-z", "Fonts"));
                    var missing = MkxpFontCatalog.DeclaredFontNames(gameDir, engine)
                        .Where(f => !Substituted.Contains(f) && !MkxpFontCatalog.IsFontAvailable(f, dirs))
                        .Take(3).ToList();
                    foreach (var f in missing)
                        issues.Add(new GameIssue($"font:{f}", $"이 게임은 '{f}' 글꼴을 씁니다. 글꼴을 설치하면 글자가 원래 모양으로 나옵니다. " +
                            (MkxpFontCatalog.PickSubstitute(f) is { } sub ? $"(지금은 비슷한 '{sub}' 글꼴로 대신 보여 줍니다)" : "(설치 전에는 기본 글꼴로 보입니다)"),
                            "글꼴 찾아보기", "https://www.google.com/search?q=" + Uri.EscapeDataString($"{f} 글꼴 다운로드")));
                    break;
                case CoreInterop.EngineMv or CoreInterop.EngineMz:
                    // Windows 11에는 기본으로 있지만 Windows 10에는 없을 수 있습니다.
                    if (!WebView2Available())
                        issues.Add(new GameIssue("webview2", "MV/MZ 게임을 실행하려면 'Microsoft Edge WebView2 런타임'이 필요합니다. 설치한 뒤 게임을 다시 실행해 주세요.",
                            "WebView2 받기", "https://go.microsoft.com/fwlink/p/?LinkId=2124703"));
                    break;
                case CoreInterop.Engine2000 or CoreInterop.Engine2003:
                    if (RtpResolver.Missing2kRtp(gameDir, engine == CoreInterop.Engine2003))
                        issues.Add(new GameIssue("rtp", $"이 게임은 {EngineName(engine)} RTP가 필요한데 찾지 못했습니다. 게임은 실행하지만 그림·소리가 빠질 수 있습니다. RTP를 설치하거나, 이미 있으면 설정 > RTP 폴더 지정에서 골라 주세요.",
                            "RTP 받기", RtpUrl));
                    break;
            }
        }
        catch (Exception ex) { UiLog.Write($"GameIssueAdvisor: check failed {ex.Message}"); }
        return issues;
    }

    /// <summary>엔진 오류 문장에서 원인을 짐작해 쉬운 설명 한 줄을 돌려줍니다 (모르면 null).</summary>
    public static string? Explain(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        string e = error;
        var file = Regex.Match(e, @"(?:No such file or directory|Unable to find|Cannot find|not found)[^\n]*?((?:Graphics|Audio|Fonts|Movies|Data)[/\\][^\s'"",)]+)", RegexOptions.IgnoreCase);
        if (file.Success)
            return $"게임 파일 '{file.Groups[1].Value}'을(를) 찾지 못했습니다. RTP가 필요한 게임이면 RTP를 설치하고, 아니면 게임 파일이 모두 있는지 확인해 주세요.";
        var font = Regex.Match(e, @"font[^\n]*?['""]([^'""]{2,40})['""]", RegexOptions.IgnoreCase);
        if (font.Success && e.Contains("font", StringComparison.OrdinalIgnoreCase))
            return $"'{font.Groups[1].Value}' 글꼴이 필요합니다. 글꼴을 설치한 뒤 다시 실행해 주세요.";
        var dll = Regex.Match(e, @"([\w\-]+\.dll)", RegexOptions.IgnoreCase);
        if (dll.Success && (e.Contains("Win32API", StringComparison.OrdinalIgnoreCase) || e.Contains("LoadLibrary", StringComparison.OrdinalIgnoreCase)))
            return $"게임 전용 부품 '{dll.Groups[1].Value}'을(를) 불러오지 못했습니다. 이 부품을 쓰는 기능(동영상, 특수 효과 등)은 동작하지 않을 수 있습니다.";
        if (e.Contains("RTP", StringComparison.Ordinal))
            return "RTP(기본 그림·소리 모음)가 필요한 게임입니다. RTP를 설치한 뒤 다시 실행해 주세요.";
        return null;
    }

    public static bool WebView2Available()
    {
        try { return !string.IsNullOrEmpty(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch { return false; }
    }

    static string EngineName(int e) => e switch
    {
        CoreInterop.Engine2000 => "RPG Maker 2000", CoreInterop.Engine2003 => "RPG Maker 2003",
        CoreInterop.EngineXp => "RPG Maker XP", CoreInterop.EngineVx => "RPG Maker VX", _ => "RPG Maker VX Ace"
    };
}
