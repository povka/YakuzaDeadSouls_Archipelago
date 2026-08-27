using System.Buffers.Binary;
using System.Text;

namespace YakuzaDeadSouls.Ps3;

// Shops are reached through the loaded-resource table in static data. Nothing
// here may cache a heap address: the allocation moves between opens, and the
// resource slot index moves too. See notes/REVERSE.md.
public static class Shops
{
    // Resource table: one record per file the game currently has open.
    public const uint ResourceTable = 0x0160F0F0;
    public const int ResourceStride = 0x188;
    public const int ResourceCount = 64;
    public const int ResourceDataPtr = 0x30;
    public const int ResourcePath = 0x68;

    // Parsed contents of shopNNNN.bin. NOT what the screen draws.
    public const int HeaderCount = 0x08;
    public const int HeaderTable = 0x0C;
    public const int EntryStride = 0x20;

    // Built when the Buy menu opens. This IS what the screen draws.
    public const int RowStride = 0x38;
    public const int RowItemId = 0x08;
    public const int RowPrice = 0x14;
    public const int RowName = 0x2C;
    public const int RowDescription = 0x30;

    public readonly record struct Shop(
        string File, uint Header, uint EntryTable, uint DisplayList, int SlotCount);

    private static string ReadPath(byte[] block, int at)
    {
        var end = at;
        while (end < block.Length && block[end] != 0 && end - at < 128) end++;
        return Encoding.ASCII.GetString(block, at, end - at);
    }

    // Returns null when no shop is open.
    public static Shop? Find(GameProcess game)
    {
        var table = game.Read(ResourceTable, ResourceStride * ResourceCount);
        for (var i = 0; i < ResourceCount; i++)
        {
            var at = i * ResourceStride;
            var path = ReadPath(table, at + ResourcePath);
            if (!path.Contains("/wdr/shop/", StringComparison.Ordinal)) continue;

            var header = BinaryPrimitives.ReadUInt32BigEndian(
                table.AsSpan(at + ResourceDataPtr, 4));
            if (header < 0x30000000 || header >= 0x40000000) continue;

            var head = game.Read(header, 16);
            var count = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(HeaderCount, 2));
            var entries = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(HeaderTable, 4));
            if (count is 0 or > 200 || entries < 0x30000000 || entries >= 0x40000000) continue;

            var display = FindDisplayList(game, entries, count);
            if (display is null) continue;

            return new Shop(Path.GetFileName(path), header, entries, display.Value, count);
        }
        return null;
    }

    // The display list is allocated near the parsed table. Match it by its item
    // ids agreeing with the parsed table's, in order - the ids alone are not
    // distinctive enough, since every id-shaped table in this game holds them.
    private static uint? FindDisplayList(GameProcess game, uint entryTable, int count)
    {
        var probe = Math.Min(count, 6);
        var parsed = game.Read(entryTable, probe * EntryStride);
        var want = new ushort[probe];
        for (var i = 0; i < probe; i++)
            want[i] = BinaryPrimitives.ReadUInt16BigEndian(parsed.AsSpan(i * EntryStride, 2));

        const uint before = 0x2000, after = 0x6000;
        var from = entryTable - before;
        var window = game.Read(from, (int)(before + after));

        for (var off = 0; off + probe * RowStride < window.Length; off += 4)
        {
            var ok = true;
            for (var r = 0; r < probe; r++)
            {
                var at = off + r * RowStride + RowItemId;
                if (BinaryPrimitives.ReadUInt16BigEndian(window.AsSpan(at, 2)) != want[r])
                {
                    ok = false; break;
                }
            }
            if (ok) return from + (uint)off;
        }
        return null;
    }

    // One read for the whole list. Reading per slot would open 37 PASV data
    // connections a tick, which is what the console refuses under load.
    public static ushort[] ItemIds(GameProcess game, in Shop shop, int slots)
    {
        var raw = game.Read(shop.DisplayList, slots * RowStride);
        var ids = new ushort[slots];
        for (var i = 0; i < slots; i++)
            ids[i] = BinaryPrimitives.ReadUInt16BigEndian(
                raw.AsSpan(i * RowStride + RowItemId, 2));
        return ids;
    }

    public static uint[] DescriptionPointers(GameProcess game, in Shop shop, int slots)
    {
        var raw = game.Read(shop.DisplayList, slots * RowStride);
        var ptrs = new uint[slots];
        for (var i = 0; i < slots; i++)
            ptrs[i] = BinaryPrimitives.ReadUInt32BigEndian(
                raw.AsSpan(i * RowStride + RowDescription, 4));
        return ptrs;
    }

    public static void SetPrice(GameProcess game, in Shop shop, int slot, uint price)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, price);
        game.Write(shop.DisplayList + (uint)(slot * RowStride) + RowPrice, buf);
    }

    // Name strings sit in a packed pool with no spare bytes, so the replacement
    // is written into that row's description buffer (60-90 bytes) and the name
    // pointer aimed at it. The description is lost, which is fine - it described
    // an item the player is no longer buying.
    public static void SetName(GameProcess game, in Shop shop, int slot, string name,
                               uint descriptionPointer = 0)
    {
        var row = shop.DisplayList + (uint)(slot * RowStride);
        var target = descriptionPointer != 0
            ? descriptionPointer
            : BinaryPrimitives.ReadUInt32BigEndian(game.Read(row + RowDescription, 4));
        if (target < 0x30000000 || target >= 0x40000000) return;

        var text = Encoding.UTF8.GetBytes(name);
        var buf = new byte[Math.Min(text.Length + 1, 96)];
        text.AsSpan(0, buf.Length - 1).CopyTo(buf);
        game.Write(target, buf);

        var ptr = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(ptr, target);
        game.Write(row + RowName, ptr);
    }
}
