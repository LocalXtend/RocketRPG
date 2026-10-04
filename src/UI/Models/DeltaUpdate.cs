#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RocketRPG.Models;

/// <summary>
/// 빠른 업데이트: 새 버전의 포터블 zip 전체(130MB 넘음)를 받지 않고, zip 목록(맨 끝 수십 KB)만 받아
/// 내 폴더의 파일과 크기·CRC32를 비교한 뒤 바뀐 파일만 HTTP 구간 요청(Range)으로 받아 임시 폴더에 풀어 둡니다.
/// 서버가 구간 요청을 받지 않으면 zip 전체를 받아 같은 방식으로 바뀐 파일만 풉니다.
/// 실제 교체는 RocketRPG가 꺼진 뒤 UpdateService.ApplyStagedUpdate가 합니다.
/// </summary>
public static class DeltaUpdate
{
    public sealed record ZipEntry(string Name, uint Crc, long CompressedSize, long Size, int Method, long Offset);

    /// <summary>받을 파일 목록과 크기</summary>
    public sealed record Plan(IReadOnlyList<ZipEntry> All, IReadOnlyList<ZipEntry> Changed, long CentralDirectoryOffset, long DownloadBytes);

    /// <summary>진행 상황: 글, 받은 바이트, 전체 바이트 (전체 0 = 모름)</summary>
    public delegate void Progress(string status, long done, long total);

    // ── zip 목록 읽기 ──

