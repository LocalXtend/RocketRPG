#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RocketRPG.Views;

/// <summary>
/// 새 버전 안내: 업데이트 내용(마크다운)을 보여 주고 지금 업데이트할지 묻습니다.
/// 기본 메시지 상자와 같은 모양(아이콘·글·예/아니요)이지만, 내용이 길면 창 안에서 스크롤되고 창은 화면을 넘지 않습니다.
/// </summary>
internal sealed class UpdateNotesWindow : Window
{
    public UpdateNotesWindow(Window owner, string headline, string notesMarkdown, string question)
    {
        Owner = owner;
        Title = "RocketRPG 업데이트";
        Width = 600;
        MinWidth = 420;
        MinHeight = 260;
        var work = SystemParameters.WorkArea;
        MaxHeight = Math.Max(MinHeight, work.Height * 0.85);
        MaxWidth = Math.Max(MinWidth, work.Width * 0.9);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = SystemColors.ControlBrush;
        FontFamily = owner.FontFamily;

        var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        try
        {
            icon.Source = Imaging.CreateBitmapSourceFromHIcon(System.Drawing.SystemIcons.Information.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch { icon.Visibility = Visibility.Collapsed; }
        var head = new DockPanel { Margin = new Thickness(12, 12, 12, 8) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(new TextBlock { Text = headline, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });

        var notes = new MarkdownView { FontFamily = FontFamily, MinHeight = 120, Margin = new Thickness(12, 0, 12, 0) };
        notes.Markdown = string.IsNullOrWhiteSpace(notesMarkdown) ? "(업데이트 내용이 없습니다)" : notesMarkdown;

        var ask = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 10, 12, 0) };
        var yes = new Button { Content = "예(_Y)", Width = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var no = new Button { Content = "아니요(_N)", Width = 88, IsCancel = true };
        yes.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 10, 12, 12), Children = { yes, no } };

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(ask, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(buttons);
        root.Children.Add(ask);
        root.Children.Add(notes);
        Content = root;
        Loaded += (_, _) => yes.Focus();
    }
}
