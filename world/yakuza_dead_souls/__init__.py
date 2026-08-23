from BaseClasses import Region, Tutorial
from worlds.AutoWorld import WebWorld, World

from .Items import (
    FILLER_ITEM,
    HOSTESS_CARDS,
    ITEM_NAME_TO_ID,
    ITEM_TABLE,
    YakuzaDeadSoulsItem,
)
from .Locations import (
    ALL_LOCATIONS,
    LOCATION_NAME_TO_ID,
    YakuzaDeadSoulsLocation,
)
from .Options import YakuzaDeadSoulsOptions


class YakuzaDeadSoulsWeb(WebWorld):
    theme = "dirt"
    tutorials = [
        Tutorial(
            "Multiworld Setup Guide",
            "A guide to setting up Yakuza: Dead Souls for Archipelago.",
            "English",
            "setup_en.md",
            "setup/en",
            ["asapaska"],
        )
    ]


class YakuzaDeadSoulsWorld(World):
    """Yakuza Dead Souls on PlayStation 3"""

    game = "Yakuza Dead Souls"
    web = YakuzaDeadSoulsWeb()

    options_dataclass = YakuzaDeadSoulsOptions
    options: YakuzaDeadSoulsOptions

    item_name_to_id = ITEM_NAME_TO_ID
    location_name_to_id = LOCATION_NAME_TO_ID

    origin_region_name = "Akiyama"

    def create_regions(self) -> None:
        akiyama = Region("Akiyama", self.player, self.multiworld)
        akiyama.add_locations(
            {name: LOCATION_NAME_TO_ID[name] for name in ALL_LOCATIONS},
            YakuzaDeadSoulsLocation,
        )
        self.multiworld.regions.append(akiyama)

    def create_item(self, name: str) -> YakuzaDeadSoulsItem:
        _, classification, _ = ITEM_TABLE[name]
        return YakuzaDeadSoulsItem(name, classification, ITEM_NAME_TO_ID[name], self.player)

    def get_filler_item_name(self) -> str:
        return FILLER_ITEM

    def create_items(self) -> None:
        cards = list(HOSTESS_CARDS)

        if self.options.start_with_one_card:
            granted = self.random.choice(cards)
            cards.remove(granted)
            self.multiworld.push_precollected(self.create_item(granted))

        pool = [self.create_item(card) for card in cards]

        remaining = len(ALL_LOCATIONS) - len(pool)
        pool += [self.create_item(FILLER_ITEM) for _ in range(remaining)]

        self.multiworld.itempool += pool

    def set_rules(self) -> None:
        # Every karaoke location is reachable from the start, so there are no
        # access rules. Victory needs both hostesses maxed, which needs both
        # cards; the client reports the actual win.
        player = self.player
        self.multiworld.completion_condition[player] = (
            lambda state: state.has_all(HOSTESS_CARDS, player)
        )
