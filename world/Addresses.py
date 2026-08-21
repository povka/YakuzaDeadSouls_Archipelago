"""Confirmed memory addresses for Yakuza: Dead Souls.

Target: NPEB02034 (EU PSN digital). Addresses are **not** portable to the disc
releases; those are unverified and will differ.

Everything here is **big-endian** - the PPU is, and reading a value as
little-endian gives a plausible-looking wrong answer rather than an error.

Rule for this file: nothing goes in until it has been confirmed by changing it
and seeing the game react, or by reading it and seeing it match what the game
displays. A scan candidate is not an address.
"""

from __future__ import annotations

GAME_ID = "NPEB02034"

# Every address here was found against the BASE game with no title update.
# Read from the console's own /dev_hdd0/game/NPEB02034/PARAM.SFO:
#
#   TITLE_ID = NPEB02034      TITLE   = YAKUZA: DEAD SOULS
#   APP_VER  = 01.00          VERSION = 01.00      CATEGORY = HG
#
# Targeting 1.00 deliberately: it is what the PKG installs, and PS3 title
# updates are increasingly unavailable from Sony's servers, so a version that
# needs chasing a download is a version players cannot reach.
#
# A different version will not error - it will read plausible nonsense from the
# same addresses. Call verify_version() before trusting anything here.
APP_VER = "01.00"
PARAM_SFO = f"/dev_hdd0/game/{GAME_ID}/PARAM.SFO"


def verify_version(host: str) -> tuple[bool, str]:
    """Check the console is running the version these addresses belong to.

    Returns (ok, detail). Cheap - one HTTP GET - and worth doing at connect,
    because a version mismatch is otherwise silent: the addresses still read,
    they just mean something else.
    """
    import http.client
    import struct

    try:
        conn = http.client.HTTPConnection(host, 80, timeout=10)
        try:
            conn.request("GET", PARAM_SFO)
            resp = conn.getresponse()
            data = resp.read()
            if resp.status != 200:
                return False, f"cannot read PARAM.SFO (HTTP {resp.status})"
        finally:
            conn.close()
    except OSError as exc:
        return False, f"cannot reach console: {exc}"

    if data[:4] != b"\x00PSF":
        return False, "PARAM.SFO is not a valid SFO"

    _magic, _ver, key_off, data_off, count = struct.unpack_from("<IIIII", data, 0)
    found = {}
    for i in range(count):
        ko, fmt, dlen, _dmax, do = struct.unpack_from("<HHIII", data, 20 + i * 16)
        end = data.index(b"\x00", key_off + ko)
        key = data[key_off + ko:end].decode()
        raw = data[data_off + do:data_off + do + dlen]
        found[key] = (struct.unpack("<I", raw[:4])[0] if fmt == 0x0404
                      else raw.rstrip(b"\x00").decode("utf-8", "replace"))

    title_id = found.get("TITLE_ID", "?")
    app_ver = found.get("APP_VER", "?")
    if title_id != GAME_ID:
        return False, f"wrong game: {title_id} ({found.get('TITLE', '?')})"
    if app_ver != APP_VER:
        return False, (f"{title_id} is version {app_ver}, addresses are for "
                       f"{APP_VER}. They will read wrong values, not fail.")
    return True, f"{title_id} {app_ver} - matches"

# Segment layout, read live from the EBOOT's ELF program headers.
# See tools/elfmap.py.
CODE_BASE = 0x00010000
CODE_END = 0x01310768   # RX, 19.0 MB - function addresses live here
DATA_BASE = 0x01320000
DATA_END = 0x0172C408   # RW, 4.0 MB - game state lives here
ENTRY = 0x01353C20      # an OPD in the data segment, not code (PPC64 ELFv1)

# Inter-segment page padding: 63,640 bytes claimed by no program header,
# reading as zeros. The safe place to test a write.
SCRATCH_BASE = 0x01310768
SCRATCH_END = 0x01320000


class Money:
    """Yen. Confirmed by writing 12345 and watching the HUD follow."""

    ADDR = 0x01537E18
    KIND = "u32"


class Exp:
    """Accumulated experience, NOT "points to next level".

    The game stores exp counting up and computes the displayed "N to next
    level" as threshold - exp. With a level-1 threshold of 150, exp 50 shows
    as "100 to next level".

    There are two copies four bytes apart. **Only ADDR is authoritative** -
    writing to MIRROR alone changed nothing on screen, writing to ADDR moved
    the display immediately. Neither is rewritten by the game, so the mirror
    is not a continuously-refreshed scratch value; it is simply not the one
    read when the display recalculates.
    """

    ADDR = 0x0154BDCC       # confirmed: 50 -> 100 moved the display
    MIRROR = 0x0154BDC8     # copy; writing here alone does nothing
    KIND = "u32"
    LEVEL_1_THRESHOLD = 150


