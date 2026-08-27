using YakuzaDeadSouls.Ps3;

namespace YakuzaDeadSouls.ApClient;

// Single source of truth for every id, name and amount in the world.
// `ydsclient --emit-world <dir>` writes the Python mirror the apworld imports,
// so the two cannot drift. Change things here, never in Data.py.
public static class ApIds
{
    public const string GameName = "Yakuza Dead Souls";
    public const long BaseId = 8_960_000;

    // --- karaoke ---------------------------------------------------------

    // Index is the song id in the per-song score table at RAM 0x01547CD4.
    public static readonly string[] SongNames =
    [
        "I Wanna Change Myself",
        "Kamurocho Lullaby",
        "Where Has Your Touch Gone?",
        "Shooting Star",
        "Saturday Night☆Lover",
        "Summer Memories",
        "Maiden-Colored Life",
        "Machine Gun Kiss",
        "Pure Love in Kamurocho",
        "Raindrops",
        "GET to the Top!",
    ];

    // One check per song. Lower tiers were free checks; higher ones were
    // three more rows in every tracker for the same act.
    public static readonly int[] ScoreTiers = [750];

    // --- character scoping -----------------------------------------------

    // The story runs Akiyama -> Majima -> Goda -> Kiryu -> Finale and does not
    // let you return to an earlier part. So a location only one character can
    // reach is MISSABLE for everyone after them: putting an item a later
    // character needs behind it produces a seed that cannot be finished.
    //
    // Finale is exempt - everything you collected carries into it, so a Finale
    // item is safe anywhere.
    [Flags]
    public enum Characters
    {
        None = 0,
        Akiyama = 1,
        Majima = 2,
        Goda = 4,
        Kiryu = 8,
        Finale = 16,
        AllParts = Akiyama | Majima | Goda | Kiryu,
    }

    // Who can sing each song. Only Akiyama's access is verified in-game; the
    // Majima entries come from the player. Goda and Kiryu are unmapped, so
    // songs default to Akiyama alone, which is the conservative assumption -
    // it over-restricts placement rather than producing an unwinnable seed.
    public static readonly Characters[] SongCharacters =
    [
        Characters.Kiryu,                                               // 00 I wanna Change Myself
        Characters.Kiryu,                                               // 01 Kamurocho Lullaby
        Characters.Akiyama,                                             // 02 Where Has Your Touch Gone?
        Characters.Goda,                                                // 03 Shooting Star
        Characters.Goda,                                                // 04 Saturday Night☆Lover
        Characters.Kiryu,                                               // 05 Summer Memories
        Characters.Kiryu,                                               // 06 Maiden-Colored Life
        Characters.Kiryu,                                               // 07 Machine Gun Kiss
        Characters.Akiyama | Characters.Goda | Characters.Kiryu,        // 08 Pure Love in Kamurocho
        Characters.Akiyama | Characters.Majima | Characters.Kiryu,      // 09 Raindrops
        Characters.Akiyama | Characters.Majima | Characters.Kiryu,      // 0A GET to the Top!
    ];

    // Which characters this build actually covers. Locations no playable
    // character can reach must not exist - Akiyama can sing only 4 of the 11
    // songs, so creating all 11 produces checks nobody can ever complete.
    public const Characters Playable = Characters.Akiyama;

    public static bool Reachable(Characters location) => (location & Playable) != 0;

    // Everything currently in the world belongs to Akiyama: his abilities, his
    // hostesses. Soul points and ammo are unscoped - any character can use them
    // and nothing requires them, so they may be placed anywhere.
    public static Characters ItemCharacters(long itemId)
    {
        if (itemId == ErikaCard || itemId == YunaCard) return Characters.Akiyama;
        if (AbilityIndexOfItem(itemId) is not null) return Characters.Akiyama;
        return Characters.None;
    }

    // An item may sit at a location when every character who needs it can reach
    // that location. Unscoped and Finale-only items are always fine.
    public static bool MayPlace(Characters item, Characters location) =>
        item == Characters.None || item == Characters.Finale || (item & location) == item;

    // Id spacing per song, so adding a tier never shifts existing ids.
    public const int MaxTiersPerSong = 10;

    // --- locations -------------------------------------------------------

    public static long KaraokeLocationId(int songId, int tierIndex) =>
        BaseId + songId * MaxTiersPerSong + tierIndex;

    // --- shops -----------------------------------------------------------

    // Each shop is a file (shopNNNN.bin) with a fixed number of slots. Buying
    // slot N is the check; the client rewrites what that slot shows and sells.
    // Only Poppo is mapped so far - the stock is static across characters and
    // parts, so one table covers every playthrough.
    public readonly record struct ShopDef(
        string File, string Name, int Slots, Characters Who, bool Excluded);

    // Loaded from data/shops.tsv, which `ydsitems shopwatch` writes while the
    // player walks into each shop. Columns: file, name, slots, available, stock.
    // `available = no` marks a shop that stops existing partway through the
    // story - its slots would be permanently missable checks.
    public static readonly ShopDef[] ShopDefs = LoadShops();

