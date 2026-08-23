using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using YakuzaDeadSouls.Ps3;

namespace YakuzaDeadSouls.ApClient;

public sealed class ClientLoop(GameProcess game, ArchipelagoSession session, string slot)
{
    private readonly HashSet<long> _sent = [];
    private int _itemsApplied;
    private bool _goalSent;
    private int _consecutiveFailures;
    private string? _statePath;

    public int PollMilliseconds { get; init; } = 2000;

    // The server resends every item on connect, so "how many have I already
    // put into the game" has to survive a client restart. Locations do not
    // need this - the server tracks those itself.
    private string StatePath
    {
        get
        {
            if (_statePath is not null) return _statePath;
            var seed = session.RoomState?.Seed ?? "noseed";
            var key = string.Concat($"{seed}_{slot}".Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var dir = Path.Combine(AppContext.BaseDirectory, "apstate");
            Directory.CreateDirectory(dir);
            return _statePath = Path.Combine(dir, $"{key}.txt");
        }
    }

    private void LoadApplied()
    {
        if (File.Exists(StatePath) && int.TryParse(File.ReadAllText(StatePath), out var n))
            _itemsApplied = n;
    }

    private void SaveApplied() => File.WriteAllText(StatePath, _itemsApplied.ToString());

    public async Task RunAsync(CancellationToken cancel)
    {
        session.MessageLog.OnMessageReceived += OnMessage;

        foreach (var id in session.Locations.AllLocationsChecked)
            _sent.Add(id);
        LoadApplied();
        Console.WriteLine($"  {_sent.Count} location(s) already checked on the server");
        Console.WriteLine($"  {_itemsApplied} item(s) already applied to this save");

        while (!cancel.IsCancellationRequested)
        {
            // The console refuses a data connection now and then, usually during
            // a heavy scene. Nothing here is worth killing the client over -
            // log it and try again on the next tick.
            try
            {
                await PollAsync();
                _consecutiveFailures = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _consecutiveFailures++;
                var reason = (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
                if (_consecutiveFailures <= 3 || _consecutiveFailures % 10 == 0)
                    Console.WriteLine($"  ps3: {reason} (failure {_consecutiveFailures})");
            }

            try { await Task.Delay(PollMilliseconds, cancel); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task PollAsync()
    {
        await SendKaraokeChecksAsync();
        ApplyReceivedItems();
        CheckGoal();
    }

    private async Task SendKaraokeChecksAsync()
    {
        var songs = Karaoke.ReadAll(game);
        var newly = new List<long>();

        foreach (var song in songs)
        {
            if (!song.EverSung) continue;
            for (var tier = 0; tier < Karaoke.ScoreTiers.Length; tier++)
            {
                if (song.HighScore < Karaoke.ScoreTiers[tier]) continue;
                var id = ApIds.LocationId(song.Id, tier);
                if (_sent.Add(id)) newly.Add(id);
            }
        }

        if (newly.Count == 0) return;

        foreach (var id in newly)
            Console.WriteLine($"  check: {ApIds.Describe(id)}");
        await session.Locations.CompleteLocationChecksAsync([.. newly]);
    }

    private void ApplyReceivedItems()
    {
        var received = session.Items.AllItemsReceived;
        if (received.Count <= _itemsApplied) return;

        for (var i = _itemsApplied; i < received.Count; i++)
        {
            var item = received[i];
            Console.WriteLine($"  received: {item.ItemName}");
            Apply(item.ItemId);
            _itemsApplied = i + 1;
            SaveApplied();
        }
    }

    private void Apply(long itemId)
    {
        switch (itemId)
        {
            case ApIds.ErikaCard:
                Hostesses.SetAvailable(game, Hostesses.Erika, true);
                break;
            case ApIds.YunaCard:
                Hostesses.SetAvailable(game, Hostesses.Yuna, true);
                break;
            case ApIds.SubmachineGunAmmo:
                if (Inventory.GrantAnywhere(game, ApIds.SubmachineGunAmmoItemId, 200) is null)
                    Console.WriteLine("    inventory AND storage full - ammo dropped");
                break;
        }
    }

    private void CheckGoal()
    {
        if (_goalSent || !KeyItems.AkiyamaHostessesMaxed(game)) return;
        Console.WriteLine("  GOAL: both hostesses maxed");
        session.SetGoalAchieved();
        _goalSent = true;
    }

    private static void OnMessage(LogMessage message) =>
        Console.WriteLine($"  [ap] {message}");

    public void EnforceGates()
    {
        var received = session.Items.AllItemsReceived.Select(i => i.ItemId).ToHashSet();
        Hostesses.SetAvailable(game, Hostesses.Erika, received.Contains(ApIds.ErikaCard));
        Hostesses.SetAvailable(game, Hostesses.Yuna, received.Contains(ApIds.YunaCard));
    }
}
