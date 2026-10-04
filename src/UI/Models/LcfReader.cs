#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RocketRPG.Views;

namespace RocketRPG.Models;

public class LcfMapData
{
    public int Width { get; set; } = 20;
    public int Height { get; set; } = 15;
    public int ChipsetId { get; set; } = 1;
    public int[] TileData { get; set; } = Array.Empty<int>();
    public List<MapEventData> Events { get; set; } = new();

    public int GetTile(int layer, int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return 0;
        int idx = (layer * Height + y) * Width + x;
        if (idx >= 0 && idx < TileData.Length) return TileData[idx];
        return 0;
    }

    public int GetTopTile(int x, int y)
    {
        int upper = GetTile(1, x, y);
        if (upper > 0 && upper != 10000) return upper;
        return GetTile(0, x, y);
    }
}

public class LcfChipset
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string GraphicName { get; set; } = "";
}

public class LcfDatabaseData
{
    public List<SwitchItem> Switches { get; set; } = new();
    public List<VariableItem> Variables { get; set; } = new();
    public List<LcfChipset> Chipsets { get; set; } = new();
    public string GameTitle { get; set; } = "";
    public int StartMapId { get; set; } = 1;
    public int StartX { get; set; }
    public int StartY { get; set; }
}

/// <summary>
/// High-performance binary parser for RPG Maker 2000 / 2003 LCF formats (RPG_RT.ldb, RPG_RT.lmt, MapXXXX.lmu).
/// Implements standard BER-compressed integer chunk decoding with UTF-8 / Shift-JIS / CP949 text handling.
/// </summary>
public static class LcfReader
{
    private static readonly byte[] LdbHeader = { 0x0B, 0x4C, 0x63, 0x66, 0x44, 0x61, 0x74, 0x61, 0x42, 0x61, 0x73, 0x65 }; // \x0bLcfDataBase
    private static readonly byte[] LmtHeader = { 0x0A, 0x4C, 0x63, 0x66, 0x4D, 0x61, 0x70, 0x54, 0x72, 0x65, 0x65 };       // \x0aLcfMapTree
    private static readonly byte[] LmuHeader = { 0x0A, 0x4C, 0x63, 0x66, 0x4D, 0x61, 0x70, 0x55, 0x6E, 0x69, 0x74 };       // \x0aLcfMapUnit
    private static readonly byte[] LsdHeader = { 0x0B, 0x4C, 0x63, 0x66, 0x53, 0x61, 0x76, 0x65, 0x44, 0x61, 0x74, 0x61 }; // \x0bLcfSaveData

    public static bool IsLdb(byte[] data) => MatchesHeader(data, LdbHeader);
    public static bool IsLmt(byte[] data) => MatchesHeader(data, LmtHeader);
    public static bool IsLmu(byte[] data) => MatchesHeader(data, LmuHeader);
    public static bool IsLsd(byte[] data) => MatchesHeader(data, LsdHeader);

