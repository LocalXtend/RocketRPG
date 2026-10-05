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
