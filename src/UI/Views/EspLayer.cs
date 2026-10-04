#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>
/// ESP 오버레이 전용 경량 렌더 레이어.
/// 이벤트마다 WPF 요소를 만들지 않고 OnRender 한 번으로 그리며, 데이터/뷰포트가 바뀔 때만 다시 그립니다.
/// 게임 뷰포트 밖(메뉴바 등)으로는 절대 그리지 않도록 클리핑합니다.
/// </summary>
public sealed class EspLayer : FrameworkElement
{
    // 정적 필드는 선언 순서대로 초기화되므로 Palette가 Fills/Strokes보다 먼저 와야 합니다.
    static readonly Color[] Palette =
    {
        Color.FromRgb(0, 153, 255),   // 결정키
        Color.FromRgb(0, 204, 68),    // 접촉
        Color.FromRgb(255, 136, 0),   // 자동실행
        Color.FromRgb(187, 51, 255),  // 병렬처리
        Color.FromRgb(0, 255, 255)    // 기타
    };
    static readonly Brush[] Fills = MakeFills();
    static readonly Pen[] Strokes = MakeStrokes();
    static readonly Brush LabelBg = Freeze(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)));
    static readonly Typeface LabelFace = new(new FontFamily("Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    List<EspItem> _items = new();
    readonly Dictionary<int, Point> _pos = new();
    readonly Dictionary<string, FormattedText> _labels = new();
    Rect _viewport = Rect.Empty;
    int _baseW = 1, _baseH = 1;
    bool _animating;

    public EspLayer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public void Update(List<EspItem>? items)
    {
        _items = items ?? new List<EspItem>();
        if (_items.Count == 0) _pos.Clear();
        StartAnimation();
        InvalidateVisual();
    }

    public void SetViewport(Rect viewport, int baseW, int baseH)
    {
        if (baseW <= 0 || baseH <= 0) return;
        if (_viewport == viewport && _baseW == baseW && _baseH == baseH) return;
        _viewport = viewport;
        _baseW = baseW;
        _baseH = baseH;
        _pos.Clear(); // 스케일이 바뀌면 보간 기준도 초기화
        InvalidateVisual();
    }

    public void Clear()
    {
        _items = new List<EspItem>();
        _pos.Clear();
        StopAnimation();
        InvalidateVisual();
    }

    void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnRendering;
    }

    void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    void OnRendering(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (_items.Count == 0 || _viewport.IsEmpty || _viewport.Width < 1 || _viewport.Height < 1)
        {
            StopAnimation();
            return;
        }

        double sx = _viewport.Width / _baseW;
        double sy = _viewport.Height / _baseH;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool moving = false;

        dc.PushClip(new RectangleGeometry(_viewport));
        foreach (var item in _items)
        {
            Point p;
            if (item.View is EspView v)
            {
                // 맵 좌표 ESP: 카메라(스크롤)는 매 프레임 정확히 따라가고, 이벤트 자신의 이동만 부드럽게 보간합니다.
                // (예전에는 화면 좌표를 0.1초마다 받아 보간해서, 걸을 때 모든 상자가 캐릭터를 늦게 따라왔음)
                var t = new Point(item.TileX, item.TileY);
                if (_pos.TryGetValue(item.Id, out var ct) && Math.Abs(t.X - ct.X) <= 3 && Math.Abs(t.Y - ct.Y) <= 3)
                {
                    double ddx = t.X - ct.X, ddy = t.Y - ct.Y;
                    if (Math.Abs(ddx) > 0.01 || Math.Abs(ddy) > 0.01) { t = new Point(ct.X + ddx * 0.35, ct.Y + ddy * 0.35); moving = true; }
                }
                _pos[item.Id] = t;
                double dx = Wrap(t.X - v.CamX, v.LoopX, v.MapW);
                double dy = Wrap(t.Y - v.CamY, v.LoopY, v.MapH);
                double gx = v.OffX + dx * v.Tile, gy = v.OffY + dy * v.Tile;
                if (gx < -v.Tile || gy < -v.Tile || gx > _baseW || gy > _baseH) continue;
                p = new Point(_viewport.X + gx * sx, _viewport.Y + gy * sy);
            }
            else
            {
                var target = new Point(_viewport.X + item.X * sx, _viewport.Y + item.Y * sy);
                if (_pos.TryGetValue(item.Id, out var cur) &&
                    Math.Abs(target.X - cur.X) <= 100 && Math.Abs(target.Y - cur.Y) <= 100)
                {
                    double dx = target.X - cur.X, dy = target.Y - cur.Y;
                    if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) p = target;
                    else { p = new Point(cur.X + dx * 0.35, cur.Y + dy * 0.35); moving = true; }
                }
                else p = target;
                _pos[item.Id] = p;
            }

            double w = Math.Max(16 * sx, item.W * sx);
            double h = Math.Max(16 * sy, item.H * sy);
            int cls = TriggerClass(item.Trigger);
            dc.DrawRoundedRectangle(Fills[cls], Strokes[cls], new Rect(p.X, p.Y, w, h), 3, 3);

            if (!string.IsNullOrEmpty(item.Name))
            {
                var ft = Label(item.Name, ppd);
                double lx = p.X + (w - ft.Width) / 2;
                double ly = p.Y - ft.Height - 1;
                dc.DrawRectangle(LabelBg, null, new Rect(lx - 2, ly, ft.Width + 4, ft.Height));
                dc.DrawText(ft, new Point(lx, ly));
            }
        }
        dc.Pop();

        if (!moving) StopAnimation();
    }

    FormattedText Label(string text, double ppd)
    {
        if (_labels.TryGetValue(text, out var ft)) return ft;
        if (_labels.Count > 512) _labels.Clear();
        ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10, Brushes.White, ppd);
        _labels[text] = ft;
        return ft;
    }

    // 반복 맵: 카메라 기준 거리를 화면 왼쪽 약간 바깥(-2칸)부터 한 바퀴 범위로 맞춥니다.
    static double Wrap(double d, bool loop, int size)
    {
        if (!loop || size <= 0) return d;
        d %= size;
        if (d < -2) d += size;
        if (d >= size - 2) d -= size;
        return d;
    }

    static int TriggerClass(int trigger) => trigger switch
    {
        0 => 0,
        1 or 2 => 1,
        3 => 2,
        4 => 3,
        _ => 4
    };

    static Brush[] MakeFills()
    {
        var arr = new Brush[Palette.Length];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = Freeze(new SolidColorBrush(Color.FromArgb(50, Palette[i].R, Palette[i].G, Palette[i].B)));
        return arr;
    }

    static Pen[] MakeStrokes()
    {
        var arr = new Pen[Palette.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            var pen = new Pen(Freeze(new SolidColorBrush(Palette[i])), 2);
            pen.Freeze();
            arr[i] = pen;
        }
        return arr;
    }

    static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
