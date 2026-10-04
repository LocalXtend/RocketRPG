#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RocketRPG.Views;

/// <summary>
/// 마크다운 글(업데이트 내용, 라이선스 고지)을 읽기 좋게 보여 주는 칸. 바탕·테두리·글꼴은 기본 Windows 입력 칸과 같습니다.
/// 제목(#), 목록(-, *, 1.; 들여쓰기로 겹침), 인용(>), 표(| |), 코드(``` / `), 굵게·기울임·취소선, 링크, 가로줄을 지원합니다.
/// 링크는 기본 브라우저로 엽니다.
/// </summary>
internal sealed class MarkdownView : FlowDocumentScrollViewer
{
    static readonly Brush CodeBack = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)));
    static readonly Brush QuoteBar = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)));
    static readonly Brush TableLine = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)));
    static readonly Brush TableHead = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));
    static readonly FontFamily Mono = new("Consolas, Malgun Gothic");

    public MarkdownView()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        IsToolBarVisible = false;
        Background = SystemColors.WindowBrush;
        BorderBrush = SystemColors.ActiveBorderBrush;
        BorderThickness = new Thickness(1);
        Focusable = true;
    }

    /// <summary>보여 줄 마크다운 (바꾸면 맨 위로)</summary>
    public string Markdown
    {
        set
        {
            var doc = Render(value ?? "", FontSize);
            doc.FontFamily = FontFamily;
            doc.FontSize = FontSize;
            doc.Foreground = SystemColors.WindowTextBrush;
            doc.PagePadding = new Thickness(10, 8, 10, 10);
            doc.TextAlignment = TextAlignment.Left;
            Document = doc;
            Dispatcher.BeginInvoke(() => (Template?.FindName("PART_ContentHost", this) as ScrollViewer)?.ScrollToHome());
        }
    }

    // ── 블록 ──

    static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    static readonly Regex ListItem = new(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$");
    static readonly Regex Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$");
    static readonly Regex TableSep = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");

    [ThreadStatic] static double _baseSize;

    public static FlowDocument Render(string markdown, double baseSize = 12)
    {
        _baseSize = baseSize > 0 ? baseSize : 12;
        var doc = new FlowDocument();
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        AddBlocks(doc.Blocks, lines, 0);
        if (doc.Blocks.FirstBlock is { } first) first.Margin = new Thickness(first.Margin.Left, 0, first.Margin.Right, first.Margin.Bottom);
        return doc;
    }

    static void AddBlocks(BlockCollection blocks, IReadOnlyList<string> lines, int depth)
    {
        int i = 0;
        var para = new List<string>();
        void FlushPara()
        {
            if (para.Count == 0) return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            for (int k = 0; k < para.Count; k++)
            {
                if (k > 0) p.Inlines.Add(new LineBreak());
                AddInlines(p.Inlines, para[k].Trim());
            }
            blocks.Add(p);
            para.Clear();
        }

        while (i < lines.Count)
        {
            string line = lines[i];
            string t = line.Trim();
            if (t.Length == 0) { FlushPara(); i++; continue; }

            // 코드 블록
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushPara();
                string fence = t[..3];
                var code = new List<string>();
                for (i++; i < lines.Count && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal); i++) code.Add(lines[i]);
                i++;
                blocks.Add(new Paragraph(new Run(string.Join("\n", code)))
                {
                    FontFamily = Mono, Background = CodeBack, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 8),
                });
                continue;
            }

            // 제목
            var h = Heading.Match(t);
            if (h.Success && line.Length - line.TrimStart().Length < 4)
            {
                FlushPara();
                int level = h.Groups[1].Value.Length;
                double[] sizes = [1.6, 1.35, 1.17, 1.05, 1.0, 0.95];
                var p = new Paragraph { FontWeight = FontWeights.Bold, Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, level <= 2 ? 6 : 4) };
                p.FontSize = _baseSize * sizes[level - 1];
                AddInlines(p.Inlines, h.Groups[2].Value);
                blocks.Add(p);
                if (level <= 2) blocks.Add(new BlockUIContainer(new Border { Height = 1, Background = TableLine, Margin = new Thickness(0, -4, 0, 6) }) { Margin = new Thickness(0) });
                i++;
                continue;
            }

            // 가로줄
            if (Rule.IsMatch(t) && para.Count == 0)
            {
                blocks.Add(new BlockUIContainer(new Border { Height = 1, Background = TableLine, Margin = new Thickness(0, 4, 0, 4) }) { Margin = new Thickness(0, 0, 0, 8) });
                i++;
                continue;
            }

            // 표
            if (t.StartsWith('|') && i + 1 < lines.Count && TableSep.IsMatch(lines[i + 1]))
            {
                FlushPara();
                var rows = new List<string> { t };
                for (i += 2; i < lines.Count && lines[i].Trim().StartsWith('|'); i++) rows.Add(lines[i].Trim());
                blocks.Add(MakeTable(rows));
                continue;
            }

            // 인용
            if (t.StartsWith('>'))
            {
                FlushPara();
                var quoted = new List<string>();
                for (; i < lines.Count && lines[i].TrimStart().StartsWith('>'); i++)
                {
                    string q = lines[i].TrimStart()[1..];
                    quoted.Add(q.StartsWith(' ') ? q[1..] : q);
                }
                var section = new Section { BorderBrush = QuoteBar, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 0, 0, 8), Foreground = SystemColors.GrayTextBrush };
                AddBlocks(section.Blocks, quoted, depth + 1);
                if (section.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
                blocks.Add(section);
                continue;
            }

            // 목록
            var li = ListItem.Match(line);
            if (li.Success)
            {
                FlushPara();
                blocks.Add(MakeList(lines, ref i, depth));
                continue;
            }

            para.Add(line);
            i++;
        }
        FlushPara();
    }

    /// <summary>목록 하나 (같은 들여쓰기의 항목들). 더 들여쓴 항목과 이어지는 줄은 항목 안으로.</summary>
    static List MakeList(IReadOnlyList<string> lines, ref int i, int depth)
    {
        var first = ListItem.Match(lines[i]);
        int indent = first.Groups[1].Value.Replace("\t", "    ").Length;
        bool ordered = char.IsDigit(first.Groups[2].Value[0]);
        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : depth % 2 == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle,
            Margin = new Thickness(0, 0, 0, depth == 0 ? 8 : 0), Padding = new Thickness(22, 0, 0, 0),
        };
        if (ordered && int.TryParse(first.Groups[2].Value.TrimEnd('.', ')'), out int start) && start > 1) list.StartIndex = start;
        while (i < lines.Count)
        {
            var m = ListItem.Match(lines[i]);
            if (!m.Success || m.Groups[1].Value.Replace("\t", "    ").Length != indent) break;
            var body = new List<string> { m.Groups[3].Value };
            int contentIndent = indent + m.Groups[2].Value.Length + 1;
            for (i++; i < lines.Count; i++)
            {
                string next = lines[i];
                if (next.Trim().Length == 0)
                {
                    // 빈 줄 뒤에 더 들여쓴 줄이 오면 같은 항목
                    if (i + 1 < lines.Count && Indent(lines[i + 1]) > indent && lines[i + 1].Trim().Length > 0) { body.Add(""); continue; }
                    break;
                }
                var nm = ListItem.Match(next);
                if (nm.Success && Indent(next) <= indent) break;
                if (!nm.Success && Indent(next) <= indent && body.Count > 0 && (next.TrimStart().StartsWith('#') || next.TrimStart().StartsWith('|') || next.TrimStart().StartsWith('>'))) break;
                body.Add(Indent(next) >= contentIndent ? next[Math.Min(next.Length, contentIndent)..] : next.TrimStart());
            }
            var item = new ListItem();
            // 첫 줄은 항목 글, 나머지(하위 목록 등)는 블록으로
            var lead = new List<string>();
            int k = 0;
            for (; k < body.Count && body[k].Trim().Length > 0 && !ListItem.IsMatch(body[k]); k++) lead.Add(body[k]);
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
            for (int j = 0; j < lead.Count; j++)
            {
                if (j > 0) p.Inlines.Add(new LineBreak());
                AddInlines(p.Inlines, lead[j].Trim());
            }
            item.Blocks.Add(p);
            if (k < body.Count) AddBlocks(item.Blocks, body.Skip(k).ToList(), depth + 1);
            list.ListItems.Add(item);
            // 항목 사이 빈 줄은 건너뜀 (같은 목록이 이어지면)
            int save = i;
            while (i < lines.Count && lines[i].Trim().Length == 0) i++;
            if (i >= lines.Count || !ListItem.IsMatch(lines[i]) || Indent(lines[i]) != indent) { i = save; break; }
        }
        return list;
    }

    static int Indent(string s) => s.TakeWhile(char.IsWhiteSpace).Sum(c => c == '\t' ? 4 : 1);

    static Table MakeTable(List<string> rows)
    {
        static List<string> Cells(string row)
        {
            string r = row.Trim();
            if (r.StartsWith('|')) r = r[1..];
            if (r.EndsWith('|') && !r.EndsWith("\\|", StringComparison.Ordinal)) r = r[..^1];
            var cells = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool code = false;
            for (int k = 0; k < r.Length; k++)
            {
                char c = r[k];
                if (c == '\\' && k + 1 < r.Length && r[k + 1] == '|') { cur.Append('|'); k++; continue; }
                if (c == '`') code = !code;
                if (c == '|' && !code) { cells.Add(cur.ToString().Trim()); cur.Clear(); continue; }
                cur.Append(c);
            }
            cells.Add(cur.ToString().Trim());
            return cells;
        }
        var data = rows.Select(Cells).ToList();
        int cols = data.Max(r => r.Count);
        var table = new Table { CellSpacing = 0, BorderBrush = TableLine, BorderThickness = new Thickness(1, 1, 0, 0), Margin = new Thickness(0, 0, 0, 10) };
        for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        for (int r = 0; r < data.Count; r++)
        {
            var row = new TableRow { Background = r == 0 ? TableHead : null };
            for (int c = 0; c < cols; c++)
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                if (r == 0) p.FontWeight = FontWeights.SemiBold;
                AddInlines(p.Inlines, c < data[r].Count ? data[r][c] : "");
                row.Cells.Add(new TableCell(p) { BorderBrush = TableLine, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(6, 3, 6, 3) });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    // ── 글 안 꾸밈 ──

    // 순서가 중요: 코드 → 링크 → 굵게 → 기울임 → 취소선 → 그냥 주소
    static readonly Regex InlineToken = new(
        @"(?<code>`+)(?<codetext>.+?)\k<code>" +
        @"|\[(?<ltext>[^\]]+)\]\((?<lurl>[^)\s]+)(?:\s+""[^""]*"")?\)" +
        @"|\*\*(?<bold>.+?)\*\*|__(?<bold2>.+?)__" +
        @"|(?<![\w*])\*(?<ital>[^*\s][^*]*?)\*(?![\w*])" +
        @"|~~(?<strike>.+?)~~" +
        @"|<br\s*/?>" +
        @"|(?<url>https?://[^\s<>()]+[^\s<>().,;:!?""'])" +
        @"|\\(?<esc>[\\`*_{}\[\]()#+\-.!|>~])");

    static void AddInlines(InlineCollection target, string text, bool inLink = false)
    {
        int pos = 0;
        foreach (Match m in InlineToken.Matches(text))
        {
            if (m.Index > pos) target.Add(new Run(text[pos..m.Index]));
            pos = m.Index + m.Length;
            if (m.Groups["codetext"].Success)
                target.Add(new Run(m.Groups["codetext"].Value.Trim()) { FontFamily = Mono, Background = CodeBack });
            else if (m.Groups["ltext"].Success && !inLink)
                target.Add(Link(m.Groups["ltext"].Value, m.Groups["lurl"].Value));
            else if (m.Groups["bold"].Success || m.Groups["bold2"].Success)
            {
                var b = new Bold();
                AddInlines(b.Inlines, m.Groups["bold"].Success ? m.Groups["bold"].Value : m.Groups["bold2"].Value, inLink);
                target.Add(b);
            }
            else if (m.Groups["ital"].Success)
            {
                var it = new Italic();
                AddInlines(it.Inlines, m.Groups["ital"].Value, inLink);
                target.Add(it);
            }
            else if (m.Groups["strike"].Success)
            {
                var s = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInlines(s.Inlines, m.Groups["strike"].Value, inLink);
                target.Add(s);
            }
            else if (m.Groups["url"].Success && !inLink) target.Add(Link(m.Groups["url"].Value, m.Groups["url"].Value));
            else if (m.Groups["esc"].Success) target.Add(new Run(m.Groups["esc"].Value));
            else if (m.Groups["ltext"].Success || m.Groups["url"].Success) target.Add(new Run(m.Value));   // 링크 안의 링크는 글자로
            else target.Add(new LineBreak());
        }
        if (pos < text.Length) target.Add(new Run(text[pos..]));
    }

    static Inline Link(string text, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new Run(text);
        var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.ToString() };
        AddInlines(link.Inlines, text, inLink: true);
        link.RequestNavigate += (_, e) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true }); } catch { }
            e.Handled = true;
        };
        return link;
    }

    static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
