from BaseClasses import (Item, ItemClassification, Location,
                         LocationProgressType, Region, Tutorial)
from worlds.generic.Rules import add_item_rule, set_rule
from worlds.AutoWorld import WebWorld, World

from .Data import (
    ABILITY_ITEMS,
    AMMO_MAX,
    AMMO_MIN,
    AMMO_TYPES,
    EXCLUDED_LOCATIONS,
    GUN_ITEMS,
    VANILLA_SHOP_ITEMS,
    MONEY_AMOUNTS,
    GAME_NAME,
    HOSTESS_CARDS,
    CHAR_FINALE,
    CHAR_NONE,
    ITEM_CHARACTERS,
    ITEM_TABLE,
    LEVEL_LOCATIONS,
    LOCATION_CHARACTERS,
    LOCATION_REQUIRES,
    LOCATION_TABLE,
    ammo_item_name,
    money_item_name,
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

    # Every level up to 100 is in the datapackage so ids never move between
    # seeds, but only the ones the YAML asks for become real locations.
    def _active_locations(self) -> dict[str, int]:
        cap = self.options.max_level_check.value
        return {
            name: location_id
            for name, location_id in LOCATION_TABLE.items()
            if LEVEL_LOCATIONS.get(name, 0) <= cap
        }

    def create_regions(self) -> None:
        akiyama = Region("Akiyama", self.player, self.multiworld)
        akiyama.add_locations(self._active_locations(), YakuzaDeadSoulsLocation)
        self.multiworld.regions.append(akiyama)

    def create_item(self, name: str) -> YakuzaDeadSoulsItem:
        item_id, classification = ITEM_TABLE[name]
        return YakuzaDeadSoulsItem(
            name, CLASSIFICATIONS[classification], item_id, self.player
        )

    def get_filler_item_name(self) -> str:
        # Kind first, then the detail, so adding variants to one kind does not
        # change how often the others appear.
        roll = self.random.random()
        if roll < 0.35:
            kind = self.random.choice(AMMO_TYPES)
            return ammo_item_name(kind, self.random.randint(AMMO_MIN, AMMO_MAX))
        if roll < 0.70:
            return money_item_name(self.random.choice(MONEY_AMOUNTS))
        return self.random.choice(VANILLA_SHOP_ITEMS)

    def create_items(self) -> None:
        cards = list(HOSTESS_CARDS)

        if self.options.start_with_one_card:
            granted = self.random.choice(cards)
            cards.remove(granted)
            self.multiworld.push_precollected(self.create_item(granted))

        pool = [self.create_item(card) for card in cards]
        pool += [self.create_item(name) for name in ABILITY_ITEMS]
        pool += [self.create_item(name) for name in GUN_ITEMS]

        remaining = len(self._active_locations()) - len(pool)
        pool += [self.create_item(self.get_filler_item_name()) for _ in range(remaining)]

        self.multiworld.itempool += pool

    def _may_place(self, item_name: str, location_characters: int) -> bool:
        needed = ITEM_CHARACTERS.get(item_name, CHAR_NONE)
        if needed in (CHAR_NONE, CHAR_FINALE):
            return True
        return needed & location_characters == needed

    def set_rules(self) -> None:
        # Only locations that were actually created exist to be looked up.
        active = self._active_locations()

        # The story never lets you return to an earlier character's part, so a
        # location only one character can reach is missable for everyone after
        # them. Keep items a later character needs out of those.
        for name, characters in LOCATION_CHARACTERS.items():
            if characters == 0 or name not in active:
                continue
            location = self.multiworld.get_location(name, self.player)
            add_item_rule(
                location,
                lambda item, c=characters: item.player != self.player
                or self._may_place(item.name, c),
            )

        # Shops that stop existing partway through the story keep their slots as
        # checks, but Archipelago must never route anything needed through them.
        for name in EXCLUDED_LOCATIONS:
            if name in active:
                loc = self.multiworld.get_location(name, self.player)
                loc.progress_type = LocationProgressType.EXCLUDED

        for name, needed in LOCATION_REQUIRES.items():
            if name not in active:
                continue
            set_rule(
                self.multiworld.get_location(name, self.player),
                lambda state, item=needed: state.has(item, self.player),
            )

        # Victory needs both hostesses maxed, which needs both cards; the client
        # reports the actual win.
        player = self.player
        self.multiworld.completion_condition[player] = (
            lambda state: state.has_all(HOSTESS_CARDS, player)
        )