    private static ShopDef[] LoadShops()
    {
        var path = FindData("shops.tsv");
        if (path is null) return [];
        var list = new List<ShopDef>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var parts = line.Split('	');
            if (parts.Length < 4) continue;
            if (!int.TryParse(parts[2].Trim(), out var slots) || slots <= 0) continue;
            if (!parts[3].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)) continue;
            var excluded = parts.Length >= 5
                && parts[4].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
            list.Add(new ShopDef(parts[0].Trim(), parts[1].Trim(), slots,
                                 Characters.AllParts, excluded));
        }
        return [.. list];
    }

    private static string? FindData(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 7 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "data", name);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    public const long ShopBase = BaseId + 2000;
    public const int MaxSlotsPerShop = 100;

    public static long ShopLocationId(int shopIndex, int slot) =>
        ShopBase + shopIndex * MaxSlotsPerShop + slot;

    public static string ShopLocationName(in ShopDef shop, int slot) =>
        $"{shop.Name} - Slot {slot + 1}";

    public static int? ShopIndexOfFile(string file)
    {
        for (var i = 0; i < ShopDefs.Length; i++)
            if (string.Equals(ShopDefs[i].File, file, StringComparison.OrdinalIgnoreCase))
                return i;
        return null;
    }

    public static (int Shop, int Slot)? ShopSlotOfLocation(long locationId)
    {
        var offset = locationId - ShopBase;
        if (offset < 0) return null;
        var shop = (int)(offset / MaxSlotsPerShop);
        var slot = (int)(offset % MaxSlotsPerShop);
        return shop < ShopDefs.Length && slot < ShopDefs[shop].Slots ? (shop, slot) : null;
    }

    // Karaoke runs to BaseId + 109, so abilities start well clear. Abilities are
    // items only now, but the base is kept because it is also the item base.
    public const long AbilityBase = BaseId + 1000;

    // Shops occupy BaseId + 2000 .. 2799, so levels start at 3000.
    public const long LevelBase = BaseId + 3000;

    // Every level to this exists in the datapackage; the YAML picks how many are
    // actually created. Location ids can never change once a seed exists, so the
    // full range is emitted and the option narrows it.
    public const int MaxLevel = 100;

    public static long LevelLocationId(int level) => LevelBase + level;

    public static string LevelLocationName(int level) => $"Level {level}";

    public static int? LevelOfLocation(long locationId)
    {
        var level = locationId - LevelBase;
        return level >= 2 && level <= MaxLevel ? (int)level : null;
    }

    // --- items -----------------------------------------------------------

    public const long ErikaCard = BaseId + 0;
    public const long YunaCard = BaseId + 1;
    // BaseId + 2 is retired; it was a single-amount ammo item.

    public const long SoulPointsBase = BaseId + 3;
    public const int SoulPointsMin = 1;
    public const int SoulPointsMax = 10;

    // Soul points are Useful, not filler: the pool carries exactly enough to buy
    // every ability, so a seed is always completable but never generous.
    //
    // Until data/ability_bits.tsv carries per-ability costs, this is the observed
    // figure - the player started with 255, bought all 39, and had 16 left.
    public const int ObservedTotalAbilityCost = 239;

    // Enough items that a uniform 1-10 draw averages out to the total:
    // 239 / 5.5 is about 43.
    public const int SoulPointItemCount = 43;

    // The tutorial forces one purchase before suspension of in-game points
    // begins, so the player pays for that one themselves. Archipelago supplies
    // the rest.
    public static int TutorialPurchaseCost =>
        Abilities.CostsKnown ? Abilities.All.Min(a => a.Cost) : 1;

    public static int TotalSoulPoints =>
        (Abilities.CostsKnown ? Abilities.TotalCost : ObservedTotalAbilityCost)
        - TutorialPurchaseCost;

    // Six ammo types, each 1..200 rounds. Ids BaseId+20000 upward:
    //   BaseId + 20000 + typeIndex*200 + (rounds - 1)
    public const long AmmoBase = BaseId + 20_000;
    public const int AmmoMin = 1;
    public const int AmmoMax = 200;
    public const int AmmoSpan = AmmoMax - AmmoMin + 1;

    public readonly record struct AmmoType(ushort ItemId, string Name);

    // Each ammo type has two consecutive item ids in the catalogue; the first
    // of each pair is what shops stock.
    public static readonly AmmoType[] AmmoTypes =
    [
        new(25, "Shotgun Ammo"),
        new(27, "Gatling Gun Ammo"),
        new(29, "Submachine Gun Ammo"),
        new(31, "Rifle Ammo"),
        new(33, "Anti-Materiel Ammo"),
        new(35, "Grenade Launcher Ammo"),
    ];

    public static long AmmoItemId(int type, int rounds) =>
        AmmoBase + type * AmmoSpan + (rounds - AmmoMin);

    public static (ushort ItemId, int Rounds)? AmmoOf(long itemId)
    {
        var offset = itemId - AmmoBase;
        if (offset < 0 || offset >= AmmoTypes.Length * AmmoSpan) return null;
        return (AmmoTypes[(int)(offset / AmmoSpan)].ItemId, (int)(offset % AmmoSpan) + AmmoMin);
    }

    // Any in-game item granted straight to the inventory: shop goods and guns
    // share this namespace, so their ids can never collide.
    public const long GameItemBase = BaseId + 30_000;

    public static readonly ushort[] VanillaShopItems = LoadVanillaShopItems();

    private static ushort[] LoadVanillaShopItems()
    {
        var path = FindData("shops.tsv");
        if (path is null) return [];
        var ids = new SortedSet<ushort>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var parts = line.Split('	');
            if (parts.Length < 6) continue;
            foreach (var entry in parts[5].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = entry.IndexOf(':');
                if (colon > 0 && ushort.TryParse(entry[..colon], out var id) && id > 1) ids.Add(id);
            }
        }
        return [.. ids];
    }

    public static long GameItemId(ushort gameItemId) => GameItemBase + gameItemId;

    public static ushort? GameItemOf(long itemId)
    {
        var offset = itemId - GameItemBase;
        return offset > 1 && offset < 1128 ? (ushort)offset : null;
    }

    public static readonly (ushort Id, string Name)[] Guns = LoadGuns();

    private static (ushort Id, string Name)[] LoadGuns()
    {
        var path = FindData("guns.tsv");
        if (path is null) return [];
        var list = new List<(ushort, string)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var parts = line.Split('	');
            if (parts.Length < 2 || !ushort.TryParse(parts[0].Trim(), out var id)) continue;
            var raw = parts[1].Trim();
            if (raw.Length == 0 || Inventory.IsPlaceholder(raw)) continue;
            list.Add((id, GunName(raw, seen)));
        }
        return [.. list];
    }

    private static string GunName(string raw, Dictionary<string, int> seen)
    {
        var before = seen.TryGetValue(raw, out var n) ? n : 0;
        seen[raw] = before + 1;
        if (before == 0) return raw;
        return $"{raw} #{before + 1}";
    }

    // Ids BaseId+10000 upward, clear of the ability block at BaseId+1000.
    // Indexed by position in MoneyAmounts, not by value, because the list is
    // deliberately not contiguous.
    public const long MoneyBase = BaseId + 10_000;

    public static readonly int[] MoneyAmounts = BuildMoneyAmounts();

    private static int[] BuildMoneyAmounts()
    {
        var amounts = new List<int> { 67, 69, 420 };
        for (var yen = 1_000; yen <= 50_000; yen += 1_000) amounts.Add(yen);
        return [.. amounts];
    }

    // Ability items share the ability *location* base; items and locations are
    // separate id namespaces in Archipelago, so the overlap is harmless.
    public static long AbilityItemId(int index) => AbilityBase + index;

    // --- names -----------------------------------------------------------

    public static string SongName(int songId) =>
        songId >= 0 && songId < SongNames.Length ? SongNames[songId] : $"Karaoke Song {songId:D2}";

    public static string KaraokeLocationName(int songId, int tier) => $"{SongName(songId)}: {tier}+";

    public static string AbilityLocationName(string ability) => $"Ability: {ability}";

    public static string AmmoItemName(int type, int rounds) =>
        $"{AmmoTypes[type].Name} ({rounds})";

    public static string SoulPointsItemName(int amount) => $"Soul Points ({amount})";

    public static string MoneyItemName(int yen) => $"{yen:N0} Yen";

    // --- decoding what arrived -------------------------------------------

    public static int? SoulPointsAmount(long itemId)
    {
        var offset = itemId - SoulPointsBase;
        return offset >= 0 && offset <= SoulPointsMax - SoulPointsMin
            ? (int)offset + SoulPointsMin
            : null;
    }

    public static int? MoneyAmount(long itemId)
    {
        var index = itemId - MoneyBase;
        return index >= 0 && index < MoneyAmounts.Length ? MoneyAmounts[(int)index] : null;
    }

    public static int? AbilityIndexOfItem(long itemId)
    {
        var index = itemId - AbilityBase;
        return index >= 0 && index < Abilities.Count ? (int)index : null;
    }

    public static string Describe(long locationId)
    {
        if (locationId >= AbilityBase)
        {
            var index = (int)(locationId - AbilityBase);
            return index < Abilities.Count ? $"bought {Abilities.All[index].Name}" : $"ability {index}";
        }
        var offset = locationId - BaseId;
        var song = (int)(offset / MaxTiersPerSong);
        var tier = (int)(offset % MaxTiersPerSong);
        return tier < ScoreTiers.Length ? $"{SongName(song)} @ {ScoreTiers[tier]}+" : $"song {song}";
    }
}
