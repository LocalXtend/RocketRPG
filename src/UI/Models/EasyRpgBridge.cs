#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace RocketRPG.Models;

public class EasyRpgBridge : IGameBridge, IDisposable
{
    private readonly string _gameDir;
    private readonly int _engine;
    private IntPtr _gameHwnd;
    private Func<IntPtr>? _getGameHwnd;
    private int _processId;

    public IntPtr GameHwnd => _gameHwnd != IntPtr.Zero ? _gameHwnd : (_getGameHwnd?.Invoke() ?? IntPtr.Zero);
    public int ProcessId => _processId;

    public bool IsRunning { get; private set; }
    public double CurrentSpeed { get; private set; } = 1.0;
    public bool IsPaused { get; private set; }
    public bool IsNoclip { get; private set; }
    public bool EspEnabled { get; private set; }
    public bool TileInspectorEnabled { get; private set; }
    public GameState LatestState { get; private set; } = new();

    public int BaseWidth => 320;
    public int BaseHeight => 240;
    public int BaseFps => 60;
    public double TileDisplayScale => 16.0;

    public event Action<GameState>? GameStateUpdated;
    public event Action<List<EspItem>>? EspDataUpdated;
    public event Action<TileInfo>? TileInfoUpdated;
    public event Action<List<SwitchItem>, List<VariableItem>>? DataInspectorUpdated;
    public event Action<string>? NotificationReceived;
#pragma warning disable CS0067
    public event Action<KeyMessage>? HotkeyReceived;
#pragma warning restore CS0067

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<int, bool> _frozenSwitches = new();
    private readonly Dictionary<int, string> _frozenVariables = new();

    private readonly List<SwitchItem> _allSwitches = new();
    private readonly List<VariableItem> _allVariables = new();

    // 스위치/변수 ID는 1부터 연속이라 목록 위치로 바로 찾습니다 (수천 개를 매번 처음부터 찾으면 0.5초마다 랙이 생겼음).
    SwitchItem? FindSwitch(int id) => FindById(_allSwitches, id, s => s.Id);
    VariableItem? FindVariable(int id) => FindById(_allVariables, id, v => v.Id);

    /// <summary>스위치/변수 관리자가 한 번에 받아 오는 최대 개수 (큰 게임도 전부 보이도록)</summary>
    internal const int MaxInspectorItems = 5000;

    internal static T? FindById<T>(List<T> list, int id, Func<T, int> idOf) where T : class
    {
        if (id >= 1 && id <= list.Count && idOf(list[id - 1]) == id) return list[id - 1];
        if (list.Count == 0 || idOf(list[^1]) == list.Count) return null;   // 연속 목록인데 범위 밖
        return list.Find(x => idOf(x) == id);
    }

    public LcfMapData? CurrentMapData { get; private set; }
    public List<MapEventData> CurrentMapEvents => CurrentMapData?.Events ?? new();
    public int CurrentMapWidth => CurrentMapData?.Width ?? 20;
    public int CurrentMapHeight => CurrentMapData?.Height ?? 15;

    // RocketRPG 패치 EasyRPG Player와의 파이프. null이면 게임 내부 상태에 접근할 수 없음(비패치 Player).
    private readonly MkxpAgentChannel? _channel;
    public bool HasAgent => _channel != null;

    public EasyRpgBridge(string gameDir, int engine, Func<IntPtr>? getGameHwnd = null, int processId = 0, IntPtr gameHwnd = default,
                         MkxpAgentChannel? channel = null)
    {
        _gameDir = gameDir;
        _engine = engine;
        _getGameHwnd = getGameHwnd;
        _gameHwnd = gameHwnd;
        _processId = processId;
        _channel = channel;

        LoadDatabaseData();

        _pollTimer.Tick += OnPollTick;
        IsRunning = true;
        _pollTimer.Start();

        LatestState = new GameState
        {
            Scene = "Scene_Map",
            MapId = 1,
            PlayerX = 10,
            PlayerY = 10,
            DisplayX = 0,
            DisplayY = 0,
            Noclip = false
        };

        LoadMap(1);
    }