class Health:
    """HP as a current/max u16 pair. The game shows a bar, never a number.

    Confirmed by writing 90 over 300 and watching the bar drop to roughly a
    third. Neither value is rewritten by the game, so this is storage rather
    than a derived copy.
    """

    CURRENT = 0x0154BDB4
    MAX = 0x0154BDB6
    KIND = "u16"
    DEFAULT = 300


# ---------------------------------------------------------------------------
# The character stats struct
# ---------------------------------------------------------------------------
# Unlike money - which sits alone in a field of zeros - this region is densely
# populated and is a real structure. Three confirmed values live in it, so its
# unknown fields are the cheapest place to look for more.
#
#   offset  address     bytes            meaning
#   +0x00   0x0154BDB0  00 00 00 02      u32 = 2            unknown
#   +0x04   0x0154BDB4  01 2C            u16 = 300          HP current  CONFIRMED
#   +0x06   0x0154BDB6  01 2C            u16 = 300          HP max      CONFIRMED
#   +0x08   0x0154BDB8  00 00 00 00                         unknown
#   +0x0C   0x0154BDBC  45 7A 00 00      f32 = 4000.0       unknown
#   +0x10   0x0154BDC0  3F 80 00 00      f32 = 1.0          unknown (multiplier?)
#   +0x14   0x0154BDC4  01 00 00 00                         unknown
#   +0x18   0x0154BDC8  <exp>            u32                EXP mirror, inert
#   +0x1C   0x0154BDCC  <exp>            u32                EXP         CONFIRMED
#
# Money is ~80 KB away (0x13FB0), so this is not one single save block, but
# both land in a 0x0153-0x0155 window inside a 4 MB segment. A broader
# player-data region is likely; sweeping that window directly is cheap.
STATS_BASE = 0x0154BDB0
OFF_HP_CURRENT = 0x04
OFF_HP_MAX = 0x06
OFF_EXP_MIRROR = 0x18
OFF_EXP = 0x1C

# ---------------------------------------------------------------------------
# An 8-byte record table at 0x01536628-0x015367B8 - NOT the player inventory
# ---------------------------------------------------------------------------
# CAUTION: this was initially assumed to be the inventory and it is not. With a
# player carrying only the Gangster's Pistol and no items at all, the table
# still holds ~38 populated records with counts of 13, 19, 26, 40, 60 and so
# on. So `count` is not "quantity the player owns".
#
# What it *is* remains open. Candidates: a static item/weapon definition table
# (it sits in initialised .data, which fits), per-weapon capacity or default
# load, a shop stock list, or another character's loadout - Dead Souls has four
# protagonists. Writing a well-formed record into a free slot produced no item
# in the player's menu, which argues against it being an owned-items list.
#
# The record FORMAT below is solid - it was decoded from real data and the ammo
# write through it worked. Only the table's *meaning* is wrong/unknown.
#
#   offset  type  field
#   +0x00   u16   count      0xFFFF means INFINITE (that is the "of infinity"
#                            the game shows for some ammo)
#   +0x02   u16   flag       almost always 1; one record held 999
#   +0x04   u8    type       0 = weapon/ammo slot, 2 = item/consumable stack
#   +0x05   u8    item id
#   +0x06   u16   tail       FFFF on item stacks, 0000 on weapon slots
#
# Slots that look empty:
#   "empty" item slot   00 00 00 01 00 00 FF FF   (count 0, id 0, tail FFFF)
#   "empty" weapon slot 00 00 00 01 00 01 00 00   (count 0, id 1, tail 0000)
# Filling one of these with a valid-looking record did NOT grant an item, so
# do not treat them as free inventory slots until the real table is found.
#
# Item ids seen in a fresh save: 128, 131, 134, 137, 140, 143, 146, 149, 152,
# 156, 157, 162, 165, 174, 187, 194, 199, 203, 204, 222. Ids are u8, so the
# space is small enough to enumerate exhaustively later by writing each value
# and reading the name off the menu.
TABLE_BASE = 0x01536628   # purpose unknown - see caution above
TABLE_END = 0x015367C0
RECORD_SIZE = 8

OFF_COUNT = 0x00
OFF_FLAG = 0x02
OFF_TYPE = 0x04
OFF_ID = 0x05
OFF_TAIL = 0x06

