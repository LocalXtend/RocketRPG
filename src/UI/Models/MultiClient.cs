#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RocketRPG.Models;

/// <summary>공개 방 목록의 한 줄</summary>
public sealed record MultiRoomSummary(string Id, string Code, string Title, bool Locked, bool Listed, int Count, int Max, string Game, bool HostAway);

public sealed record MultiMember(string Id, string Name, bool Host, bool Muted, int Color = 0);

public sealed class MultiRoomSettings
{
    public string Title { get; set; } = "";
    public int Max { get; set; } = 4;
    public bool Listed { get; set; } = true;
    public bool Control { get; set; }
    public bool NotesEditable { get; set; } = true;
    public int Fps { get; set; } = 30;
    public string Quality { get; set; } = "normal";
    public bool Streamer { get; set; }
}

public sealed class MultiRoomState
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public bool Locked { get; set; }
    public MultiRoomSettings Settings { get; set; } = new();
    public string Game { get; set; } = "";
    public string HostId { get; set; } = "";
    public bool HostAway { get; set; }
}

/// <summary>
/// 멀티 서버(Cloudflare Workers)와의 연결. 방 목록·만들기는 HTTP, 방 안은 WebSocket(JSON 한 줄씩).
/// 이벤트는 만든 스레드(UI)에서 불립니다. 연결이 갑자기 끊기면 1분 동안 같은 자격(방장 키/비밀번호)으로 다시 붙습니다.
/// </summary>
public sealed class MultiClient : IDisposable
{
    public const int Protocol = 1;
    const string DefaultServer = "https://rocketrpg-multi.rocketrpg-multi.workers.dev";

    /// <summary>서버 주소 (RR_MULTI_SERVER로 바꿀 수 있음, 예: 로컬 시험용 http://127.0.0.1:8787)</summary>
    public static string ServerUrl => (Environment.GetEnvironmentVariable("RR_MULTI_SERVER") is { Length: > 0 } s ? s : DefaultServer).TrimEnd('/');

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    ClientWebSocket? _ws;
    CancellationTokenSource? _cts;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    string _roomId = "", _name = "", _clientId = "", _password = "", _hostKey = "";
    bool _leaving;
    TaskCompletionSource<string?>? _joinWaiter;

    public bool InRoom { get; private set; }
    public bool IsHost { get; private set; }
    public bool Reconnecting { get; private set; }
    public string MyId { get; private set; } = "";
    public MultiRoomState Room { get; private set; } = new();
    public IReadOnlyList<MultiMember> Members { get; private set; } = [];
    public string HostKey => _hostKey;
    /// <summary>WebRTC 연결용 STUN/TURN 목록 (입장할 때 서버가 줌, JSON 배열)</summary>
    public string IceServersJson { get; private set; } = "[]";
    public string RoomId => _roomId;
    /// <summary>고정 채팅을 다시 보낼 수 있는 때 (Environment.TickCount64, 입장할 때 서버가 알려 줌)</summary>
    public long FixedChatReadyAt { get; set; }

    /// <summary>방 정보·인원·방장·연결 상태가 바뀜</summary>
    public event Action? StateChanged;
    public event Action<string, string, string, int, bool>? ChatReceived;
    public event Action<JsonObject>? PingReceived;
    public event Action<string, JsonNode?>? SignalReceived;          // (보낸 사람 id, 데이터)
    public event Action<string, string, JsonNode?>? RelayReceived;   // (채널, 보낸 사람 id, 데이터)
    public event Action<string>? Closed;                             // 방에서 나오게 됨 (이유 설명)
    public event Action<string, string>? ErrorReceived;             // (코드, 설명)

    // ── HTTP ──

    public static async Task<List<MultiRoomSummary>> ListRoomsAsync()
    {
        var json = await Http.GetStringAsync($"{ServerUrl}/api/rooms");
        var node = JsonNode.Parse(json)?["rooms"]?.AsArray();
        return node == null ? [] : node.Select(n => n.Deserialize<MultiRoomSummary>(JsonOpts)!).Where(r => r != null).ToList();
    }

    public static async Task<(string id, string code, string hostKey)> CreateRoomAsync(string title, string password, int max, bool listed)
    {
        var body = new StringContent(JsonSerializer.Serialize(new { title, password, max, listed, proto = Protocol }), Encoding.UTF8, "application/json");
        using var res = await Http.PostAsync($"{ServerUrl}/api/rooms", body);
        var node = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(node?["message"]?.GetValue<string>() ?? "방을 만들지 못했습니다.");
        return (node!["id"]!.GetValue<string>(), node["code"]!.GetValue<string>(), node["hostKey"]!.GetValue<string>());
    }

