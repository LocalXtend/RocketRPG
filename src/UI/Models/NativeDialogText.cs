#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RocketRPG.Models;

/// <summary>프로세스가 띄운 메시지 상자(#32770)의 본문을 읽습니다 (네이티브 런타임의 스크립트 오류 감지용).</summary>
public static class NativeDialogText
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc fn, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc fn, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder s, int n);

    /// <summary>보이는 대화상자의 정적 텍스트를 모아 반환합니다. 없으면 null.</summary>
    public static string? Find(int pid)
    {
        string? found = null;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p != (uint)pid || !IsWindowVisible(h)) return true;
            var cls = new StringBuilder(32);
            GetClassNameW(h, cls, 32);
            if (cls.ToString() != "#32770") return true;
            var sb = new StringBuilder();
            EnumChildWindows(h, (c, _) =>
            {
                var cc = new StringBuilder(32);
                GetClassNameW(c, cc, 32);
                if (cc.ToString() != "Static") return true;
                var t = new StringBuilder(2048);
                GetWindowTextW(c, t, 2048);
                if (t.Length > 0) sb.AppendLine(t.ToString());
                return true;
            }, IntPtr.Zero);
            if (sb.Length > 0) { found = sb.ToString().Trim(); return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
