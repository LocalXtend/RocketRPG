#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RocketRPG.Models;

/// <summary>BGRA32(비사전곱 알파) 이미지</summary>
public sealed class TileImage
{
    public int W, H;
    public byte[] Px = [];
}

/// <summary>
/// 게임 리소스(데이터/이미지) 접근: 게임 폴더 → 암호화 아카이브(RGSSAD) → RTP 순서로 찾습니다.
/// MV/MZ 암호화 이미지(.rpgmvp/.png_)도 해독합니다. 맵 뷰어 한 세션 동안 아카이브를 열어 둡니다.
/// </summary>
public sealed class GameAssetSource : IDisposable
{
    readonly string _gameDir;
    readonly int _engine;
    readonly Lazy<RgssArchiveReader?> _archive;
    readonly List<string> _rtp = new();
    readonly Dictionary<string, TileImage?> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    byte[]? _mvKey;
    bool _mvKeyLoaded;

    public string GameDir => _gameDir;
    public int Engine => _engine;

    public GameAssetSource(string gameDir, int engine)
    {
        _gameDir = gameDir;
        _engine = engine;
        _archive = new Lazy<RgssArchiveReader?>(() => RgssArchiveReader.TryOpen(gameDir));
        try
        {
            if (engine is CoreInterop.EngineXp or CoreInterop.EngineVx or CoreInterop.EngineAce)
                _rtp.AddRange(RtpResolver.ResolveRgss(gameDir, engine));
            else if (engine is CoreInterop.Engine2000 or CoreInterop.Engine2003)
            {
                var r = RtpResolver.Resolve2k(engine == CoreInterop.Engine2003);
                if (r != null) _rtp.Add(r);
            }
        }
        catch { }
    }

    public bool IsMvFamily => _engine is CoreInterop.EngineMv or CoreInterop.EngineMz;

    public string MvDataDir =>
        Directory.Exists(Path.Combine(_gameDir, "www", "data")) ? Path.Combine(_gameDir, "www", "data") : Path.Combine(_gameDir, "data");

    string MvRoot => Directory.Exists(Path.Combine(_gameDir, "www")) ? Path.Combine(_gameDir, "www") : _gameDir;

    /// <summary>게임 폴더 또는 아카이브에서 데이터 파일을 읽습니다 (예: "Data\\Tilesets.rvdata2").</summary>
    public byte[]? ReadData(string relative)
    {
        string p = Path.Combine(_gameDir, relative);
        if (File.Exists(p))
        {
            try { return File.ReadAllBytes(p); } catch { }
        }
        return _archive.Value?.ReadFile(relative);
    }

    byte[]? ReadAny(string relativeNoExt, string[] exts, out string ext)
    {
        foreach (var e in exts)
        {
            string rel = relativeNoExt + e;
            string p = Path.Combine(_gameDir, rel);
            if (File.Exists(p)) { ext = e; try { return File.ReadAllBytes(p); } catch { } }
            var a = _archive.Value?.ReadFile(rel);
            if (a != null) { ext = e; return a; }
            foreach (var rtp in _rtp)
            {
                string rp = Path.Combine(rtp, rel);
                if (File.Exists(rp)) { ext = e; try { return File.ReadAllBytes(rp); } catch { } }
            }
        }
        ext = "";
        return null;
    }

    /// <summary>폴더(예: "Graphics\\Tilesets")와 확장자 없는 이름으로 이미지를 불러옵니다.</summary>
    public TileImage? LoadImage(string folder, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = folder + "|" + name;
        if (_imageCache.TryGetValue(key, out var cached)) return cached;
        TileImage? img = null;
        try
        {
            if (IsMvFamily)
            {
                string baseRel = Path.Combine(MvRoot, folder, name);
                if (File.Exists(baseRel + ".png")) img = Decode(File.ReadAllBytes(baseRel + ".png"), false);
                else
                {
                    foreach (var ext in new[] { ".rpgmvp", ".png_" })
                        if (File.Exists(baseRel + ext)) { img = Decode(DecryptMv(File.ReadAllBytes(baseRel + ext)), false); break; }
                }
            }
            else
            {
                bool paletted2k = _engine is CoreInterop.Engine2000 or CoreInterop.Engine2003;
                var bytes = ReadAny(Path.Combine(folder, name), [".png", ".bmp", ".xyz", ".jpg"], out var ext);
                if (bytes != null)
                    img = ext == ".xyz" ? DecodeXyz(bytes) : Decode(bytes, paletted2k);
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"MapTileRenderer: image {folder}/{name} failed: {ex.Message}");
        }
        _imageCache[key] = img;
        return img;
    }

