#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using RocketRPG.Models;

namespace RocketRPG.Views;

// ── 멀티 조종 권한: 참가자 키보드를 방장 게임으로 ──
// 방장이 켜면(멀티 > 방 설정 > 조종 권한 켜기) 참가자가 누른 키가 WebRTC 데이터 채널로 방장에게 바로 가서(서버를 거치지 않음)
// 방장 게임에 들어갑니다. 시스템 단축키를 제외한 게임 키와 Shift, F1~F12를 전달합니다.
// 키 박힘 막기: 참가자는 0.2초마다 지금 누르고 있는 키 목록을 보내고, 방장은 그 목록으로 빠진 떼기를 바로잡습니다.
// 목록이 1초 넘게 오지 않으면(연결이 끊김 등) 그 참가자의 키를 모두 뗍니다.
// EasyRPG/mkxp-z는 Shift를 창 메시지로 넣으면 SDL이 실제 키보드 상태를 보고 바로 떼어 버리므로, 누른 키 목록을 게임에도 따로 알려 줍니다.
// MV/MZ 마우스는 방장만 씁니다.
public partial class MainWindow
{
    /// <summary>참가자 키 중 방장 게임에 넣어 주는 것 (윈도우 가상 키)</summary>
    static bool IsGameKey(int vk) =>
        vk is 0x08 or 0x09 or 0x0D or 0x10 or 0x1B or 0x20
        or (>= 0x21 and <= 0x28)                          // PageUp/Down, End, Home, 방향키
        or (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) // 숫자, 글자
        or (>= 0x60 and <= 0x7B)                          // 숫자판, F1~F12
        or 0xA0 or 0xA1;                                  // 왼쪽/오른쪽 Shift

    readonly RemoteKeyState _remoteKeys = new();   // 방장: 참가자들이 누르고 있는 키
    string _remoteFeedSent = "";      // 방장: 게임에 마지막으로 알린 참가자 키 목록
    IGameBridge? _remoteFeedBridge;   // 방장: 그 목록을 알린 게임
    bool _controlSent;   // 참가자: 방송 페이지에 알린 조종 권한 상태
    bool _controlPausedForVote;   // 참가자: 선택지 투표 때문에 조종을 멈춘 상태

    /// <summary>방장 게임에 키 하나를 넣음 (네이티브 게임은 창 메시지, MV/MZ는 브라우저 입력)</summary>
    bool PostGameKey(int vk, bool down, bool repeat = false, string code = "", string key = "")
    {
        if (ReferenceEquals(_currentBridge, _webRenderer) && _isNativeRunning)
        {
            _webRenderer.SendKey(vk, code, key, down);
            return true;
        }
        IntPtr hwnd = EmbeddedGameHwnd();
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        uint scan = NativeMethods.MapVirtualKeyW((uint)vk, 0);
        bool extended = vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x24 or 0x23 or 0x21 or 0x22 or 0x2F;
        int lp = 0x1 | ((int)scan << 16);
        if (extended) lp |= (1 << 24);
        if (down && repeat) lp |= (1 << 30);
        if (!down) lp |= unchecked((int)(1u << 30 | 1u << 31));
        NativeMethods.PostMessageW(hwnd, down ? WM_KEYDOWN : WM_KEYUP, (IntPtr)vk, (IntPtr)lp);
        return true;
    }

    /// <summary>방장: 참가자가 보낸 키 (방송 페이지의 입력 채널에서)</summary>
    void OnRemoteKey(JsonObject m)
    {
        if (_multi is not { InRoom: true, IsHost: true } || !_multi.Room.Settings.Control) return;
        string from = m["from"]?.GetValue<string>() ?? "";
        int vk = m["k"] is JsonValue kv && kv.TryGetValue<int>(out int k) ? k : 0;
        bool down = m["d"] is JsonValue dv && dv.TryGetValue<int>(out int d) && d != 0;
        if (!_multi.Members.Any(m => m.Id == from && !m.Host) || !IsGameKey(vk)) return;
        _remoteKeys.Touch(from, Environment.TickCount64);
        if (_vote.Open && down) return;   // 선택지 투표 중: 고르는 것은 방장 (떼기는 받음)
        if (_remoteKeys.Set(from, vk, down))
            PostGameKey(RemoteKeyState.Normalize(vk), down, code: m["c"]?.GetValue<string>() ?? "", key: m["key"]?.GetValue<string>() ?? "");
        SyncRemoteKeyFeed();
    }

    /// <summary>방장: 참가자가 0.2초마다 보내는 '지금 누르고 있는 키' 목록으로 맞춤 (빠진 누름/뗌 보정)</summary>
    void OnRemoteHeld(JsonObject m)
    {
        if (_multi is not { InRoom: true, IsHost: true } || !_multi.Room.Settings.Control) return;
        string from = m["from"]?.GetValue<string>() ?? "";
        if (!_multi.Members.Any(x => x.Id == from && !x.Host)) return;
        _remoteKeys.Touch(from, Environment.TickCount64);
        var held = (m["keys"] as JsonArray ?? new JsonArray())
            .Select(n => n is JsonValue v && v.TryGetValue<int>(out int vk) ? vk : 0).Where(IsGameKey);
        if (_vote.Open) held = [];   // 선택지 투표 중: 참가자 키는 모두 뗀 것으로
        var changes = _remoteKeys.Reconcile(from, held);
        foreach (var (vk, down) in changes) PostGameKey(vk, down);
        if (changes.Count > 0) UiLog.Write($"multi: corrected keys of {from}: {string.Join(",", changes.Select(c => (c.down ? "+" : "-") + c.vk))}");
        SyncRemoteKeyFeed();
    }

