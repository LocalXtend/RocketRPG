#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RocketRPG.Models;

/// <summary>
/// Enigma Virtual Box 로 묶인 실행 파일(MV/MZ의 Game_boxed.exe 등)에서 가상 파일을 꺼냅니다.
/// 이런 게임은 이미지/데이터가 exe 안에만 있어 WebView2에서 열 수 없으므로, 여기서 한 번 풀어
/// 캐시에 두고 WebView2로 실행합니다.
/// 구조는 evbunpack(mos9527, Apache-2.0)을 참고했습니다: "EVB\0" 헤더 뒤 노드 목록(UTF-16 이름, 폴더/파일),
/// 파일 데이터는 원본 그대로이거나 aPLib 청크 압축.
/// </summary>
public sealed class EvbArchive
{
    public sealed record Entry(string Path, long Offset, long OriginalSize, long StoredSize);

    const int NodeMain = 0, NodeFile = 2, NodeFolder = 3;
    static readonly byte[] Magic = "EVB\0"u8.ToArray();
    static readonly byte[] DefaultFolder = Encoding.Unicode.GetBytes("%DEFAULT FOLDER%");

    public string File { get; }
    public List<Entry> Entries { get; } = new();

    EvbArchive(string file) { File = file; }

    /// <summary>EVB 파일 시스템이 있으면 목록을 읽어 돌려줍니다. 없거나 읽을 수 없으면 null.</summary>
    public static EvbArchive? TryOpen(string exe)
    {
        try
        {
            using var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            foreach (long pos in FindMagic(fs))
            {
                foreach (bool legacy in new[] { true, false })
                {
                    var a = new EvbArchive(exe);
                    fs.Position = pos;
                    if (a.ReadTree(fs, legacy) && a.Entries.Count > 0) return a;
                }
            }
        }
        catch (Exception ex) { UiLog.Write($"EvbArchive: {ex.Message}"); }
        return null;
    }

    // "EVB\0" 은 exe 코드/데이터 안에도 우연히 있을 수 있어, 바로 뒤에 "%DEFAULT FOLDER%" 가 오는 위치만 씁니다.
    static IEnumerable<long> FindMagic(FileStream fs)
    {
        const int block = 16 << 20;
        var buf = new byte[block + 512];
        long basePos = 0;
        var found = new List<long>();
        while (true)
        {
            fs.Position = basePos;
            int n = fs.Read(buf, 0, buf.Length);
            if (n <= Magic.Length) break;
            var span = buf.AsSpan(0, n);
            int start = 0;
            while (true)
            {
                int i = span[start..].IndexOf(Magic);
                if (i < 0) break;
                i += start;
                int windowEnd = Math.Min(n, i + 400);
                if (span[i..windowEnd].IndexOf(DefaultFolder) >= 0) found.Add(basePos + i);
                start = i + 1;
            }
            if (found.Count > 0 || n < buf.Length) break;
            basePos += block;
        }
        return found;
    }

    static int U32(ReadOnlySpan<byte> b, int o) => BitConverter.ToInt32(b.Slice(o, 4));

    static string ReadName(BinaryReader r)
    {
        var sb = new List<byte>();
        while (true)
        {
            byte a = r.ReadByte(), b = r.ReadByte();
            if (a == 0 && b == 0) break;
            sb.Add(a); sb.Add(b);
        }
        return Encoding.Unicode.GetString(sb.ToArray());
    }

    bool ReadTree(FileStream fs, bool legacy)
    {
        Entries.Clear();
        var r = new BinaryReader(fs, Encoding.Unicode, leaveOpen: true);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) return false;
        r.ReadBytes(60);
        var raw = new List<(int type, string name, int count, long offset, long orig, long stored)>();
        int maxObjects = 0, objects = 0;
        long absOffset = 0;
        if (!legacy)
        {
            // pe_external_tree: 메인 노드 → 파일 데이터가 목록 뒤에 연속으로 놓임
            var main = r.ReadBytes(16);
            int mainSize = U32(main, 0), mainCount = U32(main, 12);
            absOffset = fs.Position + mainSize - 12;
            fs.Position -= 1;
            raw.Add((NodeMain, "", mainCount, 0, 0, 0));
        }
        while (fs.Position < fs.Length)
        {
            long origin = fs.Position;
            var hdr = r.ReadBytes(16);
            if (hdr.Length < 16) break;
            int size = U32(hdr, 0), count = U32(hdr, 12);
            string name = ReadName(r);
            int type = r.ReadByte();
            if (type == NodeFile)
            {
                long orig, stored, offset;
                if (legacy)
                {
                    // 파일 정보는 노드 끝에 있고, 데이터가 바로 뒤에 붙어 있음
                    fs.Position = origin + size + 4 - 49;
                    var o = r.ReadBytes(49);
                    orig = (uint)U32(o, 2); stored = (uint)U32(o, 41);
                    offset = fs.Position;
                    fs.Position += stored;
                }
                else
                {
                    var o = r.ReadBytes(53);
                    orig = (uint)U32(o, 2); stored = (uint)U32(o, 49);
                    offset = absOffset;
                    absOffset += stored;
                }
                raw.Add((type, name, 0, offset, orig, stored));
                objects++;
            }
            else if (type == NodeFolder)
            {
                if (legacy) fs.Position = origin + size + 4; else fs.Position += 25;
                maxObjects += count;
                raw.Add((type, name, count, 0, 0, 0));
                objects++;
            }
            else if (type == NodeMain && legacy)
            {
                fs.Position = origin + size + 4;
                raw.Add((type, name, count, 0, 0, 0));
            }
            else break;
            if (maxObjects > 0 && objects > maxObjects) break;
        }
        if (raw.Count == 0 || raw[0].type != NodeMain) return false;

