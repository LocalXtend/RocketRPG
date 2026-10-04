#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RocketRPG.Models;

public class GameState
{
    [JsonPropertyName("scene")]
    public string Scene { get; set; } = "";

    [JsonPropertyName("mapId")]
    public int MapId { get; set; }

    [JsonPropertyName("playerX")]
    public int PlayerX { get; set; }

    [JsonPropertyName("playerY")]
    public int PlayerY { get; set; }

    [JsonPropertyName("displayX")]
    public double DisplayX { get; set; }

    [JsonPropertyName("displayY")]
    public double DisplayY { get; set; }

    [JsonPropertyName("noclip")]
    public bool Noclip { get; set; }
}

public class EspItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("trigger")]
    public int Trigger { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("w")]
    public double W { get; set; }

    [JsonPropertyName("h")]
    public double H { get; set; }

    /// <summary>
    /// 네이티브(mkxp-z/EasyRPG) ESP: 이벤트 위치를 화면 좌표 대신 맵 타일 좌표(TileX/TileY)로 받고,
    /// 화면 위치는 View의 카메라로 매 프레임 계산합니다. null이면 X/Y가 게임 화면 좌표입니다.
    /// </summary>
    [JsonIgnore] public EspView? View { get; set; }
    [JsonIgnore] public double TileX { get; set; }
    [JsonIgnore] public double TileY { get; set; }
}

/// <summary>맵 좌표 ESP의 카메라/맵 정보. 카메라(CamX/CamY)는 스크롤할 때마다 갱신됩니다.</summary>
public sealed class EspView
{
    public double Tile = 32;          // 타일 한 칸의 게임 화면 픽셀
    public double CamX, CamY;         // 화면 왼쪽 위의 맵 타일 좌표
    public double OffX, OffY;         // 맵이 화면보다 작을 때 가운데 정렬 오프셋(픽셀)
    public int ScreenW, ScreenH;      // 게임 화면 크기(픽셀)
    public int MapW, MapH;            // 맵 크기(타일)
    public bool LoopX, LoopY;
}

public class TileInfo
{
    [JsonPropertyName("mapX")]
    public int MapX { get; set; }

    [JsonPropertyName("mapY")]
    public int MapY { get; set; }

    [JsonPropertyName("passable")]
    public bool Passable { get; set; }

    [JsonPropertyName("tileIds")]
    public List<int>? TileIds { get; set; }

    [JsonPropertyName("events")]
    public string Events { get; set; } = "";

    [JsonPropertyName("screenX")]
    public double ScreenX { get; set; }

    [JsonPropertyName("screenY")]
    public double ScreenY { get; set; }
}

public class SwitchItem : INotifyPropertyChanged
{
    private int _id;
    private string _name = "";
    private bool _value;
    private bool _isFrozen;

    [JsonPropertyName("id")]
    public int Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(nameof(Id)); } }
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } }
    }

    [JsonPropertyName("val")]
    public bool Value
    {
        get => _value;
        set { if (_value != value) { _value = value; OnPropertyChanged(nameof(Value)); } }
    }

    [JsonPropertyName("frozen")]
    public bool IsFrozen
    {
        get => _isFrozen;
        set { if (_isFrozen != value) { _isFrozen = value; OnPropertyChanged(nameof(IsFrozen)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public class VariableItem : INotifyPropertyChanged
{
    private int _id;
    private string _name = "";
    private string _value = "";
    private bool _isFrozen;

    [JsonPropertyName("id")]
    public int Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(nameof(Id)); } }
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } }
    }

    [JsonPropertyName("val")]
    public string Value
    {
        get => _value;
        set { if (_value != value) { _value = value; OnPropertyChanged(nameof(Value)); } }
    }

    [JsonPropertyName("frozen")]
    public bool IsFrozen
    {
        get => _isFrozen;
        set { if (_isFrozen != value) { _isFrozen = value; OnPropertyChanged(nameof(IsFrozen)); } }
    }

    private bool _isPinned;

    [JsonPropertyName("pinned")]
    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned != value) { _isPinned = value; OnPropertyChanged(nameof(IsPinned)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public class KeyMessage
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("ctrl")]
    public bool Ctrl { get; set; }

    [JsonPropertyName("alt")]
    public bool Alt { get; set; }

    [JsonPropertyName("shift")]
    public bool Shift { get; set; }
}

public class WebViewRenderer : IGameBridge, IExtraModeTarget, IDisposable
{
    private readonly WebView2 _webView;
    private bool _isInitialized;
    private string? _currentGameDir;
    private bool _isCurrentMv;

    public bool IsInitialized => _isInitialized;
    public string? CurrentGameDir => _currentGameDir;
    public bool IsRunning { get; private set; }
    public double CurrentSpeed { get; private set; } = 1.0;
    public bool IsPaused { get; private set; }
    public bool IsNoclip { get; private set; }
    long _noclipPendingUntil;
    public bool EspEnabled { get; private set; }
    public bool TileInspectorEnabled { get; private set; }
    public GameState LatestState { get; private set; } = new();

    public event Action<GameState>? GameStateUpdated;
    public event Action<List<EspItem>>? EspDataUpdated;
    public event Action<TileInfo>? TileInfoUpdated;
    public event Action<List<SwitchItem>, List<VariableItem>>? DataInspectorUpdated;
    public event Action<string>? NotificationReceived;
    public event Action<KeyMessage>? HotkeyReceived;
    public event Action<MessageState>? MessageStateChanged;
    public event Action<ChoiceState>? ChoiceChanged;
    public event Action? ProcessExited;
    /// <summary>게임 화면(렌더러/GPU)이 비정상 종료됨 또는 WebGL 화면을 잃음 (설명). 세션은 부르는 쪽이 정리합니다.</summary>
    public event Action<string, bool>? ProcessCrashed;   // (설명, 페이지가 사라졌는지)
    /// <summary>게임이 첫 장면을 그리기 시작함 (불러오는 중 화면을 닫을 때)</summary>
    public event Action? GameReady;
    /// <summary>게임이 자체 오류 화면(스크립트 오류/리소스 로드 실패)을 띄움 — 원본 실행 모드 대체용</summary>
    public event Action<string>? GameFatalError;

    public bool SupportsMessageDetection => true;

    // 페이지가 새로 로드될 때마다 다시 보내야 하는 호스트 측 상태
    (string family, int size, bool bold)? _font;
    double _brightness = 1.0;
    int _volume = 100;
    bool _autoMessage, _skipMessage;
    double _autoSpeed = 1.0;

    public WebViewRenderer(WebView2 webView)
    {
        _webView = webView;
    }

    readonly NwFsHost _nwHost = new();

    static string NwShimScript() => ResourceText("RocketRPG.Resources.nw_shim.js");

    static string ResourceText(string name)
    {
        using var s = typeof(WebViewRenderer).Assembly.GetManifestResourceStream(name);
        if (s == null) return "";
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>자동 테스트 로그 파일 (RR_AUTOTEST). 설정되어 있으면 테스트 스크립트를 주입하고 끝나면 앱을 닫습니다.</summary>
    static readonly string? AutotestLog = Environment.GetEnvironmentVariable("RR_AUTOTEST");

    static void AutotestWrite(string line)
    {
        if (string.IsNullOrEmpty(AutotestLog)) return;
        try { File.AppendAllText(AutotestLog, line + "\n", new System.Text.UTF8Encoding(false)); } catch { }
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        try
        {
            string userDataDir = Path.Combine(SettingsService.Root(), "webview2_data");
            // 개발/진단용: RR_WEBVIEW_ARGS="--remote-debugging-port=9222" 등 브라우저 인자 전달
            string? extraArgs = Environment.GetEnvironmentVariable("RR_WEBVIEW_ARGS");
            // NW.js와 같은 동작: 창이 가려지거나 뒤로 가도 게임이 멈추지 않게(크로미엄 백그라운드/가림 감지 스로틀 끄기),
            // 클릭 없이도 소리 재생. 끄지 않으면 다른 창에 가려진 동안 게임 루프가 멈추거나 느려졌습니다.
            string args = "--disable-background-timer-throttling --disable-renderer-backgrounding " +
                          "--disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion " +
                          "--autoplay-policy=no-user-gesture-required";
            if (!string.IsNullOrWhiteSpace(extraArgs)) args += " " + extraArgs;
            var options = new CoreWebView2EnvironmentOptions(args);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // 모든 MV/MZ 게임이 같은 주소(http://rocket.local/)로 열리므로, 캐시가 있으면 앞서 한 게임의 파일
            // (예: js/plugins.js)이 다음 게임에 쓰여 "Failed to load: js/plugins/…" 오류가 났습니다. 게임 파일은 로컬이라
            // 캐시가 필요 없으므로 끕니다.
            try
            {
                await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");
            }
            catch (Exception ex)
            {
                UiLog.Write($"WebViewRenderer: could not disable cache: {ex.Message}");
            }

            // NW.js 환경(require('fs')/path/process/nw) — 게임이 원래처럼 자기 폴더에 저장하도록. 브릿지보다 먼저.
            _webView.CoreWebView2.AddHostObjectToScript("rrnw", _nwHost);
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(NwShimScript());

            // 주입할 브릿지 스크립트 등록
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
            // 멀티 방장: 게임 캔버스 화면을 공유 메모리로 (방송할 때만 움직임)
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ResourceText("RocketRPG.Resources.rocket_multi_frames.js"));
            // 멀티 엑스트라 모드: 참가자마다 캐릭터 (켜졌을 때만 움직임)
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ResourceText("RocketRPG.Resources.rocket_extra.js"));

            // 자동 호환성 테스트(scripts/mv_autotest.ps1)에서만
            if (!string.IsNullOrEmpty(AutotestLog))
            {
                // RR_EXTRA_TEST: 첫 맵에서 엑스트라 모드(참가자 캐릭터)만 시험하고 끝냄
                if (Environment.GetEnvironmentVariable("RR_EXTRA_TEST") == "1")
                    await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.__rrExtraTest = true;");
                await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ResourceText("RocketRPG.Resources.mv_autotest.js"));
            }

