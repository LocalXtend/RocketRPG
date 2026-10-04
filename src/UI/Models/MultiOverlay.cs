#nullable enable
using System;
using System.Runtime.InteropServices;

namespace RocketRPG.Models;

/// <summary>
/// 멀티: RocketRPG 창 맨 위(게임 창 위 포함)에 그리는 흐르는 채팅과 핑 (RocketRPGCore의 DirectComposition 층).
/// 창 자체의 합성이라 디스코드 창 공유에 보이고, 방장 방송(게임이 직접 주는 화면)에는 섞이지 않습니다. UI 스레드에서만 부릅니다.
/// </summary>
public sealed class MultiOverlay : IDisposable
{
    const string Dll = "RocketRPGCore.dll";
    [DllImport(Dll)] static extern int rpg_overlay_open(IntPtr hwnd, out IntPtr overlay);
    [DllImport(Dll)] static extern void rpg_overlay_area(IntPtr overlay, int x, int y, int w, int h);
    [DllImport(Dll, CharSet = CharSet.Unicode)] static extern void rpg_overlay_chat(IntPtr overlay, string text, uint color, int fixedLane);
    [DllImport(Dll)] static extern void rpg_overlay_ping(IntPtr overlay, int id, float nx, float ny, int fresh, uint color);
    [DllImport(Dll)] static extern void rpg_overlay_remove(IntPtr overlay, int id);
    [DllImport(Dll)] static extern void rpg_overlay_clear(IntPtr overlay, int what);
    [DllImport(Dll)] static extern void rpg_overlay_tick(IntPtr overlay);
    [DllImport(Dll)] static extern void rpg_overlay_close(IntPtr overlay);

    IntPtr _h;

    MultiOverlay(IntPtr h) => _h = h;

    public static MultiOverlay? Open(IntPtr hwnd)
    {
        try
        {
            if (rpg_overlay_open(hwnd, out var h) == 0 && h != IntPtr.Zero) return new MultiOverlay(h);
            UiLog.Write($"multi: overlay unavailable ({CoreInterop.LastError()})");
        }
        catch (Exception ex) { UiLog.Write($"multi: overlay failed {ex.Message}"); }
        return null;
    }

    /// <summary>게임 화면 영역 (창 클라이언트 좌표, 물리 픽셀)</summary>
    public void SetArea(int x, int y, int w, int h) { if (_h != IntPtr.Zero) rpg_overlay_area(_h, x, y, w, h); }
    public void Chat(string text, int color = 0, bool fixedLane = false) { if (_h != IntPtr.Zero) rpg_overlay_chat(_h, text, MultiChatStyle.ChatColor(color), fixedLane ? 1 : 0); }
    public void Ping(int id, double nx, double ny, bool fresh, uint color = 0xFFD34D) { if (_h != IntPtr.Zero) rpg_overlay_ping(_h, id, (float)nx, (float)ny, fresh ? 1 : 0, color); }
    public void RemovePing(int id) { if (_h != IntPtr.Zero) rpg_overlay_remove(_h, id); }
    public void ClearChat() { if (_h != IntPtr.Zero) rpg_overlay_clear(_h, 1); }
    public void ClearPings() { if (_h != IntPtr.Zero) rpg_overlay_clear(_h, 2); }
    public void Tick() { if (_h != IntPtr.Zero) rpg_overlay_tick(_h); }

    public void Dispose()
    {
        if (_h == IntPtr.Zero) return;
        rpg_overlay_close(_h);
        _h = IntPtr.Zero;
    }
}