    public void UpdateGameHwnd(IntPtr hwnd, int pid = 0)
    {
        if (hwnd != IntPtr.Zero) _gameHwnd = hwnd;
        if (pid > 0) _processId = pid;
    }

    private void LoadDatabaseData()
    {
        try
        {
            string[] candidates = {
                Path.Combine(_gameDir, "RPG_RT.ldb"),
                Path.Combine(_gameDir, "Data", "RPG_RT.ldb"),
                Path.Combine(_gameDir, "data", "RPG_RT.ldb")
            };
            string? ldbPath = candidates.FirstOrDefault(File.Exists);

            if (ldbPath != null)
            {
                byte[] bytes = File.ReadAllBytes(ldbPath);
                var db = LcfReader.ParseDatabase(bytes);

                _allSwitches.Clear();
                foreach (var s in db.Switches) _allSwitches.Add(s);

                _allVariables.Clear();
                foreach (var v in db.Variables) _allVariables.Add(v);

                UiLog.Write($"EasyRpgBridge: loaded {_allSwitches.Count} switches, {_allVariables.Count} variables from RPG_RT.ldb");
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"EasyRpgBridge: LoadDatabaseData error {ex.Message}");
        }
    }

    public void LoadMap(int mapId)
    {
        try
        {
            string[] candidates = {
                Path.Combine(_gameDir, $"Map{mapId:D4}.lmu"),
                Path.Combine(_gameDir, $"Map{mapId:D3}.lmu"),
                Path.Combine(_gameDir, "Data", $"Map{mapId:D4}.lmu"),
                Path.Combine(_gameDir, "Data", $"Map{mapId:D3}.lmu"),
                Path.Combine(_gameDir, "data", $"Map{mapId:D4}.lmu"),
                Path.Combine(_gameDir, "data", $"Map{mapId:D3}.lmu")
            };
            string? mapFile = candidates.FirstOrDefault(File.Exists);

            if (mapFile != null)
            {
                byte[] bytes = File.ReadAllBytes(mapFile);
                CurrentMapData = LcfReader.ParseMapUnit(bytes);
                UiLog.Write($"EasyRpgBridge: loaded Map {mapId} ({CurrentMapData.Width}x{CurrentMapData.Height}, {CurrentMapData.Events.Count} events)");
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"EasyRpgBridge: LoadMap {mapId} error {ex.Message}");
        }
    }

    /// <summary>패치 Player의 텔레메트리 JSON</summary>
    // 텔레메트리 이름은 "MapId"처럼 대문자로 시작하고 GameState는 MV 브릿지에 맞춘 "mapId"라 대소문자를 가리지 않고 읽음
    // (가리면 맵 번호·카메라가 늘 0이었음)
    static readonly System.Text.Json.JsonSerializerOptions TelemetryJson = new() { PropertyNameCaseInsensitive = true };

    public void OnAgentTelemetry(string json)
    {
        try
        {
            var st = System.Text.Json.JsonSerializer.Deserialize<GameState>(json, TelemetryJson);
            if (st == null) return;
            if (st.MapId > 0 && st.MapId != LatestState.MapId) LoadMap(st.MapId);
            st.Noclip = IsNoclip = ResolveNoclip(st.Noclip);
            LatestState = st;
            GameStateUpdated?.Invoke(LatestState);
        }
        catch (Exception ex)
        {
            UiLog.Write($"EasyRpgBridge: telemetry parse error {ex.Message}");
        }
    }

    /// <summary>패치 Player의 스위치/변수 덤프: "0101..|1;20;3..."</summary>
    public void OnAgentData(string data)
    {
        var parts = data.Split('|', 2);
        if (parts.Length > 0)
        {
            string sw = parts[0];
            for (int i = 0; i < sw.Length; i++)
            {
                int id = i + 1;
                if (_frozenSwitches.ContainsKey(id)) continue;
                var s = FindSwitch(id);
                if (s != null) s.Value = sw[i] == '1';
            }
        }
        if (parts.Length > 1)
        {
            var va = parts[1].Split(';');
            for (int i = 0; i < va.Length; i++)
            {
                int id = i + 1;
                if (_frozenVariables.ContainsKey(id)) continue;
                var v = FindVariable(id);
                if (v != null) v.Value = va[i];
            }
        }
        DataInspectorUpdated?.Invoke(_allSwitches, _allVariables);
    }

