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
    // ── 도구 메뉴: 퀵 세이브/로드, 자동 넘김 속도, 게임 설정 리셋 ──

    void OnQuickSaveMenu(object s, RoutedEventArgs e) => ExecuteHotkeyAction("QuickSave");
    void OnQuickLoadMenu(object s, RoutedEventArgs e) => ExecuteHotkeyAction("QuickLoad");

    void CheckAutoSpeedItems()
    {
        foreach (var mi in AutoSpeedMenu.Items.OfType<MenuItem>())
            if (mi.Tag is string t && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
                mi.IsChecked = Math.Abs(v - _autoAdvance.Speed) < 0.05;
    }

    void OnAutoSpeedMenu(object s, RoutedEventArgs e)
    {
        if (s is not MenuItem mi || mi.Tag is not string t ||
            !double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) return;
        if (SendGuestTool("AutoSpeed", v)) return;
        _autoAdvance.Speed = v;
        CheckAutoSpeedItems();
        ShowHudMessage($"자동 넘김 속도: {v:0.0}x{(_autoAdvance.IsAuto ? "" : " (자동 넘김을 켜면 적용)")}");
    }

    void OnResetGameSettings(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentDir)) return;
        var r = MessageBox.Show(this,
            $"'{CurrentGameTitle()}'의 RocketRPG 설정을 처음 상태로 되돌립니다.\n" +
            "(비율, 밝기, 필터, 볼륨, 인게임 글꼴, 프레임, 자동 넘김, 변수 HUD 위치)\n\n" +
            "설정을 초기화하고 게임을 다시 시작합니다. 저장하지 않은 게임 진행은 사라집니다.\n계속할까요?",
            "게임 설정 리셋", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        GameSettingsService.Reset(_currentDir);
        UiLog.Write($"settings: reset per-game settings for {_currentDir}");
        RelaunchCurrentGame();
        ShowHudMessage("게임 설정을 초기화했습니다.");
    }

    private void UpdateMenuEnabledState(bool isGameRunning)
    {
        ScreenMenu.IsEnabled = isGameRunning;
        AudioMenu.IsEnabled = isGameRunning;
        // 도구 메뉴는 늘 열 수 있고, 게임이 필요한 항목만 끕니다 (노트·스크린샷 보관함은 게임 없이도 됨).
        // 스트리머 모드에서는 노트만 남깁니다.
        bool streamer = _ctl.Settings.StreamerMode;
        foreach (var mi in ToolMenu.Items.OfType<MenuItem>())
            mi.IsEnabled = ReferenceEquals(mi, NotesMenuItem) ||
                           (!streamer && (isGameRunning || ReferenceEquals(mi, GalleryMenuItem)));
        EspOverlayMenuItem.IsEnabled = !streamer;
        TileInspectorMenuItem.IsEnabled = !streamer;
        RestartMenuItem.IsEnabled = isGameRunning;
        GameExitMenuItem.IsEnabled = isGameRunning;

        _msgHideTimer.Stop();
        _dialogueVisible = false;
        UpdateMessageBarVisibility();
        if (!isGameRunning)
        {
            PinnedVarsOverlay.Visibility = Visibility.Collapsed;
            TileInspectorHud.Visibility = Visibility.Collapsed;
            EspCanvas.Clear();
        }
    }

    void OnToggleNoclip(object sender, RoutedEventArgs e) => ToggleNoclip();

    void ToggleNoclip()
    {
        if (SendGuestTool("ToggleNoclip")) return;
        if (_currentBridge != null)
        {
            _currentBridge.ToggleNoclip();
            NoclipMenuItem.IsChecked = _currentBridge.IsNoclip;
            ShowHudMessage($"벽 통과: {(_currentBridge.IsNoclip ? "켜짐" : "꺼짐")}");
        }
        else
        {
            NoclipMenuItem.IsChecked = false;
            ShowHudMessage("게임이 실행 중이 아닙니다.");
        }
    }

    void OnToggleEspOverlay(object sender, RoutedEventArgs e)
    {
        if (SendGuestTool("ToggleEspOverlay")) return;
        bool enabled = EspOverlayMenuItem.IsChecked;
        _currentBridge?.EnableEsp(enabled);
        if (!enabled)
        {
            _cachedEspItems = new();
            EspCanvas.Clear();
        }
        ShowHudMessage($"ESP 오버레이: {(enabled ? "켜짐" : "꺼짐")}");
    }

    void OnToggleTileInspector(object sender, RoutedEventArgs e)
    {
        if (SendGuestTool("ToggleTileInspector")) return;
        bool enabled = TileInspectorMenuItem.IsChecked;
        _currentBridge?.EnableTileInspector(enabled);
        TileInspectorHud.Visibility = Visibility.Collapsed;
        ShowHudMessage($"타일 인스펙터: {(enabled ? "켜짐" : "꺼짐")}");
    }

    private List<EspItem> _cachedEspItems = new();

    void OnEspDataUpdated(List<EspItem> items)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _cachedEspItems = items ?? new();
            RenderEspElements();
            ShareEspItems(_cachedEspItems);
        });
    }

    // 데이터 수신 또는 레이아웃 변경 시에만 호출됩니다 (매 프레임 호출 금지).
    void RenderEspElements()
    {
        if (!EspOverlayMenuItem.IsChecked || _cachedEspItems.Count == 0)
        {
            EspCanvas.Clear();
            return;
        }

        GetGameViewport(out double vpLeft, out double vpTop, out double vpWidth, out double vpHeight, out int baseW, out int baseH);
        if (baseW <= 0 || baseH <= 0 || vpWidth <= 0 || vpHeight <= 0) return;

        EspCanvas.SetViewport(new Rect(vpLeft, vpTop, vpWidth, vpHeight), baseW, baseH);
        EspCanvas.Update(_cachedEspItems);
    }

    void OnTileInfoUpdated(TileInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            if (!TileInspectorMenuItem.IsChecked)
            {
                TileInspectorHud.Visibility = Visibility.Collapsed;
                return;
            }

            TileInspectorText.Text = $"[타일 ({info.MapX}, {info.MapY}) | 통과: {(info.Passable ? "O" : "X")} | 이벤트: {info.Events}]";
            Point mousePos = CursorInRenderScreen();
            double left = Math.Max(8, Math.Min(RenderScreen.ActualWidth - 320, mousePos.X + 16));
            double top = Math.Max(8, Math.Min(RenderScreen.ActualHeight - 45, mousePos.Y + 16));
            TileInspectorHud.Margin = new Thickness(left, top, 0, 0);
            TileInspectorHud.HorizontalAlignment = HorizontalAlignment.Left;
            TileInspectorHud.VerticalAlignment = VerticalAlignment.Top;
            TileInspectorHud.Visibility = Visibility.Visible;
        });
    }

    void OnGameStateUpdated(GameState state)
    {
        Dispatcher.Invoke(() =>
        {
            MultiOnGameState();
            NoclipMenuItem.IsChecked = state.Noclip;
            _mapViewerWnd?.UpdatePlayerPosition(state.MapId, state.PlayerX, state.PlayerY);
        });
    }
}
