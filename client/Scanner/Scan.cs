using System.Buffers.Binary;

namespace YakuzaDeadSouls.Scanner;

public enum Width { U8, U16, U32, F32 }

public static class Widths
{
    public static int Size(this Width w) => w switch
    {
        Width.U8 => 1,
        Width.U16 => 2,
        _ => 4,
    };

    /// <summary>Read a value as a double so every width shares one comparison path.</summary>
    public static double Read(this Width w, ReadOnlySpan<byte> data, int offset) => w switch
    {
        Width.U8 => data[offset],
        Width.U16 => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2)),
        Width.U32 => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)),
        _ => BinaryPrimitives.ReadSingleBigEndian(data.Slice(offset, 4)),
    };

    /// <summary>
    /// Most of a data segment reinterpreted as float is noise. Reject NaN,
    /// infinities and absurd magnitudes so float scans stay usable.
    /// </summary>
    public static bool Plausible(this Width w, double v) =>
        w != Width.F32 || (!double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < 1e9);

    public static Width Parse(string s) => s.ToLowerInvariant() switch
    {
        "u8" => Width.U8,
        "u16" => Width.U16,
        "u32" => Width.U32,
        "f32" => Width.F32,
        _ => throw new ArgumentException($"unknown width '{s}' (u8, u16, u32, f32)"),
    };
}

/// <summary>One full sweep of a memory region, held for offline comparison.</summary>
public sealed class Snapshot(uint baseAddress, byte[] data)
{
    public uint Base { get; } = baseAddress;
    public byte[] Data { get; } = data;

    public double Read(Width w, int offset) => w.Read(Data, offset);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var f = File.Create(path);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, Base);
        f.Write(header);
        f.Write(Data);
    }

    public static Snapshot Load(string path)
    {
        var raw = File.ReadAllBytes(path);
        var b = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(0, 4));
        return new Snapshot(b, raw[4..]);
    }
}

public readonly record struct Hit(uint Address, double Before, double After);

/// <summary>
/// Comparisons over snapshots. Everything works offline on captured bytes, so
/// one capture can be reinterpreted at every width - which matters because the
/// width of an unknown value is usually a guess, and guessing wrong otherwise
/// wastes a whole capture cycle.
/// </summary>
public static class Compare
{
    /// <summary>Values equal to a target in one snapshot.</summary>
    public static List<uint> Equals(Snapshot snap, Width w, double target)
    {
        var size = w.Size();
        var hits = new List<uint>();
        for (var off = 0; off + size <= snap.Data.Length; off += size)
            if (snap.Read(w, off) == target)
                hits.Add(snap.Base + (uint)off);
        return hits;
    }

    /// <summary>Narrow an existing candidate set to those now equal to a target.</summary>
    public static List<uint> Equals(Snapshot snap, Width w, double target, IEnumerable<uint> candidates)
    {
        var size = w.Size();
        var hits = new List<uint>();
        foreach (var addr in candidates)
        {
            var off = (int)(addr - snap.Base);
            if (off < 0 || off + size > snap.Data.Length) continue;
            if (snap.Read(w, off) == target) hits.Add(addr);
        }
        return hits;
    }

    public enum Change { Changed, Unchanged, Increased, Decreased }

    public static List<Hit> Filter(Snapshot before, Snapshot after, Width w, Change mode)
    {
        var size = w.Size();
        var limit = Math.Min(before.Data.Length, after.Data.Length);
        var hits = new List<Hit>();
        for (var off = 0; off + size <= limit; off += size)
        {
            var b = before.Read(w, off);
            var a = after.Read(w, off);
            if (!w.Plausible(b) || !w.Plausible(a)) continue;
            var keep = mode switch
            {
                Change.Changed => a != b,
                Change.Unchanged => a == b,
                Change.Increased => a > b,
                _ => a < b,
            };
            if (keep) hits.Add(new Hit(before.Base + (uint)off, b, a));
        }
        return hits;
    }

    /// <summary>
    /// Values that changed by exactly <paramref name="delta"/>.
    /// </summary>
    /// <remarks>
    /// The most useful search in this project. EXP is stored counting up while
    /// the UI shows "N to next level" as threshold - exp, so scanning for the
    /// displayed 150 -> 100 returned zero hits at every width. Searching for a
    /// delta of +50 found it immediately. Anything shown as a countdown, a
    /// percentage or a bar needs this rather than a value search.
    /// </remarks>
    public static List<Hit> Delta(Snapshot before, Snapshot after, Width w, double delta,
                                  double tolerance = 1e-3)
    {
        var size = w.Size();
        var limit = Math.Min(before.Data.Length, after.Data.Length);
        var hits = new List<Hit>();
        for (var off = 0; off + size <= limit; off += size)
        {
            var b = before.Read(w, off);
            var a = after.Read(w, off);
            if (!w.Plausible(b) || !w.Plausible(a)) continue;
            if (Math.Abs(a - b - delta) <= tolerance) hits.Add(new Hit(before.Base + (uint)off, b, a));
        }
        return hits;
    }

    public readonly record struct SlotPair(uint First, uint Second, double Value);

    /// <summary>
    /// Find a slot array being filled one entry at a time, from three
    /// snapshots taken before, after one acquisition, and after a second.
    /// </summary>
    /// <remarks>
    /// This is what found the inventory. Items in Dead Souls do not stack -
    /// two Tauriners occupy two records of quantity 1 - so there is no count
    /// going 1 -> 2 to search for. The signature is instead two <i>different</i>
    /// addresses receiving the <i>same</i> value at different times:
    /// <code>
    ///   slot A:  0 -> id -> id
    ///   slot B:  0 -> 0  -> id
    /// </code>
    /// <paramref name="maxGap"/> keeps only pairs close enough to plausibly be
    /// neighbouring records, which discards almost all coincidence.
    /// </remarks>
    public static List<SlotPair> SlotFills(Snapshot a, Snapshot b, Snapshot c, Width w,
                                           uint maxGap = 0x400)
    {
        var size = w.Size();
        var limit = Math.Min(Math.Min(a.Data.Length, b.Data.Length), c.Data.Length);

        var firstFills = new Dictionary<double, List<uint>>();
        var secondFills = new Dictionary<double, List<uint>>();

        for (var off = 0; off + size <= limit; off += size)
        {
            var x = a.Read(w, off);
            var y = b.Read(w, off);
            var z = c.Read(w, off);
            if (!w.Plausible(x) || !w.Plausible(y) || !w.Plausible(z)) continue;
            var addr = a.Base + (uint)off;

            if (x == 0 && y != 0 && z == y)
                (firstFills.TryGetValue(y, out var l1) ? l1 : firstFills[y] = []).Add(addr);
            else if (x == 0 && y == 0 && z != 0)
                (secondFills.TryGetValue(z, out var l2) ? l2 : secondFills[z] = []).Add(addr);
        }

        var pairs = new List<SlotPair>();
        foreach (var (value, firsts) in firstFills)
        {
            if (!secondFills.TryGetValue(value, out var seconds)) continue;
            foreach (var p in firsts)
                foreach (var q in seconds)
                {
                    var gap = p > q ? p - q : q - p;
                    if (gap > 0 && gap <= maxGap) pairs.Add(new SlotPair(p, q, value));
                }
        }
        return pairs;
    }
}
