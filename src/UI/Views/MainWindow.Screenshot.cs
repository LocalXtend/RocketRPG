#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using RocketRPG.Models;

namespace RocketRPG.Views;

public partial class MainWindow
{
    // ── 스크린샷: 보관함(RocketRPG\screenshots\게임명\)에 저장, 원하면 노트에도 ──

    void OnScreenshotToNote(object s, RoutedEventArgs e) => _ = TakeScreenshot(toNote: true);
    void OnScreenshotOnly(object s, RoutedEventArgs e) => _ = TakeScreenshot(toNote: false);
    void OnOpenGallery(object s, RoutedEventArgs e) => OpenGallery(insertIntoNotes: false);

    /// <summary>
    /// 게임 화면(레터박스 제외)을 찍어 보관함에 저장합니다. toNote면 노트에도 넣습니다(노트는 자기 사본을 가짐).
    /// 찍는 동안 HUD·알림 오버레이는 잠깐 숨깁니다.
    /// </summary>
    async Task TakeScreenshot(bool toNote)
    {
        if (IsMultiGuest) { await TakeGuestScreenshot(toNote); return; }   // 참가자: 방장 화면(받은 영상)을 찍음
        if (_currentBridge == null || string.IsNullOrEmpty(_currentDir)) { ShowHudMessage("게임이 실행 중이 아닙니다."); return; }
        // 게임 화면만 직접 찍습니다 (화면 영역을 복사하면 게임 위에 겹친 다른 창까지 찍혔음)
        byte[]? png = null;
        if (_currentBridge == _webRenderer) png = await _webRenderer.CapturePngAsync();
        else if (_currentBridge is RocketRenderMKXP mk) { var hw = mk.GameHwnd; png = await Task.Run(() => ScreenCapture.CaptureWindowPng(hw)); }
        else if (_currentBridge is RocketRenderEasyRPG er) { var hw = er.GameHwnd; png = await Task.Run(() => ScreenCapture.CaptureWindowPng(hw)); }
        if (png == null)
        {
            // 대신: 합성된 화면에서 게임 영역 복사 (HUD·알림 오버레이는 잠깐 숨김)
            GetGameViewport(out double vx, out double vy, out double vw, out double vh, out _, out _);
            if (vw < 2 || vh < 2) return;
            var tl = RenderScreen.PointToScreen(new Point(vx, vy));
            var br = RenderScreen.PointToScreen(new Point(vx + vw, vy + vh));
            var rect = new Int32Rect((int)Math.Round(tl.X), (int)Math.Round(tl.Y),
                                     (int)Math.Round(br.X - tl.X), (int)Math.Round(br.Y - tl.Y));
            double overlayOpacity = _overlay.Opacity;
            _overlay.Opacity = 0;
            try
            {
                await Task.Delay(60);   // 오버레이가 사라진 화면이 합성될 때까지
                png = await Task.Run(() => ScreenCapture.CapturePng(rect));
            }
            finally
            {
                _overlay.Opacity = overlayOpacity;
            }
        }
        if (png == null) { ShowHudMessage("스크린샷을 찍지 못했습니다."); return; }
        string title = CurrentGameTitle();
        string saved;
        try { saved = await Task.Run(() => ScreenshotStore.Save(title, png)); }
        catch (Exception ex) { UiLog.Write($"screenshot: save failed {ex.Message}"); ShowHudMessage("스크린샷을 저장하지 못했습니다."); return; }
        if (toNote)
        {
            NotesHost.OpenGame(_currentDir, title);
            NotesHost.AddScreenshot(png);
            ShowHudMessage(NotesVisible ? $"스크린샷: {Path.GetFileName(saved)} (노트에도 추가)" : $"스크린샷: {Path.GetFileName(saved)} (노트에도 추가 · Ctrl+N: 노트 보기)");
        }
        else ShowHudMessage($"스크린샷 저장: {Path.GetFileName(saved)}");
    }

    /// <summary>스크린샷 보관함. 노트에서 열면 고른 스크린샷을 노트에 넣습니다.</summary>
    void OpenGallery(bool insertIntoNotes)
    {
        Action<string>? insert = insertIntoNotes ? path => { SetNotesVisible(true); NotesHost.AddImageFile(path); } : null;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var win = new GalleryWindow(insert, _currentBridge != null ? CurrentGameTitle() : null) { Owner = this, FontFamily = FontFamily };
            win.Show();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}
