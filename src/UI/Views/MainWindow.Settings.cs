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
    // ── 스트리머 모드: 방송·화면 공유 중 치트/도구가 보이거나 실수로 눌리지 않게 ──

    void OnMemoSettings(object sender, RoutedEventArgs e)
    {
        if (new MemoSettingsWindow(this, _ctl.Settings).ShowDialog() == true)
        {
            _ctl.SaveSettings();
            ShowHudMessage("메모 설정을 바꿨습니다.");
        }
    }

    void OnToggleStreamer(object sender, RoutedEventArgs e) => SetStreamerMode(StreamerMenuItem.IsChecked);

    TutorialWindow? _tutorialWnd;

    void OnTutorial(object sender, RoutedEventArgs e) => OpenTutorial();

    void OpenTutorial(string? page = null)
    {
        if (_tutorialWnd != null) { _tutorialWnd.Activate(); return; }
        _tutorialWnd = new TutorialWindow(this, MainMenu, page);
        _tutorialWnd.Closed += (_, _) => _tutorialWnd = null;
        _tutorialWnd.Show();
    }

    /// <summary>처음 켰을 때 한 번만: 사용법을 볼지 묻습니다 (정보 &gt; 사용법에서 언제든 다시 열 수 있음)</summary>
    void OfferTutorialOnce()
    {
        if (_ctl.Settings.TutorialOffered) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RR_AUTOTEST")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RR_MULTI_TEST"))) return;
        _ctl.Settings.TutorialOffered = true;
        _ctl.SaveSettings();
        var r = MessageBox.Show(this, "RocketRPG를 처음 쓰시나요?\n게임 실행하는 법, 멀티, 채팅 같은 사용법을 그림으로 볼 수 있습니다.\n\n사용법을 볼까요? (나중에 '정보 > 사용법'에서도 볼 수 있습니다)",
            "RocketRPG", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) OpenTutorial();
    }

    void OnRtpFolders(object sender, RoutedEventArgs e)
    {
        var dlg = new RtpFolderWindow(this, _ctl.Settings.RtpPaths ?? new());
        if (dlg.ShowDialog() != true) return;
        _ctl.Settings.RtpPaths = dlg.Result;
        _ctl.SaveSettings();
        RtpResolver.UserPaths = new(dlg.Result, StringComparer.OrdinalIgnoreCase);
        UiLog.Write($"rtp: user folders {string.Join("; ", dlg.Result.Select(kv => kv.Key + "=" + kv.Value))}");
        ShowHudMessage(_currentBridge != null ? "RTP 폴더를 저장했습니다. 게임을 다시 시작하면 적용됩니다." : "RTP 폴더를 저장했습니다.");
    }

    void SetStreamerMode(bool on)
    {
        _ctl.Settings.StreamerMode = on;
        _ctl.SaveSettings();
        HotkeyManager.StreamerMode = on;
        StreamerMenuItem.IsChecked = on;
        RenpyMsgBar.SetQuickSaveVisible(!on);
        _discord.Streaming = on;
        if (on)
        {
            // 켜져 있던 도구를 모두 끕니다 (노트는 그대로)
            if (EspOverlayMenuItem.IsChecked) { EspOverlayMenuItem.IsChecked = false; OnToggleEspOverlay(EspOverlayMenuItem, new RoutedEventArgs()); }
            if (TileInspectorMenuItem.IsChecked) { TileInspectorMenuItem.IsChecked = false; OnToggleTileInspector(TileInspectorMenuItem, new RoutedEventArgs()); }
            if (_currentBridge != null)
            {
                if (_currentBridge.IsNoclip) ToggleNoclip();
                if (Math.Abs(GetSelectedSpeed() - 1.0) > 0.001) SetSpeed(1.0);
            }
            _mapViewerWnd?.Close();
            _dataInspectorWnd?.Close();
            PinnedVarsOverlay.Visibility = Visibility.Collapsed;
        }
        UpdateMenuEnabledState(_currentBridge != null);
        UpdateMultiUi();
        UiLog.Write($"streamer: {(on ? "on" : "off")}");
        ShowHudMessage(on ? "스트리머 모드: 켜짐 (노트 말고 도구·단축키 꺼짐)" : "스트리머 모드: 꺼짐");
    }

    // 설정 > 업데이트 받을 버전 (정식만 / 베타도)
    void UpdateChannelChecks()
    {
        bool beta = UpdateService.WantsBeta(_ctl.Settings.UpdateChannel);
        UpdateStableItem.IsChecked = !beta;
        UpdateBetaItem.IsChecked = beta;
    }

    void OnUpdateChannel(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string channel }) return;
        _ctl.Settings.UpdateChannel = channel;
        _ctl.SaveSettings();
        UpdateChannelChecks();
        ShowHudMessage(channel == "beta" ? "업데이트: 베타 버전도 받습니다." : "업데이트: 정식 버전만 받습니다.");
    }

    void OnToggleDiscord(object sender, RoutedEventArgs e)
    {
        bool on = DiscordMenuItem.IsChecked;
        _ctl.Settings.DiscordPresence = on;
        _ctl.SaveSettings();
        _discord.SetEnabled(on);
        if (on && _currentBridge != null) _discord.ShowGame(CurrentGameTitle(), EngineName(_ctl.Current.Engine));
        ShowHudMessage($"디스코드 표시: {(on ? "켜짐" : "꺼짐")}");
    }

    string CurrentGameTitle()
    {
        string t = _ctl.Current.TitleUtf8;
        return string.IsNullOrWhiteSpace(t) ? Path.GetFileName(_currentDir.TrimEnd('\\', '/')) : t.Trim();
    }
}
