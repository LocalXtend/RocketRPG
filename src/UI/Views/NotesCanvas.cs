#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>
/// 게임 노트의 무한 캔버스. 메모(글)와 이미지/GIF를 자유롭게 배치합니다.
/// 최적화: 확대/이동은 RenderTransform만 바꾸고, 보이는 영역 밖의 항목은 접어 두며(그리지 않음),
/// 이미지는 표시 크기에 맞춰 디코딩하고, GIF는 화면에 보이는 동안에만 움직입니다.
/// 조작: 휠 = 확대/축소, 오른쪽(또는 가운데) 버튼 끌기 = 이동, 빈 곳 더블 클릭 = 메모, 파일 끌어 놓기/Ctrl+V = 이미지.
/// </summary>
public sealed class NotesCanvas : Border
{
    const double MinZoom = 0.2, MaxZoom = 4.0, GridStep = 32;

    readonly Canvas _surface = new() { ClipToBounds = false };
    readonly ScaleTransform _scale = new();
    readonly TranslateTransform _pan = new();
    DrawingBrush _gridBrush;
    readonly Dictionary<string, NoteItemView> _views = new();

    NotesStore? _store;
    NotePage? _page;
    NoteItemView? _selected;
    Point? _panStart;
    double _panX0, _panY0;
    bool _panMoved;

    /// <summary>항목이 바뀌었을 때 (저장 예약용)</summary>
    public event Action? Changed;

    bool _readOnly;

