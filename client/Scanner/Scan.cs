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

    // Everything reads as double so one comparison path serves all widths.
    public static double Read(this Width w, ReadOnlySpan<byte> data, int offset) => w switch
    {
        Width.U8 => data[offset],
        Width.U16 => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2)),
        Width.U32 => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)),
        _ => BinaryPrimitives.ReadSingleBigEndian(data.Slice(offset, 4)),
    };

    // Most of a data segment reinterpreted as float is noise.
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

public static class Compare
{
    public static List<uint> Equals(Snapshot snap, Width w, double target)
    {
        var size = w.Size();
        var hits = new List<uint>();
        for (var off = 0; off + size <= snap.Data.Length; off += size)
            if (snap.Read(w, off) == target)
                hits.Add(snap.Base + (uint)off);
        return hits;
    }

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

    // For values shown as a countdown, percentage or bar, where the displayed
    // number is derived and searching for it directly finds nothing.
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

    // Finds a slot array filled one entry at a time, from three snapshots. For
    // items that do not stack there is no count to watch; the signature is two
    // different addresses taking the same value at different times:
    //   slot A: 0 -> id -> id      slot B: 0 -> 0 -> id
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