    byte[] DecryptMv(byte[] data)
    {
        if (!_mvKeyLoaded)
        {
            _mvKeyLoaded = true;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(MvDataDir, "System.json")));
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

    /// <summary>PNG/BMP/JPG → BGRA. 2000/2003 팔레트 이미지는 0번 색을 투명으로 처리합니다.</summary>
    static TileImage? Decode(byte[] bytes, bool paletteIndex0Transparent)
    {
        using var ms = new MemoryStream(bytes);
        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = dec.Frames[0];
        int w = frame.PixelWidth, h = frame.PixelHeight;
        var img = new TileImage { W = w, H = h, Px = new byte[w * h * 4] };

        if (paletteIndex0Transparent && frame.Palette != null && frame.Format.BitsPerPixel <= 8)
        {
            var idx = new FormatConvertedBitmap(frame, PixelFormats.Indexed8, frame.Palette, 0);
            var ind = new byte[w * h];
            idx.CopyPixels(ind, w, 0);
            var pal = frame.Palette.Colors;
            for (int i = 0; i < ind.Length; i++)
            {
                int c = ind[i];
                if (c == 0 || c >= pal.Count) continue; // 투명
                var col = pal[c];
                img.Px[i * 4] = col.B;
                img.Px[i * 4 + 1] = col.G;
                img.Px[i * 4 + 2] = col.R;
                img.Px[i * 4 + 3] = 255;
            }
            return img;
        }

        var conv = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        conv.CopyPixels(img.Px, w * 4, 0);
        return img;
    }

    /// <summary>에셋 보기용: XYZ 그림</summary>
    public static TileImage? DecodeXyzImage(byte[] bytes) => DecodeXyz(bytes);

    /// <summary>에셋 보기용: PNG/BMP 그림 (2000/2003은 팔레트 0번 색을 투명으로)</summary>
    public static TileImage? DecodeImage(byte[] bytes, bool paletteIndex0Transparent) => Decode(bytes, paletteIndex0Transparent);

    /// <summary>RPG Maker 2000 XYZ: "XYZ1", u16 w, u16 h, zlib(팔레트 768 + 인덱스 w*h), 0번 투명</summary>
    static TileImage? DecodeXyz(byte[] bytes)
    {
        if (bytes.Length < 8 || bytes[0] != 'X' || bytes[1] != 'Y' || bytes[2] != 'Z' || bytes[3] != '1') return null;
        int w = bytes[4] | (bytes[5] << 8), h = bytes[6] | (bytes[7] << 8);
        using var z = new ZLibStream(new MemoryStream(bytes, 8, bytes.Length - 8), CompressionMode.Decompress);
        var raw = new byte[768 + w * h];
        int read = 0;
        while (read < raw.Length)
        {
            int n = z.Read(raw, read, raw.Length - read);
            if (n <= 0) break;
            read += n;
        }
        var img = new TileImage { W = w, H = h, Px = new byte[w * h * 4] };
        for (int i = 0; i < w * h; i++)
        {
            int c = raw[768 + i];
            if (c == 0) continue;
            img.Px[i * 4] = raw[c * 3 + 2];
            img.Px[i * 4 + 1] = raw[c * 3 + 1];
            img.Px[i * 4 + 2] = raw[c * 3];
            img.Px[i * 4 + 3] = 255;
        }
        return img;
    }

    public void Dispose()
    {
        if (_archive.IsValueCreated) _archive.Value?.Dispose();
    }
}

