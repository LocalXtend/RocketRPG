#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RocketRPG.Models;

/// <summary>
/// 화면의 한 영역(물리 픽셀)을 PNG로 찍습니다. 게임 창은 엔진마다 그리는 방식이 달라(OpenGL/SDL/WebView2)
/// 창에서 직접 읽기보다 합성된 화면을 복사하는 편이 모든 엔진에서 똑같이 동작합니다.
/// </summary>
public static class ScreenCapture
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    const uint PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2;

    /// <summary>
    /// 게임 창(mkxp-z/EasyRPG)의 내용만 PNG로 찍습니다. 화면을 복사하면 게임 위에 겹친 다른 창까지 찍혔습니다.
    /// 창이 그림을 주지 않으면(전부 한 색) null — 그때는 화면 영역 복사로 대신합니다.
    /// </summary>
    public static byte[]? CaptureWindowPng(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var rc)) return null;
        int w = rc.R - rc.L, h = rc.B - rc.T;
        if (w <= 0 || h <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, w, h);
            old = SelectObject(mem, bmp);
            if (!PrintWindow(hwnd, mem, PW_CLIENTONLY | PW_RENDERFULLCONTENT)) return null;
            SelectObject(mem, old);
            old = IntPtr.Zero;
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (IsBlank(src)) return null;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            UiLog.Write($"screenshot: window capture failed {ex.Message}");
            return null;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>드문드문 짚어 봐서 모두 같은 색이면 빈 그림으로 봅니다.</summary>
    static bool IsBlank(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[4];
        int? first = null;
        for (int i = 1; i <= 8; i++)
            for (int j = 1; j <= 8; j++)
            {
                src.CopyPixels(new Int32Rect(w * i / 9, h * j / 9, 1, 1), px, 4, 0);
                int v = BitConverter.ToInt32(px, 0) & 0xFFFFFF;
                if (first == null) first = v;
                else if (v != first) return false;
            }
        return true;
    }

    /// <summary>화면 좌표(물리 픽셀) 영역을 PNG 바이트로. 실패하면 null.</summary>
    public static byte[]? CapturePng(Int32Rect screenRect)
    {
        if (screenRect.Width <= 0 || screenRect.Height <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, screenRect.Width, screenRect.Height);
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, screenRect.Width, screenRect.Height, screen, screenRect.X, screenRect.Y, SRCCOPY | CAPTUREBLT))
                return null;
            SelectObject(mem, old);
            old = IntPtr.Zero;
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            UiLog.Write($"screenshot: capture failed {ex.Message}");
            return null;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
