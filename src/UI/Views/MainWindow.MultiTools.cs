#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using RocketRPG.Models;

namespace RocketRPG.Views;

// ── 멀티 상태 맞추기: 메시지 바·도구·ESP·스트리머 모드 ──
// 방장이 기준입니다. 방장은 지금 상태를 한 덩어리(스냅샷)로 만들어 바뀔 때마다 바로, 그리고 2초마다 다시 보냅니다.
// 참가자는 그 상태를 보여 주기만 하고, 누르면 방장에게 '이렇게 해 주세요'(원하는 값)를 보냅니다. 실행과 권한 검사는 방장이 합니다.
//   조종 권한 없음 또는 방장 스트리머 켬: 참가자 메시지 바는 방장 바를 따라 오르내리고 '기록'(내 대사 기록 창)만 누를 수 있음.
//   조종 권한 있음 + 방장 스트리머 끔   : 메시지 바 단추가 모두 보이고 누르면 방장 게임에서 실행. 도구·ESP도 함께.
//   (조종 권한은 방 전체 설정이라 모든 참가자에게 같습니다)
// 선택지 투표 중에는 조종 권한이 있어도 참가자의 키·메시지 바·도구 요청을 받지 않습니다 (선택은 방장이 함, MainWindow.MultiVote).
// 권한(스트리머·조종)이 바뀌면 policy 번호가 올라가고, 예전 번호로 온 요청은 버립니다.
// 맵 뷰어·에셋 보기는 어떤 경우에도 주고받지 않습니다 (각자 자기 PC의 게임 파일로만).
public partial class MainWindow
{
    // 방장
    string _toolsSent = "", _toolPolicyKey = "";
    int _toolPolicy, _toolsRev;
    readonly string _toolsSession = Guid.NewGuid().ToString("N")[..8];   // 방장이 바뀌면 참가자가 번호를 처음부터 받도록
    readonly Dictionary<string, long> _toolReq = new();                   // 참가자별 마지막 일회성 요청 번호 (퀵 세이브 등 중복 실행 막기)
    IGameBridge? _toolsSource;
    bool _espShared;
    IGameBridge? _espShareBridge;
    long _espSentAt;
    // 참가자
    JsonObject? _guestTools;
    int _guestToolsRev = -1;
    string _guestToolsSession = "";
    long _guestReqSeq;
    MultiGuestBridge? _guestBridge;

    /// <summary>방장이 스트리머 모드인지 (참가자: 방장 상태 스냅샷 우선, 없으면 방 설정)</summary>
    bool HostStreamer => _guestTools?["streamer"]?.GetValue<bool>() ?? _multi?.Room.Settings.Streamer == true;

    /// <summary>참가자: 메시지 바 단추·도구(벽 통과·배속·ESP·변수 관리자 등)를 방장에게 요청할 수 있음 (조종 권한 + 방장 스트리머 끔)</summary>
    bool GuestToolsAllowed => IsMultiGuest && _multi!.Room.Settings.Control && !HostStreamer &&
        !_ctl.Settings.StreamerMode && _guestTools?["running"]?.GetValue<bool>() == true;

    /// <summary>참가자: 방장 ESP를 내 화면에 그림 (방장이 ESP를 켜고 스트리머 모드가 아님, 게임 중)</summary>
    bool GuestEspVisible => IsMultiGuest && !HostStreamer && _guestTools?["esp"]?.GetValue<bool>() == true &&
        _guestTools?["running"]?.GetValue<bool>() == true;

    /// <summary>메시지 바 단추도 도구와 같은 권한 (조종 권한이 없으면 '기록'만)</summary>
    bool GuestBarAllowed => GuestToolsAllowed;

    /// <summary>도구 권한이 있어야 하는 요청</summary>
    static readonly HashSet<string> SharedActions = ["QuickSave", "QuickLoad", "ForceSaveMenu", "ForceLoadMenu",
        "ToggleNoclip", "ToggleEspOverlay", "ToggleAutoMessage", "ToggleSkipMessage",
        "SpeedUp", "SpeedDown", "SpeedReset", "TogglePause", "ToggleMessageBar"];

