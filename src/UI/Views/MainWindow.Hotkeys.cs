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
    // ── 핫키 및 네이티브 제어 파이프라인 ──

    void OnWebHotkeyReceived(KeyMessage km)
    {
        Dispatcher.Invoke(() =>
        {
            string? actionId = HotkeyManager.MatchWebKey(km.Key, km.Ctrl, km.Alt, km.Shift);
            if (actionId != null)
            {
                ExecuteHotkeyAction(actionId);
            }
        });
    }

    // 게임이 없어도 되는 단축키 (나머지는 게임에 영향을 주므로 게임이 꺼져 있으면 막습니다)
    static readonly HashSet<string> NoGameHotkeys = ["ToggleNotes", "MultiChat", "ToggleMenuBar"];

    bool ExecuteHotkeyAction(string actionId)
    {
        if (!HotkeyManager.IsAllowed(actionId)) return false;   // 스트리머 모드
        if (IsMultiGuest)
        {
            // 참가자: 메시지 바 동작은 방장이 스트리머 모드만 아니면, 도구는 도구 권한이 있을 때만 방장에게 요청합니다.
            // 그 밖의 경우 false → 키가 조종 입력으로 방장 게임에 갑니다 (F키 등).
            if (MultiPolicy.SharedActions.Contains(actionId) || MultiPolicy.BarActions.Contains(actionId)) return GuestToolsAllowed && SendGuestTool(actionId);
            if (actionId == "ToggleDataInspector") { if (!GuestToolsAllowed) return false; OnDataInspector(this, new RoutedEventArgs()); return true; }
            if (!HotkeyManager.IsAlwaysAvailable(actionId) && actionId is not ("Screenshot" or "ScreenshotToNote")) return false;
        }
        bool guestShot = IsMultiGuest && actionId is "Screenshot" or "ScreenshotToNote";   // 참가자는 방장 화면을 찍음
        if (_currentBridge == null && !NoGameHotkeys.Contains(actionId) && !guestShot)
        {
            ShowHudMessage("게임이 실행 중이 아닙니다.");
            return true;
        }
        switch (actionId)
        {
            case "MultiChat":
                OpenMultiChat();
                return true;

            case "MultiSummon":
                SummonGuests();
                return true;

            case "ToggleMenuBar":
                ToggleMenuBar();
                return true;

            case "QuickSave":
                if (_currentBridge != null) _currentBridge.QuickSave();
                else ShowHudMessage("게임이 실행 중이 아닙니다.");
                return true;

            case "QuickLoad":
                if (_currentBridge != null) _currentBridge.QuickLoad();
                else ShowHudMessage("게임이 실행 중이 아닙니다.");
                return true;

            case "ForceSaveMenu":
                if (_currentBridge != null) _currentBridge.ForceSaveMenu();
                else ShowHudMessage("게임이 실행 중이 아닙니다.");
                return true;

            case "ForceLoadMenu":
                if (_currentBridge != null) _currentBridge.ForceLoadMenu();
                else ShowHudMessage("게임이 실행 중이 아닙니다.");
                return true;

            case "ToggleMapViewer":
                if (_mapViewerWnd != null && _mapViewerWnd.IsLoaded)
                {
                    _mapViewerWnd.Close();
                }
                else
                {
                    OnMapViewer(this, new RoutedEventArgs());
                }
                return true;

            case "ToggleDataInspector":
                if (_dataInspectorWnd != null && _dataInspectorWnd.IsLoaded)
                {
                    _dataInspectorWnd.Close();
                }
                else
                {
                    OnDataInspector(this, new RoutedEventArgs());
                }
                return true;

            case "ToggleEspOverlay":
                EspOverlayMenuItem.IsChecked = !EspOverlayMenuItem.IsChecked;
                OnToggleEspOverlay(EspOverlayMenuItem, new RoutedEventArgs());
                return true;

            case "ToggleTileInspector":
                TileInspectorMenuItem.IsChecked = !TileInspectorMenuItem.IsChecked;
                OnToggleTileInspector(TileInspectorMenuItem, new RoutedEventArgs());
                return true;

            case "ToggleNoclip":
                ToggleNoclip();
                return true;

            case "ToggleAutoMessage":
                _autoAdvance.ToggleAuto();
                return true;

            case "ToggleSkipMessage":
                _autoAdvance.ToggleSkip();
                return true;

            case "ToggleNotes":
                SetNotesVisible(!NotesVisible);
                return true;

            case "ScreenshotToNote":
                _ = TakeScreenshot(toNote: true);
                return true;

            case "Screenshot":
                _ = TakeScreenshot(toNote: false);
                return true;

            case "ToggleMessageBar":
                bool nextShow = !_messageBarEnabled;
                SetMessageBarVisibility(nextShow);
                _ctl.Settings.ShowMessageBar = nextShow;
                _ctl.SaveSettings();
                SavePerGame();
                return true;

            case "SpeedUp":
                CycleSpeed(1);
                return true;

            case "SpeedDown":
                CycleSpeed(-1);
                return true;

            case "SpeedReset":
                SetSpeed(1.0);
                return true;

            case "TogglePause":
                if (_currentBridge != null)
                {
                    _currentBridge.TogglePause();
                    ShowHudMessage($"일시정지: {(_currentBridge.IsPaused ? "켜짐" : "해제")}");
                }
                return true;

            case "RestartGame":
                OnRestart(this, new RoutedEventArgs());
                return true;

            case "ExitGame":
                OnGameExit(this, new RoutedEventArgs());
                return true;
        }
        return false;
    }

    static readonly double[] SpeedSteps = [0.5, 1.0, 2.0, 4.0, 8.0];

    void CycleSpeed(int delta)
    {
        double cur = GetSelectedSpeed();
        int idx = Array.IndexOf(SpeedSteps, cur);
        if (idx < 0) idx = 1;
        int nextIdx = Math.Clamp(idx + delta, 0, SpeedSteps.Length - 1);
        SetSpeed(SpeedSteps[nextIdx]);
    }

    double GetSelectedSpeed()
    {
        foreach (var item in SpeedMenu.Items.OfType<MenuItem>())
        {
            if (item.IsChecked && double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
                return s;
        }
        return 1.0;
    }

    void SetSpeed(double speed)
    {
        if (SendGuestTool("SetSpeed", speed)) return;
        foreach (var item in SpeedMenu.Items.OfType<MenuItem>())
        {
            if (item.Tag != null && double.TryParse(item.Tag.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
            {
                item.IsChecked = Math.Abs(s - speed) < 0.01;
            }
        }
        _currentBridge?.SetSpeed(speed);
        ShowHudMessage($"게임 배속: {speed:0.#}x");
    }

    void OnSpeedMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && double.TryParse(mi.Tag?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
        {
            SetSpeed(s);
        }
    }

    void OnHotkeySettings(object sender, RoutedEventArgs e)
    {
        var wnd = new HotkeySettingsWindow(dict =>
        {
            _ctl.Settings.Hotkeys = dict;
            _ctl.SaveSettings();
            RefreshMenuGestures();
        }) { Owner = this };
        wnd.ShowDialog();
    }

    /// <summary>메뉴 항목 → 그 단축키 동작. 메뉴 오른쪽 글자는 늘 지금 설정에서 만듭니다 (바꾸면 바로 반영).</summary>
    (MenuItem item, string action)[] MenuHotkeys() =>
    [
        (NotesMenuItem, "ToggleNotes"), (ScreenshotOnlyMenuItem, "Screenshot"), (ScreenshotMenuItem, "ScreenshotToNote"),
        (MapViewerMenuItem, "ToggleMapViewer"), (DataInspectorMenuItem, "ToggleDataInspector"),
        (QuickSaveMenuItem, "QuickSave"), (QuickLoadMenuItem, "QuickLoad"),
        (ForceSaveMenuItem, "ForceSaveMenu"), (ForceLoadMenuItem, "ForceLoadMenu"),
        (NoclipMenuItem, "ToggleNoclip"), (PauseMenuItem, "TogglePause"),
        (AutoMessageMenuItem, "ToggleAutoMessage"), (SkipMessageMenuItem, "ToggleSkipMessage"), (ShowMessageBarMenuItem, "ToggleMessageBar"),
        (EspOverlayMenuItem, "ToggleEspOverlay"), (TileInspectorMenuItem, "ToggleTileInspector"),
        (RestartMenuItem, "RestartGame"), (GameExitMenuItem, "ExitGame"),
        (MultiChatItem, "MultiChat"), (MultiSummonItem, "MultiSummon"), (MenuBarMenuItem, "ToggleMenuBar"),
        (SpeedUpMenuItem, "SpeedUp"), (SpeedDownMenuItem, "SpeedDown"), (SpeedResetMenuItem, "SpeedReset"),
    ];

    void RefreshMenuGestures()
    {
        foreach (var (item, action) in MenuHotkeys()) item.InputGestureText = HotkeyManager.MenuText(HotkeyManager.GestureOf(action));
    }

    void OnForceSaveMenu(object s, RoutedEventArgs e) => ExecuteHotkeyAction("ForceSaveMenu");
    void OnForceLoadMenu(object s, RoutedEventArgs e) => ExecuteHotkeyAction("ForceLoadMenu");
    void OnPauseMenu(object s, RoutedEventArgs e) => ExecuteHotkeyAction("TogglePause");
    void OnMenuBarMenu(object s, RoutedEventArgs e) => ToggleMenuBar();
    void OnSpeedStepMenu(object s, RoutedEventArgs e)
    {
        if (s is MenuItem { Tag: string action }) ExecuteHotkeyAction(action);
    }

    void OnMapViewer(object sender, RoutedEventArgs e)
    {
        // 맵 뷰어는 내 PC에서 켠 게임의 파일로만 엽니다 (멀티로 맵·에셋을 주고받지 않음)
        if (IsMultiGuest) { ShowHudMessage("맵 뷰어는 내 PC에서 실행한 게임에서만 열 수 있습니다."); return; }
        if (string.IsNullOrEmpty(_currentDir))
        {
            MessageBox.Show(this, "먼저 게임을 실행해 주세요.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_mapViewerWnd != null && _mapViewerWnd.IsLoaded)
        {
            _mapViewerWnd.Activate();
            return;
        }

        try
        {
            int mapId = _currentBridge?.LatestState.MapId ?? 1;
            int px = _currentBridge?.LatestState.PlayerX ?? 0;
            int py = _currentBridge?.LatestState.PlayerY ?? 0;

            _mapViewerWnd = new MapViewerWindow(_currentDir, mapId, px, py, _currentBridge, (mid, x, y) =>
            {
                _currentBridge?.Warp(mid, x, y);
            }) { Owner = this };
            _mapViewerWnd.Closed += (_, _) => _mapViewerWnd = null;
            _mapViewerWnd.Show();
        }
        catch (Exception ex)
        {
            UiLog.Write($"OnMapViewer crash prevented: {ex}");
            MessageBox.Show(this, $"맵 뷰어를 여는 중 오류가 발생했습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    AssetViewerWindow? _assetViewerWnd;

    /// <summary>도구 &gt; 에셋 보기: 내 PC에서 연 게임의 파일만 (멀티 참가자는 방장 파일을 볼 수 없음)</summary>
    void OnAssetViewer(object sender, RoutedEventArgs e)
    {
        if (IsMultiGuest || string.IsNullOrEmpty(_currentDir))
        {
            MessageBox.Show(this, "에셋 보기는 내 PC에서 실행한 게임에서만 열 수 있습니다.", "에셋 보기", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_assetViewerWnd != null) { _assetViewerWnd.Activate(); return; }
        _assetViewerWnd = new AssetViewerWindow(this, _currentDir, _ctl.Current.Engine, CurrentGameTitle());
        _assetViewerWnd.Closed += (_, _) => _assetViewerWnd = null;
        _assetViewerWnd.Show();
    }

    void OnDataInspector(object sender, RoutedEventArgs e)
    {
        if (_dataInspectorWnd != null && _dataInspectorWnd.IsLoaded)
        {
            _dataInspectorWnd.Activate();
            return;
        }

        _dataInspectorWnd = new DataInspectorWindow(IsMultiGuest ? GuestBridge : _currentBridge) { Owner = this };
        _dataInspectorWnd.PinnedVariablesChanged += OnPinnedVariablesChanged;
        _dataInspectorWnd.Closed += (_, _) => _dataInspectorWnd = null;
        _dataInspectorWnd.Show();
    }
}
