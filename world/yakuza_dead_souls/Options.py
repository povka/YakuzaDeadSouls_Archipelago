from dataclasses import dataclass

from Options import PerGameCommonOptions, Toggle


class StartWithOneCard(Toggle):
    """Start with a random hostess business card already unlocked.

    Without this, both cards sit in the multiworld and the run has nothing to
    work toward locally until one arrives.
    """

    display_name = "Start With One Hostess Card"


@dataclass
class YakuzaDeadSoulsOptions(PerGameCommonOptions):
    start_with_one_card: StartWithOneCard