    /// <summary>방 코드로 방 찾기 (없으면 null)</summary>
    public static async Task<MultiRoomSummary?> FindByCodeAsync(string code)
    {
        using var res = await Http.GetAsync($"{ServerUrl}/api/code/{Uri.EscapeDataString(code.Trim().ToUpperInvariant())}");
        if (!res.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())?["room"]?.Deserialize<MultiRoomSummary>(JsonOpts);
    }

    public static string NewDefaultName()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return "user_" + new string(Enumerable.Range(0, 12).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
    }

    // ── 방 ──

    /// <summary>방에 들어갑니다. 성공하면 null, 아니면 이유.</summary>
    public async Task<string?> JoinAsync(string roomId, string name, string clientId, string password = "", string hostKey = "")
    {
        await LeaveAsync();
        _roomId = roomId; _name = name; _clientId = clientId; _password = password; _hostKey = hostKey;
        _leaving = false;
        string? err = await ConnectOnceAsync();
        if (err != null) { InRoom = false; Raise(() => StateChanged?.Invoke()); }
        return err;
    }

    async Task<string?> ConnectOnceAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _ws = ws;
        _joinWaiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string wsUrl = ServerUrl.Replace("https://", "wss://").Replace("http://", "ws://") + $"/api/rooms/{_roomId}/ws";
            await ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
        }
        catch (Exception ex)
        {
            UiLog.Write($"multi: connect failed {ex.Message}");
            return "서버에 연결하지 못했습니다. 인터넷 연결을 확인해 주세요.";
        }
        _ = ReceiveLoopAsync(ws, _cts.Token);
        await SendAsync(new { t = "hello", proto = Protocol, name = _name, clientId = _clientId, password = _password, hostKey = _hostKey });
        var done = await Task.WhenAny(_joinWaiter.Task, Task.Delay(10000));
        if (done != _joinWaiter.Task) return "서버 응답이 없습니다.";
        return await _joinWaiter.Task;
    }

    async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                if (!r.EndOfMessage) continue;
                string text = sb.ToString();
                sb.Clear();
                JsonObject? msg;
                try { msg = JsonNode.Parse(text) as JsonObject; } catch { continue; }
                if (msg != null) Raise(() => Handle(msg));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        catch (Exception ex) { UiLog.Write($"multi: receive error {ex.Message}"); }
        if (ReferenceEquals(ws, _ws)) Raise(() => OnDisconnected());
    }

    void Handle(JsonObject msg)
    {
        string t = msg["t"]?.GetValue<string>() ?? "";
        switch (t)
        {
            case "welcome":
                MyId = msg["you"]?["id"]?.GetValue<string>() ?? "";
                IsHost = msg["you"]?["host"]?.GetValue<bool>() == true;
                IceServersJson = msg["ice"]?.ToJsonString() ?? "[]";
                FixedChatReadyAt = Environment.TickCount64 + (msg["fixedWait"] is JsonValue fw && fw.TryGetValue<long>(out long wait) ? Math.Clamp(wait, 0, 90_000) : 0);
                ApplyRoom(msg["room"]);
                ApplyMembers(msg["members"]);
                InRoom = true;
                Reconnecting = false;
                _joinWaiter?.TrySetResult(null);
                StateChanged?.Invoke();
                break;
            case "members": ApplyMembers(msg["members"]); StateChanged?.Invoke(); break;
            case "room": ApplyRoom(msg["room"]); StateChanged?.Invoke(); break;
            case "host_away": Room.HostAway = true; StateChanged?.Invoke(); break;
            case "host_back": Room.HostAway = false; StateChanged?.Invoke(); break;
            case "host_key":
                _hostKey = msg["hostKey"]?.GetValue<string>() ?? "";
                IsHost = true;
                StateChanged?.Invoke();
                break;
            case "chat":
                ChatReceived?.Invoke(msg["from"]?.GetValue<string>() ?? "", msg["name"]?.GetValue<string>() ?? "", msg["text"]?.GetValue<string>() ?? "",
                    msg["color"]?.GetValue<int>() ?? 0, msg["fixed"]?.GetValue<bool>() == true);
                break;
            case "ping": PingReceived?.Invoke(msg); break;
            case "signal": SignalReceived?.Invoke(msg["from"]?.GetValue<string>() ?? "", msg["data"]); break;
            case "relay": RelayReceived?.Invoke(msg["ch"]?.GetValue<string>() ?? "", msg["from"]?.GetValue<string>() ?? "", msg["data"]); break;
            case "kicked": EndRoom("방장이 방에서 내보냈습니다."); break;
            case "closed":
                EndRoom(msg["reason"]?.GetValue<string>() == "host_left" ? "방장이 돌아오지 않아 방이 없어졌습니다." : "방이 해체되었습니다.");
                break;
            case "error":
            {
                string code = msg["code"]?.GetValue<string>() ?? "";
                string text = msg["message"]?.GetValue<string>() ?? "오류가 발생했습니다.";
                if (_joinWaiter is { Task.IsCompleted: false }) { _leaving = true; _joinWaiter.TrySetResult(text); }
                else ErrorReceived?.Invoke(code, text);
                break;
            }
        }
    }

    void ApplyRoom(JsonNode? room)
    {
        if (room == null) return;
        Room = new MultiRoomState
        {
            Id = room["id"]?.GetValue<string>() ?? "",
            Code = room["code"]?.GetValue<string>() ?? "",
            Locked = room["locked"]?.GetValue<bool>() == true,
            Settings = room["settings"]?.Deserialize<MultiRoomSettings>(JsonOpts) ?? new(),
            Game = room["game"]?.GetValue<string>() ?? "",
            HostId = room["hostId"]?.GetValue<string>() ?? "",
            HostAway = room["hostAway"]?.GetValue<bool>() == true,
        };
    }

    void ApplyMembers(JsonNode? members)
    {
        if (members is not JsonArray arr) return;
        Members = arr.Select(m => new MultiMember(
            m?["id"]?.GetValue<string>() ?? "", m?["name"]?.GetValue<string>() ?? "",
            m?["host"]?.GetValue<bool>() == true, m?["muted"]?.GetValue<bool>() == true,
            m?["color"] is JsonValue c && c.TryGetValue<int>(out int color) ? Math.Clamp(color, 0, 7) : 0)).ToList();
        IsHost = Members.FirstOrDefault(m => m.Id == MyId)?.Host ?? IsHost;
    }

    /// <summary>연결이 끊김: 스스로 나간 게 아니면 1분 동안 다시 붙어 봅니다.</summary>
    async void OnDisconnected()
    {
        if (_leaving || !InRoom) return;
        Reconnecting = true;
        StateChanged?.Invoke();
        UiLog.Write("multi: connection lost, reconnecting");
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!_leaving && DateTime.UtcNow < until)
        {
            await Task.Delay(3000);
            if (_leaving) return;
            string? err = await ConnectOnceAsync();
            if (err == null) { UiLog.Write("multi: reconnected"); return; }
            if (_leaving) { EndRoom(err); return; }   // 입장 거절 (방 없음·가득 참 등)
        }
        if (!_leaving) EndRoom("연결이 끊겨 방에서 나왔습니다.");
    }

    void EndRoom(string reason)
    {
        _leaving = true;
        bool was = InRoom;
        InRoom = false;
        IsHost = false;
        Reconnecting = false;
        Members = [];
        _cts?.Cancel();
        try { _ws?.Abort(); } catch { }
        _ws = null;
        StateChanged?.Invoke();
        if (was) Closed?.Invoke(reason);
    }

    // ── 보내기 ──

    public void Send(object message) => _ = SendAsync(message);

    async Task SendAsync(object message)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception ex) { UiLog.Write($"multi: send failed {ex.Message}"); }
        finally { _sendLock.Release(); }
    }

    public void Rename(string name) { _name = name; Send(new { t = "rename", name }); }

    /// <summary>나가기 (방장이면 해체)</summary>
    public async Task LeaveAsync()
    {
        if (_ws == null) return;
        _leaving = true;
        if (InRoom) await SendAsync(IsHost ? new { t = "dissolve" } : new { t = "leave" });
        var ws = _ws;
        _ws = null;
        InRoom = false;
        IsHost = false;
        Members = [];
        try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", new CancellationTokenSource(2000).Token); }
        catch { }
        _cts?.Cancel();
        StateChanged?.Invoke();
    }

    void Raise(Action a)
    {
        if (_ui == null) a();
        else _ui.Post(_ => a(), null);
    }

    public void Dispose()
    {
        _leaving = true;
        _cts?.Cancel();
        try { _ws?.Abort(); } catch { }
    }
}
