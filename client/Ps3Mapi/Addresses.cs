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
    // REFUSED by RPCS3.
    public const uint ScratchBase = 0x01310768;

    public const uint Money = 0x01537E18;
    public const uint HealthCurrent = 0x0154BDB4;
    public const uint HealthMax = 0x0154BDB6;
    public const uint FocusCurrent = 0x0154BDB8;
    public const uint FocusMax = 0x0154BDBC;
    public const uint Exp = 0x0154BDCC;         // progress within the current level
    public const uint ExpTotal = 0x0154BDC8;    // cumulative; does not drive the display
    public const uint Level = 0x0154BDC4;       // u8
    public const uint SoulPoints = 0x0154BDD6;     // u8
    public const uint AmmoDisplay = 0x01536731; // HUD only; not what the gun fires
    public const uint StatsBase = 0x0154BDB0;

    // A decrypted USER01 is a verbatim dump of this RAM region:
    //   ramAddress = saveOffset + SaveToRam
    public const uint SaveToRam = 0x0152F2D0;

    public static uint FromSave(uint saveOffset) => saveOffset + SaveToRam;
    public static uint ToSave(uint ramAddress) => ramAddress - SaveToRam;
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
            if (!ushort.TryParse(line[..tab], out var id)) continue;

            var name = line[(tab + 1)..].Trim();
            if (name.Length > 0) names[id] = name;
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

    public static bool IsPlaceholder(string name) =>
        name.StartsWith("Dummy", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Temp ", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Important Dummy", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("End of", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Start of", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<KeyValuePair<ushort, string>> RealItems =>
        KnownItems.Where(kv => kv.Key != EmptyId && kv.Key != LockedId
                               && !IsPlaceholder(kv.Value));

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

    // Same 8-byte record format, immediately after the 24 inventory slots.
    // A 130-hour save fills slots 0-132 contiguously, so at least this many are
    // real; the key-item array does not begin until 0x0153540C, leaving room for
    // ~40 more that the game has never been observed to use.
    public const uint StorageBase = 0x01534EA4;
    public const int StorageSlots = 133;

    // Unlike the player inventory, storage stacks: the same save holds 2591
    // Submachine Gun Ammo in one slot.
    public static Item[] ReadStorage(GameProcess game, int slots = StorageSlots)
    {
        var raw = game.Read(StorageBase, slots * Stride);
        var items = new Item[slots];
        for (var i = 0; i < slots; i++)
        {
            var span = raw.AsSpan(i * Stride, Stride);
            items[i] = new Item(
                BinaryPrimitives.ReadUInt16BigEndian(span[..2]),
                BinaryPrimitives.ReadUInt32BigEndian(span[4..8]));
        }
        return items;
    }

    public static uint? Grant(GameProcess game, ushort itemId, uint quantity = 1)
    {
        var slot = FindFreeSlot(game);
        if (slot is null) return null;
        game.Write(slot.Value, MakeRecord(itemId, quantity));
        return slot;
    }

    public static uint? FindStorageSlot(GameProcess game, ushort itemId)
    {
        var items = ReadStorage(game);
        for (var i = 0; i < items.Length; i++)
            if (items[i].Id == itemId)
                return StorageBase + (uint)(i * Stride);
        for (var i = 0; i < items.Length; i++)
            if (items[i].IsEmpty)
                return StorageBase + (uint)(i * Stride);
        return null;
    }

    public static uint? GrantToStorage(GameProcess game, ushort itemId, uint quantity = 1)
    {
        var slot = FindStorageSlot(game, itemId);
        if (slot is null) return null;

        var existing = game.Read(slot.Value, Stride);
        var held = BinaryPrimitives.ReadUInt16BigEndian(existing) == itemId
            ? BinaryPrimitives.ReadUInt32BigEndian(existing.AsSpan(4))
            : 0;

        game.Write(slot.Value, MakeRecord(itemId, held + quantity));
        return slot;
    }

    // Player inventory first, storage box as overflow.
    public static uint? GrantAnywhere(GameProcess game, ushort itemId, uint quantity = 1) =>
        Grant(game, itemId, quantity) ?? GrantToStorage(game, itemId, quantity);
}