    /// <summary>메시지 바 단추 (단축키로도 요청할 수 있는 것)</summary>
    static readonly HashSet<string> BarActions = ["ToggleAutoMessage", "ToggleSkipMessage", "AutoSpeed", "QuickSave", "QuickLoad"];

    /// <summary>한 번 실행하는 요청 (켜기/끄기가 아니라 중복 실행을 막아야 함)</summary>
    static readonly HashSet<string> OneShotActions = ["QuickSave", "QuickLoad", "ForceSaveMenu", "ForceLoadMenu"];

    /// <summary>참가자: 도구·메시지 바 동작을 방장에게 요청 (참가자가 아니면 false → 내 게임에서 실행)</summary>
    bool SendGuestTool(string action, double? value = null)
    {
        if (!IsMultiGuest) return false;
        if (_guestVote != null)
        {
            ShowHudMessage("선택지 투표 중에는 방장이 고릅니다. 오른쪽 상자에서 투표해 주세요.");
            return true;
        }
        if (!GuestToolsAllowed)
        {
            ShowHudMessage(HostStreamer ? "방장이 스트리머 모드라 사용할 수 없습니다."
                : _guestTools?["running"]?.GetValue<bool>() != true ? "방장이 게임을 켜면 사용할 수 있습니다."
                : "방장이 조종 권한을 켜야 사용할 수 있습니다.");
            return true;
        }
        // 켜기/끄기는 '반대로'가 아니라 원하는 값을 보냄: 두 번 눌려도, 늦게 도착해도 결과가 같게
        bool B(string name) => _guestTools?[name]?.GetValue<bool>() == true;
        int at = Array.IndexOf(SpeedSteps, _guestTools?["speed"]?.GetValue<double>() ?? 1);
        if (at < 0) at = Array.IndexOf(SpeedSteps, 1.0);
        switch (action)
        {
            case "ToggleAutoMessage": value ??= B("auto") ? 0 : 1; break;
            case "ToggleSkipMessage": value ??= B("skip") ? 0 : 1; break;
            case "ToggleNoclip": value ??= B("noclip") ? 0 : 1; break;
            case "ToggleEspOverlay": value ??= B("esp") ? 0 : 1; break;
            case "TogglePause": value ??= B("paused") ? 0 : 1; break;
            case "ToggleMessageBar": value ??= B("showBar") ? 0 : 1; break;
            case "SpeedUp": action = "SetSpeed"; value = SpeedSteps[Math.Min(SpeedSteps.Length - 1, at + 1)]; break;
            case "SpeedDown": action = "SetSpeed"; value = SpeedSteps[Math.Max(0, at - 1)]; break;
            case "SpeedReset": action = "SetSpeed"; value = 1; break;
        }
        NotesSend("host", new JsonObject
        {
            ["op"] = "tool", ["action"] = action, ["value"] = value,
            ["policy"] = _guestTools?["policy"]?.GetValue<int>() ?? -1, ["req"] = ++_guestReqSeq
        });
        return true;
    }

    // ── 방장 ──

