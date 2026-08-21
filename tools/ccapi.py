"""CCAPI over its HTTP surface. Console commands only - not memory.

Read this before reaching for CCAPI as a transport.

CCAPI splits into two protocols on port 6333:

  * An **HTTP surface** for console commands - `/ccapi/notify`,
    `/ccapi/shutdown` and friends. Documented, trivial, implemented here.
  * A **binary command-ID protocol** for everything else, including
    `CCAPIGetMemory` and `CCAPISetMemory`. A packet carrying a command id goes
    to the console and it switches on that id. This format is **not publicly
    documented**, and v2.00-2.50 additionally encrypted it.

So memory over CCAPI means one of:

  1. `CCAPI.dll`, which is **32-bit x86**. Every existing Python wrapper
     (iMoD1998/PS3API and the rest) therefore requires 32-bit Python. An
     Archipelago client runs in Archipelago's own 64-bit Python, so the DLL
     cannot be loaded in-process. Dead end for shipping.
  2. A 32-bit helper process that loads the DLL and exposes a local socket.
     Works, costs a second process and an install step for every player.
  3. Reverse engineering the packet format. A side quest, not a project.

None of those are worth it, because PS3MAPI already does memory with a fully
documented protocol - see ps3mapi.py - and the shipping design is an SPRX that
removes the network from the hot path entirely. CCAPI earns its place here for
two other reasons:

  * **Nicer notifications.** Icons, including trophy icons, which is a much
    better fit for "item received" than PS3MAPI's plain text toast.
  * **The tools ecosystem speaks it.** Memory searchers like NetCheatPS3 talk
    CCAPI, and value-scanning on real hardware is worth a lot during RE.

Stdlib only.
"""

from __future__ import annotations

import http.client
import urllib.parse

CCAPI_PORT = 6333
TIMEOUT = 5.0


class CCAPIError(Exception):
    pass


# Icon ids, as OBSERVED ON HARDWARE - not as CCAPI's ccapi.h declares them.
#
# The header's NotifyIcon enum order (Info, Caution, Friend, Slider, WrongWay,
# Dialog, DialogShadow, Text, Pointer, Grab, Hand, Pen, Finger, Arrow,
# ArrowRight, Progress, Trophy1-4) does NOT match what the XMB actually draws
# on Evilnat 4.93. Verified by sending each id and watching the screen:
#
#   id  2 -> friend icon        (matches the header)
#   id 12 -> GOLD TROPHY        (header calls this "Finger")
#   ids 0, 1, 15, 16, 17, 19 -> all fall back to the generic info "i"
#
# So most of the enum is wrong here, and unmapped ids degrade silently to info
# rather than erroring. Only ship an id that has been seen on screen.
#
# Note webMAN's own /notify.ps3mapi ignores its icon parameter entirely - every
# value 0-50 drew the info icon. CCAPI is the only route that honours icons.
ICONS = {
    "info": 0,       # generic "i" - also the fallback for any unmapped id
    "friend": 2,     # confirmed
    "trophy": 12,    # confirmed gold trophy - the right icon for an item drop
}

# Semantic names for the client to use, so call sites do not carry magic ids.
ICON_INFO = 0
ICON_FRIEND = 2
ICON_ITEM = 12

SHUTDOWN_MODES = {"shutdown": 1, "restart": 2, "softreboot": 3}


class CCAPI:
    """The documented HTTP subset of CCAPI. No memory access - see the module
    docstring for why."""

    def __init__(self, host: str, port: int = CCAPI_PORT):
        self.host = host
        self.port = port

    def _get(self, path: str) -> str:
        conn = http.client.HTTPConnection(self.host, self.port, timeout=TIMEOUT)
        try:
            conn.request("GET", path)
            resp = conn.getresponse()
            body = resp.read().decode("utf-8", "replace")
            if resp.status != 200:
                raise CCAPIError(f"{path} -> HTTP {resp.status}")
            return body
        except OSError as exc:
            raise CCAPIError(f"{path} failed: {exc}") from exc
        finally:
            conn.close()

    def available(self) -> bool:
        """Is the CCAPI server answering? Cheap enough to call on startup."""
        try:
            self.notify("", icon="info")
            return True
        except CCAPIError:
            return False

    def notify(self, message: str, icon: str | int = "info") -> None:
        """Pop an XMB toast with an icon, drawn over the running game.

        `icon` is a key of ICONS or a raw id. Use 'trophy' for an item landing.
        Raw ids are allowed so unmapped ones can still be probed, but be aware
        an unknown id silently draws the generic info icon rather than failing.
        """
        if isinstance(icon, int):
            icon_id = icon
        else:
            try:
                icon_id = ICONS[icon.lower()]
            except KeyError:
                raise CCAPIError(
                    f"unknown icon {icon!r}; known: {sorted(ICONS)}"
                ) from None
        msg = urllib.parse.quote(message)
        self._get(f"/ccapi/notify?id={icon_id}&msg={msg}")

    def shutdown(self, mode: str = "softreboot") -> None:
        try:
            value = SHUTDOWN_MODES[mode.lower()]
        except KeyError:
            raise CCAPIError(
                f"unknown mode {mode!r}; expected one of {sorted(SHUTDOWN_MODES)}"
            ) from None
        self._get(f"/ccapi/shutdown?mode={value}")
