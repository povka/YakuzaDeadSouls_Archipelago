using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using YakuzaDeadSouls.Ps3;

namespace YakuzaDeadSouls.ApClient;

public sealed class ClientLoop(GameProcess game, ArchipelagoSession session)
{
    private readonly HashSet<long> _sent = [];
    private int _itemsApplied;
    private bool _goalSent;

    public int PollMilliseconds { get; init; } = 2000;

    public async Task RunAsync(CancellationToken cancel)
    {
        session.MessageLog.OnMessageReceived += OnMessage;

        foreach (var id in session.Locations.AllLocationsChecked)
            _sent.Add(id);
        Console.WriteLine($"  {_sent.Count} location(s) already checked on the server");

        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await PollAsync();
            }
            catch (Ps3Exception ex)
            {
                Console.WriteLine($"  ps3: {ex.Message}");
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
        }
        _itemsApplied = received.Count;
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
