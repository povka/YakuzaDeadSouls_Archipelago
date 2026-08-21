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
public static class Inventory
{
    public const uint Header = 0x01534DE0;
    public const uint Base = 0x01534DE4;
    public const int Stride = 8;

    // A different structure starts at 0x01534E40, 92 bytes past Base. Raising
    // this lets FindFreeSlot return an address outside the array.
    public const int Slots = 11;

    public readonly record struct Item(ushort Id, uint Quantity)
    {
        public bool IsEmpty => Id == 0 && Quantity == 0;
    }

    public static readonly IReadOnlyDictionary<ushort, string> KnownItems =
        new Dictionary<ushort, string> { [11] = "Tauriner" };

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