/// <summary>
/// 게임의 실제 타일셋 이미지로 맵을 그립니다.
/// VX/VX Ace/MV/MZ(같은 타일 ID 체계), XP(오토타일 48패턴), 2000/2003(칩셋)을 지원합니다.
/// </summary>
public static class MapTileRenderer
{
    public sealed record Result(byte[] Pixels, int Width, int Height, int TileSize);

    // ── VX/Ace/MV/MZ 오토타일 테이블 (rpg_core.js Tilemap과 동일) ──
    static readonly int[][][] FloorTable =
    [
        [[2,4],[1,4],[2,3],[1,3]],[[2,0],[1,4],[2,3],[1,3]],[[2,4],[3,0],[2,3],[1,3]],[[2,0],[3,0],[2,3],[1,3]],
        [[2,4],[1,4],[2,3],[3,1]],[[2,0],[1,4],[2,3],[3,1]],[[2,4],[3,0],[2,3],[3,1]],[[2,0],[3,0],[2,3],[3,1]],
        [[2,4],[1,4],[2,1],[1,3]],[[2,0],[1,4],[2,1],[1,3]],[[2,4],[3,0],[2,1],[1,3]],[[2,0],[3,0],[2,1],[1,3]],
        [[2,4],[1,4],[2,1],[3,1]],[[2,0],[1,4],[2,1],[3,1]],[[2,4],[3,0],[2,1],[3,1]],[[2,0],[3,0],[2,1],[3,1]],
        [[0,4],[1,4],[0,3],[1,3]],[[0,4],[3,0],[0,3],[1,3]],[[0,4],[1,4],[0,3],[3,1]],[[0,4],[3,0],[0,3],[3,1]],
        [[2,2],[1,2],[2,3],[1,3]],[[2,2],[1,2],[2,3],[3,1]],[[2,2],[1,2],[2,1],[1,3]],[[2,2],[1,2],[2,1],[3,1]],
        [[2,4],[3,4],[2,3],[3,3]],[[2,4],[3,4],[2,1],[3,3]],[[2,0],[3,4],[2,3],[3,3]],[[2,0],[3,4],[2,1],[3,3]],
        [[2,4],[1,4],[2,5],[1,5]],[[2,0],[1,4],[2,5],[1,5]],[[2,4],[3,0],[2,5],[1,5]],[[2,0],[3,0],[2,5],[1,5]],
        [[0,4],[3,4],[0,3],[3,3]],[[2,2],[1,2],[2,5],[1,5]],[[0,2],[1,2],[0,3],[1,3]],[[0,2],[1,2],[0,3],[3,1]],
        [[2,2],[3,2],[2,3],[3,3]],[[2,2],[3,2],[2,1],[3,3]],[[2,4],[3,4],[2,5],[3,5]],[[2,0],[3,4],[2,5],[3,5]],
        [[0,4],[1,4],[0,5],[1,5]],[[0,4],[3,0],[0,5],[1,5]],[[0,2],[3,2],[0,3],[3,3]],[[0,2],[1,2],[0,5],[1,5]],
        [[0,4],[3,4],[0,5],[3,5]],[[2,2],[3,2],[2,5],[3,5]],[[0,2],[3,2],[0,5],[3,5]],[[0,0],[1,0],[0,1],[1,1]]
    ];

    static readonly int[][][] WallTable =
    [
        [[2,2],[1,2],[2,1],[1,1]],[[0,2],[1,2],[0,1],[1,1]],[[2,0],[1,0],[2,1],[1,1]],[[0,0],[1,0],[0,1],[1,1]],
        [[2,2],[3,2],[2,1],[3,1]],[[0,2],[3,2],[0,1],[3,1]],[[2,0],[3,0],[2,1],[3,1]],[[0,0],[3,0],[0,1],[3,1]],
        [[2,2],[1,2],[2,3],[1,3]],[[0,2],[1,2],[0,3],[1,3]],[[2,0],[1,0],[2,3],[1,3]],[[0,0],[1,0],[0,3],[1,3]],
        [[2,2],[3,2],[2,3],[3,3]],[[0,2],[3,2],[0,3],[3,3]],[[2,0],[3,0],[2,3],[3,3]],[[0,0],[3,0],[0,3],[3,3]]
    ];

