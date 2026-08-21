using System.Buffers.Binary;

namespace YakuzaDeadSouls.Ps3;

// NPEB02034 (EU PSN digital), base game 01.00, no title update.
// All values big-endian. See notes/REVERSE.md.
public static class Addresses
{
    public const string GameId = "NPEB02034";
    public const string AppVersion = "01.00";

    public const uint EbootBase = 0x00010000;
    public const uint CodeBase = 0x00010000;
    public const uint CodeEnd = 0x01310768;
    public const uint DataBase = 0x01320000;
    public const uint DataEnd = 0x0172C408;

    // Unclaimed page padding between the segments. Writable on hardware,
    // REFUSED by RPCS3. For a write test that works on both, use ExpMirror.
    public const uint ScratchBase = 0x01310768;

    public const uint Money = 0x01537E18;
    public const uint HealthCurrent = 0x0154BDB4;
    public const uint HealthMax = 0x0154BDB6;
    public const uint Exp = 0x0154BDCC;
    public const uint ExpMirror = 0x0154BDC8;   // nothing reads this
    public const uint AmmoDisplay = 0x01536731; // HUD only; not what the gun fires
    public const uint StatsBase = 0x0154BDB0;

    public const uint Level1Threshold = 150;
}

// 8-byte records at stride 8: [u16 id][u16 pad][u32 quantity].
//   id 0, qty 0  unlocked and empty
//   id 1, qty 1  LOCKED slot - not an item
//   anything else is an item
public static class Inventory
{
    public const uint Header = 0x01534DE0;
    public const uint Base = 0x01534DE4;
    public const int Stride = 8;
    public const int Slots = 24;

    public const ushort EmptyId = 0;
    public const ushort LockedId = 1;

    public static readonly byte[] LockedRecord = [0, 1, 0, 0, 0, 0, 0, 1];
    public static readonly byte[] EmptyRecord = new byte[Stride];

    public readonly record struct Item(ushort Id, uint Quantity)
    {
        public bool IsEmpty => Id == EmptyId && Quantity == 0;
        public bool IsLocked => Id == LockedId;
        public bool IsItem => !IsEmpty && !IsLocked;
    }

    // Names come from data/items.tsv, dumped from the game's own name table
    // in memory. Ids 2-1127; the game marks its own category boundaries.
    public const ushort FirstId = 2;
    public const ushort LastId = 1127;
    public const ushort EndOfWeapons = 764;
    public const ushort EndOfArmor = 825;
    public const ushort StartOfAccessories = 826;
    public const ushort EndOfAccessories = 876;

    private static IReadOnlyDictionary<ushort, string>? _names;

    public static IReadOnlyDictionary<ushort, string> KnownItems => _names ??= LoadNames();

    private static Dictionary<ushort, string> LoadNames()
    {
        var names = new Dictionary<ushort, string>();
        var path = FindDataFile("items.tsv");
        if (path is null) return names;

        foreach (var line in File.ReadLines(path))
        {
            var tab = line.IndexOf('	');
            if (tab <= 0) continue;
            if (ushort.TryParse(line[..tab], out var id))
                names[id] = line[(tab + 1)..];
        }
        return names;
    }

    private static string? FindDataFile(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 7 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "data", name);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    // The table is padded with placeholders the game never uses. Keep them out
    // of any randomizer item pool.
    public static bool IsPlaceholder(string name) =>
        name.StartsWith("Dummy", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Temp ", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Important Dummy", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("End of", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Start of", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<KeyValuePair<ushort, string>> RealItems =>
        KnownItems.Where(kv => !IsPlaceholder(kv.Value));

    public static byte[] MakeRecord(ushort itemId, uint quantity = 1)
    {
        var record = new byte[Stride];
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(0, 2), itemId);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4, 4), quantity);
        return record;
    }

    public static Item[] Read(GameProcess game)
    {
        var raw = game.Read(Base, Slots * Stride);
        var items = new Item[Slots];
        for (var i = 0; i < Slots; i++)
        {
            var span = raw.AsSpan(i * Stride, Stride);
            items[i] = new Item(
                BinaryPrimitives.ReadUInt16BigEndian(span[..2]),
                BinaryPrimitives.ReadUInt32BigEndian(span[4..8]));
        }
        return items;
    }

    public static uint? FindFreeSlot(GameProcess game)
    {
        var items = Read(game);
        for (var i = 0; i < items.Length; i++)
            if (items[i].IsEmpty)
                return Base + (uint)(i * Stride);
        return null;
    }

    public static uint? Grant(GameProcess game, ushort itemId, uint quantity = 1)
    {
        var slot = FindFreeSlot(game);
        if (slot is null) return null;
        game.Write(slot.Value, MakeRecord(itemId, quantity));
        return slot;
    }
}