    /// <summary>읽기 전용 (멀티: 방장이 노트 고치기를 잠갔을 때 참가자). 보기·확대·이동만 됩니다.</summary>
    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            if (_readOnly == value) return;
            _readOnly = value;
            ContextMenu = BuildBackgroundMenu();
            if (_page != null) Show(_store, _page);   // 항목 메뉴·손잡이를 다시 만듦
        }
    }

    /// <summary>사용자가 지금 이 항목을 다루는 중 (글 쓰기, 끌기, 크기 바꾸기) — 멀티 동기화가 덮어쓰지 않게</summary>
    public bool IsBusy(string id) => _views.TryGetValue(id, out var v) && v.IsBusy;

    /// <summary>
    /// 멀티 동기화: 바뀐 항목만 다시 그립니다 (페이지의 항목 객체가 바뀌었거나 force). 다루는 중인 항목은 건드리지 않습니다.
    /// </summary>
    public void RefreshItems(IEnumerable<string> ids, bool force = false)
    {
        if (_page == null) return;
        foreach (var id in ids.Distinct().ToList())
        {
            var item = _page.Items.FirstOrDefault(i => i.Id == id);
            if (_views.TryGetValue(id, out var v))
            {
                if (v.IsBusy || (!force && item != null && ReferenceEquals(v.Item, item))) continue;
                _surface.Children.Remove(v.Root);
                v.Dispose();
                _views.Remove(id);
                if (_selected == v) _selected = null;
            }
            if (item != null && !_views.ContainsKey(id)) AddView(item);
        }
        UpdateVisibility();
    }

    public NotesCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        AllowDrop = true;
        _surface.RenderTransform = new TransformGroup { Children = { _scale, _pan } };
        _gridBrush = MakeGridBrush();
        Background = _gridBrush;
        Child = _surface;

        MouseWheel += OnWheel;
        PreviewMouseRightButtonDown += OnPanStart;
        PreviewMouseDown += (s, e) => { if (e.ChangedButton == MouseButton.Middle) OnPanStart(s, e); };
        PreviewMouseMove += OnPanMove;
        PreviewMouseUp += OnPanEnd;
        LostMouseCapture += (_, _) => _panStart = null;
        MouseLeftButtonDown += OnBackgroundClick;
        SizeChanged += (_, _) => UpdateVisibility();
        IsVisibleChanged += (_, _) => UpdateVisibility();
        Drop += OnDrop;
        KeyDown += OnKeyDown;
        ContextMenu = BuildBackgroundMenu();
        NoteAppearance.Changed += OnAppearanceChanged;
    }

    /// <summary>메모 설정이 바뀌면 배경과 (기본 글꼴을 쓰는) 메모를 다시 그립니다.</summary>
    void OnAppearanceChanged()
    {
        _gridBrush = MakeGridBrush();
        Background = _gridBrush;
        UpdateGrid();
        foreach (var v in _views.Values) v.ApplyTextLook();
    }

    internal void FocusCanvas() => Focus();

    /// <summary>지금 보이는 페이지</summary>
    public NotePage? Page => _page;

    // ── 페이지 표시 ──

    public void Show(NotesStore? store, NotePage? page)
    {
        foreach (var v in _views.Values) v.Dispose();
        _views.Clear();
        _surface.Children.Clear();
        _selected = null;
        _store = store;
        _page = page;
        if (store == null || page == null) return;
        _scale.ScaleX = _scale.ScaleY = Math.Clamp(page.Zoom, MinZoom, MaxZoom);
        _pan.X = -page.ViewX;
        _pan.Y = -page.ViewY;
        foreach (var item in page.Items.OrderBy(i => i.Z)) AddView(item);
        UpdateVisibility();
    }

    NoteItemView AddView(NoteItem item)
    {
        var v = new NoteItemView(this, item);
        _views[item.Id] = v;
        _surface.Children.Add(v.Root);
        Panel.SetZIndex(v.Root, item.Z);
        return v;
    }

    /// <summary>화면에 보이는 영역(캔버스 좌표)</summary>
    Rect ViewRect()
    {
        double z = _scale.ScaleX;
        return new Rect(-_pan.X / z, -_pan.Y / z, Math.Max(1, ActualWidth) / z, Math.Max(1, ActualHeight) / z);
    }

    /// <summary>보이는 항목만 펼치고(그리고, GIF를 움직이고) 나머지는 접습니다. 확대/이동/크기 변경 때 호출.</summary>
    void UpdateVisibility()
    {
        UpdateGrid();
        if (_page == null) return;
        var view = ViewRect();
        view.Inflate(view.Width * 0.25, view.Height * 0.25);   // 가장자리에서 튀어나오지 않게 약간 넉넉히
        bool shown = IsVisible;
        foreach (var v in _views.Values)
        {
            var r = new Rect(v.Item.X, v.Item.Y, v.Item.W, v.Item.H);
            v.SetOnScreen(shown && view.IntersectsWith(r), _scale.ScaleX);
        }
    }

    void SaveView()
    {
        if (_page == null) return;
        _page.Zoom = _scale.ScaleX;
        _page.ViewX = -_pan.X;
        _page.ViewY = -_pan.Y;
        Changed?.Invoke();
    }

    // ── 확대/이동 ──

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_page == null) return;
        var p = e.GetPosition(this);
        double old = _scale.ScaleX;
        double z = Math.Clamp(old * Math.Pow(1.15, e.Delta / 120.0), MinZoom, MaxZoom);
        // 마우스 아래의 캔버스 지점이 그대로 남도록
        double cx = (p.X - _pan.X) / old, cy = (p.Y - _pan.Y) / old;
        _scale.ScaleX = _scale.ScaleY = z;
        _pan.X = p.X - cx * z;
        _pan.Y = p.Y - cy * z;
        // 끌어서 이동하는 도중이면 기준점을 지금으로 옮깁니다. 그러지 않으면 다음 마우스 이동이
        // 확대 전 기준으로 위치를 다시 계산해 화면이 튀었습니다.
        if (_panStart != null)
        {
            _panStart = p;
            _panX0 = _pan.X;
            _panY0 = _pan.Y;
        }
        UpdateVisibility();
        SaveView();
        e.Handled = true;
    }

    void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        if (_page == null) return;
        _panStart = e.GetPosition(this);
        _panX0 = _pan.X;
        _panY0 = _pan.Y;
        _panMoved = false;
        CaptureMouse();
    }

    void OnPanMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } s) return;
        var p = e.GetPosition(this);
        if (!_panMoved && (Math.Abs(p.X - s.X) + Math.Abs(p.Y - s.Y)) < 4) return;
        _panMoved = true;
        Cursor = Cursors.SizeAll;
        _pan.X = _panX0 + p.X - s.X;
        _pan.Y = _panY0 + p.Y - s.Y;
        UpdateVisibility();
    }

    void OnPanEnd(object sender, MouseButtonEventArgs e)
    {
        if (_panStart == null || e.ChangedButton == MouseButton.Left) return;
        _panStart = null;
        ReleaseMouseCapture();
        Cursor = null;
        e.Handled = true;
        if (_panMoved)
        {
            SaveView();   // 끌어서 이동했으면 오른쪽 버튼 메뉴를 띄우지 않음
            return;
        }
        // 끌지 않고 오른쪽 클릭: 캔버스가 마우스를 잡고 있어 항목의 메뉴가 저절로 뜨지 않으므로 직접 엽니다
        // (항목 위면 그 항목 메뉴, 빈 곳이면 캔버스 메뉴)
        if (e.ChangedButton != MouseButton.Right) return;
        DependencyObject? hit = InputHitTest(e.GetPosition(this)) as DependencyObject;
        FrameworkElement? owner = null;
        for (var d = hit; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { ContextMenu: not null } fe) { owner = fe; break; }
        owner ??= this;
        if (owner.ContextMenu is { } cm)
        {
            cm.PlacementTarget = owner;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            cm.IsOpen = true;
        }
    }

    /// <summary>모든 항목이 보이게 맞춥니다 (항목이 없으면 원점, 100%).</summary>
    public void FitAll()
    {
        if (_page == null) return;
        if (_page.Items.Count == 0) { SetView(0, 0, 1); return; }
        double x0 = _page.Items.Min(i => i.X), y0 = _page.Items.Min(i => i.Y);
        double x1 = _page.Items.Max(i => i.X + i.W), y1 = _page.Items.Max(i => i.Y + i.H);
        double z = Math.Clamp(Math.Min((ActualWidth - 40) / Math.Max(1, x1 - x0), (ActualHeight - 40) / Math.Max(1, y1 - y0)), MinZoom, 1.5);
        SetView(x0 - (ActualWidth / z - (x1 - x0)) / 2, y0 - (ActualHeight / z - (y1 - y0)) / 2, z);
    }

    public void ResetZoom()
    {
        var c = CenterPoint();
        double z = 1;
        SetView(c.X - ActualWidth / 2 / z, c.Y - ActualHeight / 2 / z, z);
    }

    void SetView(double left, double top, double zoom)
    {
        _scale.ScaleX = _scale.ScaleY = zoom;
        _pan.X = -left * zoom;
        _pan.Y = -top * zoom;
        UpdateVisibility();
        SaveView();
    }

    /// <summary>화면 가운데의 캔버스 좌표</summary>
    public Point CenterPoint()
    {
        var v = ViewRect();
        return new Point(v.X + v.Width / 2, v.Y + v.Height / 2);
    }

    Point ToCanvas(Point screen) => new((screen.X - _pan.X) / _scale.ScaleX, (screen.Y - _pan.Y) / _scale.ScaleY);

    // ── 격자 배경 (이동/확대를 따라 움직임) ──

    static DrawingBrush MakeGridBrush()
    {
        Color bgColor;
        try { bgColor = (Color)ColorConverter.ConvertFromString(NoteAppearance.Background); }
        catch { bgColor = Color.FromRgb(0xFA, 0xFA, 0xFA); }
        // 어두운 배경이면 점을 밝게
        bool dark = (bgColor.R * 0.299 + bgColor.G * 0.587 + bgColor.B * 0.114) < 110;
        var dotColor = dark ? Color.FromRgb(0x55, 0x55, 0x55) : Color.FromRgb(0xC8, 0xC8, 0xC8);
        var dot = new GeometryDrawing(new SolidColorBrush(dotColor), null, new RectangleGeometry(new Rect(0, 0, 1.5, 1.5)));
        var bg = new GeometryDrawing(new SolidColorBrush(bgColor), null, new RectangleGeometry(new Rect(0, 0, GridStep, GridStep)));
        var brush = new DrawingBrush(new DrawingGroup { Children = { bg, dot } })
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, GridStep, GridStep),
            Stretch = Stretch.Fill   // 확대해도 타일 전체를 채움 (None이면 빈틈이 격자무늬로 보였음)
        };
        return brush;
    }

    void UpdateGrid()
    {
        double s = GridStep * _scale.ScaleX;
        if (s < 8) s *= 4;
        _gridBrush.Viewport = new Rect(Mod(_pan.X, s), Mod(_pan.Y, s), s, s);
    }

    static double Mod(double a, double m) => ((a % m) + m) % m;

    // ── 항목 추가/선택/삭제 ──

    int NextZ() => _page == null || _page.Items.Count == 0 ? 1 : _page.Items.Max(i => i.Z) + 1;

    public NoteItem? AddText(Point? at = null, string text = "")
    {
        if (_page == null) return null;
        var p = at ?? CenterPoint();
        var item = new NoteItem { Kind = "text", X = p.X - 110, Y = p.Y - 60, W = 220, H = 130, Text = text, Z = NextZ(),
                                  Color = NoteAppearance.MemoColor, FontSize = NoteAppearance.FontSize };
        _page.Items.Add(item);
        var v = AddView(item);
        UpdateVisibility();
        Select(v);
        v.FocusText();
        Changed?.Invoke();
        return item;
    }

    /// <summary>배경 없는 글상자 (글만 자유롭게 놓기)</summary>
    public NoteItem? AddLabel(Point? at = null)
    {
        if (_page == null) return null;
        var p = at ?? CenterPoint();
        var item = new NoteItem { Kind = "label", X = p.X - 100, Y = p.Y - 24, W = 200, H = 60, Z = NextZ(), Color = "",
                                  FontSize = Math.Max(16, NoteAppearance.FontSize) };
        _page.Items.Add(item);
        var v = AddView(item);
        UpdateVisibility();
        Select(v);
        v.FocusText();
        Changed?.Invoke();
        return item;
    }

    /// <summary>노트 폴더에 저장된 이미지 파일을 캔버스에 놓습니다. 너무 크면 화면에 맞게 줄입니다.</summary>
    public NoteItem? AddImage(string imageName, Point? at = null)
    {
        if (_page == null || _store == null) return null;
        var (pw, ph) = ImageLoader.PixelSize(_store.ImagePath(imageName));
        if (pw <= 0) { pw = 320; ph = 240; }
        double maxW = Math.Max(240, ViewRect().Width * 0.9);
        double k = Math.Min(1.0, maxW / pw);
        double w = pw * k, h = ph * k;
        var p = at ?? CenterPoint();
        var item = new NoteItem { Kind = "image", Image = imageName, X = p.X - w / 2, Y = p.Y - h / 2, W = w, H = h, Z = NextZ() };
        _page.Items.Add(item);
        var v = AddView(item);
        UpdateVisibility();
        Select(v);
        Changed?.Invoke();
        return item;
    }

    internal void Select(NoteItemView? v)
    {
        if (_selected == v) return;
        _selected?.SetSelected(false);
        _selected = v;
        if (v == null) return;
        v.SetSelected(true);
        if (_page != null && !_readOnly && v.Item.Z < _page.Items.Max(i => i.Z))
        {
            v.Item.Z = NextZ();
            Panel.SetZIndex(v.Root, v.Item.Z);
            Changed?.Invoke();
        }
        Focus();
    }

    internal void Delete(NoteItemView v)
    {
        if (_page == null) return;
        _page.Items.Remove(v.Item);
        _views.Remove(v.Item.Id);
        _surface.Children.Remove(v.Root);
        v.Dispose();
        if (_selected == v) _selected = null;
        Changed?.Invoke();
        if (v.Item.Kind == "image") _store?.CleanupUnusedImages();
    }

    internal void ItemChanged() => Changed?.Invoke();
    internal NotesStore? Store => _store;

    void OnBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != this && e.OriginalSource != _surface) return;
        Select(null);
        Focus();
        if (e.ClickCount == 2 && !_readOnly) AddText(ToCanvas(e.GetPosition(this)));
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_readOnly) return;
        if (e.Key == Key.Delete && _selected != null && !_selected.IsEditingText)
        {
            Delete(_selected);
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !(_selected?.IsEditingText ?? false))
        {
            PasteFromClipboard(null);
            e.Handled = true;
        }
    }

    /// <summary>클립보드의 이미지나 글을 붙여 넣습니다.</summary>
    public void PasteFromClipboard(Point? at)
    {
        if (_store == null) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                AddFiles(Clipboard.GetFileDropList().Cast<string>(), at);
            }
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource bmp)
            {
                AddImage(_store.SavePng(ImageLoader.EncodePng(bmp), "paste"), at);
            }
            else if (Clipboard.ContainsText())
            {
                AddText(at, Clipboard.GetText());
            }
        }
        catch (Exception ex) { UiLog.Write($"notes: paste failed {ex.Message}"); }
    }

    void AddFiles(IEnumerable<string> files, Point? at)
    {
        if (_store == null) return;
        var p = at ?? CenterPoint();
        foreach (var f in files)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")) continue;
            try
            {
                AddImage(_store.ImportImageFile(f), p);
                p = new Point(p.X + 24, p.Y + 24);
            }
            catch (Exception ex) { UiLog.Write($"notes: image import failed {ex.Message}"); }
        }
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (_readOnly) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            AddFiles(files, ToCanvas(e.GetPosition(this)));
    }

    public void AddImageFromDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "노트에 넣을 이미지",
            Filter = "이미지 (*.png;*.jpg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp",
            Multiselect = true
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddFiles(dlg.FileNames, null);
    }

    ContextMenu BuildBackgroundMenu()
    {
        var menu = new ContextMenu();
        Point at = default;
        menu.Opened += (_, _) => at = ToCanvas(Mouse.GetPosition(this));
        void Add(string header, Action act) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => act(); menu.Items.Add(mi); }
        if (!_readOnly)
        {
            Add("여기에 메모 추가", () => AddText(at));
            Add("여기에 글상자 추가", () => AddLabel(at));
            Add("이미지 추가...", AddImageFromDialog);
            Add("붙여넣기", () => PasteFromClipboard(at));
            menu.Items.Add(new Separator());
        }
        Add("전체 보기", FitAll);
        Add("100% 크기로", ResetZoom);
        return menu;
    }
}

