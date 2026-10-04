#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace RocketRPG.Models;

/// <summary>
/// RocketRenderEasyRPG: Data-oriented native rendering backend for RPG Maker 2000 / 2003.
/// Directly executes 2000/2003 games via EasyRPG Player (C++ 오픈소스 엔진 / liblcf 기반) and delivers
/// hardware-accelerated frames to WPF RenderScreen (direct native surface hosting or shared texture).
/// Completely replaces the legacy PrintWindow/BitBlt OS window capture loop with safe automatic fallback.
/// </summary>
public class RocketRenderEasyRPG : IGameBridge, IDisposable
{
    private IntPtr _nativeInstance = IntPtr.Zero;
    private EasyRpgBridge? _bridge;
    private string _gameDir = "";
    private int _engine = CoreInterop.Engine2000;
    private int _processId = 0;
    private IntPtr _gameHwnd = IntPtr.Zero;
    private IntPtr _parentHwnd = IntPtr.Zero;

    private readonly DispatcherTimer _framePumpTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private Action<int, int, byte[]>? _frameCallback;

    public bool IsRunning { get; private set; }
    public double CurrentSpeed { get; private set; } = 1.0;
    public bool IsPaused { get; private set; }
    public bool IsNoclip => _bridge?.IsNoclip ?? false;
    public bool EspEnabled { get; private set; }
    public bool TileInspectorEnabled { get; private set; }
    public GameState LatestState => _bridge?.LatestState ?? new();

    public int BaseWidth => _screenW > 0 ? _screenW : 320;
    public int BaseHeight => _screenH > 0 ? _screenH : 240;
    public int BaseFps => 60;
    public double TileDisplayScale => 16.0;

    public IntPtr GameHwnd => _gameHwnd;
    /// <summary>게임 창을 품고 있는 RocketRPG 쪽 호스트 창</summary>
    public IntPtr ParentHwnd => _parentHwnd;
    public int ProcessId => _processId;
    public EasyRpgBridge? InnerBridge => _bridge;

    public event Action<GameState>? GameStateUpdated;
    public event Action<List<EspItem>>? EspDataUpdated;
    public event Action<TileInfo>? TileInfoUpdated;
    public event Action<List<SwitchItem>, List<VariableItem>>? DataInspectorUpdated;
    public event Action<string>? NotificationReceived;
    public event Action? GameHwndAttached;
    public event Action? ProcessExited;
    private readonly GameProcessWatcher _exitWatcher = new();
#pragma warning disable CS0067
    public event Action<KeyMessage>? HotkeyReceived;
#pragma warning restore CS0067

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public RocketRenderEasyRPG()
    {
        _framePumpTimer.Tick += OnFramePumpTick;
        _exitWatcher.Exited += OnProcessExited;
    }

    private void OnProcessExited()
    {
        if (!IsRunning) return;
        UiLog.Write($"RocketRenderEasyRPG: game process {_processId} exited unexpectedly");
        Stop();
        ProcessExited?.Invoke();
    }

    // ── RocketRPG 패치 EasyRPG Player 연동 (mkxp-z 에이전트와 같은 파이프 프로토콜) ──

    private MkxpAgentChannel? _channel;
    private int _screenW, _screenH;
    private readonly EspView _espView = new();
    private List<EspItem>? _lastEsp;
    public event Action<MessageState>? MessageStateChanged;
    public event Action<ChoiceState>? ChoiceChanged;
    // 원본(비패치) Player는 파이프에 접속하지 않으므로 실제 접속된 경우에만 대화 감지를 지원합니다.
    // 패치된 Player는 항상 대화 상태를 보냅니다 (연결 전에도 메시지 바 자리를 미리 잡아 화면이 나중에 줄어들지 않게).
    public bool SupportsMessageDetection => _channel != null;

    private static readonly object EnvLock = new();

    private string? _fontPath;
    private int _fontSize;
    private int _frameRate = 60;   // 0 = 무제한
    private bool _vsync;

    // 자식 프로세스는 부모 환경을 상속하므로 시작 직전에만 설정하고 바로 지웁니다.
    static int _frameMemorySeq;

    /// <summary>멀티 방송용: Player가 게임 화면을 쓰는 공유 메모리 이름 (Player 수정본이 RR_FRAME_SHM으로 받음)</summary>
    public string FrameMemoryName { get; private set; } = "";

