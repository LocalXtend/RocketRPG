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
    // ── 비율 시스템 (W2/W3/W4) ──

    void LayoutGameScreen()
    {
        string ratio = _ctl.Settings.Ratio ?? "none";
        if (_ctl.Current.Engine == CoreInterop.EngineMz) ratio = "none";   // MZ는 자체 스케일링이 있어 강제 비율이 손해

        // 메시지 바(퀵 메뉴) 자리: 게임 화면을 그만큼 줄여 대화창을 가리지 않게 합니다.
        double reserve = MessageBarReserve();
        WebScreen.Margin = new Thickness(0, 0, 0, reserve);

        if (ratio == "none" || !TryParseRatio(ratio, out int rw, out int rh))
        {
            NativeScreenHost.Width = double.NaN;
            NativeScreenHost.Height = double.NaN;
            NativeScreenHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            NativeScreenHost.VerticalAlignment = VerticalAlignment.Stretch;
            NativeScreenHost.Margin = new Thickness(0, 0, 0, reserve);

            ResizeHostedGameWindow((int)RenderScreen.ActualWidth, (int)Math.Max(1, RenderScreen.ActualHeight - reserve));
            return;
        }

        double gw = RenderScreen.ActualWidth, gh = RenderScreen.ActualHeight;
        if (gw < 1 || gh < 1) return;
        double avail = Math.Max(1, gh - reserve);
        double scale = Math.Min(gw / rw, avail / rh);
        double targetW = rw * scale;
        double targetH = rh * scale;
        double left = Math.Max(0, (gw - targetW) / 2);
        // 아래 여백(레터박스)이 이미 충분하면 가운데 그대로, 아니면 메시지 바 자리 위로 올립니다.
        double top = Math.Max(0, Math.Min((gh - targetH) / 2, avail - targetH));

        NativeScreenHost.Width = targetW;
        NativeScreenHost.Height = targetH;
        NativeScreenHost.HorizontalAlignment = HorizontalAlignment.Left;
        NativeScreenHost.VerticalAlignment = VerticalAlignment.Top;
        NativeScreenHost.Margin = new Thickness(left, top, 0, 0);

        ResizeHostedGameWindow((int)targetW, (int)targetH);
    }

    // 마지막으로 맞춘 게임 창과 크기. WPF는 그릴 때마다(글자 하나 칠 때마다, 커서가 깜박일 때마다) 크기가 같아도
    // OnWindowPositionChanged를 부릅니다. 다른 프로세스의 게임 창에 SetWindowPos를 하면 게임 스레드가 답할 때까지(~45ms)
    // UI 스레드가 멈춰, 게임을 켠 동안 이름 입력·채팅·노트 입력이 몇 초씩 밀렸습니다. 크기가 그대로면 건너뜁니다.
    IntPtr _hostedHwnd;
    int _hostedW, _hostedH;

    void ResizeHostedGameWindow(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        IntPtr game = _currentBridge == _mkxpRenderer ? _mkxpRenderer.GameHwnd : _currentBridge == _easyRpgRenderer ? _easyRpgRenderer.GameHwnd : IntPtr.Zero;
        if (game != IntPtr.Zero && game == _hostedHwnd && w == _hostedW && h == _hostedH &&
            NativeMethods.GetWindowRect(game, out var now) && now.Right - now.Left == w && now.Bottom - now.Top == h) return;
        _hostedHwnd = game;
        _hostedW = w;
        _hostedH = h;
        IntPtr hwnd = IntPtr.Zero;
        if (_currentBridge == _mkxpRenderer)
        {
            hwnd = _mkxpRenderer.GameHwnd;
            _mkxpRenderer.Resize(w, h);
        }
        else if (_currentBridge == _easyRpgRenderer)
        {
            hwnd = _easyRpgRenderer.GameHwnd;
            _easyRpgRenderer.Resize(w, h);
        }

        if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd))
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, 0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
        }
    }

    static bool TryParseRatio(string s, out int w, out int h)
    {
        w = h = 0;
        var parts = s.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out w) || !int.TryParse(parts[1], out h)) return false;
        return w > 0 && h > 0;
    }

    void RenderScreen_SizeChanged(object s, SizeChangedEventArgs e)
    {
        LayoutGameScreen();
        Dispatcher.BeginInvoke(RenderEspElements, DispatcherPriority.Loaded);
    }

    // ── 화면 > 프레임 ──

    int _frameRate = 60;   // 0 = 무제한
    bool _vsync;

    void CheckFrameItems()
    {
        foreach (var mi in FrameMenu.Items.OfType<MenuItem>())
            if (mi.Tag is string tag && int.TryParse(tag, out int fps)) mi.IsChecked = fps == _frameRate;
        VSyncMenuItem.IsChecked = _vsync;
    }

    /// <summary>
    /// 엔진마다 프레임을 다르게 다룹니다. MV/MZ(브라우저)는 바로 적용하고 VSync가 항상 켜져 있습니다.
    /// 2000/2003은 시작할 때 적용합니다. XP/VX/Ace는 게임 진행이 프레임에 묶여 있어(60 이상 = 배속) VSync만 바꿀 수 있습니다.
    /// </summary>
    void UpdateFrameAvailability()
    {
        bool rgss = _currentBridge == _mkxpRenderer;
        bool web = _currentBridge == _webRenderer;
        foreach (var mi in FrameMenu.Items.OfType<MenuItem>())
        {
            if (mi.Tag is not string tag || !int.TryParse(tag, out int fps)) continue;
            bool ok = !rgss || fps == 60;
            mi.IsEnabled = ok;
            mi.ToolTip = ok ? null : "XP/VX/Ace는 게임 진행 속도가 프레임에 묶여 있어 60 FPS보다 높이면 배속이 됩니다. (배속은 도구 > 배속 설정)";
            ToolTipService.SetShowOnDisabled(mi, true);
        }
        VSyncMenuItem.IsEnabled = !web;
        VSyncMenuItem.ToolTip = web ? "MV/MZ(브라우저 방식)는 항상 모니터 주사율에 맞춰 그립니다." : "모니터 주사율에 맞춰 그려 화면 찢어짐을 막습니다.";
        ToolTipService.SetShowOnDisabled(VSyncMenuItem, true);
    }

    void OnFrameRate(object s, RoutedEventArgs e)
    {
        if (s is not MenuItem mi || mi.Tag is not string tag || !int.TryParse(tag, out int fps)) return;
        int before = _frameRate;
        _frameRate = fps;
        CheckFrameItems();
        SavePerGame();
        ShowHudMessage($"프레임: {(fps == 0 ? "무제한" : fps + " FPS")}");
        if (_currentBridge == _webRenderer) _webRenderer.SetFrameRate(fps);
        else if (_currentBridge == _easyRpgRenderer && before != fps) AskRestartToApply("프레임 설정");
    }

    void OnToggleVSync(object s, RoutedEventArgs e)
    {
        _vsync = VSyncMenuItem.IsChecked;
        SavePerGame();
        ShowHudMessage($"수직 동기화: {(_vsync ? "켜짐" : "꺼짐")}");
        if (_currentBridge == _easyRpgRenderer || _currentBridge == _mkxpRenderer) AskRestartToApply("수직 동기화");
    }

    void OnRatio(object s, RoutedEventArgs e)
    {
        var mi = (MenuItem)s;
        _ctl.Settings.Ratio = (string)mi.Tag;
        CheckItem("ratio", mi.Tag);
        SavePerGame();
        LayoutGameScreen();
    }

    void OnGamma(object s, RoutedEventArgs e)
    {
        var mi = (MenuItem)s;
        double g = double.Parse((string)mi.Tag, System.Globalization.CultureInfo.InvariantCulture);
        _ctl.ApplyGamma(g);
        _currentBridge?.SetBrightness(g);
        CheckItem("gamma", mi.Tag);
        SavePerGame();
        SetStatus($"밝기 {(int)(g * 100)}%");
    }

    // 마스터 볼륨: 슬라이더 대신 단계 메뉴 (밝기와 같은 방식). 저장된 값이 단계 사이면 가장 가까운 단계를 표시합니다.
    void CheckVolumeItems(int volume)
    {
        MenuItem? best = null;
        int bestDiff = int.MaxValue;
        foreach (var mi in VolumeMenu.Items.OfType<MenuItem>())
        {
            mi.IsChecked = false;
            if (mi.Tag is string t && int.TryParse(t, out int lv) && Math.Abs(lv - volume) < bestDiff) { bestDiff = Math.Abs(lv - volume); best = mi; }
        }
        if (best != null) best.IsChecked = true;
    }

    /// <summary>100%를 넘는 증폭은 MV/MZ(게임 자체 오디오)만 됩니다. 나머지는 윈도우 앱 볼륨이라 100%가 최대입니다.</summary>
    void UpdateVolumeAvailability()
    {
        bool amplify = _currentBridge == _webRenderer;
        foreach (var mi in VolumeMenu.Items.OfType<MenuItem>())
        {
            if (mi.Tag is not string t || !int.TryParse(t, out int lv) || lv <= 100) continue;
            mi.IsEnabled = amplify;
            mi.ToolTip = amplify ? null : "100%보다 크게 키우는 것은 MV/MZ 게임에서만 됩니다. (다른 엔진은 윈도우 앱 볼륨이라 100%가 최대)";
            ToolTipService.SetShowOnDisabled(mi, true);
        }
        if (!amplify && _ctl.Settings.Volume > 100) CheckVolumeItems(100);
    }

    void OnVolumeLevel(object s, RoutedEventArgs e)
    {
        if (s is not MenuItem mi || mi.Tag is not string t || !int.TryParse(t, out int v)) return;
        CheckVolumeItems(v);
        _ctl.SetVolume(v);
        SavePerGame();
        if (_isNativeRunning)
        {
            _webRenderer.SetVolume(v);
            _mkxpRenderer.SetVolume(v);
            _easyRpgRenderer.SetVolume(v);
        }
        ShowHudMessage(v == 0 ? "마스터 볼륨: 음소거" : $"마스터 볼륨: {v}%");
        SyncMultiMedia();   // 멀티 참가자: 방장 소리 크기
    }
}