    static readonly int[][][] WaterfallTable =
    [
        [[2,0],[1,0],[2,1],[1,1]],[[0,0],[1,0],[0,1],[1,1]],[[2,0],[3,0],[2,1],[3,1]],[[0,0],[3,0],[0,1],[3,1]]
    ];

    // ── XP 오토타일 (48패턴 × 4쿼터, 96x128 이미지의 16px 쿼터 번호, 1부터) ──
    static readonly int[][] XpAutotile =
    [
        [27,28,33,34],[5,28,33,34],[27,6,33,34],[5,6,33,34],[27,28,33,12],[5,28,33,12],[27,6,33,12],[5,6,33,12],
        [27,28,11,34],[5,28,11,34],[27,6,11,34],[5,6,11,34],[27,28,11,12],[5,28,11,12],[27,6,11,12],[5,6,11,12],
        [25,26,31,32],[25,6,31,32],[25,26,31,12],[25,6,31,12],[15,16,21,22],[15,16,21,12],[15,16,11,22],[15,16,11,12],
        [29,30,35,36],[29,30,11,36],[5,30,35,36],[5,30,11,36],[39,40,45,46],[5,40,45,46],[39,6,45,46],[5,6,45,46],
        [25,30,31,36],[15,16,45,46],[13,14,19,20],[13,14,19,12],[17,18,23,24],[17,18,11,24],[41,42,47,48],[5,42,47,48],
        [37,38,43,44],[37,6,43,44],[13,18,19,24],[13,14,43,44],[37,42,43,48],[17,18,47,48],[13,18,43,48],[1,2,7,8]
    ];

    /// <summary>타일셋 이미지 슬롯. VX 계열: 0~8 = A1,A2,A3,A4,A5,B,C,D,E / XP: 0=타일셋, 1~7=오토타일 / 2000: 0=칩셋</summary>
    public sealed class Tileset
    {
        public TileImage?[] Sets = new TileImage?[9];
        public int TileSize;
        public int Layers;
        public string Kind = "vx"; // vx | xp | 2k
        public bool HasAny => Sets.Any(s => s != null);
    }