            // 게임이 스스로 종료(window.close / nw.App.quit)하면 세션을 끝냅니다.
            _webView.CoreWebView2.WindowCloseRequested += (_, _) =>
            {
                UiLog.Write("WebViewRenderer: game closed its window");
                if (IsRunning) ProcessExited?.Invoke();
            };

            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.ProcessFailed += OnProcessFailed;
            _isInitialized = true;
            UiLog.Write("WebViewRenderer: CoreWebView2 initialized successfully");
        }
        catch (Exception ex)
        {
            UiLog.Write($"WebViewRenderer init error: {ex.Message}");
            throw;
        }
    }

    string? _lastServeDir;

    /// <param name="contentDir">게임 파일을 실제로 읽어 올 폴더 (묶인 exe를 푼 캐시). 없으면 게임 폴더. 저장은 항상 게임 폴더.</param>
    public async Task StartGameAsync(string gameDir, bool isMv, string? contentDir = null)
    {
        if (!_isInitialized) await InitializeAsync();

        _currentGameDir = gameDir;
        _isCurrentMv = isMv;
        IsRunning = true;
        StartMemoryWatch();
        CurrentSpeed = 1.0;
        IsPaused = false;
        IsNoclip = false;

        string serveDir = string.IsNullOrEmpty(contentDir) ? gameDir : contentDir!;
        _nwHost.SetRoot(gameDir, serveDir);

        // 다른 게임으로 바뀌면 디스크 캐시에 남은 이전 게임 파일도 지웁니다 (예전 버전이 남긴 캐시 포함).
        if (!string.Equals(_lastServeDir, serveDir, StringComparison.OrdinalIgnoreCase))
        {
            _lastServeDir = serveDir;
            try { await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache); }
            catch (Exception ex) { UiLog.Write($"WebViewRenderer: cache clear failed: {ex.Message}"); }
        }

        // 가상 호스트 rocket.local을 게임 파일 폴더에 매핑
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "rocket.local",
            serveDir,
            CoreWebView2HostResourceAccessKind.Allow);

        string startUrl;
        // MV는 보통 www/, MZ는 보통 루트지만 www/로 배포된 MZ도 있으므로 실제로 있는 쪽을 씁니다.
        if (File.Exists(Path.Combine(serveDir, "www", "index.html")) && (isMv || !File.Exists(Path.Combine(serveDir, "index.html"))))
        {
            startUrl = "http://rocket.local/www/index.html";
        }
        else
        {
            startUrl = "http://rocket.local/index.html";
        }

        UiLog.Write($"WebViewRenderer: Navigating to {startUrl} (gameDir={gameDir})");
        _webView.CoreWebView2.Navigate(startUrl);
    }

    public Task ApplyInGameFontAsync(string? fontFamily, int fontSize = 0, bool isBold = false)
    {
        SetInGameFont(fontFamily, fontSize, isBold);
        return Task.CompletedTask;
    }

    public void SetInGameFont(string? family, int size, bool bold)
    {
        _font = (family ?? "", size, bold);
        PostCommand(new { type = "setFont", family = family ?? "", size, bold });
    }

    public void SetBrightness(double value)
    {
        _brightness = value;
        PostCommand(new { type = "setBrightness", value });
    }

    // WPF 쉐이더는 WebView2(별도 창)에 닿지 않으므로 게임 안의 PIXI 필터(GLSL)로 적용합니다.
    string _filter = "none";
    public void SetFilter(string filter)
    {
        _filter = filter ?? "none";
        PostCommand(new { type = "setFilter", name = _filter });
    }

    public void SetAutoMessage(bool enabled, double speed)
    {
        _autoMessage = enabled;
        _autoSpeed = speed;
        PostCommand(new { type = "setAutoMessage", enabled, speed });
    }

    public void SetSkipMessage(bool enabled)
    {
        _skipMessage = enabled;
        PostCommand(new { type = "setSkipMessage", enabled });
    }

    public void AdvanceMessage() => PostCommand(new { type = "advanceMessage" });

    // 새 문서가 로드되면 브릿지 상태가 초기화되므로 호스트 측 설정을 다시 전송합니다.
    // ── 멀티 방장: 게임 캔버스 화면 (rocket_multi_frames.js가 이 메모리에 씀) ──
    public const int FrameSlotBytes = 1920 * 1088 * 4;
    CoreWebView2SharedBuffer? _frameBuffer;
    bool _frameFeed, _framePosted;

    /// <summary>방송하는 동안 게임 페이지에 공유 메모리를 넘겨 캔버스 화면을 받습니다. 메모리 주소 (못 하면 Zero).</summary>
    public IntPtr EnableFrameFeed(bool on)
    {
        _frameFeed = on;
        if (!on || !_isInitialized || _webView.CoreWebView2 == null) return IntPtr.Zero;
        try
        {
            _frameBuffer ??= _webView.CoreWebView2.Environment.CreateSharedBuffer((ulong)(4096 + 2L * FrameSlotBytes));
            if (!_framePosted) PostFrameBuffer();
            return _frameBuffer.Buffer;
        }
        catch (Exception ex)
        {
            UiLog.Write($"WebViewRenderer: frame feed unavailable {ex.Message}");
            return IntPtr.Zero;
        }
    }

    void PostFrameBuffer()
    {
        if (_frameBuffer == null || _webView.CoreWebView2 == null) return;
        try
        {
            _webView.CoreWebView2.PostSharedBufferToScript(_frameBuffer, CoreWebView2SharedBufferAccess.ReadWrite,
                $"{{\"kind\":\"rr-frames\",\"slotBytes\":{FrameSlotBytes}}}");
            _framePosted = true;
        }
        catch (Exception ex) { UiLog.Write($"WebViewRenderer: frame buffer post failed {ex.Message}"); }
    }

    void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _framePosted = false;   // 새 페이지는 공유 메모리를 다시 받아야 함
        if (!IsRunning) return;
        if (_frameFeed && e.IsSuccess) PostFrameBuffer();
        if (!e.IsSuccess)
        {
            UiLog.Write($"WebViewRenderer: navigation failed ({e.WebErrorStatus})");
            return;
        }
        if (_font is { } f) PostCommand(new { type = "setFont", family = f.family, size = f.size, bold = f.bold });
        PostCommand(new { type = "setBrightness", value = _brightness });
        PostCommand(new { type = "setFilter", name = _filter });
        PostCommand(new { type = "setVolume", volume = Math.Clamp(_volume, 0, 200) / 100.0 });
        PostCommand(new { type = "setSpeed", speed = CurrentSpeed });
        PostCommand(new { type = "setFrameRate", fps = _frameRate });   // 페이지가 새로 뜨면 기본 60으로 돌아가므로 다시 보냄
        PostCommand(new { type = "enableEsp", enabled = EspEnabled });
        PostCommand(new { type = "enableTileInspector", enabled = TileInspectorEnabled });
        PostCommand(new { type = "setAutoMessage", enabled = _autoMessage, speed = _autoSpeed });
        PostCommand(new { type = "setSkipMessage", enabled = _skipMessage });
        if (_extraOn) { PostCommand(new { type = "extra", op = "mode", on = true }); PostCommand(new { type = "extra", op = "guests", list = _extraGuests }); }
    }

    // ── 멀티 엑스트라 모드 (rocket_extra.js) ──
    bool _extraOn;
    object[] _extraGuests = [];
    public bool ExtraSupported => true;

    /// <summary>엑스트라 모드 켜고 끄기. 끄면 참가자 캐릭터가 모두 사라짐</summary>
    public void SetExtraMode(bool on)
    {
        _extraOn = on;
        if (!on) _extraGuests = [];
        PostCommand(new { type = "extra", op = "mode", on });
    }

    /// <summary>캐릭터를 둘 참가자 (id, 이름, 이름표 색 #rrggbb)</summary>
    public void SetExtraGuests(IEnumerable<(string id, string name, string color)> guests)
    {
        _extraGuests = guests.Select(g => (object)new { id = g.id, name = g.name, color = g.color }).ToArray();
        PostCommand(new { type = "extra", op = "guests", list = _extraGuests });
    }

    public void ExtraKey(string id, int vk, bool down) => PostCommand(new { type = "extra", op = "key", id, k = vk, d = down });
    public void ExtraHeld(string id, IEnumerable<int> keys) => PostCommand(new { type = "extra", op = "held", id, keys = keys.ToArray() });

    void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        string desc = "";
        try { desc = $" exit={e.ExitCode} process='{e.ProcessDescription}'"; } catch { }
        UiLog.Write($"WebViewRenderer: process failed kind={e.ProcessFailedKind} reason={e.Reason}{desc} (memory before: {_lastMemory})");
        // 대용량 맵/이벤트를 동기 로딩하는 동안에도 Unresponsive가 옵니다.
        // 프로세스는 아직 살아 있으므로 세션을 끝내거나 페이지를 다시 열지 않습니다.
        if (!IsRunning) return;
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                IsRunning = false;
                ProcessExited?.Invoke();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                // 게임 페이지가 사라짐 (메모리 부족 등). 그냥 끝내지 않고 이유와 다시 시작을 묻습니다.
                IsRunning = false;
                ProcessCrashed?.Invoke(CrashText(e.Reason), true);
                break;
            case CoreWebView2ProcessFailedKind.GpuProcessExited:
                // 화면 그리기 프로세스는 다시 뜨지만 게임은 WebGL 화면을 잃어 깨져 보일 수 있음
                ProcessCrashed?.Invoke("화면을 그리는 부분이 다시 시작되었습니다.", false);
                break;
        }
    }

    static string CrashText(CoreWebView2ProcessFailedReason reason) => reason switch
    {
        CoreWebView2ProcessFailedReason.OutOfMemory => "메모리가 모자라 게임 화면이 꺼졌습니다.",
        CoreWebView2ProcessFailedReason.Unresponsive => "게임 화면이 오래 응답하지 않아 꺼졌습니다.",
        CoreWebView2ProcessFailedReason.Crashed => "게임 화면이 갑자기 꺼졌습니다.",
        _ => "게임 화면이 꺼졌습니다.",
    };

    // ── 진단: 게임이 도는 동안 WebView2 프로세스 메모리를 기록 (크게 늘 때와 2분마다) ──
    string _lastMemory = "?";
    long _lastMemoryLogged;
    long _lastMemoryLoggedAt;
    System.Windows.Threading.DispatcherTimer? _memoryTimer;

    void StartMemoryWatch()
    {
        if (_memoryTimer != null) return;
        _memoryTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _memoryTimer.Tick += (_, _) => SampleMemory();
        _memoryTimer.Start();
    }

    void SampleMemory()
    {
        if (!IsRunning || _webView.CoreWebView2 == null) return;
        try
        {
            var groups = new Dictionary<string, long>();
            foreach (var info in _webView.CoreWebView2.Environment.GetProcessInfos())
            {
                long ws = 0;
                try { using var p = System.Diagnostics.Process.GetProcessById(info.ProcessId); ws = p.WorkingSet64; } catch { }
                string k = info.Kind.ToString();
                groups[k] = groups.GetValueOrDefault(k) + ws;
            }
            long total = groups.Values.Sum();
            _lastMemory = string.Join(", ", groups.OrderByDescending(g => g.Value).Select(g => $"{g.Key} {g.Value >> 20}MB"));
            long now = Environment.TickCount64;
            if (Math.Abs(total - _lastMemoryLogged) > (300L << 20) || now - _lastMemoryLoggedAt > 120_000)
            {
                _lastMemoryLogged = total;
                _lastMemoryLoggedAt = now;
                UiLog.Write($"WebViewRenderer: memory {total >> 20}MB ({_lastMemory})");
            }
        }
        catch { }
    }

    public void Stop()
    {
        IsRunning = false;
        if (_isInitialized && _webView.CoreWebView2 != null)
        {
            try
            {
                _webView.CoreWebView2.Navigate("about:blank");
            }
            catch { }
        }
    }

    public void Restart()
    {
        if (!string.IsNullOrEmpty(_currentGameDir))
        {
            _ = StartGameAsync(_currentGameDir, _isCurrentMv);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;

            string type = typeProp.GetString() ?? "";
            switch (type)
            {
                case "gameState":
                    var state = JsonSerializer.Deserialize<GameState>(e.WebMessageAsJson);
                    if (state != null)
                    {
                        if (state.Noclip == IsNoclip) _noclipPendingUntil = 0;
                        else if (Environment.TickCount64 < _noclipPendingUntil) state.Noclip = IsNoclip; // 켠 직후 도착한 이전 상태
                        LatestState = state;
                        IsNoclip = state.Noclip;
                        GameStateUpdated?.Invoke(state);
                    }
                    break;

                case "gameReady":
                    GameReady?.Invoke();
                    break;

                case "espData":
                    if (root.TryGetProperty("events", out var eventsProp))
                    {
                        var items = JsonSerializer.Deserialize<List<EspItem>>(eventsProp.GetRawText());
                        if (items != null) EspDataUpdated?.Invoke(items);
                    }
                    break;

                case "tileInfo":
                    var tile = JsonSerializer.Deserialize<TileInfo>(e.WebMessageAsJson);
                    if (tile != null) TileInfoUpdated?.Invoke(tile);
                    break;

                case "dataInspectorData":
                    var switches = root.TryGetProperty("switches", out var swProp)
                        ? JsonSerializer.Deserialize<List<SwitchItem>>(swProp.GetRawText()) ?? new()
                        : new List<SwitchItem>();
                    var variables = root.TryGetProperty("variables", out var varProp)
                        ? JsonSerializer.Deserialize<List<VariableItem>>(varProp.GetRawText()) ?? new()
                        : new List<VariableItem>();
                    DataInspectorUpdated?.Invoke(switches, variables);
                    break;

                case "notification":
                    if (root.TryGetProperty("message", out var msgProp))
                    {
                        NotificationReceived?.Invoke(msgProp.GetString() ?? "");
                    }
                    break;

                case "hotkey":
                    var km = JsonSerializer.Deserialize<KeyMessage>(e.WebMessageAsJson);
                    if (km != null) HotkeyReceived?.Invoke(km);
                    break;

                case "autotest":
                {
                    if (root.TryGetProperty("line", out var lp)) AutotestWrite(lp.GetString() ?? "");
                    if (root.TryGetProperty("done", out var dp) && dp.ValueKind == JsonValueKind.True)
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
                    break;
                }
                case "openExternal":
                {
                    // nw.Shell.openExternal: 게임 속 링크(공식 사이트 등)를 기본 브라우저로 — http/https만 허용
                    string url = root.TryGetProperty("url", out var up) ? up.GetString() ?? "" : "";
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        UiLog.Write($"WebViewRenderer: openExternal {uri}");
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); } catch { }
                    }
                    break;
                }
                case "fatal":
                {
                    string kind = root.TryGetProperty("kind", out var kp) ? kp.GetString() ?? "" : "";
                    string msg = (root.TryGetProperty("name", out var np) ? np.GetString() : "") + ": " +
                                 (root.TryGetProperty("message", out var mp) ? mp.GetString() : "");
                    string stack = root.TryGetProperty("stack", out var stp) ? stp.GetString() ?? "" : "";
                    UiLog.Write($"WebViewRenderer: game {kind} error: {msg}\n{stack}");
                    AutotestWrite("0\tCRASH fatal " + msg);
                    GameFatalError?.Invoke(msg);
                    break;
                }

                case "contextLost":
                    UiLog.Write($"WebViewRenderer: game lost its WebGL screen (memory: {_lastMemory})");
                    if (IsRunning) ProcessCrashed?.Invoke("게임 화면(그래픽)을 잃었습니다. 그림이 깨지거나 검게 보일 수 있습니다.", false);
                    break;

                case "choiceState":
                {
                    var c = new ChoiceState
                    {
                        Gen = root.TryGetProperty("gen", out var gp) && gp.TryGetInt32(out int g) ? g : 0,
                        Open = root.TryGetProperty("open", out var op) && op.ValueKind == JsonValueKind.True,
                        Picked = root.TryGetProperty("picked", out var pp) && pp.TryGetInt32(out int pk) ? pk : -1,
                    };
                    if (c.Open && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var it in System.Linq.Enumerable.Take(items.EnumerateArray(), 16))
                            c.Items.Add((it.TryGetProperty("t", out var t) ? t.GetString() ?? "" : "", !it.TryGetProperty("e", out var en) || en.ValueKind != JsonValueKind.False));
                    ChoiceChanged?.Invoke(c);
                    break;
                }

                case "messageState":
                    MessageStateChanged?.Invoke(new MessageState
                    {
                        Busy = root.TryGetProperty("busy", out var bp) && bp.ValueKind == JsonValueKind.True,
                        Speaker = root.TryGetProperty("speaker", out var sp) ? sp.GetString() ?? "" : "",
                        Text = root.TryGetProperty("text", out var tp) ? tp.GetString() ?? "" : ""
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"WebViewRenderer message error: {ex.Message}");
        }
    }

    public void PostCommand(object command)
    {
        if (!_isInitialized || !IsRunning || _webView.CoreWebView2 == null) return;
        try
        {
            string json = JsonSerializer.Serialize(command);
            _webView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            UiLog.Write($"WebViewRenderer post error: {ex.Message}");
        }
    }

    public void SetSpeed(double speed)
    {
        CurrentSpeed = speed;
        PostCommand(new { type = "setSpeed", speed });
    }

    /// <summary>화면 프레임 상한 (0 = 무제한 = 모니터 주사율). 바로 적용됩니다.</summary>
    /// <summary>게임 페이지만 PNG로 찍습니다 (위에 겹친 다른 창은 찍히지 않음). 실패하면 null.</summary>
    public async Task<byte[]?> CapturePngAsync()
    {
        if (!_isInitialized || !IsRunning || _webView.CoreWebView2 == null) return null;
        try
        {
            using var ms = new MemoryStream();
            await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch (Exception ex)
        {
            UiLog.Write($"WebViewRenderer: capture failed {ex.Message}");
            return null;
        }
    }

    public void SetFrameRate(int fps)
    {
        _frameRate = fps;
        PostCommand(new { type = "setFrameRate", fps });
    }

    int _frameRate = 60;

    public void SetPause(bool paused)
    {
        IsPaused = paused;
        PostCommand(new { type = "setPause", paused });
    }

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        PostCommand(new { type = "togglePause" });
    }

    public void ToggleNoclip(bool? through = null)
    {
        IsNoclip = through ?? !IsNoclip;
        LatestState.Noclip = IsNoclip;
        _noclipPendingUntil = Environment.TickCount64 + 1500;
        GameStateUpdated?.Invoke(LatestState);
        PostCommand(new { type = "toggleNoclip", through = IsNoclip });
    }
    void IGameBridge.ToggleNoclip() => ToggleNoclip(null);

    public void Warp(int mapId, int x, int y, int? direction = null)
    {
        PostCommand(new { type = "warp", mapId, x, y, direction });
    }
    void IGameBridge.Warp(int mapId, int x, int y) => Warp(mapId, x, y, null);

    public void QuickSave() => PostCommand(new { type = "quickSave" });

    /// <summary>
    /// 게임에 키를 넣음 (멀티 조종 권한: 참가자 키). 개발자 도구 입력으로 보내 게임은 실제로 누른 키로 받습니다.
    /// </summary>
    public void SendKey(int vk, string code, string key, bool down)
    {
        if (!_isInitialized || _webView.CoreWebView2 == null) return;
        string args = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = down ? "rawKeyDown" : "keyUp",
            windowsVirtualKeyCode = vk,
            nativeVirtualKeyCode = vk,
            code,
            key,
        });
        try { _ = _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", args); }
        catch (Exception ex) { UiLog.Write($"WebViewRenderer: key failed {ex.Message}"); }
    }
    public void QuickLoad() => PostCommand(new { type = "quickLoad" });
    public void ForceSaveMenu() => PostCommand(new { type = "forceSaveMenu" });
    public void ForceLoadMenu() => PostCommand(new { type = "forceLoadMenu" });

    public void SetVolume(int volumePercent)
    {
        _volume = volumePercent;
        double vol = Math.Clamp(volumePercent, 0, 200) / 100.0;
        PostCommand(new { type = "setVolume", volume = vol });
    }

    public void SetSwitch(int id, bool value) =>
        PostCommand(new { type = "setSwitch", id, value });

    public void SetVariable(int id, string value) =>
        PostCommand(new { type = "setVariable", id, value });

    public void SetVariable(int id, object value) =>
        PostCommand(new { type = "setVariable", id, value });

    public void FreezeSwitch(int id, bool frozen, bool value) =>
        PostCommand(new { type = "freezeSwitch", id, frozen, value });

    public void FreezeVariable(int id, bool frozen, string value) =>
        PostCommand(new { type = "freezeVariable", id, frozen, value });

    public void FreezeVariable(int id, bool frozen, object value) =>
        PostCommand(new { type = "freezeVariable", id, frozen, value });

    public void EnableEsp(bool enabled)
    {
        EspEnabled = enabled;
        PostCommand(new { type = "enableEsp", enabled });
    }

    public void EnableTileInspector(bool enabled)
    {
        TileInspectorEnabled = enabled;
        PostCommand(new { type = "enableTileInspector", enabled });
    }

    public void RequestDataInspector() =>
        PostCommand(new { type = "requestDataInspector" });

    public void Dispose()
    {
        Stop();
    }

    private const string BridgeScript = @"
