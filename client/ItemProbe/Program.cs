using System.Buffers.Binary;
using YakuzaDeadSouls.Ps3;

// ydsitems <command> [options]
//
//   read                 dump the 24 inventory slots
//   unlock               clear the locked marker from every slot
//   fill <ids>           write ids into the slots, comma/range separated
//   next                 fill with the next 24 unidentified ids
//   restore              put back whatever `fill` last overwrote
//
// Options: --rpcs3 (default), --host <ip>

if (args.Length < 1) { Usage(); return 0; }

var command = args[0].ToLowerInvariant();
var rest = args[1..];
var backupPath = Path.Combine(
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "output")),
    "inventory_backup.bin");

IMemoryTarget target;
IDisposable? owned;

if (Array.IndexOf(rest, "--host") >= 0)
{
    var i = Array.IndexOf(rest, "--host");
    var host = Ps3Config.Require(i + 1 < rest.Length ? rest[i + 1] : null);
    var pid = await Ps3Console.FindGameAsync(host);
    if (pid is null) { Console.WriteLine("No game running on the console."); return 1; }
    var client = new Ps3MapiClient(host);
    client.Connect();
    owned = client;
    target = new GameProcess(client, pid.Value);
    Console.WriteLine($"console, pid 0x{pid:X8}");
}
else
{
    var rpcs3 = Rpcs3Target.Attach();
    if (rpcs3 is null) { Console.WriteLine("RPCS3 is not running, or no game is loaded."); return 1; }
    owned = rpcs3;
    target = rpcs3;
    Console.WriteLine($"rpcs3, guest base 0x{rpcs3.GuestBase:X}");
}

using var _ = owned;

switch (command)
{
    case "read": Read(); break;
    case "unlock": Unlock(); Read(); break;
    case "fill": Fill(ParseIds(rest.FirstOrDefault(a => !a.StartsWith("--")) ?? "")); break;
    case "next": Fill(NextUnknownIds(Inventory.Slots)); break;
    case "restore": Restore(); Read(); break;
    case "regions":
        if (target is Rpcs3Target r0)
        {
            Console.WriteLine("mapped guest regions:");
            ulong totalBytes = 0;
            foreach (var (guest, len, prot) in r0.MappedGuestRegions())
            {
                Console.WriteLine($"  0x{guest:X8} - 0x{guest + (uint)len:X8}  {len / 1024.0 / 1024,8:F2} MB  prot=0x{prot:X}");
                totalBytes += len;
            }
            Console.WriteLine($"  total {totalBytes / 1024.0 / 1024:F1} MB readable");
        }
        else Console.WriteLine("regions needs --rpcs3");
        break;
    case "find":
        if (target is Rpcs3Target r1)
            YakuzaDeadSouls.ItemProbe.FindStrings.Run(r1, rest.FirstOrDefault(a => !a.StartsWith("--")) ?? "Tauriner");
        else Console.WriteLine("find needs --rpcs3 (a full RAM sweep over PS3MAPI would take hours)");
        break;
    case "strings":
        if (target is Rpcs3Target r2)
            YakuzaDeadSouls.ItemProbe.FindStrings.DumpTable(r2,
                Convert.ToUInt32(rest.First(a => !a.StartsWith("--")), 16), 60);
        else Console.WriteLine("strings needs --rpcs3");
        break;
    case "ids":
        if (target is Rpcs3Target r3)
        {
            var bare = rest.Where(a => !a.StartsWith("--")).ToArray();
            YakuzaDeadSouls.ItemProbe.FindStrings.DumpIds(r3,
                Convert.ToUInt32(bare[0], 16),
                bare.Length > 1 ? ushort.Parse(bare[1]) : (ushort)2,
                bare.Length > 2 ? int.Parse(bare[2]) : 400,
                Array.IndexOf(rest, "--code") >= 0);
        }
        else Console.WriteLine("ids needs --rpcs3");
        break;
    default: Usage(); break;
}
return 0;

