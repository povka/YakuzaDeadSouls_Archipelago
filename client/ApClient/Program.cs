using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using YakuzaDeadSouls.ApClient;
using YakuzaDeadSouls.Ps3;

if (args.Contains("--help")) { Usage(); return 0; }

// No arguments means it was double-clicked, so ask instead of exiting.
var interactive = args.Length == 0;

var apHost = Opt("--ap") ?? (interactive ? Ask("Archipelago server", "archipelago.gg") : "archipelago.gg");
var apPort = int.TryParse(Opt("--port") ?? (interactive ? Ask("Port", "38281") : null), out var p) ? p : 38281;
var slot = Opt("--slot") ?? (interactive ? Ask("Slot name", null) : null);
var password = Opt("--password") ?? (interactive ? NullIfBlank(Ask("Password (blank for none)", "")) : null);
var ps3Host = Ps3Config.Resolve(Opt("--host"));

if (interactive) Console.WriteLine();
if (string.IsNullOrWhiteSpace(slot)) { Console.WriteLine("A slot name is required."); return Finish(1); }
if (ps3Host is null) { Console.WriteLine(Ps3Config.HelpText); return Finish(1); }

Console.WriteLine($"ps3   {ps3Host}");
uint? pid;
try { pid = await Ps3Console.FindGameAsync(ps3Host); }
catch (Ps3Exception ex) { Console.WriteLine($"  {ex.Message}"); return Finish(1); }
if (pid is null) { Console.WriteLine("  no game running on the console"); return Finish(1); }

using var ps3 = new Ps3MapiClient(ps3Host);
ps3.Connect();
var game = new GameProcess(ps3, pid.Value);
Console.WriteLine($"  attached pid 0x{pid:X8}");

if (!game.LooksLikeGame()) { Console.WriteLine("  no ELF header - wrong process?"); return Finish(1); }

Console.WriteLine($"\nap    {apHost}:{apPort} as '{slot}'");
var session = ArchipelagoSessionFactory.CreateSession(apHost, apPort);
var login = session.TryConnectAndLogin(
    ApIds.GameName, slot, ItemsHandlingFlags.AllItems, password: password);

if (login is LoginFailure failure)
{
    Console.WriteLine("  login failed:");
    foreach (var e in failure.Errors) Console.WriteLine($"    {e}");
    return Finish(1);
}
Console.WriteLine("  connected");

Notifier? notifier = null;
if (!args.Contains("--no-notify"))
{
    var candidate = new Notifier(ps3Host);
    var channel = await candidate.DetectAsync();
    if (channel != Notifier.Channel.None)
    {
        notifier = candidate;
        Console.WriteLine($"  toasts on via {channel} - multiworld messages show on the TV");
    }
    else
    {
        Console.WriteLine("  no notification channel answered - toasts off");
    }
}

var loop = new ClientLoop(game, session, slot, notifier);
loop.EnforceGates();

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
Console.WriteLine("\nwatching. ctrl-c to stop.\n");

await loop.RunAsync(cancel.Token);
session.Socket.DisconnectAsync().GetAwaiter().GetResult();
Console.WriteLine("\ndisconnected.");
return Finish(0);

string Ask(string prompt, string? fallback)
{
    Console.Write(fallback is { Length: > 0 } ? $"{prompt} [{fallback}]: " : $"{prompt}: ");
    var line = Console.ReadLine();
    return string.IsNullOrWhiteSpace(line) ? fallback ?? "" : line.Trim();
}

string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

// Keeps a double-clicked window open long enough to read the message.
int Finish(int code)
{
    if (interactive)
    {
        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
    return code;
}

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
      --no-notify          do not show multiworld messages on the TV

    {Ps3Config.HelpText}
    """);
