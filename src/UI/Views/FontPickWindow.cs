#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace RocketRPG.Views;

/// <summary>
/// 글꼴 고르기 (메모·글상자·메모 설정). 맨 위 '기본'을 고르면 빈 문자열(= 기본 글꼴 따름)을 돌려줍니다.
/// 취소하면 null.
/// </summary>
internal sealed class FontPickWindow : Window
{
    sealed record FontEntry(string Source, string Display);

    static List<FontEntry>? _cache;
    static readonly XmlLanguage Korean = XmlLanguage.GetLanguage("ko-kr");

    readonly ListBox _list = new();
    readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock _preview = new() { Text = "가나다 ABC 123 *기울임* **굵게**", FontSize = 18, Margin = new Thickness(0, 6, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly string _defaultLabel;
    string? _result;

    FontPickWindow(Window? owner, string current, string defaultLabel)
    {
        Owner = owner;
        Title = "글꼴";
        Width = 360;
        Height = 480;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Background = SystemColors.ControlBrush;
        FontFamily = owner?.FontFamily ?? SystemFonts.MessageFontFamily;
        _defaultLabel = defaultLabel;

        var ok = new Button { Content = "확인", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "취소", Width = 75, IsCancel = true };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_preview, Dock.Bottom);
        root.Children.Add(_search);
        root.Children.Add(buttons);
        root.Children.Add(_preview);
        root.Children.Add(_list);
        Content = root;

        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        _list.MouseDoubleClick += (_, _) => Accept();
        _list.SelectionChanged += (_, _) => UpdatePreview();
        _search.TextChanged += (_, _) => Fill(_search.Text);
        Fill("");
        Select(current);
        Loaded += (_, _) => _search.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Down && _search.IsKeyboardFocused) { _list.Focus(); e.Handled = true; } };
    }

    static List<FontEntry> AllFonts() => _cache ??= Fonts.SystemFontFamilies
        .Select(f => new FontEntry(f.Source, f.FamilyNames.TryGetValue(Korean, out var ko) ? ko : f.Source))
        .GroupBy(f => f.Display).Select(g => g.First())
        .OrderBy(f => f.Display, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>글꼴 이름을 한국어 표시 이름으로 (없으면 그대로)</summary>
    public static string DisplayName(string source) =>
        AllFonts().FirstOrDefault(f => string.Equals(f.Source, source, StringComparison.OrdinalIgnoreCase))?.Display ?? source;

    void Fill(string query)
    {
        var items = new List<object> { _defaultLabel };
        items.AddRange(AllFonts()
            .Where(f => query.Length == 0 || f.Display.Contains(query, StringComparison.CurrentCultureIgnoreCase) || f.Source.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Display));
        _list.ItemsSource = items;
    }

    void Select(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) { _list.SelectedIndex = 0; return; }
        var e = AllFonts().FirstOrDefault(f => string.Equals(f.Source, source, StringComparison.OrdinalIgnoreCase));
        _list.SelectedItem = e?.Display ?? source;
        if (_list.SelectedItem != null) _list.ScrollIntoView(_list.SelectedItem);
    }

    string? SelectedSource()
    {
        if (_list.SelectedItem is not string display) return null;
        if (display == _defaultLabel) return "";
        return AllFonts().FirstOrDefault(f => f.Display == display)?.Source ?? display;
    }

    void UpdatePreview()
    {
        string? src = SelectedSource();
        if (string.IsNullOrEmpty(src)) _preview.ClearValue(TextBlock.FontFamilyProperty);
        else _preview.FontFamily = new FontFamily(src);
    }

    void Accept()
    {
        _result = SelectedSource();
        if (_result == null) return;
        DialogResult = true;
    }

    /// <summary>고른 글꼴 이름("" = 기본), 취소하면 null</summary>
    public static string? Ask(Window? owner, string current, string defaultLabel = "기본 (메모 설정의 글꼴)")
    {
        var w = new FontPickWindow(owner, current ?? "", defaultLabel);
        return w.ShowDialog() == true ? w._result : null;
    }
}
