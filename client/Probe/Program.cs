using System.Diagnostics;
using YakuzaDeadSouls.Ps3;

string host;
try
{
    host = Ps3Config.Require(args.Length > 0 ? args[0] : null);
}
catch (Ps3Exception ex)
{
    Console.WriteLine(ex.Message);
    return 1;
}

Console.WriteLine($"connecting to {host} ...");

if (!Ps3MapiClient.IsAvailable(host))
{
    Console.WriteLine("FAIL: nothing listening on 7887.");
    Console.WriteLine("Enable PS3MAPI in webMAN Setup (VSH MENU -> DEL CFW SYSCALLS line) and reboot.");
    return 1;
}

var procs = await Ps3Console.ListAsync(host);
Console.WriteLine("processes:");
foreach (var p in procs)
    Console.WriteLine($"  0x{p.Pid:X8}  {p.Name}{(p.IsGame ? "  <- game" : "")}");

var pid = await Ps3Console.FindGameAsync(host);
if (pid is null) { Console.WriteLine("\nNo game running."); return 1; }

using var client = new Ps3MapiClient(host);
client.Connect();
var game = new GameProcess(client, pid.Value);
Console.WriteLine($"\nattached pid 0x{pid:X8}");

if (!game.LooksLikeGame()) { Console.WriteLine("FAIL: no ELF header at 0x10000"); return 1; }
Console.WriteLine("ELF header present - reads work and bytes are not corrupted.");

Console.WriteLine($"\n  money  {game.ReadU32(Addresses.Money),8} yen");
Console.WriteLine($"  level  {game.ReadU8(Addresses.Level),8}   Soul points {game.ReadU8(Addresses.SoulPoints)}");
Console.WriteLine($"  exp    {game.ReadU32(Addresses.Exp),8}   total {game.ReadU32(Addresses.ExpTotal)}");
Console.WriteLine($"  hp     {game.ReadU16(Addresses.HealthCurrent),8} / {game.ReadU16(Addresses.HealthMax)}");
Console.WriteLine($"  focus  {game.ReadF32(Addresses.FocusCurrent),8} / {game.ReadF32(Addresses.FocusMax)}");

Console.WriteLine("\nhostesses (goal: both maxed):");
foreach (var (id, who) in new (ushort, string)[]
         {
             (KeyItems.YunaCard, "Yuna    card"), (KeyItems.YunaFancyCard, "Yuna    FANCY"),
             (KeyItems.ErikaCard, "Erika   card"), (KeyItems.ErikaFancyCard, "Erika   FANCY"),
         })
    Console.WriteLine($"  {who}  {(KeyItems.Has(game, id) ? "yes" : "-")}   0x{KeyItems.AddressOf(id):X8}");
Console.WriteLine($"  => goal complete: {KeyItems.AkiyamaHostessesMaxed(game)}");

Console.WriteLine($"\nkaraoke (tiers {string.Join("/", Karaoke.ScoreTiers)}):");
var songs = Karaoke.ReadAll(game);
var checksEarned = 0;
foreach (var song in songs)
{
    if (!song.EverSung) continue;
    var tiers = Karaoke.ClearedTiers(song).ToArray();
    checksEarned += tiers.Length;
    Console.WriteLine($"  song 0x{song.Id:X2}  high {song.HighScore,4}  prev {song.PreviousScore,4}   "
                      + (tiers.Length > 0 ? $"checks: {string.Join(", ", tiers)}" : "no tier reached"));
}
Console.WriteLine($"  {songs.Count(s => s.EverSung)}/{Karaoke.SongCount} songs sung, "
                  + $"{checksEarned}/{Karaoke.SongCount * Karaoke.ScoreTiers.Length} karaoke checks earned");

Console.WriteLine("\ninventory:");
var items = Inventory.Read(game);
for (var i = 0; i < items.Length; i++)
{
    if (items[i].IsEmpty) continue;
    var name = Inventory.KnownItems.TryGetValue(items[i].Id, out var known) ? known : "?";
    Console.WriteLine($"  slot {i}: id={items[i].Id} x{items[i].Quantity}  {name}");
}
var free = Inventory.FindFreeSlot(game);
Console.WriteLine(free is null ? "  inventory full - no free slot"
                               : $"  first free slot: 0x{free:X8}");

Console.WriteLine("\nsegment layout (from the live ELF headers):");
var layout = ElfMap.Read(game);
Console.WriteLine($"  entry 0x{layout.Entry:X8}");
foreach (var seg in layout.Segments.Where(x => x.MemorySize > 0))
    Console.WriteLine($"  {seg.FlagString,3}  0x{seg.VirtualAddress:X8} - 0x{seg.End:X8}  {seg.MemorySize / 1024.0 / 1024:F1} MB");

Console.WriteLine($"\nCCAPI reachable: {await new Notifier(host).IsCcapiAvailableAsync()}");

var sw = Stopwatch.StartNew();
const int reads = 10;
for (var i = 0; i < reads; i++) game.Read(Addresses.DataBase, 65536);
sw.Stop();
var perRead = sw.Elapsed.TotalMilliseconds / reads;
Console.WriteLine($"\n  {reads}x 64 KB reads: {perRead:F1} ms each, {64 * 1000 / perRead / 1024:F2} MB/s");

Console.WriteLine("\ndone.");
return 0;
