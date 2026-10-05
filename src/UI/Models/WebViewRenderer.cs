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
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ResourceText("RocketRPG.Resources.rocket_mv_bridge.js"));
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
    public void ExtraSummon() => PostCommand(new { type = "extra", op = "summon" });

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
}
