import pkgutil

# ability_bits.tsv is copied in from the repo's data/ by build-apworld.ps1, so
# the client and the apworld read the same file. Its line order defines the id
# ordering on both sides - reorder it and existing seeds break.
#
# pkgutil.get_data works through zipimport, which matters because an apworld is
# loaded straight out of the .apworld zip.
_RAW = pkgutil.get_data(__name__, "ability_bits.tsv")
if _RAW is None:
    raise RuntimeError("ability_bits.tsv is missing from the apworld")


def _names() -> list[str]:
    found = []
    for line in _RAW.decode("utf-8").splitlines():
        parts = line.split("\t")
        if len(parts) >= 3 and parts[2].strip():
            found.append(parts[2].strip())
    return found


ABILITY_NAMES = tuple(_names())

if len(ABILITY_NAMES) != len(set(ABILITY_NAMES)):
    raise RuntimeError("duplicate ability names - location names would collide")
