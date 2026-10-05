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
