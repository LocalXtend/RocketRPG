#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace RocketRPG.Models;

public enum AssetKind { Image, Audio, Video, Font, Data, Script, Other }

/// <summary>에셋 하나: 보이는 경로(폴더/이름), 종류, 크기, 어디에 있는지</summary>
public sealed class AssetEntry
{
    public string Path { get; init; } = "";       // 게임 기준 상대 경로 ("Graphics/Characters/Actor1.png")
    public string Folder { get; init; } = "";     // 분류 ("Graphics/Characters", RTP면 "RTP · Graphics/Characters")
    public string Name => System.IO.Path.GetFileName(Path);
    public AssetKind Kind { get; init; }
    public long Size { get; init; }
    public bool Rtp { get; init; }
    public bool InArchive { get; init; }
    public bool Encrypted { get; init; }          // MV/MZ 암호화 (.rpgmvp 등)
    public bool Default { get; set; }             // 쯔꾸르 기본 에셋 (RTP와 같은 파일, MV/MZ 새 프로젝트 기본 파일)
    internal string? FullPath { get; init; }      // 디스크 파일 (아카이브 안이면 null)
}

/// <summary>
/// 도구 &gt; 에셋 보기: 지금 내 PC에서 연 게임의 파일 목록과 내용을 읽습니다 (게임 폴더, 암호화 아카이브, RTP).
/// 읽기만 합니다: 파일로 꺼내거나 저장하는 기능은 없고, 멀티로도 보내지 않습니다.
/// 해독은 이미 있는 경로만 씁니다 (RGSS 아카이브, MV/MZ System.json 키).
/// </summary>
public sealed class AssetCatalog : IDisposable
{
    static readonly string[] MvFolders = ["img", "audio", "movies", "fonts", "effects", "data", "js", "icon"];
    static readonly string[] RgssFolders = ["Graphics", "Audio", "Data", "Fonts", "Movies"];

    readonly string _gameDir;
    readonly int _engine;
    readonly Lazy<RgssArchiveReader?> _archive;
    readonly object _archiveLock = new();
    byte[]? _mvKey;
    bool _mvKeyLoaded;

    public AssetCatalog(string gameDir, int engine)
    {
        _gameDir = gameDir;
        _engine = engine;
        _archive = new Lazy<RgssArchiveReader?>(() => IsRgss ? RgssArchiveReader.TryOpen(gameDir) : null);
    }

    bool IsRgss => _engine is CoreInterop.EngineXp or CoreInterop.EngineVx or CoreInterop.EngineAce;
    bool IsMv => _engine is CoreInterop.EngineMv or CoreInterop.EngineMz;
    bool Is2k => _engine is CoreInterop.Engine2000 or CoreInterop.Engine2003;

    string MvRoot => Directory.Exists(System.IO.Path.Combine(_gameDir, "www")) ? System.IO.Path.Combine(_gameDir, "www") : _gameDir;

