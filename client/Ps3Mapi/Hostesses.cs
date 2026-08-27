using System.Buffers.Binary;

namespace YakuzaDeadSouls.Ps3;

// Akiyama has two hostesses; Saaya (ids 1050/1051) belongs to Majima.
public static class Hostesses
{
    public readonly record struct Hostess(
        string Name, string Club, uint Record, ushort Card, ushort FancyCard);

    public const uint RecordStride = 0x80;
    public const int Availability = 0x00;
    public const int Progress = 0x05;
    public const byte StorylineComplete = 0x40;

    public static readonly Hostess Erika =
        new("Erika Mizushima", "Shine", 0x0153128C, KeyItems.ErikaCard, KeyItems.ErikaFancyCard);

    public static readonly Hostess Yuna =
        new("Yuna", "Jewel", 0x0153130C, KeyItems.YunaCard, KeyItems.YunaFancyCard);

    public static readonly Hostess[] Akiyama = [Yuna, Erika];

    public static bool IsAvailable(GameProcess game, in Hostess h) =>
        game.ReadU32(h.Record + Availability) != 0;

    // This u32 carries her progress as well as her availability, so anything but
    // 0 or 1 means she has been played and must not be overwritten in either
    // direction. Locking a progressed hostess is pointless anyway - she is
    // already started.
    public static void SetAvailable(GameProcess game, in Hostess h, bool available)
    {
        var current = game.ReadU32(h.Record + Availability);
        if (current > 1) return;
        if (current == (available ? 1u : 0u)) return;

        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, available ? 1u : 0u);
        game.Write(h.Record + Availability, buf);
    }

    public static bool IsStorylineComplete(GameProcess game, in Hostess h) =>
        (game.ReadU8(h.Record + Progress) & StorylineComplete) != 0;

    public static bool IsMaxed(GameProcess game, in Hostess h) =>
        KeyItems.Has(game, h.FancyCard);

    // Both records inside one read: Yuna sits RecordStride above Erika.
    public static bool AkiyamaStorylinesComplete(GameProcess game)
    {
        var from = Erika.Record;
        var window = game.Read(from, (int)(Yuna.Record + Progress + 1 - from));
        return (window[Progress] & StorylineComplete) != 0
               && (window[Yuna.Record - from + Progress] & StorylineComplete) != 0;
    }
}
