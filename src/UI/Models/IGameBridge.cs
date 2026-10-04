#nullable enable
using System;
using System.Collections.Generic;

namespace RocketRPG.Models;

public interface IGameBridge : IDisposable
{
    bool IsRunning { get; }
    double CurrentSpeed { get; }
    bool IsPaused { get; }
    bool IsNoclip { get; }
    bool EspEnabled { get; }
    bool TileInspectorEnabled { get; }
    GameState LatestState { get; }

    event Action<GameState>? GameStateUpdated;
    event Action<List<EspItem>>? EspDataUpdated;
    event Action<TileInfo>? TileInfoUpdated;
    event Action<List<SwitchItem>, List<VariableItem>>? DataInspectorUpdated;
    event Action<string>? NotificationReceived;
    event Action<KeyMessage>? HotkeyReceived;

    void SetVolume(int volumePercent);
    void SetSpeed(double speed);
    void TogglePause();
    void ToggleNoclip();
    void QuickSave();
    void QuickLoad();
    void ForceSaveMenu();
    void ForceLoadMenu();
    void EnableEsp(bool enable);
    void EnableTileInspector(bool enable);
    void RequestDataInspector();
    void SetSwitch(int id, bool value);
    void FreezeSwitch(int id, bool isFrozen, bool value);
    void SetVariable(int id, string value);
    void FreezeVariable(int id, bool isFrozen, string value);
    void Warp(int mapId, int x, int y);
    void Restart();
    void Stop();

    // ── 선택 기능: 엔진이 지원하지 않으면 기본 구현(무동작)을 사용합니다 ──

    /// <summary>대화창 표시 상태 변화 (Ren'Py 메시지 바 자동 표시/숨김, 대사 기록용)</summary>
    event Action<MessageState>? MessageStateChanged { add { } remove { } }

    /// <summary>선택지가 열림/닫힘 (멀티 선택지 투표)</summary>
    event Action<ChoiceState>? ChoiceChanged { add { } remove { } }

    /// <summary>게임 프로세스가 사용자 조작 없이 종료(튕김 포함)되었을 때</summary>
    event Action? ProcessExited { add { } remove { } }

    bool SupportsMessageDetection => false;

    void SetBrightness(double value) { }
    /// <summary>화면 필터(쉐이더). 게임 창을 직접 임베드하는 엔진은 엔진 자체 기능으로 대응합니다.</summary>
    void SetFilter(string filter) { }
    void SetInGameFont(string? family, int size, bool bold) { }
    void SetAutoMessage(bool enabled, double speed) { }
    void SetSkipMessage(bool enabled) { }
    void AdvanceMessage() { }
}

/// <summary>게임 선택지: Gen은 선택지가 열릴 때마다 바뀌는 번호, Open=false면 닫힘 (Picked = 고른 것, 모르면 -1)</summary>
public sealed class ChoiceState
{
    public int Gen { get; set; }
    public bool Open { get; set; }
    public int Picked { get; set; } = -1;
    public List<(string Text, bool Enabled)> Items { get; set; } = new();
}

public class MessageState
{
    public bool Busy { get; set; }
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
}

public class MapEventData
{
    [System.Text.Json.Serialization.JsonPropertyName("id")] public int Id { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("x")] public int X { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("y")] public int Y { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("pages")] public List<MapEventPage>? Pages { get; set; }
}

public class MapEventPage
{
    [System.Text.Json.Serialization.JsonPropertyName("trigger")] public int Trigger { get; set; }

    /// <summary>MV/MZ 원본 명령 목록 (JSON). 순간이동 명령을 찾는 데만 씁니다.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("list")] public List<System.Text.Json.JsonElement>? List { get; set; }

    /// <summary>이 페이지의 "장소 이동" 명령 (지정 맵 직접 이동만)</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<MapTransfer> Transfers { get; } = new();
}

/// <summary>이벤트의 장소 이동 목적지</summary>
public readonly record struct MapTransfer(int MapId, int X, int Y);

public static class MapTransferExtensions
{
    /// <summary>모든 페이지의 장소 이동 목적지(중복 제거). MV/MZ는 JSON 명령 목록(코드 201)에서 읽습니다.</summary>
    public static List<MapTransfer> AllTransfers(this MapEventData ev)
    {
        var result = new List<MapTransfer>();
        foreach (var page in ev.Pages ?? new())
        {
            if (page == null) continue;
            foreach (var t in page.Transfers) if (!result.Contains(t)) result.Add(t);
            if (page.List == null) continue;
            foreach (var cmd in page.List)
            {
                try
                {
                    if (cmd.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    if (!cmd.TryGetProperty("code", out var code) || code.GetInt32() != 201) continue;
                    if (!cmd.TryGetProperty("parameters", out var p) || p.GetArrayLength() < 4 || p[0].GetInt32() != 0) continue;
                    var t = new MapTransfer(p[1].GetInt32(), p[2].GetInt32(), p[3].GetInt32());
                    if (!result.Contains(t)) result.Add(t);
                }
                catch { }
            }
        }
        return result;
    }
}
