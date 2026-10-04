#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>보관함 목록의 한 칸</summary>
internal sealed class GalleryItem : INotifyPropertyChanged
{
    public ScreenshotInfo Info { get; }
    public string Name => Path.GetFileNameWithoutExtension(Info.Path);
    public string Tip => $"{Info.Game}\n{Info.Taken:yyyy-MM-dd HH:mm:ss} · {Info.Bytes / 1024:N0} KB";
    BitmapSource? _thumb;
    public BitmapSource? Thumb { get => _thumb; set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumb))); } }
    public GalleryItem(ScreenshotInfo info) => Info = info;
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 스크린샷 보관함: 게임별로 모아 보고, 크게 보고, 하나 또는 한꺼번에 지웁니다. 노트에서 열면 '노트에 넣기'도 됩니다.
/// 윈도우 기본 모양. 썸네일은 뒤에서 작게 디코딩합니다 (창이 바로 뜨도록).
/// </summary>
internal sealed class GalleryWindow : Window
{
    readonly ComboBox _game = new() { Width = 260, Margin = new Thickness(0, 0, 8, 0) };
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(1), BorderBrush = SystemColors.ControlDarkBrush };
    readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 4) };
    readonly ObservableCollection<GalleryItem> _items = new();
    readonly Action<string>? _insert;
    CancellationTokenSource? _thumbCts;

    public GalleryWindow(Action<string>? insertIntoNotes = null, string? preferGame = null)
    {
        _insert = insertIntoNotes;
        Title = insertIntoNotes != null ? "스크린샷 보관함 — 노트에 넣을 스크린샷 고르기" : "스크린샷 보관함";
        Background = SystemColors.ControlBrush;
        Width = 900;
        Height = 620;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        Button Btn(string text, RoutedEventHandler click, string? tip = null)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            b.Click += click;
            return b;
        }
        var bar = new WrapPanel { Margin = new Thickness(8, 8, 8, 4) };
        bar.Children.Add(new TextBlock { Text = "게임:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        bar.Children.Add(_game);
        bar.Children.Add(Btn("크게 보기", (_, _) => ViewSelected(), "선택한 스크린샷 크게 보기 (더블 클릭)"));
        if (_insert != null) bar.Children.Add(Btn("노트에 넣기", (_, _) => InsertSelected(), "선택한 스크린샷을 노트에 넣기 (원본은 보관함에 남음)"));
        bar.Children.Add(Btn("폴더 열기", (_, _) => OpenFolder()));
        bar.Children.Add(Btn("삭제", (_, _) => DeleteSelected(), "선택한 스크린샷 지우기 (Delete)"));
        bar.Children.Add(Btn("모두 지우기", (_, _) => DeleteAllShown(), "지금 보이는 스크린샷을 모두 지우기"));

        // 썸네일 칸
        var tpl = new DataTemplate();
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.WidthProperty, 176.0);
        border.SetValue(Border.MarginProperty, new Thickness(3));
        border.SetBinding(ToolTipProperty, new Binding(nameof(GalleryItem.Tip)));
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetValue(Image.HeightProperty, 110.0);
        img.SetValue(Image.StretchProperty, Stretch.Uniform);
        img.SetBinding(Image.SourceProperty, new Binding(nameof(GalleryItem.Thumb)));
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        label.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0));
        label.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(GalleryItem.Name)));
        stack.AppendChild(img);
        stack.AppendChild(label);
        border.AppendChild(stack);
        tpl.VisualTree = border;
        _list.ItemTemplate = tpl;
        _list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.ItemsSource = _items;
        _list.MouseDoubleClick += (_, e) => { if (e.OriginalSource is FrameworkElement { DataContext: GalleryItem }) { if (_insert != null) InsertSelected(); else ViewSelected(); } };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.Key == Key.Enter) { if (_insert != null) InsertSelected(); else ViewSelected(); e.Handled = true; }
        };
        _list.SelectionChanged += (_, _) => UpdateStatus();

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        var statusBar = new Border { BorderBrush = SystemColors.ControlDarkBrush, BorderThickness = new Thickness(0, 1, 0, 0), Child = _status };
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(statusBar);
        _list.Margin = new Thickness(8, 0, 8, 8);
        root.Children.Add(_list);
        Content = root;

        _game.SelectionChanged += (_, _) => Reload();
        Loaded += (_, _) => LoadGames(preferGame);
        Closed += (_, _) => _thumbCts?.Cancel();
    }

    string? SelectedGame => _game.SelectedItem is ComboBoxItem { Tag: string g } ? g : null;

    void LoadGames(string? prefer)
    {
        var games = ScreenshotStore.Games();
        _game.Items.Clear();
        _game.Items.Add(new ComboBoxItem { Content = $"전체 ({games.Sum(g => g.Count)}장)", Tag = null });
        foreach (var (g, n) in games) _game.Items.Add(new ComboBoxItem { Content = $"{g} ({n}장)", Tag = g });
        string? want = prefer != null ? ScreenshotStore.SafeName(prefer) : null;
        _game.SelectedItem = _game.Items.OfType<ComboBoxItem>().FirstOrDefault(i => want != null && Equals(i.Tag, want)) ?? _game.Items[0];
    }

    void Reload()
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var token = _thumbCts.Token;
        _items.Clear();
        foreach (var s in ScreenshotStore.List(SelectedGame)) _items.Add(new GalleryItem(s));
        UpdateStatus(loading: _items.Count > 0);
        var todo = _items.ToList();
        // 썸네일은 뒤에서 작게 (160px) 디코딩
        Task.Run(() =>
        {
            foreach (var it in todo)
            {
                if (token.IsCancellationRequested) return;
                var thumb = LoadThumb(it.Info.Path);
                Dispatcher.BeginInvoke(() => { if (!token.IsCancellationRequested) it.Thumb = thumb; });
            }
            Dispatcher.BeginInvoke(() => { if (!token.IsCancellationRequested) UpdateStatus(); });
        }, token);
    }

    static BitmapSource? LoadThumb(string path)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.DecodePixelWidth = 160;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    void UpdateStatus(bool loading = false)
    {
        int sel = _list.SelectedItems.Count;
        _status.Text = _items.Count == 0
            ? "스크린샷이 없습니다. 게임 중에 Ctrl+Shift+A(보관함에만) 또는 Ctrl+Shift+S(노트에도)로 찍을 수 있습니다."
            : $"{_items.Count}장{(sel > 0 ? $" · {sel}장 선택" : "")}{(loading ? " · 미리보기 불러오는 중..." : "")}   —   {ScreenshotStore.Root}";
    }

    List<GalleryItem> Selected() => _list.SelectedItems.OfType<GalleryItem>().ToList();

    void ViewSelected()
    {
        var sel = Selected().FirstOrDefault();
        if (sel == null) return;
        int index = _items.IndexOf(sel);
        var viewer = new ScreenshotViewer(_items.Select(i => i.Info.Path).ToList(), index, DeleteOne) { Owner = this, FontFamily = FontFamily };
        viewer.ShowDialog();
    }

    void InsertSelected()
    {
        if (_insert == null) return;
        var sel = Selected();
        if (sel.Count == 0) return;
        foreach (var s in sel) _insert(s.Info.Path);
        Close();
    }

    void OpenFolder()
    {
        string dir = SelectedGame is { } g ? Path.Combine(ScreenshotStore.Root, g) : ScreenshotStore.Root;
        Directory.CreateDirectory(dir);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); } catch { }
    }

    void DeleteSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (MessageBox.Show(this, $"선택한 스크린샷 {sel.Count}장을 지울까요? (노트에 넣은 사본은 남습니다)", "스크린샷 보관함",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ScreenshotStore.Delete(sel.Select(s => s.Info.Path));
        foreach (var s in sel) _items.Remove(s);
        RefreshGameCounts();
    }

    void DeleteAllShown()
    {
        if (_items.Count == 0) return;
        string what = SelectedGame is { } g ? $"'{g}'의 스크린샷 {_items.Count}장" : $"보관함의 스크린샷 {_items.Count}장 전부";
        if (MessageBox.Show(this, $"{what}을 지울까요? 되돌릴 수 없습니다. (노트에 넣은 사본은 남습니다)", "스크린샷 보관함",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ScreenshotStore.Delete(_items.Select(i => i.Info.Path).ToList());
        _items.Clear();
        RefreshGameCounts();
    }

    /// <summary>크게 보기 창에서 지운 경우</summary>
    void DeleteOne(string path)
    {
        ScreenshotStore.Delete([path]);
        var it = _items.FirstOrDefault(i => i.Info.Path == path);
        if (it != null) _items.Remove(it);
        RefreshGameCounts();
    }

    void RefreshGameCounts()
    {
        var games = ScreenshotStore.Games();
        foreach (var ci in _game.Items.OfType<ComboBoxItem>().ToList())
        {
            if (ci.Tag is string g)
            {
                int n = games.FirstOrDefault(x => x.Game == g).Count;
                ci.Content = $"{g} ({n}장)";
            }
            else ci.Content = $"전체 ({games.Sum(x => x.Count)}장)";
        }
        UpdateStatus();
    }

}

/// <summary>스크린샷 크게 보기: ←/→ 넘기기, Delete 지우기, Esc 닫기</summary>
internal sealed class ScreenshotViewer : Window
{
    readonly List<string> _paths;
    readonly Action<string> _delete;
    readonly Image _img = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _info = new() { Margin = new Thickness(8, 4, 8, 4) };
    int _index;

    public ScreenshotViewer(List<string> paths, int index, Action<string> delete)
    {
        _paths = paths;
        _index = Math.Clamp(index, 0, Math.Max(0, paths.Count - 1));
        _delete = delete;
        Background = SystemColors.ControlBrush;
        Width = 1000;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
        var stage = new Border { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), Child = _img };
        var root = new DockPanel();
        DockPanel.SetDock(_info, Dock.Bottom);
        root.Children.Add(_info);
        root.Children.Add(stage);
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Left) Show(_index - 1);
            else if (e.Key == Key.Right) Show(_index + 1);
            else if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Delete) DeleteCurrent();
        };
        Show(_index);
    }

    void Show(int i)
    {
        if (_paths.Count == 0) { Close(); return; }
        _index = Math.Clamp(i, 0, _paths.Count - 1);
        string p = _paths[_index];
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(p);
            b.EndInit();
            b.Freeze();
            _img.Source = b;
            Title = $"{Path.GetFileName(p)} ({_index + 1}/{_paths.Count})";
            _info.Text = $"{b.PixelWidth} × {b.PixelHeight} · ← → 넘기기 · Delete 지우기 · Esc 닫기";
        }
        catch { _img.Source = null; }
    }

    void DeleteCurrent()
    {
        string p = _paths[_index];
        if (MessageBox.Show(this, $"{Path.GetFileName(p)}을(를) 지울까요?", "스크린샷 보관함", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _img.Source = null;
        _delete(p);
        _paths.RemoveAt(_index);
        Show(_index);
    }
}