    public static Tileset? LoadTileset(GameAssetSource src, int tilesetId, int chipsetId)
    {
        try
        {
            switch (src.Engine)
            {
                case CoreInterop.EngineMv:
                case CoreInterop.EngineMz:
                {
                    var ts = new Tileset { Kind = "vx", TileSize = 48, Layers = 4 };
                    using (var sys = JsonDocument.Parse(File.ReadAllText(Path.Combine(src.MvDataDir, "System.json"))))
                        if (sys.RootElement.TryGetProperty("tileSize", out var tsz) && tsz.ValueKind == JsonValueKind.Number) ts.TileSize = tsz.GetInt32();
                    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(src.MvDataDir, "Tilesets.json")));
                    var entry = doc.RootElement.EnumerateArray().Skip(tilesetId).FirstOrDefault();
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("tilesetNames", out var names)) return null;
                    int i = 0;
                    foreach (var n in names.EnumerateArray())
                    {
                        if (i < 9) ts.Sets[i] = src.LoadImage(Path.Combine("img", "tilesets"), n.GetString() ?? "");
                        i++;
                    }
                    return ts;
                }
                case CoreInterop.EngineAce:
                {
                    var ts = new Tileset { Kind = "vx", TileSize = 32, Layers = 3 };
                    var bytes = src.ReadData(@"Data\Tilesets.rvdata2");
                    if (bytes == null) return null;
                    if (new RubyMarshalReader(bytes).ReadObject() is not List<object?> list || tilesetId >= list.Count) return null;
                    if (list[tilesetId] is not RubyObject tso || tso["@tileset_names"] is not List<object?> names) return null;
                    for (int i = 0; i < 9 && i < names.Count; i++)
                        ts.Sets[i] = src.LoadImage(@"Graphics\Tilesets", names[i]?.ToString() ?? "");
                    return ts;
                }
                case CoreInterop.EngineVx:
                {
                    var ts = new Tileset { Kind = "vx", TileSize = 32, Layers = 3 };
                    string[] names = ["TileA1", "TileA2", "TileA3", "TileA4", "TileA5", "TileB", "TileC", "TileD", "TileE"];
                    for (int i = 0; i < 9; i++) ts.Sets[i] = src.LoadImage(@"Graphics\System", names[i]);
                    return ts;
                }
                case CoreInterop.EngineXp:
                {
                    var ts = new Tileset { Kind = "xp", TileSize = 32, Layers = 3 };
                    var bytes = src.ReadData(@"Data\Tilesets.rxdata");
                    if (bytes == null) return null;
                    if (new RubyMarshalReader(bytes).ReadObject() is not List<object?> list || tilesetId >= list.Count) return null;
                    if (list[tilesetId] is not RubyObject tso) return null;
                    ts.Sets[0] = src.LoadImage(@"Graphics\Tilesets", tso["@tileset_name"]?.ToString() ?? "");
                    if (tso["@autotile_names"] is List<object?> auto)
                        for (int i = 0; i < 7 && i < auto.Count; i++)
                            ts.Sets[1 + i] = src.LoadImage(@"Graphics\Autotiles", auto[i]?.ToString() ?? "");
                    return ts;
                }
                case CoreInterop.Engine2000:
                case CoreInterop.Engine2003:
                {
                    var ts = new Tileset { Kind = "2k", TileSize = 16, Layers = 2 };
                    var ldb = src.ReadData("RPG_RT.ldb");
                    if (ldb == null) return null;
                    var db = LcfReader.ParseDatabase(ldb);
                    var cs = db.Chipsets.FirstOrDefault(c => c.Id == chipsetId);
                    if (cs == null) return null;
                    ts.Sets[0] = src.LoadImage("ChipSet", cs.GraphicName);
                    return ts;
                }
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"MapTileRenderer: tileset load failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 맵 전체를 그립니다. 출력 한 변이 maxSide를 넘지 않도록 타일 크기를 줄입니다.
    /// data 배열은 (z * height + y) * width + x 순서입니다.
    /// </summary>
    public static Result? Render(Tileset ts, int mapW, int mapH, int[] data, int maxSide = 4096)
    {
        if (mapW <= 0 || mapH <= 0 || !ts.HasAny) return null;
        int outTs = Math.Max(4, Math.Min(ts.TileSize, maxSide / Math.Max(mapW, mapH)));
        int ow = mapW * outTs, oh = mapH * outTs;
        var outPx = new byte[ow * oh * 4];
        // 배경: 어두운 체커(빈 칸 표시)
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
            {
                byte v = (byte)((((x / outTs) + (y / outTs)) & 1) == 0 ? 40 : 34);
                int i = (y * ow + x) * 4;
                outPx[i] = v; outPx[i + 1] = v; outPx[i + 2] = v; outPx[i + 3] = 255;
            }

        var cache = new Dictionary<int, byte[]?>();
        int layers = Math.Min(ts.Layers, Math.Max(1, data.Length / (mapW * mapH)));
        for (int z = 0; z < layers; z++)
        {
            for (int y = 0; y < mapH; y++)
            {
                for (int x = 0; x < mapW; x++)
                {
                    int idx = (z * mapH + y) * mapW + x;
                    if (idx >= data.Length) continue;
                    int id = data[idx];
                    if (id <= 0) continue;
                    if (!cache.TryGetValue(id, out var tile))
                    {
                        var full = ts.Kind switch
                        {
                            "xp" => ComposeXp(ts, id),
                            "2k" => Compose2k(ts, id),
                            _ => ComposeVx(ts, id)
                        };
                        tile = full == null ? null : Scale(full, ts.TileSize, outTs);
                        cache[id] = tile;
                    }
                    if (tile != null) BlendTile(outPx, ow, x * outTs, y * outTs, tile, outTs);
                }
            }
        }
        return new Result(outPx, ow, oh, outTs);
    }

