#nullable enable
using System.Linq;
using System.Text;

namespace RocketRPG.Models;

/// <summary>멀티 참가자 창 제목: "방장 게임 - RocketRPG (참가 중)". 방장이 보낸 게임 이름만 쓰고 경로·계정명은 쓰지 않습니다.</summary>
public static class MultiTitle
{
    public const int MaxGameLength = 60;

    /// <summary>제어 문자를 지우고 공백을 하나로, 너무 길면 자름 (글자 단위)</summary>
    public static string CleanGame(string? game)
    {
        if (string.IsNullOrWhiteSpace(game)) return "";
        var sb = new StringBuilder(game.Length);
        bool space = false;
        foreach (char c in game)
        {
            bool ws = char.IsWhiteSpace(c) || char.IsControl(c);
            if (ws) { if (!space && sb.Length > 0) sb.Append(' '); space = true; continue; }
            sb.Append(c);
            space = false;
        }
        string t = sb.ToString().Trim();
        var runes = t.EnumerateRunes().ToList();
        return runes.Count <= MaxGameLength ? t : string.Concat(runes.Take(MaxGameLength).Select(r => r.ToString())) + "…";
    }

    public static string ForGuest(string appTitle, string? hostGame)
    {
        string game = CleanGame(hostGame);
        return game.Length > 0 ? $"{game} - {appTitle} (참가 중)" : $"{appTitle} (참가 중)";
    }
}
