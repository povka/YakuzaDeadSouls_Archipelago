using System.Buffers.Binary;

namespace YakuzaDeadSouls.Ps3;

// Per-song records, 4 bytes each, indexed by song id. Each is bit-packed:
//   bits 31-20  high score      (0-1000)
//   bits 19-8   previous score
//   bits  7-0   flags
// The global leaderboard starts immediately after, at Base + SongCount*Stride.
public static class Karaoke
{
    public const uint Base = 0x01547CD4;
    public const uint SaveBase = 0x00018A04;
    public const int Stride = 4;
    public const int SongCount = 11;
    public const int MaxScore = 1000;

    public const int PureLoveInKamurocho = 0x08;
    public const int GetToTheTop = 0x0A;

    public readonly record struct Song(int Id, int HighScore, int PreviousScore, byte Flags)
    {
        public bool EverSung => HighScore > 0;
    }

    public static uint AddressOf(int songId) => Base + (uint)(songId * Stride);

    private static Song Decode(int songId, uint raw) =>
        new(songId, (int)(raw >> 20), (int)((raw >> 8) & 0xFFF), (byte)(raw & 0xFF));

    public static Song Read(GameProcess game, int songId) =>
        Decode(songId, BinaryPrimitives.ReadUInt32BigEndian(game.Read(AddressOf(songId), Stride)));

    // One read for the whole table - 44 bytes instead of 11 round trips.
    public static Song[] ReadAll(GameProcess game)
    {
        var raw = game.Read(Base, SongCount * Stride);
        var songs = new Song[SongCount];
        for (var i = 0; i < SongCount; i++)
            songs[i] = Decode(i, BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(i * Stride, Stride)));
        return songs;
    }

    public static readonly int[] ScoreTiers = [800, 850, 900];

    public static IEnumerable<int> ClearedTiers(Song song)
    {
        foreach (var tier in ScoreTiers)
            if (song.HighScore >= tier)
                yield return tier;
    }
}