(function() {
    if (window.__rocketBridgeInstalled) return;
    window.__rocketBridgeInstalled = true;

    const bridge = {
        speed: 1.0,
        paused: false,
        espEnabled: false,
        tileInspectorEnabled: false,
        frozenSwitches: {},
        frozenVariables: {},
        mousePos: { x: 0, y: 0 },
        font: null,              // { family, size, bold }
        brightness: 1.0,
        autoMessage: false,
        autoSpeed: 1.0,
        skipMessage: false,
        msgBusy: false,
        msgWaitStart: 0
    };
    window.__rocketBridge = bridge;

    // 1. Host -> Web 메시징
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function(event) {
            try {
                const msg = event.data;
                if (!msg || !msg.type) return;
                handleHostCommand(msg);
            } catch (err) {
                console.error('[RocketBridge] Command error:', err);
            }
        });
    }

    function sendToHost(type, payload) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(Object.assign({ type: type }, payload));
        }
    }

    // 2. DOM 인게임 오버레이 레이어 관리
    let overlayDiv = null;
    let espContainer = null;
    let tileHudDiv = null;
    let toastContainer = null;

    function ensureOverlay() {
        if (!overlayDiv && document.body) {
            overlayDiv = document.createElement('div');
            overlayDiv.id = '__rocketOverlay';
            overlayDiv.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;pointer-events:none;z-index:2147483647;overflow:hidden;font-family:sans-serif;user-select:none;';

            espContainer = document.createElement('div');
            espContainer.id = '__rocketEspContainer';
            espContainer.style.cssText = 'position:absolute;top:0;left:0;width:100%;height:100%;pointer-events:none;';
            overlayDiv.appendChild(espContainer);

            tileHudDiv = document.createElement('div');
            tileHudDiv.id = '__rocketTileHud';
            tileHudDiv.style.cssText = 'position:fixed;left:16px;bottom:16px;padding:8px 12px;background:rgba(30,30,30,0.92);border:1px solid #007acc;border-radius:4px;color:#00ffcc;font-size:13px;font-family:Consolas,monospace;font-weight:600;display:none;pointer-events:none;box-shadow:0 4px 12px rgba(0,0,0,0.5);';
            overlayDiv.appendChild(tileHudDiv);

            toastContainer = document.createElement('div');
            toastContainer.id = '__rocketToastContainer';
            toastContainer.style.cssText = 'position:fixed;bottom:32px;left:50%;transform:translateX(-50%);display:flex;flex-direction:column;align-items:center;gap:8px;pointer-events:none;z-index:2147483647;';
            overlayDiv.appendChild(toastContainer);

            document.body.appendChild(overlayDiv);
        }
        return overlayDiv;
    }

    // 알림은 RocketRPG 오버레이 한 곳에서만 보여 줍니다 (페이지 안에도 그리면 두 개가 겹쳐 보였음).
    function showToast(msg) {
        try { window.chrome.webview.postMessage({ type: 'notification', message: String(msg) }); } catch (e) { }
    }

    // 불러오기가 끝나 첫 장면이 그려지기 시작하면 RocketRPG에 알립니다 (그때 '불러오는 중' 화면을 닫음)
    (function rrReadyPoll() {
        try {
            // Scene_Boot(파일 불러오는 장면)이 끝나고 첫 진짜 장면(로고/타이틀/맵)이 떴을 때
            if (window.SceneManager && SceneManager._scene && window.Scene_Boot && !(SceneManager._scene instanceof Scene_Boot) && Graphics.frameCount > 2) {
                window.chrome.webview.postMessage({ type: 'gameReady' });
                return;
            }
        } catch (e) { }
        setTimeout(rrReadyPoll, 100);
    })();

    // 화면 프레임 상한 (화면 > 프레임). MV/MZ는 게임 진행이 60 FPS 고정 단계로 돌기 때문에 그리는 횟수만 바뀝니다.
    // 0 = 제한 없음(모니터 주사율). requestAnimationFrame을 감싸 상한보다 이른 프레임은 건너뜁니다.
    // 같은 프레임(같은 t)의 콜백들은 한 번 내린 결정을 함께 씁니다. 예전에는 '마지막 프레임 시각'을 콜백마다 갱신해서,
    // 게임 루프·ESP 등 여러 콜백이 서로를 다음 프레임으로 계속 미루다 게임이 아예 멈췄습니다(검은 화면).
    let rrFrameCap = 60, rrLastAllowed = -1e9, rrDecisionT = -1, rrAllowed = true, rrNextId = 1;
    const rrRaf = window.requestAnimationFrame.bind(window);
    const rrCaf = window.cancelAnimationFrame.bind(window);
    const rrLive = new Map(); // 우리 id -> 지금 걸려 있는 실제 id (미룬 콜백도 취소되게)
    function rrAllow(t) {
        if (rrFrameCap <= 0) return true;
        if (t !== rrDecisionT) {
            rrDecisionT = t;
            rrAllowed = t - rrLastAllowed >= 1000 / rrFrameCap - 1;
            if (rrAllowed) rrLastAllowed = t;
        }
        return rrAllowed;
    }
    window.requestAnimationFrame = function (cb) {
        const id = rrNextId++;
        const step = function (t) {
            if (!rrAllow(t)) { rrLive.set(id, rrRaf(step)); return; }
            rrLive.delete(id);
            cb(t);
        };
        rrLive.set(id, rrRaf(step));
        return id;
    };
    window.cancelAnimationFrame = function (id) {
        const real = rrLive.get(id);
        if (real !== undefined) { rrCaf(real); rrLive.delete(id); }
    };

    function handleHostCommand(msg) {
        switch (msg.type) {
            case 'setFrameRate':
                rrFrameCap = Math.max(0, Number(msg.fps) || 0);
                break;
            case 'setSpeed':
                bridge.speed = Number(msg.speed) || 1.0;
                break;
            case 'setPause':
                bridge.paused = !!msg.paused;
                break;
            case 'togglePause':
                bridge.paused = !bridge.paused;
                break;
            case 'toggleNoclip':
                if (typeof $gamePlayer !== 'undefined' && $gamePlayer) {
                    $gamePlayer._through = (msg.through !== undefined && msg.through !== null) ? !!msg.through : !$gamePlayer._through;
                }
                break;
            case 'warp':
                if (typeof $gamePlayer !== 'undefined' && $gamePlayer && $gamePlayer.reserveTransfer) {
                    const dir = msg.direction || ($gamePlayer.direction ? $gamePlayer.direction() : 2);
                    $gamePlayer.reserveTransfer(Number(msg.mapId), Number(msg.x), Number(msg.y), dir, 0);
                    if (typeof SceneManager !== 'undefined' && typeof Scene_Map !== 'undefined' && SceneManager._scene && !(SceneManager._scene instanceof Scene_Map)) {
                        SceneManager.goto(Scene_Map);
                    }
                    const warpMsg = '맵 ' + msg.mapId + ' (' + msg.x + ', ' + msg.y + ') 워프 완료';
                    showToast(warpMsg);
                }
                break;
            case 'quickSave':
                performQuickSave();
                break;
            case 'quickLoad':
                performQuickLoad();
                break;
            case 'forceSaveMenu':
                if (typeof SceneManager !== 'undefined' && typeof Scene_Save !== 'undefined') {
                    if (typeof $gameSystem !== 'undefined' && $gameSystem.enableSave) {
                        $gameSystem.enableSave();
                    }
                    if (!SceneManager._scene || !(SceneManager._scene instanceof Scene_Save)) {
                        SceneManager.push(Scene_Save);
                        const fsMsg = '강제 세이브 메뉴 열림';
                        showToast(fsMsg);
                    }
                }
                break;
            case 'forceLoadMenu':
                if (typeof SceneManager !== 'undefined' && typeof Scene_Load !== 'undefined') {
                    if (!SceneManager._scene || !(SceneManager._scene instanceof Scene_Load)) {
                        SceneManager.push(Scene_Load);
                        const flMsg = '강제 로드 메뉴 열림';
                        showToast(flMsg);
                    }
                }
                break;
            case 'setVolume':
                applyVolume(Number(msg.volume));
                break;
            case 'setSwitch':
                if (typeof $gameSwitches !== 'undefined' && $gameSwitches) {
                    $gameSwitches.setValue(Number(msg.id), !!msg.value);
                }
                break;
            case 'setVariable':
                if (typeof $gameVariables !== 'undefined' && $gameVariables) {
                    let val = msg.value;
                    if (!isNaN(val) && val !== '' && val !== null) val = Number(val);
                    $gameVariables.setValue(Number(msg.id), val);
                }
                break;
            case 'freezeSwitch':
                if (msg.frozen) {
                    bridge.frozenSwitches[msg.id] = !!msg.value;
                    if (typeof $gameSwitches !== 'undefined') $gameSwitches.setValue(Number(msg.id), !!msg.value);
                } else {
                    delete bridge.frozenSwitches[msg.id];
                }
                break;
            case 'freezeVariable':
                if (msg.frozen) {
                    let fval = msg.value;
                    if (!isNaN(fval) && fval !== '' && fval !== null) fval = Number(fval);
                    bridge.frozenVariables[msg.id] = fval;
                    if (typeof $gameVariables !== 'undefined') $gameVariables.setValue(Number(msg.id), fval);
                } else {
                    delete bridge.frozenVariables[msg.id];
                }
                break;
            case 'enableEsp':
                bridge.espEnabled = !!msg.enabled;
                /* 호스트가 알림을 띄움 */
                break;
            case 'enableTileInspector':
                bridge.tileInspectorEnabled = !!msg.enabled;
                /* 호스트가 알림을 띄움 */
                break;
            case 'requestDataInspector':
                sendDataInspector();
                break;
            case 'setFont':
                bridge.font = { family: msg.family || '', size: Number(msg.size) || 0, bold: !!msg.bold };
                applyFont();
                break;
            case 'setBrightness':
                bridge.brightness = Math.max(0.05, Math.min(4.0, Number(msg.value) || 1.0));
                applyBrightness();
                break;
            case 'setAutoMessage':
                bridge.autoMessage = !!msg.enabled;
                bridge.autoSpeed = Number(msg.speed) || 1.0;
                break;
            case 'setSkipMessage':
                bridge.skipMessage = !!msg.enabled;
                break;
            case 'advanceMessage':
                advanceMessage();
                break;
            case 'setFilter':
                bridge.filter = String(msg.name || 'none');
                break;
            case 'restart':
                window.location.reload();
                break;
        }
    }

    function applyVolume(vol) {
        try {
            if (typeof AudioManager !== 'undefined') {
                AudioManager.masterVolume = vol;
            }
            if (typeof WebAudio !== 'undefined' && WebAudio.setMasterVolume) {
                WebAudio.setMasterVolume(vol);
            }
        } catch (e) { }
    }

    function performQuickSave() {
        try {
            if (typeof $gameSystem !== 'undefined' && typeof DataManager !== 'undefined') {
                $gameSystem.onBeforeSave();
                const res = DataManager.saveGame(1);
                if (res && typeof res.then === 'function') {
                    res.then(() => {
                        if (typeof SoundManager !== 'undefined') SoundManager.playSave();
                        const sMsg = '1번 슬롯에 퀵 세이브 완료';
                        showToast(sMsg);
                    }).catch(() => {
                        if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
                        const eMsg = '퀵 세이브 실패';
                        showToast(eMsg);
                    });
                } else if (res) {
                    if (typeof StorageManager !== 'undefined' && StorageManager.cleanBackup) StorageManager.cleanBackup(1);
                    if (typeof SoundManager !== 'undefined') SoundManager.playSave();
                    const sMsg = '1번 슬롯에 퀵 세이브 완료';
                    showToast(sMsg);
                } else {
                    if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
                    const eMsg = '퀵 세이브 실패';
                    showToast(eMsg);
                }
            }
        } catch (e) {
            console.error('[RocketBridge] QuickSave error:', e);
            if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
            const eMsg = '퀵 세이브 오류: ' + (e.message || e);
            showToast(eMsg);
        }
    }

    function performQuickLoad() {
        try {
            if (typeof DataManager !== 'undefined') {
                const res = DataManager.loadGame(1);
                if (res && typeof res.then === 'function') {
                    res.then(() => {
                        if (typeof SoundManager !== 'undefined') SoundManager.playLoad();
                        if (typeof SceneManager !== 'undefined') SceneManager.goto(Scene_Map);
                        if (typeof $gameSystem !== 'undefined') $gameSystem.onAfterLoad();
                        const lMsg = '1번 슬롯 퀵 로드 완료';
                        showToast(lMsg);
                    }).catch(() => {
                        if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
                        const eMsg = '1번 슬롯에 저장 데이터가 없습니다';
                        showToast(eMsg);
                    });
                } else if (res) {
                    if (typeof SoundManager !== 'undefined') SoundManager.playLoad();
                    if (typeof SceneManager !== 'undefined') SceneManager.goto(Scene_Map);
                    if (typeof $gameSystem !== 'undefined') $gameSystem.onAfterLoad();
                    const lMsg = '1번 슬롯 퀵 로드 완료';
                    showToast(lMsg);
                } else {
                    if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
                    const eMsg = '1번 슬롯에 저장 데이터가 없습니다';
                    showToast(eMsg);
                }
            }
        } catch (e) {
            console.error('[RocketBridge] QuickLoad error:', e);
            if (typeof SoundManager !== 'undefined') SoundManager.playBuzzer();
            const eMsg = '퀵 로드 오류: ' + (e.message || e);
            showToast(eMsg);
        }
    }

    function enforceFrozen() {
        if (typeof $gameSwitches !== 'undefined' && $gameSwitches && $gameSwitches.setValue && $gameSwitches.value) {
            for (let id in bridge.frozenSwitches) {
                if (Object.prototype.hasOwnProperty.call(bridge.frozenSwitches, id)) {
                    const numId = Number(id);
                    const target = bridge.frozenSwitches[id];
                    if ($gameSwitches.value(numId) !== target) {
                        $gameSwitches.setValue(numId, target);
                    }
                }
            }
        }
        if (typeof $gameVariables !== 'undefined' && $gameVariables && $gameVariables.setValue && $gameVariables.value) {
            for (let id in bridge.frozenVariables) {
                if (Object.prototype.hasOwnProperty.call(bridge.frozenVariables, id)) {
                    const numId = Number(id);
                    const target = bridge.frozenVariables[id];
                    if ($gameVariables.value(numId) !== target) {
                        $gameVariables.setValue(numId, target);
                    }
                }
            }
        }
    }

    // 2-1. 인게임 글꼴: MV(standardFontFace/Size), MZ(Game_System.mainFontFace/Size) 훅
    function hookFont() {
        if (typeof Window_Base === 'undefined') return false;
        if (Window_Base.prototype.__rocketFont) return true;
        Window_Base.prototype.__rocketFont = true;
        const wrapFace = function(orig) {
            return function() {
                const f = bridge.font;
                const base = orig.call(this);
                return (f && f.family) ? ('\'' + f.family + '\', ' + base) : base;
            };
        };
        const wrapSize = function(orig) {
            return function() {
                const f = bridge.font;
                return (f && f.size > 0) ? f.size : orig.call(this);
            };
        };
        const wb = Window_Base.prototype;
        if (typeof wb.standardFontFace === 'function') wb.standardFontFace = wrapFace(wb.standardFontFace);
        if (typeof wb.standardFontSize === 'function') wb.standardFontSize = wrapSize(wb.standardFontSize);
        if (typeof Game_System !== 'undefined') {
            const gs = Game_System.prototype;
            if (typeof gs.mainFontFace === 'function') gs.mainFontFace = wrapFace(gs.mainFontFace);
            if (typeof gs.mainFontSize === 'function') gs.mainFontSize = wrapSize(gs.mainFontSize);
        }
        if (typeof wb.resetFontSettings === 'function') {
            const reset = wb.resetFontSettings;
            wb.resetFontSettings = function() {
                reset.call(this);
                const f = bridge.font;
                if (f && f.bold && this.contents) this.contents.fontBold = true;
            };
        }
        return true;
    }

    function applyFont() {
        hookFont();
        try {
            const f = bridge.font;
            if (f && f.family && document.fonts && document.fonts.load) {
                document.fonts.load('16px \'' + f.family + '\'');
            }
        } catch (e) { }
    }

    // 2-2. 밝기: 게임 캔버스에 CSS 필터 적용 (WebView는 WPF 쉐이더가 닿지 않음)
    function applyBrightness() {
        const canvas = (typeof Graphics !== 'undefined' && Graphics._canvas) ? Graphics._canvas : document.querySelector('canvas');
        if (!canvas) return;
        const want = Math.abs(bridge.brightness - 1.0) < 0.001 ? '' : 'brightness(' + bridge.brightness + ')';
        if (canvas.style.filter !== want) canvas.style.filter = want;
    }

    // 2-3. 메시지 자동 넘김 / 고속 스킵 / 대화 감지
    function autoWaitMs() {
        const len = (typeof $gameMessage !== 'undefined' && $gameMessage && $gameMessage.allText) ? $gameMessage.allText().length : 20;
        const raw = Math.min(4500, Math.max(750, 1100 + len * 30));
        return raw / Math.max(0.2, bridge.autoSpeed || 1.0);
    }

    function hookMessage() {
        if (typeof Window_Message === 'undefined') return false;
        const wm = Window_Message.prototype;
        if (wm.__rocketMsg) return true;
        wm.__rocketMsg = true;

        if (typeof wm.startPause === 'function') {
            const origPause = wm.startPause;
            wm.startPause = function() {
                bridge.msgWaitStart = performance.now();
                return origPause.apply(this, arguments);
            };
        }
        if (typeof wm.isTriggered === 'function') {
            const origTrig = wm.isTriggered;
            wm.isTriggered = function() {
                if (bridge.forceAdvance) { bridge.forceAdvance = false; return true; }
                if (bridge.skipMessage) return true;
                if (bridge.autoMessage && this.pause && bridge.msgWaitStart &&
                    performance.now() - bridge.msgWaitStart >= autoWaitMs()) {
                    bridge.msgWaitStart = 0;
                    bridge.lastAutoOk = performance.now();
                    return true;
                }
                return origTrig.call(this);
            };
        }
        if (typeof wm.updateShowFast === 'function') {
            const origFast = wm.updateShowFast;
            wm.updateShowFast = function() {
                origFast.call(this);
                if (bridge.skipMessage) this._showFast = true;
            };
        }
        return true;
    }

    // 메시지 플러그인이 Window_Message를 바꿔 둔 게임에서도 자동 진행/스킵이 되도록, 입력 단계에서도 확인을 넣습니다.
    // $gameMessage에 글이 떠 있고 선택지/숫자 입력이 아닐 때만. 표준 창 훅이 이미 넘겼으면 끼어들지 않습니다.
    // 우리 감싸기(브릿지/자동 테스트)는 감싼 대상(__rrWraps)과 주인(__rrOwner)을 기록합니다. 사슬에 내 것이 남아 있으면 다시
    // 감싸지 않습니다 (서로를 '바뀐 것'으로 보고 번갈아 감싸면 호출이 끝없이 깊어져 게임이 멈췄음).
    function rrChainHas(fn, owner) {
        for (let i = 0; fn && i < 40; i++) { if (fn.__rrOwner === owner) return true; fn = fn.__rrWraps; }
        return false;
    }
    function rrWrap(obj, name, owner, make) {
        const cur = obj[name];
        if (typeof cur !== 'function' || rrChainHas(cur, owner)) return;
        const w = make(cur);
        w.__rrOwner = owner;
        w.__rrWraps = cur;
        obj[name] = w;
    }
    function msgWantsOk() {
        if (typeof $gameMessage === 'undefined' || !$gameMessage || !$gameMessage.hasText || !$gameMessage.hasText()) return false;
        if (($gameMessage.isChoice && $gameMessage.isChoice()) || ($gameMessage.isNumberInput && $gameMessage.isNumberInput()) ||
            ($gameMessage.isItemChoice && $gameMessage.isItemChoice())) return false;
        if (bridge.skipMessage) return true;
        if (!bridge.autoMessage) return false;
        const since = Math.max(bridge.msgShownAt || 0, bridge.lastAutoOk || 0);
        if (performance.now() - since < autoWaitMs() * 1.5) return false;
        bridge.lastAutoOk = performance.now();
        return true;
    }
    function hookInputAuto() {
        if (typeof Input === 'undefined' || !Input.isTriggered) return false;
        rrWrap(Input, 'isTriggered', 'bridge', function(t) { return function(k) { return (k === 'ok' && msgWantsOk()) || t.apply(this, arguments); }; });
        rrWrap(Input, 'isRepeated', 'bridge', function(r) { return function(k) { return (k === 'ok' && bridge.skipMessage && msgWantsOk()) || r.apply(this, arguments); }; });
        return true;
    }
    function advanceMessage() {
        bridge.forceAdvance = true;
    }

    // 대사 기록용: \C[n], \N[n], \{, \. 같은 제어 문자를 제거하고 \N[n]/\V[n]은 실제 값으로 치환
    function cleanMessageText(raw) {
        let t = String(raw || '');
        try {
            t = t.replace(/\\V\[(\d+)\]/gi, function(_, n) { return (typeof $gameVariables !== 'undefined') ? String($gameVariables.value(Number(n))) : ''; });
            t = t.replace(/\\N\[(\d+)\]/gi, function(_, n) {
                const a = (typeof $gameActors !== 'undefined') ? $gameActors.actor(Number(n)) : null;
                return a ? a.name() : '';
            });
            t = t.replace(/\\P\[(\d+)\]/gi, function(_, n) {
                const a = (typeof $gameParty !== 'undefined') ? $gameParty.members()[Number(n) - 1] : null;
                return a ? a.name() : '';
            });
            t = t.replace(/\\G/gi, (typeof TextManager !== 'undefined' && TextManager.currencyUnit) ? TextManager.currencyUnit : '');
        } catch (e) { }
        t = t.replace(/\\[A-Za-z]+\[[^\]]*\]/g, '').replace(/\\[A-Za-z]+<[^>]*>/g, '').replace(/\\[{}.|!><^$]/g, '').replace(/\\\\/g, '\\');
        return t;
    }

    // 선택지 투표(멀티): 선택지 창이 떠 있는 동안 글과 고를 수 있는지를 알림 (바뀔 때만)
    let lastChoiceKey = '', choiceGen = 0;
    function choiceWindow() {
        const s = (typeof SceneManager !== 'undefined') ? SceneManager._scene : null;
        if (!s) return null;
        return s._choiceListWindow || (s._messageWindow && s._messageWindow._choiceWindow) || null;
    }
    // WebGL 화면을 잃으면(그래픽 메모리 부족, GPU 재시작) 알림. MV/MZ는 스스로 되살리지 못해 화면이 깨집니다.
    function watchContextLoss() {
        const c = (typeof Graphics !== 'undefined' && Graphics._canvas) ? Graphics._canvas : null;
        if (!c || c.__rrCtxWatch) return;
        c.__rrCtxWatch = true;
        c.addEventListener('webglcontextlost', function () { sendToHost('contextLost', {}); });
    }

    function pollChoiceState() {
        if (typeof $gameMessage === 'undefined' || !$gameMessage || !$gameMessage.isChoice) return;
        const w = choiceWindow();
        const open = !!($gameMessage.isChoice() && w && (w.active || (w.isOpen && w.isOpen())));
        let items = [];
        if (open) {
            const list = $gameMessage.choices() || [];
            items = list.slice(0, 16).map(function (c, i) {
                let enabled = true;
                try { if (w._list && w._list[i] && w._list[i].enabled === false) enabled = false; } catch (e) { }
                return { t: cleanMessageText(String(c)).replace(/\n/g, ' '), e: enabled };
            });
        }
        const key = open ? JSON.stringify(items) : '';
        if (key === lastChoiceKey) return;
        let picked = -1;
        if (!open) { try { picked = (typeof $gameMessage._choiceIndexResult === 'number') ? $gameMessage._choiceIndexResult : -1; } catch (e) { } }
        if (open) choiceGen++;
        else if (!lastChoiceKey) return;
        lastChoiceKey = key;
        sendToHost('choiceState', { open: open, gen: choiceGen, items: items, picked: picked });
    }

    let lastMsgText = null;
    function pollMessageState() {
        if (typeof $gameMessage === 'undefined' || !$gameMessage) return;
        const busy = !!($gameMessage.hasText && $gameMessage.hasText());
        const text = busy && $gameMessage.allText ? cleanMessageText($gameMessage.allText()) : '';
        const speaker = (busy && typeof $gameMessage.speakerName === 'function') ? ($gameMessage.speakerName() || '') : '';
        if (busy !== bridge.msgBusy || text !== lastMsgText) {
            if (busy) bridge.msgShownAt = performance.now();
            bridge.msgBusy = busy;
            lastMsgText = text;
            sendToHost('messageState', { busy: busy, speaker: speaker, text: text });
        }
    }

    // 2-4. ESP: 단일 2D 캔버스에 그림 (DOM 재생성 없음), 화면 줌/스케일 추종
    let espCanvas = null;
    let espCtx = null;
    function ensureEspCanvas() {
        ensureOverlay();
        if (!espCanvas && espContainer) {
            espCanvas = document.createElement('canvas');
            espCanvas.style.cssText = 'position:absolute;top:0;left:0;width:100%;height:100%;pointer-events:none;';
            espContainer.appendChild(espCanvas);
            espCtx = espCanvas.getContext('2d');
        }
        return espCtx;
    }

    function clearEsp() {
        if (espCtx && espCanvas) espCtx.clearRect(0, 0, espCanvas.width, espCanvas.height);
    }

    const espColors = ['#0099ff', '#00cc44', '#00cc44', '#ff8800', '#bb33ff'];
    function drawEsp() {
        requestAnimationFrame(drawEsp);
        // 오버레이 오류가 게임 오류로 번지지 않게 합니다.
        try { drawEspFrame(); } catch (e) { clearEsp(); }
    }
    function drawEspFrame() {
        if (!bridge.espEnabled) { clearEsp(); return; }
        // 맵 이동 중에는 $dataMap이 잠깐 null — 이때 이벤트 화면 좌표를 구하면 게임 함수가 예외를 내고, 그게 게임 오류로 잡히던 문제
        if (typeof $gameMap === 'undefined' || !$gameMap || !$gameMap.events || !window.$dataMap) { clearEsp(); return; }
        if (typeof SceneManager === 'undefined' || typeof Scene_Map === 'undefined' || !(SceneManager._scene instanceof Scene_Map)) { clearEsp(); return; }
        const canvas = (typeof Graphics !== 'undefined' && Graphics._canvas) ? Graphics._canvas : document.querySelector('canvas');
        const ctx = ensureEspCanvas();
        if (!canvas || !ctx) return;

        const dpr = window.devicePixelRatio || 1;
        const cw = Math.round(window.innerWidth * dpr), ch = Math.round(window.innerHeight * dpr);
        if (espCanvas.width !== cw || espCanvas.height !== ch) { espCanvas.width = cw; espCanvas.height = ch; }
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, cw, ch);

        const rect = canvas.getBoundingClientRect();
        const gw = Graphics.width || canvas.width || 816;
        const gh = Graphics.height || canvas.height || 624;
        const sx = rect.width / gw, sy = rect.height / gh;
        const tw = $gameMap.tileWidth ? $gameMap.tileWidth() : 48;
        const th = $gameMap.tileHeight ? $gameMap.tileHeight() : 48;

        // $gameScreen 줌 반영: 화면 좌표 p → zoomX + (p - zoomX) * scale
        let zs = 1, zx = 0, zy = 0;
        if (typeof $gameScreen !== 'undefined' && $gameScreen && $gameScreen.zoomScale) {
            zs = $gameScreen.zoomScale() || 1;
            zx = $gameScreen.zoomX();
            zy = $gameScreen.zoomY();
        }

        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.save();
        ctx.beginPath();
        ctx.rect(rect.left, rect.top, rect.width, rect.height);
        ctx.clip();
        ctx.lineWidth = 2;
        ctx.font = '600 10px Malgun Gothic, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';

        const events = $gameMap.events();
        for (let i = 0; i < events.length; i++) {
            const ev = events[i];
            if (!ev || typeof ev.screenX !== 'function') continue;
            const ex = zx + (ev.screenX() - tw / 2 - zx) * zs;
            const ey = zy + (ev.screenY() - th - zy) * zs;
            const x = rect.left + ex * sx, y = rect.top + ey * sy;
            const w = tw * zs * sx, h = th * zs * sy;
            if (x + w < rect.left || y + h < rect.top || x > rect.right || y > rect.bottom) continue;

            const trig = (typeof ev._trigger === 'number') ? ev._trigger : 0;
            const color = espColors[trig] || '#00ffff';
            ctx.strokeStyle = color;
            ctx.fillStyle = color + '33';
            ctx.fillRect(x, y, w, h);
            ctx.strokeRect(x + 1, y + 1, w - 2, h - 2);

            const data = ev.event ? ev.event() : null;
            const name = data && data.name ? data.name : ('EV' + ev.eventId());
            const tw2 = ctx.measureText(name).width + 6;
            ctx.fillStyle = 'rgba(0,0,0,0.75)';
            ctx.fillRect(x + w / 2 - tw2 / 2, y - 14, tw2, 13);
            ctx.fillStyle = '#ffffff';
            ctx.fillText(name, x + w / 2, y - 2);
        }
        ctx.restore();
    }
    requestAnimationFrame(drawEsp);

    // 2-4b. 화면 필터: PIXI 필터(GLSL)로 게임 장면에 직접 적용 (CRT 주사선/마스크, 선명도 보정)
    //   PIXI v4(MV)는 filterArea, v5(MZ)는 inputSize 기본 유니폼으로 픽셀 좌표를 계산합니다.
    const filterCache = {};
    function pixiMajor() {
        try { return parseInt(String(PIXI.VERSION).split('.')[0], 10) || 4; } catch (e) { return 4; }
    }
    function makeFilter(name) {
        if (typeof PIXI === 'undefined' || !PIXI.Filter) return null;
        const v5 = pixiMajor() >= 5;
        const head = [
            // v5 기본 버텍스 셰이더의 inputSize(highp)와 정밀도가 달라지면 링크 오류가 나므로 highp로 맞춥니다.
            'precision highp float;',
            'varying vec2 vTextureCoord;',
            'uniform sampler2D uSampler;',
            v5 ? 'uniform vec4 inputSize;' : 'uniform vec4 filterArea;',
            v5 ? 'vec2 pixelPos() { return vTextureCoord * inputSize.xy; }' : 'vec2 pixelPos() { return vTextureCoord * filterArea.xy; }',
            v5 ? 'vec2 texel() { return inputSize.zw; }' : 'vec2 texel() { return 1.0 / filterArea.xy; }'
        ];
        let body;
        if (name === 'crt_royale') {
            body = [
                'void main(void) {',
                '  vec4 c = texture2D(uSampler, vTextureCoord);',
                '  vec2 p = pixelPos();',
                '  float scan = mod(floor(p.y), 2.0) < 1.0 ? 1.0 : 0.82;',
                '  float m = mod(floor(p.x), 3.0);',
                '  vec3 mask = m < 1.0 ? vec3(1.05, 0.97, 0.97) : (m < 2.0 ? vec3(0.97, 1.05, 0.97) : vec3(0.97, 0.97, 1.05));',
                '  gl_FragColor = vec4(c.rgb * scan * mask * 1.06, c.a);',
                '}'
            ];
        } else {
            // xbrz_cas / fsr_cas / scalefx_cas / quality → CAS 계열 선명도 보정 (게임 해상도에서 적용)
            body = [
                'void main(void) {',
                '  vec2 t = texel();',
                '  vec4 c = texture2D(uSampler, vTextureCoord);',
                '  vec3 n = texture2D(uSampler, vTextureCoord + vec2(0.0, -t.y)).rgb;',
                '  vec3 s = texture2D(uSampler, vTextureCoord + vec2(0.0, t.y)).rgb;',
                '  vec3 e = texture2D(uSampler, vTextureCoord + vec2(t.x, 0.0)).rgb;',
                '  vec3 w = texture2D(uSampler, vTextureCoord + vec2(-t.x, 0.0)).rgb;',
                '  vec3 mn = min(c.rgb, min(min(n, s), min(e, w)));',
                '  vec3 mx = max(c.rgb, max(max(n, s), max(e, w)));',
                '  vec3 amp = clamp(min(mn, 1.0 - mx) / max(mx, 0.0001), 0.0, 1.0);',
                '  vec3 k = -sqrt(amp) * 0.18;',
                '  vec3 r = (c.rgb + k * (n + s + e + w)) / (1.0 + 4.0 * k);',
                '  gl_FragColor = vec4(clamp(r, 0.0, 1.0), c.a);',
                '}'
            ];
        }
        try { return new PIXI.Filter(null, head.concat(body).join('\n')); }
        catch (e) { console.error('[RocketBridge] filter', e); return null; }
    }
    function applyFilter() {
        if (typeof SceneManager === 'undefined' || !SceneManager._scene) return;
        const scene = SceneManager._scene;
        const want = bridge.filter || 'none';
        if (want !== 'none' && !(want in filterCache)) {
            filterCache[want] = makeFilter(want);
            if (filterCache[want]) filterCache[want].__rr = true;
        }
        const f = want === 'none' ? null : filterCache[want];
        const cur = scene.filters || [];
        // 게임/플러그인이 장면에 건 필터는 유지하고 RocketRPG 필터만 교체합니다 (장면이 filters를 다시 설정해도 복구).
        if (f ? (cur.length && cur[cur.length - 1] === f) : !cur.some(x => x && x.__rr)) return;
        const keep = cur.filter(x => x && !x.__rr);
        if (f) keep.push(f);
        scene.filters = keep.length ? keep : null;
    }

    // 2-5. 게임 자체 오류 화면 감지 → 호스트가 원본 실행(Game.exe) 모드로 대체 실행
    function reportFatal(kind, name, message, stack) {
        if (bridge.fatalSent) return;
        bridge.fatalSent = true;
        sendToHost('fatal', { kind: kind, name: String(name || ''), message: String(message || ''), stack: String(stack || '').slice(0, 2000) });
    }
    function hookErrors() {
        if (typeof Graphics === 'undefined') return false;
        if (Graphics.__rocketErr) return true;
        Graphics.__rocketErr = true;
        if (typeof Graphics.printError === 'function') {
            const origPrint = Graphics.printError;
            Graphics.printError = function(name, message, error) {
                reportFatal('error', name, message, error && error.stack);
                return origPrint.apply(this, arguments);
            };
        }
        if (typeof Graphics.printLoadingError === 'function') {
            const origLoad = Graphics.printLoadingError;
            Graphics.printLoadingError = function(url) {
                reportFatal('load', 'LoadingError', url, '');
                return origLoad.apply(this, arguments);
            };
        }
        return true;
    }

    // 3. 핫키 캡처 (WebView2 브라우저 기본 동작 방지 및 호스트 전달)
    window.addEventListener('keydown', function(e) {
        const isRocketKey = (e.key === 'F5' || e.key === 'F8' || e.key === 'F2' || e.key === 'F3' || e.key === 'Pause') ||
                            (e.ctrlKey && ['s', 'S', 'l', 'L', 'p', 'P', 'r', 'R', 'q', 'Q', 'm', 'M', 'v', 'V', '0', 'ArrowUp', 'ArrowDown'].indexOf(e.key) !== -1);
        if (isRocketKey) {
            e.preventDefault();
            e.stopPropagation();
        }

        // 길게 누를 때 오는 반복 입력은 보내지 않습니다 (켜기/끄기가 계속 뒤집히던 문제)
        if (!e.repeat && window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({
                type: 'hotkey',
                key: e.key,
                code: e.code,
                ctrl: e.ctrlKey,
                alt: e.altKey,
                shift: e.shiftKey
            });
        }
    }, true);

    // 4. 마우스 위치 추적
    window.addEventListener('mousemove', function(e) {
        bridge.mousePos.x = e.clientX;
        bridge.mousePos.y = e.clientY;
    }, true);

    // 5. SceneManager 훅 (배속 및 일시정지)
    function hookSceneManager() {
        if (!window.SceneManager) return false;
        if (typeof SceneManager.determineRepeatNumber !== 'function' && typeof SceneManager.updateMain !== 'function') return false;
        if (window.SceneManager.__rocketHooked) return true;
        window.SceneManager.__rocketHooked = true;

        if (typeof SceneManager.determineRepeatNumber === 'function') {
            // MZ 스타일
            const origRepeat = SceneManager.determineRepeatNumber;
            let halfStep = false;
            SceneManager.determineRepeatNumber = function(deltaTime) {
                if (bridge.paused) return 0;
                if (bridge.speed === 0.5) {
                    halfStep = !halfStep;
                    return halfStep ? 1 : 0;
                }
                const base = origRepeat.call(this, deltaTime);
                return Math.max(1, Math.round(base * bridge.speed));
            };
        } else if (typeof SceneManager.updateMain === 'function') {
            // MV 스타일
            const origUpdateMain = SceneManager.updateMain;
            let halfStepMV = false;
            SceneManager.updateMain = function() {
                if (bridge.paused) {
                    this.renderScene();
                    this.requestUpdate();
                    return;
                }
                if (bridge.speed === 0.5) {
                    halfStepMV = !halfStepMV;
                    if (!halfStepMV) {
                        this.renderScene();
                        this.requestUpdate();
                        return;
                    }
                }
                const spd = bridge.speed || 1.0;
                if (spd > 1.0) {
                    const extra = Math.min(8, Math.round(spd)) - 1;
                    for (let i = 0; i < extra; i++) {
                        if (this._scene && !this.isSceneChanging() && typeof this.updateScene === 'function') {
                            this.updateInputData();
                            this.changeScene();
                            this.updateScene();
                        }
                    }
                }
                origUpdateMain.call(this);
            };
        }
        return true;
    }

    const initInterval = setInterval(function() {
        const a = hookSceneManager();
        const b = hookFont();
        const c = hookMessage();
        const d = hookErrors();
        if (a && b && c && d) {
            clearInterval(initInterval);
        }
    }, 100);

    // 6. 텔레메트리 전송 루프 (~60ms)
    setInterval(function() {
        try {
            enforceFrozen();

            // GameState
            const sceneName = (typeof SceneManager !== 'undefined' && SceneManager._scene)
                ? SceneManager._scene.constructor.name : '';
            const mapId = (typeof $gameMap !== 'undefined' && $gameMap && $gameMap.mapId)
                ? $gameMap.mapId() : 0;
            const px = (typeof $gamePlayer !== 'undefined' && $gamePlayer) ? $gamePlayer.x : 0;
            const py = (typeof $gamePlayer !== 'undefined' && $gamePlayer) ? $gamePlayer.y : 0;
            const dx = (typeof $gameMap !== 'undefined' && $gameMap && $gameMap.displayX)
                ? $gameMap.displayX() : 0;
            const dy = (typeof $gameMap !== 'undefined' && $gameMap && $gameMap.displayY)
                ? $gameMap.displayY() : 0;
            const noclip = (typeof $gamePlayer !== 'undefined' && $gamePlayer)
                ? !!$gamePlayer._through : false;

            const stateKey = sceneName + '|' + mapId + '|' + px + '|' + py + '|' + dx + '|' + dy + '|' + noclip;
            if (stateKey !== bridge.lastStateKey) {
                bridge.lastStateKey = stateKey;
                sendToHost('gameState', {
                    scene: sceneName,
                    mapId: mapId,
                    playerX: px,
                    playerY: py,
                    displayX: dx,
                    displayY: dy,
                    noclip: noclip
                });
            }

            const canvas = (typeof Graphics !== 'undefined' && Graphics._canvas)
                ? Graphics._canvas : document.querySelector('canvas');

            // ESP는 drawEsp()가 requestAnimationFrame으로 페이지 안에서 직접 그림
            applyBrightness();
            applyFilter();
            pollMessageState();
            pollChoiceState();
            watchContextLoss();
            hookInputAuto();

            // 타일 인스펙터 데이터 수집 및 인게임 DOM 렌더링
            if (bridge.tileInspectorEnabled && canvas && typeof $gameMap !== 'undefined' && $gameMap && $gameMap.canvasToMapX && window.$dataMap) {
                ensureOverlay();
                const rect = canvas.getBoundingClientRect();
                const mx = bridge.mousePos.x;
                const my = bridge.mousePos.y;
                if (mx >= rect.left && mx <= rect.right && my >= rect.top && my <= rect.bottom) {
                    const gw = (typeof Graphics !== 'undefined' && Graphics.width) ? Graphics.width : (canvas.width || 816);
                    const gh = (typeof Graphics !== 'undefined' && Graphics.height) ? Graphics.height : (canvas.height || 624);
                    const canvasX = (typeof Graphics !== 'undefined' && Graphics.pageToCanvasX)
                        ? Graphics.pageToCanvasX(mx)
                        : (mx - rect.left) * (gw / rect.width);
                    const canvasY = (typeof Graphics !== 'undefined' && Graphics.pageToCanvasY)
                        ? Graphics.pageToCanvasY(my)
                        : (my - rect.top) * (gh / rect.height);
                    const mapX = $gameMap.canvasToMapX(canvasX);
                    const mapY = $gameMap.canvasToMapY(canvasY);

                    let passable = false;
                    const mapW = ($gameMap && typeof $gameMap.width === 'function') ? $gameMap.width() : 0;
                    const mapH = ($gameMap && typeof $gameMap.height === 'function') ? $gameMap.height() : 0;
                    if (mapX >= 0 && mapY >= 0 && mapX < mapW && mapY < mapH) {
                        const canExit = [2, 4, 6, 8].some(function(d) { return $gameMap.isPassable(mapX, mapY, d); });
                        const canEnter = [2, 4, 6, 8].some(function(d) {
                            const rev = 10 - d;
                            const nx = (d === 6) ? mapX - 1 : (d === 4) ? mapX + 1 : mapX;
                            const ny = (d === 2) ? mapY - 1 : (d === 8) ? mapY + 1 : mapY;
                            if (nx < 0 || ny < 0 || nx >= mapW || ny >= mapH) return false;
                            return $gameMap.isPassable(nx, ny, d) && $gameMap.isPassable(mapX, mapY, rev);
                        });
                        let eventBlocked = false;
                        if ($gameMap.eventsXy) {
                            const evList = $gameMap.eventsXy(mapX, mapY) || [];
                            eventBlocked = evList.some(function(e) {
                                return e && !e.isThrough() && typeof e.isNormalPriority === 'function' && e.isNormalPriority();
                            });
                        }
                        passable = (canExit || canEnter) && !eventBlocked;
                    }
                    const tileIds = $gameMap.allTiles ? $gameMap.allTiles(mapX, mapY) : [];
                    const evList = $gameMap.eventsXy ? $gameMap.eventsXy(mapX, mapY) : [];
                    const evNames = [];
                    for (let j = 0; j < evList.length; j++) {
                        const e = evList[j];
                        if (e && e.event && e.event() && e.event().name) evNames.push(e.event().name);
                        else if (e) evNames.push('EV' + e.eventId());
                    }
                    const evStr = evNames.length > 0 ? evNames.join(', ') : '없음';

                    if (tileHudDiv) {
                        tileHudDiv.textContent = '[타일 (' + mapX + ', ' + mapY + ') | 통과: ' + (passable ? 'O' : 'X') + ' | 이벤트: ' + evStr + ']';
                        tileHudDiv.style.display = 'block';
                    }

                    sendToHost('tileInfo', {
                        mapX: mapX,
                        mapY: mapY,
                        passable: passable,
                        tileIds: tileIds,
                        events: evStr,
                        screenX: mx,
                        screenY: my
                    });
                } else if (tileHudDiv && tileHudDiv.style.display !== 'none') {
                    tileHudDiv.style.display = 'none';
                }
            } else if (tileHudDiv && tileHudDiv.style.display !== 'none') {
                tileHudDiv.style.display = 'none';
            }
        } catch (e) { }
    }, 60);

    function sendDataInspector() {
        if (typeof $dataSystem === 'undefined' || !$dataSystem) return;
        const switches = [];
        if ($dataSystem.switches && typeof $gameSwitches !== 'undefined') {
            for (let i = 1; i < $dataSystem.switches.length; i++) {
                switches.push({
                    id: i,
                    name: $dataSystem.switches[i] || '',
                    val: !!$gameSwitches.value(i),
                    frozen: Object.prototype.hasOwnProperty.call(bridge.frozenSwitches, i)
                });
            }
        }
        const variables = [];
        if ($dataSystem.variables && typeof $gameVariables !== 'undefined') {
            for (let i = 1; i < $dataSystem.variables.length; i++) {
                const v = $gameVariables.value(i);
                variables.push({
                    id: i,
                    name: $dataSystem.variables[i] || '',
                    val: (v !== undefined && v !== null) ? String(v) : '0',
                    frozen: Object.prototype.hasOwnProperty.call(bridge.frozenVariables, i)
                });
            }
        }
        sendToHost('dataInspectorData', { switches: switches, variables: variables });
    }
})();
";
}