    /// <summary>zip 맨 끝 바이트에서 목록 위치를 찾음 (EOCD). 못 찾거나 zip64면 null</summary>
    public static (long cdOffset, long cdSize, int count)? FindCentralDirectory(ReadOnlySpan<byte> tail)
    {
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail[i..]) != 0x06054b50) continue;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(tail[(i + 10)..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(tail[(i + 12)..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(tail[(i + 16)..]);
            if (count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF) return null;   // zip64 (4GB 넘음): 쓰지 않음
            return (offset, size, count);
        }
        return null;
    }

    /// <summary>zip 목록(central directory)을 항목들로</summary>
    public static List<ZipEntry> ParseCentralDirectory(ReadOnlySpan<byte> cd)
    {
        var list = new List<ZipEntry>();
        int p = 0;
        while (p + 46 <= cd.Length && BinaryPrimitives.ReadUInt32LittleEndian(cd[p..]) == 0x02014b50)
        {
            int flags = BinaryPrimitives.ReadUInt16LittleEndian(cd[(p + 8)..]);
            int method = BinaryPrimitives.ReadUInt16LittleEndian(cd[(p + 10)..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(cd[(p + 16)..]);
            uint comp = BinaryPrimitives.ReadUInt32LittleEndian(cd[(p + 20)..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(cd[(p + 24)..]);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(cd[(p + 28)..]);
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(cd[(p + 30)..]);
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(cd[(p + 32)..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(cd[(p + 42)..]);
            if (p + 46 + nameLen > cd.Length) break;
            var enc = (flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.Latin1;
            string name = enc.GetString(cd.Slice(p + 46, nameLen));
            list.Add(new ZipEntry(name, crc, comp, size, method, offset));
            p += 46 + nameLen + extraLen + commentLen;
        }
        return list;
    }

    /// <summary>zip 안 이름 → 앱 폴더 안 경로. 폴더 밖을 가리키거나 폴더 항목이면 null</summary>
    public static string? LocalPath(string appDir, string entryName)
    {
        if (entryName.Length == 0 || entryName.EndsWith('/') || entryName.EndsWith('\\')) return null;
        string rel = entryName.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(rel) || rel.Split(Path.DirectorySeparatorChar).Any(s => s == ".." || s.Length == 0)) return null;
        string root = Path.GetFullPath(appDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, rel));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>내 폴더에 이미 같은 파일(크기·CRC32)이 있는지</summary>
    public static bool IsSame(string path, ZipEntry e)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != e.Size) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
            return Crc32.Of(fs) == e.Crc;
        }
        catch { return false; }
    }

    /// <summary>바뀐 항목들 (항목 시작 위치 순서). 각 항목은 다음 항목 시작(또는 목록 시작) 전까지가 그 항목의 몫</summary>
    public static Plan MakePlan(IReadOnlyList<ZipEntry> all, long cdOffset, Func<ZipEntry, bool> isSame)
    {
        var changed = all.Where(e => e.Name.Length > 0 && !e.Name.EndsWith('/') && !isSame(e)).OrderBy(e => e.Offset).ToList();
        long bytes = Ranges(all, changed, cdOffset).Sum(r => r.end - r.start);
        return new Plan(all, changed, cdOffset, bytes);
    }

    /// <summary>받을 구간: 바뀐 항목의 [시작, 다음 항목 시작). 가까운 구간(256KB 이내)은 한 번에 받음</summary>
    public static List<(long start, long end, List<ZipEntry> entries)> Ranges(IReadOnlyList<ZipEntry> all, IReadOnlyList<ZipEntry> changed, long cdOffset)
    {
        var starts = all.Select(e => e.Offset).Append(cdOffset).Distinct().OrderBy(x => x).ToList();
        long EndOf(ZipEntry e)
        {
            int i = starts.BinarySearch(e.Offset);
            return i >= 0 && i + 1 < starts.Count ? starts[i + 1] : cdOffset;
        }
        var ranges = new List<(long start, long end, List<ZipEntry> entries)>();
        foreach (var e in changed.OrderBy(e => e.Offset))
        {
            long s = e.Offset, end = EndOf(e);
            if (ranges.Count > 0 && s - ranges[^1].end <= 256 * 1024)
            {
                var last = ranges[^1];
                last.entries.Add(e);
                ranges[^1] = (last.start, Math.Max(last.end, end), last.entries);
            }
            else ranges.Add((s, end, new List<ZipEntry> { e }));
        }
        return ranges;
    }

    // ── 받기 ──

    static HttpClient NewClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RocketRPG");
        return http;
    }

    /// <summary>구간 요청. 서버가 구간으로 답하지 않으면(200) null</summary>
    static async Task<HttpResponseMessage?> GetRange(HttpClient http, string url, long start, long endExclusive, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(start, endExclusive - 1);
        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.From == start) return resp;
        resp.Dispose();
        return null;
    }

    /// <summary>
    /// 새 버전 zip(url, 크기 zipSize)에서 appDir와 다른 파일만 staging 폴더에 풀어 둡니다.
    /// 반환: 받은 파일 수 (0 = 이미 같음). 구간 요청이 안 되면 zip 전체를 받아 같은 일을 합니다.
    /// </summary>
    public static async Task<int> StageAsync(string url, long zipSize, string appDir, string staging, Progress report, CancellationToken ct)
    {
        using var http = NewClient();
        report("바뀐 파일을 확인하는 중...", 0, 0);
        int tailLen = (int)Math.Min(zipSize, 128 * 1024);
        byte[]? tail = null;
        if (zipSize > 0)
        {
            using var resp = await GetRange(http, url, zipSize - tailLen, zipSize, ct);
            if (resp != null) tail = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        var dir = tail != null ? FindCentralDirectory(tail) : null;
        if (tail == null || dir == null)
        {
            UiLog.Write("update: range requests unavailable, downloading the whole package");
            return await StageFromFullDownloadAsync(http, url, zipSize, appDir, staging, report, ct);
        }
        var (cdOffset, cdSize, _) = dir.Value;
        byte[] cd;
        long tailStart = zipSize - tail.Length;
        if (cdOffset >= tailStart) cd = tail.AsSpan((int)(cdOffset - tailStart), (int)cdSize).ToArray();
        else
        {
            using var resp = await GetRange(http, url, cdOffset, cdOffset + cdSize, ct) ?? throw new IOException("zip 목록을 받지 못했습니다.");
            cd = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        var all = ParseCentralDirectory(cd);
        if (all.Count == 0) throw new IOException("zip 목록이 비어 있습니다.");

        var plan = await Task.Run(() => MakePlan(all, cdOffset, e => LocalPath(appDir, e.Name) is { } p && IsSame(p, e)), ct);
        UiLog.Write($"update: {plan.Changed.Count}/{all.Count} files changed, {plan.DownloadBytes / 1048576.0:0.0} MB of {zipSize / 1048576.0:0.0} MB");
        if (plan.Changed.Count == 0) return 0;

        long done = 0;
        foreach (var (start, end, entries) in Ranges(all, plan.Changed, cdOffset))
        {
            using var resp = await GetRange(http, url, start, end, ct) ?? throw new IOException("업데이트 파일 일부를 받지 못했습니다.");
            await using var net = await resp.Content.ReadAsStreamAsync(ct);
            var counted = new CountingStream(net, n => { done += n; report($"바뀐 파일 받는 중 ({plan.Changed.Count}개)", done, plan.DownloadBytes); });
            long pos = start;
            foreach (var e in entries)
            {
                await Skip(counted, e.Offset - pos, ct);
                pos = e.Offset;
                pos += await ExtractLocalEntry(counted, e, appDir, staging, ct);
            }
            await Skip(counted, end - pos, ct);
        }
        return plan.Changed.Count;
    }

    /// <summary>구간 요청이 안 될 때: zip 전체를 받아 바뀐 파일만 풂</summary>
    static async Task<int> StageFromFullDownloadAsync(HttpClient http, string url, long zipSize, string appDir, string staging, Progress report, CancellationToken ct)
    {
        string zipPath = Path.Combine(Path.GetTempPath(), $"RocketRPG_update_{Guid.NewGuid():N}.zip");
        try
        {
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? zipSize;
                await using var net = await resp.Content.ReadAsStreamAsync(ct);
                await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
                long done = 0;
                byte[] buf = new byte[1 << 20];
                int n;
                while ((n = await net.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    report("업데이트 받는 중", done, total);
                }
            }
            report("바뀐 파일을 푸는 중...", 0, 0);
            return await Task.Run(() =>
            {
                int count = 0;
                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (LocalPath(appDir, entry.FullName) is not { } local) continue;
                    if (IsSame(local, new ZipEntry(entry.FullName, entry.Crc32, entry.CompressedLength, entry.Length, 0, 0))) continue;
                    string dest = LocalPath(staging, entry.FullName)!;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                    count++;
                }
                return count;
            }, ct);
        }
        finally { try { File.Delete(zipPath); } catch { } }
    }

    /// <summary>스트림의 지금 위치에 있는 zip 항목(로컬 헤더 + 데이터)을 풀어 staging에 씀. 읽은 바이트 수를 돌려줌</summary>
    static async Task<long> ExtractLocalEntry(Stream s, ZipEntry e, string appDir, string staging, CancellationToken ct)
    {
        byte[] head = new byte[30];
        await s.ReadExactlyAsync(head, ct);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x04034b50) throw new InvalidDataException($"zip 항목이 깨졌습니다: {e.Name}");
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(26));
        int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(28));
        await Skip(s, nameLen + extraLen, ct);
        if (LocalPath(appDir, e.Name) == null) { await Skip(s, e.CompressedSize, ct); return 30 + nameLen + extraLen + e.CompressedSize; }

        string dest = LocalPath(staging, e.Name) ?? throw new InvalidDataException(e.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var limited = new LimitedStream(s, e.CompressedSize);
        uint crc;
        await using (var outFile = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
        {
            var crcOut = new Crc32Stream(outFile);
            if (e.Method == 8)
            {
                await using var inflate = new DeflateStream(limited, CompressionMode.Decompress, leaveOpen: true);
                await inflate.CopyToAsync(crcOut, 1 << 20, ct);
            }
            else if (e.Method == 0) await limited.CopyToAsync(crcOut, 1 << 20, ct);
            else throw new InvalidDataException($"지원하지 않는 압축 방식({e.Method}): {e.Name}");
            await Skip(limited, limited.Remaining, ct);
            crc = crcOut.Crc;
            if (crcOut.Length != e.Size) throw new InvalidDataException($"크기가 맞지 않습니다: {e.Name}");
        }
        if (crc != e.Crc) throw new InvalidDataException($"받은 파일이 깨졌습니다: {e.Name}");
        return 30 + nameLen + extraLen + e.CompressedSize;
    }

    static async Task Skip(Stream s, long count, CancellationToken ct)
    {
        if (count <= 0) return;
        byte[] buf = new byte[Math.Min(count, 1 << 16)];
        while (count > 0)
        {
            int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count)), ct);
            if (n <= 0) throw new EndOfStreamException();
            count -= n;
        }
    }

    sealed class CountingStream(Stream inner, Action<int> onRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { int n = inner.Read(buffer, offset, count); if (n > 0) onRead(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { int n = await inner.ReadAsync(buffer, ct); if (n > 0) onRead(n); return n; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class LimitedStream(Stream inner, long length) : Stream
    {
        public long Remaining { get; private set; } = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (Remaining <= 0) return 0;
            int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
            Remaining -= n;
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Remaining <= 0) return 0;
            int n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, Remaining)], ct);
            Remaining -= n;
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>쓰면서 CRC32를 계산</summary>
    sealed class Crc32Stream(Stream inner) : Stream
    {
        uint _crc = 0xFFFFFFFF;
        public uint Crc => ~_crc;
        long _len;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _len;
        public override long Position { get => _len; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { _crc = Crc32.Update(_crc, buffer); _len += buffer.Length; inner.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            _crc = Crc32.Update(_crc, buffer.Span);
            _len += buffer.Length;
            await inner.WriteAsync(buffer, ct);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }
}

/// <summary>zip이 쓰는 CRC-32 (IEEE). 8바이트씩 계산해 수백 MB도 금방 끝납니다.</summary>
public static class Crc32
{
    static readonly uint[][] T = Build();

    static uint[][] Build()
    {
        var t = new uint[8][];
        for (int k = 0; k < 8; k++) t[k] = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[0][i] = c;
        }
        for (int i = 0; i < 256; i++)
            for (int k = 1; k < 8; k++) t[k][i] = (t[k - 1][i] >> 8) ^ t[0][t[k - 1][i] & 0xFF];
        return t;
    }

    /// <summary>진행 중인 값(처음 0xFFFFFFFF)에 이어서 계산. 최종 값은 ~결과</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var t0 = T[0]; var t1 = T[1]; var t2 = T[2]; var t3 = T[3]; var t4 = T[4]; var t5 = T[5]; var t6 = T[6]; var t7 = T[7];
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) ^ crc;
            uint b = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            crc = t7[a & 0xFF] ^ t6[(a >> 8) & 0xFF] ^ t5[(a >> 16) & 0xFF] ^ t4[a >> 24] ^
                  t3[b & 0xFF] ^ t2[(b >> 8) & 0xFF] ^ t1[(b >> 16) & 0xFF] ^ t0[b >> 24];
        }
        for (; i < data.Length; i++) crc = t0[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Of(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFF, data);

    public static uint Of(Stream s)
    {
        uint crc = 0xFFFFFFFF;
        byte[] buf = new byte[1 << 20];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) crc = Update(crc, buf.AsSpan(0, n));
        return ~crc;
    }
}