/// <summary>캔버스 위의 항목 하나 (메모 또는 이미지). 끌어서 옮기고 오른쪽 아래 모서리로 크기를 바꿉니다.</summary>
internal sealed class NoteItemView : IDisposable
{
    static readonly Brush SelectedStroke = Frozen(Color.FromRgb(0x00, 0x78, 0xD7));
    static readonly Brush NormalStroke = Frozen(Color.FromArgb(0x60, 0x00, 0x00, 0x00));

    readonly NotesCanvas _owner;
    public NoteItem Item { get; }
    public Border Root { get; }
    readonly TextBox? _text;
    readonly TextBlock? _rendered;          // 서식을 입혀 보여 주는 글 (편집 중이 아닐 때)
    readonly ScrollViewer? _renderedScroll;
    readonly Border? _handle;
    readonly Image? _image;
    bool IsLabel => Item.Kind == "label";
    GifAnimator? _gif;
    bool _imageLoaded;
    bool _onScreen;
    Point? _dragStart;
    double _x0, _y0, _w0, _h0;
    bool _resizing;

    public NoteItemView(NotesCanvas owner, NoteItem item)
    {
        _owner = owner;
        Item = item;
        var grid = new Grid();
        Root = new Border
        {
            Width = item.W,
            Height = item.H,
            BorderBrush = NormalStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = grid,
            Visibility = Visibility.Collapsed
        };
        Canvas.SetLeft(Root, item.X);
        Canvas.SetTop(Root, item.Y);

        _caption = new TextBlock
        {
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        if (item.Kind == "image")
        {
            Root.Background = Brushes.White;
            _image = new Image { Stretch = Stretch.Uniform, Cursor = Cursors.SizeAll };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            grid.Children.Add(_image);
            _image.MouseLeftButtonDown += StartMove;
            // 이름은 이미지 위쪽 띠에 (이름이 있을 때만)
            _caption.Foreground = Brushes.White;
            _captionBar = new Border { Background = Frozen(Color.FromArgb(0xA0, 0, 0, 0)), VerticalAlignment = VerticalAlignment.Top, Child = _caption, IsHitTestVisible = false };
            grid.Children.Add(_captionBar);
        }
        else
        {
            bool label = item.Kind == "label";
            // 글상자는 배경 없이 글만 (클릭할 수 있게 투명 배경), 메모는 메모지 색
            Root.Background = label ? Brushes.Transparent : Frozen(ParseColor(item.Color, NoteAppearance.MemoColor));
            if (label)
            {
                Root.BorderBrush = Brushes.Transparent;
                Root.CornerRadius = new CornerRadius(0);
                // 글상자는 글 길이에 맞춰 높이가 늘고 줄어듭니다 (모서리를 끌면 너비만 바뀜)
                Root.Height = double.NaN;
                Root.SizeChanged += (_, e) => { if (Math.Abs(Item.H - e.NewSize.Height) > 0.5) { Item.H = e.NewSize.Height; _owner.ItemChanged(); } };
            }
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            // 메모 위쪽 손잡이: 끌어서 이동, 이름이 있으면 여기에 표시 (글상자는 글을 끌어서 이동)
            _caption.Foreground = Frozen(Color.FromRgb(0x44, 0x44, 0x44));
            _caption.FontWeight = FontWeights.SemiBold;
            _handle = new Border { Background = Frozen(Color.FromArgb(0x22, 0, 0, 0)), Cursor = Cursors.SizeAll, CornerRadius = new CornerRadius(3, 3, 0, 0), MinHeight = 12, Child = _caption };
            _handle.MouseLeftButtonDown += StartMove;
            if (label) _handle.Visibility = Visibility.Collapsed;
            grid.Children.Add(_handle);

            _text = new TextBox
            {
                Text = item.Text,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                Background = label ? Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4, 6, 4),
                VerticalScrollBarVisibility = label ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(_text, 1);
            _text.TextChanged += (_, _) => { Item.Text = _text.Text; _owner.ItemChanged(); };
            _text.GotKeyboardFocus += (_, _) => _owner.Select(this);
            _text.LostKeyboardFocus += (_, e) => { if (!_text.IsKeyboardFocusWithin) EndEdit(); };
            _text.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { EndEdit(); _owner.FocusCanvas(); e.Handled = true; } };
            grid.Children.Add(_text);

            _rendered = new TextBlock { TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6, 4, 6, 4) };
            _renderedScroll = new ScrollViewer
            {
                Content = _rendered,
                VerticalScrollBarVisibility = label ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent,
                Cursor = label ? Cursors.SizeAll : Cursors.IBeam
            };
            Grid.SetRow(_renderedScroll, 1);
            _renderedScroll.PreviewMouseLeftButtonDown += (s, e) =>
            {
                // 메모: 누르면 편집. 글상자: 끌면 이동, 두 번 누르면 편집.
                if (!label || e.ClickCount >= 2) { BeginEdit(); e.Handled = true; }
                else StartMove(s, e);
            };
            grid.Children.Add(_renderedScroll);
            ApplyTextLook();
            RenderText();
        }

