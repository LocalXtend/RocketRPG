#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>
/// 정보 &gt; RocketRPG 정보 / 라이선스: 버전, 만든 사람, RocketRPG 라이선스(MIT), 비공식 호환 프로그램 고지,
/// 함께 배포하는 구성요소와 각 라이선스 원문 (배포 폴더의 licenses\ 에서 읽어 인터넷 없이 볼 수 있음).
/// </summary>
internal sealed class AboutWindow : Window
{
    sealed record Component(string Name, string Version, string License, string[] Texts, string Source, bool Modified, string Note);

    readonly string _dir;
    readonly ListBox _list = new() { Width = 260 };
    readonly TextBox _text = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas, Malgun Gothic"), FontSize = 12, Padding = new Thickness(4),
    };
    readonly TextBlock _head = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    readonly Button _source = new() { Content = "소스·홈페이지 열기", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0) };

    public AboutWindow(Window owner, string version)
    {
        Owner = owner;
        Title = "RocketRPG 정보";
        Width = 900;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = SystemColors.ControlBrush;
        FontFamily = owner.FontFamily;
        _dir = LicensesDir();

        var top = new StackPanel { Margin = new Thickness(12, 12, 12, 8) };
        top.Children.Add(new TextBlock { Text = $"RocketRPG {version}", FontSize = 20, FontWeight = FontWeights.Bold });
        var by = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
        by.Inlines.Add("2026 ⓒ iyu.e — ");
        var link = new Hyperlink(new Run("https://iyu-e.tistory.com/")) { NavigateUri = new Uri("https://iyu-e.tistory.com/") };
        link.RequestNavigate += (_, e) => Open(e.Uri.ToString());
        by.Inlines.Add(link);
        top.Children.Add(by);
        top.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = SystemColors.GrayTextBrush,
            Text = "RocketRPG 자체 코드는 MIT 라이선스입니다. 함께 배포하는 EasyRPG Player(GPL-3.0, RocketRPG 수정본)와 mkxp-z(GPL) 등은 " +
                   "각자의 라이선스를 따르며, 아래에서 원문과 소스 받는 곳을 볼 수 있습니다.\n" +
                   "RocketRPG는 비공식 호환 프로그램입니다. \"RPG Maker\"는 그 권리자(KADOKAWA / Gotcha Gotcha Games)의 상표이며, " +
                   "RocketRPG는 이 회사들과 관계가 없습니다. 게임 파일·RTP·RPG Maker 실행 파일은 포함하지 않습니다.",
        });

        foreach (var c in Load()) _list.Items.Add(new ListBoxItem { Content = $"{c.Name}  ({c.License})", Tag = c, ToolTip = c.Name });
        _list.Items.Insert(0, new ListBoxItem { Content = "고지 전체 (THIRD_PARTY_NOTICES)", Tag = "notices", FontWeight = FontWeights.SemiBold });
        _list.SelectionChanged += (_, _) => ShowSelected();

        var folder = new Button { Content = "라이선스 폴더 열기", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0) };
        folder.Click += (_, _) => { if (Directory.Exists(_dir)) Open(_dir); };
        var close = new Button { Content = "닫기", Width = 75, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        _source.Click += (_, _) => { if (_source.Tag is string url) Open(url); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 8, 12, 12), Children = { _source, folder, close } };

        var right = new DockPanel { Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(_head, Dock.Top);
        right.Children.Add(_head);
        right.Children.Add(_text);
        var body = new DockPanel { Margin = new Thickness(12, 0, 12, 0) };
        DockPanel.SetDock(_list, Dock.Left);
        body.Children.Add(_list);
        body.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;
        Loaded += (_, _) => _list.SelectedIndex = 0;
    }

    /// <summary>배포 폴더의 licenses\ (개발 실행에서는 빌드 출력 폴더)</summary>
    static string LicensesDir()
    {
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        foreach (var d in new[] { Path.Combine(exeDir, "licenses"), Path.Combine(AppContext.BaseDirectory, "licenses") })
            if (Directory.Exists(d)) return d;
        return Path.Combine(exeDir, "licenses");
    }

    List<Component> Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "components.json")));
            return doc.RootElement.GetProperty("components").EnumerateArray().Select(c => new Component(
                c.GetProperty("name").GetString() ?? "", c.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                c.GetProperty("license").GetString() ?? "", c.GetProperty("texts").EnumerateArray().Select(t => t.GetString() ?? "").ToArray(),
                c.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "", c.TryGetProperty("modified", out var m) && m.GetBoolean(),
                c.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "")).ToList();
        }
        catch (Exception ex)
        {
            UiLog.Write($"about: components.json failed {ex.Message}");
            return [];
        }
    }

    void ShowSelected()
    {
        var tag = (_list.SelectedItem as ListBoxItem)?.Tag;
        if (tag is "notices")
        {
            _head.Text = "함께 배포하는 구성요소와 라이선스, RPG Maker 관련 고지";
            _text.Text = ReadText("THIRD_PARTY_NOTICES.md");
            _source.IsEnabled = false;
            return;
        }
        if (tag is not Component c) return;
        _head.Text = $"{c.Name} {c.Version}\n라이선스: {c.License}{(c.Modified ? " · RocketRPG가 수정함" : "")}\n소스: {c.Source}" +
                     (c.Note.Length > 0 ? $"\n{c.Note}" : "");
        _text.Text = c.Texts.Length == 0 ? "(원문은 위 소스 받는 곳의 파일 머리에 있습니다)"
            : string.Join("\n\n──────────────────────────────\n\n", c.Texts.Select(t => $"[{t}]\n\n{ReadText(Path.Combine("texts", t))}"));
        _text.ScrollToHome();
        _source.Tag = c.Source;
        _source.IsEnabled = c.Source.StartsWith("http", StringComparison.Ordinal);
    }

    string ReadText(string relative)
    {
        try { return File.ReadAllText(Path.Combine(_dir, relative)); }
        catch { return $"(파일을 찾지 못했습니다: licenses\\{relative})"; }
    }

    static void Open(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