    // ── 합성 ──

    static void Copy(TileImage src, int sx, int sy, int w, int h, byte[] dst, int dstSize, int dx, int dy)
    {
        for (int yy = 0; yy < h; yy++)
        {
            int syy = sy + yy, dyy = dy + yy;
            if (syy < 0 || syy >= src.H || dyy >= dstSize) continue;
            for (int xx = 0; xx < w; xx++)
            {
                int sxx = sx + xx, dxx = dx + xx;
                if (sxx < 0 || sxx >= src.W || dxx >= dstSize) continue;
                int si = (syy * src.W + sxx) * 4, di = (dyy * dstSize + dxx) * 4;
                dst[di] = src.Px[si]; dst[di + 1] = src.Px[si + 1]; dst[di + 2] = src.Px[si + 2]; dst[di + 3] = src.Px[si + 3];
            }
        }
    }

    static byte[]? ComposeVx(Tileset ts, int id)
    {
        int t = ts.TileSize;
        var tile = new byte[t * t * 4];
        if (id >= 8192) return null;
        if (id < 2048)
        {
            // B~E (0~1535), A5 (1536~2047): 일반 타일
            int set = id >= 1536 ? 4 : 5 + id / 256;
            var src = ts.Sets[set];
            if (src == null) return null;
            int sx = ((id / 128) % 2 * 8 + id % 8) * t;
            int sy = (id % 256 / 8 % 16) * t;
            Copy(src, sx, sy, t, t, tile, t, 0, 0);
            return tile;
        }

        int kind = (id - 2048) / 48, shape = (id - 2048) % 48;
        int tx = kind % 8, ty = kind / 8;
        int bx = 0, by = 0, setNumber;
        var table = FloorTable;
        if (id < 2816)
        {
            // A1: 물(애니메이션 첫 프레임), 폭포
            setNumber = 0;
            if (kind == 0) { bx = 0; by = 0; }
            else if (kind == 1) { bx = 0; by = 3; }
            else if (kind == 2) { bx = 6; by = 0; }
            else if (kind == 3) { bx = 6; by = 3; }
            else
            {
                bx = tx / 4 * 8;
                by = ty * 6 + tx / 2 % 2 * 3;
                if (kind % 2 != 0) { bx += 6; table = WaterfallTable; }
            }
        }
        else if (id < 4352) { setNumber = 1; bx = tx * 2; by = (ty - 2) * 3; }
        else if (id < 5888) { setNumber = 2; bx = tx * 2; by = (ty - 6) * 2; table = WallTable; }
        else
        {
            setNumber = 3;
            bx = tx * 2;
            by = (int)Math.Floor((ty - 10) * 2.5 + (ty % 2 == 1 ? 0.5 : 0));
            if (ty % 2 == 1) table = WallTable;
        }

        var img = ts.Sets[setNumber];
        if (img == null || shape >= table.Length) return null;
        int h1 = t / 2;
        var q = table[shape];
        for (int i = 0; i < 4; i++)
        {
            int sx1 = (bx * 2 + q[i][0]) * h1;
            int sy1 = (by * 2 + q[i][1]) * h1;
            Copy(img, sx1, sy1, h1, h1, tile, t, (i % 2) * h1, (i / 2) * h1);
        }
        return tile;
    }

    static byte[]? ComposeXp(Tileset ts, int id)
    {
        const int t = 32;
        var tile = new byte[t * t * 4];
        if (id < 48) return null;
        if (id < 384)
        {
            var img = ts.Sets[id / 48];
            if (img == null) return null;
            if (img.H <= 32)
            {
                Copy(img, 0, 0, t, t, tile, t, 0, 0); // 단일 타일형 오토타일
                return tile;
            }
            var q = XpAutotile[id % 48];
            for (int i = 0; i < 4; i++)
            {
                int n = q[i] - 1;
                Copy(img, (n % 6) * 16, (n / 6) * 16, 16, 16, tile, t, (i % 2) * 16, (i / 2) * 16);
            }
            return tile;
        }
        var set = ts.Sets[0];
        if (set == null) return null;
        int k = id - 384;
        Copy(set, (k % 8) * t, (k / 8) * t, t, t, tile, t, 0, 0);
        return tile;
    }

