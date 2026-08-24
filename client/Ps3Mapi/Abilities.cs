using System.Buffers.Binary;
using System.Globalization;

namespace YakuzaDeadSouls.Ps3;

// Akiyama's 39 abilities live as bits across two big-endian u32 words:
//   0x0153020C  bits 2-20, 22-31
//   0x01530210  bits 0-9
// data/ability_bits.tsv is the source of truth for names AND ordering - the
// apworld bundles the same file, so the AP item/location ids stay in step.
public static class Abilities
{
    public const uint Base = 0x0153020C;
    public const int Bytes = 8;

    public readonly record struct Ability(int Index, uint Address, int Bit, string Name);

    private static IReadOnlyList<Ability>? _all;

    public static IReadOnlyList<Ability> All => _all ??= Load();

    public static int Count => All.Count;

    private static List<Ability> Load()
    {
        var list = new List<Ability>();
        var path = FindDataFile("ability_bits.tsv");
        if (path is null) return list;

        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            var text = parts[0].Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
                continue;
            if (!int.TryParse(parts[1].Trim(), out var bit)) continue;

            list.Add(new Ability(list.Count, address, bit, parts[2].Trim()));
        }
        return list;
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

    private static int WordOffset(in Ability ability) => (int)(ability.Address - Base);

    public static bool IsSet(byte[] window, in Ability ability)
    {
        var word = BinaryPrimitives.ReadUInt32BigEndian(window.AsSpan(WordOffset(ability), 4));
        return (word & (1u << ability.Bit)) != 0;
    }

    public static void Set(byte[] window, in Ability ability, bool on)
    {
        var at = WordOffset(ability);
        var word = BinaryPrimitives.ReadUInt32BigEndian(window.AsSpan(at, 4));
        word = on ? word | (1u << ability.Bit) : word & ~(1u << ability.Bit);
        BinaryPrimitives.WriteUInt32BigEndian(window.AsSpan(at, 4), word);
    }

    // One 8-byte read covers both words.
    public static byte[] Read(GameProcess game) => game.Read(Base, Bytes);

    public static void Write(GameProcess game, byte[] window) => game.Write(Base, window);
}
