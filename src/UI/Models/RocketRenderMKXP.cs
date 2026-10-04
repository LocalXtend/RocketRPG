#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace RocketRPG.Models;

/// <summary>
/// RocketRenderMKXP: High-performance data-oriented native rendering backend for RPG Maker XP / VX / VX Ace.
/// Directly executes RGSS1/2/3 games via mkxp-z (Ruby 3.x, Win32API/FFI compatible) and delivers native
/// hardware-accelerated frames to WPF RenderScreen (direct native surface hosting or shared texture).
/// Completely replaces the legacy PrintWindow/BitBlt OS window capture loop.
/// </summary>
public class RocketRenderMKXP : IGameBridge, IDisposable
{
    private IntPtr _nativeInstance = IntPtr.Zero;
    private RubyBridge? _rubyBridge;
    private string _gameDir = "";
    private int _engine = CoreInterop.EngineUnknown;
    private int _processId = 0;
    private IntPtr _gameHwnd = IntPtr.Zero;
    private IntPtr _parentHwnd = IntPtr.Zero;

    private readonly DispatcherTimer _framePumpTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private Action<int, int, byte[]>? _frameCallback;

    public bool IsRunning { get; private set; }
    public double CurrentSpeed { get; private set; } = 1.0;
    public bool IsPaused { get; private set; }
    public bool IsNoclip => _rubyBridge?.IsNoclip ?? false;
    public bool EspEnabled { get; private set; }
    public bool TileInspectorEnabled { get; private set; }
    public GameState LatestState => _rubyBridge?.LatestState ?? new();

    // 게임이 Graphics.resize_screen 등으로 해상도를 바꾸면 에이전트가 알려준 값을 사용합니다.
    public int BaseWidth => _screenW > 0 ? _screenW : ((_engine == CoreInterop.EngineXp) ? 640 : 544);
    public int BaseHeight => _screenH > 0 ? _screenH : ((_engine == CoreInterop.EngineXp) ? 480 : 416);
    public int BaseFps => (_engine == CoreInterop.EngineXp) ? 40 : 60;
    public double TileDisplayScale => (_engine == CoreInterop.EngineXp) ? 128.0 : 256.0;

    public IntPtr GameHwnd => _gameHwnd;
    /// <summary>게임 창을 품고 있는 RocketRPG 쪽 호스트 창</summary>
    public IntPtr ParentHwnd => _parentHwnd;
    public int ProcessId => _processId;
    public RubyBridge? InnerRubyBridge => _rubyBridge;

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

    private void OnProcessExited()
    {
        if (!IsRunning) return;
        UiLog.Write($"RocketRenderMKXP: game process {_processId} exited unexpectedly");
        Stop();
        ProcessExited?.Invoke();
    }

    /// <summary>
    /// 게임이 자체 Win32 DLL(동영상 재생, 비트맵 가속 등)을 Win32API로 부르면 64비트 mkxp-z에서는 불러올 수 없습니다.
    /// 대부분은 그 기능만 빠지고 실행되므로 기록만 남깁니다.
    /// </summary>
    public static List<string> FindIncompatibleDlls(string gameDir)
    {
        var result = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(gameDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith("RGSS", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(name);
            }
        }
        catch { }
        return result;
    }

    /// <summary>시작 직후 mkxp-z가 스크립트 오류 대화상자를 띄우면 발생 (오류 안내용)</summary>
    public event Action<string>? StartupScriptError;

    private async Task WatchStartupErrorsAsync(int pid)
    {
        for (int i = 0; i < 30 && IsRunning && _processId == pid; i++)
        {
            await Task.Delay(500);
            string? text = NativeDialogText.Find(pid);
            if (!string.IsNullOrEmpty(text))
            {
                UiLog.Write($"RocketRenderMKXP: startup error dialog: {text.Replace('\n', ' ')}");
                StartupScriptError?.Invoke(text);
                return;
            }
            if (_rubyBridge?.LatestState.MapId > 0) return; // 맵까지 진입했으면 정상 기동
        }
    }

    // ── mkxp-z 루비 에이전트 연동 ──

    private MkxpAgentChannel? _channel;
    private string _fontEnv = "";
    public event Action<MessageState>? MessageStateChanged;
    public event Action<ChoiceState>? ChoiceChanged;
    public bool SupportsMessageDetection => true;
    public bool AgentConnected => _channel?.Connected == true;

    /// <summary>mkxp-z 저장소 scripts/preload 의 호환 스크립트 (CC0). 일반 mkxp-z 배포판도 함께 씁니다.</summary>
    internal static readonly string[] CompatPreloadScripts = ["ruby_classic_wrap.rb", "mkxp_wrap.rb", "win32_wrap.rb"];

    private static string AgentScriptText() => ResourceText("RocketRPG.Resources.rocket_mkxp_agent.rb");

