#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace RocketRPG.Views;

/// <summary>
/// 이미지 자르기 창: 마우스로 끌어 남길 부분을 고릅니다. 결과는 원본에 대한 비율(0~1) 사각형입니다.
/// 윈도우 기본 모양(시스템 배경색, 표준 버튼).
/// </summary>
internal sealed class CropWindow : Window
{
    readonly Image _img;
    readonly Canvas _layer;
    readonly Rectangle _sel;
    readonly Path _shade;
    readonly TextBlock _info;
    readonly BitmapSource _src;
    Rect _norm;          // 선택 (0~1)
    Point? _dragFrom;

    CropWindow(BitmapSource src, Rect initial)
    {
        _src = src;
        _norm = initial.Width > 0 && initial.Height > 0 ? initial : new Rect(0, 0, 1, 1);
        Title = "이미지 자르기";
        Background = SystemColors.ControlBrush;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 900;
        Height = 680;
        MinWidth = 360;
        MinHeight = 300;

        _img = new Image { Source = src, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
        _shade = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), IsHitTestVisible = false };
        _sel = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
        _layer = new Canvas { Background = Brushes.Transparent, Cursor = Cursors.Cross, ClipToBounds = true };
        _layer.Children.Add(_shade);
        _layer.Children.Add(_sel);
        var stage = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)) };
        stage.Children.Add(_img);
        stage.Children.Add(_layer);

        _info = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = SystemColors.GrayTextBrush };
        var all = new Button { Content = "전체", Width = 75, Margin = new Thickness(0, 0, 6, 0) };
        var ok = new Button { Content = "확인", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "취소", Width = 75, IsCancel = true };
        all.Click += (_, _) => { _norm = new Rect(0, 0, 1, 1); Redraw(); };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(all);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var bottom = new DockPanel { Margin = new Thickness(10, 8, 10, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_info);

        var hint = new TextBlock { Text = "남길 부분을 마우스로 끌어서 고르세요. 원본 이미지는 바뀌지 않고, 나중에 '자르기 취소'로 되돌릴 수 있습니다.", Margin = new Thickness(10, 8, 10, 6), TextWrapping = TextWrapping.Wrap };
        var root = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(bottom);
        root.Children.Add(stage);
        Content = root;

        _layer.MouseLeftButtonDown += (_, e) => { _dragFrom = ToNorm(e.GetPosition(_layer)); _layer.CaptureMouse(); };
        _layer.MouseMove += (_, e) =>
        {
            if (_dragFrom is not { } a) return;
            var b = ToNorm(e.GetPosition(_layer));
            _norm = new Rect(a, b);
            Redraw();
        };
        _layer.MouseLeftButtonUp += (_, _) =>
        {
            _dragFrom = null;
            _layer.ReleaseMouseCapture();
            if (_norm.Width < 0.01 || _norm.Height < 0.01) _norm = new Rect(0, 0, 1, 1);   // 거의 안 끌었으면 전체
            Redraw();
        };
        stage.SizeChanged += (_, _) => Redraw();
    }

    /// <summary>이미지가 실제로 그려진 영역 (Uniform으로 가운데 맞춤)</summary>
    Rect ImageRect()
    {
        double aw = _layer.ActualWidth, ah = _layer.ActualHeight;
        if (aw <= 0 || ah <= 0 || _src.PixelWidth <= 0) return Rect.Empty;
        double s = Math.Min(aw / _src.PixelWidth, ah / _src.PixelHeight);
        double w = _src.PixelWidth * s, h = _src.PixelHeight * s;
        return new Rect((aw - w) / 2, (ah - h) / 2, w, h);
    }

    Point ToNorm(Point p)
    {
        var r = ImageRect();
        if (r.IsEmpty) return new Point();
        return new Point(Math.Clamp((p.X - r.X) / r.Width, 0, 1), Math.Clamp((p.Y - r.Y) / r.Height, 0, 1));
    }

    void Redraw()
    {
        var r = ImageRect();
        if (r.IsEmpty) return;
        var s = new Rect(r.X + _norm.X * r.Width, r.Y + _norm.Y * r.Height, _norm.Width * r.Width, _norm.Height * r.Height);
        Canvas.SetLeft(_sel, s.X);
        Canvas.SetTop(_sel, s.Y);
        _sel.Width = Math.Max(1, s.Width);
        _sel.Height = Math.Max(1, s.Height);
        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(r), new RectangleGeometry(s));
        _info.Text = $"선택: {Math.Round(_norm.Width * _src.PixelWidth)} × {Math.Round(_norm.Height * _src.PixelHeight)} (보이는 크기 기준)";
    }

    /// <summary>자를 영역(비율)을 고르게 합니다. 취소하면 null.</summary>
    public static Rect? Ask(Window? owner, BitmapSource src, Rect current)
    {
        var w = new CropWindow(src, current) { Owner = owner, FontFamily = owner?.FontFamily ?? new FontFamily("맑은 고딕") };
        return w.ShowDialog() == true ? w._norm : null;
    }
}
