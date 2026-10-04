#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace RocketRPG.Models;

[Flags]
public enum NoteStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strike = 8
}

/// <summary>
/// 메모 서식 (마크다운 일부): ***굵게+기울임***, **굵게**, *기울임*, _밑줄_, ~~취소선~~.
/// 글을 쓰는 동안은 기호 그대로 보이고, 다 쓰고 나오면 이 규칙으로 서식을 입혀 보여 줍니다.
/// 규칙: 여는 기호 바로 뒤와 닫는 기호 바로 앞은 공백이 아니어야 하고, 같은 줄 안에서만 닫힙니다.
/// _밑줄_ 은 단어 안(snake_case 같은 것)에서는 서식이 되지 않습니다. \* 처럼 앞에 \ 를 쓰면 기호 그대로.
/// </summary>
public static class NoteMarkup
{
    public readonly record struct Segment(string Text, NoteStyle Style);

    static readonly (string mark, NoteStyle style)[] Marks =
    [
        ("***", NoteStyle.Bold | NoteStyle.Italic),
        ("**", NoteStyle.Bold),
        ("~~", NoteStyle.Strike),
        ("*", NoteStyle.Italic),
        ("_", NoteStyle.Underline),
    ];

    public static List<Segment> Parse(string text)
    {
        var result = new List<Segment>();
        ParseInto(text ?? "", NoteStyle.None, result);
        // 같은 서식이 이어진 조각은 합칩니다
        var merged = new List<Segment>();
        foreach (var s in result)
        {
            if (s.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].Style == s.Style) merged[^1] = new Segment(merged[^1].Text + s.Text, s.Style);
            else merged.Add(s);
        }
        return merged;
    }

    static void ParseInto(string text, NoteStyle style, List<Segment> output)
    {
        var plain = new StringBuilder();
        void Flush() { if (plain.Length > 0) { output.Add(new Segment(plain.ToString(), style)); plain.Clear(); } }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] is '*' or '_' or '~' or '\\'))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }
            bool matched = false;
            foreach (var (mark, markStyle) in Marks)
            {
                if (!Matches(text, i, mark)) continue;
                int close = FindClose(text, i, mark);
                if (close < 0) continue;
                Flush();
                string inner = text.Substring(i + mark.Length, close - i - mark.Length);
                ParseInto(inner, style | markStyle, output);
                i = close + mark.Length;
                matched = true;
                break;
            }
            if (matched) continue;
            // 같은 기호가 여러 개 이어진 경우(예: "****")는 통째로 글자로
            plain.Append(c);
            i++;
        }
        Flush();
    }

    static bool Matches(string text, int i, string mark) =>
        i + mark.Length <= text.Length && string.CompareOrdinal(text, i, mark, 0, mark.Length) == 0;

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    /// <summary>i 위치의 여는 기호에 맞는 닫는 기호 위치 (없으면 -1)</summary>
    static int FindClose(string text, int i, string mark)
    {
        int start = i + mark.Length;
        if (start >= text.Length || char.IsWhiteSpace(text[start])) return -1;
        // 더 긴 같은 기호의 일부면 여기서 열지 않음 (예: "**" 앞에서 "*")
        if (start < text.Length && text[start] == mark[0] && mark.Length < 3) return -1;
        char m = mark[0];
        if (m == '_' && i > 0 && IsWordChar(text[i - 1])) return -1;
        for (int j = start + 1; j + mark.Length <= text.Length; j++)
        {
            if (text[j] == '\n') return -1;
            if (text[j] == '\\') { j++; continue; }
            if (!Matches(text, j, mark)) continue;
            if (char.IsWhiteSpace(text[j - 1])) continue;
            int after = j + mark.Length;
            // 닫는 기호 바로 뒤에 같은 기호가 더 있으면 (예: "**굵게***") 그 끝까지 밀어 줍니다
            if (after < text.Length && text[after] == m && mark.Length < 3) continue;
            if (m == '_' && after < text.Length && IsWordChar(text[after])) continue;
            return j;
        }
        return -1;
    }
}

/// <summary>노트 모양 기본값 (설정 > 메모 설정). 바뀌면 열린 노트가 다시 그립니다.</summary>
public static class NoteAppearance
{
    public const string DefaultBackground = "#FFFAFAFA";
    public const string DefaultMemoColor = "#FFFFF4B0";
    public const string DefaultLabelColor = "#FF1E1E1E";

    public static string FontFamily { get; private set; } = "";      // "" = 프로그램 글꼴
    public static double FontSize { get; private set; } = 13;
    public static string MemoColor { get; private set; } = DefaultMemoColor;
    public static string Background { get; private set; } = DefaultBackground;
    public static string LabelColor { get; private set; } = DefaultLabelColor;

    public static event Action? Changed;

    public static void Apply(GlobalSettings s)
    {
        FontFamily = s.MemoFontFamily ?? "";
        FontSize = s.MemoFontSize is >= 8 and <= 72 ? s.MemoFontSize : 13;
        MemoColor = string.IsNullOrWhiteSpace(s.MemoColor) ? DefaultMemoColor : s.MemoColor;
        Background = string.IsNullOrWhiteSpace(s.NotesBackground) ? DefaultBackground : s.NotesBackground;
        LabelColor = string.IsNullOrWhiteSpace(s.LabelColor) ? DefaultLabelColor : s.LabelColor;
        Changed?.Invoke();
    }

    /// <summary>메모지 색 (포스트잇)</summary>
    public static readonly (string Name, string Color)[] MemoColors =
    [
        ("노랑", "#FFFFF4B0"), ("초록", "#FFD6F5C4"), ("분홍", "#FFFFD6E0"), ("파랑", "#FFCFE6FF"),
        ("주황", "#FFFFE0B8"), ("보라", "#FFE6DAFF"), ("회색", "#FFE9E9E9"), ("흰색", "#FFFFFFFF")
    ];

    /// <summary>글상자 글자 색</summary>
    public static readonly (string Name, string Color)[] TextColors =
    [
        ("검정", "#FF1E1E1E"), ("회색", "#FF6E6E6E"), ("빨강", "#FFD32F2F"), ("주황", "#FFE67E00"),
        ("초록", "#FF2E7D32"), ("파랑", "#FF1565C0"), ("보라", "#FF6A1B9A"), ("흰색", "#FFFFFFFF")
    ];

    /// <summary>노트 배경 색</summary>
    public static readonly (string Name, string Color)[] Backgrounds =
    [
        ("밝은 회색 (기본)", DefaultBackground), ("흰색", "#FFFFFFFF"), ("크림", "#FFFBF6E9"),
        ("연한 파랑", "#FFEEF4FB"), ("연한 초록", "#FFEEF6EC"), ("어두운 색", "#FF2B2B2B")
    ];
}