Inventory.Item[] ReadSlots()
{
    var raw = target.ReadMemory(Inventory.Base, Inventory.Slots * Inventory.Stride);
    var items = new Inventory.Item[Inventory.Slots];
    for (var i = 0; i < Inventory.Slots; i++)
    {
        var span = raw.AsSpan(i * Inventory.Stride, Inventory.Stride);
        items[i] = new Inventory.Item(
            BinaryPrimitives.ReadUInt16BigEndian(span[..2]),
            BinaryPrimitives.ReadUInt32BigEndian(span[4..8]));
    }
    return items;
}

void Read()
{
    Console.WriteLine("\nslot  id    state     name");
    var items = ReadSlots();
    for (var i = 0; i < items.Length; i++)
    {
        var it = items[i];
        var state = it.IsLocked ? "LOCKED" : it.IsEmpty ? "empty" : "item";
        var name = it.IsItem
            ? Inventory.KnownItems.TryGetValue(it.Id, out var known) ? known : "<-- UNKNOWN, name it"
            : "";
        Console.WriteLine($"  {i,2}  {it.Id,-5} {state,-9} {name}");
    }
}

void Unlock()
{
    var items = ReadSlots();
    var count = 0;
    for (var i = 0; i < items.Length; i++)
    {
        if (!items[i].IsLocked) continue;
        target.WriteMemory(Inventory.Base + (uint)(i * Inventory.Stride), Inventory.EmptyRecord);
        count++;
    }
    Console.WriteLine($"unlocked {count} slot(s)");
}

void Fill(ushort[] ids)
{
    if (ids.Length == 0) { Console.WriteLine("no ids given"); return; }

    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
    File.WriteAllBytes(backupPath, target.ReadMemory(Inventory.Base, Inventory.Slots * Inventory.Stride));
    Console.WriteLine($"backed up current inventory to {Path.GetFileName(backupPath)}");

    Unlock();

    var n = Math.Min(ids.Length, Inventory.Slots);
    for (var i = 0; i < n; i++)
        target.WriteMemory(Inventory.Base + (uint)(i * Inventory.Stride), Inventory.MakeRecord(ids[i]));
    for (var i = n; i < Inventory.Slots; i++)
        target.WriteMemory(Inventory.Base + (uint)(i * Inventory.Stride), Inventory.EmptyRecord);

    Console.WriteLine($"wrote {n} ids: {string.Join(", ", ids.Take(n))}");
    Read();
    Console.WriteLine("\nOpen the in-game item menu and read the names in slot order.");
    Console.WriteLine("`ydsitems restore` puts your real inventory back.");
}

void Restore()
{
    if (!File.Exists(backupPath)) { Console.WriteLine("no backup to restore"); return; }
    target.WriteMemory(Inventory.Base, File.ReadAllBytes(backupPath));
    Console.WriteLine("inventory restored");
}

// Ids not yet named, skipping 0 and 1 which are the empty and locked markers.
ushort[] NextUnknownIds(int count)
{
    var found = new List<ushort>();
    for (ushort id = 2; id < 256 && found.Count < count; id++)
        if (!Inventory.KnownItems.ContainsKey(id))
            found.Add(id);
    return [.. found];
}

ushort[] ParseIds(string spec)
{
    var result = new List<ushort>();
    foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var piece = part.Trim();
        if (piece.Contains('-'))
        {
            var bounds = piece.Split('-');
            for (var v = ushort.Parse(bounds[0]); v <= ushort.Parse(bounds[1]); v++)
                result.Add(v);
        }
        else result.Add(ushort.Parse(piece));
    }
    return [.. result];
}

void Usage() => Console.WriteLine("""
    ydsitems <command> [options]

      read                 dump the 24 inventory slots
      unlock               clear the locked marker from every slot
      fill <ids>           write ids into slots, e.g. 2-10,13,54,60
      next                 fill with the next 24 unidentified ids
      restore              put back whatever `fill` last overwrote

    options:
      --rpcs3              read a running RPCS3 (default)
      --host <ip>          use the console instead
    """);
