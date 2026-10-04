#nullable enable
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RocketRPG.Models;

/// <summary>
/// rocketrpg://join/코드 링크 (디스코드 '참가' 단추 → 서버의 /join 페이지 → 이 링크).
/// RocketRPG가 이미 켜져 있으면 새로 켜지 않고 켜진 쪽에 코드를 넘깁니다(파이프).
/// </summary>
public static class MultiLink
{
    static readonly string PipeName = "RocketRPG_link_" + Regex.Replace(Environment.UserName, "[^A-Za-z0-9_]", "_");
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>명령줄로 받은 방 코드 (앱이 링크로 켜졌을 때)</summary>
    public static string? PendingCode { get; set; }

    /// <summary>켜져 있는 동안 다른 RocketRPG가 넘겨준 방 코드 (UI 스레드에서)</summary>
    public static event Action<string>? CodeReceived;

    public static string? ParseCode(string? arg)
    {
        var m = Regex.Match(arg ?? "", @"^rocketrpg://join/([A-Za-z0-9]{4,12})/?$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>켜져 있는 RocketRPG에 코드를 넘김. 넘겼으면 true (이 프로세스는 끝내면 됨)</summary>
    public static bool TryForward(string code)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(400);
            AllowSetForegroundWindow(-1);   // 받는 쪽이 창을 앞으로 가져올 수 있게
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.WriteLine(code);
            return true;
        }
        catch { return false; }
    }

    /// <summary>다른 RocketRPG가 넘기는 코드를 기다림 (먼저 켜진 하나만)</summary>
    public static void Listen()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var r = new StreamReader(server);
                    string? code = ParseCode("rocketrpg://join/" + (await r.ReadLineAsync())?.Trim());
                    if (code != null) System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => CodeReceived?.Invoke(code));
                }
                catch (IOException) { return; }   // 다른 RocketRPG가 이미 듣고 있음
                catch (Exception ex) { UiLog.Write($"multi link: {ex.Message}"); await Task.Delay(1000); }
            }
        });
    }

    /// <summary>
    /// rocketrpg:// 링크를 이 RocketRPG로 열도록 등록 (사용자 계정, 관리자 권한 필요 없음). 개발 빌드(bin\Debug 등)에서는 하지 않습니다.
    /// </summary>
    public static void Register()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0 || exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase)) return;
            string command = $"\"{exe}\" \"%1\"";
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\rocketrpg");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            if (cmd.GetValue("") as string == command) return;
            key.SetValue("", "URL:RocketRPG");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            cmd.SetValue("", command);
            UiLog.Write("multi link: registered rocketrpg:// links");
        }
        catch (Exception ex) { UiLog.Write($"multi link: register failed {ex.Message}"); }
    }
}
