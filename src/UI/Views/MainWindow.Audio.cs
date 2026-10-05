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
    // ── 소리 > 출력 장치: 게임 오디오를 선택한 장치로 라우팅 ──

    void OnOutputDeviceMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!AudioRouter.Supported) return;
        OutputDeviceMenu.Items.Clear();
        OutputDeviceMenu.Items.Add(MakeDeviceItem("기본값", "", _selectedDeviceId.Length == 0));
        try
        {
            foreach (var (id, name, isDefault) in AudioRouter.GetRenderDevices())
                OutputDeviceMenu.Items.Add(MakeDeviceItem(
                    isDefault ? $"{name} (시스템 기본)" : name, id, _selectedDeviceId == id));
        }
        catch (Exception ex)
        {
            UiLog.Write($"outdev: enumerate EX {ex.GetType().Name}: {ex.Message}");
        }
    }

    MenuItem MakeDeviceItem(string header, string tag, bool isChecked)
    {
        var mi = new MenuItem { Header = header, Tag = tag, IsCheckable = true, IsChecked = isChecked };
        mi.Click += OnOutputDeviceClick;
        return mi;
    }

    void OnOutputDeviceClick(object sender, RoutedEventArgs e)
    {
        if (!AudioRouter.Supported) return;
        _selectedDeviceId = (sender as MenuItem)?.Tag as string ?? "";
        RouteRunningGameNow("menu");
    }

    void RouteRunningGameNow(string src)
    {
        if (!AudioRouter.Supported || !_isNativeRunning) return;
        var pids = GameAudioPids();
        if (pids.Count == 0)
        {
            UiLog.Write($"audioroute: {src} no game audio sessions yet");
            return;
        }
        bool ok = AudioRouter.RoutePids(pids, _selectedDeviceId.Length == 0 ? null : _selectedDeviceId);
        TrackRouted(pids);
        UiLog.Write($"audioroute: {src} device={(_selectedDeviceId.Length == 0 ? "(default)" : _selectedDeviceId)} " +
                     $"pids=[{string.Join(",", pids)}] ok={ok}");
    }

    void TrackRouted(List<int> pids)
    {
        _routedGamePids.Clear();
        _routedGamePids.AddRange(pids);
    }

    // 소리를 내는 게임 프로세스: mkxp-z/EasyRPG는 그 프로세스, MV/MZ는 게임 폴더에서 소리를 내는 프로세스(없으면 빈 목록)
    List<int> GameAudioPids()
    {
        int pid = NativeGamePid;
        if (pid > 0) return [pid];
        return AudioRouter.FindGameAudioPids(_ctl.Current.Dir ?? "");
    }

    // 게임 시작 직후엔 오디오 세션이 없을 수 있어 2초 간격으로 최대 ~30초 재시도합니다.
    void ScheduleAutoRoute()
    {
        StopAutoRoute();
        if (!AudioRouter.Supported || _selectedDeviceId.Length == 0) return;
        _autoRouteRetries = 0;
        _autoRouteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoRouteTimer.Tick += OnAutoRouteTick;
        _autoRouteTimer.Start();
    }

    void OnAutoRouteTick(object? sender, EventArgs e)
    {
        if (!_isNativeRunning) { StopAutoRoute(); return; }
        var pids = GameAudioPids();
        if (pids.Count > 0)
        {
            StopAutoRoute();
            bool ok = AudioRouter.RoutePids(pids, _selectedDeviceId);
            TrackRouted(pids);
            UiLog.Write($"audioroute: auto device={_selectedDeviceId} pids=[{string.Join(",", pids)}] ok={ok}");
        }
        else if (++_autoRouteRetries >= 15)
        {
            StopAutoRoute();
            UiLog.Write("audioroute: auto gave up (no game audio session appeared)");
        }
    }

    void StopAutoRoute()
    {
        if (_autoRouteTimer == null) return;
        _autoRouteTimer.Stop();
        _autoRouteTimer.Tick -= OnAutoRouteTick;
        _autoRouteTimer = null;
    }

    void OnGameAudioSessionEnded()
    {
        StopAutoRoute();
        if (_routedGamePids.Count > 0 && AudioRouter.Supported)
        {
            try
            {
                AudioRouter.RoutePids(_routedGamePids, null);
                UiLog.Write($"audioroute: reset to default pids=[{string.Join(",", _routedGamePids)}]");
            }
            catch { /* 종료 시점엔 pid가 이미 죽었을 수 있음 — best effort */ }
        }
        _routedGamePids.Clear();
    }

    void OnAbout(object s, RoutedEventArgs e) => new AboutWindow(this, CoreInterop.Version()).ShowDialog();

    async void OnCheckUpdate(object s, RoutedEventArgs e)
    {
        try
        {
            var result = await UpdateService.CheckForUpdateAsync(includeBeta: UpdateService.WantsBeta(_ctl.Settings.UpdateChannel));
            if (result.IsError)
            {
                MessageBox.Show(this, result.Message, "업데이트 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!result.HasUpdate || result.Release == null)
            {
                MessageBox.Show(this, result.Message, "업데이트 확인", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (result.TargetAsset == null)
            {
                MessageBox.Show(this, $"{result.Release.TagName} 버전이 확인되었으나, 설치에 적합한 파일을 찾을 수 없습니다.", "업데이트 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = new UpdateNotesWindow(this, result.Message, result.ReleaseNotes, "지금 다운로드하고 업데이트를 진행하시겠습니까?");
            if (confirm.ShowDialog() != true) return;

            // 빠른 업데이트: 포터블 zip에서 바뀐 파일만 받아 두었다가, 앱을 끈 뒤 덮어쓰고 다시 켬 (설치판도 같음)
            if (UpdateService.PortableAsset(result.Release) is { } portable)
            {
                string staging = UpdateService.StagingDir(result.Release.TagName);
                int changed = 0;
                var stageWnd = new UpdateProgressWindow("업데이트 준비 중...", async (report, ct) =>
                {
                    await Task.Run(() => { if (Directory.Exists(staging)) Directory.Delete(staging, true); }, ct);
                    changed = await DeltaUpdate.StageAsync(portable.DownloadUrl, portable.Size, UpdateService.AppDir(), staging, report, ct);
                }) { Owner = this };
                if (stageWnd.ShowDialog() != true) return;
                if (changed == 0)
                {
                    MessageBox.Show(this, "이미 새 버전과 같은 파일을 쓰고 있습니다.", "RocketRPG 업데이트", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (UpdateService.ApplyStagedUpdate(staging, result.Release.TagName)) Application.Current.Shutdown();
                else MessageBox.Show(this, "업데이트를 적용하지 못했습니다. 관리자 권한을 허용했는지 확인해 주세요.", "업데이트 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var progressWnd = new UpdateProgressWindow(
                result.TargetAsset.DownloadUrl,
                result.TargetAsset.Name,
                result.TargetAsset.Size)
            {
                Owner = this
            };

            bool? dialogRes = progressWnd.ShowDialog();
            if (dialogRes == true && progressWnd.IsCompleted && !string.IsNullOrEmpty(progressWnd.DownloadedFilePath))
            {
                if (result.IsInstalled)
                {
                    UpdateService.ApplyInstallerUpdate(progressWnd.DownloadedFilePath);
                }
                else
                {
                    UpdateService.ApplyPortableUpdate(progressWnd.DownloadedFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"업데이트 처리 중 오류가 발생했습니다:\n{ex.Message}", "업데이트 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void SavePerGame()
    {
        _ctl.SaveSettings();
        if (!string.IsNullOrEmpty(_currentDir))
        {
            var cfg = new GameConfig
            {
                Ratio = _ctl.Settings.Ratio,
                Gamma = _ctl.Gamma,
                Volume = _ctl.Settings.Volume,
                Filter = _ctl.Settings.Filter,
                InGameFontFamily = _ctl.Settings.InGameFontFamily,
                InGameFontSize = _ctl.Settings.InGameFontSize,
                InGameFontBold = _ctl.Settings.InGameFontBold,
                AutoMessageEnabled = _autoAdvance.IsAutoEnabled,
                AutoMessageSpeed = _autoAdvance.Speed,
                ShowMessageBar = _messageBarEnabled,
                FrameRate = _frameRate,
                VSync = _vsync,
                HudX = _hudPos?.X,
                HudY = _hudPos?.Y,
                HudLocked = _hudLocked,
                DismissedNotices = _dismissedNotices
            };
            GameSettingsService.Save(_currentDir, cfg);
        }
    }
}
