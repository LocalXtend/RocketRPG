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
    private void InitSpinner()
    {
        SpinnerCanvas.Children.Clear();
        int count = 16;
        double radius = 22.0;
        double dotSize = 6.0;
        double center = 32.0;

        for (int i = 0; i < count; i++)
        {
            double angle = 2.0 * Math.PI * i / count - Math.PI / 2.0;
            double x = center + radius * Math.Cos(angle) - dotSize / 2.0;
            double y = center + radius * Math.Sin(angle) - dotSize / 2.0;

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = dotSize,
                Height = dotSize,
                Fill = new SolidColorBrush(Color.FromRgb(0, 102, 204)),
                Opacity = 0.2
            };
            Canvas.SetLeft(dot, x);
            Canvas.SetTop(dot, y);
            SpinnerCanvas.Children.Add(dot);
            _spinnerDots[i] = dot;
        }

        _spinnerTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinnerStep = (_spinnerStep + 1) % count;
            for (int i = 0; i < count; i++)
            {
                int dist = (i - _spinnerStep + count) % count;
                double ratio = (double)dist / (count - 1);
                double opacity = 0.15 + 0.85 * Math.Pow(ratio, 1.8);
                _spinnerDots[i].Opacity = opacity;
            }
        };
    }

    private void ShowLoading(string gameTitle, string subtitle)
    {
        LoadingGameTitle.Text = string.IsNullOrWhiteSpace(gameTitle) ? "게임 시작 중" : gameTitle;
        LoadingStatusSub.Text = subtitle;
        GameLoadingOverlay.Visibility = Visibility.Visible;
        _spinnerTimer?.Start();
        UiLog.Write($"loading: show '{subtitle}'");
    }

    DispatcherTimer? _loadingTimeout;

    /// <summary>
    /// 엔진이 시작된 뒤에도 게임 화면이 뜰 때까지(MV/MZ는 첫 장면, 네이티브는 게임 창이 붙을 때) '불러오는 중'을 보여 줍니다.
    /// 예전에는 엔진이 시작되자마자 닫혀서 한동안 검은 화면만 보였습니다. 20초가 지나면 그냥 닫습니다.
    /// </summary>
    void HideLoadingWhenReady()
    {
        if (GameLoadingOverlay.Visibility != Visibility.Visible) return;
        LoadingStatusSub.Text = "게임 화면을 불러오는 중...";
        _loadingTimeout?.Stop();
        _loadingTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _loadingTimeout.Tick += (_, _) => HideLoadingNow();
        _loadingTimeout.Start();
        IntPtr hw = _currentBridge is RocketRenderMKXP mk ? mk.GameHwnd : _currentBridge is RocketRenderEasyRPG er ? er.GameHwnd : IntPtr.Zero;
        if (hw != IntPtr.Zero) HideLoadingSoon();   // 네이티브 게임 창이 이미 붙어 있음
    }

    /// <summary>게임 창이 붙은 뒤 첫 화면이 그려질 틈을 조금 두고 닫습니다.</summary>
    void HideLoadingSoon()
    {
        if (GameLoadingOverlay.Visibility != Visibility.Visible || _loadingTimeout == null) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) => { t.Stop(); HideLoadingNow(); };
        t.Start();
    }

    void HideLoadingNow()
    {
        UiLog.Write("loading: ready -> hide");
        _loadingTimeout?.Stop();
        _loadingTimeout = null;
        HideLoading();
    }

    private void HideLoading()
    {
        if (GameLoadingOverlay.Visibility == Visibility.Visible)
        {
            UiLog.Write("loading: hide");
            _spinnerTimer?.Stop();
            GameLoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }


    // 진단용: RR_SWITCHTEST="dir1|dir2" 환경변수가 있으면 게임 로드→전환을 자동 수행하고 종료 (RR_AUTOTEST는 호환성 자동 테스트용)
    async void StartAutoTestIfRequested()
    {
        var spec = Environment.GetEnvironmentVariable("RR_SWITCHTEST");
        var marker = System.IO.Path.Combine(SettingsService.Root(), "config", "autotest.txt");
        if (string.IsNullOrEmpty(spec) && File.Exists(marker))
            spec = File.ReadAllText(marker).Trim();
        if (string.IsNullOrEmpty(spec)) return;
        var dirs = spec.Split('|');
        await Task.Delay(2000);
        UiLog.Write($"autotest: launch #{1} {dirs[0]}");
        Launch(dirs[0]);
        for (int i = 1; i < dirs.Length; i++)
        {
            await Task.Delay(12000);
            UiLog.Write($"autotest: switch to {dirs[i]}");
            Launch(dirs[i]);
        }
        await Task.Delay(10000);
        UiLog.Write("autotest: done, closing");
        Close();
    }

    // 화면의 지정 영역을 32비트 top-down DIB로 캡처해 관리 배열로 반환합니다.
    static byte[] CaptureScreenRect(int x, int y, int w, int h)
    {
        var buf = new byte[w * h * 4];
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var bmi = new NativeMethods.BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;

            IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr dib = NativeMethods.CreateDIBSection(memDc, ref bmi, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection failed");
            try
            {
                IntPtr old = NativeMethods.SelectObject(memDc, dib);
                if (!NativeMethods.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, NativeMethods.SRCCOPY))
                    throw new InvalidOperationException("BitBlt failed");
                Marshal.Copy(bits, buf, 0, buf.Length);
                NativeMethods.SelectObject(memDc, old);
            }
            finally
            {
                NativeMethods.DeleteObject(dib);
                NativeMethods.DeleteDC(memDc);
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
        return buf;
    }

    // M6: 하단 상태 표시줄이 제거되어 기존 호출부 유지를 위한 no-op입니다.
    void SetStatus(string s, string? engine = null) { }

    static string EngineName(int e) => e switch
    {
        CoreInterop.Engine2000 => "RPG Maker 2000", CoreInterop.Engine2003 => "RPG Maker 2003",
        CoreInterop.EngineXp => "RPG Maker XP", CoreInterop.EngineVx => "RPG Maker VX",
        CoreInterop.EngineAce => "RPG Maker VX Ace", CoreInterop.EngineMv => "RPG Maker MV",
        CoreInterop.EngineMz => "RPG Maker MZ", _ => "?"
    };
}
