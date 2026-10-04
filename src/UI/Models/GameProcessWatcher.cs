#nullable enable
using System;
using System.Diagnostics;
using System.Windows;

namespace RocketRPG.Models;

/// <summary>
/// 게임 프로세스 종료(정상 종료/튕김 모두)를 감지해 UI 스레드에서 Exited를 발화합니다.
/// Watch(0)으로 감시를 해제하며, 해제 후에는 이전 프로세스의 종료 알림을 무시합니다.
/// </summary>
public sealed class GameProcessWatcher
{
    Process? _proc;
    int _generation;

    public event Action? Exited;

    public void Watch(int pid)
    {
        int gen = ++_generation;
        try { if (_proc != null) _proc.EnableRaisingEvents = false; } catch { }
        _proc?.Dispose();
        _proc = null;
        if (pid <= 0) return;

        try
        {
            var p = Process.GetProcessById(pid);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => Raise(gen);
            _proc = p;
            if (p.HasExited) Raise(gen);
        }
        catch (ArgumentException)
        {
            Raise(gen); // 이미 종료됨
        }
        catch (Exception ex)
        {
            UiLog.Write($"GameProcessWatcher: cannot watch pid {pid}: {ex.Message}");
        }
    }

    void Raise(int gen)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            if (gen != _generation) return;
            Watch(0);
            Exited?.Invoke();
        });
    }
}
