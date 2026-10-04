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

        string app = Path.Combine(Path.GetTempPath(), "rr_delta_test");
        Assert("Entry paths stay inside the app folder", DeltaUpdate.LocalPath(app, "../evil.dll") == null && DeltaUpdate.LocalPath(app, "C:/x.dll") == null &&
            DeltaUpdate.LocalPath(app, "dir/") == null && DeltaUpdate.LocalPath(app, "runtimes/a.dll") == Path.Combine(app, "runtimes", "a.dll"));
    }
}
