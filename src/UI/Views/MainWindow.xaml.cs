#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using RocketRPG.Models;

namespace RocketRPG.Views;

public partial class MainWindow : Window
{
    readonly MainController _ctl = new();
    // 소리 > 출력 장치: 게임 오디오 라우팅 상태
    string _selectedDeviceId = "";          // "" = 기본값
    readonly List<int> _routedGamePids = new();
    DispatcherTimer? _autoRouteTimer;
    int _autoRouteRetries;

    readonly WebViewRenderer _webRenderer;
    readonly RocketRenderMKXP _mkxpRenderer = new();
    readonly RocketRenderEasyRPG _easyRpgRenderer = new();
    readonly RocketTextAutoAdvance _autoAdvance = new();
    IGameBridge? _currentBridge;
    bool _isNativeRunning;
    string? _currentDir;
    MapViewerWindow? _mapViewerWnd;
    DataInspectorWindow? _dataInspectorWnd;

    private readonly System.Windows.Shapes.Ellipse[] _spinnerDots = new System.Windows.Shapes.Ellipse[16];
    private DispatcherTimer? _spinnerTimer;
    private int _spinnerStep;

    static class NativeMethods
    {
        [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, out POINT lpPoint);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr ho);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        public const uint SRCCOPY = 0x00CC0020;
        public const uint DIB_RGB_COLORS = 0;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPCHILDREN = 0x02000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }
    }

    public class NativeSurfaceHwndHost : HwndHost
    {
        private IntPtr _hwndHost = IntPtr.Zero;
        public event Action<int, int>? Resized;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _hwndHost = NativeMethods.CreateWindowEx(
                0,
                "static",
                "",
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN,
                0, 0,
                100, 100,
                hwndParent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            return new HandleRef(this, _hwndHost);
        }

        protected override void OnWindowPositionChanged(Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);
            int w = (int)Math.Round(rcBoundingBox.Width);
            int h = (int)Math.Round(rcBoundingBox.Height);
            if (w > 0 && h > 0)
            {
                Resized?.Invoke(w, h);
            }
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (hwnd.Handle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(hwnd.Handle);
                _hwndHost = IntPtr.Zero;
            }
        }
    }


    const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    const int MK_LBUTTON = 0x0001, MK_RBUTTON = 0x0002, MK_SHIFT = 0x0004, MK_CONTROL = 0x0008, MK_MBUTTON = 0x0010;

    string BaseTitle => $"RocketRPG {CoreInterop.Version()}";

    // HUD 요소는 투명 오버레이 창에 있으며, 기존 코드와의 호환을 위해 같은 이름으로 노출합니다.
    readonly OverlayWindow _overlay;
    EspLayer EspCanvas => _overlay.EspCanvas;
    Border PinnedVarsOverlay => _overlay.PinnedVarsOverlay;
    StackPanel PinnedVarsStack => _overlay.PinnedVarsStack;
    RenpyMessageBar RenpyMsgBar => _overlay.RenpyMsgBar;
    StackPanel ToastStack => _overlay.ToastStack;
    Border TileInspectorHud => _overlay.TileInspectorHud;
    TextBlock TileInspectorText => _overlay.TileInspectorText;
    // 불러오는 중 화면도 오버레이 창에 있습니다 (게임 창 위에도 보이게)
    Grid GameLoadingOverlay => _overlay.GameLoadingOverlay;
    Canvas SpinnerCanvas => _overlay.SpinnerCanvas;
    TextBlock LoadingGameTitle => _overlay.LoadingGameTitle;
    TextBlock LoadingStatusSub => _overlay.LoadingStatusSub;

    public MainWindow()
    {
        InitializeComponent();
        _overlay = new OverlayWindow(this, RenderScreen) { FontFamily = FontFamily };
        Loaded += (_, _) =>
        {
            _overlay.Attach();
            var mainHwnd = new WindowInteropHelper(this).Handle;
            // 멀티 참가자는 방송 화면(WebView2)에 포커스가 있을 때도 단축키가 되도록 / 휠 클릭은 멀티 핑
            _nativeHotkeys = new NativeHotkeyHook(() => mainHwnd, () => _hotkeyGamePid, ExecuteHotkeyAction,
                (x, y) => OnMultiMiddleClick(x, y), GuestHotkeyAllowed);
            _nativeMouseTimer.Tick += (_, _) =>
            {
                // 저수준 훅에서 WebView2 COM/UI 객체를 읽으면 키 입력 전체가 UI 응답을 기다립니다.
                _hotkeyGamePid = NativeGamePid > 0 ? NativeGamePid : MultiGuestViewPid;
                _guestHotkeyLevel = !IsMultiGuest || GuestToolsAllowed ? 0 : 2;
                PumpNativeTileMouse();
            };
            _nativeMouseTimer.Start();
            _embedWatchdog.Tick += (_, _) => EnforceEmbeddedWindow();
            _embedWatchdog.Start();
            // RocketRPG.exe "게임 폴더" (또는 exe에 폴더 끌어다 놓기)로 바로 실행
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && Directory.Exists(args[1])) Launch(args[1]);
        };
        UiLog.Write("ctor: begin");
        UiProbe.StartIfRequested(Dispatcher);
        // 노트(다른 창)에서 글을 쓰다가 게임 화면을 누르면 키 입력을 게임으로 돌려줍니다. 임베드된 게임 창을 눌러
        // RocketRPG 창이 활성화되면 WPF가 마지막 포커스를 되살려, 게임이 키를 받지 못했습니다.
        Activated += (_, _) =>
        {
            if (_isNativeRunning && IsCursorOverGame()) Dispatcher.BeginInvoke(FocusGame, DispatcherPriority.Input);
        };
        Deactivated += (_, _) => { if (IsMultiGuest) PostMulti("{\"t\":\"release\"}"); };
        BuildHistoryMenu();
        OutputDeviceMenu.IsEnabled = AudioRouter.Supported;
        if (!AudioRouter.Supported)
        {
            OutputDeviceMenu.ToolTip = Environment.OSVersion.Version.Build < 22000
                ? "게임별 출력 장치 선택은 Windows 11부터 지원합니다. (Windows 10에서는 시스템 기본 장치로 나옵니다)"
                : "이 시스템에서는 출력 장치 선택을 사용할 수 없습니다. (기본값만 사용)";
            ToolTipService.SetShowOnDisabled(OutputDeviceMenu, true);
        }
        Title = BaseTitle;
        CheckItem("ratio", _ctl.Settings.Ratio);
        CheckItem("gamma", _ctl.Settings.Gamma.ToString(System.Globalization.CultureInfo.InvariantCulture));
        CheckItem("filter", _ctl.Settings.Filter);
        CheckVolumeItems(_ctl.Settings.Volume);

        _webRenderer = new WebViewRenderer(WebScreen);
        _webRenderer.NotificationReceived += msg => Dispatcher.Invoke(() => ShowHudMessage(msg));
        _webRenderer.HotkeyReceived += OnWebHotkeyReceived;
        _webRenderer.EspDataUpdated += OnEspDataUpdated;
        _webRenderer.TileInfoUpdated += OnTileInfoUpdated;
        _webRenderer.GameStateUpdated += OnGameStateUpdated;

        _mkxpRenderer.NotificationReceived += msg => Dispatcher.Invoke(() => ShowHudMessage(msg));
        _mkxpRenderer.HotkeyReceived += OnWebHotkeyReceived;
        _mkxpRenderer.EspDataUpdated += OnEspDataUpdated;
        _mkxpRenderer.TileInfoUpdated += OnTileInfoUpdated;
        _mkxpRenderer.GameStateUpdated += OnGameStateUpdated;
        _mkxpRenderer.GameHwndAttached += () => Dispatcher.BeginInvoke(new Action(() => { LayoutGameScreen(); FocusGame(); HideLoadingSoon(); }));

        _easyRpgRenderer.NotificationReceived += msg => Dispatcher.Invoke(() => ShowHudMessage(msg));
        _easyRpgRenderer.HotkeyReceived += OnWebHotkeyReceived;
        _easyRpgRenderer.EspDataUpdated += OnEspDataUpdated;
        _easyRpgRenderer.TileInfoUpdated += OnTileInfoUpdated;
        _easyRpgRenderer.GameStateUpdated += OnGameStateUpdated;
        _easyRpgRenderer.GameHwndAttached += () => Dispatcher.BeginInvoke(new Action(() => { LayoutGameScreen(); FocusGame(); HideLoadingSoon(); }));
        _webRenderer.GameReady += () => Dispatcher.BeginInvoke(new Action(HideLoadingNow));

        _mkxpRenderer.StartupScriptError += OnMkxpStartupScriptError;
        _webRenderer.GameFatalError += OnWebGameFatalError;
        _webRenderer.ProcessExited += () => OnNativeProcessExited(_webRenderer);
        _webRenderer.ProcessCrashed += (text, gone) => Dispatcher.BeginInvoke(() => OnWebGameCrashed(text, gone));
        _mkxpRenderer.ProcessExited += () => OnNativeProcessExited(_mkxpRenderer);
        _easyRpgRenderer.ProcessExited += () => OnNativeProcessExited(_easyRpgRenderer);
        if (_ctl.Settings.HotkeySchema < HotkeyManager.CurrentSchema)
        {
            if (HotkeyManager.MigrateSaved(_ctl.Settings.Hotkeys, _ctl.Settings.HotkeySchema)) UiLog.Write("hotkeys: migrated old defaults");
            _ctl.Settings.HotkeySchema = HotkeyManager.CurrentSchema;
            _ctl.SaveSettings();
        }
        HotkeyManager.Load(_ctl.Settings.Hotkeys);
        RefreshMenuGestures();
        HotkeyManager.StreamerMode = _ctl.Settings.StreamerMode;
        NoteAppearance.Apply(_ctl.Settings);
        InitMulti();
        StreamerMenuItem.IsChecked = _ctl.Settings.StreamerMode;
        RenpyMsgBar.SetQuickSaveVisible(!_ctl.Settings.StreamerMode);
        _discord.Streaming = _ctl.Settings.StreamerMode;

        if (!string.IsNullOrWhiteSpace(_ctl.Settings.UiFontFamily))
        {
            ApplyUiFont(_ctl.Settings.UiFontFamily);
        }

        LayoutGameScreen();
        InitSpinner();
        DiscordMenuItem.IsChecked = _ctl.Settings.DiscordPresence;
        UpdateChannelChecks();
        _discord.SetEnabled(_ctl.Settings.DiscordPresence);

        SetupPinnedHud();
        NotesHost.CloseRequested += () => SetNotesVisible(false);
        NotesHost.ScreenshotRequested += () => _ = TakeScreenshot(toNote: true);
        NotesHost.GalleryRequested += () => OpenGallery(insertIntoNotes: true);
        NotesHost.DetachRequested += ToggleNotesDetached;
        RenpyMsgBar.Bind(_autoAdvance);
        RenpyMsgBar.QuickSaveRequested += () => _currentBridge?.QuickSave();
        RenpyMsgBar.QuickLoadRequested += () => _currentBridge?.QuickLoad();
        RenpyMsgBar.Interacted += () => Dispatcher.BeginInvoke(() => { if (IsMultiGuest) _multiView?.Focus(); else FocusGame(); }, DispatcherPriority.Input);
        RenpyMsgBar.RemoteAction += (action, value) => SendGuestTool(action, value);
        RenpyMsgBar.SpeedChanged += (speed, auto) =>
            ShowHudMessage($"자동 넘김 속도: {speed:0.0}x{(auto ? "" : " (자동 넘김을 켜면 적용)")}");
        _autoAdvance.StateChanged += UpdateMessageMenuItems;
        SetMessageBarVisibility(_ctl.Settings.ShowMessageBar);
        _msgHideTimer.Tick += (_, _) => { _msgHideTimer.Stop(); _dialogueVisible = false; UpdateMessageBarVisibility(); };
        _webRenderer.MessageStateChanged += s => OnMessageStateChanged(_webRenderer, s);
        ((IGameBridge)_mkxpRenderer).MessageStateChanged += s => OnMessageStateChanged(_mkxpRenderer, s);
        ((IGameBridge)_easyRpgRenderer).MessageStateChanged += s => OnMessageStateChanged(_easyRpgRenderer, s);
        _webRenderer.ChoiceChanged += c => Dispatcher.BeginInvoke(() => OnGameChoice(_webRenderer, c));
        _mkxpRenderer.ChoiceChanged += c => OnGameChoice(_mkxpRenderer, c);
        _easyRpgRenderer.ChoiceChanged += c => OnGameChoice(_easyRpgRenderer, c);
        UpdateMenuEnabledState(false);

        StartAutoTestIfRequested();
        Dispatcher.BeginInvoke(OfferTutorialOnce, DispatcherPriority.ApplicationIdle);
    }

    readonly Dictionary<string, string> _historyTitleCache = new(StringComparer.OrdinalIgnoreCase);

    string HistoryTitle(string dir)
    {
        if (_historyTitleCache.TryGetValue(dir, out var cached)) return cached;
        string title = Path.GetFileName(dir.TrimEnd('\\', '/'));
        try
        {
            var gi = CoreInterop.GameInfo.Create();
            if (CoreInterop.rpg_detect_game(dir, ref gi) == 0)
            {
                string t = gi.TitleUtf8;
                if (!string.IsNullOrWhiteSpace(t) && !string.Equals(t.Trim(), title, StringComparison.OrdinalIgnoreCase))
                    title = $"{t.Trim()} ({title})";
            }
        }
        catch { }
        _historyTitleCache[dir] = title;
        return title;
    }

    void BuildHistoryMenu()
    {
        HistoryMenu.Items.Clear();
        if (_ctl.Settings.History.Count == 0)
        {
            HistoryMenu.Items.Add(new MenuItem { Header = "(현재 불러온 기록이 없습니다.)", IsEnabled = false });
            return;
        }
        for (int i = 0; i < _ctl.Settings.History.Count; i++)
        {
            var dir = _ctl.Settings.History[i];
            bool exists = Directory.Exists(dir);
            // 밑줄(_)이 액세스 키로 먹히지 않도록 TextBlock으로 표시
            var mi = new MenuItem
            {
                Header = new TextBlock { Text = $"{i + 1}: {HistoryTitle(dir)}{(exists ? "" : " (폴더 없음)")}" },
                ToolTip = dir,
                IsEnabled = exists
            };
            var d = dir;
            mi.Click += (_, _) => Launch(d);
            HistoryMenu.Items.Add(mi);
        }
        HistoryMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "히스토리 모두 삭제(_C)" };
        clear.Click += (_, _) =>
        {
            _ctl.ClearHistory();
            _historyTitleCache.Clear();
            BuildHistoryMenu();
            ShowHudMessage("히스토리를 모두 삭제했습니다.");
        };
        HistoryMenu.Items.Add(clear);
    }

    async void Launch(string dir)
    {
        if (IsMultiGuest)
        {
            MessageBox.Show(this, "방에 참가해 있는 동안에는 게임을 켤 수 없습니다. 게임은 방장이 켭니다.\n직접 하려면 먼저 방에서 나가 주세요.",
                "RocketRPG", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        UiLog.Write($"launch: begin {dir}");
        _currentDir = dir;
        string fallbackTitle = Path.GetFileName(dir.TrimEnd('\\', '/'));
        ShowLoading(fallbackTitle, "게임을 준비하고 있습니다...");
        try
        {
            var gi = CoreInterop.GameInfo.Create();
            CoreInterop.rpg_detect_game(dir, ref gi);

            string displayTitle = !string.IsNullOrWhiteSpace(gi.TitleUtf8) ? gi.TitleUtf8 : fallbackTitle;
            ShowLoading(displayTitle, $"{EngineName(gi.Engine)} 엔진 준비 중...");

            bool isMvMz = (gi.Engine == CoreInterop.EngineMv || gi.Engine == CoreInterop.EngineMz);
            bool isRuby = (gi.Engine == CoreInterop.EngineXp || gi.Engine == CoreInterop.EngineVx || gi.Engine == CoreInterop.EngineAce);
            bool isEasyRpg = (gi.Engine == CoreInterop.Engine2000 || gi.Engine == CoreInterop.Engine2003);

            var gameCfg = GameSettingsService.Load(dir, _ctl.Settings);
            _ctl.Settings.Ratio = gameCfg.Ratio;
            CheckItem("ratio", gameCfg.Ratio);
            LayoutGameScreen();

            _ctl.Settings.Gamma = gameCfg.Gamma;
            CheckItem("gamma", gameCfg.Gamma.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ctl.ApplyGamma(gameCfg.Gamma);

            _ctl.Settings.Volume = gameCfg.Volume;
            CheckVolumeItems(gameCfg.Volume);
            _ctl.SetVolume(gameCfg.Volume);

            _ctl.Settings.Filter = gameCfg.Filter;
            CheckItem("filter", gameCfg.Filter);

            _ctl.Settings.InGameFontFamily = gameCfg.InGameFontFamily;
            _ctl.Settings.InGameFontSize = gameCfg.InGameFontSize;
            _ctl.Settings.InGameFontBold = gameCfg.InGameFontBold;

            _autoAdvance.IsAutoEnabled = gameCfg.AutoMessageEnabled;
            _autoAdvance.Speed = gameCfg.AutoMessageSpeed;
            CheckAutoSpeedItems();

            _frameRate = gameCfg.FrameRate;
            _vsync = gameCfg.VSync;
            CheckFrameItems();

            _hudPos = gameCfg.HudX is double hx && gameCfg.HudY is double hy ? new Point(hx, hy) : null;
            _hudLocked = gameCfg.HudLocked;
            ApplyHudPosition();

            _dismissedNotices = gameCfg.DismissedNotices ?? new();
            IssueBar.Visibility = Visibility.Collapsed;
            _ = CheckGameIssuesAsync(dir, gi.Engine);
            // 메시지 바는 대화 시 자동 표시되므로 게임별이 아닌 전역 설정만 따릅니다
            // (구버전은 '현재 숨김 상태'를 게임별로 false 저장해 버리는 문제가 있었음).
            SetMessageBarVisibility(_ctl.Settings.ShowMessageBar);

            if (isRuby)
            {
                RocketRenderMKXP.EnsureValidGameIni(dir, gi.Engine);
            }

            // 리소스가 실행 파일 안에 묶인 배포본(Enigma Virtual Box, Game_boxed.exe)은 한 번 풀어 캐시에서 실행합니다.
            string? contentDir = null;
            if (isMvMz)
            {
                string? boxedExe = await Task.Run(() => EvbArchive.FindBoxedExe(dir));
                if (boxedExe != null)
                {
                    LoadingStatusSub.Text = "게임 파일을 준비하는 중... (처음 한 번만)";
                    contentDir = await Task.Run(() =>
                    {
                        try { return EvbArchive.EnsureUnboxed(dir, boxedExe, msg => Dispatcher.BeginInvoke(() => LoadingStatusSub.Text = msg)); }
                        catch (Exception ex) { UiLog.Write($"launch: unboxing failed: {ex.Message}"); return null; }
                    });
                    UiLog.Write(contentDir != null ? $"launch: boxed game ({Path.GetFileName(boxedExe)}) unpacked to {contentDir}" : "launch: boxed game could not be unpacked");
                    if (contentDir == null)
                    {
                        LaunchFailed("이 게임은 파일이 실행 파일 안에 묶여 있는데, 풀어내지 못했습니다.\n게임 폴더에 쓰기 권한이 있는지, 디스크 공간이 충분한지 확인해 주세요.");
                        return;
                    }
                }
            }

            if (isMvMz)
            {
                UiLog.Write($"launch: WebView2 pipeline {dir} (Engine: {gi.Engine})");
                _isNativeRunning = true;
                _mkxpRenderer.Stop();
                _easyRpgRenderer.Stop();
                _currentBridge = _webRenderer;
                ClearGameSurface(keepLoading: true);   // 실행 중: 불러오는 중 화면 유지
                NativeScreenHost.Visibility = Visibility.Collapsed;
                WebScreen.Visibility = Visibility.Visible;

                _ctl.Current = gi;
                _ctl.PushHistory(dir);
                BuildHistoryMenu();
                SavePerGame();

                await _webRenderer.StartGameAsync(dir, isMv: gi.Engine == CoreInterop.EngineMv, contentDir: contentDir);
                _webRenderer.SetInGameFont(_ctl.Settings.InGameFontFamily, _ctl.Settings.InGameFontSize, _ctl.Settings.InGameFontBold);
                _webRenderer.SetBrightness(gameCfg.Gamma);
                _webRenderer.SetFilter(_ctl.Settings.Filter ?? RocketShaderSystem.FilterNone);
                _webRenderer.SetVolume(_ctl.Settings.Volume);
                _webRenderer.SetSpeed(GetSelectedSpeed());
                _webRenderer.SetFrameRate(_frameRate);
                _webRenderer.EnableEsp(EspOverlayMenuItem.IsChecked);
                _webRenderer.EnableTileInspector(TileInspectorMenuItem.IsChecked);
                _autoAdvance.Attach(IntPtr.Zero, gi.Engine, _webRenderer);

                OnGameLaunchSuccess();
                Title = $"{BaseTitle} - {displayTitle} ({EngineName(gi.Engine)})";
                ShowHudMessage($"{EngineName(gi.Engine)} 실행됨");
                return;
            }

            if (isRuby || isEasyRpg)
            {
                UiLog.Write($"launch: {(isRuby ? "mkxp-z" : "EasyRPG")} pipeline {dir} (Engine: {gi.Engine})");
                string runtimeName = isRuby ? "mkxp-z" : "EasyRPG Player";
                bool available = isRuby
                    ? CoreInterop.rpg_mkxp_is_available_game(dir) != 0 || !string.IsNullOrEmpty(RocketRenderMKXP.ResolveExecutable(dir))
                    : CoreInterop.rpg_easyrpg_is_available_game(dir) != 0 || !string.IsNullOrEmpty(RocketRenderEasyRPG.ResolveExecutable(dir));
                if (!available)
                {
                    LaunchFailed($"{runtimeName} 실행 파일을 찾을 수 없습니다.\nRocketRPG를 다시 설치해 주세요.");
                    return;
                }

                if (isRuby)
                {
                    // 게임 전용 Win32 DLL은 64비트 mkxp-z에서 불러올 수 없지만, 대부분은 그 기능(동영상/효과)만 빠지고 실행됩니다.
                    var nativeDlls = RocketRenderMKXP.FindIncompatibleDlls(dir);
                    if (nativeDlls.Count > 0) UiLog.Write($"launch: game DLLs not loadable by mkxp-z ({string.Join(", ", nativeDlls)}); running without them");
                }

                _isNativeRunning = true;
                _webRenderer.Stop();
                if (isRuby) _easyRpgRenderer.Stop(); else _mkxpRenderer.Stop();
                ClearGameSurface(keepLoading: true);   // 실행 중: 불러오는 중 화면 유지

                WebScreen.Visibility = Visibility.Collapsed;
                NativeScreenHost.Visibility = Visibility.Visible; // 게임 창을 직접 임베드 (HUD는 OverlayWindow)

                _currentBridge = isRuby ? _mkxpRenderer : _easyRpgRenderer;
                _ctl.Current = gi;
                _ctl.PushHistory(dir);
                BuildHistoryMenu();
                SavePerGame();

                var nativeHost = new NativeSurfaceHwndHost();
                nativeHost.Resized += (w, h) => ResizeHostedGameWindow(w, h);
                NativeScreenHost.Content = nativeHost;
                NativeScreenHost.UpdateLayout();
                LayoutGameScreen();

                bool started;
                if (isRuby)
                {
                    started = await _mkxpRenderer.StartGameAsync(
                        dir,
                        gi.Engine,
                        parentHwnd: nativeHost.Handle,
                        fixedAspectRatio: _ctl.Settings.Ratio != "none",
                        smoothScaling: _ctl.Settings.Filter == "quality",
                        frameCallback: null,
                        parentHwndProvider: () => nativeHost.Handle,
                        inGameFontFamily: _ctl.Settings.InGameFontFamily,
                        inGameFontSize: _ctl.Settings.InGameFontSize,
                        inGameFontBold: _ctl.Settings.InGameFontBold,
                        filter: _ctl.Settings.Filter,
                        vsync: _vsync);
                    if (started)
                    {
                        _mkxpRenderer.SetVolume(_ctl.Settings.Volume);
                        _mkxpRenderer.SetBrightness(gameCfg.Gamma);
                        _mkxpRenderer.SetSpeed(GetSelectedSpeed());
                        _mkxpRenderer.EnableEsp(EspOverlayMenuItem.IsChecked);
                        _mkxpRenderer.EnableTileInspector(TileInspectorMenuItem.IsChecked);
                        _autoAdvance.Attach(_mkxpRenderer.GameHwnd, gi.Engine, _mkxpRenderer);
                    }
                }
                else
                {
                    started = await _easyRpgRenderer.StartGameAsync(
                        dir,
                        gi.Engine,
                        parentHwnd: nativeHost.Handle,
                        fixedAspectRatio: _ctl.Settings.Ratio != "none",
                        smoothScaling: (_ctl.Settings.Filter ?? RocketShaderSystem.FilterNone) != RocketShaderSystem.FilterNone,
                        frameCallback: null,
                        parentHwndProvider: () => nativeHost.Handle,
                        inGameFontFamily: _ctl.Settings.InGameFontFamily,
                        inGameFontSize: _ctl.Settings.InGameFontSize,
                        inGameFontBold: _ctl.Settings.InGameFontBold,
                        frameRate: _frameRate,
                        vsync: _vsync);
                    if (started)
                    {
                        _easyRpgRenderer.SetVolume(_ctl.Settings.Volume);
                        _easyRpgRenderer.SetBrightness(gameCfg.Gamma);
                        _easyRpgRenderer.ApplyCrt(_ctl.Settings.Filter);
                        _easyRpgRenderer.SetSpeed(GetSelectedSpeed());
                        _easyRpgRenderer.EnableEsp(EspOverlayMenuItem.IsChecked);
                        _easyRpgRenderer.EnableTileInspector(TileInspectorMenuItem.IsChecked);
                        _autoAdvance.Attach(_easyRpgRenderer.GameHwnd, gi.Engine, _easyRpgRenderer);
                    }
                }

                if (!started)
                {
                    UiLog.Write($"launch: {runtimeName} failed to start");
                    _isNativeRunning = false;
                    _currentBridge = null;
                    _mkxpRenderer.Stop();
                    _easyRpgRenderer.Stop();
                    ClearGameSurface();
                    LaunchFailed($"{runtimeName}로 게임을 시작하지 못했습니다.");
                    return;
                }

                OnGameLaunchSuccess();
                Title = $"{BaseTitle} - {displayTitle} ({EngineName(gi.Engine)})";
                ShowHudMessage($"{EngineName(gi.Engine)} 실행됨");
                return;
            }

            LaunchFailed("RPG Maker 게임을 찾지 못했습니다.\n게임 폴더(Game.exe 또는 RPG_RT.exe가 있는 폴더)를 골라 주세요.");
        }
        catch (Exception ex)
        {
            UiLog.Write($"launch: EXCEPTION {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
            LaunchFailed($"게임 실행 중 오류가 발생했습니다:\n{ex.Message}");
        }
    }

    void LaunchFailed(string message)
    {
        UpdateMenuEnabledState(false);
        HideLoading();
        UiLog.Write($"launch: FAILED {message.Replace('\n', ' ')}");
        MessageBox.Show(this, message, "실행 실패", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    void OnLoadGame(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "쯔꾸르 게임 폴더 선택" };
        if (dlg.ShowDialog(this) == true)
            Launch(dlg.FolderName);
    }

    /// <summary>
    /// 키보드 포커스를 게임에 돌려줍니다. 메시지 바 등 RocketRPG UI를 클릭하면 포커스가 게임에서 빠져
    /// 게임이 입력을 받지 않거나(MV/MZ는 포커스를 잃으면 멈춤) 잠시 멈춘 것처럼 보였습니다.
    /// </summary>
    void FocusGame()
    {
        if (!_isNativeRunning)
        {
            RenderScreen.Focus();
            return;
        }
        if (!IsActive) Activate();
        if (ReferenceEquals(_currentBridge, _webRenderer))
        {
            WebScreen.Focus();
            return;
        }
        IntPtr game = _currentBridge is RocketRenderMKXP mk ? mk.GameHwnd
                    : _currentBridge is RocketRenderEasyRPG er ? er.GameHwnd : IntPtr.Zero;
        if (game == IntPtr.Zero || !NativeMethods.IsWindow(game)) return;
        // 다른 프로세스의 자식 창은 부모와 입력 큐를 함께 써야 키보드 포커스를 받습니다(Windows가 SetParent 때 붙여 줌).
        // 예전에는 SetFocus 뒤 AttachThreadInput(false)로 떼어 냈는데, 그러면 원래 붙어 있던 큐까지 끊겨
        // 전경 스레드에 포커스 창이 없어지고 게임이 키 입력을 받지 못했습니다. 게임이 도는 동안에는 붙여 둡니다.
        // 또 게임 창은 최상위 창으로 뜬 뒤(이때 전경 창이 됨) 임베드되므로 RocketRPG 창을 전경으로 되돌립니다
        // (안 그러면 제목 표시줄이 흐리고 단축키·메뉴 포커스가 꼬였음).
        uint gameThread = NativeMethods.GetWindowThreadProcessId(game, out _);
        uint me = NativeMethods.GetCurrentThreadId();
        if (gameThread != 0 && gameThread != me && NativeMethods.AttachThreadInput(me, gameThread, true))
        {
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            NativeMethods.SetFocus(game);
        }
    }

    // ── M3: 세션 종료/재시작 시 화면 정리 ──

    void ClearGameSurface(bool keepLoading = false)
    {
        if (!keepLoading) HideLoading();
        int mkxpPid = _mkxpRenderer.ProcessId;
        int easyRpgPid = _easyRpgRenderer.ProcessId;

        if (mkxpPid > 0) KillProcessTree(mkxpPid);
        if (easyRpgPid > 0) KillProcessTree(easyRpgPid);
        OnGameAudioSessionEnded();

        NativeScreenHost.Content = null;
        NativeScreenHost.Visibility = Visibility.Collapsed;
        _autoAdvance.Detach();
        UpdateMenuEnabledState(false);
    }

    void StopNativeSession(string hudMessage)
    {
        _isNativeRunning = false;
        _discord.ShowIdle();
        MultiReportGame("");
        NotesHost.CloseGame();   // 게임 노트 → 일반 메모
        _webRenderer.Stop();
        _mkxpRenderer.Stop();
        _easyRpgRenderer.Stop();
        _currentBridge = null;
        WebScreen.Visibility = Visibility.Collapsed;
        NativeScreenHost.Visibility = Visibility.Collapsed;
        _cachedEspItems = new();
        EspCanvas.Clear();
        TileInspectorHud.Visibility = Visibility.Collapsed;
        ClearGameSurface();
        Title = BaseTitle;
        ShowHudMessage(hudMessage);
    }

    // 게임 프로세스가 스스로 종료되거나 튕긴 경우: 메시지 바/HUD 등 게임 전용 UI를 모두 정리합니다.
    void OnNativeProcessExited(IGameBridge source)
    {
        if (!_isNativeRunning || !ReferenceEquals(_currentBridge, source)) return;
        UiLog.Write($"session: native game exited ({source.GetType().Name})");
        StopNativeSession("게임이 종료되었습니다.");
    }

    void OnExit(object s, RoutedEventArgs e) { _ctl.SavePerGameSettings(); Close(); }

    void OnGameLibrary(object sender, RoutedEventArgs e)
    {
        var libWin = new GameLibraryWindow(_ctl)
        {
            Owner = this
        };
        libWin.GameSelected += gamePath =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Launch(gamePath);
            }));
        };
        libWin.Show();
    }

    void OnFontSettings(object sender, RoutedEventArgs e) => ShowFontSettings(0);
    void OnInGameFontSettings(object sender, RoutedEventArgs e) => ShowFontSettings(1);

    void ShowFontSettings(int tab)
    {
        // 처음 열 때는 설치된 글꼴 목록을 만드느라 잠시 걸립니다
        Mouse.OverrideCursor = Cursors.Wait;
        FontSettingsWindow fontWin;
        try
        {
            fontWin = new FontSettingsWindow(_ctl, ApplyUiFont, initialTabIndex: tab, hasGame: _currentBridge != null, engine: _ctl.Current.Engine)
            {
                Owner = this,
                FontFamily = this.FontFamily
            };
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        var before = (_ctl.Settings.InGameFontFamily ?? "", _ctl.Settings.InGameFontSize, _ctl.Settings.InGameFontBold);
        string fallbackBefore = _ctl.Settings.MissingFontFallback ?? "";
        if (fontWin.ShowDialog() != true) return;
        _currentBridge?.SetInGameFont(_ctl.Settings.InGameFontFamily, _ctl.Settings.InGameFontSize, _ctl.Settings.InGameFontBold);
        SavePerGame();
        var after = (_ctl.Settings.InGameFontFamily ?? "", _ctl.Settings.InGameFontSize, _ctl.Settings.InGameFontBold);
        if (before == after)
        {
            // 빠진 글꼴 대체만 바뀜: XP/VX/Ace 게임에서 게임 기본 글꼴을 쓸 때만 영향이 있고, 다시 시작해야 적용됩니다.
            if (fallbackBefore != (_ctl.Settings.MissingFontFallback ?? "") && _currentBridge == _mkxpRenderer && string.IsNullOrWhiteSpace(after.Item1))
                AskRestartToApply("빠진 글꼴 대체");
            return;
        }
        string name = string.IsNullOrWhiteSpace(after.Item1) ? "게임 기본 글꼴" : after.Item1;
        // MV/MZ는 바로 바뀌고, XP/VX/Ace·2000/2003은 게임이 시작할 때 글꼴을 정하므로 다시 시작해야 합니다.
        if (_currentBridge == null || _currentBridge == _webRenderer) ShowHudMessage($"인게임 글꼴: {name}");
        else AskRestartToApply($"인게임 글꼴({name})");
    }

    private void OnGameLaunchSuccess()
    {
        UpdateMenuEnabledState(true);
        HideLoadingWhenReady();
        NoclipMenuItem.IsChecked = false;   // 새 게임은 벽 통과가 꺼진 채 시작 (이전 게임의 체크가 남아 어긋났음)
        LayoutGameScreen();   // 메시지 바 자리 반영
        UpdateFilterAvailability();
        UpdateFrameAvailability();
        UpdateVolumeAvailability();
        ScheduleAutoRoute();   // 소리 > 출력 장치를 골라 두었으면 새 게임에도 적용
        _notes?.OpenGame(_currentDir, CurrentGameTitle());
        _discord.ShowGame(CurrentGameTitle(), EngineName(_ctl.Current.Engine));
        MultiReportGame(CurrentGameTitle());
        // 이 게임에 저장된 자동 넘김이 켜진 채 시작하면 알려 줍니다 (켜져 있는지 몰라 헷갈리지 않게).
        _shownAuto = _autoAdvance.IsAuto;
        _shownSkip = _autoAdvance.IsSkip;
        if (_autoAdvance.IsAuto) ShowHudMessage($"메시지 자동 넘김: 켜짐 (속도 {_autoAdvance.Speed:0.0}x)", 3000);
    }

    // ── 디스코드 Rich Presence ──

    readonly DiscordPresence _discord = new();

    protected override void OnClosed(EventArgs e)
    {
        _discord.Dispose();
        NotesHost.Flush();
        if (_notesWindow != null) { _ctl.Settings.NotesWindowBounds = _notesWindow.Bounds; _ctl.SaveSettings(); _notesWindow.ReallyClose(); }
        _dockWindow?.Close();
        ClearGameSurface();
        EspCanvas.Clear();
        ToastStack.Children.Clear();
        _overlay.Close();
        _nativeHotkeys?.Dispose();
        _nativeMouseTimer.Stop();
        _currentBridge = null;
        _autoAdvance.Dispose();
        _webRenderer.Dispose();
        _mkxpRenderer.Dispose();
        _easyRpgRenderer.Dispose();
        _ctl.Dispose();
        base.OnClosed(e);
    }
}
