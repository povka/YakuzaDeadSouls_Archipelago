"""PS3MAPI client: process memory access for the Yakuza Dead Souls client.

The PS3 analogue of the Empire Earth client's Memory.py. Where that opens a
local process with OpenProcess/ReadProcessMemory, this talks to PS3MAPI on a
jailbroken console over the network.

Everything below was **measured against a real console** - Slim CECH-25xx,
Evilnat 4.93, webMAN MOD 1.47.48, PS3MAPI server 0x125 - not written to spec.
Several of the findings are counterintuitive and one of them silently corrupts
data, so read the next section before changing anything.

===========================================================================
THE TRAP: /ps3mapi.ps3?MEMORY GET RETURNS CORRUPT DATA. DO NOT USE IT.
===========================================================================

webMAN MOD 1.47.48's JSON bridge zeroes the **high nibble of every byte** it
returns from a memory read. Verified against webMAN's own GUI viewer at the
same address:

    true (GUI):   7F 45 4C 46 02 02 01 66 ... 00 15 ... 01 35 3C 20
    JSON bridge:  0F 05 0C 06 02 02 01 06 ... 00 05 ... 01 05 0C 00

Every byte whose high nibble is already 0 survives, which is exactly what
makes this dangerous: the data looks structured and plausible. An entire
address map could be built on it before anyone noticed. The giveaway is that
**every byte comes back <= 0x0F**.

The bug is in the response encoder only. Non-memory commands over the JSON
bridge (SERVER GETVERSION, PS3 GETFWVERSION, PS3 NOTIFY) are fine and are
still used here.

===========================================================================
TRANSPORTS
===========================================================================

Two that actually work, in preference order:

**TcpTransport - PS3MAPI on port 7887.** Raw binary over a PASV data
connection, so no hex encoding and no nibble bug by construction, and no small
size cap. The catch: the server is **off by default**, and needs enabling in
webMAN's setup plus a console reboot. Preferred once available.

**HttpTransport - /getmem.ps3mapi on port 80.** webMAN's GUI endpoint, which
returns a correct HTML hexdump. Needs no configuration and works today, but
measured limits are harsh:

  * **256 bytes maximum per request.** Anything larger silently truncates to
    256 - it does not error. HTTP_MAX_READ enforces this and read() splits.
  * **No keep-alive** - the console closes the connection after each request,
    so every read pays a fresh TCP handshake.
  * ~31 ms per request, so roughly 8 KB/sec.

That throughput is fine for watching a handful of known addresses and far too
slow for scanning. Use NetCheatPS3 over CCAPI for scanning, per notes/REVERSE.md.

===========================================================================
OTHER MEASURED QUIRKS
===========================================================================

  * **PROCESS GETALLPID's JSON is ambiguous and must not be parsed.** The
    emitter drops the comma immediately after every hex value, gluing the next
    element on: two live PIDs came back as `0x10102000x10003000`. The true
    values were 0x1010200 and 0x1000300 - note the naive read gives
    0x10003000, an 8-digit value that does not exist. Process listing goes
    through the GUI's <option> list instead, which is unambiguous and carries
    names.
  * **PROCESS GETNAME returns empty**, even for the XMB. The GUI's option
    labels are the only source of process names.
  * **A wrong PID reads as zeros rather than erroring.** Unmapped memory also
    reads as zeros. So zeros never prove anything - validate the PID against
    the process list first.
  * Addresses are parsed as **hex** with or without an 0x prefix. `10000` and
    `0x10000` agree; `2710` is not 10000 decimal.

Two things that are true of the platform rather than the tooling:

  * The PPU is **big-endian**. Every struct format here is '>'.
  * The EBOOT is **ELF64, big-endian**, mapped with its header at 0x00010000.

Stdlib only, so this can be vendored into an apworld with no dependencies.
"""

from __future__ import annotations

import http.client
import re
import socket
import struct
import time
import urllib.parse

HTTP_PORT = 80
TCP_PORT = 7887
TIMEOUT = 15.0

# Measured: 256 exact, anything larger truncates to 256 without an error.
HTTP_MAX_READ = 256
# The TCP path has no such cap; this is just a sane transfer unit.
TCP_MAX_READ = 65536

EBOOT_BASE = 0x00010000
ELF_MAGIC = b"\x7fELF"

