using System.Buffers.Binary;

namespace YakuzaDeadSouls.Ps3;

/// <summary>
/// Confirmed memory addresses for Yakuza: Dead Souls, NPEB02034 (EU PSN
/// digital), base game <b>version 01.00</b> with no title update.
/// </summary>
/// <remarks>
/// <para>
/// Addresses are not portable to the disc releases, and a version mismatch
/// does <b>not</b> error - it reads plausible nonsense from the same offsets.
/// Check the version before trusting anything here.
/// </para>
/// <para>
/// Rule for this file: nothing goes in until the game has visibly reacted to
/// it. A scan candidate is not an address. Values that turned out to be
/// mirrors or display-only are kept and labelled rather than deleted, so they
/// are not rediscovered and trusted later.
/// </para>
/// </remarks>
public static class Addresses
{
    public const string GameId = "NPEB02034";
    public const string AppVersion = "01.00";

    // Segment layout, read live from the EBOOT's ELF program headers.
    public const uint EbootBase = 0x00010000;
    public const uint CodeBase = 0x00010000;
    public const uint CodeEnd = 0x01310768;   // RX, 19.0 MB
    public const uint DataBase = 0x01320000;
    public const uint DataEnd = 0x0172C408;   // RW, 4.0 MB - game state lives here

    /// <summary>
    /// Page-alignment padding between the code and data segments: 63,640 bytes
    /// no program header claims, reading as zeros. The safe place to test a
    /// write without risking game state.
    /// </summary>
    public const uint ScratchBase = 0x01310768;

    /// <summary>Yen. Confirmed: wrote 12345, HUD followed.</summary>
    public const uint Money = 0x01537E18;

    /// <summary>HP current / max, u16 each. The game only ever shows a bar.</summary>
    public const uint HealthCurrent = 0x0154BDB4;
    public const uint HealthMax = 0x0154BDB6;

    /// <summary>
    /// Accumulated experience, counting <b>up</b>. The "N to next level" on
    /// screen is computed as threshold - exp, so searching for the displayed
    /// number finds nothing.
    /// </summary>
    public const uint Exp = 0x0154BDCC;

    /// <summary>A copy of Exp that nothing reads. Writing here does nothing.</summary>
    public const uint ExpMirror = 0x0154BDC8;

    public const uint Level1Threshold = 150;

    /// <summary>
    /// The ammo number the HUD <b>shows</b> - not the ammo the gun has.
    /// Writing 99 made the UI read 99 while the weapon still reloaded after 13
    /// rounds. Cosmetic only; the real magazine count is not yet found.
    /// </summary>
    public const uint AmmoDisplay = 0x01536731;

    /// <summary>Character stats struct; HP and EXP live inside it.</summary>
    public const uint StatsBase = 0x0154BDB0;
}

/// <summary>
/// The player's inventory: 8-byte records at stride 8.
/// <c>[u16 id][u16 pad][u32 quantity]</c>
/// </summary>
/// <remarks>
/// <para>
/// Granting an item is <b>one 8-byte write</b> into a free slot. Nothing else
/// needs updating - the item-count bytes elsewhere in memory and the header at
/// <see cref="Header"/> both stayed stale after a successful grant, so they
/// are derived and the slot array is authoritative.
/// </para>
/// <para>
/// Items do <b>not</b> stack: buying two Tauriners produced two records of
/// quantity 1. That behaviour is what made the array findable at all.
/// </para>
/// </remarks>
public static class Inventory
{
    public const uint Header = 0x01534DE0;   // read 6 with 2-3 items; inert
    public const uint Base = 0x01534DE4;
    public const int Stride = 8;

    /// <summary>
    /// Slot count, deliberately conservative. <b>The real bound is unverified.</b>
    /// A different structure begins at 0x01534E40 - reading 64 slots ran into
    /// it and reported a dozen phantom "id=1 x1" items. That is harmless when
    /// reading, but <see cref="FindFreeSlot"/> would have handed back an
    /// address outside the array once the real slots filled up, and writing
    /// there would corrupt whatever lives at 0x01534E40.
    /// 0x01534E40 - Base = 92 bytes = 11.5 records, so 11 is the last slot
    /// that provably belongs to the inventory.
    /// </summary>
    public const int Slots = 11;

    public readonly record struct Item(ushort Id, uint Quantity)
    {
        public bool IsEmpty => Id == 0 && Quantity == 0;
    }

    /// <summary>Known item ids. Small and dense, so enumerable by writing and reading names.</summary>
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

    /// <summary>Read the whole array in one round trip.</summary>
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

    /// <summary>Address of the first free slot, or null if the array is full.</summary>
    public static uint? FindFreeSlot(GameProcess game)
    {
        var items = Read(game);
        for (var i = 0; i < items.Length; i++)
            if (items[i].IsEmpty)
                return Base + (uint)(i * Stride);
        return null;
    }

    /// <summary>Give the player an item. Returns the slot used, or null if full.</summary>
    public static uint? Grant(GameProcess game, ushort itemId, uint quantity = 1)
    {
        var slot = FindFreeSlot(game);
        if (slot is null) return null;
        game.Write(slot.Value, MakeRecord(itemId, quantity));
        return slot;
    }
}
