namespace YakuzaDeadSouls.Ps3;

// Akiyama has two hostesses; Saaya (ids 1050/1051) belongs to Majima.
// Availability is a single byte, NOT the business card - the card is a receipt
// the game hands out and does not gate on. See notes/REVERSE.md.
public static class Hostesses
{
    public readonly record struct Hostess(
        string Name, string Club, uint AvailableFlag, ushort Card, ushort FancyCard);

    // Entries are 0x80 apart in a sparse flag table.
    public const uint FlagStride = 0x80;

    public static readonly Hostess Erika =
        new("Erika Mizushima", "Shine", 0x0153128F, KeyItems.ErikaCard, KeyItems.ErikaFancyCard);

    public static readonly Hostess Yuna =
        new("Yuna", "Jewel", 0x0153130F, KeyItems.YunaCard, KeyItems.YunaFancyCard);

    public static readonly Hostess[] Akiyama = [Yuna, Erika];

    public static bool IsAvailable(GameProcess game, in Hostess h) =>
        h.AvailableFlag != 0 && game.ReadU8(h.AvailableFlag) != 0;

    public static void SetAvailable(GameProcess game, in Hostess h, bool available)
    {
        if (h.AvailableFlag == 0) return;
        game.Write(h.AvailableFlag, [(byte)(available ? 1 : 0)]);
    }

    public static bool IsMaxed(GameProcess game, in Hostess h) =>
        KeyItems.Has(game, h.FancyCard);
}
