#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RocketRPG.Views;

namespace RocketRPG.Models;

public class RubyObject
{
    public string ClassName { get; set; } = "";
    public Dictionary<string, object?> IVars { get; } = new(StringComparer.Ordinal);

    public object? this[string key]
    {
        get => IVars.TryGetValue(key, out var val) ? val : null;
        set => IVars[key] = value;
    }
}

public class RubyUserClass
{
    public string ClassName { get; set; } = "";
    public byte[] Data { get; set; } = [];
}

public class RubyTable
{
    public int Dimensions { get; set; }
    public int XSize { get; set; }
    public int YSize { get; set; }
    public int ZSize { get; set; }
    public int TotalElements { get; set; }
    public int[] Tiles { get; set; } = [];

    public int GetTile(int x, int y, int z)
    {
        if (x < 0 || x >= XSize || y < 0 || y >= YSize || z < 0 || z >= ZSize) return 0;
        int idx = z * (XSize * YSize) + y * XSize + x;
        return (idx >= 0 && idx < Tiles.Length) ? Tiles[idx] : 0;
    }

    public int GetTopTile(int x, int y)
    {
        for (int z = ZSize - 1; z >= 0; z--)
        {
            int tile = GetTile(x, y, z);
            if (tile > 0) return tile;
        }
        return 0;
    }
}

public class RubyMarshalReader
{
    private readonly byte[] _data;
    private int _pos;
    private readonly List<string> _symbols = new();
    private readonly List<object?> _objects = new();

    public RubyMarshalReader(byte[] data)
    {
        _data = data;
        _pos = 0;
        if (ReadByte() != 4 || ReadByte() != 8)
        {
            throw new InvalidDataException("Invalid Ruby Marshal header (expected 4.8)");
        }
    }

    private byte ReadByte()
    {
        if (_pos >= _data.Length) throw new EndOfStreamException();
        return _data[_pos++];
    }

    private sbyte ReadSByte()
    {
        return (sbyte)ReadByte();
    }

    private byte[] ReadBytes(int count)
    {
        if (_pos + count > _data.Length) throw new EndOfStreamException();
        byte[] buf = new byte[count];
        Buffer.BlockCopy(_data, _pos, buf, 0, count);
        _pos += count;
        return buf;
    }

    public int ReadFixnum()
    {
        sbyte b = ReadSByte();
        if (b == 0) return 0;
        if (b > 0 && b <= 4)
        {
            int val = 0;
            for (int i = 0; i < b; i++)
            {
                val |= (ReadByte() << (i * 8));
            }
            return val;
        }
        if (b > 4) return b - 5;
        if (b >= -4 && b < 0)
        {
            int val = -1;
            int shift = 0;
            for (int i = 0; i < -b; i++)
            {
                val &= ~(0xFF << shift);
                val |= (ReadByte() << shift);
                shift += 8;
            }
            return val;
        }
        if (b < -4) return b + 5;
        return 0;
    }

    public string ReadSymbol()
    {
        byte tag = ReadByte();
        if (tag == ':')
        {
            int len = ReadFixnum();
            byte[] bytes = ReadBytes(len);
            string sym = Encoding.Latin1.GetString(bytes);
            _symbols.Add(sym);
            return sym;
        }
        if (tag == ';')
        {
            int idx = ReadFixnum();
            if (idx >= 0 && idx < _symbols.Count) return _symbols[idx];
            return $"SymbolRef_{idx}";
        }
        throw new InvalidDataException($"Expected symbol tag (':' or ';'), got 0x{tag:X2} at {_pos - 1}");
    }

