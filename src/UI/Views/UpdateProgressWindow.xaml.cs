#nullable enable
using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>업데이트 진행 창: 받는 동안 진행 막대를 보여 주고, 취소할 수 있습니다.</summary>
public partial class UpdateProgressWindow : Window
{
    readonly Func<DeltaUpdate.Progress, CancellationToken, Task> _job;
    readonly CancellationTokenSource _cts = new();
    string? _tempFilePath;
    long _lastUiTick;

    public string? DownloadedFilePath { get; private set; }
    public bool IsCompleted { get; private set; }

    /// <summary>파일 하나를 그대로 받음 (빠른 업데이트를 쓸 수 없는 릴리즈)</summary>
    public UpdateProgressWindow(string downloadUrl, string fileName, long expectedSize)
    {
        InitializeComponent();
        StatusText.Text = $"다운로드 중: {fileName}";
        _job = (report, ct) => DownloadAsync(downloadUrl, fileName, expectedSize, report, ct);
        Loaded += OnLoaded;
    }

    /// <summary>임의의 작업 (빠른 업데이트: 바뀐 파일만 받아 풀기)</summary>
    public UpdateProgressWindow(string title, Func<DeltaUpdate.Progress, CancellationToken, Task> job)
    {
        InitializeComponent();
        StatusText.Text = title;
        _job = job;
        Loaded += OnLoaded;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _job(Report, _cts.Token);
            IsCompleted = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            CleanupPartialFile();
            DialogResult = false;
        }
        catch (Exception ex)
        {
            CleanupPartialFile();
            UiLog.Write($"update: failed {ex}");
            MessageBox.Show(this, $"업데이트를 받는 중 오류가 발생했습니다:\n{ex.Message}", "다운로드 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
        }
    }

    /// <summary>
    /// 진행 상황 (자주 불려도 화면은 0.1초에 한 번만 바꿈). 압축 풀기·받기는 다른 스레드에서 알려 오므로
    /// 화면은 늘 UI 스레드에서 바꿉니다 (1.1.0~1.1.2: 다른 스레드에서 바꾸다 '다른 스레드가 이 개체를 소유…' 오류로 업데이트 실패).
    /// </summary>
    void Report(string status, long done, long total)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Report(status, done, total));
            return;
        }
        long now = Environment.TickCount64;
        if (done < total && now - _lastUiTick < 100 && total > 0) return;
        _lastUiTick = now;
        StatusText.Text = status;
        DownloadBar.IsIndeterminate = total <= 0;
        if (total > 0)
        {
            double pct = Math.Min(100, (double)done / total * 100.0);
            DownloadBar.Value = pct;
            DetailText.Text = $"{done / 1048576.0:0.0} MB / {total / 1048576.0:0.0} MB ({pct:0}%)";
        }
        else DetailText.Text = done > 0 ? $"{done / 1048576.0:0.0} MB" : "";
    }

    async Task DownloadAsync(string url, string fileName, long expectedSize, DeltaUpdate.Progress report, CancellationToken ct)
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), fileName);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RocketRPG");
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long totalBytes = resp.Content.Headers.ContentLength ?? expectedSize;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(_tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        byte[] buffer = new byte[1 << 20];
        long totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;
            report($"다운로드 중: {fileName}", totalRead, totalBytes);
        }
        DownloadedFilePath = _tempFilePath;
    }

    void CleanupPartialFile()
    {
        if (!string.IsNullOrEmpty(_tempFilePath) && File.Exists(_tempFilePath))
        {
            try { File.Delete(_tempFilePath); } catch { }
        }
    }

    void OnCancelClick(object sender, RoutedEventArgs e) => _cts.Cancel();

    void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!IsCompleted) _cts.Cancel();
    }
}
