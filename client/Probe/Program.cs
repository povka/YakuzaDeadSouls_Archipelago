using System.Diagnostics;
using YakuzaDeadSouls.Ps3;

var host = args.Length > 0 ? args[0] : "192.168.1.129";
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
Console.WriteLine($"  hp     {game.ReadU16(Addresses.HealthCurrent),8} / {game.ReadU16(Addresses.HealthMax)}");
var exp = game.ReadU32(Addresses.Exp);
Console.WriteLine($"  exp    {exp,8}   (to next: {Addresses.Level1Threshold - exp})");

Console.WriteLine("\ninventory:");
var items = Inventory.Read(game);
for (var i = 0; i < items.Length; i++)
{
    if (items[i].IsEmpty) continue;
    var name = Inventory.KnownItems.TryGetValue(items[i].Id, out var known) ? known : "?";
    Console.WriteLine($"  slot {i}: id={items[i].Id} x{items[i].Quantity}  {name}");
}
var free = Inventory.FindFreeSlot(game);
Console.WriteLine($"  first free slot: 0x{free:X8}");

var sw = Stopwatch.StartNew();
const int n = 10;
for (var i = 0; i < n; i++) game.Read(Addresses.DataBase, 65536);
sw.Stop();
var perRead = sw.Elapsed.TotalMilliseconds / n;
Console.WriteLine($"\n  {n}x 64 KB reads: {perRead:F1} ms each, {64 * 1000 / perRead / 1024:F2} MB/s");

Console.WriteLine("\ndone.");
return 0;