    /// <summary>확장자로 종류 (MV/MZ 암호화 확장자 포함)</summary>
    public static AssetKind KindOf(string name)
    {
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".bmp" or ".jpg" or ".jpeg" or ".gif" or ".xyz" or ".webp" or ".rpgmvp" or ".png_" => AssetKind.Image,
            ".ogg" or ".mp3" or ".wav" or ".mid" or ".midi" or ".m4a" or ".wma" or ".rpgmvo" or ".ogg_" or ".rpgmvm" or ".m4a_" => AssetKind.Audio,
            ".webm" or ".mp4" or ".ogv" or ".avi" or ".mpg" or ".mpeg" or ".wmv" => AssetKind.Video,
            ".ttf" or ".otf" or ".ttc" or ".woff" or ".woff2" or ".fon" => AssetKind.Font,
            ".js" or ".rb" or ".lua" => AssetKind.Script,
            ".json" or ".rxdata" or ".rvdata" or ".rvdata2" or ".lmu" or ".ldb" or ".lmt" or ".lsd" or ".ini" or ".txt" or ".csv" or ".xml" => AssetKind.Data,
            _ => AssetKind.Other,
        };
    }

    static bool IsEncryptedExt(string name) =>
        System.IO.Path.GetExtension(name).ToLowerInvariant() is ".rpgmvp" or ".png_" or ".rpgmvo" or ".ogg_" or ".rpgmvm" or ".m4a_";

    /// <summary>
    /// 모든 에셋 (게임 폴더 → 아카이브 → RTP). 수천 개도 금방 끝나도록 디스크 크기만 읽고 내용은 읽지 않습니다.
    /// </summary>
    public List<AssetEntry> Enumerate(CancellationToken ct = default)
    {
        var list = new List<AssetEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddDir(string root, IEnumerable<string> topFolders, bool rtp)
        {
            foreach (var top in topFolders)
            {
                string dir = System.IO.Path.Combine(root, top);
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
                catch { continue; }
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string rel = System.IO.Path.GetRelativePath(root, f).Replace('\\', '/');
                    if (!rtp && !seen.Add(rel)) continue;
                    long size = 0;
                    try { size = new FileInfo(f).Length; } catch { }
                    string folder = System.IO.Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
                    list.Add(new AssetEntry
                    {
                        Path = rel, Folder = rtp ? "RTP · " + folder : folder, Kind = KindOf(rel), Size = size, Rtp = rtp,
                        Encrypted = IsEncryptedExt(rel), FullPath = f,
                    });
                }
            }
        }

        if (IsMv) AddDir(MvRoot, MvFolders, false);
        else if (IsRgss) AddDir(_gameDir, RgssFolders, false);
        else if (Is2k) AddDir(_gameDir, TopFolders(_gameDir), false);

        if (IsRgss && _archive.Value is { } arc)
        {
            foreach (var e in arc.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string rel = e.RelativePath.Replace('\\', '/');
                if (!seen.Add(rel)) continue;
                list.Add(new AssetEntry
                {
                    Path = rel, Folder = System.IO.Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "", Kind = KindOf(rel), Size = e.Size, InArchive = true,
                });
            }
        }

        foreach (var rtp in RtpDirs())
        {
            if (Is2k) AddDir(rtp, TopFolders(rtp), true);
            else AddDir(rtp, RgssFolders, true);
        }

        // 기본 에셋 표시: RTP 폴더의 파일, 그리고 게임에 들어 있지만 RTP·기본 목록과 경로·크기가 같은 파일
        var rtpFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in list) if (e.Rtp) rtpFiles.Add($"{e.Path}|{e.Size}");
        foreach (var e in list)
        {
            if (e.Rtp) { e.Default = true; continue; }
            e.Default = rtpFiles.Contains($"{e.Path}|{e.Size}") || DefaultAssets.IsDefault(_engine, e.Path, e.Size, e.Encrypted);
        }
        return list;
    }

    static IEnumerable<string> TopFolders(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).Select(System.IO.Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList(); }
        catch { return []; }
    }

    List<string> RtpDirs()
    {
        try
        {
            if (IsRgss) return RtpResolver.ResolveRgss(_gameDir, _engine);
            if (Is2k) return RtpResolver.Resolve2kPaths(_engine == CoreInterop.Engine2003).Take(1).ToList();
        }
        catch { }
        return [];
    }

    /// <summary>에셋 내용 (MV/MZ 암호화는 풀어서). 못 읽으면 null.</summary>
    public byte[]? Read(AssetEntry e)
    {
        try
        {
            byte[]? bytes;
            if (e.FullPath != null) bytes = File.ReadAllBytes(e.FullPath);
            else lock (_archiveLock) bytes = _archive.Value?.ReadFile(e.Path.Replace('/', '\\'));
            if (bytes != null && e.Encrypted) bytes = DecryptMv(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            UiLog.Write($"assets: read {e.Path} failed {ex.Message}");
            return null;
        }
    }

    /// <summary>MV/MZ 암호화 파일: 앞 16바이트 머리를 떼고 다음 16바이트를 System.json의 키로 XOR</summary>
    byte[] DecryptMv(byte[] data)
    {
        if (!_mvKeyLoaded)
        {
            _mvKeyLoaded = true;
            try
            {
                string data1 = System.IO.Path.Combine(MvRoot, "data", "System.json");
                using var doc = JsonDocument.Parse(File.ReadAllText(data1));
                if (doc.RootElement.TryGetProperty("encryptionKey", out var k) && k.GetString() is { Length: >= 32 } hex)
                    _mvKey = Convert.FromHexString(hex[..32]);
            }
            catch { }
        }
        if (data.Length <= 16) return data;
        var body = data[16..];
        if (_mvKey != null)
            for (int i = 0; i < 16 && i < body.Length; i++) body[i] ^= _mvKey[i];
        return body;
    }

    /// <summary>소리·영상의 실제 형식 (MV 암호화 확장자 → 원래 확장자)</summary>
    public static string PlainExtension(string name)
    {
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext switch { ".rpgmvp" or ".png_" => ".png", ".rpgmvo" or ".ogg_" => ".ogg", ".rpgmvm" or ".m4a_" => ".m4a", _ => ext };
    }

    public void Dispose()
    {
        if (_archive.IsValueCreated) _archive.Value?.Dispose();
    }
}
