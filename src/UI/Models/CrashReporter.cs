#nullable enable
using System;
using System.IO;
using System.Text;

namespace RocketRPG.Models;

/// <summary>
/// M8: crash/crash-타임스탬프-에러코드.txt 크래시 리포트 기록기.
/// 크래시 처리 중 이중 크래시를 방지하기 위해 모든 경로가 예외를 던지지 않습니다.
/// </summary>
public static class CrashReporter
{
    /// <summary>
    /// 네이티브 크래시 핸들러(RocketRPGCore.dll)를 설치합니다.
    /// 예외 처리기 안에서 .NET 코드를 실행하면 런타임 자체가 깨지므로(0x80131506) 순수 Win32 처리기를 사용합니다.
    /// DllImport 경로 확인자가 등록된 뒤에 호출해야 합니다.
    /// </summary>
    public static void InstallNativeCrashHandler()
    {
        try
        {
            string dir = Path.Combine(SettingsService.Root(), "crash");
            int st = CoreInterop.rpg_install_crash_handler(dir);
            UiLog.Write($"CrashReporter: native handler installed (status={st}, dir={dir})");
        }
        catch (Exception ex)
        {
            UiLog.Write($"CrashReporter: native handler unavailable: {ex.Message}");
        }
    }

    public static void Write(Exception? ex, string source)
    {
        try
        {
            string dir = Path.Combine(SettingsService.Root(), "crash");
            Directory.CreateDirectory(dir);
            string code = ex != null ? (ex.HResult & 0xFFFFFFFF).ToString("X8") : "UNKNOWN";
            string file = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{code}.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"timestamp : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"source    : {source}");
            sb.AppendLine();
            AppendException(sb, ex, 0);
            sb.AppendLine();
            try { sb.AppendLine($"core.lastError : {CoreInterop.LastError()}"); } catch { }
            sb.AppendLine($"os        : {Environment.OSVersion.VersionString}");
            sb.AppendLine($"process   : {(Environment.Is64BitProcess ? "x64" : "x86")}");
            try { sb.AppendLine($"app       : RocketRPG {CoreInterop.Version()}"); } catch { }
            sb.AppendLine($"runtime   : {Environment.Version}");

            File.WriteAllText(file, sb.ToString());
        }
        catch
        {
            // 크래시 핸들러는 절대 throw하지 않음
        }
    }

    static void AppendException(StringBuilder sb, Exception? ex, int depth)
    {
        if (ex == null) { sb.AppendLine("(예외 정보 없음)"); return; }
        string pad = new string(' ', depth * 2);
        sb.AppendLine($"{pad}[{depth}] type    : {ex.GetType().FullName}");
        sb.AppendLine($"{pad}    message : {ex.Message}");
        sb.AppendLine($"{pad}    hresult : 0x{(ex.HResult & 0xFFFFFFFF):X8}");
        sb.AppendLine($"{pad}    stack   :");
        sb.AppendLine(ex.StackTrace ?? $"{pad}    (없음)");
        if (ex.InnerException != null)
        {
            sb.AppendLine($"{pad}    inner:");
            AppendException(sb, ex.InnerException, depth + 1);
        }
    }
}
