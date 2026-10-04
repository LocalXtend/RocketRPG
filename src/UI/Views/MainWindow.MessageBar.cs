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
    void OnToggleAutoMessage(object sender, RoutedEventArgs e)
    {
        if (SendGuestTool("ToggleAutoMessage")) return;
        _autoAdvance.ToggleAuto();
        SavePerGame();
    }

    void OnToggleSkipMessage(object sender, RoutedEventArgs e)
    {
        if (SendGuestTool("ToggleSkipMessage")) return;
        _autoAdvance.ToggleSkip();
    }

    void OnToggleMessageBar(object sender, RoutedEventArgs e)
    {
        if (SendGuestTool("ToggleMessageBar")) return;
        bool nextShow = ShowMessageBarMenuItem.IsChecked;
        SetMessageBarVisibility(nextShow);
        _ctl.Settings.ShowMessageBar = nextShow;
        _ctl.SaveSettings();
        SavePerGame();
    }

    // 메시지 바 = Ren'Py 퀵 메뉴. 사용자 설정(_messageBarEnabled)이 켜져 있고,
    // 대화 감지를 지원하는 엔진에서 대화창이 표시 중일 때만 나타납니다(수동 표시 불필요).
    bool _messageBarEnabled = true;
    bool _dialogueVisible;
    readonly DispatcherTimer _msgHideTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public void SetMessageBarVisibility(bool enabled)
    {
        _messageBarEnabled = enabled;
        ShowMessageBarMenuItem.IsChecked = enabled;
        UpdateMessageBarVisibility();
        if (_isNativeRunning) LayoutGameScreen();
    }

    /// <summary>메시지 바 높이 (오버레이의 RenpyMsgBar와 같은 값)</summary>
    public const double MessageBarHeight = 30;

    /// <summary>게임 화면 아래에 비워 둘 메시지 바 자리. 대화 감지가 되는 엔진에서 메시지 바가 켜져 있을 때만.</summary>
    double MessageBarReserve() =>
        _messageBarEnabled && _isNativeRunning && _currentBridge?.SupportsMessageDetection == true ? MessageBarHeight : 0;

    void UpdateMessageBarVisibility()
    {
        bool show = IsMultiGuest ? _guestTools?["bar"]?.GetValue<bool>() == true && !_multi!.Room.HostAway && !_multi.Reconnecting
            : _messageBarEnabled && _dialogueVisible && _currentBridge?.SupportsMessageDetection == true;
        if (show == _msgBarShown) return;
        _msgBarShown = show;
        UiLog.Write($"msgbar: {(show ? "show" : "hide")} (enabled={_messageBarEnabled}, dialogue={_dialogueVisible}, detect={_currentBridge?.SupportsMessageDetection})");
        AnimateMessageBar(show);
        SyncMultiTools();   // 방장: 참가자 메시지 바도 바로 따라 오르내리게
    }

    bool _msgBarShown;

    /// <summary>
    /// 게임 위 HUD(OverlayWindow)를 움직여 그려도 되는지. EasyRPG/mkxp-z 게임 창이 들어 있으면 그 위의 창을 한 번 그릴 때마다
    /// 40ms 넘게 걸려, 대사마다 나오는 메시지 바의 흐려짐 효과만으로도 RocketRPG가 잠깐씩 멈췄습니다. 그때는 바로 보이고 사라지게 합니다.
    /// </summary>
    bool OverlayCanAnimate => EmbeddedGameHwnd() == IntPtr.Zero;

    // 메시지 바는 살짝 떠오르며 나타나고, 가라앉으며 사라집니다 (160ms).
    void AnimateMessageBar(bool show)
    {
        var bar = RenpyMsgBar;
        if (bar.RenderTransform is not TranslateTransform tt) bar.RenderTransform = tt = new TranslateTransform();
        if (!OverlayCanAnimate)
        {
            bar.BeginAnimation(UIElement.OpacityProperty, null);
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            bar.Opacity = 1;
            tt.Y = 0;
            bar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var dur = TimeSpan.FromMilliseconds(160);
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        bool fromHidden = show && bar.Visibility != Visibility.Visible;
        if (show) bar.Visibility = Visibility.Visible;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(show ? 1 : 0, dur) { EasingFunction = ease };
        var slide = new System.Windows.Media.Animation.DoubleAnimation(show ? 0 : 8, dur) { EasingFunction = ease };
        if (fromHidden) { fade.From = 0; slide.From = 8; }
        if (!show) fade.Completed += (_, _) => { if (!_msgBarShown) bar.Visibility = Visibility.Collapsed; };
        bar.BeginAnimation(UIElement.OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    void OnMessageStateChanged(IGameBridge source, MessageState state)
    {
        if (!ReferenceEquals(source, _currentBridge))
        {
            UiLog.Write($"msgbar: ignored message state from inactive bridge {source.GetType().Name} (current={_currentBridge?.GetType().Name ?? "null"})");
            return;
        }
        if (state.Busy)
        {
            if (!string.IsNullOrWhiteSpace(state.Text))
            {
                string line = string.IsNullOrWhiteSpace(state.Speaker) ? state.Text : $"{state.Speaker}: {state.Text}";
                string clean = line.Replace("\r", "").Replace('\n', ' ');
                DialogueLogManager.Add(clean);
                MultiShareDialogue(clean);   // 멀티 참가자의 대사 기록으로
            }
            _msgHideTimer.Stop();
            if (!_dialogueVisible) { _dialogueVisible = true; UpdateMessageBarVisibility(); }
        }
        else if (_dialogueVisible && !_msgHideTimer.IsEnabled)
        {
            // 대사 사이의 짧은 공백에 깜박이지 않도록 잠시 뒤에 숨깁니다.
            _msgHideTimer.Start();
        }
    }

    bool _shownAuto, _shownSkip;

    // 자동/스킵 상태는 메뉴·메시지 바·단축키 어디서 바꾸든 여기 한 곳에서 메뉴 체크와 알림을 맞춥니다.
    private void UpdateMessageMenuItems()
    {
        Dispatcher.Invoke(() =>
        {
            AutoMessageMenuItem.IsChecked = _autoAdvance.IsAuto;
            SkipMessageMenuItem.IsChecked = _autoAdvance.IsSkip;
            if (_isNativeRunning || _currentBridge != null)
            {
                if (_shownAuto != _autoAdvance.IsAuto) ShowHudMessage($"메시지 자동 넘김: {(_autoAdvance.IsAuto ? "켜짐" : "꺼짐")}");
                if (_shownSkip != _autoAdvance.IsSkip) ShowHudMessage($"메시지 고속 스킵: {(_autoAdvance.IsSkip ? "켜짐" : "꺼짐")}");
            }
            bool autoChanged = _shownAuto != _autoAdvance.IsAuto || Math.Abs(_shownAutoSpeed - _autoAdvance.Speed) > 0.01;
            _shownAuto = _autoAdvance.IsAuto;
            _shownSkip = _autoAdvance.IsSkip;
            _shownAutoSpeed = _autoAdvance.Speed;
            CheckAutoSpeedItems();
            // 메시지 바·메뉴·단축키 어디서 바꿔도 게임별로 저장해 다음 실행과 같은 상태가 되게 합니다.
            if (autoChanged && !string.IsNullOrEmpty(_currentDir) && _isNativeRunning) SavePerGame();
        });
    }

    double _shownAutoSpeed = 1.0;
}
