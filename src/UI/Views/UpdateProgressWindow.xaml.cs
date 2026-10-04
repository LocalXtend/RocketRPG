#nullable enable
using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RocketRPG.Views;

public partial class UpdateProgressWindow : Window
{
    readonly string _downloadUrl;
    readonly string _fileName;
    readonly long _expectedSize;
    readonly CancellationTokenSource _cts = new();
    string? _tempFilePath;

    public string? DownloadedFilePath { get; private set; }
    public bool IsCompleted { get; private set; }

    public UpdateProgressWindow(string downloadUrl, string fileName, long expectedSize)
    {
        InitializeComponent();
        _downloadUrl = downloadUrl;
        _fileName = fileName;
        _expectedSize = expectedSize;
        StatusText.Text = $"다운로드 중: {_fileName}";
        Loaded += OnLoaded;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await StartDownloadAsync();
    }

    async Task StartDownloadAsync()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), _fileName);
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RocketRPG");

            using var resp = await client.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? _expectedSize;
            if (totalBytes <= 0)
            {
                DownloadBar.IsIndeterminate = true;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(_cts.Token);
            using var fs = new FileStream(_tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
            {
                await fs.WriteAsync(buffer, 0, read, _cts.Token);
                totalRead += read;

                if (totalBytes > 0)
                {
                    double pct = (double)totalRead / totalBytes * 100.0;
                    DownloadBar.Value = Math.Min(100, pct);
                    DetailText.Text = $"{(totalRead / 1048576.0):0.0} MB / {(totalBytes / 1048576.0):0.0} MB ({pct:0.0}%)";
                }
                else
                {
                    DetailText.Text = $"{(totalRead / 1048576.0):0.0} MB 다운로드됨";
                }
            }

            IsCompleted = true;
            DownloadedFilePath = _tempFilePath;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            CleanupPartialFile();
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            CleanupPartialFile();
            MessageBox.Show(this, $"다운로드 중 오류가 발생했습니다:\n{ex.Message}", "다운로드 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
            Close();
        }
    }

    void CleanupPartialFile()
    {
        if (!string.IsNullOrEmpty(_tempFilePath) && File.Exists(_tempFilePath))
        {
            try { File.Delete(_tempFilePath); } catch { }
        }
    }

    void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
    }

    void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!IsCompleted)
        {
            _cts.Cancel();
            CleanupPartialFile();
        }
    }
}
