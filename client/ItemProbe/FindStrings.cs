using System.Text;
using YakuzaDeadSouls.Ps3;

namespace YakuzaDeadSouls.ItemProbe;

public static class FindStrings
{
    public static void Run(Rpcs3Target target, string needle)
    {
        var patterns = new (string Label, byte[] Bytes)[]
        {
            ("ascii", Encoding.ASCII.GetBytes(needle)),
            ("utf16le", Encoding.Unicode.GetBytes(needle)),
            ("utf16be", Encoding.BigEndianUnicode.GetBytes(needle)),
        };

        const int chunk = 4 * 1024 * 1024;
        const int overlap = 64;

        Console.WriteLine($"searching mapped guest memory for \"{needle}\" ...");
        var hits = new List<(uint Addr, string Kind)>();
        var scanned = 0L;
        var gaps = 0;

        foreach (var (regionStart, regionSize, _) in target.MappedGuestRegions())
        {
            var regionEnd = regionStart + (uint)Math.Min(regionSize, uint.MaxValue - regionStart);
            for (var at = regionStart; at < regionEnd; at += chunk - overlap)
            {
                var want = (int)Math.Min((ulong)chunk, regionEnd - at);
                if (want <= 0) break;
                byte[] buffer;
                try { buffer = target.ReadMemory(at, want); }
                catch (Ps3Exception) { gaps++; continue; }
                scanned += want;

                foreach (var (label, bytes) in patterns)
                {
                    var from = 0;
                    while (true)
                    {
                        var idx = IndexOf(buffer, bytes, from);
                        if (idx < 0) break;
                        hits.Add((at + (uint)idx, label));
                        from = idx + 1;
                        if (hits.Count > 200) break;
                    }
                }
                if (hits.Count > 200) break;
            }
            if (hits.Count > 200) break;
        }

        Console.WriteLine($"scanned {scanned / 1024.0 / 1024:F0} MB ({gaps} chunks unreadable), {hits.Count} hit(s)");
        foreach (var (addr, kind) in hits.Take(40))
            Console.WriteLine($"  0x{addr:X8}  {kind}");
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        var last = haystack.Length - needle.Length;
        for (var i = from; i <= last; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    public static void DumpTable(Rpcs3Target target, uint at, int count)
    {
        var raw = target.ReadMemory(at, 8192);
        var pos = 0;
        var shown = 0;
        Console.WriteLine($"strings from 0x{at:X8}:");
        while (pos < raw.Length && shown < count)
        {
            var end = Array.IndexOf(raw, (byte)0, pos);
            if (end < 0) break;
            var len = end - pos;
            if (len > 0)
            {
                var text = Encoding.ASCII.GetString(raw, pos, len);
                if (text.All(c => c >= 0x20 && c < 0x7F))
                {
                    Console.WriteLine($"  0x{at + (uint)pos:X8}  {text}");
                    shown++;
                }
            }
            pos = end + 1;
            while (pos < raw.Length && raw[pos] == 0) pos++;
        }
    }

    public static void DumpIds(Rpcs3Target target, uint at, ushort firstId, int count, bool asCode)
    {
        var raw = target.ReadMemory(at, 65536);
        var pos = 0;
        var id = firstId;
        var emitted = 0;

        while (pos < raw.Length && emitted < count)
        {
            var end = Array.IndexOf(raw, (byte)0, pos);
            if (end < 0) break;
            var text = Encoding.ASCII.GetString(raw, pos, end - pos);
            pos = end + 1;

            if (text.Length == 0) continue;
            if (!text.All(c => c >= 0x20 && c < 0x7F))
            {
                Console.WriteLine($"  -- stopped at id {id}: non-ascii entry --");
                break;
            }

            if (asCode)
                Console.WriteLine($"{id}	{text}");
            else
                Console.WriteLine($"  {id,4}  0x{at + (uint)(pos - text.Length - 1):X8}  {text}");
            id++;
            emitted++;
        }
        Console.WriteLine($"  -- {emitted} entries, ids {firstId}..{id - 1} --");
    }
}