        var grip = new Thumb
        {
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Opacity = 0.6
        };
        Grid.SetRowSpan(grip, 2);
        grip.DragStarted += (_, _) =>
        {
            _resizing = true; _w0 = Item.W; _h0 = Item.H;
            // 크기를 바꾸는 동안은 빠른 축소 방식으로 (고품질은 매 움직임마다 큰 그림을 다시 계산해 버벅였음)
            if (_image != null) RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.LowQuality);
        };
        grip.DragDelta += (_, e) =>
        {
            // Thumb의 변화량은 캔버스(확대 전) 좌표라 배율과 상관없이 그대로 더합니다.
            Item.W = Math.Max(60, Item.W + e.HorizontalChange);
            if (IsLabel) { Root.Width = Item.W; return; }   // 글상자 높이는 글에 맞춤
            Item.H = Math.Max(40, Item.H + e.VerticalChange);
            if (_image != null && _image.Source is BitmapSource b && b.PixelWidth > 0)   // 이미지는 비율 유지
                Item.H = Item.W * b.PixelHeight / b.PixelWidth;
            Root.Width = Item.W;
            Root.Height = Item.H;
        };
        grip.DragCompleted += (_, _) =>
        {
            _resizing = false;
            if (_image != null) RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            if (_image != null && _imageLoaded && Math.Abs(Item.W - _w0) > 1) ReloadImage();   // 새 크기에 맞게 다시 디코딩
            _owner.ItemChanged();
        };
        if (!owner.ReadOnly) grid.Children.Add(grip);

        Root.MouseMove += OnMove;
        Root.MouseLeftButtonUp += EndMove;
        Root.MouseLeftButtonDown += (_, _) => _owner.Select(this);
        if (!owner.ReadOnly) Root.ContextMenu = BuildMenu();
        UpdateCaption();
    }

    readonly TextBlock _caption;
    readonly Border? _captionBar;

    void UpdateCaption()
    {
        bool has = !string.IsNullOrWhiteSpace(Item.Title);
        _caption.Text = has ? Item.Title : "";
        _caption.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (_captionBar != null) _captionBar.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>자른 부분만 보여 줍니다 (원본 파일은 그대로).</summary>
    BitmapSource? ApplyCrop(BitmapSource? src)
    {
        if (src == null || !Item.HasCrop) return src;
        int w = src.PixelWidth, h = src.PixelHeight;
        int x = (int)Math.Round(Item.CropL * w), y = (int)Math.Round(Item.CropT * h);
        int cw = Math.Max(1, (int)Math.Round((Item.CropR - Item.CropL) * w)), ch = Math.Max(1, (int)Math.Round((Item.CropB - Item.CropT) * h));
        x = Math.Clamp(x, 0, w - 1); y = Math.Clamp(y, 0, h - 1);
        cw = Math.Min(cw, w - x); ch = Math.Min(ch, h - y);
        var c = new CroppedBitmap(src, new Int32Rect(x, y, cw, ch));
        c.Freeze();
        return c;
    }

    /// <summary>자른 영역의 원본 픽셀 크기</summary>
    (int w, int h) VisiblePixelSize()
    {
        if (_owner.Store == null) return (0, 0);
        var (w, h) = ImageLoader.PixelSize(_owner.Store.ImagePath(Item.Image));
        return ((int)Math.Round(w * (Item.CropR - Item.CropL)), (int)Math.Round(h * (Item.CropB - Item.CropT)));
    }

    public bool IsEditingText => _text?.IsKeyboardFocusWithin == true;

    /// <summary>글 쓰는 중이거나 끌거나 크기를 바꾸는 중</summary>
    public bool IsBusy => IsEditingText || _dragStart != null || _resizing;

    public void FocusText() => BeginEdit();

    /// <summary>편집 시작: 서식 대신 기호가 그대로 보이는 입력 칸으로 바꿉니다.</summary>
    void BeginEdit()
    {
        if (_text == null || _renderedScroll == null || _owner.ReadOnly) return;
        _owner.Select(this);
        _renderedScroll.Visibility = Visibility.Collapsed;
        _text.Visibility = Visibility.Visible;
        _text.Dispatcher.BeginInvoke(() => { _text.Focus(); _text.CaretIndex = _text.Text.Length; }, DispatcherPriority.Input);
    }

    /// <summary>편집 끝: 서식을 입혀 보여 줍니다.</summary>
    void EndEdit()
    {
        if (_text == null || _renderedScroll == null || _text.Visibility != Visibility.Visible) return;
        RenderText();
        _text.Visibility = Visibility.Collapsed;
        _renderedScroll.Visibility = Visibility.Visible;
    }

    static readonly Brush PlaceholderBrush = Frozen(Color.FromArgb(0x80, 0x60, 0x60, 0x60));

    /// <summary>*기울임*, **굵게**, ***둘 다***, _밑줄_, ~~취소선~~ 을 입혀 보여 줍니다.</summary>
    void RenderText()
    {
        if (_rendered == null) return;
        _rendered.Inlines.Clear();
        if (string.IsNullOrEmpty(Item.Text))
        {
            // 빈 글상자는 보이지 않으므로 자리 표시 글을 보여 줍니다
            _rendered.Inlines.Add(new System.Windows.Documents.Run(IsLabel ? "글상자 (두 번 눌러 쓰기)" : "") { Foreground = PlaceholderBrush, FontStyle = FontStyles.Italic });
            return;
        }
        foreach (var seg in NoteMarkup.Parse(Item.Text))
        {
            var lines = seg.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) _rendered.Inlines.Add(new System.Windows.Documents.LineBreak());
                if (lines[i].Length == 0) continue;
                var run = new System.Windows.Documents.Run(lines[i]);
                if (seg.Style.HasFlag(NoteStyle.Bold)) run.FontWeight = FontWeights.Bold;
                if (seg.Style.HasFlag(NoteStyle.Italic)) run.FontStyle = FontStyles.Italic;
                if (seg.Style.HasFlag(NoteStyle.Underline) || seg.Style.HasFlag(NoteStyle.Strike))
                {
                    var deco = new TextDecorationCollection();
                    if (seg.Style.HasFlag(NoteStyle.Underline)) deco.Add(TextDecorations.Underline);
                    if (seg.Style.HasFlag(NoteStyle.Strike)) deco.Add(TextDecorations.Strikethrough);
                    run.TextDecorations = deco;
                }
                _rendered.Inlines.Add(run);
            }
        }
    }

    /// <summary>글꼴·크기·글자 색 (비어 있으면 메모 설정의 기본값)</summary>
    public void ApplyTextLook()
    {
        if (_text == null || _rendered == null) return;
        var family = !string.IsNullOrWhiteSpace(Item.FontFamily) ? new FontFamily(Item.FontFamily)
                   : !string.IsNullOrWhiteSpace(NoteAppearance.FontFamily) ? new FontFamily(NoteAppearance.FontFamily) : null;
        foreach (Control c in new Control[] { _text })
        {
            if (family != null) c.FontFamily = family; else c.ClearValue(Control.FontFamilyProperty);
            c.FontSize = Item.FontSize;
        }
        if (family != null) _rendered.FontFamily = family; else _rendered.ClearValue(TextBlock.FontFamilyProperty);
        _rendered.FontSize = Item.FontSize;
        if (IsLabel)
        {
            var fg = Frozen(ParseColor(Item.TextColor, NoteAppearance.LabelColor));
            _text.Foreground = fg;
            _rendered.Foreground = fg;
        }
    }

    static Color ParseColor(string? value, string fallback)
    {
        try { if (!string.IsNullOrWhiteSpace(value)) return (Color)ColorConverter.ConvertFromString(value); } catch { }
        return (Color)ColorConverter.ConvertFromString(fallback);
    }

    public void SetSelected(bool on)
    {
        Root.BorderBrush = on ? SelectedStroke : IsLabel ? Brushes.Transparent : NormalStroke;
        Root.BorderThickness = new Thickness(on ? 2 : 1);
    }

    /// <summary>화면 안에 있을 때만 그립니다. 이미지는 처음 보일 때 디코딩, GIF는 보일 때만 움직임.</summary>
    public void SetOnScreen(bool on, double zoom)
    {
        if (_onScreen == on) return;
        _onScreen = on;
        Root.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (_image == null) return;
        if (on && !_imageLoaded) ReloadImage();
        if (_gif != null) { if (on) _gif.Start(); else _gif.Stop(); }
    }

    void ReloadImage()
    {
        if (_image == null || _owner.Store == null) return;
        _gif?.Dispose();
        _gif = null;
        string path = _owner.Store.ImagePath(Item.Image);
        // 표시 크기의 2배까지만 (확대해도 선명, 메모리 절약). 잘랐으면 원본이 그만큼 더 커야 합니다.
        int decodeW = (int)Math.Clamp(Item.W * 2 / Math.Max(0.05, Item.CropR - Item.CropL), 64, 4096);
        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            _gif = GifAnimator.TryCreate(path, decodeW, f => _image.Source = ApplyCrop(f));
            if (_gif != null)
            {
                _imageLoaded = true;
                if (_onScreen) _gif.Start();
                return;
            }
        }
        _image.Source = ApplyCrop(ImageLoader.Load(path, decodeW));
        _imageLoaded = true;
    }

    void StartMove(object sender, MouseButtonEventArgs e)
    {
        _owner.Select(this);
        if (_owner.ReadOnly) return;
        var canvas = (IInputElement)Root.Parent;
        _dragStart = e.GetPosition(canvas);
        _x0 = Item.X;
        _y0 = Item.Y;
        Root.CaptureMouse();
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } s || _resizing || !Root.IsMouseCaptured) return;
        var p = e.GetPosition((IInputElement)Root.Parent);
        Item.X = _x0 + p.X - s.X;
        Item.Y = _y0 + p.Y - s.Y;
        Canvas.SetLeft(Root, Item.X);
        Canvas.SetTop(Root, Item.Y);
    }

    void EndMove(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart == null) return;
        _dragStart = null;
        Root.ReleaseMouseCapture();
        _owner.ItemChanged();
    }

    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        if (_text != null)
        {
            var colors = new MenuItem { Header = IsLabel ? "글자 색" : "색" };
            foreach (var (name, color) in IsLabel ? NoteAppearance.TextColors : NoteAppearance.MemoColors)
            {
                var mi = new MenuItem { Header = name, Icon = ColorSwatch(color) };
                mi.Click += (_, _) =>
                {
                    if (IsLabel) Item.TextColor = color;
                    else
                    {
                        Item.Color = color;
                        Root.Background = Frozen(ParseColor(color, NoteAppearance.MemoColor));
                    }
                    ApplyTextLook();
                    _owner.ItemChanged();
                };
                colors.Items.Add(mi);
            }
            menu.Items.Add(colors);
            var font = new MenuItem { Header = "글꼴..." };
            font.Click += (_, _) =>
            {
                string? f = FontPickWindow.Ask(Window.GetWindow(Root), Item.FontFamily);
                if (f == null) return;
                Item.FontFamily = f;
                ApplyTextLook();
                _owner.ItemChanged();
            };
            menu.Items.Add(font);
            var size = new MenuItem { Header = "글자 크기" };
            foreach (var fs in IsLabel ? new[] { 11.0, 13.0, 16.0, 20.0, 28.0, 36.0, 48.0 } : new[] { 11.0, 13.0, 16.0, 20.0, 28.0 })
            {
                var mi = new MenuItem { Header = $"{fs:0}" };
                mi.Click += (_, _) => { Item.FontSize = fs; ApplyTextLook(); _owner.ItemChanged(); };
                size.Items.Add(mi);
            }
            menu.Items.Add(size);
            var edit = new MenuItem { Header = "글 고치기" };
            edit.Click += (_, _) => BeginEdit();
            menu.Items.Add(edit);
        }
        else
        {
            var crop = new MenuItem { Header = "자르기..." };
            crop.Click += (_, _) => CropImage();
            menu.Items.Add(crop);
            var uncrop = new MenuItem { Header = "자르기 취소" };
            uncrop.Click += (_, _) => SetCrop(0, 0, 1, 1);
            menu.Opened += (_, _) => uncrop.IsEnabled = Item.HasCrop;
            menu.Items.Add(uncrop);
            var orig = new MenuItem { Header = "원래 크기로" };
            orig.Click += (_, _) =>
            {
                if (_owner.Store == null) return;
                var (w, h) = VisiblePixelSize();
                if (w <= 0) return;
                Item.W = w;
                Item.H = h;
                Root.Width = w;
                Root.Height = h;
                ReloadImage();
                _owner.ItemChanged();
            };
            menu.Items.Add(orig);
            var copy = new MenuItem { Header = "이미지 복사" };
            copy.Click += (_, _) =>
            {
                if (_owner.Store != null && ImageLoader.Load(_owner.Store.ImagePath(Item.Image), 0) is BitmapSource b) Clipboard.SetImage(b);
            };
            menu.Items.Add(copy);
        }
        var rename = new MenuItem { Header = "이름 붙이기..." };
        rename.Click += (_, _) =>
        {
            string? n = InputDialog.Ask(Window.GetWindow(Root), "이름 붙이기", "이 항목의 이름 (비우면 이름을 숨깁니다):", Item.Title);
            if (n == null) return;
            Item.Title = n.Trim();
            UpdateCaption();
            _owner.ItemChanged();
        };
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        var del = new MenuItem { Header = "삭제", InputGestureText = "Delete" };
        del.Click += (_, _) => _owner.Delete(this);
        menu.Items.Add(del);
        return menu;
    }

    /// <summary>원본 이미지에서 보여 줄 부분을 고릅니다 (원본 파일은 그대로 — 보관함의 스크린샷도 그대로).</summary>
    void CropImage()
    {
        if (_owner.Store == null) return;
        var full = ImageLoader.Load(_owner.Store.ImagePath(Item.Image), 1600);
        if (full == null) return;
        var r = CropWindow.Ask(Window.GetWindow(Root), full, new Rect(Item.CropL, Item.CropT, Item.CropR - Item.CropL, Item.CropB - Item.CropT));
        if (r is { } sel) SetCrop(sel.Left, sel.Top, sel.Right, sel.Bottom);
    }

    void SetCrop(double l, double t, double r, double b)
    {
        double oldW = Item.CropR - Item.CropL;
        Item.CropL = l; Item.CropT = t; Item.CropR = r; Item.CropB = b;
        // 보이는 너비가 바뀐 만큼 항목 크기도 맞추고, 비율은 자른 모양대로
        var (pw, ph) = VisiblePixelSize();
        if (pw > 0 && ph > 0)
        {
            Item.W = Math.Max(60, Item.W * (r - l) / Math.Max(0.01, oldW));
            Item.H = Item.W * ph / pw;
            Root.Width = Item.W;
            Root.Height = Item.H;
        }
        ReloadImage();
        _owner.ItemChanged();
    }

    public void Dispose()
    {
        _gif?.Dispose();
        _gif = null;
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    static UIElement ColorSwatch(string color) => new Border
    {
        Width = 14, Height = 14,
        Background = Frozen(ParseColor(color, "#FFFFFFFF")),
        BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1)
    };
}

