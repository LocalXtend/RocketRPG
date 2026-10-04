#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RocketRPG.Models;

/// <summary>
/// 멀티 방장: 게임 화면과 게임 소리를 잡아 방송 페이지와 함께 쓰는 메모리에 넣습니다.
/// 페이지가 그 메모리를 스스로 읽어 가므로(UI 스레드를 거치지 않음) 게임 창이 바빠도 방송이 밀리지 않습니다.
///
/// 공유 메모리 구성 (int32 = 리틀 엔디언):
///   [머리 4096바이트] 0: 화면 번호(새 그림마다 +1), 1: 칸, 2: 폭, 3: 높이, 4: 소리 쓴 위치(프레임, 계속 늘어남), 5: 소리 고리 크기(프레임)
///   [화면 칸 × Slots] BGRA, 폭×4 간격
///   [소리 고리] 48kHz 스테레오 16비트
/// </summary>
public sealed class MultiStreamer : IDisposable
{
    const string Dll = "RocketRPGCore.dll";
    [DllImport(Dll)] static extern int rpg_cap_open(IntPtr topHwnd, out IntPtr cap);
    [DllImport(Dll)] static extern int rpg_cap_read(IntPtr cap, IntPtr target, int maxW, int maxH, int allowNew, IntPtr dst, uint capBytes, out int w, out int h, out int took);
    [DllImport(Dll)] static extern void rpg_cap_close(IntPtr cap);
    [DllImport(Dll)] static extern int rpg_loopback_open(uint pid, out IntPtr lb);
    [DllImport(Dll)] static extern int rpg_loopback_read(IntPtr lb, IntPtr dst, int maxFrames);
    [DllImport(Dll)] static extern void rpg_loopback_close(IntPtr lb);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attr, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    public const int HeaderBytes = 4096;
    /// <summary>화면 칸 수. 페이지가 한 칸을 읽는 동안 다른 칸에 씁니다.</summary>
    public const int Slots = 3;
    public const int MaxWidth = 1920, MaxHeight = 1088;
    public const int SlotBytes = MaxWidth * MaxHeight * 4;
    public const int AudioFrames = 48000 * 2;   // 2초
    public const int AudioOffset = HeaderBytes + Slots * SlotBytes;
    public const int BufferBytes = AudioOffset + AudioFrames * 4;

    /// <summary>페이지에 알려 줄 구성 (JSON)</summary>
    public static string LayoutJson =>
        $"{{\"header\":{HeaderBytes},\"slots\":{Slots},\"slotBytes\":{SlotBytes},\"audioOffset\":{AudioOffset},\"audioFrames\":{AudioFrames}}}";

    readonly IntPtr _top, _buffer;
    Thread? _thread;
    volatile bool _stop;
    volatile int _fps = 30, _maxHeight = 720;
    IntPtr _target;
    int _audioPid;
    // 게임이 직접 주는 화면 (있으면 창 캡처 대신 씀): Player 공유 메모리 이름 또는 MV/MZ 페이지와 함께 쓰는 메모리 주소
    string? _feedName;
    IntPtr _feedPtr;

    /// <param name="topHwnd">RocketRPG 주 창</param>
    /// <param name="buffer">방송 페이지와 공유하는 메모리 (<see cref="BufferBytes"/> 이상)</param>
    public MultiStreamer(IntPtr topHwnd, IntPtr buffer)
    {
        _top = topHwnd;
        _buffer = buffer;
    }

    /// <summary>
    /// 화면을 어디서 받을지와 소리를 낼 프로세스. 게임이 바뀌면 다시 부릅니다.
    /// 게임이 직접 주는 화면(feedName/feedPtr)이 있으면 그것을 쓰고(채팅·핑이 섞이지 않은 게임 화면), 없으면 게임 창 영역을 잡습니다.
    /// </summary>
    public void SetSource(IntPtr gameHwnd, int audioPid, string? feedName = null, IntPtr feedPtr = default)
    {
        Interlocked.Exchange(ref _target, gameHwnd);
        Interlocked.Exchange(ref _audioPid, audioPid);
        Volatile.Write(ref _feedName, string.IsNullOrEmpty(feedName) ? null : feedName);
        Interlocked.Exchange(ref _feedPtr, feedPtr);
    }

