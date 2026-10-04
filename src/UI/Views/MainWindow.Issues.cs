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
    // 게임 실행 중 스크립트 오류: 쉬운 설명과 함께 오류 내용을 보여 줍니다.
    void OnMkxpStartupScriptError(string message)
    {
        if (!_isNativeRunning || !ReferenceEquals(_currentBridge, _mkxpRenderer) || string.IsNullOrEmpty(_currentDir)) return;
        UiLog.Write($"launch: mkxp-z script error: {message.Replace('\n', ' ')}");
        ShowGameError(message);
    }

    // MV/MZ가 WebView2에서 자체 오류 화면을 띄움 (게임 화면에 오류 내용이 그대로 보입니다)
    void OnWebGameFatalError(string message)
    {
        if (!_isNativeRunning || !ReferenceEquals(_currentBridge, _webRenderer) || string.IsNullOrEmpty(_currentDir)) return;
        UiLog.Write($"launch: MV/MZ error in WebView2: {message}");
        ShowGameError(message);
    }

    bool _webCrashOpen;

    /// <summary>
    /// MV/MZ 게임 화면이 비정상 종료됨(gone) 또는 그래픽을 잃음. 그냥 꺼 버리지 않고 이유와 기록 위치를 보여 주고,
    /// 사용자가 고르면 다시 시작합니다 (저절로 계속 다시 시작하지는 않음).
    /// </summary>
    void OnWebGameCrashed(string text, bool gone)
    {
        if (!ReferenceEquals(_currentBridge, _webRenderer) || string.IsNullOrEmpty(_currentDir) || _webCrashOpen) return;
        string dir = _currentDir;
        UiLog.Write($"session: MV/MZ {(gone ? "page gone" : "graphics lost")}: {text}");
        if (gone) StopNativeSession(text);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RR_AUTOTEST"))) return;
        _webCrashOpen = true;
        try
        {
            string body = $"{text}\n\n그림·대사·이벤트를 한꺼번에 많이 불러오는 장면에서 생길 수 있습니다.\n" +
                          (gone ? "게임을 다시 시작할까요? (저장하지 않은 진행은 사라집니다)" : "화면이 깨져 보이면 다시 시작할 수 있습니다. 지금 다시 시작할까요? (저장하지 않은 진행은 사라집니다)") +
                          $"\n\n문제를 알려 주실 때는 이 기록 파일을 함께 보내 주세요:\n{UiLog.LogPath}";
            var r = MessageBox.Show(this, body, "RocketRPG", MessageBoxButton.YesNo, MessageBoxImage.Warning, gone ? MessageBoxResult.Yes : MessageBoxResult.No);
            if (r == MessageBoxResult.Yes) Launch(dir);
        }
        finally { _webCrashOpen = false; }
    }

    bool _gameErrorOpen;

    void ShowGameError(string message)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RR_AUTOTEST"))) return;   // 자동 테스트는 기록만
        if (_gameErrorOpen) return;
        _gameErrorOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                string brief = message.Replace("\r", "").Trim();
                if (brief.Length > 300) brief = brief[..300] + "…";
                // 원인을 짐작할 수 있으면 쉬운 설명을 먼저 보여 줍니다 (예: 빠진 게임 파일, 필요한 글꼴/RTP)
                string? hint = GameIssueAdvisor.Explain(message);
                string head = hint != null ? $"{hint}\n\n(오류 내용: {brief})" : brief;
                MessageBox.Show(this, $"게임 실행 중 오류가 발생했습니다.\n\n{head}", "RocketRPG", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _gameErrorOpen = false; }
        }, DispatcherPriority.Background);
    }

    // ── 알림 막대: 게임에 필요한데 빠진 것 (RTP, 글꼴) ──

    List<string> _dismissedNotices = new();
    List<GameIssue> _issues = new();

    async Task CheckGameIssuesAsync(string dir, int engine)
    {
        var issues = await Task.Run(() => GameIssueAdvisor.Check(dir, engine));
        if (!string.Equals(_currentDir, dir, StringComparison.OrdinalIgnoreCase)) return;   // 그 사이 다른 게임으로 바뀜
        // 사용자가 인게임 글꼴을 직접 골랐으면 게임 글꼴 대신 그 글꼴을 쓰므로 "글꼴 없음" 안내는 필요 없습니다.
        bool userFont = !string.IsNullOrWhiteSpace(_ctl.Settings.InGameFontFamily);
        _issues = issues.Where(i => !_dismissedNotices.Contains(i.Key) && !(userFont && i.Key.StartsWith("font:", StringComparison.Ordinal))).ToList();
        foreach (var i in issues) UiLog.Write($"notice: {i.Key} {(_dismissedNotices.Contains(i.Key) ? "(dismissed)" : "")}");
        ShowNextIssue();
    }

    void ShowNextIssue()
    {
        if (_issues.Count == 0) { IssueBar.Visibility = Visibility.Collapsed; LayoutGameScreen(); return; }
        var i = _issues[0];
        IssueText.Text = _issues.Count > 1 ? $"{i.Message}  (알림 {_issues.Count}개)" : i.Message;
        IssueActionButton.Content = i.ActionText;
        IssueActionButton.Visibility = i.ActionUrl != null ? Visibility.Visible : Visibility.Collapsed;
        IssueBar.Visibility = Visibility.Visible;
        LayoutGameScreen();
    }

    void OnIssueAction(object s, RoutedEventArgs e)
    {
        if (_issues.Count == 0 || _issues[0].ActionUrl is not { } url) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { UiLog.Write($"notice: open {url} failed {ex.Message}"); }
    }

    void OnIssueDismiss(object s, RoutedEventArgs e)
    {
        if (_issues.Count == 0) return;
        _dismissedNotices.Add(_issues[0].Key);
        SavePerGame();
        OnIssueClose(s, e);
    }

    void OnIssueClose(object s, RoutedEventArgs e)
    {
        if (_issues.Count > 0) _issues.RemoveAt(0);
        ShowNextIssue();
    }
}
