#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RocketRPG.Models;

namespace RocketRPG.Views;

// ── 멀티 채팅과 핑 ──
// 채팅: Ctrl+T로 입력, 게임 화면 위로 오른쪽→왼쪽 흐름(이름 없이), 채팅 기록 창에는 이름과 함께.
// 핑: 휠(가운데) 클릭. 1초에 한 번, 새로 찍으면 내 이전 핑은 사라짐. 맵에서는 카메라를 따라 움직이다 화면 밖으로 나가면 사라짐.
// 둘 다 RocketRPG 창 맨 위 층(MultiOverlay)에 그려 디스코드 창 공유에도 보입니다.
public partial class MainWindow
{
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref NativeMethods.POINT p);

    /// <summary>방장 게임의 카메라: 맵 번호(맵이 아니면 0), 카메라 위치(게임 픽셀), 게임 화면 크기</summary>
    sealed record MultiCam(int Map, double Cx, double Cy, int Bw, int Bh);

    sealed class PingMark
    {
        public int OverlayId;
        public double X, Y, Cx, Cy;
        public int Map;
        public uint Color;
        public DateTime Created;
    }

    const double PingSeconds = 4;
    const int ChatLogMax = 500;

    MultiOverlay? _multiOverlay;
    readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    int _overlayTicks;
    readonly List<MultiChatLine> _chatLog = new();
    ChatLogWindow? _chatLogWnd;
    ChatInputWindow? _chatInput;
    readonly Dictionary<string, PingMark> _pings = new();
    int _pingSeq;
    DateTime _lastPingSent = DateTime.MinValue;
    MultiCam? _remoteCam;          // 참가자: 방장에게서 받은 카메라
    MultiCam? _camSent;            // 방장: 마지막으로 보낸 카메라
    Rect _guestVideoRect = Rect.Empty;   // 참가자: 방송 페이지 안 영상이 그려진 곳 (DIP)

    void InitMultiChat()
    {
        _multi!.ChatReceived += OnMultiChat;
        _multi.PingReceived += OnMultiPing;
        _overlayTimer.Tick += (_, _) => OverlayTick();
    }

    /// <summary>방에 있는 동안만 층을 둠 (UpdateMultiUi에서 부름)</summary>
    void SyncMultiOverlay()
    {
        bool inRoom = _multi?.InRoom == true;
        if (inRoom && _multiOverlay == null)
        {
            _multiOverlay = MultiOverlay.Open(new WindowInteropHelper(this).Handle);
            _overlayTimer.Start();
        }
        else if (!inRoom && _multiOverlay != null)
        {
            _overlayTimer.Stop();
            _multiOverlay.Dispose();
            _multiOverlay = null;
            _pings.Clear();
            _remoteCam = _camSent = null;
            _chatInput?.Close();
        }
        if (!inRoom && _chatLog.Count > 0)
        {
            _chatLog.Clear();
            _chatLogWnd?.SetLines(_chatLog);
        }
    }

    // ── 영역 ──

    /// <summary>채팅·핑이 보이는 곳 = 게임 화면 (화면 좌표, 물리 픽셀). 참가자는 방장 영상이 그려진 곳.</summary>
    bool TryMultiArea(out Rect screen)
    {
        screen = Rect.Empty;
        try
        {
            if (IsMultiGuest)
            {
                if (_multiView is not { IsVisible: true } view) return false;
                Rect r = _guestVideoRect.IsEmpty || _guestVideoRect.Width < 8 ? new Rect(0, 0, view.ActualWidth, view.ActualHeight) : _guestVideoRect;
                screen = new Rect(view.PointToScreen(r.TopLeft), view.PointToScreen(r.BottomRight));
                return screen.Width > 8 && screen.Height > 8;
            }
            if (!RenderScreen.IsVisible) return false;
            Rect a = new(0, 0, RenderScreen.ActualWidth, RenderScreen.ActualHeight);
            if (_currentBridge != null)
            {
                GetGameViewport(out double l, out double t, out double w, out double h, out _, out _);
                a = new Rect(l, t, w, h);
            }
            screen = new Rect(RenderScreen.PointToScreen(a.TopLeft), RenderScreen.PointToScreen(a.BottomRight));
            return screen.Width > 8 && screen.Height > 8;
        }
        catch (InvalidOperationException) { return false; }   // 창이 아직 화면에 없음
    }

    void OverlayTick()
    {
        if (_multiOverlay == null || _multi?.InRoom != true) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (TryMultiArea(out var s))
        {
            var tl = new NativeMethods.POINT { X = (int)Math.Round(s.X), Y = (int)Math.Round(s.Y) };
            ScreenToClient(hwnd, ref tl);
            _multiOverlay.SetArea(tl.X, tl.Y, (int)Math.Round(s.Width), (int)Math.Round(s.Height));
        }
        else _multiOverlay.SetArea(0, 0, 0, 0);
        UpdatePings();
        if (_multi.IsHost) CheckRemoteKeyLease();
        if (_overlayTicks % 5 == 0) SyncMultiTools(force: _overlayTicks % 40 == 0);   // 바뀌면 바로, 2초마다는 그대로라도 (빠진 것 메우기)
        if (++_overlayTicks % 6 == 0) _multiOverlay.Tick();
    }

    // ── 채팅 ──

    void OnMultiChat(string fromId, string name, string text, int color, bool fixedLane)
    {
        if (fixedLane && fromId == _multi?.MyId) _multi.FixedChatReadyAt = Environment.TickCount64 + FixedChatMs;
        _chatLog.Add(new MultiChatLine(DateTime.Now, name, text, fromId == _multi?.MyId, color, fixedLane));
        if (_chatLog.Count > ChatLogMax) _chatLog.RemoveRange(0, _chatLog.Count - ChatLogMax);
        _chatLogWnd?.SetLines(_chatLog);
        if (!_ctl.Settings.MultiHideChat) _multiOverlay?.Chat(text, color, fixedLane);
    }

    const long FixedChatMs = 90_000;

    /// <summary>고정 채팅을 다시 보낼 수 있을 때까지 남은 초 (0 = 지금 보낼 수 있음)</summary>
    int FixedChatWaitSeconds() =>
        _multi == null ? 0 : (int)Math.Ceiling(Math.Max(0, _multi.FixedChatReadyAt - Environment.TickCount64) / 1000.0);

    void SendStyledChat(string text, int color, bool fixedLane)
    {
        if (fixedLane && FixedChatWaitSeconds() is int wait && wait > 0)
        {
            ShowHudMessage($"고정 채팅은 {wait}초 뒤에 보낼 수 있습니다.");
            return;
        }
        _multi?.Send(new { t = "chat", text, color, @fixed = fixedLane });
    }

    void OpenMultiChat()
    {
        if (_multi?.InRoom != true) { ShowHudMessage("방에 들어가 있을 때 채팅할 수 있습니다."); return; }
        if (_chatInput != null) { _chatInput.Activate(); return; }
        TryMultiArea(out var area);
        var dpi = VisualTreeHelper.GetDpi(this);
        _chatInput = new ChatInputWindow(this, FixedChatWaitSeconds);
        _chatInput.Sent += (text, color, fixedLane) => SendStyledChat(text, color, fixedLane);
        _chatInput.Closed += (_, _) => { _chatInput = null; RefocusGame(); };
        if (!area.IsEmpty)
        {
            double w = Math.Clamp(area.Width / dpi.DpiScaleX * 0.7, 280, 640);
            _chatInput.Width = w;
            _chatInput.Left = (area.X + area.Width / 2) / dpi.DpiScaleX - w / 2;
            _chatInput.Top = area.Bottom / dpi.DpiScaleY - 64;
        }
        _chatInput.Show();
    }

    /// <summary>채팅 입력을 닫은 뒤 키보드를 다시 게임으로</summary>
    void RefocusGame()
    {
        Activate();
        IntPtr game = EmbeddedGameHwnd();
        if (game != IntPtr.Zero) SetFocusNative(game);
        else if (IsMultiGuest) _multiView?.Focus();
        else if (_currentBridge == _webRenderer) WebScreen.Focus();
    }

    [DllImport("user32.dll", EntryPoint = "SetFocus")] static extern IntPtr SetFocusNative(IntPtr hWnd);

    void OnMultiChatMenu(object sender, RoutedEventArgs e) => OpenMultiChat();

    void OnMultiChatLog(object sender, RoutedEventArgs e)
    {
        if (_chatLogWnd != null) { _chatLogWnd.Activate(); return; }
        _chatLogWnd = new ChatLogWindow(this, FixedChatWaitSeconds);
        _chatLogWnd.Sent += (text, color, fixedLane) => SendStyledChat(text, color, fixedLane);
        _chatLogWnd.Closed += (_, _) => _chatLogWnd = null;
        _chatLogWnd.SetLines(_chatLog);
        _chatLogWnd.Show();
    }

    // ── 핑 ──

    /// <summary>휠 클릭 (NativeHotkeyHook의 마우스 훅에서, 화면 좌표 물리 픽셀)</summary>
    void OnMultiMiddleClick(int x, int y)
    {
        if (_multi?.InRoom != true || !TryMultiArea(out var area) || !area.Contains(new Point(x, y))) return;
        if ((DateTime.Now - _lastPingSent).TotalSeconds < 1) return;   // 1초에 한 번
        _lastPingSent = DateTime.Now;
        double nx = (x - area.X) / area.Width, ny = (y - area.Y) / area.Height;
        var cam = CurrentCam();
        int map = cam?.Map ?? 0;
        double cx = cam?.Cx ?? 0, cy = cam?.Cy ?? 0;
        _multi.Send(new { t = "ping", x = nx, y = ny, map, cx, cy });
        ShowPing(_multi.MyId, nx, ny, map, cx, cy);   // 내 핑은 바로 보임 (서버에서 돌아오는 것은 건너뜀)
    }

    void OnMultiPing(JsonObject msg)
    {
        string from = msg["from"]?.GetValue<string>() ?? "";
        if (from.Length == 0 || from == _multi?.MyId) return;
        double N(string k) => msg[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        UiLog.Write($"multi: ping from {from} at {N("x"):0.00},{N("y"):0.00} map {N("map")}");
        ShowPing(from, N("x"), N("y"), (int)N("map"), N("cx"), N("cy"));
    }

    void ShowPing(string from, double x, double y, int map, double cx, double cy)
    {
        if (_pings.Remove(from, out var old)) _multiOverlay?.RemovePing(old.OverlayId);   // 새로 찍으면 이전 핑은 사라짐
        int color = _multi?.Members.FirstOrDefault(m => m.Id == from)?.Color ?? 0;
        var p = new PingMark { OverlayId = ++_pingSeq, X = x, Y = y, Map = map, Cx = cx, Cy = cy, Created = DateTime.Now,
            Color = MultiChatStyle.PingColors[Math.Clamp(color, 0, 7)] };
        _pings[from] = p;
        if (!_ctl.Settings.MultiHidePings) _multiOverlay?.Ping(p.OverlayId, x, y, fresh: true, p.Color);
    }

    void UpdatePings()
    {
        if (_pings.Count == 0) return;
        var cam = CurrentCam();
        foreach (var (from, p) in _pings.ToList())
        {
            double nx = p.X, ny = p.Y;
            bool gone = (DateTime.Now - p.Created).TotalSeconds > PingSeconds;
            if (!gone && p.Map > 0)
            {
                // 맵 위의 핑: 찍은 뒤 카메라가 움직인 만큼 반대로 (맵이 바뀌거나 메뉴·전투로 가면 사라짐).
                // 방장 카메라를 아직 못 받았으면 찍은 자리에 그대로 둠.
                if (cam == null || cam.Bw <= 0 || cam.Bh <= 0) { }
                else if (cam.Map != p.Map) gone = true;
                else
                {
                    nx = p.X - (cam.Cx - p.Cx) / cam.Bw;
                    ny = p.Y - (cam.Cy - p.Cy) / cam.Bh;
                    gone = nx < -0.03 || nx > 1.03 || ny < -0.03 || ny > 1.03;   // 화면 밖으로 나감
                }
            }
            if (gone)
            {
                _pings.Remove(from);
                _multiOverlay?.RemovePing(p.OverlayId);
            }
            else if (!_ctl.Settings.MultiHidePings) _multiOverlay?.Ping(p.OverlayId, nx, ny, fresh: false, p.Color);
        }
    }

    // ── 카메라 (방장이 참가자에게, WebRTC 데이터 채널로) ──

    MultiCam? CurrentCam() => IsMultiGuest ? _remoteCam : HostCam();

    /// <summary>방장 게임의 카메라를 게임 픽셀로. 엔진마다 단위가 달라 맞춥니다.</summary>
    MultiCam? HostCam()
    {
        if (_currentBridge == null || !_isNativeRunning) return null;
        var st = _currentBridge.LatestState;
        GetGameViewport(out _, out _, out _, out _, out int bw, out int bh);
        double k = _ctl.Current.Engine switch
        {
            CoreInterop.Engine2000 or CoreInterop.Engine2003 => 1 / 16.0,   // EasyRPG: 1/16 픽셀
            CoreInterop.EngineXp => 1 / 4.0,                                // RGSS1: 1/4 픽셀
            CoreInterop.EngineVx => 1 / 8.0,                                // RGSS2: 1/8 픽셀
            CoreInterop.EngineAce => 32,                                    // RGSS3: 타일 (32픽셀)
            CoreInterop.EngineMv or CoreInterop.EngineMz => 48,             // MV/MZ: 타일 (48픽셀)
            _ => 0
        };
        bool onMap = st.MapId > 0 && k > 0 && st.Scene is "Scene_Map" or "Map";
        return new MultiCam(onMap ? st.MapId : 0, st.DisplayX * k, st.DisplayY * k, bw, bh);
    }

    /// <summary>방장: 게임 상태가 바뀌면 카메라를 참가자에게 (바뀐 때만)</summary>
    void MultiOnGameState()
    {
        if (_multi is not { InRoom: true, IsHost: true } || _multiView?.CoreWebView2 == null) return;
        var cam = HostCam();
        if (cam == null || cam == _camSent) return;
        if (_camSent == null || _camSent.Map != cam.Map) UiLog.Write($"multi: camera map {cam.Map} ({cam.Cx:0},{cam.Cy:0}) screen {cam.Bw}x{cam.Bh}");
        _camSent = cam;
        PostMulti(JsonSerializer.Serialize(new { t = "cam", map = cam.Map, cx = cam.Cx, cy = cam.Cy, bw = cam.Bw, bh = cam.Bh }));
    }

    /// <summary>참가자: 방송 페이지가 알려 준 카메라 / 영상 위치</summary>
    void OnMultiPageCam(JsonObject m)
    {
        double N(string k) => m[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        var cam = new MultiCam((int)N("map"), N("cx"), N("cy"), (int)N("bw"), (int)N("bh"));
        if (_remoteCam == null || _remoteCam.Map != cam.Map) UiLog.Write($"multi: host camera map {cam.Map} ({cam.Cx:0},{cam.Cy:0}) screen {cam.Bw}x{cam.Bh}");
        _remoteCam = cam;
    }

    void OnMultiPageVideoRect(JsonObject m)
    {
        double N(string k) => m[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        _guestVideoRect = new Rect(N("x"), N("y"), Math.Max(0, N("w")), Math.Max(0, N("h")));
    }

    void OnMultiHidePingsChanged()
    {
        if (_ctl.Settings.MultiHidePings) _multiOverlay?.ClearPings();
    }

    void OnMultiHideChatChanged()
    {
        if (_ctl.Settings.MultiHideChat) _multiOverlay?.ClearChat();
    }
}

public sealed record MultiChatLine(DateTime Time, string Name, string Text, bool Mine, int Color = 0, bool Fixed = false);
