#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;

namespace RocketRPG.Models;

/// <summary>
/// mkxp-z 안에서 실행되는 루비 에이전트(rocket_mkxp_agent.rb)와의 명명된 파이프 채널.
/// 에이전트가 한 줄을 보내면 즉시 대기 중인 명령들을 한 줄로 응답합니다(게임 스레드가 멈추지 않도록 항상 바로 응답).
/// 수신 줄은 UI 스레드에서 LineReceived로 전달됩니다.
/// </summary>
public sealed class MkxpAgentChannel : IDisposable
{
    public const char SepCmd = '\x1E';
    public const char SepField = '\x1F';
    public const char NewLine = '\x1D';

    public string PipeName { get; } = "RocketRPG_mkxp_" + Guid.NewGuid().ToString("N");
    public bool Connected { get; private set; }

    /// <summary>(kind, payload) — payload의 줄바꿈은 복원된 상태</summary>
    public event Action<string, string>? LineReceived;
    public event Action<bool>? ConnectionChanged;

    readonly ConcurrentQueue<string> _pending = new();
    readonly CancellationTokenSource _cts = new();
    readonly Thread _thread;

    public MkxpAgentChannel()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RocketRPG.MkxpAgent" };
        _thread.Start();
    }

    /// <summary>다음 응답에 실려 보낼 명령을 추가합니다.</summary>
    public void Send(string kind, params object[] args)
    {
        var sb = new StringBuilder(kind);
        foreach (var a in args)
        {
            sb.Append(SepField);
            string s = a switch
            {
                bool b => b ? "1" : "0",
                double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
                float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => a?.ToString() ?? ""
            };
            sb.Append(s.Replace("\r", "").Replace('\n', NewLine).Replace(SepCmd, ' ').Replace(SepField, ' '));
        }
        _pending.Enqueue(sb.ToString());
    }

    void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                SetConnected(true);
                Serve(server);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                UiLog.Write($"MkxpAgentChannel: {ex.GetType().Name}: {ex.Message}");
            }
            SetConnected(false);
            if (!_cts.IsCancellationRequested) Thread.Sleep(200);
        }
    }

    void Serve(NamedPipeServerStream server)
    {
        var buffer = new MemoryStream();
        var one = new byte[4096];
        while (!_cts.IsCancellationRequested && server.IsConnected)
        {
            int n = server.Read(one, 0, one.Length);
            if (n <= 0) break;
            int start = 0;
            for (int i = 0; i < n; i++)
            {
                if (one[i] != (byte)'\n') continue;
                buffer.Write(one, start, i - start);
                start = i + 1;
                string line = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                buffer.SetLength(0);
                // 카메라 줄("C")은 매 프레임 오고 답을 기다리지 않습니다. 나머지는 대기 중인 명령으로 바로 답합니다.
                if (!line.StartsWith("C" + SepField, StringComparison.Ordinal)) Reply(server);
                Dispatch(line);
            }
            if (start < n) buffer.Write(one, start, n - start);
        }
    }

    void Reply(Stream s)
    {
        var sb = new StringBuilder();
        while (_pending.TryDequeue(out var cmd))
        {
            if (sb.Length > 0) sb.Append(SepCmd);
            sb.Append(cmd);
        }
        sb.Append('\n');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        s.Write(bytes, 0, bytes.Length);
        s.Flush();
    }

    void Dispatch(string line)
    {
        int sep = line.IndexOf(SepField);
        string kind = sep < 0 ? line : line[..sep];
        string payload = sep < 0 ? "" : line[(sep + 1)..].Replace(NewLine, '\n');
        if (kind == "H") return; // heartbeat
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() => LineReceived?.Invoke(kind, payload));
    }

    void SetConnected(bool c)
    {
        if (Connected == c) return;
        Connected = c;
        var app = Application.Current;
        app?.Dispatcher.BeginInvoke(() => ConnectionChanged?.Invoke(c));
    }

    public void Dispose()
    {
        _cts.Cancel();
    }

    // ── 에이전트 공통 메시지 파서 (mkxp-z 루비 에이전트 / EasyRPG 패치 동일 형식) ──

    /// <summary>"M" 줄: busy␟speaker␟text</summary>
    /// <summary>"Q" 줄: 번호 ␟ 고른 것 ␟ 줄마다 (1|0 고를 수 있음) + 글. 줄이 없으면 닫힘.</summary>
    public static ChoiceState ParseChoice(string payload)
    {
        var f = payload.Split(SepField);
        var c = new ChoiceState
        {
            Gen = f.Length > 0 && int.TryParse(f[0], out int g) ? g : 0,
            Picked = f.Length > 1 && int.TryParse(f[1], out int p) ? p : -1,
        };
        if (f.Length > 2 && f[2].Length > 0)
            foreach (var line in f[2].Split('\n').Take(16))
                c.Items.Add((line.Length > 1 ? line[1..] : "", !line.StartsWith('0')));
        c.Open = c.Items.Count > 0;
        return c;
    }

    public static MessageState ParseMessage(string payload)
    {
        var f = payload.Split(SepField);
        return new MessageState
        {
            Busy = f.Length > 0 && f[0] == "1",
            Speaker = f.Length > 1 ? f[1] : "",
            Text = f.Length > 2 ? f[2] : ""
        };
    }

    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    static double D(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, Inv, out var v) ? v : 0;

    /// <summary>
    /// "E" 줄. 맵 좌표 형식: "tile,screenW,screenH,mapW,mapH,loopX,loopY,offX,offY,camX,camY␟id,tileX,tileY,trigger,name;..."
    /// (구 형식 "w,h␟id,x,y,trigger,name;..."는 게임 화면 좌표). 맵 좌표 형식이면 view에 카메라/맵 정보를 채웁니다.
    /// </summary>
    public static System.Collections.Generic.List<EspItem> ParseEsp(string payload, int tileSize, out int screenW, out int screenH, EspView? view = null)
    {
        screenW = screenH = 0;
        var list = new System.Collections.Generic.List<EspItem>();
        var parts = payload.Split(SepField, 2);
        if (parts.Length < 2) return list;
        var head = parts[0].Split(',');
        bool mapSpace = head.Length >= 11 && view != null;
        if (mapSpace)
        {
            view!.Tile = Math.Max(1, D(head[0]));
            view.ScreenW = screenW = (int)D(head[1]);
            view.ScreenH = screenH = (int)D(head[2]);
            view.MapW = (int)D(head[3]);
            view.MapH = (int)D(head[4]);
            view.LoopX = head[5] == "1";
            view.LoopY = head[6] == "1";
            view.OffX = D(head[7]);
            view.OffY = D(head[8]);
            view.CamX = D(head[9]);
            view.CamY = D(head[10]);
        }
        else if (head.Length >= 2)
        {
            screenW = (int)D(head[0]);
            screenH = (int)D(head[1]);
        }
        foreach (var item in parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = item.Split(',', 5);
            if (f.Length < 4) continue;
            var e = new EspItem
            {
                Id = int.TryParse(f[0], out var id) ? id : 0,
                Trigger = int.TryParse(f[3], out var t) ? t : 0,
                Name = f.Length > 4 ? f[4] : "",
                W = mapSpace ? view!.Tile : tileSize,
                H = mapSpace ? view!.Tile : tileSize
            };
            if (mapSpace) { e.View = view; e.TileX = D(f[1]); e.TileY = D(f[2]); }
            else { e.X = D(f[1]); e.Y = D(f[2]); }
            list.Add(e);
        }
        return list;
    }

    /// <summary>"C" 줄: "camX,camY" (맵 타일 좌표). 스크롤할 때 매 프레임 옵니다.</summary>
    public static bool ParseCamera(string payload, EspView view)
    {
        var f = payload.Split(',');
        if (f.Length < 2) return false;
        view.CamX = D(f[0]);
        view.CamY = D(f[1]);
        return true;
    }
}
