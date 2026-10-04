#nullable enable
using System;
using System.Linq;
using System.Windows;
using RocketRPG.Models;

namespace RocketRPG.Views;

public partial class DialogueLogWindow : Window
{
    public DialogueLogWindow()
    {
        InitializeComponent();
        RefreshLog();
        DialogueLogManager.LogUpdated += OnLogUpdated;
        Closed += (_, _) => DialogueLogManager.LogUpdated -= OnLogUpdated;
    }

    private void OnLogUpdated()
    {
        Dispatcher.BeginInvoke(new Action(RefreshLog));
    }

    private void RefreshLog()
    {
        var entries = DialogueLogManager.GetEntries();
        // 바뀐 게 없으면 목록을 다시 만들지 않습니다 (항목의 '복사됨' 표시와 스크롤 위치 유지)
        if (LogListBox.ItemsSource is System.Collections.Generic.IList<string> cur && cur.SequenceEqual(entries)) return;
        LogListBox.ItemsSource = entries;
        if (entries.Count > 0)
        {
            LogListBox.ScrollIntoView(entries[^1]);
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        DialogueLogManager.Clear();
        RefreshLog();
    }

    private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
    {
        var entries = DialogueLogManager.GetEntries();
        if (entries.Count > 0)
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine + Environment.NewLine, entries));
                MessageBox.Show(this, "전체 대사 기록이 클립보드에 복사되었습니다.", "복사 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"클립보드 복사 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    // 대사 하나만 복사: 창을 띄우지 않고 버튼 글자로 잠깐 알려 줍니다.
    private void BtnCopyOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not string text) return;
        try
        {
            Clipboard.SetText(text);
            btn.Content = "복사됨";
        }
        catch
        {
            btn.Content = "실패";
        }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) => { timer.Stop(); btn.Content = "복사"; };
        timer.Start();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
