#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>
/// 도구 &gt; 에셋 보기: 지금 내 PC에서 연 게임의 그림·소리·영상·글꼴·데이터 파일을 분류하고 찾아 봅니다.
/// 보기만 합니다: 저장·내보내기·복사·끌어 내기가 없고, 멀티로 아무것도 보내지 않으며, 이 창은 화면 캡처(방송)에 찍히지 않습니다.
/// 큰 게임도 바로 뜨도록 목록은 뒤에서 모으고, 작은 그림(썸네일)은 보이는 줄만 동시에 2개씩 작게 읽습니다. 창을 닫으면 모두 비웁니다.
/// 소리·영상은 이 창 안의 작은 브라우저에서 메모리로 바로 재생합니다 (파일로 꺼내지 않음, 인터넷 사용 안 함).
/// </summary>
internal sealed class AssetViewerWindow : Window
{
    sealed class Row : INotifyPropertyChanged
    {
        public AssetEntry Entry { get; init; } = null!;
        public string Name => Entry.Name;
        public string KindText => KindName(Entry.Kind);
        public string SizeText => FormatSize(Entry.Size);
        public string Where => Entry.Rtp ? "RTP" : Entry.InArchive ? "압축 파일 안" : Entry.Encrypted ? "게임 (암호화)" : "게임";
        ImageSource? _thumb;
        public ImageSource? Thumb { get => _thumb; set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumb))); } }
        public bool ThumbRequested;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    static readonly string AllFolders = "(전체)";
    const string MediaHost = "https://rocketrpg.asset/";

    readonly AssetCatalog _catalog;
    readonly bool _is2k;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _thumbGate = new(2);
    List<Row> _all = new();
    readonly ObservableCollection<Row> _shown = new();
    readonly ListBox _folders = new() { MinWidth = 160 };
    readonly ListView _list = new();
    readonly TextBox _search = new() { Width = 200, Padding = new Thickness(2, 1, 2, 1) };
    readonly ComboBox _kind = new() { Width = 100, Margin = new Thickness(6, 0, 0, 0) };
    readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = SystemColors.GrayTextBrush };
    readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
    readonly TextBlock _fontSample = new() { Text = "가나다라마바사 ABCDE abcde 12345", FontSize = 22, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly Button _play = new() { Content = "재생", MinWidth = 70, Margin = new Thickness(0, 8, 6, 0), Visibility = Visibility.Collapsed };
    readonly Button _stop = new() { Content = "정지", MinWidth = 70, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    readonly Grid _mediaHost = new() { Height = 0 };
    WebView2? _media;
    byte[]? _mediaBytes;
    string _mediaType = "";
    int _mediaToken;
    int _previewSeq;

    public AssetViewerWindow(Window owner, string gameDir, int engine, string gameTitle)
    {
        Owner = owner;
        Title = $"에셋 보기 - {gameTitle}";
        Width = 1000;
        Height = 640;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = SystemColors.ControlBrush;
        FontFamily = owner.FontFamily;
        _catalog = new AssetCatalog(gameDir, engine);
        _is2k = engine is CoreInterop.Engine2000 or CoreInterop.Engine2003;

        // 위: 찾기
        _kind.Items.Add("모든 종류");
        foreach (AssetKind k in Enum.GetValues<AssetKind>()) _kind.Items.Add(KindName(k));
        _kind.SelectedIndex = 0;
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock { Text = "찾기:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(_search);
        top.Children.Add(_kind);
        top.Children.Add(_count);

        // 가운데: 목록 (가상화)
        var grid = new GridView();
        var thumb = new FrameworkElementFactory(typeof(Image));
        thumb.SetBinding(Image.SourceProperty, new Binding(nameof(Row.Thumb)));
        thumb.SetValue(WidthProperty, 32.0);
        thumb.SetValue(HeightProperty, 32.0);
        thumb.SetValue(Image.StretchProperty, Stretch.Uniform);
        grid.Columns.Add(new GridViewColumn { Header = "", Width = 44, CellTemplate = new DataTemplate { VisualTree = thumb } });
        grid.Columns.Add(new GridViewColumn { Header = "이름", Width = 260, DisplayMemberBinding = new Binding(nameof(Row.Name)) });
        grid.Columns.Add(new GridViewColumn { Header = "종류", Width = 70, DisplayMemberBinding = new Binding(nameof(Row.KindText)) });
        grid.Columns.Add(new GridViewColumn { Header = "크기", Width = 80, DisplayMemberBinding = new Binding(nameof(Row.SizeText)) });
        grid.Columns.Add(new GridViewColumn { Header = "위치", Width = 100, DisplayMemberBinding = new Binding(nameof(Row.Where)) });
        _list.View = grid;
        _list.ItemsSource = _shown;
        _list.SelectionMode = SelectionMode.Single;
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetIsDeferredScrollingEnabled(_list, false);
        // 줄이 화면에 들어올 때(처음 뜰 때, 스크롤, 거르기) 그 줄들의 썸네일만 읽음
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => RequestVisibleThumbs()));

        // 오른쪽: 미리보기
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);   // 도트 그림은 흐리지 않게
        var imageBox = new Border
        {
            Background = CheckerBrush(), BorderBrush = SystemColors.ActiveBorderBrush, BorderThickness = new Thickness(1), MinHeight = 200,
            Child = new Grid { Children = { _image, _fontSample } },
        };
        _play.Click += (_, _) => PlayMedia();
        _stop.Click += (_, _) => StopMedia();
        var preview = new StackPanel { Width = 320, Margin = new Thickness(8, 0, 0, 0) };
        preview.Children.Add(imageBox);
        preview.Children.Add(_mediaHost);
        preview.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _play, _stop } });
        preview.Children.Add(_info);
        preview.Children.Add(_note);
        preview.Children.Add(new TextBlock
        {
            Text = "에셋 보기는 이 PC 안에서만 동작합니다. 저장·내보내기는 할 수 없고, 멀티 참가자에게 보내지 않으며 방송·화면 공유에도 이 창은 보이지 않습니다.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(0, 12, 0, 0),
        });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_folders, 0);
        Grid.SetColumn(_list, 1);
        _list.Margin = new Thickness(6, 0, 0, 0);
        body.Children.Add(_folders);
        body.Children.Add(_list);
        body.Children.Add(new ScrollViewer { Content = preview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(body.Children[^1], 2);

        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(body);
        Content = root;

        _search.TextChanged += (_, _) => ApplyFilter();
        _kind.SelectionChanged += (_, _) => ApplyFilter();
        _folders.SelectionChanged += (_, _) => ApplyFilter();
        _list.SelectionChanged += (_, _) => ShowPreview(_list.SelectedItem as Row);
        // 복사·끌어 내기 막기 (목록의 Ctrl+C 등)
        CommandManager.AddPreviewExecutedHandler(this, (_, e) => { if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut) e.Handled = true; });
        SourceInitialized += (_, _) => ExcludeFromCapture();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => Cleanup();
        _count.Text = "목록을 읽는 중...";
    }

    // ── 목록 ──

    async Task LoadAsync()
    {
        var ct = _cts.Token;
        List<AssetEntry> entries;
        try { entries = await Task.Run(() => _catalog.Enumerate(ct), ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            UiLog.Write($"assets: list failed {ex.Message}");
            _count.Text = "목록을 읽지 못했습니다.";
            return;
        }
        if (ct.IsCancellationRequested) return;
        _all = entries.OrderBy(e => e.Rtp).ThenBy(e => e.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                      .Select(e => new Row { Entry = e }).ToList();
        _folders.Items.Add($"{AllFolders} ({_all.Count})");
        foreach (var g in _all.GroupBy(r => r.Entry.Folder).OrderBy(g => g.First().Entry.Rtp).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            _folders.Items.Add(new ListBoxItem { Content = $"{(g.Key.Length == 0 ? "(맨 위)" : g.Key)} ({g.Count()})", Tag = g.Key });
        _folders.SelectedIndex = 0;
        UiLog.Write($"assets: {_all.Count} files ({_all.Count(r => r.Entry.Rtp)} RTP, {_all.Count(r => r.Entry.InArchive)} in archive)");
        ApplyFilter();
    }

    void ApplyFilter()
    {
        string q = _search.Text.Trim();
        string? folder = (_folders.SelectedItem as ListBoxItem)?.Tag as string;
        int kind = _kind.SelectedIndex - 1;
        _shown.Clear();
        foreach (var r in _all)
        {
            if (folder != null && r.Entry.Folder != folder) continue;
            if (kind >= 0 && (int)r.Entry.Kind != kind) continue;
            if (q.Length > 0 && r.Entry.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _shown.Add(r);
        }
        _count.Text = $"{_shown.Count}개";
        Dispatcher.BeginInvoke(RequestVisibleThumbs, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void RequestVisibleThumbs()
    {
        var panel = FindChild<VirtualizingStackPanel>(_list);
        if (panel == null) return;
        foreach (var child in panel.Children)
            if (child is ListViewItem { DataContext: Row r, IsVisible: true }) RequestThumb(r);
    }

    static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is T found) return found;
        }
        return null;
    }

    // ── 썸네일 (보이는 줄만, 동시에 2개, 작게) ──

    void RequestThumb(Row r)
    {
        if (r.ThumbRequested || r.Entry.Kind != AssetKind.Image) return;
        r.ThumbRequested = true;
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            await _thumbGate.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested || !IsRowVisible(r)) { r.ThumbRequested = false; return; }
                var bytes = _catalog.Read(r.Entry);
                var img = bytes == null ? null : DecodeImage(bytes, r.Entry.Name, 32, false);
                if (img != null && !ct.IsCancellationRequested) _ = Dispatcher.BeginInvoke(() => r.Thumb = img);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { UiLog.Write($"assets: thumb {r.Entry.Path} {ex.Message}"); }
            finally { _thumbGate.Release(); }
        }, ct);
    }

    /// <summary>아직 화면에 있는 줄인지 (빠르게 스크롤해 지나간 줄은 읽지 않음)</summary>
    bool IsRowVisible(Row r) => Dispatcher.Invoke(() => _list.ItemContainerGenerator.ContainerFromItem(r) is ListViewItem { IsVisible: true });

    /// <summary>그림 → 화면용 그림. maxSide &gt; 0이면 그 크기로 작게 읽음 (썸네일). paletted2k: 2000/2003 팔레트 0번 = 투명</summary>
    static BitmapSource? DecodeImage(byte[] bytes, string name, int maxSide, bool paletted2k)
    {
        string ext = AssetCatalog.PlainExtension(name);
        BitmapSource? src;
        if (ext == ".xyz" || (paletted2k && ext is ".png" or ".bmp"))
        {
            var t = ext == ".xyz" ? GameAssetSource.DecodeXyzImage(bytes) : GameAssetSource.DecodeImage(bytes, true);
            if (t == null) return null;
            src = BitmapSource.Create(t.W, t.H, 96, 96, PixelFormats.Bgra32, null, t.Px, t.W * 4);
            if (maxSide > 0 && Math.Max(t.W, t.H) > maxSide)
            {
                double s = (double)maxSide / Math.Max(t.W, t.H);
                src = new TransformedBitmap(src, new ScaleTransform(s, s));
            }
        }
        else
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = new MemoryStream(bytes);
            if (maxSide > 0) bi.DecodePixelHeight = maxSide;
            bi.EndInit();
            src = bi;
        }
        src.Freeze();
        return src;
    }

    // ── 미리보기 ──

    async void ShowPreview(Row? r)
    {
        int seq = ++_previewSeq;
        StopMedia();
        _image.Source = null;
        _fontSample.Visibility = Visibility.Collapsed;
        _play.Visibility = _stop.Visibility = Visibility.Collapsed;
        _mediaHost.Height = 0;
        _note.Text = "";
        _mediaBytes = null;
        if (r == null) { _info.Text = ""; return; }
        var e = r.Entry;
        _info.Text = $"{e.Name}\n폴더: {(e.Folder.Length == 0 ? "(맨 위)" : e.Folder)}\n종류: {KindName(e.Kind)} · {FormatSize(e.Size)} · {r.Where}";
        switch (e.Kind)
        {
            case AssetKind.Image:
            {
                var img = await Task.Run(() => { var b = _catalog.Read(e); try { return b == null ? null : DecodeImage(b, e.Name, 0, _is2k); } catch { return null; } });
                if (seq != _previewSeq) return;
                if (img == null) { _note.Text = "이 그림은 읽지 못했습니다."; return; }
                _image.Source = img;
                _image.MaxWidth = Math.Max(img.PixelWidth * 4, 64);
                _image.MaxHeight = Math.Max(img.PixelHeight * 4, 64);
                _info.Text += $"\n크기: {img.PixelWidth} × {img.PixelHeight}";
                break;
            }
            case AssetKind.Audio or AssetKind.Video:
            {
                string ext = AssetCatalog.PlainExtension(e.Name);
                string? type = ext switch
                {
                    ".ogg" => "audio/ogg", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".m4a" => "audio/mp4",
                    ".webm" => "video/webm", ".mp4" => "video/mp4", ".ogv" => "video/ogg", _ => null,
                };
                if (type == null) { _note.Text = $"{ext} 형식은 미리 들을 수 없습니다."; return; }
                _mediaType = type;
                _play.Visibility = _stop.Visibility = Visibility.Visible;
                _note.Text = e.Kind == AssetKind.Video ? "재생을 누르면 여기서 영상이 나옵니다." : "재생을 누르면 소리가 나옵니다. 창을 닫으면 멈춥니다.";
                break;
            }
            case AssetKind.Font when e.FullPath != null:
                try
                {
                    var face = new GlyphTypeface(new Uri(e.FullPath));
                    string family = face.FamilyNames.Values.FirstOrDefault() ?? "";
                    _fontSample.FontFamily = new FontFamily(new Uri(Path.GetDirectoryName(e.FullPath)! + Path.DirectorySeparatorChar), "./#" + family);
                    _fontSample.Visibility = Visibility.Visible;
                    _info.Text += $"\n글꼴 이름: {family}";
                }
                catch { _note.Text = "이 글꼴은 미리 볼 수 없습니다."; }
                break;
            case AssetKind.Font:
                _note.Text = "압축 파일 안의 글꼴은 미리 볼 수 없습니다.";
                break;
            case AssetKind.Data or AssetKind.Script:
                _note.Text = "데이터·스크립트 파일은 내용을 보여 주지 않습니다 (실행하지도 않습니다).";
                break;
            default:
                _note.Text = "미리 볼 수 없는 형식입니다.";
                break;
        }
    }

    // ── 소리·영상: 이 창 안의 작은 브라우저에 메모리로 넘겨 재생 ──

    async void PlayMedia()
    {
        if (_list.SelectedItem is not Row r) return;
        int seq = _previewSeq;
        var bytes = await Task.Run(() => _catalog.Read(r.Entry));
        if (seq != _previewSeq || bytes == null) { if (bytes == null) _note.Text = "이 파일은 읽지 못했습니다."; return; }
        if (!await EnsureMediaView() || seq != _previewSeq) return;
        _mediaBytes = bytes;
        bool video = _mediaType.StartsWith("video/", StringComparison.Ordinal);
        _mediaHost.Height = video ? 180 : 0;
        string url = $"{MediaHost}m/{++_mediaToken}";
        string tag = video ? "video" : "audio";
        _media!.NavigateToString($"<!doctype html><html><body style=\"margin:0;background:#000;overflow:hidden\" oncontextmenu=\"return false\">" +
            $"<{tag} src=\"{url}\" autoplay {(video ? "controls controlsList=\"nodownload noremoteplayback\" disablePictureInPicture" : "")} " +
            $"style=\"width:100%;height:100%\"></{tag}></body></html>");
    }

    void StopMedia()
    {
        _mediaBytes = null;
        try { _media?.NavigateToString("<html><body style=\"background:#000\"></body></html>"); } catch { }
        _mediaHost.Height = 0;
    }

    async Task<bool> EnsureMediaView()
    {
        if (_media?.CoreWebView2 != null) return true;
        try
        {
            _media = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
            _mediaHost.Children.Add(_media);
            string dir = Path.Combine(SettingsService.Root(), "asset_webview2");
            var env = await CoreWebView2Environment.CreateAsync(null, dir);
            await _media.EnsureCoreWebView2Async(env);
            var core = _media.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            // 이 창의 재생 말고는 아무 곳에도 연결하지 않음
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var bytes = _mediaBytes;
                if (e.Request.Uri.StartsWith($"{MediaHost}m/{_mediaToken}", StringComparison.Ordinal) && bytes != null)
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes, false), 200, "OK",
                        $"Content-Type: {_mediaType}\r\nCache-Control: no-store\r\nAccept-Ranges: none");
                else
                    e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            return true;
        }
        catch (Exception ex)
        {
            UiLog.Write($"assets: media view failed {ex.Message}");
            _note.Text = "재생 준비에 실패했습니다.";
            return false;
        }
    }

    // ── 정리 ──

    void Cleanup()
    {
        _cts.Cancel();
        StopMedia();
        try { _media?.Dispose(); } catch { }
        _media = null;
        foreach (var r in _all) r.Thumb = null;
        _all.Clear();
        _shown.Clear();
        _catalog.Dispose();
    }

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>방송·화면 공유·스크린샷에 이 창이 찍히지 않게 (Windows 10 2004 이상)</summary>
    void ExcludeFromCapture()
    {
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11, WDA_MONITOR = 0x1;
        var h = new WindowInteropHelper(this).Handle;
        if (!SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE)) SetWindowDisplayAffinity(h, WDA_MONITOR);
    }

    static string KindName(AssetKind k) => k switch
    {
        AssetKind.Image => "그림", AssetKind.Audio => "소리", AssetKind.Video => "영상", AssetKind.Font => "글꼴",
        AssetKind.Data => "데이터", AssetKind.Script => "스크립트", _ => "기타",
    };

    static string FormatSize(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.0} MB" : b >= 1024 ? $"{b / 1024.0:0} KB" : $"{b} B";

    static Brush CheckerBrush()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) } }));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }
}
