from BaseClasses import Item, ItemClassification

BASE_ID = 8_960_000


# name -> (offset from BASE_ID, classification, in-game item id or None)
#
# The business cards are NOT the in-game key items of the same name. The game
# hands those out as receipts and does not gate on them; receiving one of these
# makes the client set the hostess availability flag. The names match the game's
# so the player knows what they got.
ITEM_TABLE: dict[str, tuple[int, ItemClassification, int | None]] = {
    "Erika's Business Card": (0, ItemClassification.progression, None),
    "Yuna's Business Card": (1, ItemClassification.progression, None),
    "Submachine Gun Ammo": (2, ItemClassification.filler, 29),
}

ITEM_NAME_TO_ID = {name: BASE_ID + offset for name, (offset, _, _) in ITEM_TABLE.items()}

HOSTESS_CARDS = ("Erika's Business Card", "Yuna's Business Card")

FILLER_ITEM = "Submachine Gun Ammo"


class YakuzaDeadSoulsItem(Item):
    game = "Yakuza Dead Souls"
