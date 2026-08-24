using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using YakuzaDeadSouls.Ps3;

namespace YakuzaDeadSouls.ApClient;

public sealed class ClientLoop(
    GameProcess game, ArchipelagoSession session, string slot, Notifier? notifier = null)
{
    // OnMessageReceived fires on the Archipelago receive thread. Toasts are an
    // HTTP call with an 8s timeout, so sending one inline would stall that
    // thread and stop the client reading its own socket. Queue here, send from
    // the poll loop, capped so a busy multiworld cannot bury the screen.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _toasts = new();
    private const int MaxToastsPerTick = 3;
    private const int MaxQueuedToasts = 40;

    private readonly HashSet<long> _sent = [];
    private int _itemsApplied;
    private bool _goalSent;
    private int _consecutiveFailures;
    private int _soulPointBaseline = -1;
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
        _toasts.Clear();

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
        await SyncAbilitiesAsync();
        ApplyReceivedItems();
        EnforceSoulPoints();
        CheckGoal();
        await DrainToastsAsync();
    }

    // Buying an ability is the check; the ability itself is the item. So a bit
    // the player set by spending gets reported and then cleared, and only an
    // ability Archipelago has actually sent stays on. One 8-byte read covers
    // both words, and the write only happens when something differs.
    private async Task SyncAbilitiesAsync()
    {
        var abilities = Abilities.All;
        if (abilities.Count == 0) return;

        var granted = new HashSet<int>();
        foreach (var item in session.Items.AllItemsReceived)
            if (ApIds.AbilityIndexOfItem(item.ItemId) is { } index)
                granted.Add(index);

        var window = Abilities.Read(game);
        var dirty = false;
        var newly = new List<long>();

        foreach (var ability in abilities)
        {
            var isSet = Abilities.IsSet(window, ability);
            var shouldBeSet = granted.Contains(ability.Index);
            if (isSet == shouldBeSet) continue;

            if (isSet)
            {
                var id = ApIds.AbilityLocationId(ability.Index);
                if (_sent.Add(id))
                {
                    newly.Add(id);
                    Console.WriteLine($"  check: bought {ability.Name}");
                }
            }

            Abilities.Set(window, ability, shouldBeSet);
            dirty = true;
        }

        if (dirty) Abilities.Write(game, window);
        if (newly.Count > 0) await session.Locations.CompleteLocationChecksAsync([.. newly]);
    }

    private async Task SendKaraokeChecksAsync()
    {
        var songs = Karaoke.ReadAll(game);
        var newly = new List<long>();

        foreach (var song in songs)
        {
            if (!song.EverSung) continue;
            for (var tier = 0; tier < ApIds.ScoreTiers.Length; tier++)
            {
                if (song.HighScore < ApIds.ScoreTiers[tier]) continue;
                var id = ApIds.KaraokeLocationId(song.Id, tier);
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

    // Abilities are absent here on purpose - SyncAbilitiesAsync enforces those
    // every tick rather than applying them once.
    private void Apply(long itemId)
    {
        if (itemId == ApIds.ErikaCard)
        {
            Hostesses.SetAvailable(game, Hostesses.Erika, true);
        }
        else if (itemId == ApIds.YunaCard)
        {
            Hostesses.SetAvailable(game, Hostesses.Yuna, true);
        }
        else if (ApIds.AmmoAmount(itemId) is { } rounds)
        {
            if (Inventory.GrantAnywhere(game, ApIds.SubmachineGunAmmoItemId, (uint)rounds) is null)
                Console.WriteLine("    inventory AND storage full - ammo dropped");
        }
        else if (ApIds.SoulPointsAmount(itemId) is { } amount)
        {
            GrantSoulPoints(amount);
        }
    }

    // u8, so it saturates rather than wrapping to nothing.
    private void GrantSoulPoints(int amount)
    {
        var current = game.ReadU8(Addresses.SoulPoints);
        var next = (byte)Math.Min(255, current + amount);
        game.Write(Addresses.SoulPoints, [next]);
        _soulPointBaseline = next;
        Console.WriteLine($"    soul points {current} -> {next}");
    }

    // Soul points come from Archipelago, not from levelling up. There is no way
    // to tell what caused a gain, only that one happened - so hold a baseline
    // and take back anything that appears without the client having granted it.
    // Spending is allowed through and lowers the baseline.
    //
    // Runs after ApplyReceivedItems so a grant this tick has already moved the
    // baseline and is not immediately clawed back.
    private void EnforceSoulPoints()
    {
        var current = game.ReadU8(Addresses.SoulPoints);

        // First sample of the session: adopt whatever is there. Points earned
        // while the client was closed are kept - the alternative is punishing
        // the player for a disconnect.
        if (_soulPointBaseline < 0)
        {
            _soulPointBaseline = current;
            Console.WriteLine($"  soul points baseline {current}");
            return;
        }

        if (current > _soulPointBaseline)
        {
            game.Write(Addresses.SoulPoints, [(byte)_soulPointBaseline]);
            Console.WriteLine($"  soul points {current} -> {_soulPointBaseline} (in-game gain suppressed)");
        }
        else if (current < _soulPointBaseline)
        {
            _soulPointBaseline = current;
        }
    }

    private void CheckGoal()
    {
        if (_goalSent || !KeyItems.AkiyamaHostessesMaxed(game)) return;
        Console.WriteLine("  GOAL: both hostesses maxed");
        session.SetGoalAchieved();
        _goalSent = true;
    }

    private void OnMessage(LogMessage message)
    {
        var text = message.ToString();
        Console.WriteLine($"  [ap] {text}");

        if (notifier is null || string.IsNullOrWhiteSpace(text)) return;
        if (_toasts.Count >= MaxQueuedToasts) return;
        _toasts.Enqueue(text);
    }

    private async Task DrainToastsAsync()
    {
        if (notifier is null) return;
        for (var i = 0; i < MaxToastsPerTick && _toasts.TryDequeue(out var text); i++)
        {
            try { await notifier.SendAsync(text); }
            catch (Exception) { /* a dropped toast is not worth reporting */ }
        }
    }

    public void EnforceGates()
    {
        var received = session.Items.AllItemsReceived.Select(i => i.ItemId).ToHashSet();
        Hostesses.SetAvailable(game, Hostesses.Erika, received.Contains(ApIds.ErikaCard));
        Hostesses.SetAvailable(game, Hostesses.Yuna, received.Contains(ApIds.YunaCard));
    }
}
