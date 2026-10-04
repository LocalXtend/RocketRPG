#nullable enable
using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;

namespace RocketRPG.Models;

/// <summary>
/// 진단용 (RR_UIPROBE=1): UI 스레드에서 오래 걸린 작업(30ms 이상)과 입력 지연을 ui.log에 남깁니다.
/// 노트 입력이 버벅이는 원인처럼 "어디서 UI가 막히는지" 찾을 때만 켭니다.
/// </summary>
public static class UiProbe
{
    static readonly FieldInfo? MethodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void StartIfRequested(Dispatcher d)
    {
        if (Environment.GetEnvironmentVariable("RR_UIPROBE") != "1") return;
        UiLog.Write("uiprobe: on");
        long started = 0;
        d.Hooks.OperationStarted += (_, e) => started = Clock.ElapsedTicks;
        d.Hooks.OperationCompleted += (_, e) =>
        {
            double ms = (Clock.ElapsedTicks - started) * 1000.0 / Stopwatch.Frequency;
            if (ms < 30) return;
            string what = MethodField?.GetValue(e.Operation) is Delegate del ? $"{del.Method.DeclaringType?.Name}.{del.Method.Name}" : "?";
            UiLog.Write($"uiprobe: {ms:0} ms {e.Operation.Priority} {what}");
        };
        // 입력 우선순위 작업이 얼마나 늦게 실행되는지 (UI가 막힌 시간)
        var t = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        long last = Clock.ElapsedMilliseconds;
        t.Tick += (_, _) =>
        {
            long now = Clock.ElapsedMilliseconds;
            long late = now - last - 50;
            last = now;
            if (late >= 60) UiLog.Write($"uiprobe: UI blocked ~{late} ms");
        };
        t.Start();
        // (CompositionTarget.Rendering은 붙이면 매 프레임 다시 그리게 되어 측정을 흐리므로 쓰지 않습니다)
        // 키 입력이 UI 스레드에서 처리되기까지 걸린 시간 (Windows가 키를 받은 시각 → WPF가 처리한 시각)
        System.Windows.Input.InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is System.Windows.Input.KeyEventArgs k && k.RoutedEvent == System.Windows.Input.Keyboard.PreviewKeyDownEvent)
            {
                int late = Environment.TickCount - k.Timestamp;
                if (late >= 30) UiLog.Write($"uiprobe: key {k.Key} handled {late} ms late");
            }
        };
    }
}
