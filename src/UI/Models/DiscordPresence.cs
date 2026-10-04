#nullable enable
using System;
using System.Text;
using DiscordRPC;

namespace RocketRPG.Models;

/// <summary>
/// 디스코드 프로필에 "RocketRPG 하는 중"과 지금 플레이 중인 게임을 보여 줍니다 (Rich Presence).
/// 디스코드 앱과 로컬 파이프로만 통신하며, 디스코드가 꺼져 있으면 조용히 기다렸다가 켜지면 연결됩니다.
/// 표시 이름·아이콘은 Discord Developer Portal의 "RocketRPG" 앱(아래 ID, 이미지 키 rocketrpg)에서 정합니다.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    public const string ApplicationId = "1553289094992240711";
    const string LargeImageKey = "rocketrpg";

    const string ReleaseUrl = "https://github.com/LocalXtend/RocketRPG-Release/releases/latest";

    DiscordRpcClient? _client;
    string? _game;          // 표시 중인 게임 (null = 게임 고르는 중)
    string? _engine;        // 엔진 이름 (예: "RPG Maker 2003")
    DateTime _startedUtc;
    bool _streaming;

    (int count, int max)? _multi;

    /// <summary>멀티 방 인원 (방에 없으면 null) — 상태 줄에 '멀티 (2/4)'</summary>
    public (int count, int max)? Multi
    {
        get => _multi;
        set { if (_multi == value) return; _multi = value; Push(); }
    }

    string? _joinUrl;

    /// <summary>멀티 방 '참가' 단추 주소 (없으면 단추 없음)</summary>
    public string? JoinUrl
    {
        get => _joinUrl;
        set { if (_joinUrl == value) return; _joinUrl = value; Push(); }
    }

    /// <summary>스트리머 모드 (상태 줄에 '방송 중' 표시)</summary>
    public bool Streaming
    {
        get => _streaming;
        set { if (_streaming == value) return; _streaming = value; Push(); }
    }

    public bool Enabled { get; private set; }

    public void SetEnabled(bool enabled)
    {
        if (enabled == Enabled) return;
        Enabled = enabled;
        if (enabled)
        {
            try
            {
                _client = new DiscordRpcClient(ApplicationId);
                _client.OnError += (_, e) => UiLog.Write($"DiscordPresence: {e.Code} {e.Message}");
                // 연결되기 전에 보낸 표시는 버려질 수 있어, 연결되면 지금 상태를 다시 보냅니다.
                _client.OnReady += (_, _) => { UiLog.Write("DiscordPresence: connected to Discord"); Push(); };
                _client.OnPresenceUpdate += (_, e) => UiLog.Write($"DiscordPresence: shown '{e.Presence?.Details}'");
                _client.Initialize();
                Push();
            }
            catch (Exception ex)
            {
                UiLog.Write($"DiscordPresence: init failed {ex.Message}");
                _client = null;
            }
        }
        else
        {
            Close();
        }
    }

    /// <summary>게임을 시작했을 때 (첫 줄 = 게임 제목, 둘째 줄 = 엔진, 플레이 시간 표시).</summary>
    public void ShowGame(string title, string? engine = null)
    {
        string game = string.IsNullOrWhiteSpace(title) ? "이름 없는 게임" : title.Trim();
        // 같은 게임을 다시 시작(재시작/설정 적용)해도 플레이 시간은 이어서
        if (game != _game) _startedUtc = DateTime.UtcNow;
        _game = game;
        _engine = engine;
        Push();
    }

    /// <summary>게임을 끄거나 아직 고르지 않았을 때.</summary>
    public void ShowIdle()
    {
        _game = null;
        _engine = null;
        Push();
    }

    /// <summary>둘째 줄: 엔진 · 방송 중</summary>
    public static string? StateLine(string? game, string? engine, bool streaming, (int count, int max)? multi = null)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (game != null && !string.IsNullOrWhiteSpace(engine)) parts.Add(engine!);
        if (multi is { } m && !streaming) parts.Add($"멀티 ({m.count}/{m.max})");
        if (streaming) parts.Add("방송 중");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    void Push()
    {
        if (!Enabled || _client == null || _client.IsDisposed) return;
        try
        {
            string? state = StateLine(_game, _engine, _streaming, _multi);
            var p = new RichPresence
            {
                Details = Fit(_game ?? "게임 고르는 중"),
                State = state == null ? null : Fit(state),
                Assets = new Assets { LargeImageKey = LargeImageKey, LargeImageText = $"RocketRPG {UpdateService.CurrentVersion}" },
                // 멀티 방에 있으면 '참가' 단추 (스트리머 모드에서는 JoinUrl이 없음)
                Buttons = _joinUrl != null && !_streaming
                    ? [new Button { Label = "참가", Url = _joinUrl }, new Button { Label = "RocketRPG 받기", Url = ReleaseUrl }]
                    : [new Button { Label = "RocketRPG 받기", Url = ReleaseUrl }]
            };
            if (_game != null) p.Timestamps = new Timestamps(_startedUtc);
            _client.SetPresence(p);
            UiLog.Write($"DiscordPresence: set '{p.Details}' (connected={_client.IsInitialized && _client.CurrentUser != null})");
        }
        catch (Exception ex)
        {
            UiLog.Write($"DiscordPresence: set failed {ex.Message}");
        }
    }

    /// <summary>디스코드는 한 줄을 128바이트(UTF-8)까지만 받습니다. 넘치면 글자 단위로 줄이고 '…'을 붙입니다.</summary>
    public static string Fit(string s)
    {
        const int max = 128;
        if (Encoding.UTF8.GetByteCount(s) <= max) return s.Length < 2 ? s + "  " : s;   // 2자 미만도 거부하므로 채움
        var sb = new StringBuilder();
        foreach (var rune in s.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(sb.ToString() + rune.ToString()) + 3 > max) break;
            sb.Append(rune.ToString());
        }
        return sb.Append('…').ToString();
    }

    void Close()
    {
        try
        {
            _client?.ClearPresence();
            _client?.Dispose();
        }
        catch { }
        _client = null;
    }

    public void Dispose()
    {
        Enabled = false;
        Close();
    }
}
