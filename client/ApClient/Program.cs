using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using YakuzaDeadSouls.ApClient;
using YakuzaDeadSouls.Ps3;

if (args.Contains("--help") || args.Length == 0) { Usage(); return 0; }

var apHost = Opt("--ap") ?? "archipelago.gg";
var apPort = int.TryParse(Opt("--port"), out var p) ? p : 38281;
var slot = Opt("--slot");
var password = Opt("--password");
var ps3Host = Ps3Config.Resolve(Opt("--host"));

if (slot is null) { Console.WriteLine("--slot is required"); return 1; }
if (ps3Host is null) { Console.WriteLine(Ps3Config.HelpText); return 1; }

Console.WriteLine($"ps3   {ps3Host}");
uint? pid;
try { pid = await Ps3Console.FindGameAsync(ps3Host); }
catch (Ps3Exception ex) { Console.WriteLine($"  {ex.Message}"); return 1; }
if (pid is null) { Console.WriteLine("  no game running on the console"); return 1; }

using var ps3 = new Ps3MapiClient(ps3Host);
ps3.Connect();
var game = new GameProcess(ps3, pid.Value);
Console.WriteLine($"  attached pid 0x{pid:X8}");

if (!game.LooksLikeGame()) { Console.WriteLine("  no ELF header - wrong process?"); return 1; }

Console.WriteLine($"\nap    {apHost}:{apPort} as '{slot}'");
var session = ArchipelagoSessionFactory.CreateSession(apHost, apPort);
var login = session.TryConnectAndLogin(
    ApIds.GameName, slot, ItemsHandlingFlags.AllItems, password: password);

if (login is LoginFailure failure)
{
    Console.WriteLine("  login failed:");
    foreach (var e in failure.Errors) Console.WriteLine($"    {e}");
    return 1;
}
Console.WriteLine("  connected");

var loop = new ClientLoop(game, session);
loop.EnforceGates();

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
Console.WriteLine("\nwatching. ctrl-c to stop.\n");

await loop.RunAsync(cancel.Token);
session.Socket.DisconnectAsync().GetAwaiter().GetResult();
Console.WriteLine("\ndisconnected.");
return 0;

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    if (i < 0 || i + 1 >= args.Length) return null;
    return args[i + 1].StartsWith("--") ? null : args[i + 1];
}

void Usage() => Console.WriteLine($"""
    ydsclient --slot <name> [options]

      --slot <name>        your slot name in the multiworld  (required)
      --ap <host>          Archipelago server   (default archipelago.gg)
      --port <n>           Archipelago port     (default 38281)
      --password <pw>      room password, if any
      --host <ip>          PS3 address; otherwise taken from console.txt

    {Ps3Config.HelpText}
    """);