        // 트리 순서(깊이 우선)로 경로를 만듭니다.
        int idx = 1;
        bool Walk(string prefix, int count)
        {
            for (int k = 0; k < count; k++)
            {
                if (idx >= raw.Count) return false;
                var n = raw[idx++];
                string name = n.name == "%DEFAULT FOLDER%" ? "" : n.name;
                if (name.IndexOfAny(['\\', '/', ':']) >= 0 || name == ".." || name == ".") return false;
                string path = string.IsNullOrEmpty(name) ? prefix : (prefix.Length == 0 ? name : prefix + "/" + name);
                if (n.type == NodeFile) Entries.Add(new Entry(path, n.offset, n.orig, n.stored));
                else if (n.type == NodeFolder && !Walk(path, n.count)) return false;
            }
            return true;
        }
        return Walk("", raw[0].count) || Entries.Count > 0;
    }

    /// <summary>파일 하나를 꺼냅니다 (압축돼 있으면 aPLib 청크를 풉니다).</summary>
    public void Extract(Entry e, string outPath, FileStream src)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        src.Position = e.Offset;
        using var dst = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        if (e.OriginalSize == e.StoredSize)
        {
            CopyBytes(src, dst, e.StoredSize);
            return;
        }
        var r = new BinaryReader(src, Encoding.UTF8, leaveOpen: true);
        int blkSize = r.ReadInt32();
        r.ReadInt32();
        var table = r.ReadBytes(blkSize - 8);
        var sizes = new List<int>();
        for (int i = 0; i + 4 <= table.Length; i += 12) sizes.Add(BitConverter.ToInt32(table, i));
        long remaining = e.StoredSize - blkSize;
        foreach (int cs in sizes)
        {
            if (remaining <= 0) break;
            int n = (int)Math.Min(cs, remaining);
            var chunk = r.ReadBytes(n);
            remaining -= n;
            var outBytes = APLib.Decompress(chunk);
            dst.Write(outBytes, 0, outBytes.Length);
        }
    }

    static void CopyBytes(Stream src, Stream dst, long count)
    {
        var buf = new byte[1 << 16];
        while (count > 0)
        {
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            dst.Write(buf, 0, n);
            count -= n;
        }
    }

    /// <summary>
    /// MV/MZ 게임 데이터(data/System.json)가 폴더에 없고 exe 안에 묶여 있으면 그 exe 경로를 돌려줍니다.
    /// 실행 파일 이름은 게임마다 달라서(Game_boxed.exe, 게임 이름.exe …) 이름이 아니라 내용으로 판단합니다.
    /// </summary>
    public static string? FindBoxedExe(string gameDir)
    {
        try
        {
            if (System.IO.File.Exists(Path.Combine(gameDir, "www", "data", "System.json")) ||
                System.IO.File.Exists(Path.Combine(gameDir, "data", "System.json")))
                return null;
            foreach (var exe in new DirectoryInfo(gameDir).GetFiles("*.exe").OrderByDescending(f => f.Length))
            {
                if (exe.Length < 4 << 20) continue;   // 게임 파일이 들어 있을 만큼 큰 exe만
                if (TryOpen(exe.FullName) is { Entries.Count: > 0 }) return exe.FullName;
            }
        }
        catch (Exception ex) { UiLog.Write($"EvbArchive: boxed exe check failed: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// 게임 폴더의 묶인 exe를 %LOCALAPPDATA%\RocketRPG\unboxed\&lt;게임키&gt; 에 풉니다 (exe가 바뀌지 않았으면 재사용).
    /// 게임 폴더에만 있는 파일은 그대로 두고 WebView가 두 곳을 함께 보도록 경로를 돌려줍니다. 실행 파일/DLL은 풀지 않습니다.
    /// </summary>
    public static string? EnsureUnboxed(string gameDir, string exe, Action<string>? progress = null)
    {
        var fi = new FileInfo(exe);
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RocketRPG", "unboxed", GameSettingsService.ComputeSafeKey(gameDir));
        string marker = Path.Combine(dir, ".rocketrpg_unboxed");
        string stamp = $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        if (System.IO.File.Exists(marker) && System.IO.File.ReadAllText(marker) == stamp) return dir;

        var a = TryOpen(exe);
        if (a == null) return null;
        UiLog.Write($"EvbArchive: unboxing {a.Entries.Count} files from {exe}");
        using var src = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        int done = 0;
        foreach (var e in a.Entries)
        {
            string ext = Path.GetExtension(e.Path).ToLowerInvariant();
            if (ext is ".exe" or ".dll" or ".pak" or ".dat" && !e.Path.Contains('/')) { done++; continue; }
            string outPath = Path.Combine(dir, e.Path.Replace('/', '\\'));
            if (!Path.GetFullPath(outPath).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;
            a.Extract(e, outPath, src);
            if (++done % 100 == 0) progress?.Invoke($"게임 파일 준비 중... {done}/{a.Entries.Count}");
        }
        // 묶이지 않고 게임 폴더에만 있는 파일(나중에 추가된 파일 등)도 함께 보이도록 복사합니다. 세이브는 게임 폴더에 그대로.
        foreach (var f in Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(gameDir, f);
            string first = rel.Split('\\')[0];
            if (first.Equals("save", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith("www\\save\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (!rel.Contains('\\') && Path.GetExtension(rel).ToLowerInvariant() is ".exe" or ".dll" or ".pak" or ".dat") continue;
            string dst = Path.Combine(dir, rel);
            if (System.IO.File.Exists(dst)) continue;
            try { Directory.CreateDirectory(Path.GetDirectoryName(dst)!); System.IO.File.Copy(f, dst); } catch { }
        }
        System.IO.File.WriteAllText(marker, stamp);
        return dir;
    }
}

/// <summary>aPLib 압축 해제 (Jørgen Ibsen의 aPLib 형식, depack.c 알고리즘)</summary>
public static class APLib
{
    public static byte[] Decompress(byte[] src)
    {
        int s = 0;
        // "AP32" 헤더가 있으면 건너뜀
        if (src.Length >= 24 && src[0] == 'A' && src[1] == 'P' && src[2] == '3' && src[3] == '2')
            s = BitConverter.ToInt32(src, 4);
        var dst = new List<byte>(src.Length * 2);
        int tag = 0, bitCount = 0;
        int GetBit()
        {
            if (bitCount-- == 0) { tag = src[s++]; bitCount = 7; }
            int bit = (tag >> 7) & 1;
            tag = (tag << 1) & 0xFF;
            return bit;
        }
        int GetGamma()
        {
            int result = 1;
            do { result = (result << 1) + GetBit(); } while (GetBit() != 0);
            return result;
        }
        void Copy(int offs, int len)
        {
            for (int i = 0; i < len; i++) dst.Add(dst[dst.Count - offs]);
        }
        dst.Add(src[s++]);
        int r0 = -1, lwm = 0;
        while (true)
        {
            if (GetBit() != 0)
            {
                if (GetBit() != 0)
                {
                    if (GetBit() != 0)
                    {
                        int offs = 0;
                        for (int i = 0; i < 4; i++) offs = (offs << 1) + GetBit();
                        dst.Add(offs != 0 ? dst[dst.Count - offs] : (byte)0);
                        lwm = 0;
                    }
                    else
                    {
                        int offs = src[s++];
                        int len = 2 + (offs & 1);
                        offs >>= 1;
                        if (offs == 0) break;
                        Copy(offs, len);
                        r0 = offs;
                        lwm = 1;
                    }
                }
                else
                {
                    int offs = GetGamma();
                    if (lwm == 0 && offs == 2)
                    {
                        offs = r0;
                        Copy(offs, GetGamma());
                    }
                    else
                    {
                        offs -= lwm == 0 ? 3 : 2;
                        offs = (offs << 8) + src[s++];
                        int len = GetGamma();
                        if (offs >= 32000) len++;
                        if (offs >= 1280) len++;
                        if (offs < 128) len += 2;
                        Copy(offs, len);
                        r0 = offs;
                    }
                    lwm = 1;
                }
            }
            else
            {
                dst.Add(src[s++]);
                lwm = 0;
            }
        }
        return dst.ToArray();
    }
}
