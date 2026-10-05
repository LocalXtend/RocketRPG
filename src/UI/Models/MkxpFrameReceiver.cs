#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace RocketRPG.Models;

/// <summary>
/// mkxp-z 에이전트가 보내는 게임 화면을 받는 파이프. 받은 화면은 멀티 방송이 읽는 메모리(GameFrameFeed와 같은 구성)에 둡니다.
/// 파이프 한 장: 'RRFR', 폭, 높이, 바이트 수 (각 uint32) + RGBA.
/// </summary>
sealed class MkxpFrameReceiver : IDisposable
{
    const int HeaderBytes = 4096, SlotBytes = 1280 * 960 * 4, Magic = 0x52524652;
    public string PipeName { get; } = "RocketRPG_frames_" + Guid.NewGuid().ToString("N");
    public IntPtr Buffer { get; }
    readonly System.Threading.CancellationTokenSource _cts = new();

    // 한 번 만들어 계속 씀: 게임을 바꿀 때 방송 스레드가 아직 읽고 있어도 안전하도록 풀지 않습니다 (게임은 한 번에 하나)
    static readonly IntPtr SharedBuffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(HeaderBytes + 2 * SlotBytes);

    public MkxpFrameReceiver()
    {
        Buffer = SharedBuffer;
        for (int i = 1; i < 8; i++) System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, i * 4, 0);
        System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 0, Magic);
        System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 6 * 4, SlotBytes);
        _ = System.Threading.Tasks.Task.Run(() => ServeAsync(_cts.Token));
    }

    async System.Threading.Tasks.Task ServeAsync(System.Threading.CancellationToken ct)
    {
        var head = new byte[16];
        byte[] data = [];
        int slot = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new System.IO.Pipes.NamedPipeServerStream(PipeName, System.IO.Pipes.PipeDirection.In, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 8 << 20, 0);
                await server.WaitForConnectionAsync(ct);
                while (server.IsConnected && !ct.IsCancellationRequested)
                {
                    await server.ReadExactlyAsync(head, ct);
                    int magic = BitConverter.ToInt32(head, 0), w = BitConverter.ToInt32(head, 4), h = BitConverter.ToInt32(head, 8), len = BitConverter.ToInt32(head, 12);
                    if (magic != Magic || w <= 0 || h <= 0 || len != w * h * 4 || len > 64 << 20) break;   // 어긋나면 다시 연결
                    if (data.Length < len) data = new byte[len];
                    await server.ReadExactlyAsync(data.AsMemory(0, len), ct);
                    if (len > SlotBytes) continue;
                    slot ^= 1;
                    System.Runtime.InteropServices.Marshal.Copy(data, 0, Buffer + HeaderBytes + slot * SlotBytes, len);
                    System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 2 * 4, slot);
                    System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 3 * 4, w);
                    System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 4 * 4, h);
                    System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 5 * 4, 0);   // RGBA
                    System.Threading.Thread.MemoryBarrier();
                    System.Runtime.InteropServices.Marshal.WriteInt32(Buffer, 4, System.Runtime.InteropServices.Marshal.ReadInt32(Buffer, 4) + 1);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.EndOfStreamException)
            {
                await System.Threading.Tasks.Task.Delay(200, CancellationToken.None);
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
