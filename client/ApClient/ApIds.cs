using YakuzaDeadSouls.Ps3;

namespace YakuzaDeadSouls.ApClient;

// These ids MUST match world/yakuza_dead_souls/{Items,Locations}.py.
// Nothing enforces that at build time - changing one side silently desyncs the
// client from generated seeds.
public static class ApIds
{
    public const string GameName = "Yakuza Dead Souls";
    public const long BaseId = 8_960_000;

    public const int MaxTiersPerSong = 10;

    public const long ErikaCard = BaseId + 0;
    public const long YunaCard = BaseId + 1;
    public const long SubmachineGunAmmo = BaseId + 2;

    public const ushort SubmachineGunAmmoItemId = 29;

    public static long LocationId(int songId, int tierIndex) =>
        BaseId + songId * MaxTiersPerSong + tierIndex;

    public static IEnumerable<long> AllLocationIds()
    {
        for (var song = 0; song < Karaoke.SongCount; song++)
            for (var tier = 0; tier < Karaoke.ScoreTiers.Length; tier++)
                yield return LocationId(song, tier);
    }

    public static string Describe(long locationId)
    {
        var offset = locationId - BaseId;
        var song = (int)(offset / MaxTiersPerSong);
        var tier = (int)(offset % MaxTiersPerSong);
        var score = tier < Karaoke.ScoreTiers.Length ? Karaoke.ScoreTiers[tier] : -1;
        return $"song 0x{song:X2} @ {score}+";
    }
}
