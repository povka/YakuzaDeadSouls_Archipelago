using System.Buffers.Binary;
using YakuzaDeadSouls.Ps3;

namespace YakuzaDeadSouls.Scanner;

public static class Watch
{
    private const int SpanLimit = 65536;

    public static void Run(IMemoryTarget target, IReadOnlyList<uint> addresses,
                           int width, int intervalMs, bool console)
    {
        if (addresses.Count == 0) { Console.WriteLine("nothing to watch"); return; }

        var lo = addresses.Min();
        var hi = addresses.Max() + (uint)width;
        var span = (int)(hi - lo);
        var batched = span <= SpanLimit;

        Console.WriteLine($"watching {addresses.Count} address(es), width {width}, every {intervalMs} ms");
        Console.WriteLine(batched
            ? $"  one span read: 0x{lo:X8}-0x{hi:X8} ({span / 1024.0:F1} KB)"
            : $"  span is {span / 1024.0:F1} KB, too wide to batch - reading individually");
        Console.WriteLine("  ctrl-c to stop\n");

        var last = new Dictionary<uint, uint>();
        var ticks = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            var now = new Dictionary<uint, uint>();
            try
            {
                if (batched)
                {
                    var block = target.ReadMemory(lo, span);
                    foreach (var a in addresses)
                        now[a] = ReadAt(block, (int)(a - lo), width);
                }
                else
                {
                    foreach (var a in addresses)
                        now[a] = ReadAt(target.ReadMemory(a, width), 0, width);
                }
            }
            catch (Ps3Exception ex)
            {
                Console.WriteLine($"read failed: {ex.Message}");
                Thread.Sleep(intervalMs);
                continue;
            }

            foreach (var (addr, value) in now)
            {
                if (!last.TryGetValue(addr, out var before)) continue;
                if (before == value) continue;
                Console.WriteLine($"  [{sw.Elapsed:mm\\:ss}] 0x{addr:X8}  {before} -> {value}");
            }
            last = now;

            if (++ticks % 50 == 0)
                Console.WriteLine($"  ... {ticks} polls, {sw.Elapsed:mm\\:ss} elapsed, no change" +
                                  (console ? "" : " (rpcs3)"));

            Thread.Sleep(intervalMs);
        }
    }

    private static uint ReadAt(byte[] data, int offset, int width) => width switch
    {
        1 => data[offset],
        2 => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2)),
        _ => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)),
    };

    public static List<uint> LoadList(string path)
    {
        var result = new List<uint>();
        if (!File.Exists(path)) return result;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var token = line.Split(' ', '\t')[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            if (uint.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out var a))
                result.Add(a);
        }
        return result;
    }
}
