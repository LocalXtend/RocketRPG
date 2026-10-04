#nullable enable
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using RocketRPG.Models;

namespace RocketRPG.Views;

// ── 멀티 조종 방식: 컨트롤 모드 / 엑스트라 모드 ──
// 조종 권한이 켜져 있을 때
//   컨트롤 모드(기본): 참가자 키가 방장 캐릭터를 함께 움직입니다 (MainWindow.MultiControl).
//   엑스트라 모드: 참가자마다 방장과 같은 모습의 캐릭터가 생기고, 참가자 키는 자기 캐릭터만 움직입니다.
//                 캐릭터는 방장 화면 밖으로 나갈 수 없고, 이벤트에 말을 걸 수 있습니다.
//                 MV/MZ: rocket_extra.js, XP/VX/Ace: 루비 에이전트, 2000/2003: EasyRPG Player 수정본 (IExtraModeTarget).
// 참가자에게는 방 relay "mode"로 지금 방식을 알려 안내 문구를 맞춥니다.
public partial class MainWindow
{
    const string ModeChannel = "mode";
    bool _extraSent;                 // 방장: 게임에 알린 엑스트라 모드 상태
    IExtraModeTarget? _extraTarget;  // 방장: 그 상태를 알린 게임 (게임이 바뀌면 이전 게임은 끔)
    string _extraGuestsSent = "";    // 방장: 게임에 알린 참가자 목록
    string _extraModeBroadcast = ""; // 방장: 참가자에게 알린 방식 + 참가자 목록
    string _extraUnsupportedFor = "";// 방장: 'MV/MZ만 됨' 안내를 한 게임
    bool _guestExtra;                // 참가자: 방장이 엑스트라 모드를 씀

    /// <summary>지금 게임이 엑스트라 모드를 받을 수 있으면 그 게임</summary>
    IExtraModeTarget? CurrentExtraTarget => _isNativeRunning && _currentBridge is IExtraModeTarget t && t.ExtraSupported ? t : null;
    bool CurrentSupportsExtra => CurrentExtraTarget != null;

    /// <summary>방장: 지금 엑스트라 모드로 참가자 키를 받는지 (조종 권한 + 엑스트라 모드 + 받을 수 있는 게임)</summary>
    bool ExtraActive => _multi is { InRoom: true, IsHost: true } && _multi.Room.Settings.Control && _ctl.Settings.MultiExtraMode && CurrentSupportsExtra;

    void OnMultiExtra(object sender, RoutedEventArgs e)
    {
        if (_multi is not { InRoom: true, IsHost: true }) return;
        _ctl.Settings.MultiExtraMode = MultiExtraItem.IsChecked;
        _ctl.SaveSettings();
        if (_ctl.Settings.MultiExtraMode && !_multi.Room.Settings.Control)
        {
            _multi.Send(new { t = "settings", control = true });   // 엑스트라 모드는 조종 권한이 있어야 움직임
            MultiControlItem.IsChecked = true;
        }
        ShowHudMessage(_ctl.Settings.MultiExtraMode
            ? CurrentSupportsExtra || _currentBridge == null ? "엑스트라 모드: 참가자마다 캐릭터가 생깁니다." : "엑스트라 모드는 지금 MV/MZ 게임만 됩니다. 이 게임에서는 컨트롤 모드로 조작합니다."
            : "컨트롤 모드: 참가자가 방장 캐릭터를 함께 조작합니다.", 4000);
        ReleaseRemoteKeys();   // 방식이 바뀌면 누르던 키는 뗌
        SyncExtra();
    }

    /// <summary>방장: 게임에 엑스트라 모드와 참가자 목록을, 참가자에게 지금 방식을 알림 (SyncMultiControl에서 부름)</summary>
    void SyncExtra()
    {
        if (_multi == null) return;
        bool host = _multi is { InRoom: true, IsHost: true };
        bool active = ExtraActive;
        var target = active ? CurrentExtraTarget : null;
        if (active != _extraSent || !ReferenceEquals(target, _extraTarget))
        {
            if (_extraTarget != null && !ReferenceEquals(target, _extraTarget)) _extraTarget.SetExtraMode(false);   // 바뀐 게임은 끔
            _extraSent = active;
            _extraTarget = target;
            _extraGuestsSent = "";
            target?.SetExtraMode(true);
            UiLog.Write($"multi: extra mode {(active ? "on" : "off")} ({target?.GetType().Name ?? "-"})");
        }
        if (active)
        {
            var guests = _multi.Members.Where(m => !m.Host && _own.Allowed(m.Id)).ToList();
            string key = string.Join("|", guests.Select(m => $"{m.Id}:{m.Name}:{m.Color}"));
            if (key != _extraGuestsSent)
            {
                _extraGuestsSent = key;
                target?.SetExtraGuests(guests.Select(m =>
                {
                    uint c = MultiChatStyle.PingColors[Math.Clamp(m.Color, 0, MultiChatStyle.PingColors.Length - 1)];
                    return (m.Id, m.Name, $"#{c:X6}");
                }));
            }
        }
        // MV/MZ가 아닌 게임에서 엑스트라 모드를 고른 경우: 한 번 알림
        if (host && _multi.Room.Settings.Control && _ctl.Settings.MultiExtraMode && _currentBridge != null && !CurrentSupportsExtra && _extraUnsupportedFor != _currentDir)
        {
            _extraUnsupportedFor = _currentDir ?? "";
            ShowHudMessage("엑스트라 모드는 지금 MV/MZ 게임만 됩니다. 이 게임에서는 컨트롤 모드로 조작합니다.", 5000);
        }
        // 참가자에게 방식 알림 (바뀌었거나 새 참가자가 들어왔을 때)
        if (host)
        {
            string b = $"{active}/{string.Join(",", _multi.Members.Select(m => m.Id))}";
            if (b != _extraModeBroadcast)
            {
                _extraModeBroadcast = b;
                _multi.Send(new { t = "relay", ch = ModeChannel, data = new JsonObject { ["extra"] = active } });
            }
        }
        else _extraModeBroadcast = "";
    }

    void InitMultiExtra()
    {
        _multi!.RelayReceived += (ch, from, data) =>
        {
            if (ch != ModeChannel || _multi is not { InRoom: true, IsHost: false } || from != _multi.Room.HostId) return;
            bool extra = data?["extra"]?.GetValue<bool>() == true;
            if (extra == _guestExtra) return;
            _guestExtra = extra;
            if (_controlSent) ShowHudMessage(extra ? "엑스트라 모드: 내 캐릭터를 방향키로 움직이고 Z·Enter로 말을 겁니다. 방장 화면 밖으로는 못 갑니다."
                                                   : "컨트롤 모드: 방장 캐릭터를 함께 조작합니다.", 5000);
        };
    }

    /// <summary>방장: 엑스트라 모드면 참가자 키를 그 참가자 캐릭터로 (true = 처리함)</summary>
    bool RouteExtraKey(string from, int vk, bool down)
    {
        if (!ExtraActive) return false;
        if (!(_vote.Open && down)) _extraTarget?.ExtraKey(from, RemoteKeyState.Normalize(vk), down);
        return true;
    }

    bool RouteExtraHeld(string from, int[] keys)
    {
        if (!ExtraActive) return false;
        _extraTarget?.ExtraHeld(from, _vote.Open ? [] : keys.Select(RemoteKeyState.Normalize).Distinct());
        return true;
    }
}