    /// <summary>
    /// 지금 상태를 참가자에게 (바뀌었을 때만, force면 그대로라도). 새 참가자에게는 연결 즉시 to = 그 사람으로 보냅니다.
    /// UI 스레드에서 50ms 오버레이 틱마다(250ms 간격) + 상태가 바뀌는 곳에서 바로 부릅니다.
    /// </summary>
    void SyncMultiTools(string to = "*", bool force = false)
    {
        if (_multi is not { InRoom: true, IsHost: true }) return;
        if (_toolsSource != _currentBridge)
        {
            if (_toolsSource != null) _toolsSource.DataInspectorUpdated -= ShareInspector;
            _toolsSource = _currentBridge;
            if (_toolsSource != null) _toolsSource.DataInspectorUpdated += ShareInspector;
        }
        bool streamer = _ctl.Settings.StreamerMode, control = _multi.Room.Settings.Control;
        string policyKey = $"{streamer}|{control}";
        if (policyKey != _toolPolicyKey) { _toolPolicyKey = policyKey; _toolPolicy++; }
        bool tools = control && !streamer;
        var state = new JsonObject
        {
            ["op"] = "tools", ["hs"] = _toolsSession, ["policy"] = _toolPolicy, ["streamer"] = streamer, ["control"] = control,
            ["running"] = _isNativeRunning && _currentBridge != null, ["bar"] = _msgBarShown, ["showBar"] = _messageBarEnabled,
            ["auto"] = _autoAdvance.IsAuto, ["skip"] = _autoAdvance.IsSkip, ["autoSpeed"] = _autoAdvance.Speed,
        };
        // 도구 상태와 게임 위치는 참가자가 도구를 쓸 수 있을 때만 (스트리머 모드에서는 보내지 않음)
        if (tools)
        {
            state["speed"] = _currentBridge?.CurrentSpeed ?? 1;
            state["noclip"] = _currentBridge?.IsNoclip == true;
            state["paused"] = _currentBridge?.IsPaused == true;
            state["game"] = _currentBridge == null ? null : JsonSerializer.SerializeToNode(_currentBridge.LatestState);
        }
        // ESP는 보기만 하는 것이라 조종 권한과 상관없이 방장이 켜면 함께 봄 (스트리머 모드에서는 숨김)
        if (!streamer) state["esp"] = EspOverlayMenuItem.IsChecked;
        UpdateEspShare(!streamer);
        string json = state.ToJsonString();
        if (to == "*")
        {
            if (json == _toolsSent && !force) return;
            _toolsSent = json;
        }
        state["rev"] = ++_toolsRev;
        NotesSend(to, state);
    }

    void ShareInspector(List<SwitchItem> switches, List<VariableItem> variables)
    {
        Dispatcher.BeginInvoke(() => {
            if (_multi is not { InRoom: true, IsHost: true } || !_multi.Room.Settings.Control || _ctl.Settings.StreamerMode) return;
            NotesSend("*", new JsonObject { ["op"] = "inspector", ["switches"] = JsonSerializer.SerializeToNode(switches), ["variables"] = JsonSerializer.SerializeToNode(variables) });
        });
    }

    /// <summary>
    /// 방장: ESP를 켰으면 참가자도 ESP를 봄 (조종 권한과 상관없음, 스트리머 모드에서는 숨김).
    /// 2000/2003·XP/VX/Ace는 게임이 그리는 ESP를 방송 화면에 함께 넣고, MV/MZ는 ESP 항목(위치·짧은 이름·종류)만 보냅니다.
    /// </summary>
    void UpdateEspShare(bool allowed)
    {
        bool share = allowed && EspOverlayMenuItem.IsChecked && _multi!.Members.Any(m => !m.Host);
        if (share == _espShared && ReferenceEquals(_espShareBridge, _currentBridge)) return;
        if (_espShareBridge is RocketRenderEasyRPG oe) oe.SetEspShare(false);
        else if (_espShareBridge is RocketRenderMKXP om) om.SetEspShare(false);
        _espShared = share;
        _espShareBridge = _currentBridge;
        if (_currentBridge is RocketRenderEasyRPG e) e.SetEspShare(share);
        else if (_currentBridge is RocketRenderMKXP mk) mk.SetEspShare(share);
        if (!share && ReferenceEquals(_currentBridge, _webRenderer)) NotesSend("*", new JsonObject { ["op"] = "esp", ["items"] = new JsonArray() });
        UiLog.Write($"multi: esp share {(share ? "on" : "off")}");
    }

