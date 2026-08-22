namespace YakuzaDeadSouls.Ps3;

public enum ItemClass
{
    Progression,
    Useful,
    Filler,
    Excluded,
}

public static class Abilities
{
    public const uint BitfieldBase = 0x01530210;
    public const int BitfieldBytes = 32;

    private static IReadOnlyList<string>? _names;

    public static IReadOnlyList<string> Names => _names ??= LoadNames();

    private static List<string> LoadNames()
    {
        var names = new List<string>();
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 7 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "data", "abilities.tsv");
            if (File.Exists(candidate))
            {
                foreach (var line in File.ReadLines(candidate))
                {
                    var tab = line.IndexOf('\t');
                    if (tab > 0) names.Add(line[(tab + 1)..].Trim());
                }
                break;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return names;
    }

    public static bool IsUnlocked(byte[] bitfield, int index) =>
        index >= 0 && index / 8 < bitfield.Length
        && (bitfield[index / 8] & (1 << (index % 8))) != 0;

    public static void SetUnlocked(byte[] bitfield, int index, bool value)
    {
        if (index < 0 || index / 8 >= bitfield.Length) return;
        if (value) bitfield[index / 8] |= (byte)(1 << (index % 8));
        else bitfield[index / 8] &= (byte)~(1 << (index % 8));
    }

    public static byte[] Read(IMemoryTarget target) =>
        target.ReadMemory(BitfieldBase, BitfieldBytes);

    public static void Write(IMemoryTarget target, byte[] bitfield) =>
        target.WriteMemory(BitfieldBase, bitfield);

    public static ItemClass Classify(int index, string name)
    {
        // TODO(human): decide how each ability enters the Archipelago item pool.
        return ItemClass.Filler;
    }
}