    private int _agentPollCount;

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (!IsRunning) return;

        if (_channel != null)
        {
            // 고정(Freeze) 값은 게임 쪽에서 계속 덮어쓸 수 있으므로 주기적으로 다시 적용합니다.
            if (++_agentPollCount % 4 == 0)
            {
                foreach (var (id, v) in _frozenSwitches) _channel.Send("setsw", id, v);
                foreach (var (id, v) in _frozenVariables) _channel.Send("setvar", id, v);
            }
            return;
        }

        // Freeze enforcement
        bool inspectorDirty = false;
        foreach (var kvp in _frozenSwitches)
        {
            var sw = FindSwitch(kvp.Key);
            if (sw != null && (sw.Value != kvp.Value || !sw.IsFrozen))
            {
                sw.Value = kvp.Value;
                sw.IsFrozen = true;
                inspectorDirty = true;
            }
        }
        foreach (var kvp in _frozenVariables)
        {
            var va = FindVariable(kvp.Key);
            if (va != null && (va.Value != kvp.Value || !va.IsFrozen))
            {
                va.Value = kvp.Value;
                va.IsFrozen = true;
                inspectorDirty = true;
            }
        }

        if (inspectorDirty)
        {
            DataInspectorUpdated?.Invoke(_allSwitches, _allVariables);
        }

