#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RocketRPG.Models;

namespace RocketRPG.Views;

// ── 멀티 영상·소리: 방송 페이지(WebView2 + WebRTC)와 방장 화면·소리 캡처 ──
// 방장: 게임 화면·소리를 공유 메모리로 페이지에 넘기고, 페이지가 참가자마다 WebRTC로 보냅니다 (직접 연결만, 중계 서버 없음).
// 참가자: 게임 화면 자리에 페이지를 띄워 방장 영상을 재생합니다. 연결 협상 메시지는 방 서버를 거쳐 오갑니다.
public partial class MainWindow
{
    const string MultiPageHost = "https://rocketrpg.multi/";
    WebView2? _multiView;
    Task<bool>? _multiViewInit;
    TaskCompletionSource<bool>? _multiPageReady;
    CoreWebView2SharedBuffer? _multiShared;
    bool _multiSharedPosted;
    MultiStreamer? _streamer;
    // 페이지에 마지막으로 보낸 값 (바뀔 때만 다시 보냄)
    string _multiPageRole = "", _multiPageMe = "", _multiMembersSent = "", _multiQualitySent = "", _multiStatusSent = "\u0001", _multiHostSent = "";
    int _multiVolumeSent = -1;
    bool _multiGuestAudioMenu;   // 참가자: 소리 메뉴(볼륨)를 방장 소리에 쓰도록 켜 둠
    /// <summary>방장: 직통 연결이 안 되는 참가자 id → 막힌 쪽 ("me" = 방장 네트워크, "peer" = 참가자 네트워크, "" = 모름)</summary>
    readonly System.Collections.Generic.Dictionary<string, string> _multiBlocked = new();
    string _multiLink = "";   // 참가자: 방장과의 연결 ("ok" / "blocked" / "")

    Task<bool> EnsureMultiView() => _multiViewInit ??= CreateMultiViewAsync();

