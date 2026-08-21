"""Acceptance test for the PS3MAPI link. Run this before writing anything else.

    py tools/probe.py 192.168.1.129

Talks to webMAN MOD's PS3MAPI bridge on port 80 - no console configuration
needed. Checks the link, lists processes, proves memory reads work, and
benchmarks the round trip, which is the number the client design hangs on.

Options:
    --pid HEX       use this process instead of auto-picking
    --notify        fire a toast on both PS3MAPI and CCAPI
    --addr HEX      hexdump 64 bytes at this address
    --scan          probe common ports to see which servers are up
"""

from __future__ import annotations

import argparse
import socket
import statistics
import sys
import time

from ccapi import CCAPI, CCAPIError
from ps3mapi import EBOOT_BASE, ELF_MAGIC, PS3MAPI, PS3Error, attach

PORTS = {
    21: "FTP (webMAN)",
    80: "webMAN MOD web server + PS3MAPI bridge",
    6333: "CCAPI",
    7887: "PS3MAPI TCP server (optional, off by default)",
}


def hexdump(base: int, data: bytes) -> str:
    lines = []
    for i in range(0, len(data), 16):
        row = data[i:i + 16]
        hexs = " ".join(f"{b:02X}" for b in row).ljust(47)
        text = "".join(chr(b) if 32 <= b < 127 else "." for b in row)
        lines.append(f"  {base + i:08X}  {hexs}  {text}")
    return "\n".join(lines)


def scan(host: str) -> None:
    print("port scan:")
    for port, label in sorted(PORTS.items()):
        s = socket.socket()
        s.settimeout(1.5)
        try:
            s.connect((host, port))
            state = "OPEN"
        except socket.timeout:
            state = "filtered"
        except ConnectionRefusedError:
            state = "closed"
        except OSError as exc:
            state = exc.__class__.__name__
        finally:
            s.close()
        mark = "  <<<" if state == "OPEN" else ""
        print(f"  {port:>5}  {state:<10} {label}{mark}")
    print()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("host", help="console IP address")
    ap.add_argument("--prefer", choices=("auto", "tcp", "http"), default="auto",
                    help="memory transport (default: tcp if 7887 is up)")
    ap.add_argument("--pid", default="", help="hex PID to use")
    ap.add_argument("--notify", action="store_true")
    ap.add_argument("--addr", default="", help="hex address to dump")
    ap.add_argument("--scan", action="store_true")
    args = ap.parse_args()

    if args.scan:
        scan(args.host)

    print(f"connecting to {args.host} ...")
    try:
        api = PS3MAPI(args.host, prefer=args.prefer)
        api.connect()
    except (PS3Error, OSError) as exc:
        print(f"FAIL: {exc}")
        print("\nCheck: console powered on, webMAN MOD running, same LAN.")
        print("Run again with --scan to see which servers answer.")
        return 1

    print(f"connected. PS3MAPI server {api.server_version()}, "
          f"firmware {api.firmware_pretty()}")
    print(f"memory transport: {api.transport_name}"
          + ("  (binary, uncapped)" if api.transport_name == "tcp"
             else "  (HTTP GUI: 256 B/request, read-only)"))

    print("\nprocesses:")
    try:
        procs = api.processes()
    except PS3Error as exc:
        print(f"FAIL: cannot list processes: {exc}")
        return 1
    game = api.game_pid()
    for pid, name in procs:
        tag = "  <- game" if pid == game else ""
        print(f"  0x{pid:08X}  {name or '(no name)'}{tag}")

    api.close()

    want = int(args.pid, 16) if args.pid else None
    found = attach(args.host, pid=want, prefer=args.prefer)
    if found is None:
        print("\nNo game process - only the XMB is running.")
        print("Start Yakuza: Dead Souls and run this again. Everything above")
        print("passed, so the link itself is fine.")
        return 0
    api, proc = found
    print(f"\nattached: 0x{proc.pid:08X}  {proc.name or '(no name)'}")

    # 1. Does memory read, and is it the game?
    head = proc.read(EBOOT_BASE, 16)
    if head is None:
        print(f"FAIL: cannot read {EBOOT_BASE:08X}")
        return 1
    print(f"\n{EBOOT_BASE:08X}: {head.hex(' ').upper()}")
    if head.startswith(ELF_MAGIC):
        klass = {1: "ELF32", 2: "ELF64"}.get(head[4], f"class {head[4]}")
        endian = {1: "little-endian", 2: "big-endian"}.get(head[5], f"data {head[5]}")
        print(f"  ELF header present - {klass}, {endian}")
        print("  PASS: reads work and this is the game's own image.")
    elif not any(head):
        print("  All zeros. Careful: a wrong PID also reads as zeros, so this")
        print("  proves nothing either way. Confirm the PID is really the game.")
    elif all(b <= 0x0F for b in head):
        print("  Every byte is <= 0x0F. That is the webMAN JSON-bridge nibble")
        print("  bug - the high nibble of each byte has been zeroed. The data")
        print("  is CORRUPT. Something is routing reads through /ps3mapi.ps3")
        print("  instead of /getmem.ps3mapi. See the ps3mapi.py docstring.")
        return 1
    else:
        print("  No ELF magic, but not zeros either - the read is real, the")
        print("  image just maps elsewhere. Confirm against RPCS3.")

    # 2. Round trip cost, which decides the polling design.
    cap = api.transport.max_read if api.transport else 256
    print(f"\ntiming 12 reads of 4 bytes ...")
    small = [_time(proc, EBOOT_BASE, 4) for _ in range(12)]
    print(f"timing 12 reads of {cap} bytes (transport maximum) ...")
    big = [_time(proc, EBOOT_BASE, cap) for _ in range(12)]
    if None in small or None in big:
        print("  a read failed mid-benchmark")
        return 1

    def report(label, samples):
        print(f"  {label}: median {statistics.median(samples):6.1f} ms   "
              f"min {min(samples):6.1f}   max {max(samples):6.1f}")

    report("4 B     ", small)
    report(f"{cap} B", big)

    per_op = statistics.median(small)
    thruput = cap / (statistics.median(big) / 1000) / 1024
    print(f"\n  ~{1000 / per_op:.0f} reads/sec, ~{thruput:.0f} KB/sec at the cap.")
    print("  The round trip dominates, so read one span per tick and slice it")
    print("  locally rather than issuing many small reads.")
    if api.transport_name == "http":
        print("\n  This is the fallback transport. Enabling PS3MAPI on 7887 in")
        print("  webMAN's setup (then rebooting) switches to the binary path,")
        print("  which is uncapped and can also write.")

    # 3. Optional extras.
    if args.addr:
        addr = int(args.addr, 16)
        raw = proc.read(addr, 64)
        if raw is None:
            print(f"\ncannot read {addr:08X}")
        else:
            print(f"\ndump at {addr:08X}:")
            print(hexdump(addr, raw))

    if args.notify:
        try:
            api.notify("Archipelago link OK")
            print("\nPS3MAPI notify sent.")
        except PS3Error as exc:
            print(f"\nPS3MAPI notify failed: {exc}")
        try:
            CCAPI(args.host).notify("Archipelago link OK", icon="trophy1")
            print("CCAPI trophy notify sent - check the console screen.")
        except CCAPIError as exc:
            print(f"CCAPI notify failed (not a problem): {exc}")

    api.close()
    print("\ndone.")
    return 0


def _time(proc, addr, size) -> float | None:
    t0 = time.perf_counter()
    if proc.read(addr, size) is None:
        return None
    return (time.perf_counter() - t0) * 1000


if __name__ == "__main__":
    sys.exit(main())
