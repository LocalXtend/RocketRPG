using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace RocketRPG.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            var ex = ev.ExceptionObject as Exception;
            MessageBox.Show($"설치 프로그램 실행 중 예외가 발생했습니다.\n{ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}",
                "RocketRPG 설치 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (s, ev) =>
        {
            MessageBox.Show($"설치 프로그램 실행 중 예외가 발생했습니다.\n{ev.Exception?.GetType().Name}: {ev.Exception?.Message}\n{ev.Exception?.StackTrace}",
                "RocketRPG 설치 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
            Shutdown(1);
        };

        base.OnStartup(e);

        var procName = Path.GetFileName(Environment.ProcessPath ?? "");
        bool isUninstall = procName.StartsWith("unist000", StringComparison.OrdinalIgnoreCase) ||
                           procName.StartsWith("unins000", StringComparison.OrdinalIgnoreCase) ||
                           e.Args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));

        if (isUninstall)
        {
            InstallWindow.RunUninstall();
            Shutdown();
            return;
        }

        if (e.Args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase) || a.Equals("/update", StringComparison.OrdinalIgnoreCase)))
        {
            bool ok = InstallWindow.PerformInstall();
            if (ok && e.Args.Any(a => a.Equals("/update", StringComparison.OrdinalIgnoreCase)))
            {
                string targetExe = Path.Combine(InstallWindow.TargetDir, "RocketRPG.exe");
                if (File.Exists(targetExe))
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(targetExe) { UseShellExecute = true }); } catch { }
                }
            }
            Shutdown(ok ? 0 : 1);
            return;
        }

        var wnd = new InstallWindow();
        wnd.Show();
    }
}
