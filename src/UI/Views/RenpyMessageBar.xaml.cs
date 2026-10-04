#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>
/// Ren'Py 퀵 메뉴 형태의 하단 메시지 제어 줄. 대화창이 표시되는 동안에만 보입니다(표시 여부는 MainWindow가 결정).
/// </summary>
public partial class RenpyMessageBar : UserControl
{
    private RocketTextAutoAdvance? _autoAdvance;
    public event Action? QuickSaveRequested;
    public event Action? QuickLoadRequested;
    /// <summary>버튼을 누른 뒤 — 게임에 키보드 포커스를 돌려주기 위해 (안 그러면 게임이 입력을 받지 않아 멈춘 것처럼 보임)</summary>
    public event Action? Interacted;
    /// <summary>자동 넘김 속도 변경 (속도, 자동 넘김 켜짐 여부) — 알림 표시용</summary>
    public event Action<double, bool>? SpeedChanged;
    /// <summary>멀티 참가자: 단추를 누르면 방장에게 요청 (단축키 id, 원하는 값: 켜기 1/끄기 0, 속도)</summary>
    public event Action<string, double>? RemoteAction;

    // 멀티 참가자: 내 자동 넘김 대신 방장 상태를 보여 주고, 누르면 방장 게임에서 실행됩니다.
    bool _remote, _remoteAuto, _remoteSkip;
    double _remoteSpeed = 1.0;

    private static readonly double[] SpeedSteps = [1.0, 1.5, 2.0, 3.0, 0.5];

    // (평소, 마우스 올림, 누름) — 켜진 버튼은 켜짐 색을 기준으로 진해집니다.
    private static readonly Brush[] NormalColors = Palette(0xE6FFFFFF, 0xFFFFFFFF, 0xFFC8C8C8);
    private static readonly Brush[] AutoColors = Palette(0xFF66CCFF, 0xFF1FA8F2, 0xFF0A86D0);
    private static readonly Brush[] SkipColors = Palette(0xFFFFA040, 0xFFFF8410, 0xFFE06A00);

    public RenpyMessageBar()
    {
        InitializeComponent();
        foreach (var b in new[] { BtnLog, BtnSkip, BtnAuto, BtnSpeed, BtnQSave, BtnQLoad })
        {
            b.MouseEnter += (_, _) => Refresh();
            b.MouseLeave += (_, _) => Refresh();
            b.PreviewMouseLeftButtonDown += (_, _) => Dispatcher.BeginInvoke(new Action(Refresh));
            b.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(new Action(Refresh));
        }
        Refresh();
    }