    public void SetQuality(int fps, int maxHeight)
    {
        _fps = Math.Clamp(fps, 10, 60);
        _maxHeight = Math.Clamp(maxHeight, 240, MaxHeight);
    }

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "multi-stream", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void Header(int index, int value) => Marshal.WriteInt32(_buffer, index * 4, value);
    int Header(int index) => Marshal.ReadInt32(_buffer, index * 4);

    void Run()
    {
        IntPtr cap = IntPtr.Zero, lb = IntPtr.Zero, timer = IntPtr.Zero, pcm = IntPtr.Zero;
        int lbPid = 0;
        GameFrameFeed? feed = null;
        try
        {
            bool capTried = false;
            string feedKey = "";
            int feedSeq = 0;
            double feedRetryAt = 0, feedGiveUpAt = 0;
            bool feedAlive = false, useWindow = false;
            timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            const int chunk = 4800;   // 한 번에 옮기는 소리 (최대 100ms)
            pcm = Marshal.AllocHGlobal(chunk * 4);
            Header(5, AudioFrames);
            int slot = (Header(1) + 1) % Slots;
            uint audioPos = (uint)Header(4);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double nextAllowed = 0;
            // 진단: 10초마다 잡은 프레임 수와 한 장 잡는 데 든 시간
            int statTicks = 0, statFrames = 0;
            double statReadMs = 0, statNext = 10_000;
            int lastW = 0, lastH = 0;

            while (!_stop)
            {
                int pid = Volatile.Read(ref _audioPid);
                if (pid != lbPid)
                {
                    if (lb != IntPtr.Zero) { rpg_loopback_close(lb); lb = IntPtr.Zero; }
                    lbPid = pid;
                    if (pid > 0)
                    {
                        if (rpg_loopback_open((uint)pid, out lb) == 0) UiLog.Write($"multi: capturing game sound (pid {pid})");
                        else { lb = IntPtr.Zero; UiLog.Write($"multi: game sound capture unavailable ({CoreInterop.LastError()})"); }
                    }
                }

                // 게임이 직접 주는 화면
                string? feedName = Volatile.Read(ref _feedName);
                IntPtr feedPtr = Volatile.Read(ref _feedPtr);
                string key = feedName ?? (feedPtr != IntPtr.Zero ? feedPtr.ToString() : "");
                if (key != feedKey)
                {
                    feed?.Dispose();
                    feed = null;
                    feedKey = key;
                    feedRetryAt = 0;
                    feedAlive = useWindow = false;
                    feedGiveUpAt = clock.Elapsed.TotalMilliseconds + 5000;
                }
                double now = clock.Elapsed.TotalMilliseconds;
                // 게임이 5초 안에 화면을 한 장도 주지 않으면(예전 Player, 에이전트 오류) 창 캡처로 대신함
                if (key.Length > 0 && !feedAlive && !useWindow && now > feedGiveUpAt)
                {
                    useWindow = true;
                    UiLog.Write("multi: the game sent no frames, falling back to window capture");
                }
                if (feed == null && key.Length > 0 && now >= feedRetryAt)
                {
                    feed = feedName != null ? GameFrameFeed.OpenNamed(feedName) : GameFrameFeed.FromPointer(feedPtr);
                    feedRetryAt = now + 500;   // Player는 첫 화면을 그릴 때 메모리를 만듦
                    if (feed != null) { feedSeq = feed.Seq; UiLog.Write($"multi: game frames from {(feedName != null ? "Player" : "the game")}"); }
                }

                IntPtr target = Volatile.Read(ref _target);
                if (feed != null && !useWindow)
                {
                    feed.RequestFps(_fps);
                    double interval = 1000.0 / _fps;
                    if (feed.Seq != feedSeq && now >= nextAllowed - 4 &&
                        feed.CopyTo(_buffer + HeaderBytes + slot * SlotBytes, SlotBytes, MaxWidth, _maxHeight, out int w, out int h))
                    {
                        feedSeq = feed.Seq;
                        feedAlive = true;
                        nextAllowed = now - nextAllowed > interval ? now + interval : nextAllowed + interval;
                        Header(1, slot);
                        Header(2, w);
                        Header(3, h);
                        Thread.MemoryBarrier();
                        Header(0, Header(0) + 1);
                        slot = (slot + 1) % Slots;
                        statFrames++;
                        statReadMs += clock.Elapsed.TotalMilliseconds - now;
                        lastW = w; lastH = h;
                    }
                }
                else if ((key.Length == 0 || useWindow) && target != IntPtr.Zero && !capTried)
                {
                    // 게임이 화면을 직접 주지 못하면 RocketRPG 창에서 게임 영역을 잡음 (창 위의 채팅·핑도 함께 잡힘)
                    capTried = true;
                    if (rpg_cap_open(_top, out cap) != 0)
                    {
                        cap = IntPtr.Zero;
                        UiLog.Write($"multi: screen capture unavailable ({CoreInterop.LastError()})");
                    }
                    else UiLog.Write("multi: game frames from window capture");
                }
                if ((key.Length == 0 || useWindow) && cap != IntPtr.Zero && target != IntPtr.Zero)
                {
                    int maxH = _maxHeight;
                    int maxW = Math.Min(MaxWidth, maxH * 2);
                    double t0 = clock.Elapsed.TotalMilliseconds;
                    // 초당 프레임 제한: 일정한 간격의 예정 시각에 맞춰 받되 4ms 일찍 온 그림도 받음 (게임 쪽 흔들림 때문에 버리지 않게).
                    // 게임이 그리는 대로 바로 잡으려고 3ms마다 봅니다.
                    double interval = 1000.0 / _fps;
                    int allowNew = t0 >= nextAllowed - 4 ? 1 : 0;
                    int result = rpg_cap_read(cap, target, maxW, maxH, allowNew, _buffer + HeaderBytes + slot * SlotBytes, SlotBytes, out int w, out int h, out int took);
                    if (took != 0) nextAllowed = t0 - nextAllowed > interval ? t0 + interval : nextAllowed + interval;
                    if (result == 0)
                    {
                        Header(1, slot);
                        Header(2, w);
                        Header(3, h);
                        Thread.MemoryBarrier();
                        Header(0, Header(0) + 1);   // 페이지는 이 번호가 바뀌면 새 그림을 읽음
                        slot = (slot + 1) % Slots;
                        statFrames++;
                        statReadMs += clock.Elapsed.TotalMilliseconds - t0;
                        lastW = w; lastH = h;
                    }
                }

                if (lb != IntPtr.Zero)
                {
                    int n;
                    while ((n = rpg_loopback_read(lb, pcm, chunk)) > 0)
                    {
                        // 고리에 이어 쓰기 (끝에 닿으면 처음으로)
                        int at = (int)(audioPos % AudioFrames);
                        int first = Math.Min(n, AudioFrames - at);
                        unsafe
                        {
                            Buffer.MemoryCopy((void*)pcm, (void*)(_buffer + AudioOffset + at * 4), (AudioFrames - at) * 4L, first * 4L);
                            if (n > first)
                                Buffer.MemoryCopy((void*)(pcm + first * 4), (void*)(_buffer + AudioOffset), AudioFrames * 4L, (n - first) * 4L);
                        }
                        audioPos += (uint)n;
                        Thread.MemoryBarrier();
                        Header(4, (int)audioPos);
                        if (n < chunk) break;
                    }
                }

                statTicks++;
                if (clock.Elapsed.TotalMilliseconds >= statNext)
                {
                    statNext = clock.Elapsed.TotalMilliseconds + 10_000;
                    if (statFrames > 0)
                        UiLog.Write($"multi: captured {statFrames / 10.0:0.#} fps (limit {_fps}), {statReadMs / statFrames:0.0} ms per frame ({lastW}x{lastH})");
                    statTicks = statFrames = 0;
                    statReadMs = 0;
                }

                // 3ms 뒤 다시 (고해상도 타이머)
                if (timer != IntPtr.Zero)
                {
                    long rel = -30_000;
                    SetWaitableTimer(timer, ref rel, 0, IntPtr.Zero, IntPtr.Zero, false);
                    WaitForSingleObject(timer, 1000);
                }
                else Thread.Sleep(1);
            }
        }
        catch (Exception ex) { UiLog.Write($"multi: stream thread failed {ex.Message}"); }
        finally
        {
            feed?.Dispose();
            if (lb != IntPtr.Zero) rpg_loopback_close(lb);
            if (cap != IntPtr.Zero) rpg_cap_close(cap);
            if (timer != IntPtr.Zero) CloseHandle(timer);
            if (pcm != IntPtr.Zero) Marshal.FreeHGlobal(pcm);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
    }
}

/// <summary>
/// 게임이 직접 주는 화면 (EasyRPG Player 수정본의 공유 메모리 / MV·MZ 페이지와 함께 쓰는 메모리).
/// 구성 (int32): 0 'RRFR', 1 번호, 2 칸(0/1), 3 폭, 4 높이, 5 형식(0 RGBA, 1 BGRA), 6 칸 크기, 7 원하는 초당 장 수(0 = 멈춤, RocketRPG가 씀)
/// </summary>
sealed class GameFrameFeed : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint high, uint low, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    const uint FILE_MAP_ALL_ACCESS = 0xF001F;
    const int Magic = 0x52524652, HeaderBytes = 4096;