    /// <summary>방장 (MV/MZ): 새 ESP 항목을 참가자에게 (초당 8번까지)</summary>
    void ShareEspItems(List<EspItem> items)
    {
        if (!_espShared || !ReferenceEquals(_currentBridge, _webRenderer) || _multi is not { InRoom: true, IsHost: true }) return;
        long now = Environment.TickCount64;
        if (now - _espSentAt < 125) return;
        _espSentAt = now;
        GetGameViewport(out _, out _, out _, out _, out int bw, out int bh);
        var arr = new JsonArray();
        foreach (var it in items.Take(128))
            arr.Add(new JsonObject
            {
                ["id"] = it.Id, ["name"] = it.Name.Length > 12 ? it.Name[..12] : it.Name, ["trigger"] = it.Trigger,
                ["x"] = Math.Round(it.X, 1), ["y"] = Math.Round(it.Y, 1), ["w"] = Math.Round(it.W, 1), ["h"] = Math.Round(it.H, 1),
            });
        NotesSend("*", new JsonObject { ["op"] = "esp", ["bw"] = bw, ["bh"] = bh, ["items"] = arr });
    }

    bool OnMultiToolMessage(string from, JsonObject m)
    {
        string op = m["op"]?.GetValue<string>() ?? "";
        if (op is not ("tools" or "tool" or "inspect" or "inspector" or "editData" or "esp")) return false;
        try
        {
            if (_multi is { InRoom: true, IsHost: true })
            {
                if (_currentBridge == null || !_multi.Members.Any(x => x.Id == from && !x.Host)) return true;
                bool streamer = _ctl.Settings.StreamerMode, tools = _multi.Room.Settings.Control && !streamer;
                // 권한이 바뀐 뒤에 도착한 요청은 버림 (예전 권한으로 누른 것)
                if (m["policy"]?.GetValue<int>() is int policy && policy != _toolPolicy)
                {
                    UiLog.Write($"multi: dropped {op} from {from} (policy {policy} != {_toolPolicy})");
                    return true;
                }
                if (op == "tool")
                {
                    string action = m["action"]?.GetValue<string>() ?? "";
                    if (!tools) return true;
                    if (_vote.Open) { UiLog.Write($"multi: ignored {action} from {from} (choice vote open)"); return true; }
                    if (OneShotActions.Contains(action))
                    {
                        long req = m["req"]?.GetValue<long>() ?? 0;
                        if (req <= _toolReq.GetValueOrDefault(from)) return true;
                        _toolReq[from] = req;
                    }
                    RunGuestTool(action, m["value"] is JsonValue v && v.TryGetValue<double>(out double d) ? d : null);
                    SyncMultiTools();
                }
                else if (!tools) return true;
                else if (op == "inspect") _currentBridge.RequestDataInspector();
                else if (op == "editData")
                {
                    int id = m["id"]?.GetValue<int>() ?? 0;
                    if (id < 1 || id > 100_000) return true;
                    string value = m["value"]?.GetValue<string>() ?? "";
                    if (value.Length > 1024) return true;
                    bool on = m["on"]?.GetValue<bool>() == true;
                    switch (m["kind"]?.GetValue<string>()) {
                        case "switch": _currentBridge.SetSwitch(id, on); break;
                        case "variable": _currentBridge.SetVariable(id, value); break;
                        case "freezeSwitch": _currentBridge.FreezeSwitch(id, on, value == "true"); break;
                        case "freezeVariable": _currentBridge.FreezeVariable(id, on, value); break;
                    }
                }
            }
            else if (IsMultiGuest && from == "host")
            {
                if (op == "tools") OnHostToolsState(m);
                else if (op == "inspector" && GuestToolsAllowed)
                    GuestBridge.ReceiveData(m["switches"]?.Deserialize<List<SwitchItem>>() ?? [], m["variables"]?.Deserialize<List<VariableItem>>() ?? []);
                else if (op == "esp") OnHostEsp(m);
            }
        }
        catch (Exception ex) { UiLog.Write($"multi: invalid tool message {op}: {ex.Message}"); }
        return true;
    }

