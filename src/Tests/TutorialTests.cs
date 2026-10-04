using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// 1.0.0 사용법: 모든 메뉴 기능에 설명이 있는지, 단축키 자리 표시가 실제 단축키인지
public partial class Program
{
    private static void TestTutorialCoversMenus()
    {
        Console.WriteLine("--- Testing tutorial coverage ---");
        string xaml = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "UI", "Views", "MainWindow.xaml"));
        if (!File.Exists(xaml)) xaml = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "UI", "Views", "MainWindow.xaml"));
        if (!File.Exists(xaml)) { Console.WriteLine("[SKIP] tutorial coverage (MainWindow.xaml not found)"); return; }
        string text = File.ReadAllText(xaml);
        // 메뉴 항목(MenuItem)의 Click 처리 함수들
        var handlers = Regex.Matches(text, @"<MenuItem\b[^>]*?\bClick=""([A-Za-z]+)""", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert($"Menu handlers found in MainWindow.xaml ({handlers.Count})", handlers.Count > 40);
        var missing = handlers.Where(h => !TutorialContent.MenuHelp.ContainsKey(h)).ToList();
        Assert("Every menu item has a tutorial description", missing.Count == 0, "missing: " + string.Join(", ", missing));

        // {key:id}는 실제 단축키 id여야 함
        var texts = TutorialContent.AllPages.SelectMany(p => new[] { p.Summary, p.Tip ?? "" }.Concat(p.Steps)).Concat(TutorialContent.MenuHelp.Values);
        var ids = texts.SelectMany(t => Regex.Matches(t, @"\{key:([A-Za-z]+)\}").Select(m => m.Groups[1].Value)).Distinct().ToList();
        var unknown = ids.Where(id => HotkeyManager.DefaultHotkeys.All(h => h.Id != id)).ToList();
        Assert("Tutorial shortcut placeholders name real hotkeys", unknown.Count == 0, string.Join(", ", unknown));
        Assert("Shortcut placeholder shows the current key", TutorialContent.ApplyKeys("{key:MultiChat}") == HotkeyManager.MenuText(HotkeyManager.GestureOf("MultiChat")));
        Assert("Tutorial pages have unique ids", TutorialContent.AllPages.GroupBy(p => p.Id).All(g => g.Count() == 1));
        Assert("Basic sections are present (start, name, multi, chat/ping)",
            new[] { "start-library", "start-open", "name", "multi-join", "chat", "ping" }.All(id => TutorialContent.AllPages.Any(p => p.Id == id)));
    }
}
