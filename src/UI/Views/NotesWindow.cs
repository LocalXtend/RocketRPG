#nullable enable
using System;
using System.ComponentModel;
using System.Windows;

namespace RocketRPG.Views;

/// <summary>
/// 노트를 따로 띄우는 창. 닫기(✕)는 창을 없애지 않고 숨깁니다 (노트 끄기와 같음).
/// RocketRPG 창에 딸린 창이라 항상 RocketRPG 위에 보입니다.
/// </summary>
internal sealed class NotesWindow : Window
{
    bool _reallyClose;

    public event Action? HideRequested;

    public NotesWindow(Window owner, double[] bounds)
    {
        Owner = owner;
        Title = "노트";
        Background = SystemColors.ControlBrush;
        ShowInTaskbar = false;
        ShowActivated = false;   // 노트를 켜도 게임 입력(포커스)을 빼앗지 않음
        MinWidth = 260;
        MinHeight = 240;
        FontFamily = owner.FontFamily;
        if (bounds is [var x, var y, var w, var h] && w > 0 && h > 0)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = x; Top = y; Width = w; Height = h;
        }
        else
        {
            // 처음엔 RocketRPG 창 오른쪽 옆에
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 380;
            Height = Math.Max(400, owner.ActualHeight);
            Left = owner.Left + owner.ActualWidth + 4;
            Top = owner.Top;
            var wa = SystemParameters.VirtualScreenWidth + SystemParameters.VirtualScreenLeft;
            if (Left + Width > wa) Left = Math.Max(SystemParameters.VirtualScreenLeft, owner.Left - Width - 4);
        }
    }

    public double[] Bounds => [Left, Top, ActualWidth, ActualHeight];

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            e.Cancel = true;
            HideRequested?.Invoke();
        }
        base.OnClosing(e);
    }

    public void ReallyClose()
    {
        _reallyClose = true;
        Close();
    }
}
