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
    // ── 변수 HUD 이동 (게임별 저장) ──
    // 위치는 게임 화면(오버레이) 크기에 대한 비율로 저장해 창 크기가 바뀌어도 같은 자리에 둡니다. null = 기본 위치(왼쪽 위).
    Point? _hudPos;
    bool _hudLocked;
    Point? _hudDragStart;
    Thickness _hudDragMargin;
    static readonly Thickness HudDefaultMargin = new(12, 12, 0, 0);

    void SetupPinnedHud()
    {
        var hud = PinnedVarsOverlay;
        hud.IsHitTestVisible = true;
        hud.MouseLeftButtonDown += (_, e) =>
        {
            if (_hudLocked) return;
            _hudDragStart = e.GetPosition(_overlay.Root);
            _hudDragMargin = hud.Margin;
            hud.CaptureMouse();
            e.Handled = true;
        };
        hud.MouseMove += (_, e) =>
        {
            if (_hudDragStart is not { } s || !hud.IsMouseCaptured) return;
            var p = e.GetPosition(_overlay.Root);
            double maxX = Math.Max(0, _overlay.Root.ActualWidth - hud.ActualWidth);
            double maxY = Math.Max(0, _overlay.Root.ActualHeight - hud.ActualHeight);
            hud.Margin = new Thickness(Math.Clamp(_hudDragMargin.Left + p.X - s.X, 0, maxX),
                                       Math.Clamp(_hudDragMargin.Top + p.Y - s.Y, 0, maxY), 0, 0);
        };
        hud.MouseLeftButtonUp += (_, e) =>
        {
            if (_hudDragStart == null) return;
            _hudDragStart = null;
            hud.ReleaseMouseCapture();
            double w = Math.Max(1, _overlay.Root.ActualWidth), h = Math.Max(1, _overlay.Root.ActualHeight);
            _hudPos = new Point(hud.Margin.Left / w, hud.Margin.Top / h);
            SavePerGame();
        };

        var lockItem = new MenuItem { Header = "위치 고정", IsCheckable = true };
        lockItem.Click += (_, _) =>
        {
            _hudLocked = lockItem.IsChecked;
            ApplyHudPosition();
            SavePerGame();
            ShowHudMessage($"변수 HUD 위치 고정: {(_hudLocked ? "켜짐" : "꺼짐")}");
        };
        var resetItem = new MenuItem { Header = "기본 위치로 되돌리기" };
        resetItem.Click += (_, _) =>
        {
            _hudPos = null;
            _hudLocked = false;   // 되돌리기는 고정을 무시하고 해제합니다
            ApplyHudPosition();
            SavePerGame();
            ShowHudMessage("변수 HUD: 기본 위치");
        };
        var menu = new ContextMenu();
        menu.Items.Add(lockItem);
        menu.Items.Add(resetItem);
        menu.Opened += (_, _) => lockItem.IsChecked = _hudLocked;
        hud.ContextMenu = menu;
        _overlay.Root.SizeChanged += (_, _) => ApplyHudPosition();
    }

    void ApplyHudPosition()
    {
        var hud = PinnedVarsOverlay;
        hud.Cursor = _hudLocked ? null : Cursors.SizeAll;
        hud.ToolTip = _hudLocked ? "오른쪽 클릭: 위치 고정 해제 / 기본 위치" : "끌어서 옮기기 · 오른쪽 클릭: 위치 고정 / 기본 위치";
        if (_hudPos is not { } p) { hud.Margin = HudDefaultMargin; return; }
        double w = _overlay.Root.ActualWidth, h = _overlay.Root.ActualHeight;
        if (w < 1 || h < 1) return;
        hud.Margin = new Thickness(Math.Clamp(p.X * w, 0, Math.Max(0, w - hud.ActualWidth)),
                                   Math.Clamp(p.Y * h, 0, Math.Max(0, h - hud.ActualHeight)), 0, 0);
    }

    static readonly Brush PinnedIdBrush = FrozenBrush(Color.FromRgb(0x80, 0x80, 0x80));
    static readonly Brush PinnedNameBrush = FrozenBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    static readonly Brush PinnedValueBrush = FrozenBrush(Color.FromRgb(0x00, 0x66, 0xCC));
    static Brush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private void OnPinnedVariablesChanged(List<VariableItem> pinned)
    {
        Dispatcher.Invoke(() =>
        {
            PinnedVarsStack.Children.Clear();
            var topPinned = pinned.Take(10).ToList();
            if (topPinned.Count == 0 || _ctl.Settings.StreamerMode)
            {
                PinnedVarsOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            // 토스트와 같은 흰 바탕·어두운 글자 (예전에는 이 HUD만 검은 바탕·형광 글자라 따로 놀았음)
            foreach (var item in topPinned)
            {
                var tb = new TextBlock
                {
                    FontFamily = this.FontFamily,
                    FontSize = 12,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                tb.Inlines.Add(new System.Windows.Documents.Run($"V{item.Id:D4} ") { Foreground = PinnedIdBrush });
                tb.Inlines.Add(new System.Windows.Documents.Run($"{item.Name}: ") { Foreground = PinnedNameBrush });
                tb.Inlines.Add(new System.Windows.Documents.Run(item.Value) { Foreground = PinnedValueBrush, FontWeight = FontWeights.SemiBold });
                PinnedVarsStack.Children.Add(tb);
            }
            PinnedVarsOverlay.Visibility = Visibility.Visible;
        });
    }
}
