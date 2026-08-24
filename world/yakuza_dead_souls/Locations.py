from BaseClasses import Location

from .Abilities import ABILITY_NAMES

BASE_ID = 8_960_000

# Song ids are the index into the per-song score table at RAM 0x01547CD4,
# 4 bytes per song. Only two names are confirmed in-game so far; the rest are
# placeholders.
#
# WARNING: location names go into the datapackage. Renaming one invalidates
# seeds generated with the old name, so confirm the real titles before this
# world is used for anything you want to keep.
SONG_NAMES: dict[int, str] = {
    0x00: "Karaoke Song 00",
    0x01: "Karaoke Song 01",
    0x02: "Where Has Your Touch Gone?",
    0x03: "Karaoke Song 03",
    0x04: "Karaoke Song 04",
    0x05: "Karaoke Song 05",
    0x06: "Karaoke Song 06",
    0x07: "Karaoke Song 07",
    0x08: "Pure Love in Kamurocho",
    0x09: "Raindrops",
    0x0A: "GET to the Top!",
}

SCORE_TIERS = (800, 850, 900)

MAX_TIERS_PER_SONG = 10  # id spacing, so adding a tier never shifts existing ids


def _location_name(song_id: int, tier: int) -> str:
    return f"{SONG_NAMES[song_id]}: {tier}+"


LOCATION_NAME_TO_ID: dict[str, int] = {}

# location name -> (song id, score threshold), for the client
LOCATION_SONG_TIER: dict[str, tuple[int, int]] = {}

for _song_id in sorted(SONG_NAMES):
    for _index, _tier in enumerate(SCORE_TIERS):
        _name = _location_name(_song_id, _tier)
        LOCATION_NAME_TO_ID[_name] = BASE_ID + _song_id * MAX_TIERS_PER_SONG + _index
        LOCATION_SONG_TIER[_name] = (_song_id, _tier)

# Buying an ability is the check. Offset well clear of the karaoke block, which
# runs to BASE_ID + 109.
ABILITY_LOCATION_BASE = BASE_ID + 1000

# location name -> ability index, for the client
LOCATION_ABILITY: dict[str, int] = {}

for _index, _ability in enumerate(ABILITY_NAMES):
    _name = f"Ability: {_ability}"
    LOCATION_NAME_TO_ID[_name] = ABILITY_LOCATION_BASE + _index
    LOCATION_ABILITY[_name] = _index

ALL_LOCATIONS = tuple(LOCATION_NAME_TO_ID)
KARAOKE_LOCATIONS = tuple(LOCATION_SONG_TIER)
ABILITY_LOCATIONS = tuple(LOCATION_ABILITY)


class YakuzaDeadSoulsLocation(Location):
    game = "Yakuza Dead Souls"