    /// <summary>방장: 참가자 요청 실행. value가 있으면 그 값으로 맞춤(이미 그렇다면 아무것도 안 함).</summary>
    void RunGuestTool(string action, double? value)
    {
        bool? want = value is double v ? v >= 0.5 : null;
        switch (action)
        {
            case "ToggleAutoMessage": if (want != _autoAdvance.IsAuto) _autoAdvance.ToggleAuto(); break;
            case "ToggleSkipMessage": if (want != _autoAdvance.IsSkip) _autoAdvance.ToggleSkip(); break;
            case "ToggleNoclip": if (want != _currentBridge?.IsNoclip) ToggleNoclip(); break;
            case "TogglePause": if (want != _currentBridge?.IsPaused) ExecuteHotkeyAction("TogglePause"); break;
            case "ToggleMessageBar": if (want != _messageBarEnabled) ExecuteHotkeyAction("ToggleMessageBar"); break;
            case "ToggleEspOverlay":
                if (want != EspOverlayMenuItem.IsChecked)
                {
                    EspOverlayMenuItem.IsChecked = !EspOverlayMenuItem.IsChecked;
                    OnToggleEspOverlay(EspOverlayMenuItem, new RoutedEventArgs());
                }
                break;
            case "SetSpeed" when value is double s && SpeedSteps.Contains(s): SetSpeed(s); break;
            case "AutoSpeed" when value is double a && new[] { .5, 1, 1.5, 2, 3 }.Contains(a):
                _autoAdvance.Speed = a;
                CheckAutoSpeedItems();
                break;
            case "QuickSave" or "QuickLoad" or "ForceSaveMenu" or "ForceLoadMenu": ExecuteHotkeyAction(action); break;
        }
    }

    // ── 참가자 ──

    MultiGuestBridge GuestBridge => _guestBridge ??= new MultiGuestBridge(m =>
    {
        if (!GuestToolsAllowed) return;
        m["policy"] = _guestTools?["policy"]?.GetValue<int>() ?? -1;
        NotesSend("host", m);
    });

    /// <summary>참가자: 방장 상태 스냅샷 (늦게 온 옛 것은 버림)</summary>
    void OnHostToolsState(JsonObject m)
    {
        string hs = m["hs"]?.GetValue<string>() ?? "";
        int rev = m["rev"]?.GetValue<int>() ?? 0;
        if (hs == _guestToolsSession && rev <= _guestToolsRev) return;
        _guestToolsSession = hs;
        _guestToolsRev = rev;
        _guestTools = m;
        UpdateGuestTools();
        if (m["game"] is JsonNode game && GuestToolsAllowed) GuestBridge.ReceiveState(game.Deserialize<GameState>()!);
    }

    /// <summary>참가자 (MV/MZ 방장): 방장 ESP 항목을 내 방송 화면 위에 그림</summary>
    void OnHostEsp(JsonObject m)
    {
        if (!GuestEspVisible || m["items"] is not JsonArray arr || arr.Count == 0)
        {
            EspCanvas.Clear();
            return;
        }
        int bw = m["bw"]?.GetValue<int>() ?? 0, bh = m["bh"]?.GetValue<int>() ?? 0;
        if (bw <= 0 || bh <= 0 || _multiView == null) return;
        var items = new List<EspItem>();
        foreach (var n in arr.Take(128))
        {
            if (n is not JsonObject o) continue;
            items.Add(new EspItem
            {
                Id = o["id"]?.GetValue<int>() ?? 0, Name = o["name"]?.GetValue<string>() ?? "", Trigger = o["trigger"]?.GetValue<int>() ?? 0,
                X = o["x"]?.GetValue<double>() ?? 0, Y = o["y"]?.GetValue<double>() ?? 0, W = o["w"]?.GetValue<double>() ?? 0, H = o["h"]?.GetValue<double>() ?? 0,
            });
        }
        Rect r = _guestVideoRect.IsEmpty || _guestVideoRect.Width < 8 ? new Rect(0, 0, _multiView.ActualWidth, _multiView.ActualHeight) : _guestVideoRect;
        try { r = new Rect(_multiView.TranslatePoint(r.TopLeft, RenderScreen), r.Size); } catch (InvalidOperationException) { return; }
        EspCanvas.SetViewport(r, bw, bh);
        EspCanvas.Update(items);
    }