    /// <summary>방장: 1초 넘게 키 소식이 없는 참가자의 키를 모두 뗌 (연결이 끊겨 떼기가 오지 않는 경우). 50ms마다 부름.</summary>
    void CheckRemoteKeyLease()
    {
        if (!_remoteKeys.Any) return;
        foreach (var from in _remoteKeys.Expired(Environment.TickCount64))
        {
            UiLog.Write($"multi: released keys of {from} (no key news for {RemoteKeyState.LeaseMs} ms)");
            ReleaseRemoteKeys(from);
        }
    }

    /// <summary>방장: 참가자가 누르고 있는 키 목록을 게임에도 알림 (EasyRPG/mkxp-z: Shift와 GetAsyncKeyState용)</summary>
    void SyncRemoteKeyFeed()
    {
        bool control = _multi is { InRoom: true, IsHost: true } && _multi.Room.Settings.Control && _multi.Members.Any(x => !x.Host);
        var bridge = control ? _currentBridge : null;
        string list = !control ? "" : string.Join(",", _remoteKeys.Pressed);
        if (!ReferenceEquals(bridge, _remoteFeedBridge))
        {
            // 게임이 바뀌거나 조종 권한이 꺼짐: 이전 게임은 끄고 새 게임은 켬
            if (_remoteFeedBridge is RocketRenderEasyRPG oe) { oe.SetRemoteKeys(""); oe.SetRemoteControl(false); }
            else if (_remoteFeedBridge is RocketRenderMKXP om) { om.SetRemoteKeys(""); om.SetRemoteControl(false); }
            _remoteFeedBridge = bridge;
            _remoteFeedSent = "\u0001";
            if (bridge is RocketRenderEasyRPG ne) ne.SetRemoteControl(true);
            else if (bridge is RocketRenderMKXP nm) nm.SetRemoteControl(true);
        }
        if (list == _remoteFeedSent) return;
        _remoteFeedSent = list;
        if (bridge is RocketRenderEasyRPG e) e.SetRemoteKeys(list);
        else if (bridge is RocketRenderMKXP mk) mk.SetRemoteKeys(list);
    }

    /// <summary>방장: 참가자(또는 모두)가 누르고 있던 키를 뗌 (나감, 연결 끊김, 조종 권한 끔)</summary>
    void ReleaseRemoteKeys(string? from = null)
    {
        foreach (int vk in _remoteKeys.Release(from)) PostGameKey(vk, false);
        SyncRemoteKeyFeed();
    }

    /// <summary>방 상태가 바뀔 때: 조종 권한 끔 / 나간 참가자의 키 정리, 참가자에게 안내 (SyncMultiMediaOnce에서 부름)</summary>
    void SyncMultiControl(string role)
    {
        if (_multi == null) return;
        bool control = _multi.InRoom && _multi.Room.Settings.Control;
        if (role == "host")
        {
            // 조종 권한을 끄거나 게임이 바뀌면 참가자 키를 모두 뗌
            if (!control || (_remoteFeedBridge != null && !ReferenceEquals(_remoteFeedBridge, _currentBridge))) ReleaseRemoteKeys();
            else
            {
                var present = _multi.Members.Select(m => m.Id).ToHashSet();
                foreach (var id in _remoteKeys.Holders.Where(id => !present.Contains(id)).ToList()) ReleaseRemoteKeys(id);
                SyncRemoteKeyFeed();
            }
        }
        else if (_remoteKeys.Any || _remoteFeedBridge != null) ReleaseRemoteKeys();   // 방장을 넘김 / 방을 나옴
        // 참가자: 선택지 투표 중에는 조종을 잠시 멈춤 (방장이 고름)
        bool voting = role == "guest" && _guestVote != null;
        bool guestControl = role == "guest" && control && !voting;
        if (guestControl != _controlSent)
        {
            _controlSent = guestControl;
            PostMulti($"{{\"t\":\"control\",\"on\":{(guestControl ? "true" : "false")}}}");
            if (role == "guest")
                ShowHudMessage(guestControl ? (_controlPausedForVote ? "투표가 끝났습니다. 다시 조작할 수 있습니다." : "방장이 조종 권한을 켰습니다. 게임 화면을 누른 뒤 키보드로 조작할 수 있습니다.")
                    : voting && control ? "선택지는 방장이 고릅니다. 오른쪽 상자에서 투표해 주세요." : "조종 권한이 꺼졌습니다.", 4000);
            _controlPausedForVote = voting && control;
            if (guestControl) _multiView?.Focus();
        }
    }

    /// <summary>참가자: 방송 화면이 아닌 RocketRPG 창에 키보드 포커스가 있을 때 누른 키 (OnPreviewKeyDown/Up)</summary>
    bool ForwardGuestKey(KeyEventArgs e, bool down)
    {
        if (!_controlSent || !IsMultiGuest) return false;
        // 떼기는 포커스와 상관없이 먼저 보냄 (게임 화면에서 누른 뒤 메뉴·노트로 옮겨 가서 떼어도 방장 쪽 키가 박히지 않게).
        // 방송 페이지는 누르고 있지 않은 키의 떼기는 버립니다.
        if (!down)
        {
            int up = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
            if (!IsGameKey(up)) return false;
            PostMulti(JsonSerializer.Serialize(new { t = "key", k = up, d = 0, c = "", key = "" }));
            return !(MainMenu.IsKeyboardFocusWithin || NotesHost.IsKeyboardFocusWithin);
        }
        if (MainMenu.IsKeyboardFocusWithin || NotesHost.IsKeyboardFocusWithin) return false;
        if (down && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        Key k = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(k);
        if (!IsGameKey(vk)) return false;
        if (down && e.IsRepeat) return true;
        PostMulti(JsonSerializer.Serialize(new { t = "key", k = vk, d = down ? 1 : 0, c = "", key = "" }));
        return true;
    }
}
