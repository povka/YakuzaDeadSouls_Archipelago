from dataclasses import dataclass

from Options import PerGameCommonOptions, Range, Toggle


class MaxLevelCheck(Range):
    """Highest character level that is a location.

    Every level from 2 up to this becomes a check. Archipelago has no idea how
    hard levelling is, so set this no higher than you are confident of reaching
    - a level you never hit is a check you can never make, and the seed can put
    something it needs behind it.
    """

    display_name = "Max Level Check"
    range_start = 2
    range_end = 100
    default = 20


class StartWithOneCard(Toggle):
    """Start with a random hostess business card already unlocked.

    Without this, both cards sit in the multiworld and the run has nothing to
    work toward locally until one arrives.
    """

    display_name = "Start With One Hostess Card"


@dataclass
class YakuzaDeadSoulsOptions(PerGameCommonOptions):
    max_level_check: MaxLevelCheck
    start_with_one_card: StartWithOneCard