    async Task<bool> CreateMultiViewAsync()
    {
        try
        {
            var view = new WebView2 { Visibility = Visibility.Collapsed, DefaultBackgroundColor = System.Drawing.Color.Black, Focusable = false };
            RenderScreen.Children.Add(view);
            _multiView = view;
            // 게임용 WebView2와 따로 둡니다 (게임을 바꿀 때마다 게임 쪽이 다시 시작되어도 방송은 이어지도록)
            string dir = Path.Combine(SettingsService.Root(), "multi_webview2");
            // WebRtcHideLocalIpsWithMdns 끔: 내부·가상 LAN(ZeroTier 등)·IPv6 주소를 xxx.local 이름으로 숨기지 않고 연결 후보로 냄.
            // 숨긴 이름은 같은 공유기 안에서만 풀려, 그 밖의 직접 연결 길이 모두 버려졌습니다. (같은 방 사람에게만 보이고 서버에는 저장하지 않음)
            // 게임용 WebView2에는 넣지 않습니다.
            string args = "--disable-background-timer-throttling --disable-renderer-backgrounding " +
                          "--disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion,WebRtcHideLocalIpsWithMdns " +
                          "--autoplay-policy=no-user-gesture-required";
            var env = await CoreWebView2Environment.CreateAsync(null, dir, new CoreWebView2EnvironmentOptions(args));
            await view.EnsureCoreWebView2Async(env);
            var core = view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("RR_MULTI_DEVTOOLS") == "1";
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            // Chromium은 마이크·카메라 권한이 없는 페이지에서 기본 경로(공유기) 주소 하나만 연결 후보로 냅니다.
            // 그러면 ZeroTier·Tailscale·Radmin VPN·Hamachi 같은 가상 LAN 주소가 빠져 가상 LAN으로는 연결되지 않았습니다 (1.0.0).
            // 권한 상태만 '허용'으로 두면 모든 네트워크 주소를 냅니다. 페이지는 마이크를 열지 않습니다 (getUserMedia를 부르지 않음).
            try { await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, MultiPageHost.TrimEnd('/'), CoreWebView2PermissionState.Allow); }
            catch (Exception ex) { UiLog.Write($"multi: permission preset failed {ex.Message}"); }
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;   // 실제 권한 요청(마이크 열기 등)은 모두 거절
            core.AddWebResourceRequestedFilter(MultiPageHost + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var page = e.Request.Uri.StartsWith(MultiPageHost + "index.html", StringComparison.OrdinalIgnoreCase)
                    ? typeof(MainWindow).Assembly.GetManifestResourceStream("RocketRPG.Resources.multi.html") : null;
                e.Response = page == null
                    ? env.CreateWebResourceResponse(null, 404, "Not Found", "")
                    : env.CreateWebResourceResponse(page, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
            core.WebMessageReceived += OnMultiPageMessage;
            core.ProcessFailed += (_, e) =>
            {
                UiLog.Write($"multi: view process failed ({e.ProcessFailedKind})");
                if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    Dispatcher.BeginInvoke(() => { try { _multiView?.CoreWebView2?.Reload(); } catch { } });
            };
            _multiPageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            core.Navigate(MultiPageHost + "index.html");
            var done = await Task.WhenAny(_multiPageReady.Task, Task.Delay(15000));
            if (done != _multiPageReady.Task) { UiLog.Write("multi: view page did not load"); return false; }
            return true;
        }
        catch (Exception ex)
        {
            UiLog.Write($"multi: view init failed {ex.Message}");
            return false;
        }
    }

    void PostMulti(string json)
    {
        try { _multiView?.CoreWebView2?.PostWebMessageAsJson(json); }
        catch (Exception ex) { UiLog.Write($"multi: post failed {ex.Message}"); }
    }

    void OnMultiPageMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject? m;
        try { m = JsonNode.Parse(e.WebMessageAsJson) as JsonObject; } catch { return; }
        if (m == null) return;
        switch (m["t"]?.GetValue<string>())
        {
            case "ready":
                UiLog.Write($"multi: view ready (generator {m["generator"]}, codecs {m["codecs"]})");
                // 페이지가 (다시) 열림: 처음부터 다시 알려 줌
                _multiPageRole = _multiPageMe = _multiMembersSent = _multiQualitySent = _multiHostSent = "";
                _multiStatusSent = "\u0001";
                _multiVolumeSent = -1;
                _multiSharedPosted = false;
                _controlSent = false;
                _multiPageReady?.TrySetResult(true);
                SyncMultiMedia();
                break;
            case "log":
                UiLog.Write($"multi-view: {m["text"]?.GetValue<string>()}");
                break;
            case "cam": OnMultiPageCam(m); break;          // 참가자: 방장 게임의 카메라
            case "input": OnRemoteKey(m); break;           // 방장: 참가자가 누른 키 (조종 권한)
            case "held": OnRemoteHeld(m); break;           // 방장: 참가자가 지금 누르고 있는 키 목록 (0.2초마다)
            case "nat": UiLog.Write($"multi: my network {m["nat"]?.GetValue<string>()}"); break;
            case "nrecv": OnNotesMessage(m["from"]?.GetValue<string>() ?? "", m["data"]?.GetValue<string>() ?? ""); break;   // 노트 공유
            case "nopen":                                  // 방장: 참가자와 노트 채널이 열림 → 노트 전체를 보냄
                if (_multi?.IsHost == true)
                {
                    SendNotesFull(m["from"]?.GetValue<string>() ?? "");
                    SendDialogueLog(m["from"]?.GetValue<string>() ?? "");
                    SyncMultiTools(m["from"]?.GetValue<string>() ?? "");
                    if (_vote.Open) BroadcastVote(m["from"]?.GetValue<string>() ?? "");
                }
                break;
            case "snap": OnMultiPageSnap(m); break;        // 참가자: 스크린샷 (방장 영상)
            case "inputclosed":                           // 방장: 참가자 입력 채널이 닫힘 → 누르던 키를 뗌
                ReleaseRemoteKeys(m["from"]?.GetValue<string>() ?? "");
                break;
            case "vrect": OnMultiPageVideoRect(m); break;  // 참가자: 영상이 그려진 곳
            case "peers":   // 방장: 연결이 막힌 참가자 (어느 쪽 네트워크가 막는지)
                _multiBlocked.Clear();
                if (m["blocked"] is JsonArray blocked)
                    foreach (var id in blocked)
                        if (id?.GetValue<string>() is { } s) _multiBlocked[s] = m["why"]?[s]?.GetValue<string>() ?? "";
                UiLog.Write($"multi: blocked participants [{string.Join(",", _multiBlocked.Select(kv => kv.Key + ":" + kv.Value))}]");
                UpdateMultiUi();
                break;
            case "link":    // 참가자: 방장과 직통 연결이 막힘 / 다시 됨
            {
                string state = m["state"]?.GetValue<string>() ?? "";
                UiLog.Write($"multi: link to host {state} {m["side"]?.GetValue<string>()}");
                if (state == "ok" && _multiLink == "blocked") ShowHudMessage("방장과 연결되었습니다.", 3000);
                _multiLink = state;
                break;
            }
            case "signal":
                if (_multi?.InRoom == true)
                    _multi.Send(new { t = "signal", to = m["to"]?.GetValue<string>() ?? "", data = m["data"]?.DeepClone() });
                break;
        }
    }

