#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RocketRPG.Models;

namespace RocketRPG.Views;

// ── 멀티: 방 만들기/입장/나가기, 멀티 메뉴, 참가자 대기 화면 ──
public partial class MainWindow
{
    MultiClient? _multi;

    // 방장 RocketRPG가 튕겨도 1분 안에 다시 켜면 같은 방으로 돌아가도록, 방 id와 방장 키를 10초마다 파일에 남깁니다.
    static string MultiResumeFile => System.IO.Path.Combine(SettingsService.Root(), "config", "multi_resume.json");
    readonly System.Windows.Threading.DispatcherTimer _multiResumeTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    bool InMultiRoom => _multi?.InRoom == true;
    bool IsMultiGuest => _multi is { InRoom: true, IsHost: false };

    void InitMulti()
    {
        var s = _ctl.Settings;
        bool dirty = false;
        if (string.IsNullOrWhiteSpace(s.MultiName)) { s.MultiName = MultiClient.NewDefaultName(); dirty = true; }
        if (string.IsNullOrWhiteSpace(s.MultiClientId)) { s.MultiClientId = Guid.NewGuid().ToString("N"); dirty = true; }
        if (dirty) _ctl.SaveSettings();

        _multi = new MultiClient();
        _multi.StateChanged += UpdateMultiUi;
        _multi.Closed += reason =>
        {
            UiLog.Write($"multi: left room ({reason})");
            UpdateMultiUi();
            MessageBox.Show(this, reason, "멀티", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        _multi.ErrorReceived += (_, text) => ShowHudMessage(text, 3000);
        _multi.SignalReceived += OnMultiSignal;
        InitMultiChat();
        InitMultiOwnership();
        InitMultiExtra();
        // 디스코드 '참가' 링크 (rocketrpg://join/코드)
        MultiLink.CodeReceived += code => _ = JoinByLinkAsync(code);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RR_PROFILE_ROOT")))
        {
            MultiLink.Listen();
            MultiLink.Register();
        }
        if (MultiLink.PendingCode is { } pending)
        {
            MultiLink.PendingCode = null;
            Dispatcher.BeginInvoke(async () => { await Task.Delay(800); await JoinByLinkAsync(pending); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        MultiHidePingsItem.IsChecked = s.MultiHidePings;
        MultiHideChatItem.IsChecked = s.MultiHideChat;
        _multiResumeTimer.Tick += (_, _) => SaveMultiResume();
        UpdateMultiUi();
        TryResumeHost();
        _ = MultiSelfTestAsync(Environment.GetEnvironmentVariable("RR_MULTI_TEST"));
    }

    /// <summary>
    /// 개발용 (scripts\multi_test.ps1): 한 PC에서 방장·참가자를 함께 띄워 멀티를 시험합니다.
    ///   RR_MULTI_TEST="host|게임 폴더[|60][|control]" : 'multi-test' 방을 만들고 게임을 켬 (control이면 조종 권한도 켬)
    ///   RR_MULTI_TEST="guest[|이름]"                  : 그 방에 들어감
    ///   RR_MULTI_TEST_CODEFILE=파일 : 방장은 비공개 방을 만들어 방 코드를 이 파일에 쓰고, 참가자는 이 파일의 코드로 들어감
    ///                                 (운영 서버에서 시험해도 공개 방 목록에 뜨지 않게). 없으면 공개 방 목록에서 찾음.
    /// </summary>
    async Task MultiSelfTestAsync(string? spec)
    {
        if (string.IsNullOrEmpty(spec) || _multi == null) return;
        string? codeFile = Environment.GetEnvironmentVariable("RR_MULTI_TEST_CODEFILE") is { Length: > 0 } cf ? cf : null;
        try
        {
            await Task.Delay(1500);
            var parts = spec.Split('|');
            if (parts[0] == "host")
            {
                var (id, code, key) = await MultiClient.CreateRoomAsync("multi-test", "", 4, listed: codeFile == null);
                string? err = await _multi.JoinAsync(id, _ctl.Settings.MultiName, _ctl.Settings.MultiClientId, hostKey: key);
                UiLog.Write($"multitest: hosting {code} {err}");
                if (err != null) return;
                if (codeFile != null) System.IO.File.WriteAllText(codeFile, code);
                if (parts.Length > 2 && parts[2] is "30" or "60") _multi.Send(new { t = "settings", fps = parts[2] == "60" ? 60 : 30 });
                if (parts.Contains("control")) _multi.Send(new { t = "settings", control = true });
                if (parts.Length > 1 && parts[1].Length > 0) Launch(parts[1]);
                return;
            }
            string name = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "guest-test";
            for (int i = 0; i < 60; i++)
            {
                MultiRoomSummary? room = null;
                if (codeFile != null)
                {
                    string code = System.IO.File.Exists(codeFile) ? System.IO.File.ReadAllText(codeFile).Trim() : "";
                    if (code.Length > 0) room = await MultiClient.FindByCodeAsync(code);
                }
                else room = (await MultiClient.ListRoomsAsync()).FirstOrDefault(r => r.Title == "multi-test");
                if (room != null)
                {
                    string? err = await _multi.JoinAsync(room.Id, name, _ctl.Settings.MultiClientId);
                    UiLog.Write($"multitest: joined {room.Code} as {name} {err}");
                    return;
                }
                await Task.Delay(1000);
            }
            UiLog.Write("multitest: room not found");
        }
        catch (Exception ex) { UiLog.Write($"multitest: failed {ex.Message}"); }
    }

    bool _resumeWritten;

    void SaveMultiResume()
    {
        try
        {
            if (_multi is { InRoom: true, IsHost: true })
            {
                System.IO.File.WriteAllText(MultiResumeFile, System.Text.Json.JsonSerializer.Serialize(new { room = _multi.RoomId, key = _multi.HostKey, pid = Environment.ProcessId }));
                _resumeWritten = true;
                if (!_multiResumeTimer.IsEnabled) _multiResumeTimer.Start();
            }
            else
            {
                _multiResumeTimer.Stop();
                if (_resumeWritten && System.IO.File.Exists(MultiResumeFile)) System.IO.File.Delete(MultiResumeFile);
                _resumeWritten = false;
            }
        }
        catch { }
    }

    /// <summary>방장이었다가 1분 안에 다시 켰으면 그 방으로 돌아갑니다 (서버가 아직 방을 기다리는 동안).</summary>
    async void TryResumeHost()
    {
        try
        {
            var f = new System.IO.FileInfo(MultiResumeFile);
            if (!f.Exists) return;
            bool fresh = DateTime.Now - f.LastWriteTime < TimeSpan.FromSeconds(75);
            var node = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(f.FullName));
            // 그 방장이 아직 켜져 있으면(RocketRPG를 하나 더 켠 경우) 건드리지 않습니다
            int pid = node?["pid"]?.GetValue<int>() ?? 0;
            if (pid > 0 && pid != Environment.ProcessId && IsRocketProcess(pid)) return;
            f.Delete();
            string room = node?["room"]?.GetValue<string>() ?? "", key = node?["key"]?.GetValue<string>() ?? "";
            if (!fresh || room.Length == 0 || key.Length == 0 || _multi == null) return;
            UiLog.Write("multi: resuming host session");
            string? err = await _multi.JoinAsync(room, _ctl.Settings.MultiName, _ctl.Settings.MultiClientId, hostKey: key);
            ShowHudMessage(err == null ? "방으로 돌아왔습니다." : "방에 돌아가지 못했습니다 (방이 이미 없어짐).", 4000);
        }
        catch (Exception ex) { UiLog.Write($"multi: resume failed {ex.Message}"); }
    }

    static bool IsRocketProcess(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.Equals(System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>메뉴 글자의 _ 는 단축키 표시로 쓰이므로 그대로 보이게 바꿉니다 (user_abc → user__abc)</summary>
    static string MenuText(string s) => s.Replace("_", "__");

    /// <summary>멀티 메뉴·대기 화면·제목을 지금 상태에 맞춥니다.</summary>
    void UpdateMultiUi()
    {
        if (_multi == null) return;
        bool inRoom = _multi.InRoom;
        bool host = _multi.IsHost;
        bool hideInfo = MultiHideInfo;   // 스트리머 모드(나 또는 방장): 방 제목·코드를 화면에 보이지 않음
        var room = _multi.Room;
        int count = _multi.Members.Count;
        ResetOwnershipIfNeeded(inRoom, host);
        if (inRoom && host) AskOwnership();   // 새로 들어온 참가자에게 게임 확인

        // 보이고 숨기는 규칙은 MultiMenuRules 한 곳에 (사용법 그림도 같은 규칙으로 그림)
        foreach (FrameworkElement item in new FrameworkElement[] { MultiRoomHeader, MultiCodeItem, MultiMembersMenu, MultiColorMenu, MultiRoomSeparator, MultiChatItem, MultiChatLogItem,
                                                                   MultiCreateItem, MultiJoinItem, MultiControlItem, MultiExtraItem, MultiSummonItem, MultiQualityMenu, MultiNotesEditItem, MultiLeaveItem, MultiDissolveItem })
            Vis(item, MultiMenuRules.Visible(item.Name, inRoom, host) ?? true);
        if (hideInfo) Vis(MultiCodeItem, false);
        MultiRoomHeader.Header = MenuText($"{(hideInfo ? "(방 제목 숨김)" : room.Settings.Title)} / 방 인원 {count}명{(_multi.Reconnecting ? " (다시 연결 중)" : "")}");
        MultiCodeItem.Header = $"방 코드 {room.Code} 복사{(room.Settings.Listed ? "" : " (비공개 방)")}";
        MultiMembersMenu.Header = $"참가자 ({count}/{room.Settings.Max})";
        MultiMembersMenu.Items.Clear();
        foreach (var m in _multi.Members) MultiMembersMenu.Items.Add(MemberMenuItem(m, host));
        UpdateColorMenu();
        MultiControlItem.IsChecked = room.Settings.Control;
        MultiExtraItem.IsChecked = _ctl.Settings.MultiExtraMode;
        MultiNotesEditItem.IsChecked = room.Settings.NotesEditable;
        MultiQualityLow.IsChecked = room.Settings.Quality == "low";
        MultiQualityNormal.IsChecked = room.Settings.Quality is not ("low" or "high");
        MultiQualityHigh.IsChecked = room.Settings.Quality == "high";
        MultiFps30.IsChecked = room.Settings.Fps < 60;
        MultiFps60.IsChecked = room.Settings.Fps >= 60;

        // 참가자 대기 화면: 방송 페이지가 뜨기 전까지 (뜨면 페이지가 영상과 안내 글을 보여 줌)
        bool guest = inRoom && !host;
        MultiWaitPanel.Visibility = guest && _multiPageRole != "guest" ? Visibility.Visible : Visibility.Collapsed;
        if (guest)
        {
            string status = MultiGuestStatus();
            MultiWaitText.Text = status.Length > 0 ? status : $"{room.Game}\n방장 화면을 받는 중...";
        }
        SaveMultiResume();
        MultiAfterUiUpdate();
        SyncVoteMembers();
        SyncMultiMedia();
        SyncMultiOverlay();
    }

    static void Vis(UIElement e, bool visible) => e.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>방장: 지금 하는 게임을 방에 알림 ("" = 대기 중)</summary>
    void MultiReportGame(string title)
    {
        // 게임을 켜고 끄면 열려 있던 선택지 투표는 끝
        if (_vote.Open) OnGameChoice(_currentBridge!, new ChoiceState { Gen = _vote.Gen, Open = false });
        if (_multi is { InRoom: true, IsHost: true }) _multi.Send(new { t = "game", title });
        OwnershipGameChanged(title.Length > 0);   // 스팀 게임이면 참가자도 가지고 있는지 확인
        SyncMultiMedia();   // 게임을 켜고 끌 때 방송 시작/멈춤
    }

    async void OnMultiCreate(object sender, RoutedEventArgs e)
    {
        if (_multi == null || _multi.InRoom) return;
        var dlg = new MultiCreateWindow(this, $"{_ctl.Settings.MultiName}의 방");
        if (dlg.ShowDialog() != true) return;
        var (title, password, max, listed) = dlg.Result;
        try
        {
            ShowHudMessage("방을 만드는 중...");
            var (id, code, hostKey) = await MultiClient.CreateRoomAsync(title, password, max, listed);
            string? err = await _multi.JoinAsync(id, _ctl.Settings.MultiName, _ctl.Settings.MultiClientId, hostKey: hostKey);
            if (err != null) { MessageBox.Show(this, err, "멀티", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            UiLog.Write($"multi: created room {code} (max {max}, listed {listed})");
            if (_currentBridge != null) MultiReportGame(CurrentGameTitle());
            ShowHudMessage(listed ? "방을 만들었습니다." : $"비공개 방을 만들었습니다. 방 코드: {code}", 4000);
        }
        catch (Exception ex)
        {
            UiLog.Write($"multi: create failed {ex.Message}");
            MessageBox.Show(this, ex is InvalidOperationException ? ex.Message : "서버에 연결하지 못했습니다. 인터넷 연결을 확인해 주세요.",
                "멀티", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    async void OnMultiJoin(object sender, RoutedEventArgs e)
    {
        if (_multi == null || _multi.InRoom) return;
        var dlg = new MultiJoinWindow(this);
        if (dlg.ShowDialog() != true || dlg.Selected is not { } room) return;
        await JoinRoomAsync(room);
    }

    async Task JoinRoomAsync(MultiRoomSummary room)
    {
        if (_multi == null) return;
        string password = "";
        if (room.Locked)
        {
            string? pw = PasswordDialog.Ask(this, "방 입장하기", $"'{room.Title}' 방의 비밀번호");
            if (pw == null) return;
            password = pw;
        }
        // 참가자는 방장 화면을 보므로 내 게임은 끕니다
        if (_currentBridge != null)
        {
            var r = MessageBox.Show(this, "방에 들어가면 지금 하던 게임을 끕니다. 계속할까요?", "방 입장하기", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            StopNativeSession("방에 들어가려고 게임을 껐습니다.");
        }
        string? err = await _multi.JoinAsync(room.Id, _ctl.Settings.MultiName, _ctl.Settings.MultiClientId, password);
        if (err != null) { MessageBox.Show(this, err, "방 입장하기", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        UiLog.Write($"multi: joined room {room.Code}");
        ShowHudMessage("방에 들어왔습니다.");
    }

    void OnMultiCopyCode(object sender, RoutedEventArgs e)
    {
        if (_multi?.InRoom != true) return;
        try { Clipboard.SetText(_multi.Room.Code); ShowHudMessage($"방 코드를 복사했습니다: {_multi.Room.Code}"); }
        catch { ShowHudMessage($"방 코드: {_multi.Room.Code}"); }
    }

    void OnMultiRename(object sender, RoutedEventArgs e)
    {
        string? name = InputDialog.Ask(this, "이름 설정하기", "방에서 보일 내 이름 (다른 사람과 겹쳐도 됩니다):", _ctl.Settings.MultiName);
        if (name == null) return;
        name = name.Trim();
        if (name.Length == 0) name = MultiClient.NewDefaultName();
        if (name.Length > 20) name = name[..20];
        _ctl.Settings.MultiName = name;
        _ctl.SaveSettings();
        if (_multi?.InRoom == true) _multi.Rename(name);
        ShowHudMessage($"이름: {name}");
    }

    void OnMultiHidePings(object sender, RoutedEventArgs e)
    {
        _ctl.Settings.MultiHidePings = MultiHidePingsItem.IsChecked;
        _ctl.SaveSettings();
        OnMultiHidePingsChanged();
    }

    void OnMultiHideChat(object sender, RoutedEventArgs e)
    {
        _ctl.Settings.MultiHideChat = MultiHideChatItem.IsChecked;
        _ctl.SaveSettings();
        OnMultiHideChatChanged();
    }

    void OnMultiControl(object sender, RoutedEventArgs e)
    {
        if (_multi is not { InRoom: true, IsHost: true }) return;
        bool on = MultiControlItem.IsChecked;
        _multi.Send(new { t = "settings", control = on });
        ShowHudMessage(on ? "조종 권한: 켜짐 (참가자도 키보드로 조작)" : "조종 권한: 꺼짐");
    }

    async void OnMultiLeave(object sender, RoutedEventArgs e)
    {
        if (_multi?.InRoom != true) return;
        await _multi.LeaveAsync();
        ShowHudMessage("방에서 나왔습니다.");
    }

    async void OnMultiDissolve(object sender, RoutedEventArgs e)
    {
        if (_multi is not { InRoom: true, IsHost: true }) return;
        var r = MessageBox.Show(this, "방을 해체할까요? 모든 참가자가 방에서 나가게 됩니다.", "방 해체하기", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        await _multi.LeaveAsync();
        ShowHudMessage("방을 해체했습니다.");
    }

    bool _closeAfterLeave;

    /// <summary>창을 닫을 때: 방장이면 해체할지 묻고, 참가자면 조용히 나갑니다 (서버에 알린 뒤 닫음).</summary>
    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeAfterLeave && _multi?.InRoom == true)
        {
            e.Cancel = true;
            if (_multi.IsHost)
            {
                var r = MessageBox.Show(this, "방장이 RocketRPG를 끄면 방이 해체됩니다. 끌까요?", "RocketRPG", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }
            _closeAfterLeave = true;
            try { await Task.WhenAny(_multi.LeaveAsync(), Task.Delay(2500)); } catch { }
            Close();
            return;
        }
        base.OnClosing(e);
    }
}
