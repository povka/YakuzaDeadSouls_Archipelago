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
    private int _tutorialAbility = -1;
    private bool _goalSent;
    private int _consecutiveFailures;
    private HashSet<long>? _karaokeBaseline;
    private HashSet<long>? _slotLocations;
    private int _levelBaseline = -1;
    private Dictionary<ushort, int>? _heldBaseline;
    private readonly Dictionary<ushort, int> _grantedPending = [];
    private const int SettleTicks = 3;
    private int _settleTicks;
    private bool? _goalPrev;
    private int _lastSuppressed = -1;
    private bool _saveWasLoaded = true;
    private readonly Dictionary<long, string> _scouted = [];
    private string? _shopShown;
    // shop index -> the item id each slot sells, remembered from the last
    // visit so purchases are still detected after the buy list closes.
    private readonly Dictionary<int, ushort[]> _shopStock = [];
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
        if (!File.Exists(StatePath)) return;
        var lines = File.ReadAllLines(StatePath);
        if (lines.Length > 0 && int.TryParse(lines[0], out var n)) _itemsApplied = n;
        if (lines.Length > 1 && int.TryParse(lines[1], out var t)) _tutorialAbility = t;
    }

    private void SaveApplied() =>
        File.WriteAllLines(StatePath, [_itemsApplied.ToString(), _tutorialAbility.ToString()]);

    public async Task RunAsync(CancellationToken cancel)
    {
        session.MessageLog.OnMessageReceived += OnMessage;

        foreach (var id in session.Locations.AllLocationsChecked)
            _sent.Add(id);
        LoadApplied();
        Console.WriteLine($"  {_sent.Count} location(s) already checked on the server");
        Console.WriteLine($"  {_itemsApplied} item(s) already applied to this save");

        if (_tutorialAbility >= 0)
            Console.WriteLine($"  tutorial ability: {Abilities.All[_tutorialAbility].Name}");

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
        // Title screen or mid-load: every address reads zero. Acting on that
        // once cost a player their soul points - the baseline adopted 0, then
        // clawed the real value back down to it after the save reloaded.
        if (!Addresses.SaveLoaded(game))
        {
            if (_saveWasLoaded)
            {
                Console.WriteLine("  no save loaded - holding off");
                _saveWasLoaded = false;
            }
            // Re-sample on the next load rather than trusting a stale baseline.
            _karaokeBaseline = null;
            _levelBaseline = -1;
            _goalPrev = null;
            _heldBaseline = null;
            _grantedPending.Clear();
            _settleTicks = 0;
            await DrainToastsAsync();
            return;
        }

        if (!_saveWasLoaded)
        {
            Console.WriteLine("  save loaded - resuming");
            _saveWasLoaded = true;
        }

        // SaveLoaded only proves HealthMax and Level are populated, and those
        // land before the inventory and the ability bits do. Adopting a baseline
        // in that window reads the rest of the save arriving as player actions.
        if (_settleTicks < SettleTicks)
        {
            _settleTicks++;
            if (_settleTicks == SettleTicks) Console.WriteLine("  save settled - watching");
            await DrainToastsAsync();
            return;
        }

        await SendKaraokeChecksAsync();
        await SendLevelChecksAsync();
        SyncAbilities();
        await SyncShopAsync();
        ApplyReceivedItems();
        EnforceSoulPoints();
        CheckGoal();
        await DrainToastsAsync();
    }

    // Abilities are items, never checks. Bits are only ever turned ON, so an
    // ability the player can see in the menu never disappears from it.
    private void SyncAbilities()
    {
        var abilities = Abilities.All;
        if (abilities.Count == 0) return;

        var granted = new HashSet<int>();
        foreach (var item in session.Items.AllItemsReceived)
            if (ApIds.AbilityIndexOfItem(item.ItemId) is { } index)
                granted.Add(index);

        var window = Abilities.Read(game);

        // The tutorial refuses to advance until the player buys an ability, so
        // the first purchase is allowed through and kept. EnforceSoulPoints
        // takes the currency away once this has happened.
        if (_tutorialAbility < 0)
        {
            foreach (var ability in abilities)
            {
                if (!Abilities.IsSet(window, ability) || granted.Contains(ability.Index)) continue;
                _tutorialAbility = ability.Index;
                SaveApplied();
                Console.WriteLine($"  tutorial: keeping {ability.Name}");
                break;
            }
        }

        var dirty = false;
        foreach (var ability in abilities)
        {
            if (!granted.Contains(ability.Index) || Abilities.IsSet(window, ability)) continue;
            Abilities.Set(window, ability, true);
            dirty = true;
        }

        if (dirty) Abilities.Write(game, window);
    }

    // Shop slots are locations. While a shop is open the client relabels each
    // unchecked slot with the Archipelago item sitting there, and treats an item
    // appearing in the inventory as that slot being bought.
    private async Task SyncShopAsync()
    {
        var shop = Shops.Find(game);
        if (shop is null)
        {
            // The display list only exists while the buy list is on screen.
            // Losing it does not mean nothing was bought, so detection below
            // runs against every shop seen this session regardless.
            _shopShown = null;
        }
        else
        {
            var open = shop.Value;
            var index = ApIds.ShopIndexOfFile(open.File);
            if (index is null)
            {
                if (_shopShown != open.File)
                {
                    Console.WriteLine($"  shop {open.File} is open but not mapped - ignoring");
                    _shopShown = open.File;
                }
            }
            else
            {
                var def = ApIds.ShopDefs[index.Value];
                var slots = Math.Min(def.Slots, open.SlotCount);
                _shopStock[index.Value] = Shops.ItemIds(game, open, slots);

                if (_shopShown != open.File)
                {
                    await LabelShopAsync(open, index.Value, def, slots);
                    _shopShown = open.File;
                }
            }
        }

        await DetectShopPurchasesAsync();
    }

    private async Task LabelShopAsync(Shops.Shop open, int index, ApIds.ShopDef def, int slots)
    {
        var ids = new List<long>();
        for (var slot = 0; slot < slots; slot++)
        {
            var id = ApIds.ShopLocationId(index, slot);
            if (!_sent.Contains(id) && !_scouted.ContainsKey(id)) ids.Add(id);
        }

        if (ids.Count > 0)
        {
            try
            {
                var scouted = await session.Locations.ScoutLocationsAsync([.. ids]);
                foreach (var (id, info) in scouted)
                    _scouted[id] = $"{info.ItemName} ({info.Player.Name})";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  scout failed: {ex.Message}");
            }
        }

        var descs = Shops.DescriptionPointers(game, open, slots);
        var shown = 0;
        for (var slot = 0; slot < slots; slot++)
        {
            var id = ApIds.ShopLocationId(index, slot);
            if (_sent.Contains(id))
            {
                Shops.SetName(game, open, slot, "(already bought)", descs[slot]);
                continue;
            }
            if (!_scouted.TryGetValue(id, out var label)) continue;
            Shops.SetName(game, open, slot, label, descs[slot]);
            shown++;
        }
        Console.WriteLine($"  {def.Name}: relabelled {shown} slot(s)");
    }

    // Runs every tick against every shop visited this session, whether or not a
    // buy list is currently open.
    private async Task DetectShopPurchasesAsync()
    {
        if (_shopStock.Count == 0) return;

        var items = Inventory.Read(game);
        var counts = CountHeld(items);

        // What was already in the bag was not bought from us. Without this a
        // fresh seed on a played save reports every matching item at once, and
        // then confiscates it.
        if (_heldBaseline is null)
        {
            _heldBaseline = counts;
            _grantedPending.Clear();
            return;
        }

        // Archipelago grants land in this same inventory, so a vanilla item
        // arriving from the multiworld would otherwise read as a purchase and
        // send another check, which grants another item.
        var gained = new Dictionary<ushort, int>();
        foreach (var (id, n) in counts)
        {
            var delta = n - _heldBaseline.GetValueOrDefault(id)
                          - _grantedPending.GetValueOrDefault(id);
            if (delta > 0) gained[id] = delta;
        }

        var newly = new List<long>();
        var removed = new Dictionary<ushort, int>();
        foreach (var (index, stock) in _shopStock)
        {
            var def = ApIds.ShopDefs[index];
            for (var slot = 0; slot < stock.Length; slot++)
            {
                var id = ApIds.ShopLocationId(index, slot);
                if (_sent.Contains(id)) continue;
                if (gained.GetValueOrDefault(stock[slot]) <= 0) continue;

                gained[stock[slot]]--;
                removed[stock[slot]] = removed.GetValueOrDefault(stock[slot]) + 1;
                RemoveItem(items, stock[slot]);
                if (_sent.Add(id))
                {
                    newly.Add(id);
                    Console.WriteLine($"  check: {def.Name} slot {slot + 1}");
                }
            }
        }

        foreach (var (id, n) in removed)
            counts[id] = Math.Max(0, counts.GetValueOrDefault(id) - n);
        _heldBaseline = counts;
        _grantedPending.Clear();

        if (newly.Count > 0)
            await session.Locations.CompleteLocationChecksAsync([.. newly]);
    }

    private void NoteGranted(ushort itemId) =>
        _grantedPending[itemId] = _grantedPending.GetValueOrDefault(itemId) + 1;

    private static Dictionary<ushort, int> CountHeld(Inventory.Item[] items)
    {
        var counts = new Dictionary<ushort, int>();
        foreach (var item in items)
            if (item.IsItem) counts[item.Id] = counts.GetValueOrDefault(item.Id) + 1;
        return counts;
    }

    private void RemoveItem(Inventory.Item[] items, ushort itemId)
    {
        for (var i = 0; i < items.Length; i++)
            if (items[i].Id == itemId && items[i].IsItem)
            {
                game.Write(Inventory.Base + (uint)(i * Inventory.Stride), Inventory.EmptyRecord);
                items[i] = default;
                return;
            }
    }

    private bool InSlot(long locationId) =>
        (_slotLocations ??= [.. session.Locations.AllLocations]).Contains(locationId);

    // Levels above MaxLevel still count as reaching MaxLevel; they simply have
    // no location left to report.
    private async Task SendLevelChecksAsync()
    {
        var level = game.ReadU8(Addresses.Level);
        if (level == 0) return;

        if (_levelBaseline < 0)
        {
            _levelBaseline = level;
            if (level > 1) Console.WriteLine($"  already level {level} - adopted, not reported");
            return;
        }

        if (level <= _levelBaseline) return;

        // The datapackage has every level to 100; this slot's YAML decided how
        // many exist. Anything past the cap is simply not a location here.
        var newly = new List<long>();
        for (var l = _levelBaseline + 1; l <= level && l <= ApIds.MaxLevel; l++)
        {
            var id = ApIds.LevelLocationId(l);
            if (InSlot(id) && _sent.Add(id)) newly.Add(id);
        }
        _levelBaseline = level;

        if (newly.Count == 0) return;

        foreach (var id in newly)
            Console.WriteLine($"  check: {ApIds.LevelLocationName(ApIds.LevelOfLocation(id) ?? 0)}");
        await session.Locations.CompleteLocationChecksAsync([.. newly]);
    }

    private async Task SendKaraokeChecksAsync()
    {
        var songs = Karaoke.ReadAll(game);
        var qualifying = new HashSet<long>();

        foreach (var song in songs)
        {
            if (!song.EverSung) continue;
            for (var tier = 0; tier < ApIds.ScoreTiers.Length; tier++)
            {
                if (song.HighScore < ApIds.ScoreTiers[tier]) continue;
                qualifying.Add(ApIds.KaraokeLocationId(song.Id, tier));
            }
        }

        // High scores persist, so an absolute reading cannot tell a song sung
        // just now from one sung in whatever save was last in memory.
        if (_karaokeBaseline is null)
        {
            _karaokeBaseline = qualifying;
            if (qualifying.Count > 0)
                Console.WriteLine($"  {qualifying.Count} karaoke score(s) already at tier - adopted, not reported");
            return;
        }

        var newly = new List<long>();
        foreach (var id in qualifying)
            if (!_karaokeBaseline.Contains(id) && _sent.Add(id)) newly.Add(id);

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
        else if (ApIds.AmmoOf(itemId) is { } ammo)
        {
            if (Inventory.GrantAnywhere(game, ammo.ItemId, (uint)ammo.Rounds) is null)
                Console.WriteLine("    inventory AND storage full - ammo dropped");
            else NoteGranted(ammo.ItemId);
        }
        else if (ApIds.GameItemOf(itemId) is { } gameItem)
        {
            if (Inventory.GrantAnywhere(game, gameItem) is null)
                Console.WriteLine("    inventory AND storage full - item dropped");
            else NoteGranted(gameItem);
        }
        else if (ApIds.MoneyAmount(itemId) is { } yen)
        {
            GrantMoney(yen);
        }
    }

    // u32, so overflow is not a practical concern at 50,000 a time.
    private void GrantMoney(int yen)
    {
        var current = game.ReadU32(Addresses.Money);
        var next = current + (uint)yen;
        game.Write(Addresses.Money, [(byte)(next >> 24), (byte)(next >> 16),
                                     (byte)(next >> 8), (byte)next]);
        Console.WriteLine($"    money {current:N0} -> {next:N0} yen");
    }

    // Soul points come from Archipelago, not from levelling up. There is no way
    // to tell what caused a gain, only that one happened - so hold a baseline
    // and take back anything that appears without the client having granted it.
    // Spending is allowed through and lowers the baseline.
    //
    // Runs after ApplyReceivedItems so a grant this tick has already moved the
    // baseline and is not immediately clawed back.
    // True until the first ability purchase is reported. Akiyama's tutorial
    // will not continue until you buy one ability, so the points that pay for
    // it must survive - confiscating them softlocks the game.
    //
    // Keyed on "has an ability check ever been sent", not "does the player own
    // an ability": SyncAbilities clears the bit after reporting, so an
    // ownership test would flip back to false after every purchase and let the
    // player farm points indefinitely.
    // One point until the tutorial purchase, which will not advance without it,
    // and none after. Capping at 1 rather than leaving it open stops the player
    // banking points and buying several abilities before the first is recorded.
    private void EnforceSoulPoints()
    {
        var cap = _tutorialAbility < 0 ? 1 : 0;
        var current = game.ReadU8(Addresses.SoulPoints);
        if (current <= cap)
        {
            _lastSuppressed = -1;
            return;
        }

        game.Write(Addresses.SoulPoints, [(byte)cap]);
        if (current != _lastSuppressed)
        {
            Console.WriteLine($"  soul points {current} -> {cap} (abilities come from the multiworld)");
            _lastSuppressed = current;
        }
    }

    private void CheckGoal()
    {
        if (_goalSent) return;

        var maxed = Hostesses.AkiyamaStorylinesComplete(game);
        var previously = _goalPrev;
        _goalPrev = maxed;

        if (previously is null)
        {
            if (maxed) Console.WriteLine("  hostesses already maxed - adopted, not reported");
            return;
        }
        if (!maxed || previously.Value) return;

        Console.WriteLine("  GOAL: both hostess storylines complete");
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
