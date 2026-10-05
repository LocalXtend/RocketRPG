using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// 1.1.0 빠른 업데이트: zip 목록 읽기, 바뀐 파일 고르기, 받을 구간
public partial class Program
{
    /// <summary>scripts\zip64_end.ps1과 같은 변환: 목록 뒤에 zip64 끝 기록·위치 기록, 원래 끝 기록은 최댓값</summary>
    static byte[] ToZip64End(byte[] zip)
    {
        int at = zip.Length - 22;
        ushort count = BitConverter.ToUInt16(zip, at + 10);
        uint size = BitConverter.ToUInt32(zip, at + 12), offset = BitConverter.ToUInt32(zip, at + 16);
        var ms = new MemoryStream();
        ms.Write(zip, 0, at);
        var w = new BinaryWriter(ms);
        w.Write(0x06064b50u); w.Write(44UL); w.Write((ushort)45); w.Write((ushort)45); w.Write(0u); w.Write(0u);
        w.Write((ulong)count); w.Write((ulong)count); w.Write((ulong)size); w.Write((ulong)offset);
        w.Write(0x07064b50u); w.Write(0u); w.Write((ulong)at); w.Write(1u);
        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF); w.Write(uint.MaxValue); w.Write(uint.MaxValue); w.Write((ushort)0);
        return ms.ToArray();
    }

    private static void TestDeltaUpdate()
    {
        Console.WriteLine("--- Testing DeltaUpdate (changed files only) ---");
        Assert("CRC32 of '123456789' is CBF43926", Crc32.Of(Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926u);
        var big = new byte[100_003];
        new Random(7).NextBytes(big);
        Assert("CRC32 by stream equals CRC32 by span", Crc32.Of(new MemoryStream(big)) == Crc32.Of(big));

        // zip 하나 만들어 목록 읽기
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("a.txt", "same"), ("sub/b.txt", "new content"), ("c.bin", new string('x', 5000)), ("dir/", "") })
            {
                var e = zip.CreateEntry(name);
                if (!name.EndsWith("/")) using (var w = new StreamWriter(e.Open())) w.Write(text);
            }
        }
        byte[] bytes = ms.ToArray();
        var dir = DeltaUpdate.FindCentralDirectory(bytes);
        Assert("Central directory found from the zip tail", dir != null);
        var (cdOffset, cdSize, count) = dir!.Value;
        var entries = DeltaUpdate.ParseCentralDirectory(bytes.AsSpan((int)cdOffset, (int)cdSize));
        Assert("All zip entries listed", entries.Count == 4 && count == 4, string.Join(",", entries.Select(e => e.Name)));
        Assert("Entry CRC matches the content", entries.First(e => e.Name == "a.txt").Crc == Crc32.Of(Encoding.UTF8.GetBytes("same")));

        var plan = DeltaUpdate.MakePlan(entries, cdOffset, e => e.Name == "a.txt");
        Assert("Unchanged files and folders are skipped", plan.Changed.Select(e => e.Name).OrderBy(n => n).SequenceEqual(new[] { "c.bin", "sub/b.txt" }),
            string.Join(",", plan.Changed.Select(e => e.Name)));
        var ranges = DeltaUpdate.Ranges(entries, plan.Changed, cdOffset);
        Assert("Nearby changed files are fetched in one request", ranges.Count == 1 && ranges[0].entries.Count == 2);
        Assert("Ranges end before the central directory", ranges.All(r => r.end <= cdOffset));
        Assert("Download size is far smaller than the zip when one file changes",
            DeltaUpdate.MakePlan(entries, cdOffset, e => e.Name != "a.txt").DownloadBytes < bytes.Length / 2);

        // zip64 끝 기록 (1.1.3 포터블 zip): 옛 읽기는 못 찾고(→ 1.1.0~1.1.2는 전체 받기로), 새 읽기는 같은 목록을 찾음
        byte[] z64 = ToZip64End(bytes);
        Assert("Classic reader rejects zip64 end records (old clients fall back to full download)", DeltaUpdate.FindCentralDirectory(z64) == null);
        var any = DeltaUpdate.FindCentralDirectoryAny(z64, 0);
        Assert("zip64 end records are read", any is { } a64 && a64.cdOffset == cdOffset && a64.cdSize == cdSize && a64.count == count, any?.ToString() ?? "null");
        var tail = z64.AsSpan(z64.Length - 100).ToArray();
        Assert("zip64 end records are read from a tail slice", DeltaUpdate.FindCentralDirectoryAny(tail, z64.Length - 100) is { } t64 && t64.cdOffset == cdOffset);
        Assert("Classic zips still read the same", DeltaUpdate.FindCentralDirectoryAny(bytes, 0) is { } c64 && c64.cdOffset == cdOffset && c64.count == count);
        using (var zr = new ZipArchive(new MemoryStream(z64), ZipArchiveMode.Read))
            Assert(".NET still opens the zip64-end zip", zr.Entries.Count == 4 && new StreamReader(zr.GetEntry("sub/b.txt")!.Open()).ReadToEnd() == "new content");

        string app = Path.Combine(Path.GetTempPath(), "rr_delta_test");
        Assert("Entry paths stay inside the app folder", DeltaUpdate.LocalPath(app, "../evil.dll") == null && DeltaUpdate.LocalPath(app, "C:/x.dll") == null &&
            DeltaUpdate.LocalPath(app, "dir/") == null && DeltaUpdate.LocalPath(app, "runtimes/a.dll") == Path.Combine(app, "runtimes", "a.dll"));
    }
}
