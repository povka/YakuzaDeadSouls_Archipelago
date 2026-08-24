using YakuzaDeadSouls.Ps3;

namespace YakuzaDeadSouls.ApClient;

// Single source of truth for every id, name and amount in the world.
// `ydsclient --emit-world <dir>` writes the Python mirror the apworld imports,
// so the two cannot drift. Change things here, never in Data.py.
public static class ApIds
{
    public const string GameName = "Yakuza Dead Souls";
    public const long BaseId = 8_960_000;

    // --- karaoke ---------------------------------------------------------

    // Index is the song id in the per-song score table at RAM 0x01547CD4.
    public static readonly string[] SongNames =
    [
        "Karaoke Song 00",
        "Karaoke Song 01",
        "Where Has Your Touch Gone?",
        "Karaoke Song 03",
        "Karaoke Song 04",
        "Karaoke Song 05",
        "Karaoke Song 06",
        "Karaoke Song 07",
        "Pure Love in Kamurocho",
        "Raindrops",
        "GET to the Top!",
    ];

    public static readonly int[] ScoreTiers = [550, 650, 750, 800, 850];

    // Id spacing per song, so adding a tier never shifts existing ids.
    public const int MaxTiersPerSong = 10;

    // --- locations -------------------------------------------------------

    public static long KaraokeLocationId(int songId, int tierIndex) =>
        BaseId + songId * MaxTiersPerSong + tierIndex;

    // Karaoke runs to BaseId + 109, so abilities start well clear.
    public const long AbilityBase = BaseId + 1000;

    public static long AbilityLocationId(int index) => AbilityBase + index;

    // --- items -----------------------------------------------------------

    public const long ErikaCard = BaseId + 0;
    public const long YunaCard = BaseId + 1;
    // BaseId + 2 is retired; it was a single-amount ammo item.

    public const long SoulPointsBase = BaseId + 3;
    public const int SoulPointsMin = 1;
    public const int SoulPointsMax = 10;

    // Soul points are Useful, not filler: the pool carries exactly enough to buy
    // every ability, so a seed is always completable but never generous.
    //
    // Until data/ability_bits.tsv carries per-ability costs, this is the observed
    // figure - the player started with 255, bought all 39, and had 16 left.
    public const int ObservedTotalAbilityCost = 239;

    // Enough items that a uniform 1-10 draw averages out to the total:
    // 239 / 5.5 is about 43.
    public const int SoulPointItemCount = 43;

    public static int TotalSoulPoints =>
        Abilities.CostsKnown ? Abilities.TotalCost : ObservedTotalAbilityCost;

    public const long AmmoBase = BaseId + 100;
    public const int AmmoMin = 1;
    public const int AmmoMax = 200;

    public const ushort SubmachineGunAmmoItemId = 29;

    // Ability items share the ability *location* base; items and locations are
    // separate id namespaces in Archipelago, so the overlap is harmless.
    public static long AbilityItemId(int index) => AbilityBase + index;

    // --- names -----------------------------------------------------------

    public static string SongName(int songId) =>
        songId >= 0 && songId < SongNames.Length ? SongNames[songId] : $"Karaoke Song {songId:D2}";

    public static string KaraokeLocationName(int songId, int tier) => $"{SongName(songId)}: {tier}+";

    public static string AbilityLocationName(string ability) => $"Ability: {ability}";

    public static string AmmoItemName(int rounds) => $"Submachine Gun Ammo ({rounds})";

    public static string SoulPointsItemName(int amount) => $"Soul Points ({amount})";

    // --- decoding what arrived -------------------------------------------

    public static int? AmmoAmount(long itemId)
    {
        var offset = itemId - AmmoBase;
        return offset >= 0 && offset <= AmmoMax - AmmoMin ? (int)offset + AmmoMin : null;
    }

    public static int? SoulPointsAmount(long itemId)
    {
        var offset = itemId - SoulPointsBase;
        return offset >= 0 && offset <= SoulPointsMax - SoulPointsMin
            ? (int)offset + SoulPointsMin
            : null;
    }

    public static int? AbilityIndexOfItem(long itemId)
    {
        var index = itemId - AbilityBase;
        return index >= 0 && index < Abilities.Count ? (int)index : null;
    }

    public static string Describe(long locationId)
    {
        if (locationId >= AbilityBase)
        {
            var index = (int)(locationId - AbilityBase);
            return index < Abilities.Count ? $"bought {Abilities.All[index].Name}" : $"ability {index}";
        }
        var offset = locationId - BaseId;
        var song = (int)(offset / MaxTiersPerSong);
        var tier = (int)(offset % MaxTiersPerSong);
        return tier < ScoreTiers.Length ? $"{SongName(song)} @ {ScoreTiers[tier]}+" : $"song {song}";
    }
}
