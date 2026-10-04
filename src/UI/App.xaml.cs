using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using RocketRPG.Models;

namespace RocketRPG;

public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDllDirectoryW(string lpPathName);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var exeDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(exeDir)) SetDllDirectoryW(exeDir);

        try
        {
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(CoreInterop).Assembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName.Equals("RocketRPGCore.dll", StringComparison.OrdinalIgnoreCase))
                {
                    string target = System.IO.Path.Combine(exeDir, "RocketRPGCore.dll");
                    if (System.IO.File.Exists(target) && System.Runtime.InteropServices.NativeLibrary.TryLoad(target, out var handle))
                        return handle;
                }
                return IntPtr.Zero;
            });
        }
        catch { }

        // 디스코드 '참가' 등 rocketrpg://join/코드 로 켜졌을 때: 이미 켜진 RocketRPG가 있으면 그쪽에 넘기고 끝냄
        string? joinCode = e.Args.Select(MultiLink.ParseCode).FirstOrDefault(c => c != null);
        if (joinCode != null && MultiLink.TryForward(joinCode))
        {
            Environment.Exit(0);   // 아무것도 띄우지 않고 바로 끝냄 (Shutdown은 창이 잠깐 뜸)
        }
        MultiLink.PendingCode = joinCode;

        CrashReporter.InstallNativeCrashHandler();
        try { TimeBeginPeriod(1); } catch { }
        UiLog.Write("app: OnStartup");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TimeEndPeriod(1);
        UiLog.Write($"app: OnExit code={e.ApplicationExitCode}");
        base.OnExit(e);
    }

    // UI 스레드의 처리되지 않은 예외: 리포트를 기록하고 짧은 안내 후 종료합니다.
    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;
        for (int depth = 0; ex != null && depth < 6; depth++, ex = ex.InnerException)
            UiLog.Write($"dispatcher EX[{depth}] {ex.GetType().Name}: {ex.Message}");
        CrashReporter.Write(e.Exception, "UI Dispatcher");
        MessageBox.Show(
            $"예기치 않은 오류가 발생하여 종료합니다.\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\n상세 정보는 crash 폴더에 기록되었습니다.",
            "RocketRPG", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(-1);
    }

    // 백그라운드/Finalizer 예외: 기록 및 종료 시 안내합니다.
    void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        CrashReporter.Write(ex, $"AppDomain (terminating={e.IsTerminating})");
        if (e.IsTerminating)
        {
            try
            {
                MessageBox.Show(
                    $"프로그램 실행 중 치명적인 오류가 발생했습니다.\n{ex?.GetType().Name}: {ex?.Message}\n\n상세 정보는 crash 폴더에 기록되었습니다.",
                    "RocketRPG", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }
    }

    // 관찰되지 않은 Task 예외: 기록만 하고 관찰 처리합니다.
    void OnUnobservedTaskException(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        CrashReporter.Write(e.Exception, "TaskScheduler");
        e.SetObserved();
    }
}
