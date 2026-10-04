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

        // 메뉴 그림의 경로가 실제 메뉴에 있고, 그림의 상태(방 밖·방장·참가자)에서 보이는 항목인지
        var doc = System.Xml.Linq.XDocument.Parse(text);
        System.Xml.Linq.XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var mainMenu = doc.Descendants(wpf + "Menu").First(e => (string?)e.Attribute(x + "Name") == "MainMenu");
        static string Clean(string? h) => Regex.Replace(h ?? "", @"\(_[A-Za-z]\)", "").Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_").Replace("...", "").Trim();
        var pictures = TutorialContent.AllPages.SelectMany(p => new[] { p.Picture, p.Result }.Concat(p.More ?? []))
            .Where(pic => pic is { Kind: "Menu" }).Select(pic => pic!).ToList();
        Assert($"Tutorial has menu pictures ({pictures.Count})", pictures.Count > 5);
        foreach (var pic in pictures)
        {
            var level = mainMenu;
            string where = "";
            for (int i = 0; i < pic.Args.Length && level != null; i++)
            {
                string want = pic.Args[i];
                // 참가자 목록은 방에 들어가야 생기는 메뉴라 그림은 예시 참가자를 씀
                if ((string?)level.Attribute(x + "Name") == "MultiMembersMenu") { where = ""; break; }
                var hit = level.Elements(wpf + "MenuItem").FirstOrDefault(e =>
                {
                    string? name = (string?)e.Attribute(x + "Name");
                    bool visible = pic.State != null && MultiMenuRules.Visible(name, pic.State) is bool rule ? rule : (string?)e.Attribute("Visibility") != "Collapsed";
                    return visible && Clean((string?)e.Attribute("Header")).StartsWith(want, StringComparison.Ordinal);
                });
                if (hit == null) { where = $"'{want}' missing"; break; }
                level = hit;
            }
            Assert($"Menu picture path exists ({string.Join(" > ", pic.Args)}, {pic.State ?? "now"})", where.Length == 0, where);
        }
        Assert("Multi menu rules: host sees dissolve, guest sees leave, outside sees create",
            MultiMenuRules.Visible("MultiDissolveItem", MultiMenuRules.Host) == true && MultiMenuRules.Visible("MultiLeaveItem", MultiMenuRules.Host) == false &&
            MultiMenuRules.Visible("MultiLeaveItem", MultiMenuRules.Guest) == true && MultiMenuRules.Visible("MultiControlItem", MultiMenuRules.Guest) == false &&
            MultiMenuRules.Visible("MultiCreateItem", MultiMenuRules.Outside) == true && MultiMenuRules.Visible("MultiChatItem", MultiMenuRules.Outside) == false);
    }
}