    /// <summary>
    /// 2000/2003 칩셋 (480x256). 하층 E블록/상층 F블록은 정확히, 지형 오토타일(D)과 물(A~C)은
    /// 해당 블록의 대표 타일로 근사합니다(가장자리 연결 무늬는 생략).
    /// </summary>
    static byte[]? Compose2k(Tileset ts, int id)
    {
        const int t = 16;
        var img = ts.Sets[0];
        if (img == null) return null;
        var tile = new byte[t * t * 4];
        int sx, sy;
        if (id >= 10000)
        {
            int n = id - 10000;
            if (n >= 144) return null;
            if (n < 48) { sx = 288 + (n % 6) * t; sy = 128 + (n / 6) * t; }
            else { n -= 48; sx = 384 + (n % 6) * t; sy = (n / 6) * t; }
        }
        else if (id >= 5000)
        {
            int n = id - 5000;
            if (n >= 144) return null;
            if (n < 96) { sx = 192 + (n % 6) * t; sy = (n / 6) * t; }
            else { n -= 96; sx = 288 + (n % 6) * t; sy = (n / 6) * t; }
        }
        else if (id >= 4000)
        {
            int k = (id - 4000) / 50;
            if (k >= 12) return null;
            int bx, by;
            if (k < 4) { bx = (k % 2) * 48; by = 128 + (k / 2) * 64; }
            else { int j = k - 4; bx = 96 + (j % 2) * 48; by = (j / 2) * 64; }
            sx = bx + 16; sy = by + 32; // 3x3 영역의 가운데 타일
        }
        else if (id >= 3000)
        {
            int k = Math.Min(2, (id - 3000) / 50);
            sx = 48; sy = 64 + k * t;
        }
        else if (id >= 2000) { sx = 0; sy = 64; }
        else if (id >= 1000) { sx = 48; sy = 0; }
        else { sx = 0; sy = 0; }
        Copy(img, sx, sy, t, t, tile, t, 0, 0);
        return tile;
    }

    static byte[] Scale(byte[] src, int from, int to)
    {
        if (from == to) return src;
        var dst = new byte[to * to * 4];
        for (int y = 0; y < to; y++)
        {
            int sy = y * from / to;
            for (int x = 0; x < to; x++)
            {
                int sx = x * from / to;
                Buffer.BlockCopy(src, (sy * from + sx) * 4, dst, (y * to + x) * 4, 4);
            }
        }
        return dst;
    }

    static void BlendTile(byte[] dst, int dstW, int dx, int dy, byte[] tile, int t)
    {
        for (int y = 0; y < t; y++)
        {
            int drow = ((dy + y) * dstW + dx) * 4;
            int srow = y * t * 4;
            for (int x = 0; x < t; x++)
            {
                int si = srow + x * 4;
                int a = tile[si + 3];
                if (a == 0) continue;
                int di = drow + x * 4;
                if (a == 255)
                {
                    dst[di] = tile[si]; dst[di + 1] = tile[si + 1]; dst[di + 2] = tile[si + 2];
                }
                else
                {
                    int ia = 255 - a;
                    dst[di] = (byte)((tile[si] * a + dst[di] * ia) / 255);
                    dst[di + 1] = (byte)((tile[si + 1] * a + dst[di + 1] * ia) / 255);
                    dst[di + 2] = (byte)((tile[si + 2] * a + dst[di + 2] * ia) / 255);
                }
            }
        }
    }

    public static BitmapSource ToBitmap(Result r)
    {
        var bmp = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgra32, null, r.Pixels, r.Width * 4);
        bmp.Freeze();
        return bmp;
    }
}
