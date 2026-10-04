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
    // ── 토스트(알림) 규칙 ──
    // 1. 사용자가 한 조작(켜기/끄기, 배속, 일시정지, 밝기 등)의 알림은 MainWindow의 해당 처리기에서만 띄웁니다.
    // 2. 브리지/엔진(에이전트 N 줄, WebView 'notification')은 MainWindow가 알 수 없는 결과만 보냅니다
    //    (퀵 세이브/로드 완료·실패, 워프, 지원하지 않는 기능 안내).
    // 3. 같은 문장이나 같은 머리말("벽 통과: …")의 토스트가 떠 있으면 새로 쌓지 않고 그 토스트를 갱신합니다.
    readonly Dictionary<string, (Border border, TextBlock text, DispatcherTimer timer)> _activeToasts = new();

    static string ToastKey(string message)
    {
        int colon = message.IndexOf(':');
        return colon > 0 && colon <= 20 ? message[..colon] : message;
    }

    void ShowHudMessage(string message, int durationMs = 2000)
    {
        Dispatcher.Invoke(() =>
        {
            string key = ToastKey(message);
            if (_activeToasts.TryGetValue(key, out var existing) && ToastStack.Children.Contains(existing.border))
            {
                existing.text.Text = message;
                existing.border.BeginAnimation(UIElement.OpacityProperty, null);
                existing.border.Opacity = 1;
                existing.timer.Stop();
                existing.timer.Interval = TimeSpan.FromMilliseconds(durationMs);
                existing.timer.Start();
                return;
            }

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 3, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 6,
                    ShadowDepth = 2,
                    Opacity = 0.25
                }
            };

            var tb = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = this.FontFamily ?? new FontFamily("맑은 고딕, Segoe UI, sans-serif"),
                TextAlignment = TextAlignment.Center
            };
            border.Child = tb;

            while (ToastStack.Children.Count >= 5)
            {
                ToastStack.Children.RemoveAt(0);
            }

            ToastStack.Children.Add(border);

            if (OverlayCanAnimate)
                border.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (!OverlayCanAnimate)
                {
                    ToastStack.Children.Remove(border);
                    if (_activeToasts.TryGetValue(key, out var c) && c.border == border) _activeToasts.Remove(key);
                    return;
                }
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
                fadeOut.Completed += (s2, e2) =>
                {
                    if (timer.IsEnabled) return; // 페이드 중에 같은 알림으로 갱신됨
                    ToastStack.Children.Remove(border);
                    if (_activeToasts.TryGetValue(key, out var cur) && cur.border == border) _activeToasts.Remove(key);
                };
                border.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
            _activeToasts[key] = (border, tb, timer);
            timer.Start();
        });
    }
}
