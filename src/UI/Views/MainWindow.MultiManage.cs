#nullable enable
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RocketRPG.Models;

namespace RocketRPG.Views;

// ── 멀티 방 관리: 참가자 목록 메뉴(내보내기·채팅 금지·방장 넘기기), 역할이 바뀔 때 안내, 스트리머 모드, 디스코드, 대사 기록 공유 ──
public partial class MainWindow
{
    bool _wasHost, _wasGuest, _wasMuted;
    string _discordGuestGame = "\u0001";

    /// <summary>방 설정 스트리머 모드(방장이 켬)도 함께: 방 제목·코드를 화면에 보이지 않음</summary>
    bool MultiHideInfo => _ctl.Settings.StreamerMode || _multi?.Room.Settings.Streamer == true;

    /// <summary>참가자 목록의 한 사람. 방장에게는 관리 메뉴가 달립니다.</summary>
    MenuItem MemberMenuItem(MultiMember m, bool host)
    {
        string label = m.Name + (m.Host ? " (방장)" : "") + (m.Id == _multi!.MyId ? " (나)" : "") + (m.Muted ? " (채팅 금지)" : "")
                       + (host && _multiBlocked.TryGetValue(m.Id, out var side)
                           ? side == "me" ? " (화면 연결 안 됨: 내 네트워크가 막음)" : side == "peer" ? " (화면 연결 안 됨: 상대 네트워크가 막음)" : " (화면 연결 안 됨)"
                           : "")
                       + (host && !m.Host && _own.Status(m.Id) is { Length: > 0 } own ? $" ({own})" : "");
        // 이 사람의 핑·마커 색 (색만으로 구분하지 않도록 이름과 함께)
        uint pc = MultiChatStyle.PingColors[Math.Clamp(m.Color, 0, MultiChatStyle.PingColors.Length - 1)];
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 10, Height = 10, Stroke = System.Windows.Media.Brushes.DimGray, StrokeThickness = 1,
            Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(pc >> 16), (byte)(pc >> 8), (byte)pc)),
        };
        var item = new MenuItem { Header = MenuText(label), Icon = dot };
        if (!host || m.Host)
        {
            item.IsEnabled = false;
            return item;
        }
        var mute = new MenuItem { Header = "채팅 금지(_M)", IsCheckable = true, IsChecked = m.Muted, ToolTip = "채팅과 핑을 보낼 수 없게 합니다." };
        mute.Click += (_, _) =>
        {
            _multi.Send(new { t = "mute", id = m.Id, on = mute.IsChecked });
            ShowHudMessage(mute.IsChecked ? $"{m.Name}: 채팅 금지" : $"{m.Name}: 채팅 금지 풀림");
        };
        var transfer = new MenuItem { Header = "방장 넘기기(_T)..." };
        transfer.Click += (_, _) => TransferHost(m);
        var kick = new MenuItem { Header = "내보내기(_K)..." };
        kick.Click += (_, _) =>
        {
            var r = MessageBox.Show(this, $"'{m.Name}'을(를) 방에서 내보낼까요? 이 방에는 다시 들어올 수 없습니다.", "내보내기",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
            _multi.Send(new { t = "kick", id = m.Id });
            ShowHudMessage($"{m.Name}을(를) 내보냈습니다.");
        };
        item.Items.Add(mute);
        item.Items.Add(transfer);
        item.Items.Add(new Separator());
        item.Items.Add(kick);
        return item;
    }

    /// <summary>방장 넘기기: 새 방장의 게임 화면과 노트로 바뀝니다. 내 게임은 끕니다 (참가자는 게임을 켜지 않음).</summary>
    void TransferHost(MultiMember m)
    {
        if (_multi is not { InRoom: true, IsHost: true }) return;
        string warn = _currentBridge != null ? "\n지금 하던 게임은 끄고, 새 방장의 화면을 보게 됩니다." : "\n새 방장의 화면을 보게 됩니다.";
        var r = MessageBox.Show(this, $"방장을 '{m.Name}'에게 넘길까요?{warn}", "방장 넘기기",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes || !_multi.InRoom || !_multi.IsHost) return;
        if (_currentBridge != null) StopNativeSession("방장을 넘겨 게임을 껐습니다.");
        _multi.Send(new { t = "transfer", id = m.Id });
        UiLog.Write($"multi: host transferred to {m.Id}");
    }

    /// <summary>UpdateMultiUi 끝에서: 역할·채팅 금지가 바뀌면 안내, 스트리머 모드 알림, 디스코드, 참가자 도구</summary>
    void MultiAfterUiUpdate()
    {
        if (_multi == null) return;
        bool inRoom = _multi.InRoom, host = inRoom && _multi.IsHost, guest = inRoom && !_multi.IsHost;
        bool muted = inRoom && _multi.Members.FirstOrDefault(x => x.Id == _multi.MyId)?.Muted == true;

        if (host && _wasGuest) ShowHudMessage("방장이 되었습니다. 게임을 켜면 참가자에게 보입니다.", 5000);
        if (guest && _wasHost) ShowHudMessage("방장을 넘겼습니다. 이제 새 방장의 화면을 봅니다.", 5000);
        if (guest && !_wasGuest) DialogueLogManager.Clear();   // 참가자는 방장 게임의 대사를 받음
        if (inRoom && muted != _wasMuted && (_wasHost || _wasGuest))
            ShowHudMessage(muted ? "방장이 채팅을 막았습니다 (채팅·핑을 보낼 수 없음)." : "채팅 금지가 풀렸습니다.", 4000);

        // 참가자 도구: 대사 기록과 스크린샷만 (게임이 없어 나머지는 이미 꺼져 있음)
        if (guest != _wasGuest)
        {
            if (guest)
            {
                bool streamer = _ctl.Settings.StreamerMode;
                ScreenshotOnlyMenuItem.IsEnabled = ScreenshotMenuItem.IsEnabled = !streamer;
                DialogueLogMenuItem.IsEnabled = true;
            }
            else UpdateMenuEnabledState(_isNativeRunning);
        }

        // 방장의 스트리머 모드를 방 설정으로 (참가자 화면에서도 방 제목·코드를 숨김)
        if (host && _multi.Room.Settings.Streamer != _ctl.Settings.StreamerMode)
            _multi.Send(new { t = "settings", streamer = _ctl.Settings.StreamerMode });

        // 디스코드: '멀티 (2/4)'와 참가 단추 (스트리머 모드면 숨김). 참가자는 방장 게임 제목을 보여 줌.
        bool hide = MultiHideInfo;
        _discord.Multi = inRoom && !hide ? (_multi.Members.Count, _multi.Room.Settings.Max) : null;
        _discord.JoinUrl = inRoom && !hide && _multi.Room.Code.Length > 0 ? $"{MultiClient.ServerUrl}/join/{_multi.Room.Code}" : null;
        if (guest)
        {
            if (_multi.Room.Game != _discordGuestGame)
            {
                _discordGuestGame = _multi.Room.Game;
                if (_multi.Room.Game.Length > 0) _discord.ShowGame(_multi.Room.Game, "멀티 참가");
                else _discord.ShowIdle();
            }
        }
        else if (_wasGuest)
        {
            _discordGuestGame = "\u0001";
            if (_currentBridge == null) _discord.ShowIdle();
        }

        // 참가자 창 제목은 방장 게임을 따라 바뀜. 방을 나오면 원래대로 (참가자는 게임을 켜지 않으므로 기본 제목)
        if (guest) Title = MultiTitle.ForGuest(BaseTitle, _multi.Room.Game);
        else if (_wasGuest && _currentBridge == null) Title = BaseTitle;
        if (!guest && _wasGuest) ResetAfterGuest();

        _wasHost = host;
        _wasGuest = guest;
        _wasMuted = muted;
        UpdateGuestTools();
        SyncMultiTools();   // 방장: 스트리머·조종 권한이 바뀌면 참가자에게 바로
    }

    /// <summary>
    /// 참가자로 있다가 나옴(나가기·내보내짐·방 없어짐·방장이 됨): 방장 게임에서 받은 것을 지우고 내 상태로 되돌림.
    /// 방송 화면·키·노트·채팅·핑은 각자 정리됩니다 (SyncMultiMedia/SyncMultiNotes/SyncMultiOverlay).
    /// </summary>
    void ResetAfterGuest()
    {
        DialogueLogManager.Clear();             // 방장 게임의 대사
        _dataInspectorWnd?.Close();
        EspCanvas.Clear();
        _guestBridge = null;
        _snapWaiter?.TrySetResult(null);
        // 메뉴 체크: 방장 상태를 보여 주던 것을 내 상태로
        EspOverlayMenuItem.IsChecked = false;
        NoclipMenuItem.IsChecked = false;
        TileInspectorMenuItem.IsChecked = false;
        ShowMessageBarMenuItem.IsChecked = _messageBarEnabled;
        AutoMessageMenuItem.IsChecked = _autoAdvance.IsAuto;
        SkipMessageMenuItem.IsChecked = _autoAdvance.IsSkip;
        CheckAutoSpeedItems();
        foreach (var item in SpeedMenu.Items.OfType<MenuItem>())
            item.IsChecked = Equals(item.Tag, "1.0");
        UiLog.Write("multi: guest state reset");
    }

    // ── 대사 기록 공유: 방장 게임의 대사를 참가자의 대사 기록으로 ──

    /// <summary>방장: 새 대사 한 줄 (OnMessageStateChanged에서)</summary>
    void MultiShareDialogue(string line)
    {
        if (_multi is { InRoom: true, IsHost: true } && _multi.Members.Count > 1 && _multiView?.CoreWebView2 != null)
            NotesSend("*", new JsonObject { ["op"] = "dlg", ["text"] = line });
    }

    /// <summary>방장: 새로 연결된 참가자에게 지금까지의 대사</summary>
    void SendDialogueLog(string to)
    {
        var lines = DialogueLogManager.GetEntries();
        if (lines.Count == 0) return;
        NotesSend(to, new JsonObject { ["op"] = "dlgs", ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()) });
    }

    /// <summary>참가자: 받은 대사</summary>
    static void OnDialogueMessage(JsonObject m)
    {
        if (m["op"]?.GetValue<string>() == "dlgs")
        {
            DialogueLogManager.Clear();
            foreach (var l in m["lines"] as JsonArray ?? new JsonArray())
                if (l?.GetValue<string>() is { } s) DialogueLogManager.Add(s);
        }
        else if (m["text"]?.GetValue<string>() is { } text) DialogueLogManager.Add(text);
    }

    void OnDialogueLogMenu(object sender, RoutedEventArgs e)
    {
        var win = new DialogueLogWindow { Owner = this };
        win.ShowDialog();
    }

    // ── 디스코드 '참가' / rocketrpg://join/코드 ──

    /// <summary>방 코드로 들어가기 (디스코드 참가 단추, 다른 RocketRPG에서 넘겨받음)</summary>
    async Task JoinByLinkAsync(string code)
    {
        if (_multi == null) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_multi.InRoom)
        {
            if (string.Equals(_multi.Room.Code, code, StringComparison.OrdinalIgnoreCase)) { ShowHudMessage("이미 이 방에 있습니다."); return; }
            var r = MessageBox.Show(this, $"지금 방에서 나가고 코드 {code} 방에 들어갈까요?", "방 입장하기", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            await _multi.LeaveAsync();
        }
        try
        {
            var room = await MultiClient.FindByCodeAsync(code);
            if (room == null) { MessageBox.Show(this, $"코드 {code} 방을 찾지 못했습니다. 방이 없어졌을 수 있습니다.", "방 입장하기", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (room.Count >= room.Max) { MessageBox.Show(this, "방이 가득 찼습니다.", "방 입장하기", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            await JoinRoomAsync(room);
        }
        catch (Exception ex)
        {
            UiLog.Write($"multi: join link failed {ex.Message}");
            MessageBox.Show(this, "서버에 연결하지 못했습니다.", "방 입장하기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