    private static bool MatchesHeader(byte[] data, byte[] expected)
    {
        if (data == null || data.Length < expected.Length) return false;
        for (int i = 0; i < expected.Length; i++)
        {
            if (data[i] != expected[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// Reads a variable-length BER compressed integer.
    /// </summary>
    public static int ReadBer(byte[] data, ref int pos)
    {
        int val = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            val = (val << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return val;
    }

    public static int ReadBer(Stream stream)
    {
        int val = 0;
        while (true)
        {
            int b = stream.ReadByte();
            if (b == -1) break;
            val = (val << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return val;
    }

    public static int ReadChunkInt(byte[] data, int pos, int len)
    {
        if (data == null || pos >= data.Length || len <= 0) return 0;
        if (len == 1) return data[pos];

        int p = pos;
        int berVal = ReadBer(data, ref p);
        if (p - pos == len)
        {
            return berVal;
        }

        if (len == 2 && pos + 1 < data.Length) return BitConverter.ToUInt16(data, pos);
        if (len == 4 && pos + 3 < data.Length) return BitConverter.ToInt32(data, pos);
        return berVal;
    }

    private static bool IsLikelyShiftJis(byte[] bytes)
    {
        int korScore = 0;
        int sjisScore = 0;

        for (int i = 0; i < bytes.Length - 1; i++)
        {
            byte b1 = bytes[i];
            byte b2 = bytes[i + 1];

            // Korean KS C 5601 common Hangul syllables (0xB0..0xC8, 0xA1..0xFE)
            if (b1 >= 0xB0 && b1 <= 0xC8 && b2 >= 0xA1 && b2 <= 0xFE)
            {
                korScore += 2;
                i++;
                continue;
            }

            // Shift-JIS punctuation & symbols: 0x81 [0x40..0x7E, 0x80..0xAC]
            if (b1 == 0x81 && ((b2 >= 0x40 && b2 <= 0x7E) || (b2 >= 0x80 && b2 <= 0xAC)))
            {
                sjisScore += 3;
                i++;
                continue;
            }

            // Hiragana: 0x82 [0x9F..0xF1]
            if (b1 == 0x82 && b2 >= 0x9F && b2 <= 0xF1)
            {
                sjisScore += 3;
                i++;
                continue;
            }

            // Katakana: 0x83 [0x40..0x96] (excluding 0x7F)
            if (b1 == 0x83 && b2 >= 0x40 && b2 <= 0x96 && b2 != 0x7F)
            {
                sjisScore += 3;
                i++;
                continue;
            }

            // Shift-JIS Kanji with low-trail byte (0x40..0x7E), which cannot be KS C 5601 Korean
            if (((b1 >= 0x88 && b1 <= 0x9F) || (b1 >= 0xE0 && b1 <= 0xEA)) && b2 >= 0x40 && b2 <= 0x7E && b2 != 0x7F)
            {
                sjisScore += 2;
                i++;
                continue;
            }
        }

        return sjisScore > korScore;
    }

    public static string DecodeLcfString(byte[] bytes, int offset = 0, int length = -1)
    {
        if (bytes == null || bytes.Length == 0) return "";
        if (length < 0) length = bytes.Length - offset;
        if (length <= 0 || offset >= bytes.Length) return "";

        byte[] sub = new byte[length];
        Buffer.BlockCopy(bytes, offset, sub, 0, length);

        try
        {
            var utf8 = new UTF8Encoding(false, true);
            return utf8.GetString(sub);
        }
        catch
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            if (IsLikelyShiftJis(sub))
            {
                try
                {
                    return Encoding.GetEncoding(932).GetString(sub);
                }
                catch { }
            }

            try
            {
                return Encoding.GetEncoding(949).GetString(sub);
            }
            catch
            {
                try
                {
                    return Encoding.GetEncoding(932).GetString(sub);
                }
                catch
                {
                    return Encoding.Latin1.GetString(sub);
                }
            }
        }
    }

    /// <summary>
    /// Parses RPG_RT.lmt Map Tree file and returns MapListItem collection for MapViewer.
    /// </summary>
    public static List<MapListItem> ParseMapTree(byte[] lmtData)
    {
        var result = new List<MapListItem>();
        if (!IsLmt(lmtData)) return result;

        int pos = LmtHeader.Length;
        if (pos >= lmtData.Length) return result;

        int mapCount = ReadBer(lmtData, ref pos);
        for (int i = 0; i < mapCount && pos < lmtData.Length; i++)
        {
            int mapId = ReadBer(lmtData, ref pos);
            string name = "";
            int parentId = 0;
            int order = 0;
            int type = 1;

            while (pos < lmtData.Length)
            {
                int tag = ReadBer(lmtData, ref pos);
                if (tag == 0) break;
                int len = ReadBer(lmtData, ref pos);

                if (pos + len > lmtData.Length)
                {
                    pos = lmtData.Length;
                    break;
                }

                if (tag == 1) // Name
                {
                    name = DecodeLcfString(lmtData, pos, len);
                }
                else if (tag == 2) // Parent ID
                {
                    parentId = ReadChunkInt(lmtData, pos, len);
                }
                else if (tag == 3) // Indentation / Order
                {
                    order = ReadChunkInt(lmtData, pos, len);
                }
                else if (tag == 4) // Type (1 = Map, 2 = Area)
                {
                    type = ReadChunkInt(lmtData, pos, len);
                }

                pos += len;
            }

            // Exclude root project entry (ID 0) and area-only entries
            if (mapId > 0 && type != 2)
            {
                result.Add(new MapListItem
                {
                    Id = mapId,
                    Name = name,
                    ParentId = parentId,
                    Order = order
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Parses MapXXXX.lmu file and returns map dimensions, tile layers, and event objects.
    /// </summary>
    public static LcfMapData ParseMapUnit(byte[] lmuData)
    {
        var result = new LcfMapData();
        if (!IsLmu(lmuData)) return result;

        int pos = LmuHeader.Length;
        int width = 0;
        int height = 0;
        int chipsetId = 1;
        byte[]? lowerLayer = null;
        byte[]? upperLayer = null;

        while (pos < lmuData.Length)
        {
            int tag = ReadBer(lmuData, ref pos);
            if (tag == 0) break;
            int len = ReadBer(lmuData, ref pos);

            if (pos + len > lmuData.Length)
            {
                pos = lmuData.Length;
                break;
            }

            switch (tag)
            {
                case 1: // Chipset ID
                    chipsetId = ReadChunkInt(lmuData, pos, len);
                    break;
                case 2: // Width
                    width = ReadChunkInt(lmuData, pos, len);
                    break;
                case 3: // Height
                    height = ReadChunkInt(lmuData, pos, len);
                    break;
                case 71: // Lower Layer Tiles (0x47)
                    lowerLayer = new byte[len];
                    Buffer.BlockCopy(lmuData, pos, lowerLayer, 0, len);
                    break;
                case 72: // Upper Layer Tiles (0x48)
                    upperLayer = new byte[len];
                    Buffer.BlockCopy(lmuData, pos, upperLayer, 0, len);
                    break;
                case 81: // Events (0x51)
                    ParseEvents(lmuData, pos, len, result.Events);
                    break;
            }

            pos += len;
        }

        // Handle default / omitted dimensions
        int tileCount = 0;
        if (lowerLayer != null) tileCount = lowerLayer.Length / 2;
        else if (upperLayer != null) tileCount = upperLayer.Length / 2;

        if (width <= 0) width = 20;
        if (height <= 0)
        {
            height = (tileCount > 0 && width > 0) ? (tileCount / width) : 15;
            if (height <= 0) height = 15;
        }

        result.Width = width;
        result.Height = height;
        result.ChipsetId = chipsetId;

        int totalTilesPerLayer = width * height;
        var combinedTiles = new int[totalTilesPerLayer * 2];

        // Layer 0: Lower Layer
        if (lowerLayer != null)
        {
            for (int i = 0; i < Math.Min(totalTilesPerLayer, lowerLayer.Length / 2); i++)
            {
                combinedTiles[i] = BitConverter.ToUInt16(lowerLayer, i * 2);
            }
        }

        // Layer 1: Upper Layer
        if (upperLayer != null)
        {
            for (int i = 0; i < Math.Min(totalTilesPerLayer, upperLayer.Length / 2); i++)
            {
                combinedTiles[totalTilesPerLayer + i] = BitConverter.ToUInt16(upperLayer, i * 2);
            }
        }

        result.TileData = combinedTiles;
        return result;
    }

    private static void ParseEvents(byte[] data, int chunkOffset, int chunkLen, List<MapEventData> outEvents)
    {
        int pos = chunkOffset;
        int end = chunkOffset + chunkLen;
        if (pos >= end) return;

        int eventCount = ReadBer(data, ref pos);
        for (int i = 0; i < eventCount && pos < end; i++)
        {
            int eventId = ReadBer(data, ref pos);
            var ev = new MapEventData { Id = eventId, Pages = new List<MapEventPage>() };

            while (pos < end)
            {
                int tag = ReadBer(data, ref pos);
                if (tag == 0) break;
                int len = ReadBer(data, ref pos);

                if (pos + len > end) { pos = end; break; }

                if (tag == 1) // Name
                {
                    ev.Name = DecodeLcfString(data, pos, len);
                }
                else if (tag == 2) // X
                {
                    ev.X = ReadChunkInt(data, pos, len);
                }
                else if (tag == 3) // Y
                {
                    ev.Y = ReadChunkInt(data, pos, len);
                }
                else if (tag == 5) // Pages
                {
                    ParseEventPages(data, pos, len, ev.Pages);
                }

                pos += len;
            }

            outEvents.Add(ev);
        }
    }

    private static void ParseEventPages(byte[] data, int pageOffset, int pageLen, List<MapEventPage> outPages)
    {
        int pos = pageOffset;
        int end = pageOffset + pageLen;
        if (pos >= end) return;

        int pageCount = ReadBer(data, ref pos);
        for (int i = 0; i < pageCount && pos < end; i++)
        {
            int pageId = ReadBer(data, ref pos);
            var page = new MapEventPage();

            while (pos < end)
            {
                int tag = ReadBer(data, ref pos);
                if (tag == 0) break;
                int len = ReadBer(data, ref pos);

                if (pos + len > end) { pos = end; break; }

                if (tag == 33) // Trigger (0x21): 0=Action, 1=Player Touch, 2=Event Touch, 3=Autorun, 4=Parallel
                {
                    page.Trigger = ReadChunkInt(data, pos, len);
                }
                else if (tag == 52) // 이벤트 명령 목록 (0x34)
                {
                    ParseTransfers(data, pos, len, page.Transfers);
                }

                pos += len;
            }

            outPages.Add(page);
        }
    }

    /// <summary>
    /// 이벤트 명령 목록에서 "장소 이동"(10810: 맵ID, x, y, 방향)을 찾습니다.
    /// 명령 하나 = 코드, 들여쓰기, 문자열 길이+바이트, 인수 개수, 인수들 (모두 BER).
    /// </summary>
    private static void ParseTransfers(byte[] data, int offset, int len, List<MapTransfer> outTransfers)
    {
        int pos = offset, end = offset + len;
        try
        {
            while (pos < end)
            {
                int code = ReadBer(data, ref pos);
                if (pos >= end) break;
                ReadBer(data, ref pos);                    // indent
                int slen = ReadBer(data, ref pos);
                pos += slen;
                if (pos > end) break;
                int argc = ReadBer(data, ref pos);
                if (argc < 0 || argc > 4096) break;
                var args = new int[argc];
                for (int i = 0; i < argc && pos < end; i++) args[i] = ReadBer(data, ref pos);
                if (code == 10810 && argc >= 3)
                {
                    var t = new MapTransfer(args[0], args[1], args[2]);
                    if (!outTransfers.Contains(t)) outTransfers.Add(t);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Parses RPG_RT.ldb Database file and returns switches, variables, chipsets, and system metadata.
    /// </summary>
    public static LcfDatabaseData ParseDatabase(byte[] ldbData)
    {
        var result = new LcfDatabaseData();
        if (!IsLdb(ldbData)) return result;

        int pos = LdbHeader.Length;
        while (pos < ldbData.Length)
        {
            int tag = ReadBer(ldbData, ref pos);
            if (tag == 0) break;
            int len = ReadBer(ldbData, ref pos);

            if (pos + len > ldbData.Length)
            {
                pos = ldbData.Length;
                break;
            }

            switch (tag)
            {
                case 20: // ChipSets (0x14)
                    ParseChipsets(ldbData, pos, len, result.Chipsets);
                    break;
                case 22: // System (0x16)
                    ParseSystem(ldbData, pos, len, result);
                    break;
                case 23: // Switches (0x17)
                    ParseSwitches(ldbData, pos, len, result.Switches);
                    break;
                case 24: // Variables (0x18)
                    ParseVariables(ldbData, pos, len, result.Variables);
                    break;
            }

            pos += len;
        }

        return result;
    }

    private static void ParseSwitches(byte[] data, int offset, int len, List<SwitchItem> outSwitches)
    {
        int pos = offset;
        int end = offset + len;
        if (pos >= end) return;

        int count = ReadBer(data, ref pos);
        for (int i = 0; i < count && pos < end; i++)
        {
            int id = ReadBer(data, ref pos);
            string name = "";

            while (pos < end)
            {
                int tag = ReadBer(data, ref pos);
                if (tag == 0) break;
                int slen = ReadBer(data, ref pos);

                if (pos + slen > end) { pos = end; break; }

                if (tag == 1) // Switch Name
                {
                    name = DecodeLcfString(data, pos, slen);
                }

                pos += slen;
            }

            outSwitches.Add(new SwitchItem
            {
                Id = id,
                Name = name,
                Value = false,
                IsFrozen = false
            });
        }
    }

    private static void ParseVariables(byte[] data, int offset, int len, List<VariableItem> outVariables)
    {
        int pos = offset;
        int end = offset + len;
        if (pos >= end) return;

        int count = ReadBer(data, ref pos);
        for (int i = 0; i < count && pos < end; i++)
        {
            int id = ReadBer(data, ref pos);
            string name = "";

            while (pos < end)
            {
                int tag = ReadBer(data, ref pos);
                if (tag == 0) break;
                int slen = ReadBer(data, ref pos);

                if (pos + slen > end) { pos = end; break; }

                if (tag == 1) // Variable Name
                {
                    name = DecodeLcfString(data, pos, slen);
                }

                pos += slen;
            }

            outVariables.Add(new VariableItem
            {
                Id = id,
                Name = name,
                Value = "0",
                IsFrozen = false
            });
        }
    }

    private static void ParseChipsets(byte[] data, int offset, int len, List<LcfChipset> outChipsets)
    {
        int pos = offset;
        int end = offset + len;
        if (pos >= end) return;

        int count = ReadBer(data, ref pos);
        for (int i = 0; i < count && pos < end; i++)
        {
            int id = ReadBer(data, ref pos);
            var cs = new LcfChipset { Id = id };

            while (pos < end)
            {
                int tag = ReadBer(data, ref pos);
                if (tag == 0) break;
                int slen = ReadBer(data, ref pos);

                if (pos + slen > end) { pos = end; break; }

                if (tag == 1) // Chipset Name
                {
                    cs.Name = DecodeLcfString(data, pos, slen);
                }
                else if (tag == 2) // Chipset Graphic Name
                {
                    cs.GraphicName = DecodeLcfString(data, pos, slen);
                }

                pos += slen;
            }

            outChipsets.Add(cs);
        }
    }

    private static void ParseSystem(byte[] data, int offset, int len, LcfDatabaseData db)
    {
        int pos = offset;
        int end = offset + len;
        if (pos >= end) return;

        while (pos < end)
        {
            int tag = ReadBer(data, ref pos);
            if (tag == 0) break;
            int slen = ReadBer(data, ref pos);

            if (pos + slen > end) { pos = end; break; }

            if (tag == 1) // Game Title
            {
                db.GameTitle = DecodeLcfString(data, pos, slen);
            }
            else if (tag == 11) // Start Map ID
            {
                db.StartMapId = ReadChunkInt(data, pos, slen);
            }
            else if (tag == 12) // Start X
            {
                db.StartX = ReadChunkInt(data, pos, slen);
            }
            else if (tag == 13) // Start Y
            {
                db.StartY = ReadChunkInt(data, pos, slen);
            }

            pos += slen;
        }
    }
}