/// <summary>이미지 읽기 도우미: 파일을 잠그지 않고, 원하는 너비로 디코딩합니다.</summary>
internal static class ImageLoader
{
    public static BitmapSource? Load(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            if (decodeWidth > 0)
            {
                var (w, _) = PixelSize(path);
                if (w > decodeWidth) bmp.DecodePixelWidth = decodeWidth;
            }
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public static (int w, int h) PixelSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch { return (0, 0); }
    }

    public static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// 움직이는 GIF: 프레임을 한 번 합성해 두고(부분 프레임·지우기 방식 반영) 보이는 동안에만 타이머로 넘깁니다.
/// 너무 큰 GIF(합성 프레임 메모리 약 200MB 초과)는 첫 프레임만 보여 줍니다.
/// </summary>
internal sealed class GifAnimator : IDisposable
{
    readonly List<BitmapSource> _frames;
    readonly List<int> _delays;
    readonly Action<BitmapSource> _show;
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    int _index;

    GifAnimator(List<BitmapSource> frames, List<int> delays, Action<BitmapSource> show)
    {
        _frames = frames;
        _delays = delays;
        _show = show;
        _timer.Tick += (_, _) => Next();
        _show(_frames[0]);
    }

    public static GifAnimator? TryCreate(string path, int maxWidth, Action<BitmapSource> show)
    {
        try
        {
            GifBitmapDecoder dec;
            using (var fs = File.OpenRead(path))
                dec = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (dec.Frames.Count <= 1) return null;
            int w = dec.Frames[0].PixelWidth, h = dec.Frames[0].PixelHeight;
            if (dec.Metadata is BitmapMetadata m)
            {
                if (m.GetQuery("/logscrdesc/Width") is ushort lw) w = lw;
                if (m.GetQuery("/logscrdesc/Height") is ushort lh) h = lh;
            }
            double scale = Math.Min(1.0, (double)maxWidth / Math.Max(1, w));
            if ((long)w * h * dec.Frames.Count * scale * scale * 4 > 200L * 1024 * 1024)
                return new GifAnimator([dec.Frames[0]], [1000], show);   // 너무 큼: 첫 프레임만

            int ow = Math.Max(1, (int)(w * scale)), oh = Math.Max(1, (int)(h * scale));
            var frames = new List<BitmapSource>();
            var delays = new List<int>();
            var canvas = new RenderTargetBitmap(ow, oh, 96, 96, PixelFormats.Pbgra32);
            BitmapSource? restore = null;
            foreach (var f in dec.Frames)
            {
                var md = f.Metadata as BitmapMetadata;
                double fx = Q(md, "/imgdesc/Left"), fy = Q(md, "/imgdesc/Top");
                int delay = (int)Q(md, "/grctlext/Delay") * 10;
                int disposal = (int)Q(md, "/grctlext/Disposal");
                if (disposal == 3) restore = Snapshot(canvas);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawImage(f, new Rect(fx * scale, fy * scale, f.PixelWidth * scale, f.PixelHeight * scale));
                canvas.Render(dv);
                var shot = Snapshot(canvas);
                frames.Add(shot);
                delays.Add(delay < 20 ? 100 : delay);   // 브라우저처럼 너무 짧은 지연은 100ms로
                if (disposal == 2)   // 배경으로 지우기: 이번 프레임 영역을 비움
                {
                    var clear = new RenderTargetBitmap(ow, oh, 96, 96, PixelFormats.Pbgra32);
                    var cv = new DrawingVisual();
                    using (var dc = cv.RenderOpen())
                    {
                        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, ow, oh)),
                            new RectangleGeometry(new Rect(fx * scale, fy * scale, f.PixelWidth * scale, f.PixelHeight * scale))));
                        dc.DrawImage(shot, new Rect(0, 0, ow, oh));
                        dc.Pop();
                    }
                    clear.Render(cv);
                    canvas = clear;
                }
                else if (disposal == 3 && restore != null)   // 이전 상태로
                {
                    var back = new RenderTargetBitmap(ow, oh, 96, 96, PixelFormats.Pbgra32);
                    var bv = new DrawingVisual();
                    using (var dc = bv.RenderOpen()) dc.DrawImage(restore, new Rect(0, 0, ow, oh));
                    back.Render(bv);
                    canvas = back;
                }
            }
            return new GifAnimator(frames, delays, show);
        }
        catch (Exception ex)
        {
            UiLog.Write($"notes: gif decode failed {ex.Message}");
            return null;
        }
    }

    static double Q(BitmapMetadata? md, string query)
    {
        try { return md?.GetQuery(query) is { } v ? Convert.ToDouble(v) : 0; } catch { return 0; }
    }

    static BitmapSource Snapshot(RenderTargetBitmap rtb)
    {
        var copy = new WriteableBitmap(rtb);
        copy.Freeze();
        return copy;
    }

    void Next()
    {
        _index = (_index + 1) % _frames.Count;
        _show(_frames[_index]);
        _timer.Interval = TimeSpan.FromMilliseconds(_delays[_index]);
    }

    public void Start()
    {
        if (_frames.Count <= 1 || _timer.IsEnabled) return;
        _timer.Interval = TimeSpan.FromMilliseconds(_delays[_index]);
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Dispose() => _timer.Stop();
}
