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