    private static string WriteResource(string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, ResourceText("RocketRPG.Resources." + fileName), new System.Text.UTF8Encoding(false));
        return path;
    }

    private static string ResourceText(string name)
    {
        using var s = typeof(RocketRenderMKXP).Assembly.GetManifestResourceStream(name);
        if (s == null) return "";
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        return r.ReadToEnd();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>
    /// mkxp-z는 시작 시 항상 실행 파일 폴더로 이동해 그곳의 mkxp.json을 읽습니다(SDL_GetBasePath).
    /// 게임마다 설정을 분리하고 설치 폴더/게임 폴더를 건드리지 않도록, 게임별 실행 폴더에 런타임을 하드링크(실패 시 복사)합니다.
    /// </summary>
    private static string MirrorRuntime(string runtimeExe, string dir)
    {
        string srcDir = Path.GetDirectoryName(runtimeExe)!;
        foreach (var src in Directory.GetFiles(srcDir))
        {
            string name = Path.GetFileName(src);
            if (name.Equals("mkxp.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
            string dst = Path.Combine(dir, name);
            var si = new FileInfo(src);
            var di = new FileInfo(dst);
            if (di.Exists && di.Length == si.Length && di.LastWriteTimeUtc == si.LastWriteTimeUtc) continue;
            try { if (di.Exists) File.Delete(dst); } catch { continue; }
            if (!CreateHardLinkW(dst, src, IntPtr.Zero))
            {
                try { File.Copy(src, dst, true); } catch (Exception ex) { UiLog.Write($"RocketRenderMKXP: runtime copy failed {name}: {ex.Message}"); }
            }
        }
        // 이전 버전이 런타임 폴더에 남긴 공용 설정은 다른 게임 설정과 섞이므로 제거
        foreach (var stale in new[] { "mkxp.json", "Game.ini" })
        {
            try { File.Delete(Path.Combine(srcDir, stale)); } catch { }
        }
        return Path.Combine(dir, Path.GetFileName(runtimeExe));
    }

    /// <summary>
    /// %LOCALAPPDATA%\RocketRPG\mkxp\&lt;게임키&gt;\ 에 런타임 미러, mkxp.json, 에이전트를 준비합니다. 게임 폴더에는 아무것도 쓰지 않습니다.
    /// </summary>
    private static string PrepareLaunchDirectory(string gameDir, int engine, bool fixedAspectRatio, bool smoothScaling, string? fontFamily, string? filter, bool vsync = false)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RocketRPG", "mkxp", GameSettingsService.ComputeSafeKey(gameDir));
        Directory.CreateDirectory(dir);

        // mkxp-z 공식 호환 프리로드(RGSS의 Ruby 1.8 동작, Win32API 오류 허용) → 에이전트 순서로 불러옵니다.
        var preload = new List<string>();
        foreach (var name in CompatPreloadScripts)
        {
            string text = ResourceText("RocketRPG.Resources.mkxp_preload." + name);
            if (text.Length == 0) continue;
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            preload.Add(path);
        }
        preload.Add(WriteResource(dir, "rocket_win32_fallbacks.rb"));
        bool rgss18 = engine is CoreInterop.EngineXp or CoreInterop.EngineVx;
        if (rgss18) preload.Add(WriteResource(dir, "rocket_rgss18_compat.rb"));
        string agentPath = Path.Combine(dir, "rocket_mkxp_agent.rb");
        File.WriteAllText(agentPath, AgentScriptText(), new System.Text.UTF8Encoding(false));
        preload.Add(agentPath);
        string autotestPath = Path.Combine(dir, "rocket_mkxp_autotest.rb");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RR_AUTOTEST")))
        {
            // 자동 호환성 테스트(scripts/mkxp_autotest.ps1)에서만 사용합니다.
            File.WriteAllText(autotestPath, ResourceText("RocketRPG.Resources.rocket_mkxp_autotest.rb"), new System.Text.UTF8Encoding(false));
            preload.Add(autotestPath);
        }
        else
        {
            try { File.Delete(autotestPath); } catch { }
        }
        // XP/VX: 게임 스크립트를 RocketRPG 로더가 실행합니다 (반드시 마지막 프리로드). rocket_rgss_loader.rb 참고.
        if (rgss18) preload.Add(WriteResource(dir, "rocket_rgss_loader.rb"));

        var rtp = RtpResolver.ResolveRgss(gameDir, engine);
        UiLog.Write($"RocketRenderMKXP: RTP = [{string.Join(", ", rtp)}]");
        string json = GenerateMkxpConfigJson(gameDir, engine, fixedAspectRatio, smoothScaling,
            gameFolder: Path.GetFullPath(gameDir), targetFontFamily: fontFamily,
            rtpDirs: rtp, preloadScripts: preload, scalingMode: RocketShaderSystem.ToMkxpScaling(filter), vsync: vsync);
        File.WriteAllText(Path.Combine(dir, "mkxp.json"), json, new System.Text.UTF8Encoding(false));
        return dir;
    }

    /// <summary>
    /// 테스트용: RocketRPG UI 없이 게임별 실행 폴더를 준비하고, 그 폴더의 mkxp-z 실행 파일 경로를 돌려줍니다.
    /// 실제 실행과 같은 mkxp.json/프리로드/런타임 미러를 사용합니다.
    /// </summary>
    public static string PrepareStandalone(string gameDir, int engine)
    {
        string exe = ResolveExecutable(gameDir);
        if (string.IsNullOrEmpty(exe)) return "";
        EnsureValidGameIni(gameDir, engine);
        string dir = PrepareLaunchDirectory(gameDir, engine, true, false, null, null);
        if (string.Equals(Path.GetDirectoryName(exe), Path.GetFullPath(gameDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return exe;
        return MirrorRuntime(exe, dir);
    }

    private static readonly object EnvLock = new();

    // 자식 프로세스는 부모 환경 변수를 상속하므로 시작 직전에만 설정하고 바로 지웁니다.
    private int StartWithAgentEnvironment()
    {
        lock (EnvLock)
        {
            int rgss = _engine switch { CoreInterop.EngineXp => 1, CoreInterop.EngineVx => 2, _ => 3 };
            Environment.SetEnvironmentVariable("RR_BRIDGE_PIPE", _channel?.PipeName);
            Environment.SetEnvironmentVariable("RR_RGSS", rgss.ToString());
            Environment.SetEnvironmentVariable("RR_FONT", _fontEnv);
            try
            {
                return CoreInterop.rpg_mkxp_start(_nativeInstance);
            }
            finally
            {
                Environment.SetEnvironmentVariable("RR_BRIDGE_PIPE", null);
                Environment.SetEnvironmentVariable("RR_RGSS", null);
                Environment.SetEnvironmentVariable("RR_FONT", null);
            }
        }
    }

    private int _screenW, _screenH;
    private int _lastEspCount = -1;
    private bool _vsync;
    private readonly EspView _espView = new();
    private List<EspItem>? _lastEsp;
    private bool _loggedTelemetry;

    private static readonly bool AgentDebug = Environment.GetEnvironmentVariable("RR_AGENT_DEBUG") == "1";

    private void OnAgentLine(string kind, string payload)
    {
        if (AgentDebug && kind is not ("T" or "E")) UiLog.Write($"agent<{kind}> {payload.Replace('\n', '|').Replace(MkxpAgentChannel.SepField, '¦')}");
        try
        {
            switch (kind)
            {
                case "T":
                    if (!_loggedTelemetry) { _loggedTelemetry = true; UiLog.Write($"RocketRenderMKXP: first telemetry {payload}"); }
                    _rubyBridge?.OnAgentTelemetry(payload);
                    using (var doc = JsonDocument.Parse(payload))
                    {
                        if (doc.RootElement.TryGetProperty("ScreenW", out var sw)) _screenW = sw.GetInt32();
                        if (doc.RootElement.TryGetProperty("ScreenH", out var sh)) _screenH = sh.GetInt32();
                    }
                    break;
                case "D":
                    _rubyBridge?.OnAgentData(payload);
                    break;
                case "M":
                    MessageStateChanged?.Invoke(MkxpAgentChannel.ParseMessage(payload));
                    break;
                case "Q":
                    ChoiceChanged?.Invoke(MkxpAgentChannel.ParseChoice(payload));
                    break;
                case "E":
                    var esp = MkxpAgentChannel.ParseEsp(payload, 32, out int ew, out int eh, _espView);
                    if (ew > 0) { _screenW = ew; _screenH = eh; }
                    _lastEsp = esp;
                    if (esp.Count != _lastEspCount)
                    {
                        _lastEspCount = esp.Count;
                        UiLog.Write($"RocketRenderMKXP: ESP {esp.Count} events on screen ({_screenW}x{_screenH})");
                    }
                    EspDataUpdated?.Invoke(esp);
                    break;
                case "C":
                    // 카메라만 바뀜(스크롤): 마지막 ESP 목록을 새 카메라 위치로 다시 그립니다.
                    if (_lastEsp != null && MkxpAgentChannel.ParseCamera(payload, _espView)) EspDataUpdated?.Invoke(_lastEsp);
                    break;
                case "I":
                    var tile = JsonSerializer.Deserialize<TileInfo>(payload);
                    if (tile != null) TileInfoUpdated?.Invoke(tile);
                    break;
                case "N":
                    NotificationReceived?.Invoke(payload);
                    break;
                case "L":
                    UiLog.Write($"mkxp-agent: {payload}");
                    break;
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"RocketRenderMKXP: agent line '{kind}' error: {ex.Message}");
        }
    }

    public void SetBrightness(double value) => _channel?.Send("bright", value);
    // 필터(mkxp-z 내장 xBRZ)와 글꼴은 게임을 다시 시작할 때 적용됩니다. MainWindow가 재시작을 묻습니다.
    public void SetFilter(string filter) { }
    public void SetInGameFont(string? family, int size, bool bold) { }
    public void SetAutoMessage(bool enabled, double speed) => _channel?.Send("auto", enabled, speed);
    public void SetSkipMessage(bool enabled) => _channel?.Send("skip", enabled);
    public void AdvanceMessage() => _channel?.Send("advance");
    /// <summary>멀티: 참가자 조종 중이면 켬 (게임이 매 프레임 키 상태를 받아 감)</summary>
    public void SetRemoteControl(bool on) => _channel?.Send("rctl", on);
    /// <summary>멀티: 참가자가 지금 누르고 있는 키 (윈도우 가상 키, 쉼표로 구분). Shift·Win32API 키 상태는 이 경로로만 들어갑니다.</summary>
    public void SetRemoteKeys(string vkList) => _channel?.Send("rkeys", vkList);
    /// <summary>멀티: 방송 화면에도 ESP를 그림 (참가자가 도구 권한이 있을 때)</summary>
    public void SetEspShare(bool on) => _channel?.Send("espshare", on);

    /// <summary>타일 인스펙터: 게임 해상도 기준 마우스 좌표 (null이면 해제)</summary>
    public void UpdateVirtualMouse(double? x, double? y)
    {
        if (x is double mx && y is double my) _channel?.Send("mouse", mx, my);
        else _channel?.Send("mouse", "", "");
    }

    public RocketRenderMKXP()
    {
        _exitWatcher.Exited += OnProcessExited;
        _framePumpTimer.Tick += OnFramePumpTick;
    }

    /// <summary>
    /// Ensures that Game.ini exists in the game directory and contains a valid [Game] section
    /// with correct Library, Scripts, and Title entries, preventing the fatal
    /// "No script file has been specified. Check the game's INI and try again" error.
    /// </summary>
    public static void EnsureValidGameIni(string gameDir, int engine)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return;

        try
        {
            int rgssVersion = engine switch
            {
                CoreInterop.EngineXp => 1,
                CoreInterop.EngineVx => 2,
                CoreInterop.EngineAce => 3,
                _ => 1
            };

            string defaultDll = rgssVersion switch
            {
                1 => "RGSS104E.dll",
                2 => "RGSS202E.dll",
                3 => "RGSS301.dll",
                _ => "RGSS104E.dll"
            };

            // Detect actual script file on disk
            string scriptRelPath = "";
            if (File.Exists(Path.Combine(gameDir, "Data", "Scripts.rxdata")))
                scriptRelPath = @"Data\Scripts.rxdata";
            else if (File.Exists(Path.Combine(gameDir, "Data", "Scripts.rvdata2")))
                scriptRelPath = @"Data\Scripts.rvdata2";
            else if (File.Exists(Path.Combine(gameDir, "Data", "Scripts.rvdata")))
                scriptRelPath = @"Data\Scripts.rvdata";
            else
            {
                string dataDir = Path.Combine(gameDir, "Data");
                if (Directory.Exists(dataDir))
                {
                    var cands = Directory.GetFiles(dataDir, "Scripts.*");
                    if (cands.Length > 0)
                    {
                        scriptRelPath = @"Data\" + Path.GetFileName(cands[0]);
                    }
                    else
                    {
                        var anyData = Directory.GetFiles(dataDir, "*.rxdata")
                            .Concat(Directory.GetFiles(dataDir, "*.rvdata*")).ToArray();
                        if (anyData.Length > 0)
                            scriptRelPath = @"Data\" + Path.GetFileName(anyData[0]);
                    }
                }
                if (string.IsNullOrEmpty(scriptRelPath))
                {
                    scriptRelPath = rgssVersion switch
                    {
                        1 => @"Data\Scripts.rxdata",
                        2 => @"Data\Scripts.rvdata",
                        3 => @"Data\Scripts.rvdata2",
                        _ => @"Data\Scripts.rxdata"
                    };
                }
            }

            string iniPath = Path.Combine(gameDir, "Game.ini");
            string title = Path.GetFileName(gameDir.TrimEnd('\\', '/'));

            if (!File.Exists(iniPath))
            {
                string content = $"[Game]\r\nLibrary={defaultDll}\r\nScripts={scriptRelPath}\r\nTitle={title}\r\nRTP1=\r\nRTP2=\r\nRTP3=\r\n";
                File.WriteAllText(iniPath, content, new System.Text.UTF8Encoding(false));
                UiLog.Write($"RocketRenderMKXP: Created missing Game.ini -> Scripts={scriptRelPath}, Library={defaultDll}");
                return;
            }

            // Existing Game.ini: verify and fix Scripts and Library safely.
            // 원본 인코딩(ANSI/UTF-8)을 유지해야 원래 Game.exe에서도 제목이 깨지지 않습니다.
            string iniText = RtpResolver.ReadIniText(iniPath, out var iniEncoding);

            var lines = iniText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
            bool hasGameSection = false;
            int gameSectionIndex = -1;
            int scriptsLineIndex = -1;
            int libraryLineIndex = -1;
            int titleLineIndex = -1;
            string existingScript = "";

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Equals("[Game]", StringComparison.OrdinalIgnoreCase))
                {
                    hasGameSection = true;
                    gameSectionIndex = i;
                }
                else if (hasGameSection && trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    break;
                }
                else if (hasGameSection)
                {
                    if (trimmed.StartsWith("Scripts=", StringComparison.OrdinalIgnoreCase))
                    {
                        scriptsLineIndex = i;
                        existingScript = trimmed.Substring(8).Trim().Trim('"');
                    }
                    else if (trimmed.StartsWith("Library=", StringComparison.OrdinalIgnoreCase))
                    {
                        libraryLineIndex = i;
                    }
                    else if (trimmed.StartsWith("Title=", StringComparison.OrdinalIgnoreCase))
                    {
                        titleLineIndex = i;
                    }
                }
            }

            bool modified = false;

            if (!hasGameSection)
            {
                lines.Insert(0, "[Game]");
                gameSectionIndex = 0;
                modified = true;
            }

            bool scriptNeedsFix = false;
            if (scriptsLineIndex == -1 || string.IsNullOrWhiteSpace(existingScript))
            {
                scriptNeedsFix = true;
            }
            else
            {
                string norm = existingScript.Replace('/', '\\');
                if (!File.Exists(Path.Combine(gameDir, norm)) && File.Exists(Path.Combine(gameDir, scriptRelPath)))
                {
                    scriptNeedsFix = true;
                }
            }

            if (scriptNeedsFix)
            {
                string scriptLine = $"Scripts={scriptRelPath}";
                if (scriptsLineIndex != -1)
                {
                    lines[scriptsLineIndex] = scriptLine;
                }
                else
                {
                    lines.Insert(gameSectionIndex + 1, scriptLine);
                }
                modified = true;
                UiLog.Write($"RocketRenderMKXP: Repaired Game.ini -> {scriptLine}");
            }

            if (libraryLineIndex == -1)
            {
                lines.Insert(gameSectionIndex + 1, $"Library={defaultDll}");
                modified = true;
            }

            if (titleLineIndex == -1)
            {
                lines.Insert(gameSectionIndex + 1, $"Title={title}");
                modified = true;
            }

            if (modified)
            {
                File.WriteAllText(iniPath, string.Join("\r\n", lines), iniEncoding is System.Text.UTF8Encoding ? new System.Text.UTF8Encoding(false) : iniEncoding);
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"RocketRenderMKXP: EnsureValidGameIni warning: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates mkxp.json content with the requested engine settings.
    /// </summary>
    public static string GenerateMkxpConfigJson(
        string gameDir,
        int engine,
        bool fixedAspectRatio = true,
        bool smoothScaling = false,
        string? gameFolder = null,
        string? targetFontFamily = null,
        string? soundFontPath = null,
        IEnumerable<string>? rtpDirs = null,
        IEnumerable<string>? preloadScripts = null,
        int? scalingMode = null,
        bool vsync = false)
    {
        int rgssVersion = engine switch
        {
            CoreInterop.EngineXp => 1,
            CoreInterop.EngineVx => 2,
            CoreInterop.EngineAce => 3,
            _ => 1
        };
        int w = (engine == CoreInterop.EngineXp) ? 640 : 544;
        int h = (engine == CoreInterop.EngineXp) ? 480 : 416;

        string iniFileName = "Game.ini";
        try
        {
            var inis = Directory.GetFiles(gameDir, "*.ini");
            if (inis.Length > 0)
            {
                var preferred = inis.FirstOrDefault(i => Path.GetFileName(i).Equals("Game.ini", StringComparison.OrdinalIgnoreCase))
                             ?? inis[0];
                iniFileName = Path.GetFileName(preferred);
            }
        }
        catch { }

        string title = Path.GetFileName(gameDir.TrimEnd('\\', '/'));
        string iniPath = Path.Combine(gameDir, iniFileName);
        if (File.Exists(iniPath))
        {
            try
            {
                // ini는 대개 ANSI(CP932/CP949)이므로 원본 인코딩으로 읽어야 제목이 깨지지 않습니다.
                var t = RtpResolver.IniValue(RtpResolver.ReadIniText(iniPath, out _), "Game", "Title");
                if (!string.IsNullOrWhiteSpace(t)) title = t;
            }
            catch { }
        }

        string targetFolder = string.IsNullOrEmpty(gameFolder) ? gameDir : gameFolder;
        string? sf = soundFontPath ?? RocketFontSystem.ResolveSoundFontPath();

        // RTP: 게임이 요구하는 실제 RTP 폴더(레지스트리/번들) + 한글 글꼴이 든 mkxp-z 런타임 폴더(Fonts/)
        var rtpList = new List<string>();
        void AddRtp(string r)
        {
            if (!Directory.Exists(r)) return;
            string rNorm = Path.GetFullPath(r).Replace('\\', '/');
            if (!rtpList.Contains(rNorm, StringComparer.OrdinalIgnoreCase)) rtpList.Add(rNorm);
        }
        if (rtpDirs != null) foreach (var r in rtpDirs) AddRtp(r);
        string baseDir = AppContext.BaseDirectory;
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? baseDir;
        AddRtp(Path.Combine(exeDir, "runtimes", "mkxp-z"));
        AddRtp(Path.Combine(baseDir, "runtimes", "mkxp-z"));
        // 사용자 PC의 시스템 글꼴 사본(한글 대체용). 설치 파일에 MS 글꼴을 넣지 않기 위해 실행 시 복사합니다.
        var scriptFontNames = MkxpFontCatalog.FontNamesInScripts(gameDir, engine);
        // 게임 글꼴이 PC에 없으면 비슷한 설치 글꼴로 대신 보여 줍니다 (사용자가 인게임 글꼴을 고르지 않았을 때)
        var missingSubs = string.IsNullOrWhiteSpace(targetFontFamily)
            ? MkxpFontCatalog.MissingFontSubstitutes(gameDir, engine, new[] { Path.Combine(gameDir, "Fonts") }.Concat(rtpList.Select(r => Path.Combine(r, "Fonts"))))
            : new Dictionary<string, string>();
        foreach (var (from, to) in missingSubs) UiLog.Write($"RocketRenderMKXP: missing font '{from}' -> '{to}'");
        AddRtp(MkxpFontCatalog.EnsureSystemFontRoot(targetFontFamily, scriptFontNames.Concat(missingSubs.Values)));

        // 게임에 동봉된 mkxp.json이 있으면 그 설정을 기본값으로 존중합니다 (주석/후행 쉼표 허용).
        var obj = new Dictionary<string, object?>();
        string ownJson = Path.Combine(gameDir, "mkxp.json");
        if (File.Exists(ownJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ownJson),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var p in doc.RootElement.EnumerateObject()) obj[p.Name] = p.Value.Clone();
            }
            catch (Exception ex)
            {
                UiLog.Write($"RocketRenderMKXP: game mkxp.json ignored ({ex.Message})");
            }
        }

        if (obj.TryGetValue("RTP", out var ownRtp) && ownRtp is JsonElement ownRtpEl && ownRtpEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in ownRtpEl.EnumerateArray())
            {
                string? rs = r.GetString();
                if (!string.IsNullOrEmpty(rs)) AddRtp(Path.IsPathRooted(rs) ? rs : Path.Combine(gameDir, rs));
            }
        }

        var ownSubs = new List<string>();
        if (obj.TryGetValue("fontSub", out var ownSub) && ownSub is JsonElement ownSubEl && ownSubEl.ValueKind == JsonValueKind.Array)
            foreach (var x in ownSubEl.EnumerateArray()) if (x.GetString() is { Length: > 0 } xs) ownSubs.Add(xs);
        string gameFonts = Path.Combine(gameDir, "Fonts");
        MkxpFontCatalog.SplitCollectionsFrom(gameFonts, scriptFontNames, targetFontFamily);   // 게임 동봉 .ttc도 mkxp-z가 읽을 수 있게 (캐시에)
        var fontSubs = MkxpFontCatalog.BuildSubstitutions(
            new[] { gameFonts }.Concat(rtpList.Select(r => Path.Combine(r, "Fonts"))), gameFonts, targetFontFamily, ownSubs, scriptFontNames, missingSubs);

        var preload = new List<string>();
        if (obj.TryGetValue("preloadScript", out var ownPre) && ownPre is JsonElement ownPreEl && ownPreEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in ownPreEl.EnumerateArray())
            {
                string? ss = s.GetString();
                if (!string.IsNullOrEmpty(ss)) preload.Add((Path.IsPathRooted(ss) ? ss : Path.Combine(gameDir, ss)).Replace('\\', '/'));
            }
        }
        // 게임이 같은 호환 스크립트를 이미 쓰면 다시 넣지 않습니다 (win32_wrap 등은 두 번 불러오면 alias가 자기 자신을 호출해 멈춥니다).
        if (preloadScripts != null)
        {
            foreach (var p in preloadScripts)
            {
                string name = Path.GetFileName(p);
                if (preload.Any(x => Path.GetFileName(x).Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                preload.Add(p.Replace('\\', '/'));
            }
        }

        obj["gameFolder"] = targetFolder.Replace('\\', '/');
        // mkxp-z는 ini(execName.ini)와 암호화 아카이브(execName.rgssad/rgss2a/rgss3a) 이름을 실행 파일 이름에서 가져옵니다.
        // 실행 파일이 mkxp-z.exe라 mkxp-z.rgssad를 찾다가 스크립트를 못 읽고 멈추던 문제를 막기 위해 게임 이름으로 고정합니다.
        obj["execName"] = Path.GetFileNameWithoutExtension(iniFileName);
        obj.Remove("iniFileName");
        obj["RTP"] = rtpList.ToArray();
        obj["useScriptNames"] = true;   // bool (정수 1은 "Invalid variable"로 무시됨)
        obj["defScreenW"] = w;
        obj["defScreenH"] = h;
        obj["windowTitle"] = title;
        obj["fixedAspectRatio"] = fixedAspectRatio;
        // 화면 필터 → mkxp-z 내장 보간 모드(정수). 지정이 없으면 기존처럼 켜기/끄기.
        // mkxp-z는 smoothScaling을 정수(0=최근접…4=xBRZ)로만 받습니다 (bool을 주면 "Invalid variable"로 무시됨).
        obj["smoothScaling"] = scalingMode ?? (smoothScaling ? 1 : 0);
        // 기본은 끔: 켜면 배속(Graphics.frame_rate 상향)이 모니터 주사율에 묶입니다.
        obj["vsync"] = vsync;   // 화면 > 프레임 > 수직 동기화
        obj["syncToRefreshrate"] = false;
        obj["frameSkip"] = true;
        obj["winResizable"] = true;
        obj["rgssVersion"] = rgssVersion;
        obj["fontSub"] = fontSubs;
        obj["enableReset"] = true;
        if (preload.Count > 0) obj["preloadScript"] = preload.ToArray();

        if (!string.IsNullOrEmpty(sf) && File.Exists(sf))
        {
            obj["midiSoundFont"] = sf.Replace('\\', '/');
        }

        return JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// Resolves the mkxp-z executable path matching the project's standard runtime hierarchy.
    /// Checks AppContext.BaseDirectory/runtimes/mkxp-z/, process directory, game directory, and fallbacks.
    /// </summary>
    public static string ResolveExecutable(string? gameDir = null)
    {
        // 1. Game directory
        if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
        {
            string[] gameCands = ["mkxp-z.exe", "RocketRenderMKXP.exe", "mkxp.exe"];
            foreach (var name in gameCands)
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
            Path.Combine(baseDir, "runtimes", "mkxp-z"),
            Path.Combine(appDir, "runtimes", "mkxp-z"),
            baseDir,
            appDir,
            Path.Combine(Directory.GetCurrentDirectory(), "dist", "portable", "runtimes", "mkxp-z"),
            Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "mkxp-z"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RocketRPG", "runtimes", "mkxp-z"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RocketRPG", "runtimes", "mkxp-z")
        };

        // Walk up directory tree to support dev/test environments (up to 7 parent levels)
        string? cur = baseDir;
        for (int i = 0; i < 7 && !string.IsNullOrEmpty(cur); i++)
        {
            searchDirs.Add(Path.Combine(cur, "runtimes", "mkxp-z"));
            cur = Path.GetDirectoryName(cur);
        }
        cur = appDir;
        for (int i = 0; i < 7 && !string.IsNullOrEmpty(cur); i++)
        {
            searchDirs.Add(Path.Combine(cur, "runtimes", "mkxp-z"));
            cur = Path.GetDirectoryName(cur);
        }

        string[] exeNames = ["mkxp-z.exe", "RocketRenderMKXP.exe", "mkxp.exe"];
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
    /// Starts the mkxp-z native engine for the specified game directory and RGSS engine version.
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
        string? filter = null,
        bool vsync = false)
    {
        _vsync = vsync;
        if (IsRunning)
        {
            Stop();
        }

        _gameDir = gameDir;
        _engine = engine;
        _parentHwnd = parentHwnd;
        _frameCallback = frameCallback;

        if (!Directory.Exists(gameDir))
        {
            UiLog.Write($"RocketRenderMKXP: game directory not found: {gameDir}");
            return false;
        }

        string resolvedExe = ResolveExecutable(gameDir);
        if (CoreInterop.rpg_mkxp_is_available_game(gameDir) == 0 && string.IsNullOrEmpty(resolvedExe))
        {
            UiLog.Write("RocketRenderMKXP: mkxp-z executable not found");
            return false;
        }

        int rgssVersion = engine switch
        {
            CoreInterop.EngineXp => 1,
            CoreInterop.EngineVx => 2,
            CoreInterop.EngineAce => 3,
            _ => 1
        };

        var cfg = CoreInterop.MkxpConfig.Create();
        cfg.RgssVersion = rgssVersion;
        cfg.ScreenWidth = (uint)BaseWidth;
        cfg.ScreenHeight = (uint)BaseHeight;
        cfg.TargetFps = (uint)BaseFps;
        cfg.Fullscreen = 0;
        cfg.FixedAspectRatio = (byte)(fixedAspectRatio ? 1 : 0);
        cfg.SmoothScaling = (byte)(smoothScaling ? 1 : 0);
        cfg.Vsync = 0;
        cfg.WinResizable = 1;
        cfg.PreloadBridge = 0;
        cfg.UseSharedSurface = 1;
        if (!string.IsNullOrEmpty(resolvedExe))
        {
            cfg.CustomExe = resolvedExe;
        }

        // Ensure Game.ini exists and has valid Scripts and Library (원본 인코딩 유지, 필요할 때만 수정)
        EnsureValidGameIni(gameDir, engine);

        // mkxp-z는 작업 폴더의 mkxp.json을 읽은 뒤 gameFolder로 이동합니다.
        // 게임 폴더를 더럽히지 않도록 RocketRPG 전용 폴더에 설정/에이전트를 두고 그곳을 작업 폴더로 실행합니다.
        string configDir = PrepareLaunchDirectory(gameDir, engine, fixedAspectRatio, smoothScaling, inGameFontFamily, filter, _vsync);
        cfg.GameDir = configDir;
        // 게임에 자체 mkxp-z가 동봉된 경우가 아니라면, 게임별 실행 폴더의 미러된 런타임으로 실행합니다.
        if (!string.IsNullOrEmpty(resolvedExe) &&
            !string.Equals(Path.GetDirectoryName(resolvedExe), Path.GetFullPath(gameDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            cfg.CustomExe = MirrorRuntime(resolvedExe, configDir);
        }

        _channel?.Dispose();
        _channel = new MkxpAgentChannel();
        _channel.LineReceived += OnAgentLine;
        _channel.ConnectionChanged += c => UiLog.Write($"RocketRenderMKXP: agent {(c ? "connected" : "disconnected")}");
        _fontEnv = $"{inGameFontFamily}|{inGameFontSize}|{(inGameFontBold ? 1 : 0)}";

        // Create native runtime instance in C++ Core
        int st = CoreInterop.rpg_mkxp_create(ref cfg, out _nativeInstance);
        if (st != 0 || _nativeInstance == IntPtr.Zero)
        {
            UiLog.Write($"RocketRenderMKXP: rpg_mkxp_create returned error {st}");
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
            CoreInterop.rpg_mkxp_host_window(_nativeInstance, effectiveParent, 0, 0, initW, initH);
        }

        int startSt = StartWithAgentEnvironment();
        if (startSt != 0)
        {
            UiLog.Write($"RocketRenderMKXP: rpg_mkxp_start returned error {startSt}");
            CoreInterop.rpg_mkxp_destroy(_nativeInstance);
            _nativeInstance = IntPtr.Zero;
            return false;
        }

        // Capture PID immediately
        if (CoreInterop.rpg_mkxp_get_pid(_nativeInstance, out var initialPid) == 0 && initialPid != 0)
        {
            _processId = (int)initialPid;
        }

        // RubyBridge: 스위치/변수 이름, 맵 데이터, 명령 스크립트 생성 담당. 전송은 에이전트 파이프를 사용합니다.
        // ESP/타일 정보는 게임 내부 좌표를 아는 에이전트가 직접 계산해 보냅니다(OnAgentLine).
        _rubyBridge = new RubyBridge(gameDir, engine, () => _gameHwnd, _processId, _gameHwnd, _channel);
        _rubyBridge.GameStateUpdated += s => GameStateUpdated?.Invoke(s);
        _rubyBridge.DataInspectorUpdated += (sw, va) => DataInspectorUpdated?.Invoke(sw, va);
        _rubyBridge.NotificationReceived += m => NotificationReceived?.Invoke(m);

        IsRunning = true;
        _exitWatcher.Watch(_processId);
        _ = WatchStartupErrorsAsync(_processId);

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

                if (CoreInterop.rpg_mkxp_get_hwnd(_nativeInstance, out var hwnd) == 0 && hwnd != IntPtr.Zero)
                {
                    _gameHwnd = hwnd;
                    CoreInterop.rpg_mkxp_get_pid(_nativeInstance, out var pid);
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
                        CoreInterop.rpg_mkxp_host_window(_nativeInstance, targetParent, 0, 0, hostW, hostH);
                    }
                    _rubyBridge?.UpdateGameHwnd(_gameHwnd, _processId);
                    UiLog.Write($"RocketRenderMKXP: native surface detected hwnd={hwnd}, pid={_processId}");
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

        var fi = new CoreInterop.MkxpFrame();
        if (CoreInterop.rpg_mkxp_get_frame(_nativeInstance, ref fi) == 0 && fi.Width > 0 && fi.Height > 0)
        {
            int needBytes = (int)(fi.Width * fi.Height * 4);
            EnsurePixelBuffer(needBytes);
            if (_pixelBufferPtr != IntPtr.Zero &&
                CoreInterop.rpg_mkxp_read_pixels(_nativeInstance, _pixelBufferPtr, (uint)needBytes) == 0)
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
            CoreInterop.rpg_mkxp_host_window(_nativeInstance, parentHwnd, x, y, w, h);
        }
    }

    public void Resize(int w, int h)
    {
        if (_nativeInstance != IntPtr.Zero)
        {
            CoreInterop.rpg_mkxp_resize(_nativeInstance, w, h);
        }
    }

    public void SendInput(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_nativeInstance != IntPtr.Zero)
        {
            CoreInterop.rpg_mkxp_send_input(_nativeInstance, msg, wParam, lParam);
        }
    }

    public void SetSpeed(double speed)
    {
        CurrentSpeed = speed;
        _rubyBridge?.SetSpeed(speed);
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
        _rubyBridge?.TogglePause();
    }

    public void ToggleNoclip(bool? through = null) => _rubyBridge?.ToggleNoclip();
    void IGameBridge.ToggleNoclip() => ToggleNoclip(null);

    public void Warp(int mapId, int x, int y, int? direction = null) => _rubyBridge?.Warp(mapId, x, y);
    void IGameBridge.Warp(int mapId, int x, int y) => Warp(mapId, x, y, null);

    public void QuickSave() => _channel?.Send("qsave");
    public void QuickLoad() => _channel?.Send("qload");
    public void ForceSaveMenu() => _rubyBridge?.ForceSaveMenu();
    public void ForceLoadMenu() => _rubyBridge?.ForceLoadMenu();

    public void SetVolume(int volumePercent)
    {
        if (_processId > 0) CoreInterop.rpg_set_volume_for_pid((uint)_processId, volumePercent);
    }

    public void SetSwitch(int id, bool value) => _rubyBridge?.SetSwitch(id, value);
    public void SetVariable(int id, string value) => _rubyBridge?.SetVariable(id, value);
    public void SetVariable(int id, object value) => _rubyBridge?.SetVariable(id, value?.ToString() ?? "");

    public void FreezeSwitch(int id, bool frozen, bool value) => _rubyBridge?.FreezeSwitch(id, frozen, value);
    public void FreezeVariable(int id, bool frozen, string value) => _rubyBridge?.FreezeVariable(id, frozen, value);
    public void FreezeVariable(int id, bool frozen, object value) => _rubyBridge?.FreezeVariable(id, frozen, value?.ToString() ?? "");

    public void EnableEsp(bool enabled)
    {
        EspEnabled = enabled;
        _channel?.Send("esp", enabled);
        if (!enabled) EspDataUpdated?.Invoke(new List<EspItem>());
    }

    public void EnableTileInspector(bool enabled)
    {
        TileInspectorEnabled = enabled;
        if (!enabled) UpdateVirtualMouse(null, null);
    }

    public void RequestDataInspector() => _rubyBridge?.RequestDataInspector();

    public void Restart()
    {
        _rubyBridge?.Restart();
        if (_nativeInstance != IntPtr.Zero)
        {
            _exitWatcher.Watch(0);
            CoreInterop.rpg_mkxp_stop(_nativeInstance);
            StartWithAgentEnvironment();
            if (CoreInterop.rpg_mkxp_get_pid(_nativeInstance, out var pid) == 0 && pid != 0)
                _processId = (int)pid;
            _exitWatcher.Watch(_processId);
        }
    }

    // ── 멀티 방장: 에이전트가 찍어 보내는 게임 화면 ──
    MkxpFrameReceiver? _frames;
    int _framesFps;

    /// <summary>방송하는 동안 에이전트에게 fps에 맞춰 게임 화면을 보내 달라고 합니다. 받은 화면이 쌓이는 메모리 주소 (끄면 Zero).</summary>
    public IntPtr EnableFrameFeed(bool on, int fps)
    {
        if (!on || _channel == null)
        {
            if (_frames != null)
            {
                _channel?.Send("frames", 0, "");
                _frames.Dispose();
                _frames = null;
            }
            _framesFps = 0;
            return IntPtr.Zero;
        }
        _frames ??= new MkxpFrameReceiver();
        if (_framesFps != fps)
        {
            _framesFps = fps;
            _channel.Send("frames", fps, _frames.PipeName);
        }
        return _frames.Buffer;
    }

    public void Stop()
    {
        EnableFrameFeed(false, 0);
        _framePumpTimer.Stop();
        _exitWatcher.Watch(0);
        IsRunning = false;

        ReleasePixelBuffer();

        _rubyBridge?.Dispose();
        _rubyBridge = null;

        if (_nativeInstance != IntPtr.Zero)
        {
            CoreInterop.rpg_mkxp_stop(_nativeInstance);
            CoreInterop.rpg_mkxp_destroy(_nativeInstance);
            _nativeInstance = IntPtr.Zero;
        }

        _channel?.Dispose();
        _channel = null;
        _screenW = _screenH = 0;
        // 설정은 RocketRPG 전용 폴더에만 쓰므로 게임 폴더의 mkxp.json(게임 동봉 파일일 수 있음)은 건드리지 않습니다.
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// mkxp-z 에이전트가 보내는 게임 화면을 받는 파이프. 받은 화면은 멀티 방송이 읽는 메모리(GameFrameFeed와 같은 구성)에 둡니다.
/// 파이프 한 장: 'RRFR', 폭, 높이, 바이트 수 (각 uint32) + RGBA.
/// </summary>
sealed class MkxpFrameReceiver : IDisposable
{
    const int HeaderBytes = 4096, SlotBytes = 1280 * 960 * 4, Magic = 0x52524652;
    public string PipeName { get; } = "RocketRPG_frames_" + Guid.NewGuid().ToString("N");
    public IntPtr Buffer { get; }
    readonly System.Threading.CancellationTokenSource _cts = new();

    // 한 번 만들어 계속 씀: 게임을 바꿀 때 방송 스레드가 아직 읽고 있어도 안전하도록 풀지 않습니다 (게임은 한 번에 하나)
    static readonly IntPtr SharedBuffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(HeaderBytes + 2 * SlotBytes);

    public MkxpFrameReceiver()
    {
        Buffer = SharedBuffer;
        for (int i = 1; i < 8; i++) System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, i * 4, 0);
        System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 0, Magic);
        System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 6 * 4, SlotBytes);
        _ = System.Threading.Tasks.Task.Run(() => ServeAsync(_cts.Token));
    }

    async System.Threading.Tasks.Task ServeAsync(System.Threading.CancellationToken ct)
    {
        var head = new byte[16];
        byte[] data = [];
        int slot = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new System.IO.Pipes.NamedPipeServerStream(PipeName, System.IO.Pipes.PipeDirection.In, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 8 << 20, 0);
                await server.WaitForConnectionAsync(ct);
                while (server.IsConnected && !ct.IsCancellationRequested)
                {
                    await server.ReadExactlyAsync(head, ct);
                    int magic = BitConverter.ToInt32(head, 0), w = BitConverter.ToInt32(head, 4), h = BitConverter.ToInt32(head, 8), len = BitConverter.ToInt32(head, 12);
                    if (magic != Magic || w <= 0 || h <= 0 || len != w * h * 4 || len > 64 << 20) break;   // 어긋나면 다시 연결
                    if (data.Length < len) data = new byte[len];
                    await server.ReadExactlyAsync(data.AsMemory(0, len), ct);
                    if (len > SlotBytes) continue;
                    slot ^= 1;
                    System.Runtime.InteropServices.Marshal.Copy(data, 0, Buffer + HeaderBytes + slot * SlotBytes, len);
                    System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 2 * 4, slot);
                    System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 3 * 4, w);
                    System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 4 * 4, h);
                    System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 5 * 4, 0);   // RGBA
                    System.Threading.Thread.MemoryBarrier();
                    System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 4, System.Runtime.InteropServices.Marshal.ReadInt32(Buffer, 4) + 1);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.EndOfStreamException)
            {
                await System.Threading.Tasks.Task.Delay(200, CancellationToken.None);
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
