using System.Buffers.Binary;

namespace YakuzaDeadSouls.Ps3;

// Flat array indexed by item id, 8-byte records in the Inventory layout.
// Reliable for ids >= FirstValidId; below that the offsets collide with the
// packed inventory and other structures. See notes/REVERSE.md.
public static class KeyItems
{
    public const uint Base = 0x015342DC;
    public const uint SaveBase = 0x0000500C;
    public const int Stride = 8;
    public const ushort FirstValidId = 550;

    public const ushort YunaCard = 1046;
    public const ushort YunaFancyCard = 1047;
    public const ushort ErikaCard = 1048;
    public const ushort ErikaFancyCard = 1049;
    public const ushort SaayaCard = 1050;
    public const ushort SaayaFancyCard = 1051;

    public static uint AddressOf(ushort itemId) => Base + (uint)(itemId * Stride);

    public static uint SaveOffsetOf(ushort itemId) => SaveBase + (uint)(itemId * Stride);

    public static bool Has(GameProcess game, ushort itemId)
    {
        var raw = game.Read(AddressOf(itemId), Stride);
        return BinaryPrimitives.ReadUInt16BigEndian(raw) == itemId
               && BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4)) > 0;
    }

    public static void Grant(GameProcess game, ushort itemId, uint quantity = 1) =>
        game.Write(AddressOf(itemId), Inventory.MakeRecord(itemId, quantity));

    private static bool OwnedIn(byte[] window, uint windowBase, ushort itemId)
    {
        var at = (int)(AddressOf(itemId) - windowBase);
        return BinaryPrimitives.ReadUInt16BigEndian(window.AsSpan(at, 2)) == itemId
               && BinaryPrimitives.ReadUInt32BigEndian(window.AsSpan(at + 4, 4)) > 0;
    }

    // Both fancy cards sit 16 bytes apart, so one read answers this instead of
    // two round trips. The client polls it every tick over a ~1.3 MB/s link.
    public static bool AkiyamaHostessesMaxed(GameProcess game)
    {
        var from = AddressOf(YunaFancyCard);
        var length = (int)(AddressOf(ErikaFancyCard) + Stride - from);
        var window = game.Read(from, length);
        return OwnedIn(window, from, YunaFancyCard) && OwnedIn(window, from, ErikaFancyCard);
    }
}
