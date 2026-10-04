#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace RocketRPG.Models;

public class RubyBridge : IGameBridge, IDisposable
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

    public int BaseWidth => (_engine == CoreInterop.EngineXp) ? 640 : 544;
    public int BaseHeight => (_engine == CoreInterop.EngineXp) ? 480 : 416;
    public int BaseFps => (_engine == CoreInterop.EngineXp) ? 40 : 60;
    public double TileDisplayScale => (_engine == CoreInterop.EngineXp) ? 128.0 : 256.0;

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

    const int MaxInspectorItems = EasyRpgBridge.MaxInspectorItems;
    SwitchItem? FindSwitch(int id) => EasyRpgBridge.FindById(_allSwitches, id, s => s.Id);
    VariableItem? FindVariable(int id) => EasyRpgBridge.FindById(_allVariables, id, v => v.Id);

    // 로드된 현재 맵 캐시 (타일 인스펙터 및 ESP용)
    public RubyTable? CurrentMapTable { get; private set; }
    public List<MapEventData> CurrentMapEvents { get; private set; } = new();
    public int CurrentMapWidth { get; private set; }
    public int CurrentMapHeight { get; private set; }

    // mkxp-z 네이티브: 루비 에이전트 파이프. 모든 명령과 텔레메트리가 이 파이프로 오갑니다.
    private readonly MkxpAgentChannel? _channel;

    public RubyBridge(string gameDir, int engine, Func<IntPtr>? getGameHwnd = null, int processId = 0, IntPtr gameHwnd = default,
                      MkxpAgentChannel? channel = null)
    {
        _gameDir = gameDir;
        _engine = engine;
        _getGameHwnd = getGameHwnd;
        _gameHwnd = gameHwnd;
        _processId = processId;
        _channel = channel;

        // 시스템 데이터(스위치/변수 이름) 미리 로드
        LoadSystemData();

        _pollTimer.Tick += OnPollTick;
        IsRunning = true;
        _pollTimer.Start();

        // 초기 상태 설정
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

        // 초기 맵 로드 시도
        LoadMap(1);
    }

    /// <summary>에이전트가 보낸 텔레메트리 JSON </summary>
    // 텔레메트리 이름은 "MapId"처럼 대문자로 시작하고 GameState는 MV 브릿지에 맞춘 "mapId"라 대소문자를 가리지 않고 읽음
    // (가리면 맵 번호·카메라가 늘 0이었음)
    static readonly System.Text.Json.JsonSerializerOptions TelemetryJson = new() { PropertyNameCaseInsensitive = true };

    public void OnAgentTelemetry(string json)
    {
        try
        {
            var st = JsonSerializer.Deserialize<GameState>(json, TelemetryJson);
            if (st == null) return;
            if (st.MapId > 0 && st.MapId != LatestState.MapId) LoadMap(st.MapId);
            st.Noclip = IsNoclip = ResolveNoclip(st.Noclip);
            LatestState = st;
            GameStateUpdated?.Invoke(LatestState);
        }
        catch (Exception ex)
        {
            UiLog.Write($"RubyBridge: telemetry parse error {ex.Message}");
        }
    }

    public void OnAgentData(string data) => ParseLiveInspectorData(data);

    public void UpdateGameHwnd(IntPtr hwnd, int processId = 0)
    {
        if (hwnd != IntPtr.Zero) _gameHwnd = hwnd;
        if (processId > 0) _processId = processId;
    }

    private void LoadSystemData()
    {
        byte[]? sysBytes = TryReadGameFile("Data\\System.rvdata2") ??
                           TryReadGameFile("Data\\System.rvdata") ??
                           TryReadGameFile("Data\\System.rxdata") ??
                           TryReadGameFile("Copy of Data\\System.rxdata");

        if (sysBytes != null)
        {
            var (sw, va) = RubyMarshalReader.ParseSystem(sysBytes);
            _allSwitches.AddRange(sw);
            _allVariables.AddRange(va);
        }
    }

    public void LoadMap(int mapId)
    {
        string[] candidates = [
            $"Data\\Map{mapId:D3}.rvdata2",
            $"Data\\Map{mapId:D3}.rvdata",
            $"Data\\Map{mapId:D3}.rxdata",
            $"Copy of Data\\Map{mapId:D3}.rxdata"
        ];

        byte[]? mapBytes = null;
        foreach (var c in candidates)
        {
            mapBytes = TryReadGameFile(c);
            if (mapBytes != null) break;
        }

        if (mapBytes != null)
        {
            var (w, h, _, tileData, evs) = RubyMarshalReader.ParseMap(mapBytes);
            CurrentMapWidth = w;
            CurrentMapHeight = h;
            CurrentMapEvents = evs;

            if (tileData != null)
            {
                int layers = (w > 0 && h > 0) ? (tileData.Length / (w * h)) : 3;
                CurrentMapTable = new RubyTable
                {
                    Dimensions = 3,
                    XSize = w,
                    YSize = h,
                    ZSize = Math.Max(1, layers),
                    TotalElements = tileData.Length,
                    Tiles = tileData
                };
            }
        }
    }

    private byte[]? TryReadGameFile(string relativePath)
    {
        string full = Path.Combine(_gameDir, relativePath);
        if (File.Exists(full))
        {
            try { return File.ReadAllBytes(full); } catch { }
        }

        // RGSS 아카이브 확인
        using var archive = RgssArchiveReader.TryOpen(_gameDir);
        if (archive != null && archive.FileExists(relativePath))
        {
            return archive.ReadFile(relativePath);
        }

        return null;
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

    public void QuickSave()
    {
        // XP vs VX vs Ace 빠른 저장 (슬롯 1)
        string script = (_engine == CoreInterop.EngineXp)
            ? "File.open('Save1.rxdata', 'wb') { |f| Marshal.dump($game_system, f); Marshal.dump($game_actors, f); Marshal.dump($game_party, f); Marshal.dump($game_map, f); Marshal.dump($game_player, f) }"
            : (_engine == CoreInterop.EngineVx)
            ? "$game_system.save_disabled = false; SceneManager.call(Scene_Save) rescue ($scene = Scene_Save.new)"
            : "$game_system.save_disabled = false; SceneManager.call(Scene_Save)";

        SendCommand(1, 0, 0, 0, 0, script);
        NotificationReceived?.Invoke("빠른 저장을 수행했습니다.");
    }

    public void QuickLoad()
    {
        string script = (_engine == CoreInterop.EngineXp)
            ? "$scene = Scene_Load.new"
            : (_engine == CoreInterop.EngineVx)
            ? "$scene = Scene_Load.new"
            : "SceneManager.call(Scene_Load)";

        SendCommand(1, 0, 0, 0, 0, script);
        NotificationReceived?.Invoke("불러오기 화면을 호출했습니다.");
    }

    public void ForceSaveMenu()
    {
        string script = (_engine == CoreInterop.EngineXp)
            ? "$scene = Scene_Save.new"
            : (_engine == CoreInterop.EngineVx)
            ? "$game_system.save_disabled = false; $scene = Scene_Save.new"
            : "$game_system.save_disabled = false; SceneManager.call(Scene_Save)";

        SendCommand(6, 0, 0, 0, 0, script);
        NotificationReceived?.Invoke("강제 세이브 메뉴를 호출했습니다.");
    }

    public void ForceLoadMenu()
    {
        string script = (_engine == CoreInterop.EngineXp)
            ? "$scene = Scene_Load.new"
            : (_engine == CoreInterop.EngineVx)
            ? "$scene = Scene_Load.new"
            : "SceneManager.call(Scene_Load)";

        SendCommand(7, 0, 0, 0, 0, script);
        NotificationReceived?.Invoke("강제 로드 메뉴를 호출했습니다.");
    }

    public void EnableEsp(bool enable)
    {
        EspEnabled = enable;
        if (enable) UpdateEsp();
        else EspDataUpdated?.Invoke([]);
    }

    public void EnableTileInspector(bool enable)
    {
        TileInspectorEnabled = enable;
    }

    public void RequestDataInspector()
    {
        EnforceFrozenData();

        int maxSw = Math.Min(MaxInspectorItems, _allSwitches.Count > 0 ? _allSwitches.Max(s => s.Id) : 100);
        int maxVa = Math.Min(MaxInspectorItems, _allVariables.Count > 0 ? _allVariables.Max(v => v.Id) : 100);

        _channel?.Send("dump", maxSw, maxVa);
        DataInspectorUpdated?.Invoke(new List<SwitchItem>(_allSwitches), new List<VariableItem>(_allVariables));
    }

    public void SetSwitch(int id, bool value)
    {
        var sw = FindSwitch(id);
        if (sw != null) sw.Value = value;

        SendCommand(8, id, value ? 1 : 0, 0, 0, $"$game_switches[{id}] = {value.ToString().ToLowerInvariant()}");
    }

    public void FreezeSwitch(int id, bool isFrozen, bool value)
    {
        var sw = FindSwitch(id);
        if (sw != null)
        {
            sw.IsFrozen = isFrozen;
            sw.Value = value;
        }

        if (isFrozen) _frozenSwitches[id] = value;
        else _frozenSwitches.Remove(id);

        SetSwitch(id, value);
    }

    public void SetVariable(int id, string value)
    {
        var va = FindVariable(id);
        if (va != null) va.Value = value;

        // 정수 또는 문자열
        string valExpr = int.TryParse(value, out int iv) ? iv.ToString() : $"\"{value.Replace("\"", "\\\"")}\"";
        SendCommand(9, id, 0, 0, 0, $"$game_variables[{id}] = {valExpr}");
    }

    public void FreezeVariable(int id, bool isFrozen, string value)
    {
        var va = FindVariable(id);
        if (va != null)
        {
            va.IsFrozen = isFrozen;
            va.Value = value;
        }

        if (isFrozen) _frozenVariables[id] = value;
        else _frozenVariables.Remove(id);

        SetVariable(id, value);
    }

    public void Warp(int mapId, int x, int y)
    {
        LatestState.MapId = mapId;
        LatestState.PlayerX = x;
        LatestState.PlayerY = y;

        LoadMap(mapId);

        string script = (_engine == CoreInterop.EngineXp)
            ? $"$game_temp.player_transferring = true; $game_temp.player_new_map_id = {mapId}; $game_temp.player_new_x = {x}; $game_temp.player_new_y = {y}; $game_temp.player_new_direction = 2"
            : $"$game_player.reserve_transfer({mapId}, {x}, {y}, 2)";

        SendCommand(5, mapId, x, y, 0, script);
        NotificationReceived?.Invoke($"맵 [{mapId:D3}] ({x}, {y}) 위치로 워프했습니다.");
        GameStateUpdated?.Invoke(LatestState);
        UpdateEsp();
    }

    public void UpdateVirtualMouse(double screenX, double screenY)
    {
        if (!TileInspectorEnabled) return;

        // 가상 마우스 좌표를 맵 타일 좌표로 변환
        int mapX = (int)Math.Floor(LatestState.DisplayX / TileDisplayScale) + (int)Math.Floor(screenX / 32.0);
        int mapY = (int)Math.Floor(LatestState.DisplayY / TileDisplayScale) + (int)Math.Floor(screenY / 32.0);

        bool passable = true;
        var tileIds = new List<int>();
        string evNames = "";

        if (CurrentMapTable != null && mapX >= 0 && mapX < CurrentMapWidth && mapY >= 0 && mapY < CurrentMapHeight)
        {
            for (int z = 0; z < CurrentMapTable.ZSize; z++)
            {
                int tid = CurrentMapTable.GetTile(mapX, mapY, z);
                tileIds.Add(tid);
            }

            // 최상위 타일이 0이 아니면 통과 여부 검사 (간이 계산)
            int topTile = CurrentMapTable.GetTopTile(mapX, mapY);
            passable = (topTile < 5000); // 5000번대 이상은 통상 벽/장애물

            var hits = CurrentMapEvents.Where(e => e.X == mapX && e.Y == mapY).Select(e => e.Name).ToList();
            if (hits.Count > 0)
            {
                evNames = string.Join(", ", hits);
                passable = false;
            }
        }
        else
        {
            passable = false;
        }

        var info = new TileInfo
        {
            MapX = mapX,
            MapY = mapY,
            Passable = passable,
            TileIds = tileIds,
            Events = evNames,
            ScreenX = screenX,
            ScreenY = screenY
        };

        TileInfoUpdated?.Invoke(info);
    }

    public void UpdateEsp()
    {
        if (!EspEnabled) return;

        var items = new List<EspItem>();
        double dispX = LatestState.DisplayX / TileDisplayScale;
        double dispY = LatestState.DisplayY / TileDisplayScale;

        foreach (var ev in CurrentMapEvents)
        {
            double sx = (ev.X - dispX) * 32.0;
            double sy = (ev.Y - dispY) * 32.0;

            // 뷰포트 내(또는 약간의 여유)에 있는 이벤트만 추출
            if (sx >= -64 && sx <= BaseWidth + 64 && sy >= -64 && sy <= BaseHeight + 64)
            {
                int trigger = (ev.Pages != null && ev.Pages.Count > 0) ? ev.Pages[0].Trigger : 0;
                items.Add(new EspItem
                {
                    Id = ev.Id,
                    Name = ev.Name,
                    Trigger = trigger,
                    X = sx,
                    Y = sy,
                    W = 32,
                    H = 32
                });
            }
        }

        EspDataUpdated?.Invoke(items);
    }

    private void EnforceFrozenData()
    {
        if (_frozenSwitches.Count == 0 && _frozenVariables.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var (id, val) in _frozenSwitches)
        {
            sb.Append($"$game_switches[{id}] = {val.ToString().ToLowerInvariant()}; ");
        }
        foreach (var (id, val) in _frozenVariables)
        {
            string valExpr = int.TryParse(val, out int iv) ? iv.ToString() : $"\"{val.Replace("\"", "\\\"")}\"";
            sb.Append($"$game_variables[{id}] = {valExpr}; ");
        }

        if (sb.Length > 0)
        {
            SendCommand(1, 0, 0, 0, 0, sb.ToString());
        }
    }

    private int _pollCount = 0;

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (!IsRunning) return;

        // 고정 값만 주기적으로 강제합니다 (텔레메트리는 에이전트가 푸시).
        if ((_frozenSwitches.Count > 0 || _frozenVariables.Count > 0) && ++_pollCount % 4 == 0) EnforceFrozenData();

    }

    private void ParseLiveInspectorData(string dataStr)
    {
        var parts = dataStr.Split('|', 2);
        if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0]))
        {
            string swStr = parts[0];
            for (int i = 0; i < swStr.Length; i++)
            {
                int swId = i + 1;
                if (!_frozenSwitches.ContainsKey(swId))
                {
                    var sw = FindSwitch(swId);
                    if (sw != null) sw.Value = (swStr[i] == '1');
                }
            }
        }
        if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]))
        {
            var vars = parts[1].Split(';');
            for (int i = 0; i < vars.Length; i++)
            {
                int varId = i + 1;
                if (!_frozenVariables.ContainsKey(varId))
                {
                    var va = FindVariable(varId);
                    if (va != null) va.Value = vars[i];
                }
            }
        }
        DataInspectorUpdated?.Invoke(new List<SwitchItem>(_allSwitches), new List<VariableItem>(_allVariables));
    }

    private void SendCommand(uint cmdType, int p1, int p2, int p3, double fp, string script)
    {
        // 에이전트는 텔레메트리를 스스로 보내므로 조회(2)는 필요 없고, 나머지는 루비 코드로 실행합니다.
        if (_channel != null && cmdType != 2 && !string.IsNullOrWhiteSpace(script)) _channel.Send("eval", script);
    }

    public void Restart()
    {
        SendCommand(1, 0, 0, 0, 0, "$scene = Scene_Title.new rescue nil");
    }

    public void Stop()
    {
        IsRunning = false;
        _pollTimer.Stop();
    }

    public void Dispose()
    {
        Stop();
    }
}
