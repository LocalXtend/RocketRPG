#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace RocketRPG.Models;

public class RocketTextAutoAdvance : IDisposable
{
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private IntPtr _gameHwnd = IntPtr.Zero;
    private int _engine = 0;
    private IGameBridge? _bridge;

    private bool _isAutoEnabled;
    private bool _isSkipEnabled;
    private double _speed = 1.0;

    private DateTime _messageStartTime = DateTime.MinValue;
    private int _lastCharCount = 0;
    private bool _waitingForAdvance = false;

    public bool IsAuto => IsAutoEnabled;
    public bool IsSkip => IsSkipEnabled;

    public bool IsAutoEnabled
    {
        get => _isAutoEnabled;
        set
        {
            if (_isAutoEnabled != value)
            {
                _isAutoEnabled = value;
                UpdateTimer();
                SyncToBridge();
                StateChanged?.Invoke();
            }
        }
    }

    public bool IsSkipEnabled
    {
        get => _isSkipEnabled;
        set
        {
            if (_isSkipEnabled != value)
            {
                _isSkipEnabled = value;
                UpdateTimer();
                SyncToBridge();
                StateChanged?.Invoke();
            }
        }
    }

    public double Speed
    {
        get => _speed;
        set
        {
            double clamped = Math.Clamp(value, 0.5, 3.0);
            if (Math.Abs(_speed - clamped) > 0.01)
            {
                _speed = clamped;
                SyncToBridge();
                StateChanged?.Invoke();
            }
        }
    }

    public event Action? StateChanged;
    public event Action<string>? StatusTextUpdated;

    public RocketTextAutoAdvance()
    {
        _tickTimer.Tick += OnTick;
    }

    // 자동 넘김/스킵이 켜져 있을 때만 타이머를 돌립니다 (꺼져 있으면 초당 20번 깨어날 이유가 없음).
    private void UpdateTimer() => _tickTimer.IsEnabled = _isAutoEnabled || _isSkipEnabled;

    // 게임 창이 실행 뒤에 생기는 경우를 위해 창을 나중에 찾아 쓸 수 있게 합니다.
    private Func<IntPtr>? _hwndProvider;

    public void Attach(IntPtr hwnd, int engine, IGameBridge? bridge, Func<IntPtr>? hwndProvider = null)
    {
        _gameHwnd = hwnd;
        _hwndProvider = hwndProvider;
        _engine = engine;
        _bridge = bridge;
        _waitingForAdvance = false;
        _messageStartTime = DateTime.MinValue;
        SyncToBridge();
    }

    public void Detach()
    {
        _gameHwnd = IntPtr.Zero;
        _hwndProvider = null;
        _bridge = null;
        _waitingForAdvance = false;
    }

    public void ToggleAuto() => IsAutoEnabled = !IsAutoEnabled;
    public void ToggleSkip() => IsSkipEnabled = !IsSkipEnabled;

    public static double CalculateWaitTimeMs(int charCount, double speed = 1.0)
    {
        double baseMs = 1100.0;
        double perCharMs = 30.0;
        double raw = baseMs + (Math.Max(0, charCount) * perCharMs);
        double clamped = Math.Clamp(raw, 750.0, 4500.0);
        return clamped / Math.Max(0.2, speed);
    }

    // 대화 감지를 지원하는 엔진은 게임 내부에서 자동 넘김/스킵을 처리합니다 (대화가 없을 때 키 입력 금지).
    private bool BridgeHandlesMessages => _bridge?.SupportsMessageDetection == true;

    private void SyncToBridge()
    {
        if (_bridge == null) return;
        _bridge.SetAutoMessage(_isAutoEnabled, _speed);
        _bridge.SetSkipMessage(_isSkipEnabled);
    }

    /// <summary>메시지 바의 '다음' 버튼: 대화 감지 엔진은 게임 내부에서, 나머지는 결정 키로 넘깁니다.</summary>
    public void Advance()
    {
        if (BridgeHandlesMessages) _bridge!.AdvanceMessage();
        else TriggerConfirmKey();
    }

    private int _skipTickCounter = 0;

    private void OnTick(object? sender, EventArgs e)
    {
        if (BridgeHandlesMessages)
        {
            if (_isSkipEnabled) StatusTextUpdated?.Invoke("고속 스킵 중...");
            else if (_isAutoEnabled) StatusTextUpdated?.Invoke("대화 자동 넘김 활성");
            return;
        }
        if (_hwndProvider != null && (_isAutoEnabled || _isSkipEnabled))
        {
            IntPtr h = _hwndProvider();
            if (h != IntPtr.Zero) _gameHwnd = h;
        }
        if (_gameHwnd == IntPtr.Zero) return;

        // Skip mode: Rapid pulse Enter key
        if (_isSkipEnabled)
        {
            _skipTickCounter++;
            if (_skipTickCounter % 2 == 0) // every 100ms
            {
                TriggerConfirmKey();
                StatusTextUpdated?.Invoke("고속 스킵 중...");
            }
            return;
        }

        // Auto Advance mode for engines without dialogue detection
        if (_isAutoEnabled)
        {
            // For mkxp-z and EasyRPG, advance dialog at calculated pace
            if (!_waitingForAdvance)
            {
                _waitingForAdvance = true;
                _messageStartTime = DateTime.UtcNow;
                _lastCharCount = 20; // Default nominal dialogue line length
                StatusTextUpdated?.Invoke("대화 자동 읽는 중...");
            }
            else
            {
                double waitMs = CalculateWaitTimeMs(_lastCharCount, _speed);
                double elapsed = (DateTime.UtcNow - _messageStartTime).TotalMilliseconds;
                if (elapsed >= waitMs)
                {
                    TriggerConfirmKey();
                    _messageStartTime = DateTime.UtcNow;
                    StatusTextUpdated?.Invoke("다음 대화 넘김");
                }
                else
                {
                    double remainingSec = Math.Max(0, (waitMs - elapsed) / 1000.0);
                    StatusTextUpdated?.Invoke($"자동 진행 대기 ({remainingSec:0.0}s)");
                }
            }
        }
        else
        {
            _waitingForAdvance = false;
        }
    }

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int VK_RETURN = 0x0D;
    private const int VK_SPACE = 0x20;
    private const int VK_C = 0x43;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    /// <summary>
    /// 확인(Enter) 키를 사용자가 누르는 것처럼 보냅니다: 스캔 코드가 든 lParam으로 누르고, 80ms 뒤에 뗍니다.
    /// (눌림/뗌을 연달아 보내면 매 프레임 키 상태를 읽는 게임은 눌린 것을 보지 못해 자동 진행이 되지 않았음)
    /// </summary>
    public void TriggerConfirmKey()
    {
        IntPtr hwnd = _gameHwnd;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            uint scan = MapVirtualKeyW(VK_RETURN, 0);
            int down = 0x1 | ((int)scan << 16);
            int up = down | unchecked((int)(1u << 30 | 1u << 31));
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)down);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            t.Tick += (_, _) => { t.Stop(); PostMessage(hwnd, WM_KEYUP, (IntPtr)VK_RETURN, (IntPtr)up); };
            t.Start();
        }
        catch { }
    }

    public void Dispose()
    {
        _tickTimer.Stop();
        Detach();
    }
}