    private int StartWithAgentEnvironment()
    {
        FrameMemoryName = $"RocketRPG_Frame_{Environment.ProcessId}_{System.Threading.Interlocked.Increment(ref _frameMemorySeq)}";
        lock (EnvLock)
        {
            bool is2003 = _engine == CoreInterop.Engine2003;
            UiLog.Write($"EasyRPG: RTP found = {string.Join("; ", RtpResolver.Resolve2kSources(is2003).Select(x => $"{x.Source}:{x.Path}"))}");
            string rtp = RtpResolver.PlayerRtpPaths(is2003);
            // 부모(RocketRPG)에 설정된 값은 그대로 물려줌 (사용자가 직접 정한 환경 변수)
            string inherited = Environment.GetEnvironmentVariable(is2003 ? "RPG2K3_RTP_PATH" : "RPG2K_RTP_PATH") ?? "";
            if (inherited.Length > 0) rtp = rtp.Length > 0 ? rtp + ";" + inherited : inherited;
            var vars = new Dictionary<string, string?>
            {
                ["RR_BRIDGE_PIPE"] = _channel?.PipeName,
                ["RR_FRAME_SHM"] = FrameMemoryName,
                ["RR_EASYRPG_FONT"] = _fontPath,
                ["RR_EASYRPG_FONT_SIZE"] = _fontSize > 0 ? _fontSize.ToString() : null,
                ["RR_EASYRPG_ENCODING"] = _gameDir != null ? Rpg2kEncoding.Detect(_gameDir) : null,
                ["RR_EASYRPG_FPS"] = _frameRate.ToString(),
                // EasyRPG에 더 알려 줄 RTP 폴더 (사용자 지정·기본 설치 폴더; 설치 기록은 Player가 직접 읽음)
                ["RPG2K_RTP_PATH"] = _engine == CoreInterop.Engine2000 ? rtp : null,
                ["RPG2K3_RTP_PATH"] = _engine == CoreInterop.Engine2003 ? rtp : null,
            };
            var previous = vars.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            foreach (var (k, v) in vars) Environment.SetEnvironmentVariable(k, v);
            try { return CoreInterop.rpg_easyrpg_start(_nativeInstance); }
            finally { foreach (var (k, v) in previous) Environment.SetEnvironmentVariable(k, v); }
        }
    }

    private void OnAgentLine(string kind, string payload)
    {
        try
        {
            switch (kind)
            {
                case "T":
                    _bridge?.OnAgentTelemetry(payload);
                    using (var doc = System.Text.Json.JsonDocument.Parse(payload))
                    {
                        if (doc.RootElement.TryGetProperty("ScreenW", out var sw)) _screenW = sw.GetInt32();
                        if (doc.RootElement.TryGetProperty("ScreenH", out var sh)) _screenH = sh.GetInt32();
                    }
                    break;
                case "D":
                    _bridge?.OnAgentData(payload);
                    break;
                case "M":
                    MessageStateChanged?.Invoke(MkxpAgentChannel.ParseMessage(payload));
                    break;
                case "Q":
                    ChoiceChanged?.Invoke(MkxpAgentChannel.ParseChoice(payload));
                    break;
                case "E":
                    var esp = MkxpAgentChannel.ParseEsp(payload, 16, out int ew, out int eh, _espView);
                    if (ew > 0) { _screenW = ew; _screenH = eh; }
                    _lastEsp = esp;
                    EspDataUpdated?.Invoke(esp);
                    break;
                case "C":
                    // 카메라만 바뀜(스크롤): 마지막 ESP 목록을 새 카메라 위치로 다시 그립니다.
                    if (_lastEsp != null && MkxpAgentChannel.ParseCamera(payload, _espView)) EspDataUpdated?.Invoke(_lastEsp);
                    break;
                case "I":
                    var tile = System.Text.Json.JsonSerializer.Deserialize<TileInfo>(payload);
                    if (tile != null) TileInfoUpdated?.Invoke(tile);
                    break;
                case "N":
                    UiLog.Write($"easyrpg-bridge notice: {payload}");
                    NotificationReceived?.Invoke(payload);
                    break;
                case "L":
                    UiLog.Write($"easyrpg-bridge: {payload}");
                    break;
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"RocketRenderEasyRPG: agent line '{kind}' error: {ex.Message}");
        }
    }

