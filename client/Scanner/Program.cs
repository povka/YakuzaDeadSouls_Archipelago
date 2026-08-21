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
        "event" => Event(Positional(0)!, Positional(1)!, Positional(2)!),
        "list" => List(),
        "watch" => await WatchCmd(),
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
    IMemoryTarget target;
    IDisposable? owned = null;
    // Only PS3MAPI has a per-request cap; local memory has none.
    var chunk = Ps3MapiClient.MaxRead;

    if (Flag("--rpcs3"))
    {
        var rpcs3 = Rpcs3Target.Attach();
        if (rpcs3 is null)
        {
            Console.WriteLine("RPCS3 is not running, or no game is loaded in it.");
            return 1;
        }
        owned = rpcs3;
        target = rpcs3;
        chunk = int.MaxValue;
        Console.WriteLine($"attached rpcs3 pid {rpcs3.ProcessId}, guest base 0x{rpcs3.GuestBase:X}");
    }
    else
    {
        var host = Ps3Config.Require(Opt("--host"));
        var pid = await Ps3Console.FindGameAsync(host);
        if (pid is null) { Console.WriteLine("No game running."); return 1; }

        var client = new Ps3MapiClient(host);
        client.Connect();
        owned = client;
        target = new GameProcess(client, pid.Value);
        Console.WriteLine($"attached 0x{pid:X8}");
    }

    using var _ = owned;

    const uint start = Addresses.DataBase, end = Addresses.DataEnd;
    var total = (int)(end - start);
    var buffer = new byte[total];
    var sw = Stopwatch.StartNew();
    var done = 0;
    var step = 0;
    while (done < total)
    {
        var want = (int)Math.Min((long)chunk, total - done);
        target.ReadMemory(start + (uint)done, want).CopyTo(buffer, done);
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

async Task<int> WatchCmd()
{
    var listArg = Positional(0) ?? Path.Combine(outDir, "..", "data", "chapter_candidates.txt");
    var path = File.Exists(listArg) ? listArg
        : Path.GetFullPath(Path.Combine(outDir, "..", "data", listArg));
    var addresses = Watch.LoadList(path);
    if (addresses.Count == 0) { Console.WriteLine($"no addresses in {path}"); return 1; }

    var width = int.TryParse(Opt("--width2"), out var w2) ? w2 : 4;
    var interval = int.TryParse(Opt("--interval"), out var ms) ? ms : 500;

    if (Flag("--rpcs3"))
    {
        var rpcs3 = Rpcs3Target.Attach();
        if (rpcs3 is null) { Console.WriteLine("RPCS3 not running."); return 1; }
        using (rpcs3) Watch.Run(rpcs3, addresses, width, interval, false);
        return 0;
    }

    var host = Ps3Config.Require(Opt("--host"));
    var pid = await Ps3Console.FindGameAsync(host);
    if (pid is null) { Console.WriteLine("No game running."); return 1; }
    using var client = new Ps3MapiClient(host);
    client.Connect();
    Watch.Run(new GameProcess(client, pid.Value), addresses, width, interval, true);
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

int Event(string idleA, string idleB, string after)
{
    var (a, b, c) = (Snapshot.Load(SnapPath(idleA)), Snapshot.Load(SnapPath(idleB)), Snapshot.Load(SnapPath(after)));
    var fromArg = Opt("--from");
    var toArg = Opt("--to");
    if (fromArg is not null && toArg is not null)
    {
        foreach (var w in widths)
        {
            var t = Compare.EventTransition(a, b, c, w, double.Parse(fromArg), double.Parse(toArg));
            Console.WriteLine($"  {w,-4}: {t.Count} went {fromArg} -> {toArg}");
            foreach (var h in t.Take(limit == 0 ? 40 : limit))
                Console.WriteLine($"      0x{h.Address:X8}");
        }
        return 0;
    }
    foreach (var w in widths)
    {
        var noisy = Compare.Filter(a, b, w, Compare.Change.Changed).Count;
        var hits = Compare.EventOnly(a, b, c, w);
        Console.WriteLine($"  {w,-4}: {hits.Count} changed by the event  ({noisy} noisy addresses excluded)");
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
      watch [file]                poll addresses and report changes
      eq <snap> <value>           addresses holding a value
      delta <a> <b> <value>       values that changed by exactly this
      filter <a> <b> <mode>       changed | unchanged | increased | decreased
      slots <a> <b> <c>           slot-array fill pattern across three snaps
      event <idleA> <idleB> <after>
                                  what an event changed, with ambient churn
                                  subtracted using two idle snapshots

    options:
      --rpcs3                     read a running RPCS3 instead of the console
      --host <ip>                 console address (only 'snap' needs it)
      --width u8|u16|u32|f32      default u32
      --all                       run every width
      --limit N                   max hits to print (default 20)

    Only 'snap' reads a target; everything else works offline on saved
    snapshots, so one capture can be reinterpreted at any width.

    Guest addresses are the same on hardware and in RPCS3, so snapshots from
    either are interchangeable. --rpcs3 reads local memory rather than a
    ~1.3 MB/s network link, so sweeps are far faster.

    {Ps3Config.HelpText}
    """);