    void OnMultiSignal(string from, JsonNode? data)
    {
        if (_multiView?.CoreWebView2 == null) return;
        PostMulti(new JsonObject { ["t"] = "signal", ["from"] = from, ["data"] = data?.DeepClone() }.ToJsonString());
    }

    /// <summary>참가자: 방송 화면(WebView2) 브라우저 프로세스 (단축키 훅이 이 창의 키도 보도록)</summary>
    int MultiGuestViewPid
    {
        get
        {
            if (!IsMultiGuest || _multiView?.CoreWebView2 == null) return 0;
            try { return (int)_multiView.CoreWebView2.BrowserProcessId; } catch { return 0; }
        }
    }

    /// <summary>방 설정의 화질 → (초당 프레임, 최대 높이, 최대 비트레이트)</summary>
    static (int fps, int maxHeight, int bitrate) MultiQuality(MultiRoomSettings s)
    {
        int fps = s.Fps >= 60 ? 60 : 30;
        return s.Quality switch
        {
            "low" => (fps, 540, fps == 60 ? 1_800_000 : 1_200_000),
            "high" => (fps, 1080, fps == 60 ? 8_000_000 : 5_000_000),
            _ => (fps, 720, fps == 60 ? 4_000_000 : 2_500_000),
        };
    }

    /// <summary>참가자 화면 안내 글 ("" = 게임 중이라 영상을 보여 줌)</summary>
    string MultiGuestStatus()
    {
        if (_multi == null) return "";
        var room = _multi.Room;
        return _multi.Reconnecting ? "연결이 끊겨 다시 연결하는 중..."
            : room.HostAway ? "방장 연결이 끊겼습니다.\n1분 안에 돌아오지 않으면 방이 없어집니다."
            : string.IsNullOrEmpty(room.Game) ? "방장이 게임을 켜기를 기다리는 중..."
            : _ownGuestStatus;
    }

    bool _multiSyncing, _multiSyncAgain;

    /// <summary>방 상태·게임 상태가 바뀔 때마다: 페이지 역할, 참가자 목록, 화질, 방장 캡처를 맞춥니다.</summary>
    async void SyncMultiMedia()
    {
        if (_multi == null) return;
        if (_multiSyncing) { _multiSyncAgain = true; return; }
        _multiSyncing = true;
        try
        {
            do
            {
                _multiSyncAgain = false;
                await SyncMultiMediaOnce();
            } while (_multiSyncAgain);
        }
        catch (Exception ex) { UiLog.Write($"multi: media sync failed {ex.Message}"); }
        finally { _multiSyncing = false; }
    }