    public void SetBrightness(double value) => _channel?.Send("bright", value);
    // 2000/2003에서 쓸 수 있는 필터는 원본과 CRT(패치 Player가 주사선을 즉시 그림)뿐입니다. 나머지는 메뉴에서 비활성화됩니다.
    public void SetFilter(string filter) => ApplyCrt(filter);
    /// <summary>CRT 주사선은 패치된 플레이어가 즉시 그립니다 (나머지 필터는 실행 시 보간 설정).</summary>
    public void ApplyCrt(string? filter) => _channel?.Send("crt", filter == RocketShaderSystem.FilterCrtRoyale);
    public void SetAutoMessage(bool enabled, double speed) => _channel?.Send("auto", enabled, speed);
    public void SetSkipMessage(bool enabled) => _channel?.Send("skip", enabled);
    public void AdvanceMessage() => _channel?.Send("advance");
    /// <summary>멀티: 참가자 조종 중이면 켬 (게임이 매 프레임 키 상태를 받아 감)</summary>
    public void SetRemoteControl(bool on) => _channel?.Send("rctl", on);
    /// <summary>멀티: 참가자가 지금 누르고 있는 키 (윈도우 가상 키, 쉼표로 구분). Shift·Win32API 키 상태는 이 경로로만 들어갑니다.</summary>
    public void SetRemoteKeys(string vkList) => _channel?.Send("rkeys", vkList);
    /// <summary>멀티: 방송 화면에도 ESP를 그림 (참가자가 도구 권한이 있을 때)</summary>
    public void SetEspShare(bool on) => _channel?.Send("espshare", on);
    /// <summary>글꼴은 게임을 다시 시작할 때 적용합니다 (MainWindow가 재시작을 묻습니다).</summary>
    public void SetInGameFont(string? family, int size, bool bold)
    {
        _fontPath = string.IsNullOrWhiteSpace(family) ? null : RocketFontSystem.ResolveFontPath(family, bold);
        _fontSize = size;
    }

    /// <summary>
    /// Resolves the EasyRPG Player executable path matching the project's standard runtime hierarchy.
    /// Checks AppContext.BaseDirectory/runtimes/easyrpg/, process directory, game directory, and fallbacks.
    /// </summary>
    public static string ResolveExecutable(string? gameDir = null)
    {
        string[] exeNames = ["EasyRPG.exe", "Player.exe", "easyrpg-player.exe", "RocketRenderEasyRPG.exe"];

        // 1. Check game directory
        if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
        {
            foreach (var name in exeNames)
            {
                var cand = Path.Combine(gameDir, name);
                if (File.Exists(cand)) return Path.GetFullPath(cand);
            }
        }

        string appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string baseDir = AppContext.BaseDirectory;

        // 2. Portable and runtime standard paths
        var searchDirs = new List<string>
        {
            Path.Combine(baseDir, "runtimes", "easyrpg"),
            Path.Combine(appDir, "runtimes", "easyrpg"),
            baseDir,
            appDir,
            Path.Combine(Directory.GetCurrentDirectory(), "dist", "portable", "runtimes", "easyrpg"),
            Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "easyrpg"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RocketRPG", "runtimes", "easyrpg"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RocketRPG", "runtimes", "easyrpg")
        };

