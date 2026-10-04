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

public partial class MainWindow
{
    // ── 노트 (게임 노트 / 일반 메모): 오른쪽에 붙이거나 따로 창으로 ──

    NotesPanel _notes => NotesHost;
    NotesWindow? _notesWindow;
    NotesDockWindow? _dockWindow;
    double _notesShiftedBy;   // 노트 자리를 만들려고 창을 왼쪽으로 옮긴 만큼 (닫을 때 되돌림)

    bool NotesVisible => _ctl.Settings.NotesDetached
        ? _notesWindow?.IsVisible == true
        : NotesHost.Visibility == Visibility.Visible;

    void OnToggleNotes(object s, RoutedEventArgs e) => SetNotesVisible(!NotesVisible);

    void SetNotesVisible(bool show)
    {
        NotesMenuItem.IsChecked = show;
        if (show == NotesVisible) return;
        if (show)
        {
            // 게임 중이면 그 게임 노트, 게임이 없으면 일반 메모
            if (_currentBridge != null && !string.IsNullOrEmpty(_currentDir)) NotesHost.OpenGame(_currentDir, CurrentGameTitle());
            else NotesHost.EnsureOpen();
        }

        if (_ctl.Settings.NotesDetached)
        {
            if (show) ShowNotesWindow();
            else
            {
                NotesHost.Flush();
                _notesWindow?.Hide();
                FocusGame();
            }
            return;
        }

        if (show)
        {
            EnsureDocked();
            NotesHost.Visibility = Visibility.Visible;
            _dockWindow!.PanelWidth = Math.Clamp(_ctl.Settings.NotesWidth, NotesDockWindow.MinPanelWidth, NotesDockWindow.MaxPanelWidth);
            MakeRoomForDockedNotes();
            _dockWindow.Wanted = true;
        }
        else
        {
            NotesHost.Flush();
            NotesHost.Visibility = Visibility.Collapsed;
            if (_dockWindow != null) _dockWindow.Wanted = false;
            GiveBackDockedNotesRoom();
            FocusGame();
        }
    }

    // 붙인 노트는 RocketRPG 창 오른쪽 바깥에 붙습니다. 화면(작업 영역) 안에 들어오도록 창을 왼쪽으로 옮기고,
    // 그래도 모자라면 창을 줄입니다(게임 화면이 그만큼 작아짐). 최대화 상태면 '노트를 뺀 나머지 전체'로 바꿉니다. 끄면 되돌립니다.
    bool _notesUnmaximized;
    double _notesShrunkBy;

    void MakeRoomForDockedNotes()
    {
        _notesShiftedBy = 0;
        _notesShrunkBy = 0;
        _notesUnmaximized = false;
        if (_dockWindow == null || WindowState == WindowState.Minimized) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (WindowState == WindowState.Maximized)
        {
            var waDev = NotesDockWindow.WorkAreaOf(_dockWindow.OwnerFrame());
            _notesUnmaximized = true;
            WindowState = WindowState.Normal;
            UpdateLayout();
            var b = _dockWindow.OwnerInvisibleBorder();
            Left = waDev.Left / dpi.DpiScaleX - b.Left;
            Top = waDev.Top / dpi.DpiScaleY - b.Top;
            Width = waDev.Width / dpi.DpiScaleX + b.Left + b.Right;
            Height = waDev.Height / dpi.DpiScaleY + b.Top + b.Bottom;
            UpdateLayout();
        }
        var f = _dockWindow.OwnerFrame();
        if (f.IsEmpty) return;
        var wa = NotesDockWindow.WorkAreaOf(f);
        double need = (_dockWindow.OwnerRight() - wa.Right) / dpi.DpiScaleX + _dockWindow.PanelWidth;   // 오른쪽에서 모자라는 만큼
        if (need <= 0) return;
        double roomLeft = Math.Max(0, (f.Left - wa.Left) / dpi.DpiScaleX);
        double shift = Math.Min(need, roomLeft);
        if (shift > 0) { Left -= shift; _notesShiftedBy = shift; need -= shift; }
        if (need > 0)
        {
            double shrink = Math.Min(need, Math.Max(0, ActualWidth - MinWidth));
            Width = ActualWidth - shrink;
            _notesShrunkBy = shrink;
        }
    }

    void GiveBackDockedNotesRoom()
    {
        if (WindowState == WindowState.Normal)
        {
            if (_notesShrunkBy > 0) Width = ActualWidth + _notesShrunkBy;
            if (_notesShiftedBy > 0) Left += _notesShiftedBy;
            if (_notesUnmaximized) WindowState = WindowState.Maximized;
        }
        _notesShiftedBy = 0;
        _notesShrunkBy = 0;
        _notesUnmaximized = false;
    }