    async Task SyncMultiMediaOnce()
    {
        if (_multi == null) return;
        string role = !_multi.InRoom ? "" : _multi.IsHost ? "host" : "guest";
        if (role.Length == 0 && _multiView == null) { StopStreamer(); return; }
        if (!await EnsureMultiView() || _multiView?.CoreWebView2 == null)
        {
            StopStreamer();
            return;   // 페이지를 못 열면 참가자는 대기 화면(안내 글)만 봅니다
        }
        role = !_multi.InRoom ? "" : _multi.IsHost ? "host" : "guest";

        if (role != _multiPageRole || _multi.MyId != _multiPageMe)
        {
            _multiPageRole = role;
            _multiPageMe = _multi.MyId;
            _multiMembersSent = _multiQualitySent = "";
            _multiStatusSent = "\u0001";
            _multiHostSent = _multi.Room.HostId;
            _camSent = null;   // 새 페이지에 카메라를 다시 알려 줌 (아래 init 뒤)
            PostMulti($"{{\"t\":\"init\",\"role\":\"{role}\",\"me\":{JsonSerializer.Serialize(_multi.MyId)},\"host\":{JsonSerializer.Serialize(_multi.Room.HostId)},\"ice\":{_multi.IceServersJson}}}");
        }
        if (role == "guest" && _multi.Room.HostId != _multiHostSent)
        {
            _multiHostSent = _multi.Room.HostId;
            PostMulti(JsonSerializer.Serialize(new { t = "host", id = _multiHostSent }));
        }

        bool guest = role == "guest";
        _multiView.Visibility = guest ? Visibility.Visible : Visibility.Collapsed;
        _multiView.Focusable = guest;
        if (guest != _multiGuestAudioMenu)
        {
            // 참가자는 게임이 없어도 방장 소리 크기를 조절할 수 있게 (출력 장치 고르기는 게임에만 적용)
            _multiGuestAudioMenu = guest;
            AudioMenu.IsEnabled = guest || _isNativeRunning;
            OutputDeviceMenu.IsEnabled = !guest;
            UpdateVolumeAvailability();
        }
        if (guest)
        {
            MultiWaitPanel.Visibility = Visibility.Collapsed;
            string status = MultiGuestStatus();
            if (status != _multiStatusSent)
            {
                _multiStatusSent = status;
                PostMulti(new JsonObject { ["t"] = "status", ["text"] = status }.ToJsonString());
            }
            int volume = Math.Clamp(_ctl.Settings.Volume, 0, 100);
            if (volume != _multiVolumeSent)
            {
                _multiVolumeSent = volume;
                PostMulti($"{{\"t\":\"volume\",\"value\":{volume / 100.0:0.00}}}");
            }
        }

        if (role == "host")
        {
            MultiOnGameState();
            // 스팀 게임 중이면 게임 확인을 마친 참가자에게만 화면을 보냄
            string ids = string.Join(",", _multi.Members.Where(m => !m.Host && _own.Allowed(m.Id)).Select(m => m.Id));
            string key = (_own.Required ? "gate:" : "") + ids;
            if (key != _multiMembersSent)
            {
                _multiMembersSent = key;
                PostMulti(JsonSerializer.Serialize(new { t = "members", ids = ids.Length == 0 ? Array.Empty<string>() : ids.Split(','), gate = _own.Required }));
            }
            var (fps, _, bitrate) = MultiQuality(_multi.Room.Settings);
            string q = $"{fps}/{bitrate}";
            if (q != _multiQualitySent)
            {
                _multiQualitySent = q;
                PostMulti($"{{\"t\":\"quality\",\"fps\":{fps},\"bitrate\":{bitrate}}}");
            }
        }
        UpdateStreamer(role);
        SyncMultiControl(role);
        SyncMultiNotes(role);
    }

