import logging

from BaseClasses import Item, ItemClassification, Location, Region, Tutorial
from worlds.generic.Rules import add_item_rule
from worlds.AutoWorld import WebWorld, World

from .Data import (
    ABILITY_ITEMS,
    AMMO_MAX,
    AMMO_MIN,
    MONEY_AMOUNTS,
    GAME_NAME,
    HOSTESS_CARDS,
    CHAR_FINALE,
    CHAR_NONE,
    ITEM_CHARACTERS,
    ITEM_TABLE,
    LOCATION_CHARACTERS,
    LOCATION_TABLE,
    SOUL_POINTS_MAX,
    SOUL_POINTS_MIN,
    SOUL_POINTS_TOTAL,
    SOUL_POINT_ITEM_COUNT,
    ammo_item_name,
    money_item_name,
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
        # Kind first, then amount, so adding variants to one kind does not
        # change how often the other appears.
        if self.random.random() < 0.5:
            return ammo_item_name(self.random.randint(AMMO_MIN, AMMO_MAX))
        return money_item_name(self.random.choice(MONEY_AMOUNTS))

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
        #
        # The pool can be too small to carry them all: abilities are both a
        # location and an item, so they cancel out and leave very little room.
        # Clamp rather than overfill - Archipelago drops surplus items silently,
        # which produces a seed where abilities can never be bought and nothing
        # in the log says so.
        free_slots = len(LOCATION_TABLE) - len(pool)
        count = max(0, min(SOUL_POINT_ITEM_COUNT, free_slots))

        amounts = [SOUL_POINTS_MIN] * count
        headroom = [SOUL_POINTS_MAX - SOUL_POINTS_MIN] * count
        remaining = SOUL_POINTS_TOTAL - SOUL_POINTS_MIN * count

        candidates = [i for i in range(count) if headroom[i]]
        while remaining > 0 and candidates:
            i = self.random.choice(candidates)
            amounts[i] += 1
            headroom[i] -= 1
            remaining -= 1
            if not headroom[i]:
                candidates.remove(i)

        pool += [self.create_item(soul_points_item_name(a)) for a in amounts]

        granted = sum(amounts)
        if granted < SOUL_POINTS_TOTAL:
            logging.warning(
                "%s: only %d of %d soul points fit in %d locations, so not every "
                "ability can be bought. Add more locations that are not also "
                "items (shop purchases, substories) to close the gap.",
                self.player_name, granted, SOUL_POINTS_TOTAL, len(LOCATION_TABLE),
            )

        remaining = len(LOCATION_TABLE) - len(pool)
        pool += [self.create_item(self.get_filler_item_name()) for _ in range(remaining)]

        self.multiworld.itempool += pool

    def _may_place(self, item_name: str, location_characters: int) -> bool:
        needed = ITEM_CHARACTERS.get(item_name, CHAR_NONE)
        if needed in (CHAR_NONE, CHAR_FINALE):
            return True
        return needed & location_characters == needed

    def set_rules(self) -> None:
        # The story never lets you return to an earlier character's part, so a
        # location only one character can reach is missable for everyone after
        # them. Keep items a later character needs out of those.
        for name, characters in LOCATION_CHARACTERS.items():
            if characters == 0:
                continue
            location = self.multiworld.get_location(name, self.player)
            add_item_rule(
                location,
                lambda item, c=characters: item.player != self.player
                or self._may_place(item.name, c),
            )

        # Every location is reachable from the start, so there are no access
        # rules. Victory needs both hostesses maxed, which needs both cards;
        # the client reports the actual win.
        player = self.player
        self.multiworld.completion_condition[player] = (
            lambda state: state.has_all(HOSTESS_CARDS, player)
        )
