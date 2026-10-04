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
    // ── 입력 전달 (I1): 에뮬레이터 창에서 게임 창으로 키보드/마우스 전달 ──

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 게임 노트에서 글을 쓰는 중이면 키를 게임으로 보내지 않고, 노트 관련 단축키만 받습니다.
        bool inNotes = NotesHost.IsKeyboardFocusWithin;
        string? actionId = HotkeyManager.MatchAction(e);
        if (actionId != null && (!inNotes || actionId is "ToggleNotes" or "ScreenshotToNote" or "Screenshot"))
        {
            // 키를 누르고 있으면 반복 입력이 와서 켜기/끄기가 계속 뒤집혔습니다. 반복은 삼키기만 합니다.
            if (e.IsRepeat)
            {
                e.Handled = true;
                return;
            }
            if (ExecuteHotkeyAction(actionId))
            {
                e.Handled = true;
                return;
            }
        }
        if (inNotes) return;

        if (ForwardKey(e, down: true) || ForwardGuestKey(e, down: true))
        {
            e.Handled = true;
        }
    }

    void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (NotesHost.IsKeyboardFocusWithin) return;
        if (ForwardKey(e, down: false) || ForwardGuestKey(e, down: false))
        {
            e.Handled = true;
        }
    }

    // 메뉴 줄 숨기기/보이기 (기본 Alt+H, 단축키 설정에서 바꿀 수 있음).
    // 예전에는 Alt 한 번으로 바꿨는데 Alt+Tab 같은 윈도우 조합키와 메뉴 단축 글자(Alt+F 등)까지 막혀 바꿨습니다.
    void ToggleMenuBar()
    {
        MainMenu.Visibility = MainMenu.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (MainMenu.Visibility == Visibility.Collapsed)
            ShowHudMessage($"메뉴를 숨겼습니다. {HotkeyManager.MenuText(HotkeyManager.GestureOf("ToggleMenuBar"))}를 누르면 다시 보입니다.", 3000);
        if (MainMenu.Visibility == Visibility.Collapsed && MainMenu.IsKeyboardFocusWithin)
        {
            if (_isNativeRunning) FocusGame();
            else RenderScreen.Focus();
        }
    }

    bool ForwardKey(KeyEventArgs e, bool down)
    {
        if (EmbeddedGameHwnd() == IntPtr.Zero) return false;
        if (MainMenu.IsKeyboardFocusWithin && MainMenu.Items.OfType<MenuItem>().Any(m => m.IsSubmenuOpen)) return false;

        Key k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (k is Key.LeftAlt or Key.RightAlt) return false;
        int vk = KeyInterop.VirtualKeyFromKey(k);
        if (vk == 0) return false;

        return PostGameKey(vk, down, e.IsRepeat);
    }

    /// <summary>임베드된 네이티브 게임(mkxp-z/EasyRPG) 창, 없으면 Zero (MV/MZ는 WebView2가 직접 입력을 받음)</summary>
    IntPtr EmbeddedGameHwnd()
    {
        if (!_isNativeRunning) return IntPtr.Zero;
        if (_currentBridge == _mkxpRenderer && _mkxpRenderer.IsRunning) return _mkxpRenderer.GameHwnd;
        if (_currentBridge == _easyRpgRenderer && _easyRpgRenderer.IsRunning) return _easyRpgRenderer.GameHwnd;
        return IntPtr.Zero;
    }

    bool TryMapMouseToGame(out double gx, out double gy)
    {
        bool ok = TryMapMouseToTarget(out _, out gx, out gy);
        if (!ok) { gx = gy = 0; }
        return ok;
    }

    void GetGameViewport(out double vpLeft, out double vpTop, out double vpWidth, out double vpHeight, out int baseW, out int baseH)
    {
        if (_currentBridge is RocketRenderMKXP mk)
        {
            baseW = mk.BaseWidth;
            baseH = mk.BaseHeight;
        }
        else if (_currentBridge is RocketRenderEasyRPG er)
        {
            baseW = er.BaseWidth;
            baseH = er.BaseHeight;
        }
        else
        {
            baseW = 816;   // MV 기본 해상도 (MV/MZ는 WebView2가 직접 그려 좌표 변환에 거의 쓰이지 않음)
            baseH = 624;
        }
        if (baseW <= 0) baseW = 640;
        if (baseH <= 0) baseH = 480;

        double gw = RenderScreen.ActualWidth;
        double gh = RenderScreen.ActualHeight;
        if (gw < 1) gw = 1;
        if (gh < 1) gh = 1;

        if (NativeScreenHost.Visibility == Visibility.Visible)
        {
            bool sized = !double.IsNaN(NativeScreenHost.Width) && NativeScreenHost.Width > 0 &&
                         !double.IsNaN(NativeScreenHost.Height) && NativeScreenHost.Height > 0;
            double hl = sized ? NativeScreenHost.Margin.Left : 0;
            double ht = sized ? NativeScreenHost.Margin.Top : 0;
            double hw = sized ? NativeScreenHost.Width : gw;
            double hh = sized ? NativeScreenHost.Height : Math.Max(1, gh - MessageBarReserve());
            // 비율 고정 시 네이티브 런타임은 호스트 영역 안에서 원본 비율로 레터박스합니다.
            bool fixedAspect = (_ctl.Settings.Ratio ?? "none") != "none";
            if (fixedAspect)
            {
                double s = Math.Min(hw / baseW, hh / baseH);
                vpWidth = baseW * s;
                vpHeight = baseH * s;
                vpLeft = hl + (hw - vpWidth) / 2;
                vpTop = ht + (hh - vpHeight) / 2;
            }
            else
            {
                vpLeft = hl; vpTop = ht; vpWidth = hw; vpHeight = hh;
            }
            return;
        }

        string ratio = _ctl.Settings.Ratio ?? "none";
        if (_ctl.Current.Engine == CoreInterop.EngineMz) ratio = "none";

        int rw = baseW, rh = baseH;
        if (ratio != "none" && TryParseRatio(ratio, out int prw, out int prh))
        {
            rw = prw;
            rh = prh;
        }

        gh = Math.Max(1, gh - MessageBarReserve());   // WebView는 메시지 바 자리를 뺀 영역을 씁니다
        if (ratio != "none")
        {
            double scale = Math.Min(gw / rw, gh / rh);
            vpWidth = rw * scale;
            vpHeight = rh * scale;
            vpLeft = Math.Max(0, (gw - vpWidth) / 2);
            vpTop = Math.Max(0, (gh - vpHeight) / 2);
        }
        else
        {
            vpLeft = 0;
            vpTop = 0;
            vpWidth = gw;
            vpHeight = gh;
        }
    }

    // RenderScreen 좌표 → 게임 픽셀 (임베드된 게임 창 기준)
    bool TryMapMouseToTarget(out IntPtr hwnd, out double tx, out double ty)
    {
        hwnd = EmbeddedGameHwnd(); tx = ty = 0;
        if (hwnd == IntPtr.Zero) return false;

        GetGameViewport(out double vpLeft, out double vpTop, out double vpWidth, out double vpHeight, out int baseW, out int baseH);
        if (vpWidth <= 0 || vpHeight <= 0) return false;

        Point p = CursorInRenderScreen();
        if (p.X < vpLeft || p.X > vpLeft + vpWidth || p.Y < vpTop || p.Y > vpTop + vpHeight) return false;

        tx = (p.X - vpLeft) / vpWidth * baseW;
        ty = (p.Y - vpTop) / vpHeight * baseH;
        return true;
    }

    IntPtr PointsLParam(double gx, double gy) =>
        (IntPtr)(unchecked((short)Math.Round(gx)) & 0xFFFF | ((unchecked((short)Math.Round(gy)) & 0xFFFF) << 16));

    IntPtr MouseWParam()
    {
        int m = 0;
        if (Mouse.LeftButton == MouseButtonState.Pressed) m |= MK_LBUTTON;
        if (Mouse.RightButton == MouseButtonState.Pressed) m |= MK_RBUTTON;
        if (Mouse.MiddleButton == MouseButtonState.Pressed) m |= MK_MBUTTON;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) m |= MK_SHIFT;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) m |= MK_CONTROL;
        return (IntPtr)m;
    }

    void PostMouseButton(uint msg)
    {
        if (msg == 0) return;
        if (!TryMapMouseToTarget(out IntPtr hwnd, out double tx, out double ty)) return;
        NativeMethods.PostMessageW(hwnd, msg, MouseWParam(), PointsLParam(tx, ty));
    }

    void OnGameMouseMove(object s, MouseEventArgs e)
    {
        if (!TryMapMouseToTarget(out IntPtr hwnd, out double tx, out double ty)) return;
        if (_currentBridge is RocketRenderMKXP mkxp) mkxp.InnerRubyBridge?.UpdateVirtualMouse(tx, ty);
        else if (_currentBridge is RocketRenderEasyRPG easyRpg) easyRpg.InnerBridge?.UpdateVirtualMouse(tx, ty);
        NativeMethods.PostMessageW(hwnd, WM_MOUSEMOVE, MouseWParam(), PointsLParam(tx, ty));
    }

    void OnGameMouseDown(object s, MouseButtonEventArgs e) =>
        PostMouseButton(ButtonMessage(e.ChangedButton, down: true));
    void OnGameMouseUp(object s, MouseButtonEventArgs e) => PostMouseButton(ButtonMessage(e.ChangedButton, down: false));

    static uint ButtonMessage(MouseButton b, bool down) => b switch
    {
        MouseButton.Left => down ? WM_LBUTTONDOWN : WM_LBUTTONUP,
        MouseButton.Right => down ? WM_RBUTTONDOWN : WM_RBUTTONUP,
        MouseButton.Middle => down ? WM_MBUTTONDOWN : WM_MBUTTONUP,
        _ => 0
    };

    void OnKeyDown(object s, KeyEventArgs e)
    {
    }

    void OnRestart(object s, RoutedEventArgs e)
    {
        if (!_isNativeRunning) return;
        if (_currentBridge == _webRenderer) _webRenderer.Restart();
        else if (!string.IsNullOrEmpty(_currentDir)) Launch(_currentDir);
        ShowHudMessage("게임을 재시작했습니다.");
    }

    void OnGameExit(object s, RoutedEventArgs e)
    {
        if (_isNativeRunning) StopNativeSession("게임을 종료했습니다.");
    }

    NativeHotkeyHook? _nativeHotkeys;
    volatile int _hotkeyGamePid;
    // 참가자 단축키 범위 (훅 스레드에서 읽음): 0 = 모두, 1 = 메시지 바 동작 + 노트·채팅·스크린샷, 2 = 노트·채팅·스크린샷만
    volatile int _guestHotkeyLevel;

    bool GuestHotkeyAllowed(string id) => _guestHotkeyLevel switch
    {
        0 => true,
        1 => id is "ToggleNotes" or "MultiChat" or "Screenshot" or "ScreenshotToNote" || BarActions.Contains(id),
        _ => id is "ToggleNotes" or "MultiChat" or "Screenshot" or "ScreenshotToNote",
    };
    readonly DispatcherTimer _nativeMouseTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>임베드된 네이티브 게임(mkxp-z/EasyRPG) 프로세스 ID, 없으면 0</summary>
    int NativeGamePid =>
        !_isNativeRunning ? 0 :
        ReferenceEquals(_currentBridge, _mkxpRenderer) ? _mkxpRenderer.ProcessId :
        ReferenceEquals(_currentBridge, _easyRpgRenderer) ? _easyRpgRenderer.ProcessId : 0;

    bool IsCursorOverGame()
    {
        var p = CursorInRenderScreen();
        return p.X >= 0 && p.Y >= 0 && p.X < RenderScreen.ActualWidth && p.Y < RenderScreen.ActualHeight;
    }

    /// <summary>
    /// RenderScreen 기준 커서 위치. 임베드된 게임 창/WebView2(별도 HWND) 위에서는 WPF가 마우스 이동을 받지 못해
    /// Mouse.GetPosition이 마지막으로 WPF 위에 있던 위치(예: 메뉴 바)에 멈춰 있으므로, 실제 커서 위치를 윈도우에서 읽습니다.
    /// </summary>
    Point CursorInRenderScreen()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return Mouse.GetPosition(RenderScreen);
        try { return RenderScreen.PointFromScreen(new Point(pt.X, pt.Y)); }
        catch (InvalidOperationException) { return Mouse.GetPosition(RenderScreen); }
    }
    // 임베드된 게임 창 위의 마우스는 WPF가 받지 못하므로 커서 위치를 주기적으로 읽어 타일 인스펙터에 전달합니다.
    void PumpNativeTileMouse()
    {
        if (!TileInspectorMenuItem.IsChecked || NativeGamePid == 0) return;
        GetGameViewport(out double vpLeft, out double vpTop, out double vpWidth, out double vpHeight, out int baseW, out int baseH);
        if (vpWidth < 1 || vpHeight < 1) return;
        Point p = CursorInRenderScreen();
        double gx = (p.X - vpLeft) * baseW / vpWidth;
        double gy = (p.Y - vpTop) * baseH / vpHeight;
        bool inside = gx >= 0 && gy >= 0 && gx < baseW && gy < baseH;
        if (_currentBridge is RocketRenderMKXP mk)
        {
            if (inside) mk.UpdateVirtualMouse(gx, gy); else mk.UpdateVirtualMouse(null, null);
        }
        else if (_currentBridge is RocketRenderEasyRPG er && inside)
        {
            er.InnerBridge?.UpdateVirtualMouse(gx, gy);
        }
        if (!inside) TileInspectorHud.Visibility = Visibility.Collapsed;
    }

    // 게임이 스스로 창 크기/위치를 바꾸거나(해상도 변경 스크립트, Graphics.resize_screen, Win32API 호출)
    // 부모에서 떨어져 나가면 게임 화면이 영역 밖으로 벗어납니다. 주기적으로 확인해 호스트 영역에 다시 맞춥니다.
    readonly DispatcherTimer _embedWatchdog = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool _embedFixLogged;

    void EnforceEmbeddedWindow()
    {
        if (!_isNativeRunning) return;
        IntPtr game, parent;
        Action<IntPtr, int, int> rehost;
        if (_currentBridge is RocketRenderMKXP mk) { game = mk.GameHwnd; parent = mk.ParentHwnd; rehost = (p, w, h) => mk.HostWindow(p, 0, 0, w, h); }
        else if (_currentBridge is RocketRenderEasyRPG er) { game = er.GameHwnd; parent = er.ParentHwnd; rehost = (p, w, h) => er.HostWindow(p, 0, 0, w, h); }
        else return;
        if (game == IntPtr.Zero || parent == IntPtr.Zero || !NativeMethods.IsWindow(game) || !NativeMethods.IsWindow(parent)) return;
        if (!NativeMethods.GetClientRect(parent, out var pr) || pr.Right <= 0 || pr.Bottom <= 0) return;
        int w = pr.Right, h = pr.Bottom;

        const int GWL_STYLE = -16;
        const long WS_CHILD = 0x40000000;
        bool detached = NativeMethods.GetParent(game) != parent ||
                        (NativeMethods.GetWindowLongPtr(game, GWL_STYLE).ToInt64() & WS_CHILD) == 0;
        if (detached)
        {
            UiLog.Write("embed: game window left its host, re-attaching");
            rehost(parent, w, h);
            return;
        }
        // 임베드된 게임 창이 "전경 창"으로 남아 있으면(게임이 스스로 창을 올린 경우) RocketRPG 창을 다시 활성화합니다.
        if (NativeMethods.GetForegroundWindow() == game) FocusGame();
        if (!NativeMethods.GetWindowRect(game, out var r)) return;
        var p0 = new NativeMethods.POINT { X = r.Left, Y = r.Top };
        NativeMethods.ScreenToClient(parent, ref p0);
        if (p0.X != 0 || p0.Y != 0 || r.Right - r.Left != w || r.Bottom - r.Top != h)
        {
            if (!_embedFixLogged)
            {
                _embedFixLogged = true;
                UiLog.Write($"embed: game window moved/resized itself to ({p0.X},{p0.Y}) {r.Right - r.Left}x{r.Bottom - r.Top}, restoring {w}x{h}");
            }
            NativeMethods.SetWindowPos(game, IntPtr.Zero, 0, 0, w, h, 0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
        }
    }
}
