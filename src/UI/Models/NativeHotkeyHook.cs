#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace RocketRPG.Models;

/// <summary>
/// 네이티브 게임 창(mkxp-z/EasyRPG)을 임베드하면 키보드 포커스가 게임 프로세스에 있어 WPF가 키를 받지 못합니다.
/// RocketRPG 창이 전면이고 포커스가 게임 프로세스 창에 있을 때만 저수준 키보드 훅으로 단축키를 가로챕니다.
/// 일치한 단축키는 게임에 전달하지 않습니다(삼킴).
/// 훅은 전용 스레드(자체 메시지 루프)에 둡니다. UI 스레드에 두면 UI가 잠깐이라도 멈췄을 때(노트 입력, 맵 뷰어 열기 등)
/// 훅 응답이 늦어지고, Windows는 늦는 저수준 훅을 알림 없이 제거해 그 뒤로 모든 단축키(도구)가 동작하지 않았습니다.
/// </summary>
public sealed class NativeHotkeyHook : IDisposable
{
    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    const uint WM_QUIT = 0x0012;

    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    // 누르고 있는 키 (길게 누르면 오는 반복 입력으로 켜기/끄기가 계속 뒤집히지 않게)
    readonly System.Collections.Generic.HashSet<uint> _down = new();

    readonly LowLevelKeyboardProc _proc;
    IntPtr _hook;
    uint _threadId;
    readonly System.Threading.Thread _thread;
    readonly Func<IntPtr> _mainHwnd;
    readonly Func<int> _gamePid;
    readonly Func<string, bool> _execute;
    readonly Func<string, bool>? _canExecute;
    // 휠(가운데) 클릭: RocketRPG 창이 전면일 때 화면 좌표로 알림 (멀티 핑)
    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] static extern IntPtr SetMouseHookEx(int idHook, LowLevelMouseProc fn, IntPtr hMod, uint threadId);
    const int WH_MOUSE_LL = 14, WM_MBUTTONDOWN = 0x0207;
    readonly LowLevelMouseProc? _mouseProc;
    readonly Action<int, int>? _middleDown;
    IntPtr _mouseHook;

    /// <param name="mainHwnd">RocketRPG 메인 창</param>
    /// <param name="gamePid">현재 임베드된 네이티브 게임 프로세스 ID (없으면 0 → 훅 비활성)</param>
    /// <param name="execute">단축키 ID 실행 (처리했으면 true)</param>
    public NativeHotkeyHook(Func<IntPtr> mainHwnd, Func<int> gamePid, Func<string, bool> execute, Action<int, int>? middleDown = null, Func<string, bool>? canExecute = null)
    {
        _mainHwnd = mainHwnd;
        _gamePid = gamePid;
        _execute = execute;
        _canExecute = canExecute;
        _proc = HookProc;
        _middleDown = middleDown;
        if (middleDown != null) _mouseProc = MouseProc;
        var ready = new System.Threading.ManualResetEventSlim();
        _thread = new System.Threading.Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
            if (_hook == IntPtr.Zero) UiLog.Write($"NativeHotkeyHook: install failed ({Marshal.GetLastWin32Error()})");
            if (_mouseProc != null)
            {
                _mouseHook = SetMouseHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandleW(null), 0);
                if (_mouseHook == IntPtr.Zero) UiLog.Write($"NativeHotkeyHook: mouse hook failed ({Marshal.GetLastWin32Error()})");
            }
            ready.Set();
            // 훅 콜백은 이 스레드의 메시지 루프에서 불립니다.
            while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        })
        { IsBackground = true, Name = "RocketRPG hotkey hook" };
        _thread.Start();
        ready.Wait(2000);
    }

    bool FocusInGame()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg != _mainHwnd()) return false;
        int pid = _gamePid();
        if (pid <= 0) return false;
        uint tid = GetWindowThreadProcessId(fg, out _);
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        IntPtr focus = GetGUIThreadInfo(tid, ref gti) ? gti.hwndFocus : IntPtr.Zero;
        // 게임 창은 다른 스레드(다른 프로세스)이므로 메인 창 스레드 정보의 포커스가 비어 있거나 게임 창입니다.
        if (focus != IntPtr.Zero)
        {
            GetWindowThreadProcessId(focus, out uint fpid);
            return fpid == (uint)pid;
        }
        return true;
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == WM_KEYUP || wParam == WM_SYSKEYUP))
                _down.Remove(Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode);
            if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) && FocusInGame())
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool repeat = !_down.Add(kb.vkCode);
                Key key = KeyInterop.KeyFromVirtualKey((int)kb.vkCode);
                if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin))
                {
                    ModifierKeys mods = ModifierKeys.None;
                    if ((GetAsyncKeyState(0x11) & 0x8000) != 0) mods |= ModifierKeys.Control;
                    if ((GetAsyncKeyState(0x12) & 0x8000) != 0) mods |= ModifierKeys.Alt;
                    if ((GetAsyncKeyState(0x10) & 0x8000) != 0) mods |= ModifierKeys.Shift;
                    string gesture = HotkeyManager.GestureToString(mods, key);
                    foreach (var h in HotkeyManager.ActiveHotkeys)
                    {
                        if (!string.IsNullOrEmpty(h.CurrentGesture) && HotkeyManager.IsAllowed(h.Id) && (_canExecute?.Invoke(h.Id) ?? true) &&
                            string.Equals(h.CurrentGesture, gesture, StringComparison.OrdinalIgnoreCase))
                        {
                            string id = h.Id;
                            // 훅 콜백은 짧게: 실제 처리는 UI 디스패처에서. 반복 입력은 게임에 보내지 않고 무시만 합니다.
                            if (!repeat) System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _execute(id));
                            return (IntPtr)1;
                        }
                    }
                }
            }
        }
        catch { }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam == WM_MBUTTONDOWN && GetForegroundWindow() == _mainHwnd())
            {
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _middleDown!(m.x, m.y));
            }
        }
        catch { }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _threadId = 0;
    }
}
