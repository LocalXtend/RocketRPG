#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RocketRPG.Models;

public class RgssArchiveEntry
{
    public string RelativePath { get; set; } = "";
    public long Offset { get; set; }
    public int Size { get; set; }
    public uint FileKey { get; set; }
    public int Version { get; set; }
}

public class RgssArchiveReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private readonly int _version;
    private readonly Dictionary<string, RgssArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Version => _version;
    public IReadOnlyCollection<string> FilePaths => _entries.Keys;
    public IReadOnlyCollection<RgssArchiveEntry> Entries => _entries.Values;
    public int Count => _entries.Count;

    private RgssArchiveReader(FileStream stream, int version)
    {
        _stream = stream;
        _reader = new BinaryReader(_stream);
        _version = version;
    }

    public static bool IsArchive(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            using var fs = File.OpenRead(filePath);
            if (fs.Length < 8) return false;
            byte[] magic = new byte[8];
            fs.ReadExactly(magic, 0, 8);
            return magic[0] == 'R' && magic[1] == 'G' && magic[2] == 'S' && magic[3] == 'S' &&
                   magic[4] == 'A' && magic[5] == 'D' && magic[6] == 0 &&
                   (magic[7] == 1 || magic[7] == 2 || magic[7] == 3);
        }
        catch
        {
            return false;
        }
    }

    public static RgssArchiveReader? TryOpen(string gameDir)
    {
        string[] candidates = ["Game.rgss3a", "Game.rgss2a", "Game.rgssad"];
        foreach (var c in candidates)
        {
            string p = Path.Combine(gameDir, c);
            if (File.Exists(p))
            {
                var reader = Open(p);
                if (reader != null) return reader;
            }
        }
        return null;
    }

    public static RgssArchiveReader? Open(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            byte[] magic = new byte[8];
            stream.ReadExactly(magic, 0, 8);
            if (magic[0] != 'R' || magic[1] != 'G' || magic[2] != 'S' || magic[3] != 'S' ||
                magic[4] != 'A' || magic[5] != 'D' || magic[6] != 0)
            {
                stream.Dispose();
                return null;
            }

            int ver = magic[7];
            var archive = new RgssArchiveReader(stream, ver);
            if (ver == 3)
            {
                archive.IndexV3();
            }
            else
            {
                archive.IndexV1();
            }
            return archive;
        }
        catch
        {
            stream.Dispose();
            return null;
        }
    }

    private static uint NextKey(uint k) => (k * 7u + 3u);

    private void IndexV1()
    {
        uint key = 0xDEADCAFE;
        long len = _stream.Length;

        while (_stream.Position < len)
        {
            if (_stream.Position + 4 > len) break;
            uint encLen = _reader.ReadUInt32();
            uint nameLen = encLen ^ key;
            key = NextKey(key);

            if (nameLen == 0 || nameLen > 4096 || _stream.Position + nameLen > len) break;

            byte[] nameBytes = _reader.ReadBytes((int)nameLen);
            for (int i = 0; i < nameLen; i++)
            {
                nameBytes[i] ^= (byte)(key & 0xFF);
                key = NextKey(key);
            }

            if (_stream.Position + 4 > len) break;
            uint encSize = _reader.ReadUInt32();
            int fileSize = (int)(encSize ^ key);
            key = NextKey(key);

            if (fileSize < 0 || _stream.Position + fileSize > len) break;

            string name = DecodeString(nameBytes);
            name = NormalizePath(name);

            _entries[name] = new RgssArchiveEntry
            {
                RelativePath = name,
                Offset = _stream.Position,
                Size = fileSize,
                FileKey = key,
                Version = _version
            };

            _stream.Position += fileSize;
        }
    }

    private void IndexV3()
    {
        uint rawKey = _reader.ReadUInt32();
        uint key = (rawKey * 9u + 3u);

        while (true)
        {
            uint encOffset = _reader.ReadUInt32();
            uint offset = encOffset ^ key;
            if (offset == 0) break;

            uint size = _reader.ReadUInt32() ^ key;
            uint fileKey = _reader.ReadUInt32() ^ key;
            uint nameLen = _reader.ReadUInt32() ^ key;

            byte[] nameBytes = _reader.ReadBytes((int)nameLen);
            byte[] kBytes = BitConverter.GetBytes(key);
            for (int i = 0; i < nameLen; i++)
            {
                nameBytes[i] ^= kBytes[i % 4];
            }

            string name = DecodeString(nameBytes);
            name = NormalizePath(name);

            _entries[name] = new RgssArchiveEntry
            {
                RelativePath = name,
                Offset = offset,
                Size = (int)size,
                FileKey = fileKey,
                Version = 3
            };
        }
    }

    public bool FileExists(string relativePath)
    {
        return _entries.ContainsKey(NormalizePath(relativePath));
    }

    public byte[]? ReadFile(string relativePath)
    {
        string norm = NormalizePath(relativePath);
        if (!_entries.TryGetValue(norm, out var entry))
        {
            return null;
        }

        lock (_stream)
        {
            _stream.Seek(entry.Offset, SeekOrigin.Begin);
            byte[] data = new byte[entry.Size];
            _stream.ReadExactly(data, 0, entry.Size);

            if (entry.Version == 3)
            {
                uint currKey = entry.FileKey;
                for (int i = 0; i < entry.Size; i += 4)
                {
                    byte[] kb = BitConverter.GetBytes(currKey);
                    int count = Math.Min(4, entry.Size - i);
                    for (int j = 0; j < count; j++)
                    {
                        data[i + j] ^= kb[j];
                    }
                    currKey = (currKey * 7u + 3u);
                }
            }
            else
            {
                uint k = entry.FileKey;
                for (int i = 0; i < entry.Size; i += 4)
                {
                    byte[] kb = BitConverter.GetBytes(k);
                    int count = Math.Min(4, entry.Size - i);
                    for (int j = 0; j < count; j++)
                    {
                        data[i + j] ^= kb[j];
                    }
                    k = NextKey(k);
                }
            }
            return data;
        }
    }

    private static string NormalizePath(string p)
    {
        return p.Replace('/', '\\').TrimStart('\\');
    }

    private static bool IsLikelyShiftJis(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length - 1; i++)
        {
            byte b1 = bytes[i];
            byte b2 = bytes[i + 1];
            // Hiragana: 0x82 [0x9F..0xF1]
            if (b1 == 0x82 && b2 >= 0x9F && b2 <= 0xF1) return true;
            // Katakana: 0x83 [0x40..0x96] (excluding 0x7F)
            if (b1 == 0x83 && b2 >= 0x40 && b2 <= 0x96 && b2 != 0x7F) return true;
        }
        return false;
    }

    private static string DecodeString(byte[] bytes)
    {
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            return utf8.GetString(bytes);
        }
        catch
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            if (IsLikelyShiftJis(bytes))
            {
                try
                {
                    return Encoding.GetEncoding(932).GetString(bytes);
                }
                catch { }
            }

            try
            {
                return Encoding.GetEncoding(949).GetString(bytes);
            }
            catch
            {
                try
                {
                    return Encoding.GetEncoding(932).GetString(bytes);
                }
                catch
                {
                    return Encoding.Latin1.GetString(bytes);
                }
            }
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
