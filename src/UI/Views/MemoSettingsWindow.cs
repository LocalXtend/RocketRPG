#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>
/// 설정 > 메모 설정: 메모 기본 글꼴·크기, 새 메모 색, 글상자 기본 글자 색, 노트 배경 색.
/// 기본 글꼴과 배경은 이미 있는 메모에도 바로 적용되고, 메모 색은 새로 만드는 메모부터 적용됩니다.
/// </summary>
internal sealed class MemoSettingsWindow : Window
{
    static readonly double[] Sizes = [11, 12, 13, 14, 16, 18, 20, 24];

    readonly GlobalSettings _settings;
    string _font;
    readonly TextBlock _fontText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ComboBox _size = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox _memoColor = new();
    readonly ComboBox _labelColor = new();
    readonly ComboBox _background = new();

    public MemoSettingsWindow(Window owner, GlobalSettings settings)
    {
        Owner = owner;
        _settings = settings;
        Title = "메모 설정";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = SystemColors.ControlBrush;
        FontFamily = owner.FontFamily;

        _font = settings.MemoFontFamily ?? "";
        UpdateFontText();
        foreach (var s in Sizes) _size.Items.Add(s.ToString("0"));
        _size.SelectedItem = (Sizes.Contains(settings.MemoFontSize) ? settings.MemoFontSize : 13).ToString("0");
        FillColors(_memoColor, NoteAppearance.MemoColors, NoteAppearance.MemoColor);
        FillColors(_labelColor, NoteAppearance.TextColors, NoteAppearance.LabelColor);
        FillColors(_background, NoteAppearance.Backgrounds, NoteAppearance.Background);

        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        void AddRow(string label, UIElement editor)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            Grid.SetRow(l, row);
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            if (editor is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            grid.Children.Add(l);
            grid.Children.Add(editor);
            row++;
        }

        var change = new Button { Content = "바꾸기...", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0) };
        change.Click += (_, _) =>
        {
            string? f = FontPickWindow.Ask(this, _font, "프로그램 글꼴 (기본)");
            if (f == null) return;
            _font = f;
            UpdateFontText();
        };
        var fontRow = new DockPanel();
        DockPanel.SetDock(change, Dock.Right);
        fontRow.Children.Add(change);
        fontRow.Children.Add(_fontText);

        AddRow("메모 기본 글꼴", fontRow);
        AddRow("메모 기본 글자 크기", _size);
        AddRow("새 메모 색", _memoColor);
        AddRow("글상자 기본 글자 색", _labelColor);
        AddRow("노트 배경 색", _background);

        var reset = new Button { Content = "기본값으로", Padding = new Thickness(8, 1, 8, 1) };
        reset.Click += (_, _) =>
        {
            _font = "";
            UpdateFontText();
            _size.SelectedItem = "13";
            SelectColor(_memoColor, NoteAppearance.DefaultMemoColor);
            SelectColor(_labelColor, NoteAppearance.DefaultLabelColor);
            SelectColor(_background, NoteAppearance.DefaultBackground);
        };
        var ok = new Button { Content = "확인", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "취소", Width = 75, IsCancel = true };
        ok.Click += (_, _) => { Save(); DialogResult = true; };
        var buttons = new DockPanel { Margin = new Thickness(12, 4, 12, 12) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { ok, cancel } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(new ContentControl { Content = reset, HorizontalAlignment = HorizontalAlignment.Left });

        var note = new TextBlock
        {
            Text = "글꼴·글자 색·배경은 지금 있는 메모에도 바로 적용됩니다 (메모마다 따로 고른 것은 그대로). 메모 색과 글자 크기는 새로 만드는 메모부터 적용됩니다.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(12, 0, 12, 8)
        };
        Content = new StackPanel { Children = { grid, note, buttons } };
    }

    void UpdateFontText() => _fontText.Text = string.IsNullOrEmpty(_font) ? "프로그램 글꼴 (기본)" : FontPickWindow.DisplayName(_font);

    static void FillColors(ComboBox box, (string Name, string Color)[] colors, string current)
    {
        foreach (var (name, color) in colors)
        {
            var swatch = new Border { Width = 16, Height = 12, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0) };
            try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); } catch { }
            box.Items.Add(new ComboBoxItem { Tag = color, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = name } } } });
        }
        SelectColor(box, current);
    }

    static void SelectColor(ComboBox box, string color)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, color, StringComparison.OrdinalIgnoreCase))
                           ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    void Save()
    {
        _settings.MemoFontFamily = _font;
        _settings.MemoFontSize = double.TryParse(_size.SelectedItem as string, out var s) ? s : 13;
        _settings.MemoColor = Selected(_memoColor);
        _settings.LabelColor = Selected(_labelColor);
        _settings.NotesBackground = Selected(_background);
        NoteAppearance.Apply(_settings);
    }
}