    /// <summary>창이 있는 모니터의 작업 영역 (작업 표시줄 제외, WPF 단위). 보조 모니터에서도 맞게.</summary>
    Rect MonitorWorkArea()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi) && PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
            {
                var m = ct.TransformFromDevice;
                var tl = m.Transform(new Point(mi.rcWork.L, mi.rcWork.T));
                var br = m.Transform(new Point(mi.rcWork.R, mi.rcWork.B));
                return new Rect(tl, br);
            }
        }
        catch { }
        return SystemParameters.WorkArea;
    }

    [StructLayout(LayoutKind.Sequential)] struct MonRect { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public MonRect rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);

    void ShowNotesWindow()
    {
        if (_notesWindow == null)
        {
            _notesWindow = new NotesWindow(this, _ctl.Settings.NotesWindowBounds);
            _notesWindow.HideRequested += () => SetNotesVisible(false);
            _notesWindow.LocationChanged += (_, _) => SaveNotesWindowBoundsLater();
            _notesWindow.SizeChanged += (_, _) => SaveNotesWindowBoundsLater();
        }
        if (!ReferenceEquals(NotesHost.Parent, _notesWindow))
        {
            ReleaseNotesHost();
            _notesWindow.Content = NotesHost;
            _notesWindow.PreviewKeyDown += OnNotesWindowKeyDown;
        }
        NotesHost.Detached = true;
        NotesHost.Visibility = Visibility.Visible;
        _notesWindow.Show();
    }

    readonly DispatcherTimer _notesBoundsTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    void SaveNotesWindowBoundsLater()
    {
        if (_notesWindow == null || !_notesWindow.IsVisible) return;
        _notesBoundsTimer.Stop();
        _notesBoundsTimer.Tick -= OnNotesBoundsTimer;
        _notesBoundsTimer.Tick += OnNotesBoundsTimer;
        _notesBoundsTimer.Start();
    }

    void OnNotesBoundsTimer(object? s, EventArgs e)
    {
        _notesBoundsTimer.Stop();
        if (_notesWindow == null) return;
        _ctl.Settings.NotesWindowBounds = _notesWindow.Bounds;
        _ctl.SaveSettings();
    }

    /// <summary>노트를 따로 창으로 띄우거나 다시 오른쪽에 붙입니다 (보이던 상태는 유지).</summary>
    void ToggleNotesDetached()
    {
        SetNotesVisible(false);
        bool detach = !_ctl.Settings.NotesDetached;
        _ctl.Settings.NotesDetached = detach;
        _ctl.SaveSettings();
        if (!detach)
        {
            if (_notesWindow != null) _ctl.Settings.NotesWindowBounds = _notesWindow.Bounds;
            EnsureDocked();
            NotesHost.Detached = false;
            NotesHost.Visibility = Visibility.Collapsed;
        }
        SetNotesVisible(true);
    }

    /// <summary>노트 패널을 붙인 노트 창(NotesDockWindow)으로 옮깁니다.</summary>
    void EnsureDocked()
    {
        if (_dockWindow == null)
        {
            _dockWindow = new NotesDockWindow(this);
            _dockWindow.PreviewKeyDown += OnNotesWindowKeyDown;
            _dockWindow.PanelWidthCommitted += () =>
            {
                _ctl.Settings.NotesWidth = _dockWindow.PanelWidth;
                _ctl.SaveSettings();
            };
        }
        if (!ReferenceEquals(_dockWindow.Panel, NotesHost))
        {
            ReleaseNotesHost();
            _dockWindow.Panel = NotesHost;
        }
    }

    /// <summary>노트 패널을 지금 들어 있는 곳(창 안 자리 / 붙인 노트 창 / 따로 띄운 창)에서 뺍니다.</summary>
    void ReleaseNotesHost()
    {
        switch (NotesHost.Parent)
        {
            case Panel p: p.Children.Remove(NotesHost); break;
            case ContentControl c: c.Content = null; break;
        }
    }

    // 노트 창(붙인 창/따로 띄운 창)은 RocketRPG 창과 다른 창이라 단축키를 따로 받습니다. 노트 관련 단축키만 처리합니다.
    void OnNotesWindowKeyDown(object sender, KeyEventArgs e)
    {
        string? actionId = HotkeyManager.MatchAction(e);
        if (actionId is not ("ToggleNotes" or "ScreenshotToNote" or "Screenshot")) return;
        e.Handled = true;
        if (!e.IsRepeat) ExecuteHotkeyAction(actionId);
    }
}