# /notify.ps3mapi limits, read off webMAN's own form.
NOTIFY_MAXLEN = 199
# The icon parameter exists and is ACCEPTED but has no effect - every value
# 0-50 draws the generic info "i". Use ccapi.py if the icon matters.
NOTIFY_ICON_MAX = 50

# The `snd` parameter, as heard on hardware. Two different sound systems hide
# behind one list, which the labels do not make obvious:
#
#   1, 2, 3  -> the PHYSICAL CONSOLE BUZZER (the disc-eject / power-on beeper).
#               Loud, hardware, and thoroughly wrong over a game.
#   5        -> the XMB trophy-unlock chime. Confirmed, and exactly right for
#               an item landing.
#   ""       -> silent.
#
# The remaining XMB sounds (0, 4, 6-9) are accepted but unverified by ear.
NOTIFY_SOUND_SILENT = ""
NOTIFY_SOUND_TROPHY = 5
NOTIFY_SOUNDS = {
    "none": "", "trophy": 5,
    # Hardware buzzer - avoid in-game.
    "buzz_simple": 1, "buzz_double": 2, "buzz_triple": 3,
    # XMB sounds, accepted but not yet confirmed by ear.
    "cancel": 0, "cursor": 4, "decide": 6,
    "option": 7, "system_ok": 8, "system_ng": 9,
}

_RESP_STR = re.compile(r'"response"\s*:\s*"([^"]*)"')
_ERR = re.compile(r'"code"\s*:\s*(\d+)\s*,\s*"status"\s*:\s*"([^"]*)"')
# <option value="0x1010200"/>01010200_main_EBOOT.BIN
_OPTION = re.compile(r'<option\s+value="(0x[0-9a-fA-F]+)"\s*/?>([^<]*)')
# 00010000 7F 45 4C 46 ... - up to 16 byte pairs. The final row of a dump can
# be short, and a request under 16 bytes produces only a short row, so this
# must not insist on a full 16.
_DUMP_ROW = re.compile(r'([0-9A-F]{8})((?:\s+[0-9A-F]{2}){1,16})')


class PS3Error(Exception):
    """The console refused a command, or the link dropped."""


class ProcessGone(PS3Error):
    """The game process is no longer running."""


# ---------------------------------------------------------------------------
# Transports
# ---------------------------------------------------------------------------