    readonly IntPtr _map, _base;
    readonly bool _owned;

    GameFrameFeed(IntPtr map, IntPtr view, bool owned) { _map = map; _base = view; _owned = owned; }

    public static GameFrameFeed? OpenNamed(string name)
    {
        IntPtr map = OpenFileMappingW(FILE_MAP_ALL_ACCESS, false, name);
        if (map == IntPtr.Zero) return null;
        IntPtr view = MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
        if (view == IntPtr.Zero || Marshal.ReadInt32(view) != Magic)
        {
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            CloseHandle(map);
            return null;
        }
        return new GameFrameFeed(map, view, true);
    }

    public static GameFrameFeed? FromPointer(IntPtr p) => p == IntPtr.Zero ? null : new GameFrameFeed(IntPtr.Zero, p, false);

    int H(int i) => Marshal.ReadInt32(_base, i * 4);

    public int Seq => H(1);

    public void RequestFps(int fps) { if (H(7) != fps) Marshal.WriteInt32(_base, 7 * 4, fps); }

    /// <summary>
    /// 최근 화면을 dst에 BGRX로 씀. 작은 게임 화면(도트)은 선명하도록 정수 배로 키움 (최대 maxH 높이).
    /// </summary>
    public unsafe bool CopyTo(IntPtr dst, int capBytes, int maxW, int maxH, out int ow, out int oh)
    {
        ow = oh = 0;
        if (H(0) != Magic) return false;
        int slot = H(2), w = H(3), h = H(4), fmt = H(5), slotBytes = H(6);
        if (w <= 0 || h <= 0 || slotBytes <= 0 || (long)w * h * 4 > slotBytes || slot is < 0 or > 1) return false;
        int k = Math.Max(1, Math.Min(maxH / h, maxW / w));
        // 큰 화면(MV/MZ)은 정수 배로 줄일 수 없으니 그대로 (페이지 쪽 부호화가 필요하면 줄임)
        ow = w * k & ~1;
        oh = h * k & ~1;
        if ((long)ow * oh * 4 > capBytes) return false;
        uint* src = (uint*)(_base + HeaderBytes + (long)slot * slotBytes);
        uint* d = (uint*)dst;
        bool rgba = fmt == 0;
        if (!rgba && k == 1 && ow == w && oh == h)
        {
            Buffer.MemoryCopy(src, d, capBytes, (long)w * h * 4);   // 이미 BGR 순서, 알파는 방송 페이지가 무시함 (BGRX)
            return true;
        }
        for (int y = 0; y < oh / k; y++)
        {
            uint* s = src + (long)y * w;
            uint* row = d + (long)y * k * ow;
            int x = 0;
            for (int sx = 0; sx < w && x < ow; sx++)
            {
                uint p = s[sx];
                uint v = rgba ? ((p & 0xFFu) << 16) | (p & 0xFF00u) | ((p >> 16) & 0xFFu) | 0xFF000000u : p | 0xFF000000u;
                for (int i = 0; i < k && x < ow; i++) row[x++] = v;
            }
            for (int r = 1; r < k; r++) Buffer.MemoryCopy(row, row + (long)r * ow, (long)ow * 4, (long)ow * 4);
        }
        return true;
    }

    public void Dispose()
    {
        try { Marshal.WriteInt32(_base, 7 * 4, 0); } catch { }   // 이제 안 읽음
        if (_owned)
        {
            UnmapViewOfFile(_base);
            CloseHandle(_map);
        }
    }
}