        // 에이전트가 없으면 게임의 실제 화면 위치를 알 수 없으므로 ESP를 그리지 않습니다
        // (예전에는 고정 좌표로 맵 1의 이벤트를 그려 엉뚱한 위치에 표시되었음).
    }

    public void UpdateVirtualMouse(double screenX, double screenY)
    {
        if (_channel != null)
        {
            // 패치 Player가 실제 표시 좌표/통행 판정으로 계산해 "I" 줄로 돌려줍니다.
            if (TileInspectorEnabled) _channel.Send("mouse", screenX, screenY);
            return;
        }
        if (!TileInspectorEnabled || CurrentMapData == null) return;

        // 가상 320x240 좌표를 맵 타일 좌표로 변환
        int tileX = (int)((LatestState.DisplayX + screenX) / 16.0);
        int tileY = (int)((LatestState.DisplayY + screenY) / 16.0);

        if (tileX >= 0 && tileX < CurrentMapWidth && tileY >= 0 && tileY < CurrentMapHeight)
        {
            int lowerTile = CurrentMapData.GetTile(0, tileX, tileY);
            int upperTile = CurrentMapData.GetTile(1, tileX, tileY);

            var evs = CurrentMapData.Events
                .Where(e => e.X == tileX && e.Y == tileY)
                .Select(e => string.IsNullOrEmpty(e.Name) ? $"EV{e.Id}" : e.Name)
                .ToList();

            int topTile = CurrentMapData.GetTopTile(tileX, tileY);
            bool passable = (topTile < 5000);
            if (evs.Count > 0) passable = false;

            var info = new TileInfo
            {
                MapX = tileX,
                MapY = tileY,
                Passable = passable,
                TileIds = new List<int> { lowerTile, upperTile },
                Events = string.Join(", ", evs),
                ScreenX = screenX,
                ScreenY = screenY
            };

            TileInfoUpdated?.Invoke(info);
        }
    }

    // 볼륨은 이 브리지를 쓰는 렌더러가 프로세스별로 적용합니다.
    public void SetVolume(int volumePercent) { }

    public void SetSpeed(double speed)
    {
        CurrentSpeed = Math.Clamp(speed, 0.5, 8.0);
        _channel?.Send("speed", CurrentSpeed);
    }

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        _channel?.Send("pause", IsPaused);
    }

    // 켜고 끈 직후에는 이미 전송 중이던 이전 텔레메트리가 도착해 상태를 되돌리므로, 게임이 새 값을 보고할 때까지 유지합니다.
    long _noclipPendingUntil;

    public void ToggleNoclip()
    {
        IsNoclip = !IsNoclip;
        LatestState.Noclip = IsNoclip;
        _noclipPendingUntil = Environment.TickCount64 + 1500;
        _channel?.Send("noclip", IsNoclip);
        GameStateUpdated?.Invoke(LatestState);
    }

    bool ResolveNoclip(bool reported)
    {
        if (reported == IsNoclip) _noclipPendingUntil = 0;
        else if (Environment.TickCount64 < _noclipPendingUntil) return IsNoclip;
        return reported;
    }

    public void Warp(int mapId, int x, int y)
    {
        if (_channel == null)
        {
            NotificationReceived?.Invoke("이 실행 방식에서는 워프를 지원하지 않습니다.");
            return;
        }
        _channel.Send("warp", mapId, x, y);
        NotificationReceived?.Invoke($"맵 {mapId} ({x}, {y}) 워프");
    }

    public void QuickSave()
    {
        if (_channel != null) _channel.Send("qsave");
        else NotificationReceived?.Invoke("이 실행 방식에서는 퀵 세이브를 지원하지 않습니다.");
    }

    public void QuickLoad()
    {
        if (_channel != null) _channel.Send("qload");
        else NotificationReceived?.Invoke("이 실행 방식에서는 퀵 로드를 지원하지 않습니다.");
    }

    public void ForceSaveMenu()
    {
        NotificationReceived?.Invoke("세이브 화면이 호출되었습니다.");
    }

    public void ForceLoadMenu()
    {
        NotificationReceived?.Invoke("로드 화면이 호출되었습니다.");
    }

    public void EnableEsp(bool enable)
    {
        EspEnabled = enable;
        _channel?.Send("esp", enable);
        if (!enable) EspDataUpdated?.Invoke(new List<EspItem>());
    }

    public void EnableTileInspector(bool enable)
    {
        TileInspectorEnabled = enable;
        if (!enable) _channel?.Send("mouse", "", "");
    }

    public void RequestDataInspector()
    {
        if (_channel != null)
        {
            int maxSw = Math.Min(MaxInspectorItems, _allSwitches.Count > 0 ? _allSwitches.Max(s => s.Id) : 100);
            int maxVa = Math.Min(MaxInspectorItems, _allVariables.Count > 0 ? _allVariables.Max(v => v.Id) : 100);
            _channel.Send("dump", maxSw, maxVa);
        }
        DataInspectorUpdated?.Invoke(_allSwitches, _allVariables);
    }

    public void SetSwitch(int id, bool value)
    {
        _channel?.Send("setsw", id, value);
        var sw = FindSwitch(id);
        if (sw != null)
        {
            sw.Value = value;
            if (_frozenSwitches.ContainsKey(id))
            {
                _frozenSwitches[id] = value;
            }
            DataInspectorUpdated?.Invoke(_allSwitches, _allVariables);
        }
    }

    public void FreezeSwitch(int id, bool isFrozen, bool value)
    {
        var sw = FindSwitch(id);
        if (sw != null)
        {
            sw.IsFrozen = isFrozen;
            sw.Value = value;
            if (isFrozen) _frozenSwitches[id] = value;
            else _frozenSwitches.Remove(id);
            DataInspectorUpdated?.Invoke(_allSwitches, _allVariables);
        }
    }

    public void SetVariable(int id, string value)
    {
        if (int.TryParse(value, out int iv)) _channel?.Send("setvar", id, iv);
        var va = FindVariable(id);
        if (va != null)
        {
            va.Value = value;
            if (_frozenVariables.ContainsKey(id))
            {
                _frozenVariables[id] = value;
            }
            DataInspectorUpdated?.Invoke(_allSwitches, _allVariables);
        }
    }

    public void FreezeVariable(int id, bool isFrozen, string value)
    {
        var va = FindVariable(id);
        if (va != null)
        {
            va.IsFrozen = isFrozen;
            va.Value = value;
            if (isFrozen) _frozenVariables[id] = value;
            else _frozenVariables.Remove(id);
            DataInspectorUpdated?.Invoke(_allSwitches, _allVariables);
        }
    }

    public void Restart()
    {
        LatestState.PlayerX = 10;
        LatestState.PlayerY = 10;
        GameStateUpdated?.Invoke(LatestState);
    }

    public void Stop()
    {
        _pollTimer.Stop();
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    }
}