    public object? ReadObject()
    {
        byte tag = ReadByte();
        switch ((char)tag)
        {
            case '0': return null;
            case 'T': return true;
            case 'F': return false;
            case 'i': return ReadFixnum();
            case ':':
            case ';':
                _pos--;
                return ReadSymbol();
            case '@':
            {
                int idx = ReadFixnum();
                if (idx >= 0 && idx < _objects.Count) return _objects[idx];
                return null;
            }
            case 'I':
            {
                object? obj = ReadObject();
                int nIvars = ReadFixnum();
                for (int i = 0; i < nIvars; i++)
                {
                    ReadSymbol();
                    ReadObject();
                }
                return obj;
            }
            case '"':
            {
                int len = ReadFixnum();
                byte[] raw = ReadBytes(len);
                string str = DecodeRubyString(raw);
                _objects.Add(str);
                return str;
            }
            case '[':
            {
                int count = ReadFixnum();
                var arr = new List<object?>(count);
                _objects.Add(arr);
                for (int i = 0; i < count; i++)
                {
                    arr.Add(ReadObject());
                }
                return arr;
            }
            case '{':
            {
                int count = ReadFixnum();
                var dict = new Dictionary<object, object?>(count);
                _objects.Add(dict);
                for (int i = 0; i < count; i++)
                {
                    object? k = ReadObject();
                    object? v = ReadObject();
                    if (k != null) dict[k] = v;
                }
                return dict;
            }
            case 'o':
            {
                string cls = ReadSymbol();
                var obj = new RubyObject { ClassName = cls };
                _objects.Add(obj);
                int nIvars = ReadFixnum();
                for (int i = 0; i < nIvars; i++)
                {
                    string ivar = ReadSymbol();
                    object? val = ReadObject();
                    obj.IVars[ivar] = val;
                }
                return obj;
            }
            case 'u':
            {
                string cls = ReadSymbol();
                int len = ReadFixnum();
                byte[] raw = ReadBytes(len);
                var ucls = new RubyUserClass { ClassName = cls, Data = raw };
                _objects.Add(ucls);
                return ucls;
            }
            case 'U':
            {
                string cls = ReadSymbol();
                object? val = ReadObject();
                var ucls = new RubyUserClass { ClassName = cls };
                _objects.Add(ucls);
                return val;
            }
            case 'f':
            {
                int len = ReadFixnum();
                byte[] raw = ReadBytes(len);
                string fs = Encoding.Latin1.GetString(raw);
                double.TryParse(fs, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fval);
                _objects.Add(fval);
                return fval;
            }
            case 'l': // Bignum
            {
                sbyte sign = ReadSByte();
                int len = ReadFixnum() * 2;
                byte[] raw = ReadBytes(len);
                long val = 0;
                for (int i = 0; i < Math.Min(8, raw.Length); i++)
                {
                    val |= ((long)raw[i] << (i * 8));
                }
                if (sign == '-') val = -val;
                _objects.Add(val);
                return val;
            }
            default:
                throw new InvalidDataException($"Unknown Ruby Marshal tag '{(char)tag}' (0x{tag:X2}) at {_pos - 1}");
        }
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

    private static string DecodeRubyString(byte[] bytes)
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

    public static RubyTable? ParseTable(byte[] rawTableBytes)
    {
        if (rawTableBytes.Length < 20) return null;
        int dim = BitConverter.ToInt32(rawTableBytes, 0);
        int xsize = BitConverter.ToInt32(rawTableBytes, 4);
        int ysize = BitConverter.ToInt32(rawTableBytes, 8);
        int zsize = BitConverter.ToInt32(rawTableBytes, 12);
        int total = BitConverter.ToInt32(rawTableBytes, 16);

        if (xsize <= 0 || ysize <= 0 || zsize <= 0 || total != xsize * ysize * zsize) return null;
        if (rawTableBytes.Length < 20 + total * 2) return null;

        int[] tiles = new int[total];
        for (int i = 0; i < total; i++)
        {
            tiles[i] = BitConverter.ToInt16(rawTableBytes, 20 + i * 2);
        }

        return new RubyTable
        {
            Dimensions = dim,
            XSize = xsize,
            YSize = ysize,
            ZSize = zsize,
            TotalElements = total,
            Tiles = tiles
        };
    }

