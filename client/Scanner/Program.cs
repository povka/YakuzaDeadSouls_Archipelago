using System.Diagnostics;
using YakuzaDeadSouls.Ps3;
using YakuzaDeadSouls.Scanner;

// ydsscan <host> <command> [options]
//
//   snap <name>                    sweep the data segment and save it
//   eq <name> <value>              addresses equal to a value in a snapshot
//   delta <a> <b> <value>          values that changed by exactly this
//   filter <a> <b> <mode>          changed | unchanged | increased | decreased
//   slots <a> <b> <c>              slot-array fill pattern across three snaps
//
// Options: --width u8|u16|u32|f32 (default u32), --all (every width), --limit N

if (args.Length < 2) { Usage(); return 1; }
var host = args[0];
var command = args[1].ToLowerInvariant();
var rest = args[2..];

var widthArg = Opt("--width");
var allWidths = Flag("--all");
var limit = int.TryParse(Opt("--limit"), out var l) ? l : 20;
var widths = allWidths
    ? new[] { Width.U8, Width.U16, Width.U32, Width.F32 }
    : [widthArg is null ? Width.U32 : Widths.Parse(widthArg)];

var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "output");
outDir = Path.GetFullPath(outDir);
string SnapPath(string name) => Path.Combine(outDir, $"snap_{name}.bin");

try
{
    switch (command)
    {
        case "snap": return await Snap(Positional(0) ?? "default");
        case "eq": return Equals(Positional(0)!, double.Parse(Positional(1)!));
        case "delta": return Delta(Positional(0)!, Positional(1)!, double.Parse(Positional(2)!));
        case "filter": return Filter(Positional(0)!, Positional(1)!, Positional(2)!);
        case "slots": return Slots(Positional(0)!, Positional(1)!, Positional(2)!);
        default: Usage(); return 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"error: {ex.Message}");
    return 1;
}

async Task<int> Snap(string name)
{
    var pid = await Ps3Console.FindGameAsync(host);
    if (pid is null) { Console.WriteLine("No game running."); return 1; }

    using var client = new Ps3MapiClient(host);
    client.Connect();
    var game = new GameProcess(client, pid.Value);
    Console.WriteLine($"attached 0x{pid:X8}");

    const uint start = Addresses.DataBase, end = Addresses.DataEnd;
    var total = (int)(end - start);
    var buffer = new byte[total];
    var sw = Stopwatch.StartNew();
    var done = 0;
    var step = 0;
    while (done < total)
    {
        var want = Math.Min(Ps3MapiClient.MaxRead, total - done);
        game.Read(start + (uint)done, want).CopyTo(buffer, done);
        done += want;
        var pct = done * 10 / total;
        if (pct > step) { step = pct; Console.Write($"\r  sweeping {pct * 10,3}%"); }
    }
    sw.Stop();
    Console.WriteLine($"\r  swept {total / 1024.0 / 1024:F1} MB in {sw.Elapsed.TotalSeconds:F1}s " +
                      $"({total / 1024.0 / sw.Elapsed.TotalSeconds:F0} KB/s)");

    new Snapshot(start, buffer).Save(SnapPath(name));
    Console.WriteLine($"  saved '{name}'");
    return 0;
}

int Equals(string name, double value)
{
    var snap = Snapshot.Load(SnapPath(name));
    foreach (var w in widths)
    {
        var hits = Compare.Equals(snap, w, value);
        Console.WriteLine($"  {w,-4}: {hits.Count} hits equal to {value}");
        foreach (var a in hits.Take(limit)) Console.WriteLine($"      0x{a:X8}");
        if (hits.Count > limit) Console.WriteLine($"      ... and {hits.Count - limit} more");
    }
    return 0;
}

int Delta(string a, string b, double delta)
{
    var (before, after) = (Snapshot.Load(SnapPath(a)), Snapshot.Load(SnapPath(b)));
    foreach (var w in widths)
    {
        var hits = Compare.Delta(before, after, w, delta);
        Console.WriteLine($"  {w,-4}: {hits.Count} changed by exactly {delta}");
        foreach (var h in hits.Take(limit))
            Console.WriteLine($"      0x{h.Address:X8}  {h.Before} -> {h.After}");
        if (hits.Count > limit) Console.WriteLine($"      ... and {hits.Count - limit} more");
    }
    return 0;
}

int Filter(string a, string b, string mode)
{
    var m = Enum.Parse<Compare.Change>(mode, true);
    var (before, after) = (Snapshot.Load(SnapPath(a)), Snapshot.Load(SnapPath(b)));
    foreach (var w in widths)
    {
        var hits = Compare.Filter(before, after, w, m);
        Console.WriteLine($"  {w,-4}: {hits.Count} {mode}");
        foreach (var h in hits.Take(limit))
            Console.WriteLine($"      0x{h.Address:X8}  {h.Before} -> {h.After}");
        if (hits.Count > limit) Console.WriteLine($"      ... and {hits.Count - limit} more");
    }
    return 0;
}

int Slots(string a, string b, string c)
{
    var (s1, s2, s3) = (Snapshot.Load(SnapPath(a)), Snapshot.Load(SnapPath(b)), Snapshot.Load(SnapPath(c)));
    foreach (var w in widths)
    {
        var pairs = Compare.SlotFills(s1, s2, s3, w);
        Console.WriteLine($"  {w,-4}: {pairs.Count} slot-fill pairs");
        foreach (var p in pairs.Take(limit))
            Console.WriteLine($"      0x{p.First:X8} & 0x{p.Second:X8}  value={p.Value}  " +
                              $"gap=0x{(p.First > p.Second ? p.First - p.Second : p.Second - p.First):X}");
        if (pairs.Count > limit) Console.WriteLine($"      ... and {pairs.Count - limit} more");
    }
    return 0;
}

string? Opt(string name)
{
    var i = Array.IndexOf(rest, name);
    return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
}

bool Flag(string name) => Array.IndexOf(rest, name) >= 0;

string? Positional(int index)
{
    var seen = 0;
    for (var i = 0; i < rest.Length; i++)
    {
        if (rest[i].StartsWith("--")) { if (rest[i] != "--all") i++; continue; }
        if (seen++ == index) return rest[i];
    }
    return null;
}

void Usage()
{
    Console.WriteLine("""
        ydsscan <host> <command> [options]

          snap <name>                 sweep the data segment and save it
          eq <snap> <value>           addresses holding a value
          delta <a> <b> <value>       values that changed by exactly this
          filter <a> <b> <mode>       changed | unchanged | increased | decreased
          slots <a> <b> <c>           slot-array fill pattern across three snaps

        options:
          --width u8|u16|u32|f32      default u32
          --all                       run every width
          --limit N                   max hits to print (default 20)

        For a value with no number on screen (a health bar), snapshot either
        side of a change and use 'filter' or 'delta' with --all.
        """);
}
