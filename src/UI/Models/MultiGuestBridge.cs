#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace RocketRPG.Models;

// 참가자의 변수 관리자에서 로컬 게임 파일 없이 같은 창을 사용합니다.
public sealed class MultiGuestBridge(Action<JsonObject> send) : IGameBridge
{
    public bool IsRunning => true;
    public double CurrentSpeed => 1;
    public bool IsPaused => false;
    public bool IsNoclip => false;
    public bool EspEnabled => false;
    public bool TileInspectorEnabled => false;
    public GameState LatestState { get; private set; } = new();
    public event Action<GameState>? GameStateUpdated;
    public event Action<List<SwitchItem>, List<VariableItem>>? DataInspectorUpdated;
    public event Action<List<EspItem>>? EspDataUpdated { add { } remove { } }
    public event Action<TileInfo>? TileInfoUpdated { add { } remove { } }
    public event Action<string>? NotificationReceived { add { } remove { } }
    public event Action<KeyMessage>? HotkeyReceived { add { } remove { } }
    public void ReceiveState(GameState state) { LatestState = state; GameStateUpdated?.Invoke(state); }
    public void ReceiveData(List<SwitchItem> s, List<VariableItem> v) => DataInspectorUpdated?.Invoke(s, v);
    public void RequestDataInspector() => send(new JsonObject { ["op"] = "inspect" });
    void Edit(string kind, int id, bool on, string value = "") => send(new JsonObject { ["op"] = "editData", ["kind"] = kind, ["id"] = id, ["on"] = on, ["value"] = value });
    public void SetSwitch(int id, bool value) => Edit("switch", id, value);
    public void FreezeSwitch(int id, bool frozen, bool value) => Edit("freezeSwitch", id, frozen, value ? "true" : "false");
    public void SetVariable(int id, string value) => Edit("variable", id, false, value);
    public void FreezeVariable(int id, bool frozen, string value) => Edit("freezeVariable", id, frozen, value);
    public void Warp(int mapId, int x, int y) { }   // 맵 정보는 멀티로 주고받지 않습니다
    public void Dispose() { }
    public void SetVolume(int value) { }
    public void SetSpeed(double value) { }
    public void TogglePause() { }
    public void ToggleNoclip() { }
    public void QuickSave() { }
    public void QuickLoad() { }
    public void ForceSaveMenu() { }
    public void ForceLoadMenu() { }
    public void EnableEsp(bool value) { }
    public void EnableTileInspector(bool value) { }
    public void Restart() { }
    public void Stop() { }
}