        // Walk up directory tree to support dev/test environments (up to 7 parent levels)
        string? cur = baseDir;
        for (int i = 0; i < 7 && !string.IsNullOrEmpty(cur); i++)
        {
            searchDirs.Add(Path.Combine(cur, "runtimes", "easyrpg"));
            cur = Path.GetDirectoryName(cur);
        }
        cur = appDir;
        for (int i = 0; i < 7 && !string.IsNullOrEmpty(cur); i++)
        {
            searchDirs.Add(Path.Combine(cur, "runtimes", "easyrpg"));
            cur = Path.GetDirectoryName(cur);
        }

        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var name in exeNames)
            {
                var cand = Path.Combine(dir, name);
                if (File.Exists(cand)) return Path.GetFullPath(cand);
            }
        }

        return "";
    }

    /// <summary>
    /// Starts the EasyRPG Player native engine for the specified game directory.
    /// Returns true if the native runtime successfully launched; false otherwise.
    /// </summary>
    public async Task<bool> StartGameAsync(
        string gameDir,
        int engine,
        IntPtr parentHwnd = default,
        bool fixedAspectRatio = true,
        bool smoothScaling = false,
        Action<int, int, byte[]>? frameCallback = null,
        Func<IntPtr>? parentHwndProvider = null,
        string? inGameFontFamily = null,
        int inGameFontSize = 0,
        bool inGameFontBold = false,
        int frameRate = 60,
        bool vsync = false)
    {
        Stop();
        _fontPath = string.IsNullOrWhiteSpace(inGameFontFamily) ? null : RocketFontSystem.ResolveFontPath(inGameFontFamily, inGameFontBold);
        _fontSize = inGameFontSize;
        _frameRate = frameRate;
        _vsync = vsync;
        if (!string.IsNullOrWhiteSpace(inGameFontFamily))
            UiLog.Write($"RocketRenderEasyRPG: in-game font '{inGameFontFamily}' -> {_fontPath ?? "(file not found)"} size {_fontSize}");

        _gameDir = gameDir;
        _engine = engine;
        _parentHwnd = parentHwnd;
        _frameCallback = frameCallback;

        if (!Directory.Exists(gameDir))
        {
            UiLog.Write($"RocketRenderEasyRPG: game directory not found: {gameDir}");
            return false;
        }

        string resolvedExe = ResolveExecutable(gameDir);
        if (CoreInterop.rpg_easyrpg_is_available_game(gameDir) == 0 && string.IsNullOrEmpty(resolvedExe))
        {
            UiLog.Write("RocketRenderEasyRPG: EasyRPG Player executable not found");
            return false;
        }

        var cfg = CoreInterop.EasyRpgConfig.Create();
        cfg.EngineType = (engine == CoreInterop.Engine2003) ? 2 : 1;
        cfg.ScreenWidth = (uint)BaseWidth;
        cfg.ScreenHeight = (uint)BaseHeight;
        cfg.TargetFps = (uint)BaseFps;
        cfg.Fullscreen = 0;
        cfg.FixedAspectRatio = (byte)(fixedAspectRatio ? 1 : 0);
        cfg.SmoothScaling = (byte)(smoothScaling ? 1 : 0);
        cfg.Vsync = (byte)(_vsync ? 1 : 0);
        cfg.WinResizable = 1;
        cfg.PreloadBridge = 1;
        cfg.UseSharedSurface = 1;
        cfg.GameDir = gameDir;
        if (!string.IsNullOrEmpty(resolvedExe))
        {
            cfg.CustomExe = resolvedExe;
        }

        int st = CoreInterop.rpg_easyrpg_create(ref cfg, out _nativeInstance);
        if (st != 0 || _nativeInstance == IntPtr.Zero)
        {
            UiLog.Write($"RocketRenderEasyRPG: rpg_easyrpg_create returned error {st}");
            return false;
        }

        IntPtr effectiveParent = parentHwnd;
        if (effectiveParent == IntPtr.Zero && parentHwndProvider != null)
        {
            try { effectiveParent = parentHwndProvider(); } catch { }
        }
        if (effectiveParent != IntPtr.Zero)
        {
            int initW = BaseWidth;
            int initH = BaseHeight;
            if (GetClientRect(effectiveParent, out var rc) && rc.Right > 0 && rc.Bottom > 0)
            {
                initW = rc.Right;
                initH = rc.Bottom;
            }
            CoreInterop.rpg_easyrpg_host_window(_nativeInstance, effectiveParent, 0, 0, initW, initH);
        }

        _channel?.Dispose();
        _channel = new MkxpAgentChannel();
        _channel.LineReceived += OnAgentLine;
        _channel.ConnectionChanged += c => UiLog.Write($"RocketRenderEasyRPG: bridge {(c ? "connected" : "disconnected")}");

        int startSt = StartWithAgentEnvironment();
        if (startSt != 0)
        {
            UiLog.Write($"RocketRenderEasyRPG: rpg_easyrpg_start returned error {startSt}");
            CoreInterop.rpg_easyrpg_destroy(_nativeInstance);
            _nativeInstance = IntPtr.Zero;
            return false;
        }

        // Capture PID immediately
        if (CoreInterop.rpg_easyrpg_get_pid(_nativeInstance, out var initialPid) == 0 && initialPid != 0)
        {
            _processId = (int)initialPid;
        }

        // Initialize EasyRpgBridge IPC with real processId
        // ESP/타일 정보는 패치 Player가 직접 보냅니다(OnAgentLine). 브릿지는 DB 이름/맵 데이터와 명령 전달 담당.
        _bridge = new EasyRpgBridge(gameDir, engine, () => _gameHwnd, _processId, _gameHwnd, _channel);
        _bridge.GameStateUpdated += s => GameStateUpdated?.Invoke(s);
        _bridge.DataInspectorUpdated += (sw, va) => DataInspectorUpdated?.Invoke(sw, va);
        _bridge.NotificationReceived += m => NotificationReceived?.Invoke(m);

        IsRunning = true;
        _exitWatcher.Watch(_processId);

        // Begin polling to detect HWND and optionally host native surface
        _ = PollGameHwndAsync(_parentHwnd, parentHwndProvider);

        if (_frameCallback != null)
        {
            _framePumpTimer.Start();
        }

        return await Task.FromResult(true);
    }

    private async Task PollGameHwndAsync(IntPtr parent, Func<IntPtr>? parentProvider = null)
    {
        for (int i = 0; i < 100 && IsRunning; i++)
        {
            await Task.Delay(50);
            if (_nativeInstance != IntPtr.Zero)
            {
                IntPtr targetParent = parent;
                if (targetParent == IntPtr.Zero && parentProvider != null)
                {
                    try { targetParent = parentProvider(); } catch { }
                }

                if (CoreInterop.rpg_easyrpg_get_hwnd(_nativeInstance, out var hwnd) == 0 && hwnd != IntPtr.Zero)
                {
                    _gameHwnd = hwnd;
                    CoreInterop.rpg_easyrpg_get_pid(_nativeInstance, out var pid);
                    if (pid != 0) _processId = (int)pid;

                    if (targetParent != IntPtr.Zero)
                    {
                        int hostW = BaseWidth;
                        int hostH = BaseHeight;
                        if (GetClientRect(targetParent, out var rc) && rc.Right > 0 && rc.Bottom > 0)
                        {
                            hostW = rc.Right;
                            hostH = rc.Bottom;
                        }
                        CoreInterop.rpg_easyrpg_host_window(_nativeInstance, targetParent, 0, 0, hostW, hostH);
                    }
                    _bridge?.UpdateGameHwnd(_gameHwnd, _processId);
                    UiLog.Write($"RocketRenderEasyRPG: native surface detected hwnd={hwnd}, pid={_processId}");
                    GameHwndAttached?.Invoke();
                    break;
                }
            }
        }
    }

    private byte[]? _pixelBuffer;
    private GCHandle _pixelBufferHandle;
    private IntPtr _pixelBufferPtr;

    private void EnsurePixelBuffer(int needBytes)
    {
        if (_pixelBuffer == null || _pixelBuffer.Length < needBytes)
        {
            if (_pixelBufferHandle.IsAllocated)
            {
                _pixelBufferHandle.Free();
            }
            _pixelBuffer = new byte[needBytes];
            _pixelBufferHandle = GCHandle.Alloc(_pixelBuffer, GCHandleType.Pinned);
            _pixelBufferPtr = _pixelBufferHandle.AddrOfPinnedObject();
        }
    }

    private void ReleasePixelBuffer()
    {
        if (_pixelBufferHandle.IsAllocated)
        {
            _pixelBufferHandle.Free();
        }
        _pixelBufferPtr = IntPtr.Zero;
        _pixelBuffer = null;
    }

    private void OnFramePumpTick(object? sender, EventArgs e)
    {
        if (!IsRunning || _nativeInstance == IntPtr.Zero || _frameCallback == null) return;

        var fi = new CoreInterop.EasyRpgFrame();
        if (CoreInterop.rpg_easyrpg_get_frame(_nativeInstance, ref fi) == 0 && fi.Width > 0 && fi.Height > 0)
        {
            int needBytes = (int)(fi.Width * fi.Height * 4);
            EnsurePixelBuffer(needBytes);
            if (_pixelBufferPtr != IntPtr.Zero &&
                CoreInterop.rpg_easyrpg_read_pixels(_nativeInstance, _pixelBufferPtr, (uint)needBytes) == 0)
            {
                _frameCallback((int)fi.Width, (int)fi.Height, _pixelBuffer!);
            }
        }
    }

    public void HostWindow(IntPtr parentHwnd, int x, int y, int w, int h)
    {
        _parentHwnd = parentHwnd;
        if (_nativeInstance != IntPtr.Zero && parentHwnd != IntPtr.Zero)
        {
            CoreInterop.rpg_easyrpg_host_window(_nativeInstance, parentHwnd, x, y, w, h);
        }
    }

    public void Resize(int w, int h)
    {
        if (_nativeInstance != IntPtr.Zero)
        {
            CoreInterop.rpg_easyrpg_resize(_nativeInstance, w, h);
        }
    }

    public void SendInput(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_nativeInstance != IntPtr.Zero)
        {
            CoreInterop.rpg_easyrpg_send_input(_nativeInstance, msg, wParam, lParam);
        }
    }

    public void SetSpeed(double speed)
    {
        CurrentSpeed = speed;
        _bridge?.SetSpeed(speed);
    }

    public void SetPause(bool paused)
    {
        if (IsPaused != paused)
        {
            TogglePause();
        }
    }

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        _bridge?.TogglePause();
    }

    public void ToggleNoclip(bool? through = null) => _bridge?.ToggleNoclip();
    void IGameBridge.ToggleNoclip() => ToggleNoclip(null);

    public void Warp(int mapId, int x, int y, int? direction = null) => _bridge?.Warp(mapId, x, y);
    void IGameBridge.Warp(int mapId, int x, int y) => Warp(mapId, x, y, null);

    public void QuickSave() => _bridge?.QuickSave();
    public void QuickLoad() => _bridge?.QuickLoad();
    public void ForceSaveMenu() => _bridge?.ForceSaveMenu();
    public void ForceLoadMenu() => _bridge?.ForceLoadMenu();

    public void SetVolume(int volumePercent)
    {
        if (_processId > 0) CoreInterop.rpg_set_volume_for_pid((uint)_processId, volumePercent);
    }

    public void SetSwitch(int id, bool value) => _bridge?.SetSwitch(id, value);
    public void SetVariable(int id, string value) => _bridge?.SetVariable(id, value);
    public void FreezeSwitch(int id, bool frozen, bool value) => _bridge?.FreezeSwitch(id, frozen, value);
    public void FreezeVariable(int id, bool frozen, string value) => _bridge?.FreezeVariable(id, frozen, value);

    public void EnableEsp(bool enabled)
    {
        EspEnabled = enabled;
        _bridge?.EnableEsp(enabled);
    }

    public void EnableTileInspector(bool enabled)
    {
        TileInspectorEnabled = enabled;
        _bridge?.EnableTileInspector(enabled);
    }

    public void RequestDataInspector() => _bridge?.RequestDataInspector();

    public void Restart()
    {
        _bridge?.Restart();
        if (_nativeInstance != IntPtr.Zero)
        {
            _exitWatcher.Watch(0);
            CoreInterop.rpg_easyrpg_stop(_nativeInstance);
            StartWithAgentEnvironment();
            if (CoreInterop.rpg_easyrpg_get_pid(_nativeInstance, out var pid) == 0 && pid != 0)
                _processId = (int)pid;
            _exitWatcher.Watch(_processId);
        }
    }

    public void Stop()
    {
        _framePumpTimer.Stop();
        _exitWatcher.Watch(0);
        IsRunning = false;

        ReleasePixelBuffer();

        _bridge?.Dispose();
        _bridge = null;

        if (_nativeInstance != IntPtr.Zero)
        {
            CoreInterop.rpg_easyrpg_stop(_nativeInstance);
            CoreInterop.rpg_easyrpg_destroy(_nativeInstance);
            _nativeInstance = IntPtr.Zero;
        }

        _channel?.Dispose();
        _channel = null;
        _screenW = _screenH = 0;
    }

    public void Dispose()
    {
        Stop();
    }
}
