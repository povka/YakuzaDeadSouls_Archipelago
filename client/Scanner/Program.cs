using System.Diagnostics;
using YakuzaDeadSouls.Ps3;
using YakuzaDeadSouls.Scanner;

if (args.Length < 1) { Usage(); return 1; }

var command = args[0].ToLowerInvariant();
var rest = args[1..];

var widthArg = Opt("--width");
var allWidths = Flag("--all");
var limit = int.TryParse(Opt("--limit"), out var parsed) ? parsed : 20;
var widths = allWidths
    ? new[] { Width.U8, Width.U16, Width.U32, Width.F32 }
    : [widthArg is null ? Width.U32 : Widths.Parse(widthArg)];

var outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "output"));
string SnapPath(string name) => Path.Combine(outDir, $"snap_{name}.bin");

try
{
    return command switch
    {
        "snap" => await Snap(Positional(0) ?? "default"),
        "eq" => Equal(Positional(0)!, double.Parse(Positional(1)!)),
        "delta" => Delta(Positional(0)!, Positional(1)!, double.Parse(Positional(2)!)),
        "filter" => Filter(Positional(0)!, Positional(1)!, Positional(2)!),
        "slots" => Slots(Positional(0)!, Positional(1)!, Positional(2)!),
        "list" => List(),
        _ => Unknown(),
    };
}
catch (Exception ex)
{
    Console.WriteLine($"error: {ex.Message}");
    return 1;
}

int Unknown() { Usage(); return 1; }

async Task<int> Snap(string name)
{
    var host = Ps3Config.Require(Opt("--host"));
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

int List()
{
    if (!Directory.Exists(outDir)) { Console.WriteLine("no snapshots yet"); return 0; }
    foreach (var f in Directory.GetFiles(outDir, "snap_*.bin").OrderBy(f => f))
    {
        var info = new FileInfo(f);
        var name = Path.GetFileNameWithoutExtension(f)["snap_".Length..];
        Console.WriteLine($"  {name,-24} {info.Length / 1024.0 / 1024:F1} MB   {info.LastWriteTime:g}");
    }
    return 0;
}

int Equal(string name, double value)
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

void Usage() => Console.WriteLine($"""
    ydsscan <command> [args] [options]

      snap <name>                 sweep the data segment and save it
      list                        show saved snapshots
      eq <snap> <value>           addresses holding a value
      delta <a> <b> <value>       values that changed by exactly this
      filter <a> <b> <mode>       changed | unchanged | increased | decreased
      slots <a> <b> <c>           slot-array fill pattern across three snaps

    options:
      --host <ip>                 console address (only 'snap' needs it)
      --width u8|u16|u32|f32      default u32
      --all                       run every width
      --limit N                   max hits to print (default 20)

    Only 'snap' talks to the console; everything else works offline on saved
    snapshots, so one capture can be reinterpreted at any width.

    {Ps3Config.HelpText}
    """);