    public static List<MapListItem> ParseMapInfos(byte[] marshalBytes)
    {
        var result = new List<MapListItem>();
        try
        {
            var reader = new RubyMarshalReader(marshalBytes);
            var root = reader.ReadObject();
            if (root is Dictionary<object, object?> mapDict)
            {
                foreach (var kvp in mapDict)
                {
                    if (kvp.Key is int id && kvp.Value is RubyObject mobj)
                    {
                        string name = mobj["@name"]?.ToString() ?? $"Map{id:D3}";
                        int parentId = (mobj["@parent_id"] is int pid) ? pid : 0;
                        int order = (mobj["@order"] is int ord) ? ord : id;

                        result.Add(new MapListItem
                        {
                            Id = id,
                            Name = name,
                            ParentId = parentId,
                            Order = order
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"ParseMapInfos error: {ex.Message}");
        }

        result.Sort((a, b) => a.Order.CompareTo(b.Order));
        return result;
    }

    public static (int width, int height, int tilesetId, int[]? tileData, List<MapEventData> events) ParseMap(byte[] marshalBytes)
    {
        int width = 0, height = 0, tilesetId = 0;
        int[]? tileData = null;
        var events = new List<MapEventData>();

        try
        {
            var reader = new RubyMarshalReader(marshalBytes);
            var root = reader.ReadObject();
            if (root is RubyObject mapObj)
            {
                width = (mapObj["@width"] is int w) ? w : 0;
                height = (mapObj["@height"] is int h) ? h : 0;
                tilesetId = (mapObj["@tileset_id"] is int tid) ? tid : 0;

                // Table 파싱
                if (mapObj["@data"] is RubyUserClass uclass && string.Equals(uclass.ClassName, "Table", StringComparison.OrdinalIgnoreCase))
                {
                    var table = ParseTable(uclass.Data);
                    if (table != null)
                    {
                        if (width <= 0) width = table.XSize;
                        if (height <= 0) height = table.YSize;

                        // MapViewerWindow expects tileData indexed by: (z * height + y) * width + x
                        // In Table: tiles are stored by: z * (width * height) + y * width + x
                        // Both match: (z * height + y) * width + x == z * (width * height) + y * width + x!
                        tileData = table.Tiles;
                    }
                }

                // 이벤트 파싱
                if (mapObj["@events"] is Dictionary<object, object?> evDict)
                {
                    foreach (var kvp in evDict)
                    {
                        if (kvp.Value is RubyObject evObj)
                        {
                            int evId = (evObj["@id"] is int eid) ? eid : (kvp.Key is int kid ? kid : 0);
                            string evName = evObj["@name"]?.ToString() ?? $"EV{evId:D3}";
                            int evX = (evObj["@x"] is int ex) ? ex : 0;
                            int evY = (evObj["@y"] is int ey) ? ey : 0;

                            var evPages = new List<MapEventPage>();
                            if (evObj["@pages"] is List<object?> pagesList)
                            {
                                foreach (var p in pagesList)
                                {
                                    if (p is RubyObject pageObj)
                                    {
                                        int trigger = (pageObj["@trigger"] is int tr) ? tr : 0;
                                        var page = new MapEventPage { Trigger = trigger };
                                        // 장소 이동(코드 201, 직접 지정: [0, 맵ID, x, y, 방향])
                                        if (pageObj["@list"] is List<object?> cmds)
                                        {
                                            foreach (var c in cmds)
                                            {
                                                if (c is RubyObject cmd && cmd["@code"] is int code && code == 201 &&
                                                    cmd["@parameters"] is List<object?> ps && ps.Count >= 4 &&
                                                    ps[0] is int mode && mode == 0 && ps[1] is int tm && ps[2] is int tx && ps[3] is int ty)
                                                {
                                                    var t = new MapTransfer(tm, tx, ty);
                                                    if (!page.Transfers.Contains(t)) page.Transfers.Add(t);
                                                }
                                            }
                                        }
                                        evPages.Add(page);
                                    }
                                }
                            }

                            events.Add(new MapEventData
                            {
                                Id = evId,
                                Name = evName,
                                X = evX,
                                Y = evY,
                                Pages = evPages
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"ParseMap error: {ex.Message}");
        }

        return (width, height, tilesetId, tileData, events);
    }

    public static (List<SwitchItem> switches, List<VariableItem> variables) ParseSystem(byte[] systemBytes)
    {
        var switches = new List<SwitchItem>();
        var variables = new List<VariableItem>();

        try
        {
            var reader = new RubyMarshalReader(systemBytes);
            var root = reader.ReadObject();
            if (root is RubyObject sysObj)
            {
                if (sysObj["@switches"] is List<object?> swList)
                {
                    for (int i = 1; i < swList.Count; i++)
                    {
                        string name = swList[i]?.ToString() ?? "";
                        switches.Add(new SwitchItem { Id = i, Name = name, Value = false, IsFrozen = false });
                    }
                }
                if (sysObj["@variables"] is List<object?> varList)
                {
                    for (int i = 1; i < varList.Count; i++)
                    {
                        string name = varList[i]?.ToString() ?? "";
                        variables.Add(new VariableItem { Id = i, Name = name, Value = "0", IsFrozen = false });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"ParseSystem error: {ex.Message}");
        }

        return (switches, variables);
    }
}