class HttpTransport:
    """Memory reads via /getmem.ps3mapi. Correct, configuration-free, slow."""

    name = "http"
    max_read = HTTP_MAX_READ
    can_write = False  # /getmem.ps3mapi is read-only; writes need the TCP path

    def __init__(self, host: str, port: int = HTTP_PORT):
        self.host = host
        self.port = port

    def _get(self, path: str) -> str:
        # Deliberately a fresh connection: this endpoint closes it anyway.
        conn = http.client.HTTPConnection(self.host, self.port, timeout=TIMEOUT)
        try:
            conn.request("GET", path)
            resp = conn.getresponse()
            body = resp.read().decode("utf-8", "replace")
            if resp.status != 200:
                raise PS3Error(f"{path} -> HTTP {resp.status}")
            return body
        except (http.client.HTTPException, OSError) as exc:
            raise PS3Error(f"{path} failed: {exc}") from exc
        finally:
            conn.close()

    def available(self) -> bool:
        try:
            self._get("/index.ps3")
            return True
        except PS3Error:
            return False

    def read(self, pid: int, addr: int, size: int) -> bytes:
        if size > self.max_read:
            raise PS3Error(f"{size} exceeds the {self.max_read} byte HTTP cap")
        # The dump renders 16 bytes per row, so ask for a whole number of rows
        # and slice. A read costs ~30 ms whatever its size, so rounding up is
        # free.
        want = min(self.max_read, ((size + 15) // 16) * 16)
        html = self._get(
            f"/getmem.ps3mapi?proc=0x{pid:X}&addr={addr:X}&len={want}"
        )
        out = bytearray()
        for _addr_text, byte_text in _DUMP_ROW.findall(html):
            out += bytes(int(b, 16) for b in byte_text.split())
        if len(out) < size:
            raise PS3Error(
                f"short read at {addr:08X}: {len(out)} of {size} "
                f"(requested {want})"
            )
        return bytes(out[:size])

    def write(self, pid: int, addr: int, payload: bytes) -> None:
        raise PS3Error(
            "the HTTP transport cannot write. Enable the PS3MAPI server on "
            "port 7887 in webMAN's setup and reboot the console."
        )

    def processes(self) -> list[tuple[int, str]]:
        """Authoritative process list, from the GUI's own dropdown.

        Entries with numeric values (LV1 Memory, LV2 Memory, Flash, /dev_hdd0
        and friends) are pseudo-targets, not processes, and are filtered out by
        requiring the 0x prefix.
        """
        html = self._get("/getmem.ps3mapi")
        out = []
        for value, label in _OPTION.findall(html):
            try:
                pid = int(value, 16)
            except ValueError:
                continue
            out.append((pid, label.strip()))
        return out


class TcpTransport:
    """Memory over PS3MAPI's binary protocol on 7887.

    FTP-shaped: a text control connection, and a PASV data connection per
    transfer. Because the payload is raw binary rather than hex, this transport
    is immune to the nibble bug and has no small size cap.

    Written to the documented command set. **Not yet exercised against a
    console** - the server was off during development. Treat as unverified.
    """

    name = "tcp"
    max_read = TCP_MAX_READ
    can_write = True

    def __init__(self, host: str, port: int = TCP_PORT):
        self.host = host
        self.port = port
        self._sock: socket.socket | None = None
        self._buf = b""
        self._binary = False

    # -- link ------------------------------------------------------------

    def available(self) -> bool:
        s = socket.socket()
        s.settimeout(1.5)
        try:
            s.connect((self.host, self.port))
            return True
        except OSError:
            return False
        finally:
            s.close()

    def connect(self) -> None:
        self.close()
        sock = socket.create_connection((self.host, self.port), TIMEOUT)
        sock.settimeout(TIMEOUT)
        self._sock = sock
        self._buf = b""
        self._binary = False
        deadline = time.monotonic() + TIMEOUT
        while time.monotonic() < deadline:
            code, text = self._response()
            if code == 230:
                return
            if code >= 400:
                raise PS3Error(f"console refused the connection: {code} {text}")
        raise PS3Error("no 230 ready response from PS3MAPI")

    def close(self) -> None:
        if self._sock is not None:
            try:
                self._send("DISCONNECT")
            except OSError:
                pass
            try:
                self._sock.close()
            except OSError:
                pass
        self._sock = None
        self._buf = b""

    def _send(self, line: str) -> None:
        if self._sock is None:
            raise PS3Error("not connected")
        self._sock.sendall(line.encode("ascii", "replace") + b"\r\n")

    def _line(self) -> str:
        if self._sock is None:
            raise PS3Error("not connected")
        while b"\r\n" not in self._buf:
            chunk = self._sock.recv(512)
            if not chunk:
                raise PS3Error("control connection closed by console")
            self._buf += chunk
        line, self._buf = self._buf.split(b"\r\n", 1)
        return line.decode("ascii", "replace")

    def _response(self) -> tuple[int, str]:
        line = self._line()
        head = line[:3]
        if not head.isdigit():
            return 0, line
        return int(head), line[4:] if len(line) > 4 else ""

    def _await(self, *accept: int) -> tuple[int, str]:
        while True:
            code, text = self._response()
            if code == 0:
                continue
            if code >= 400:
                raise PS3Error(f"{code} {text}")
            if not accept or code in accept or 200 <= code < 300:
                return code, text

    def command(self, line: str) -> str:
        self._send(line)
        return self._await()[1]

    def _pasv(self) -> socket.socket:
        self._send("PASV")
        _code, text = self._await(227)
        start, end = text.rfind("("), text.rfind(")")
        if start == -1 or end == -1:
            raise PS3Error(f"cannot parse PASV response: {text!r}")
        parts = [int(p) for p in text[start + 1:end].split(",")]
        if len(parts) != 6:
            raise PS3Error(f"cannot parse PASV response: {text!r}")
        host = ".".join(str(p) for p in parts[:4])
        if host == "0.0.0.0":
            host = self.host
        data = socket.create_connection((host, parts[4] * 256 + parts[5]), TIMEOUT)
        data.settimeout(TIMEOUT)
        return data

    def _ensure_binary(self) -> None:
        if not self._binary:
            self.command("TYPE I")
            self._binary = True

    # -- memory ----------------------------------------------------------

    def read(self, pid: int, addr: int, size: int) -> bytes:
        if self._sock is None:
            self.connect()
        self._ensure_binary()
        data = self._pasv()
        try:
            self._send(f"MEMORY GET {pid} {addr:08X} {size}")
            self._await(125, 150)
            out = bytearray()
            while len(out) < size:
                chunk = data.recv(min(65536, size - len(out)))
                if not chunk:
                    break
                out += chunk
        finally:
            data.close()
        self._await(226, 250)
        if len(out) != size:
            raise PS3Error(f"short read at {addr:08X}: {len(out)} of {size}")
        return bytes(out)

    def write(self, pid: int, addr: int, payload: bytes) -> None:
        if self._sock is None:
            self.connect()
        self._ensure_binary()
        data = self._pasv()
        try:
            self._send(f"MEMORY SET {pid} {addr:08X}")
            self._await(125, 150, 350)
            data.sendall(payload)
        finally:
            data.close()
        self._await(226, 250)

    def processes(self) -> list[tuple[int, str]]:
        # GETALLPID's JSON is ambiguous over HTTP, but this is the raw text
        # protocol so it is trustworthy here.
        if self._sock is None:
            self.connect()
        text = self.command("PROCESS GETALLPID")
        out = []
        for tok in text.replace("|", " ").split():
            try:
                pid = int(tok, 0)
            except ValueError:
                continue
            if pid:
                out.append((pid, ""))
        return out


# ---------------------------------------------------------------------------
# Console
# ---------------------------------------------------------------------------


class PS3MAPI:
    """A console. Picks the best available memory transport."""

    def __init__(self, host: str, *, prefer: str = "auto"):
        self.host = host
        self.prefer = prefer
        self.http = HttpTransport(host)
        self.tcp = TcpTransport(host)
        self.transport: HttpTransport | TcpTransport | None = None

    # -- connection ------------------------------------------------------

    def connect(self) -> None:
        if self.prefer in ("auto", "tcp") and self.tcp.available():
            try:
                self.tcp.connect()
                self.transport = self.tcp
                return
            except PS3Error:
                if self.prefer == "tcp":
                    raise
        if self.prefer == "tcp":
            raise PS3Error(
                "PS3MAPI on 7887 is not listening. Enable it in webMAN's "
                "setup and reboot the console."
            )
        if not self.http.available():
            raise PS3Error(f"no webMAN web server on {self.host}:80")
        self.transport = self.http

    def close(self) -> None:
        self.tcp.close()
        self.transport = None

    @property
    def transport_name(self) -> str:
        return self.transport.name if self.transport else "none"

    # -- JSON bridge, for non-memory commands only -----------------------

    def _bridge(self, command: str) -> str:
        """Run a command through /ps3mapi.ps3.

        Safe for everything EXCEPT memory reads - see the module docstring.
        """
        conn = http.client.HTTPConnection(self.host, HTTP_PORT, timeout=TIMEOUT)
        try:
            conn.request("GET", "/ps3mapi.ps3?" + urllib.parse.quote(command))
            resp = conn.getresponse()
            body = resp.read().decode("utf-8", "replace")
        except (http.client.HTTPException, OSError) as exc:
            raise PS3Error(f"{command!r} failed: {exc}") from exc
        finally:
            conn.close()
        err = _ERR.search(body)
        # The bridge reports success the same shape as failure - a plain
        # {"code": 200, "status": "OK"} - so only 3xx and up is an error.
        if err and int(err.group(1)) >= 300:
            raise PS3Error(f"{command!r} -> {err.group(1)} {err.group(2)}")
        m = _RESP_STR.search(body)
        return m.group(1) if m else body

    def server_version(self) -> str:
        try:
            return self._bridge("SERVER GETVERSION")
        except PS3Error:
            return ""

    def firmware(self) -> str:
        return self._bridge("PS3 GETFWVERSION")

    def firmware_pretty(self) -> str:
        raw = self.firmware()
        try:
            digits = f"{int(raw, 16):03X}"
            return f"{int(digits[0])}.{digits[1:]}"
        except ValueError:
            return raw

    def notify(self, message: str, icon: int = 0, sound: str | int = "") -> None:
        """XMB toast, drawn over the running game.

        Goes through /notify.ps3mapi rather than the `PS3 NOTIFY` bridge
        command, because that endpoint also takes an icon and a sound.

        `icon` is 0-50, indexing the XMB's own icon set - 0 is the plain info
        "i". There is no way to supply a custom image here; see NOTIFY_SOUNDS
        and the notes on getting an Archipelago logo.

        Bursts are safe: toasts queue rather than replacing each other, so
        there is no need to throttle sends.
        """
        if len(message) > NOTIFY_MAXLEN:
            message = message[:NOTIFY_MAXLEN - 1] + "…"
        icon = max(0, min(50, int(icon)))
        query = urllib.parse.urlencode(
            {"msg": message, "icon": icon, "snd": sound}
        )
        conn = http.client.HTTPConnection(self.host, HTTP_PORT, timeout=TIMEOUT)
        try:
            conn.request("GET", "/notify.ps3mapi?" + query)
            resp = conn.getresponse()
            resp.read()
            if resp.status != 200:
                raise PS3Error(f"notify -> HTTP {resp.status}")
        except (http.client.HTTPException, OSError) as exc:
            raise PS3Error(f"notify failed: {exc}") from exc
        finally:
            conn.close()

    # -- processes -------------------------------------------------------

    def processes(self) -> list[tuple[int, str]]:
        """Always from the HTTP GUI, which is the only unambiguous source."""
        return self.http.processes()

    def pids(self) -> list[int]:
        return [pid for pid, _ in self.processes()]

    def game_pid(self) -> int | None:
        """The game process: an EBOOT that is not the XMB's vsh.self."""
        for pid, name in self.processes():
            low = name.lower()
            if "vsh.self" in low:
                continue
            if "eboot" in low:
                return pid
        return None

    # -- memory ----------------------------------------------------------

    def get_memory(self, pid: int, addr: int, size: int) -> bytes:
        if self.transport is None:
            self.connect()
        assert self.transport is not None
        out = bytearray()
        cursor = addr
        remaining = size
        while remaining > 0:
            want = min(remaining, self.transport.max_read)
            out += self.transport.read(pid, cursor, want)
            cursor += want
            remaining -= want
        return bytes(out)

    def set_memory(self, pid: int, addr: int, payload: bytes) -> None:
        if self.transport is None:
            self.connect()
        assert self.transport is not None
        self.transport.write(pid, addr, payload)


class ProcessHandle:
    """One game process, with the read/write helpers the EE client has.

    Batching matters far more here than on PC. On the HTTP transport a read
    costs ~31 ms and returns at most 256 bytes, so twenty scattered reads is
    twenty round trips and over half a second. Read one span covering
    everything you watch and slice it locally - that is what read_block and
    Block are for.
    """

    def __init__(self, api: PS3MAPI, pid: int, name: str = ""):
        self.api = api
        self.pid = pid
        self.name = name

    def read(self, addr: int, size: int) -> bytes | None:
        try:
            return self.api.get_memory(self.pid, addr, size)
        except PS3Error:
            return None

    def write(self, addr: int, data: bytes) -> bool:
        try:
            self.api.set_memory(self.pid, addr, data)
            return True
        except PS3Error:
            return False

    def read_block(self, addr: int, size: int) -> "Block | None":
        raw = self.read(addr, size)
        return None if raw is None else Block(addr, raw)

    # -- typed, big-endian ------------------------------------------------

    def read_u8(self, addr):
        b = self.read(addr, 1)
        return None if b is None else b[0]

    def read_u16(self, addr):
        b = self.read(addr, 2)
        return None if b is None else struct.unpack(">H", b)[0]

    def read_u32(self, addr):
        b = self.read(addr, 4)
        return None if b is None else struct.unpack(">I", b)[0]

    def read_i32(self, addr):
        b = self.read(addr, 4)
        return None if b is None else struct.unpack(">i", b)[0]

    def read_u64(self, addr):
        b = self.read(addr, 8)
        return None if b is None else struct.unpack(">Q", b)[0]

    def read_f32(self, addr):
        b = self.read(addr, 4)
        return None if b is None else struct.unpack(">f", b)[0]

    def read_f64(self, addr):
        b = self.read(addr, 8)
        return None if b is None else struct.unpack(">d", b)[0]

    def read_string(self, addr: int, size: int = 64, encoding="utf-8") -> str | None:
        raw = self.read(addr, size)
        if raw is None:
            return None
        return raw.split(b"\x00", 1)[0].decode(encoding, "replace")

    def write_u8(self, addr, value) -> bool:
        return self.write(addr, bytes([value & 0xFF]))

    def write_u16(self, addr, value) -> bool:
        return self.write(addr, struct.pack(">H", value & 0xFFFF))

    def write_u32(self, addr, value) -> bool:
        return self.write(addr, struct.pack(">I", value & 0xFFFFFFFF))

    def write_i32(self, addr, value) -> bool:
        return self.write(addr, struct.pack(">i", value))

    def write_f32(self, addr, value) -> bool:
        return self.write(addr, struct.pack(">f", value))

    def resolve(self, base: int, offsets: list[int]) -> int | None:
        """Walk a pointer chain. Same shape as the EE client's resolve."""
        addr = base
        for off in offsets[:-1]:
            ptr = self.read_u32(addr)
            if not ptr:
                return None
            addr = ptr + off
        return addr + offsets[-1] if offsets else addr

    def looks_like_eboot(self) -> bool:
        """Read the ELF magic. The cheapest proof the PID is really the game
        and that the transport is not corrupting bytes."""
        head = self.read(EBOOT_BASE, 8)
        return bool(head and head.startswith(ELF_MAGIC))

    def alive(self) -> bool:
        try:
            return self.pid in self.api.pids()
        except PS3Error:
            return False


class Block:
    """A span read in one round trip, sliced locally and big-endian."""

    __slots__ = ("base", "data")

    def __init__(self, base: int, data: bytes):
        self.base = base
        self.data = data

    def _at(self, addr: int, size: int) -> bytes:
        off = addr - self.base
        if off < 0 or off + size > len(self.data):
            raise IndexError(
                f"{addr:08X} outside block {self.base:08X}+{len(self.data)}"
            )
        return self.data[off:off + size]

    def u8(self, addr):
        return self._at(addr, 1)[0]

    def u16(self, addr):
        return struct.unpack(">H", self._at(addr, 2))[0]

    def u32(self, addr):
        return struct.unpack(">I", self._at(addr, 4))[0]

    def i32(self, addr):
        return struct.unpack(">i", self._at(addr, 4))[0]

    def f32(self, addr):
        return struct.unpack(">f", self._at(addr, 4))[0]

    def __contains__(self, addr: int) -> bool:
        return self.base <= addr < self.base + len(self.data)


# NPEB02034 is the development target: confirmed from the license file
# EP0177-NPEB02034_00-YAKUZADSPSNEU001.rap (EP0177 = SEGA Europe) and by
# running it. The disc IDs are conventional and want checking against a real
# dump before being relied on.
TARGET_ID = "NPEB02034"
GAME_IDS = {
    "NPEB02034": "Yakuza: Dead Souls (EU, PSN digital) - confirmed",
    "BLES01399": "Yakuza: Dead Souls (EU, disc)",
    "BLUS30931": "Yakuza: Dead Souls (US, disc)",
    "BLJM60378": "Ryu ga Gotoku: Of the End (JP, disc)",
}

# Measured live from the running EBOOT's program headers (tools/elfmap.py).
# ELF64 big-endian, PPC64, entry 0x01353C20 (an OPD in the data segment).
CODE_BASE, CODE_END = 0x00010000, 0x01310768   # RX, 19.0 MB
DATA_BASE, DATA_END = 0x01320000, 0x0172C408   # RW, 4.0 MB


def attach(host: str, *, pid: int | None = None, prefer: str = "auto"
           ) -> tuple[PS3MAPI, ProcessHandle] | None:
    """Connect and pick the game process."""
    api = PS3MAPI(host, prefer=prefer)
    api.connect()
    try:
        if pid is None:
            pid = api.game_pid()
        if pid is None:
            api.close()
            return None
        name = dict(api.processes()).get(pid, "")
    except PS3Error:
        api.close()
        raise
    return api, ProcessHandle(api, pid, name)