    /// <summary>퀵 세이브/로드 단추 보이기 (스트리머 모드에서는 숨김)</summary>
    public void SetQuickSaveVisible(bool visible)
    {
        BtnQSave.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BtnQLoad.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetLogOnly(bool onlyLog)
    {
        foreach (var b in new[] { BtnSkip, BtnAuto, BtnSpeed }) b.Visibility = onlyLog ? Visibility.Collapsed : Visibility.Visible;
        SetQuickSaveVisible(!onlyLog && !HotkeyManager.StreamerMode);
    }

    /// <summary>멀티 참가자: 방장 상태로 보여 주기 (remote=false면 내 자동 넘김으로 돌아감)</summary>
    public void SetRemote(bool remote, bool auto = false, bool skip = false, double speed = 1.0)
    {
        if (_remote == remote && _remoteAuto == auto && _remoteSkip == skip && Math.Abs(_remoteSpeed - speed) < 0.01) return;
        _remote = remote;
        _remoteAuto = auto;
        _remoteSkip = skip;
        _remoteSpeed = speed;
        Refresh();
    }

    public void Bind(RocketTextAutoAdvance autoAdvance)
    {
        _autoAdvance = autoAdvance;
        _autoAdvance.StateChanged += () => Dispatcher.BeginInvoke(new Action(Refresh));
        Refresh();
    }

    private void Refresh()
    {
        bool auto = _remote ? _remoteAuto : _autoAdvance?.IsAutoEnabled == true;
        bool skip = _remote ? _remoteSkip : _autoAdvance?.IsSkipEnabled == true;
        Paint(BtnLog, NormalColors);
        Paint(BtnSkip, skip ? SkipColors : NormalColors);
        Paint(BtnAuto, auto ? AutoColors : NormalColors);
        Paint(BtnSpeed, auto ? AutoColors : NormalColors);   // 속도는 자동 넘김 속도라 자동이 켜졌을 때 같은 색
        Paint(BtnQSave, NormalColors);
        Paint(BtnQLoad, NormalColors);
        // 켜진 버튼은 색 글자에 더해 은은한 배경 알약을 깔아, 켜져 있는지 한눈에 보이게 합니다.
        BtnAuto.Background = auto ? AutoPill : NoPill;
        BtnSkip.Background = skip ? SkipPill : NoPill;
        BtnAuto.ToolTip = auto ? "메시지 자동 넘김: 켜짐 (누르면 끔)" : "메시지 자동 넘김: 꺼짐 (누르면 켬)";
        BtnSkip.ToolTip = skip ? "메시지 고속 스킵: 켜짐 (누르면 끔)" : "메시지 고속 스킵: 꺼짐 (누르면 켬)";
        BtnSpeed.Content = $"속도 {(_remote ? _remoteSpeed : _autoAdvance?.Speed ?? 1.0):0.0}x";
    }

    private static readonly Brush NoPill = Solid(0x01000000);
    private static readonly Brush AutoPill = Solid(0x4466CCFF);
    private static readonly Brush SkipPill = Solid(0x44FFA040);

    private static void Paint(Button b, Brush[] colors) =>
        b.Foreground = b.IsPressed || (b.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed) ? colors[2]
                     : b.IsMouseOver ? colors[1] : colors[0];

    private void BtnAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_remote) { RemoteAction?.Invoke("ToggleAutoMessage", _remoteAuto ? 0 : 1); Interacted?.Invoke(); return; }
        _autoAdvance?.ToggleAuto(); Refresh(); Interacted?.Invoke();
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_remote) { RemoteAction?.Invoke("ToggleSkipMessage", _remoteSkip ? 0 : 1); Interacted?.Invoke(); return; }
        _autoAdvance?.ToggleSkip(); Refresh(); Interacted?.Invoke();
    }

    private void BtnSpeed_Click(object sender, RoutedEventArgs e)
    {
        if (_remote)
        {
            int at = Array.FindIndex(SpeedSteps, s => Math.Abs(s - _remoteSpeed) < 0.05);
            RemoteAction?.Invoke("AutoSpeed", SpeedSteps[(at + 1) % SpeedSteps.Length]);
            Interacted?.Invoke();
            return;
        }
        if (_autoAdvance == null) return;
        double cur = _autoAdvance.Speed;
        int idx = Array.FindIndex(SpeedSteps, s => Math.Abs(s - cur) < 0.05);
        _autoAdvance.Speed = SpeedSteps[(idx + 1) % SpeedSteps.Length];
        Refresh();
        SpeedChanged?.Invoke(_autoAdvance.Speed, _autoAdvance.IsAutoEnabled);
        Interacted?.Invoke();
    }

    private void BtnQSave_Click(object sender, RoutedEventArgs e)
    {
        if (_remote) RemoteAction?.Invoke("QuickSave", 1); else QuickSaveRequested?.Invoke();
        Interacted?.Invoke();
    }

    private void BtnQLoad_Click(object sender, RoutedEventArgs e)
    {
        if (_remote) RemoteAction?.Invoke("QuickLoad", 1); else QuickLoadRequested?.Invoke();
        Interacted?.Invoke();
    }

    private void BtnLog_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this)?.Owner ?? Window.GetWindow(this);
        var logWin = new DialogueLogWindow { Owner = owner };
        logWin.ShowDialog();
        Interacted?.Invoke();
    }

    private static Brush[] Palette(uint normal, uint hover, uint pressed) => [Solid(normal), Solid(hover), Solid(pressed)];

    private static Brush Solid(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }
}