    /// <summary>방 상태·방장 상태가 바뀔 때: 메시지 바 모양, 도구 메뉴 켜기/끄기와 체크, 열린 도구 창</summary>
    void UpdateGuestTools()
    {
        if (!IsMultiGuest)
        {
            _guestTools = null;
            _guestToolsRev = -1;
            _guestToolsSession = "";
            _toolsSent = "";
            RenpyMsgBar.SetRemote(false);
            RenpyMsgBar.SetLogOnly(false);
            if (_multiView != null) _multiView.Margin = new Thickness(0);
            return;
        }
        bool tools = GuestToolsAllowed && _guestVote == null, bar = tools;
        bool B(string name) => _guestTools?[name]?.GetValue<bool>() == true;
        double D(string name, double fallback) => _guestTools?[name]?.GetValue<double>() ?? fallback;
        // 메시지 바: 조종 권한이 없거나 방장이 스트리머거나 투표 중이면 '기록'만, 아니면 방장 상태 그대로 (누르면 방장 게임에서 실행)
        RenpyMsgBar.SetLogOnly(!bar);
        RenpyMsgBar.SetRemote(true, B("auto"), B("skip"), D("autoSpeed", 1));
        // 방장처럼 방송 화면 아래에 메시지 바 자리를 비워 둠 (안 그러면 바가 게임 대화창 위에 겹침)
        if (_multiView != null)
        {
            var margin = new Thickness(0, 0, 0, B("showBar") && B("running") ? MessageBarHeight : 0);
            if (_multiView.Margin != margin) _multiView.Margin = margin;
        }
        foreach (var item in new[] { QuickSaveMenuItem, QuickLoadMenuItem, AutoMessageMenuItem, SkipMessageMenuItem, AutoSpeedMenu }) item.IsEnabled = bar;
        foreach (var item in new[] { NoclipMenuItem, SpeedMenu, EspOverlayMenuItem, ShowMessageBarMenuItem }) item.IsEnabled = tools;
        // 타일 인스펙터는 방장 마우스로만 동작하고, 맵 뷰어·에셋 보기는 각자 자기 PC 파일로만 열 수 있습니다.
        TileInspectorMenuItem.IsEnabled = false;
        foreach (var item in ToolMenu.Items.OfType<MenuItem>())
            if (item.Name.Contains("DataInspector")) item.IsEnabled = tools;
        ScreenMenu.IsEnabled = true;
        if (!tools) _dataInspectorWnd?.Close();
        if (!GuestEspVisible) EspCanvas.Clear();
        AutoMessageMenuItem.IsChecked = B("auto"); SkipMessageMenuItem.IsChecked = B("skip");
        NoclipMenuItem.IsChecked = B("noclip"); EspOverlayMenuItem.IsChecked = B("esp"); TileInspectorMenuItem.IsChecked = false;
        ShowMessageBarMenuItem.IsChecked = B("showBar");
        foreach (var item in SpeedMenu.Items.OfType<MenuItem>())
            item.IsChecked = double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && Math.Abs(s - D("speed", 1)) < .01;
        foreach (var item in AutoSpeedMenu.Items.OfType<MenuItem>())
            item.IsChecked = double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && Math.Abs(s - D("autoSpeed", 1)) < .01;
        UpdateMessageBarVisibility();
    }
}