COUNT_INFINITE = 0xFFFF
TYPE_WEAPON = 0
TYPE_ITEM = 2

EMPTY_ITEM_SLOT = bytes.fromhex("0000000100 00FFFF".replace(" ", ""))
EMPTY_WEAPON_SLOT = bytes.fromhex("0000000100 010000".replace(" ", ""))


class AmmoDisplay:
    """The ammo number the HUD SHOWS. **Not** the ammo the gun actually has.

    Writing 99 here made the UI read 99 (later 104), but the weapon still
    reloaded after 13 rounds - its real magazine count is stored elsewhere and
    was untouched. So this is a display field, and the authoritative count is
    still to be found.

    This is the same trap as Exp.MIRROR but inverted: there, two copies existed
    and only one drove the display; here, the value that drives the display is
    not the one that drives behaviour. Both cases prove the same point - a
    write that visibly "works" has still only proven what that one address
    feeds, not that it is the real value.

    Useful anyway: cosmetic ammo, and a marker for where the real one may live.
    """

    EQUIPPED_SLOT = 0x01536730
    ADDR = 0x01536731       # u8 low byte of the count; u16 at EQUIPPED_SLOT
    KIND = "u8"
    REAL_MAGAZINE = None    # unfound: the gun reloads on ITS count, not this


def make_record(item_id: int, count: int, kind: int = TYPE_ITEM) -> bytes:
    """Build a record in this table's format.

    The format is verified; what the table controls is not. Writing one of
    these into a free slot did not grant an item.
    """
    import struct
    tail = b"\xff\xff" if kind == TYPE_ITEM else b"\x00\x00"
    return struct.pack(">HH", count & 0xFFFF, 1) + bytes([kind, item_id & 0xFF]) + tail


# ---------------------------------------------------------------------------
# THE PLAYER INVENTORY - confirmed, and this is how Archipelago grants items
# ---------------------------------------------------------------------------
# An array of 8-byte records at stride 8:
#
#   +0x00  u16  item id
#   +0x02  u16  padding / unknown, observed 0
#   +0x04  u32  quantity
#
# A free slot is eight zero bytes. **Writing one well-formed record into a free
# slot grants the item** - confirmed by writing id 11 qty 1 and seeing a third
# Tauriner appear in the menu.
#
# Crucially, nothing else has to be updated. The item-count bytes at
# 0x0160FD1A and 0x01615152 still read 2 after the write, and the header at
# INVENTORY_HEADER still read 6, yet the game showed 3 items. So those counters
# are derived or cosmetic, and the slot array is authoritative. Granting an
# item is exactly one 8-byte write.
#
# Items do NOT stack. Buying two Tauriners produced two separate records, each
# qty 1. That is what made this findable: the signature to search for was two
# slots receiving the SAME id at different times, not a stack count going up.
INVENTORY_HEADER = 0x01534DE0   # read 6 with 2-3 items; meaning unknown, inert
INVENTORY_BASE = 0x01534DE4
INVENTORY_STRIDE = 8
INVENTORY_SLOTS = 64            # not yet bounded; slots read as zeros beyond use

FREE_SLOT = b"\x00" * 8

# Item ids seen so far. The space looks small and dense, so it can be
# enumerated by writing each id and reading the name off the menu - which gives
# the randomizer's item pool without ever finding a name table.
ITEM_IDS = {
    11: "Tauriner",
}


def make_item(item_id: int, quantity: int = 1) -> bytes:
    """One inventory record. Write over a free slot to grant the item."""
    import struct
    return struct.pack(">HHI", item_id & 0xFFFF, 0, quantity)


def find_free_slot(proc, base: int = INVENTORY_BASE,
                   slots: int = INVENTORY_SLOTS) -> int | None:
    """First all-zero record in the array, or None if full.

    Reads the whole array in one request - at stride 8 and 64 slots that is
    512 bytes, well inside a single round trip.
    """
    raw = proc.read(base, slots * INVENTORY_STRIDE)
    if raw is None:
        return None
    for i in range(slots):
        if raw[i * INVENTORY_STRIDE:(i + 1) * INVENTORY_STRIDE] == FREE_SLOT:
            return base + i * INVENTORY_STRIDE
    return None


def grant_item(proc, item_id: int, quantity: int = 1) -> int | None:
    """Give the player an item. Returns the slot used, or None if full."""
    slot = find_free_slot(proc)
    if slot is None:
        return None
    return slot if proc.write(slot, make_item(item_id, quantity)) else None


UNCONFIRMED: dict[str, int] = {}
