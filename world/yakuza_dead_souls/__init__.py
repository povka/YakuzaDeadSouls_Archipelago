from BaseClasses import Item, ItemClassification, Location, Region, Tutorial
from worlds.AutoWorld import WebWorld, World

from .Data import (
    ABILITY_ITEMS,
    AMMO_MAX,
    AMMO_MIN,
    GAME_NAME,
    HOSTESS_CARDS,
    ITEM_TABLE,
    LOCATION_TABLE,
    SOUL_POINTS_MAX,
    SOUL_POINTS_MIN,
    SOUL_POINTS_TOTAL,
    SOUL_POINT_ITEM_COUNT,
    ammo_item_name,
    soul_points_item_name,
)
from .Options import YakuzaDeadSoulsOptions

CLASSIFICATIONS = {
    "progression": ItemClassification.progression,
    "useful": ItemClassification.useful,
    "filler": ItemClassification.filler,
}


class YakuzaDeadSoulsItem(Item):
    game = GAME_NAME


class YakuzaDeadSoulsLocation(Location):
    game = GAME_NAME


class YakuzaDeadSoulsWeb(WebWorld):
    theme = "dirt"
    tutorials = [
        Tutorial(
            "Multiworld Setup Guide",
            "A guide to setting up Yakuza Dead Souls for Archipelago.",
            "English",
            "setup_en.md",
            "setup/en",
            ["asapaska"],
        )
    ]


class YakuzaDeadSoulsWorld(World):
    """Yakuza Dead Souls on PlayStation 3"""

    game = GAME_NAME
    web = YakuzaDeadSoulsWeb()

    options_dataclass = YakuzaDeadSoulsOptions
    options: YakuzaDeadSoulsOptions

    item_name_to_id = {name: data[0] for name, data in ITEM_TABLE.items()}
    location_name_to_id = LOCATION_TABLE

    origin_region_name = "Akiyama"

    def create_regions(self) -> None:
        akiyama = Region("Akiyama", self.player, self.multiworld)
        akiyama.add_locations(LOCATION_TABLE, YakuzaDeadSoulsLocation)
        self.multiworld.regions.append(akiyama)

    def create_item(self, name: str) -> YakuzaDeadSoulsItem:
        item_id, classification = ITEM_TABLE[name]
        return YakuzaDeadSoulsItem(
            name, CLASSIFICATIONS[classification], item_id, self.player
        )

    def get_filler_item_name(self) -> str:
        # Soul points are no longer filler - the pool below places an exact
        # number of them, so filler is ammo only.
        return ammo_item_name(self.random.randint(AMMO_MIN, AMMO_MAX))

    def create_items(self) -> None:
        cards = list(HOSTESS_CARDS)

        if self.options.start_with_one_card:
            granted = self.random.choice(cards)
            cards.remove(granted)
            self.multiworld.push_precollected(self.create_item(granted))

        pool = [self.create_item(card) for card in cards]
        pool += [self.create_item(name) for name in ABILITY_ITEMS]

        # Exactly enough soul points to buy every ability - no more, no less -
        # split into random 1-10 amounts. Each starts at the minimum, then the
        # remainder is scattered one point at a time over items with headroom,
        # so the total is exact however the draw falls.
        amounts = [SOUL_POINTS_MIN] * SOUL_POINT_ITEM_COUNT
        headroom = [SOUL_POINTS_MAX - SOUL_POINTS_MIN] * SOUL_POINT_ITEM_COUNT
        remaining = SOUL_POINTS_TOTAL - SOUL_POINTS_MIN * SOUL_POINT_ITEM_COUNT

        candidates = [i for i in range(SOUL_POINT_ITEM_COUNT) if headroom[i]]
        while remaining > 0 and candidates:
            i = self.random.choice(candidates)
            amounts[i] += 1
            headroom[i] -= 1
            remaining -= 1
            if not headroom[i]:
                candidates.remove(i)

        pool += [self.create_item(soul_points_item_name(a)) for a in amounts]

        remaining = len(LOCATION_TABLE) - len(pool)
        pool += [self.create_item(self.get_filler_item_name()) for _ in range(remaining)]

        self.multiworld.itempool += pool

    def set_rules(self) -> None:
        # Every location is reachable from the start, so there are no access
        # rules. Victory needs both hostesses maxed, which needs both cards;
        # the client reports the actual win.
        player = self.player
        self.multiworld.completion_condition[player] = (
            lambda state: state.has_all(HOSTESS_CARDS, player)
        )