    /// <summary>방장 캡처: 방장이고, 게임 중이고, 참가자가 있을 때만 돕니다.</summary>
    void UpdateStreamer(string role)
    {
        var core = _multiView?.CoreWebView2;
        bool want = role == "host" && core != null && _isNativeRunning && _currentBridge != null && _multi!.Members.Any(m => !m.Host);
        IntPtr target = want ? GameCaptureHwnd() : IntPtr.Zero;
        if (!want || target == IntPtr.Zero) { StopStreamer(); return; }

        _multiShared ??= core!.Environment.CreateSharedBuffer((ulong)MultiStreamer.BufferBytes);
        if (!_multiSharedPosted)
        {
            core!.PostSharedBufferToScript(_multiShared, CoreWebView2SharedBufferAccess.ReadOnly, MultiStreamer.LayoutJson);
            _multiSharedPosted = true;
        }
        if (_streamer == null)
        {
            _streamer = new MultiStreamer(new WindowInteropHelper(this).Handle, _multiShared.Buffer);
            _streamer.Start();
            UiLog.Write("multi: streaming started");
        }
        var (fps, maxHeight, _) = MultiQuality(_multi!.Room.Settings);
        _streamer.SetQuality(fps, maxHeight);
        // 게임이 직접 주는 화면 (채팅·핑이 섞이지 않음): 2000/2003은 Player 공유 메모리, MV/MZ는 게임 페이지
        string? feedName = ReferenceEquals(_currentBridge, _easyRpgRenderer) ? _easyRpgRenderer.FrameMemoryName : null;
        IntPtr feedPtr = ReferenceEquals(_currentBridge, _webRenderer) ? _webRenderer.EnableFrameFeed(true)
                       : ReferenceEquals(_currentBridge, _mkxpRenderer) ? _mkxpRenderer.EnableFrameFeed(true, fps) : IntPtr.Zero;
        _streamer.SetSource(target, GameSoundPid(), feedName, feedPtr);
    }

    void StopStreamer()
    {
        _webRenderer.EnableFrameFeed(false);
        _mkxpRenderer.EnableFrameFeed(false, 0);
        if (_streamer == null) return;
        _streamer.Dispose();
        _streamer = null;
        UiLog.Write("multi: streaming stopped");
    }

    /// <summary>방장 쪽 게임 화면 창: 네이티브 게임 창, MV/MZ는 게임 WebView2 창</summary>
    IntPtr GameCaptureHwnd()
    {
        IntPtr hwnd = EmbeddedGameHwnd();
        if (hwnd != IntPtr.Zero) return hwnd;
        if (ReferenceEquals(_currentBridge, _webRenderer) && WebScreen.IsVisible) return WebScreen.Handle;
        return IntPtr.Zero;
    }

    /// <summary>게임 소리를 내는 프로세스 (그 자식 프로세스까지 함께 잡음)</summary>
    int GameSoundPid()
    {
        int pid = NativeGamePid;
        if (pid > 0) return pid;
        if (ReferenceEquals(_currentBridge, _webRenderer))
        {
            try { return (int)(WebScreen.CoreWebView2?.BrowserProcessId ?? 0); } catch { }
        }
        return 0;
    }

    void OnMultiQuality(object sender, RoutedEventArgs e)
    {
        if (_multi is not { InRoom: true, IsHost: true } || sender is not MenuItem { Tag: string q }) return;
        _multi.Send(new { t = "settings", quality = q });
        ShowHudMessage($"방송 화질: {(q == "low" ? "낮음" : q == "high" ? "높음" : "보통")}");
    }

    void OnMultiFps(object sender, RoutedEventArgs e)
    {
        if (_multi is not { InRoom: true, IsHost: true } || sender is not MenuItem { Tag: string f }) return;
        int fps = f == "60" ? 60 : 30;
        _multi.Send(new { t = "settings", fps });
        ShowHudMessage($"방송: 초당 {fps}프레임");
    }
}
