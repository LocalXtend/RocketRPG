#nullable enable
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using RocketRPG.Models;

namespace RocketRPG.Views;

// ── 멀티: 스팀 게임 소유 확인 ──
// 방장이 스팀 라이브러리 안의 게임을 하면, 참가자마다 같은 게임(exe + 데이터 파일)을 가지고 있는지 물어보고
// 맞게 답한 사람만 방장 화면·소리·조종을 받습니다. 스팀 게임이 아니면 확인하지 않습니다. (방 서버의 relay "own"으로 주고받음)
public partial class MainWindow
{
    readonly OwnershipGate _own = new();
    int _ownIdentifyTicket;
    bool _ownWasRequired;
    string _ownGuestStatus = "";   // 참가자: 확인 결과 안내 ("" = 문제 없음)

    const string OwnChannel = "own";

    void InitMultiOwnership()
    {
        _multi!.RelayReceived += (ch, from, data) => { if (ch == OwnChannel && data is JsonObject o) OnOwnershipMessage(from, o); };
    }

    /// <summary>방장: 게임을 켜고 끌 때. 스팀 게임이면 열쇠를 만들고(큰 파일은 몇 초) 참가자에게 물어봄</summary>
    void OwnershipGameChanged(bool running)
    {
        int ticket = ++_ownIdentifyTicket;
        bool steam = running && _multi is { InRoom: true, IsHost: true } && !string.IsNullOrEmpty(_currentDir) && GameOwnership.SteamLocation(_currentDir) != null;
        _own.SetGame(null, pending: steam);   // 스팀 게임이면 알아보는 몇 초 동안도 막아 둠
        OwnershipGateChanged();
        if (!steam) return;
        string dir = _currentDir!;
        int engine = _ctl.Current.Engine;
        string exe = _ctl.Current.ExeName;
        _ = Task.Run(() =>
        {
            try { return GameOwnership.Identify(dir, engine, exe); }
            catch (Exception ex) { UiLog.Write($"multi own: identify failed {ex.Message}"); return null; }
        }).ContinueWith(t =>
        {
            if (ticket != _ownIdentifyTicket) return;
            _own.SetGame(t.Result);   // 파일을 못 찾으면 null: 확인하지 않음
            if (t.Result != null) UiLog.Write($"multi own: steam game {t.Result.InstallDir} ({t.Result.DataRel}), checking participants");
            OwnershipGateChanged();
            AskOwnership();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>방장: 아직 묻지 않은 참가자에게 물음을 보냄 (참가자가 들어올 때마다)</summary>
    void AskOwnership()
    {
        if (_multi is not { InRoom: true, IsHost: true } || _own.Game is not { } game) return;
        _own.Forget(_multi.Members.Select(m => m.Id));
        foreach (var m in _multi.Members.Where(m => !m.Host))
        {
            if (_own.NonceFor(m.Id) is not { } nonce) continue;
            var data = new JsonObject { ["k"] = "ask", ["gen"] = _own.Gen, ["nonce"] = nonce, ["game"] = game.Describe() };
            _multi.Send(new { t = "relay", ch = OwnChannel, to = m.Id, data });
        }
    }

    /// <summary>방장: 확인 상태가 바뀜 → 방송 페이지에 받을 사람 목록을 다시 보내고 참가자 목록 표시를 고침</summary>
    void OwnershipGateChanged()
    {
        // 스팀 게임을 끄거나 바꾸면 참가자에게 남아 있던 '게임 없음' 안내를 지움
        if (_ownWasRequired && !_own.Required && _multi is { InRoom: true, IsHost: true })
            _multi.Send(new { t = "relay", ch = OwnChannel, data = new JsonObject { ["k"] = "clear" } });
        _ownWasRequired = _own.Required;
        _multiMembersSent = "\u0001";
        UpdateMultiUi();
    }

    void OnOwnershipMessage(string from, JsonObject m)
    {
        if (_multi is not { InRoom: true }) return;
        string k = m["k"]?.GetValue<string>() ?? "";
        int gen = m["gen"] is JsonValue g && g.TryGetValue<int>(out int gv) ? gv : -1;
        if (_multi.IsHost)
        {
            if (k is not ("proof" or "none")) return;
            bool? ok = _own.Check(from, gen, k == "proof" ? m["proof"]?.GetValue<string>() : null);
            if (ok == null) return;
            string name = _multi.Members.FirstOrDefault(x => x.Id == from)?.Name ?? from;
            UiLog.Write($"multi own: {from} {(ok == true ? "has the game" : k == "none" ? "does not have the game" : "has different game files")}");
            if (ok != true) ShowHudMessage($"{name}: 이 스팀 게임을 가지고 있지 않아 함께할 수 없습니다.", 4000);
            _multi.Send(new { t = "relay", ch = OwnChannel, to = from, data = new JsonObject { ["k"] = "result", ["gen"] = gen, ["ok"] = ok == true, ["why"] = k } });
            OwnershipGateChanged();
            return;
        }
        if (from != _multi.Room.HostId) return;
        switch (k)
        {
            case "ask":
                if (m["game"] is not JsonObject game) return;
                string nonce = m["nonce"]?.GetValue<string>() ?? "";
                SetOwnGuestStatus("방장 게임(스팀)을 이 PC에서 확인하는 중...");
                _ = Task.Run(() =>
                {
                    string? dir = GameOwnership.FindLocalCopy(game);
                    return dir == null ? null : GameOwnership.Proof(GameOwnership.KeyOf(dir, game), nonce);
                }).ContinueWith(t =>
                {
                    string? proof = t.IsCompletedSuccessfully ? t.Result : null;
                    var reply = new JsonObject { ["k"] = proof != null ? "proof" : "none", ["gen"] = gen };
                    if (proof != null) reply["proof"] = proof;
                    _multi?.Send(new { t = "relay", ch = OwnChannel, to = from, data = reply });
                    if (proof == null)
                        SetOwnGuestStatus("방장이 하는 스팀 게임을 이 PC에서 찾지 못해 함께할 수 없습니다.\n같은 게임을 스팀에서 설치하면 함께할 수 있습니다.");
                }, TaskScheduler.FromCurrentSynchronizationContext());
                break;
            case "result":
                bool ok = m["ok"]?.GetValue<bool>() == true;
                SetOwnGuestStatus(ok ? "" : m["why"]?.GetValue<string>() == "none"
                    ? "방장이 하는 스팀 게임을 이 PC에서 찾지 못해 함께할 수 없습니다.\n같은 게임을 스팀에서 설치하면 함께할 수 있습니다."
                    : "방장과 게임 파일이 달라 함께할 수 없습니다.\n스팀에서 게임을 최신 버전으로 업데이트해 주세요.");
                if (ok) PostMulti("{\"t\":\"retry\"}");   // 기다리지 않고 바로 방장 화면에 연결
                break;
            case "clear":
                SetOwnGuestStatus("");
                PostMulti("{\"t\":\"retry\"}");
                break;
        }
    }

    void SetOwnGuestStatus(string text)
    {
        if (_ownGuestStatus == text) return;
        _ownGuestStatus = text;
        UpdateMultiUi();
    }

    /// <summary>방을 나가거나 역할이 바뀌면 지움</summary>
    void ResetOwnershipIfNeeded(bool inRoom, bool host)
    {
        if (!inRoom || host) _ownGuestStatus = "";
        if ((!inRoom || !host) && _own.Required) { ++_ownIdentifyTicket; _own.SetGame(null); _ownWasRequired = false; }
    }
}
