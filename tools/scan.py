"""Value scanner for the running game - the Cheat Engine step, over the network.

    py tools/scan.py 192.168.1.129 --new --eq 5000      # money is 5000
    ... spend some money in game ...
    py tools/scan.py 192.168.1.129 --eq 4200            # now it is 4200
    py tools/scan.py 192.168.1.129 --list

Only viable because the TCP transport moves ~1 MB/sec: the game's whole 4 MB
data segment sweeps in about four seconds, so every pass re-reads everything
and filters locally. On the 256-byte HTTP transport the same sweep took over
seven minutes, which is why this tool did not exist before.

Unknown-value searches work too, for things with no visible number:

    py tools/scan.py 192.168.1.129 --new --unknown
    ... take damage ...
    py tools/scan.py 192.168.1.129 --decreased
    ... heal ...
    py tools/scan.py 192.168.1.129 --increased

State lives in output/ so passes chain across invocations: scan.bin is the
previous sweep, scan_addrs.bin the surviving candidates as packed big-endian
u32, scan.json just the width and count. --reset clears all three.

Widths: --u32 (default), --u16, --u8, --f32. Values are read big-endian.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import struct
import sys
import time

from ps3mapi import DATA_BASE, DATA_END, PS3Error, attach

OUT = pathlib.Path(__file__).resolve().parent.parent / "output"
SNAPSHOT = OUT / "scan.bin"
STATE = OUT / "scan.json"
ADDRS = OUT / "scan_addrs.bin"

FORMATS = {"u8": (">B", 1), "u16": (">H", 2), "u32": (">I", 4), "f32": (">f", 4)}


def sweep(proc, base: int, end: int, chunk: int = 65536) -> bytes:
    """Read a whole region. One 64 KB request per chunk."""
    out = bytearray()
    total = end - base
    t0 = time.perf_counter()
    cursor = base
    step = 0
    while cursor < end:
        want = min(chunk, end - cursor)
        data = proc.read(cursor, want)
        if data is None:
            raise PS3Error(f"read failed at {cursor:08X}")
        out += data
        cursor += want
        done = int(len(out) / total * 10)
        if done > step:
            step = done
            sys.stdout.write(f"\r  sweeping {done * 10:3d}%")
            sys.stdout.flush()
    dt = time.perf_counter() - t0
    print(f"\r  swept {len(out) / 1024 / 1024:.1f} MB in {dt:.1f}s"
          f"  ({len(out) / 1024 / dt:.0f} KB/s)")
    return bytes(out)


def values(data: bytes, base: int, kind: str, addrs=None):
    """Yield (addr, value) for a width, either everywhere or at given addrs."""
    fmt, size = FORMATS[kind]
    if addrs is None:
        # Aligned scan: game state is essentially always aligned, and it cuts
        # the candidate count by the width.
        for off in range(0, len(data) - size + 1, size):
            yield base + off, struct.unpack_from(fmt, data, off)[0]
    else:
        for addr in addrs:
            off = addr - base
            if 0 <= off <= len(data) - size:
                yield addr, struct.unpack_from(fmt, data, off)[0]


def snap_path(name: str) -> pathlib.Path:
    return OUT / f"snap_{name}.bin"


def diff_widths(before: bytes, after: bytes, base: int, mode: str,
                limit: int = 40) -> dict[str, list[tuple[int, object, object]]]:
    """Compare two snapshots at every width at once.

    For an analog value - a health bar with no number on screen - the width is
    unknown, and guessing wrong wastes a whole capture session. A sweep grabs
    all 4 MB regardless, so both snapshots can be reinterpreted as u8, u16, u32
    and f32 offline and the width that converges wins.
    """
    results: dict[str, list[tuple[int, object, object]]] = {}
    for kind, (fmt, size) in FORMATS.items():
        hits = []
        for off in range(0, min(len(before), len(after)) - size + 1, size):
            b = struct.unpack_from(fmt, before, off)[0]
            a = struct.unpack_from(fmt, after, off)[0]
            if kind == "f32":
                # Reject NaN/inf and absurd magnitudes; most of a data segment
                # reinterpreted as float is noise.
                if not (-1e9 < b < 1e9) or not (-1e9 < a < 1e9):
                    continue
                if b != b or a != a:
                    continue
            if mode == "decreased" and a < b:
                hits.append((base + off, b, a))
            elif mode == "increased" and a > b:
                hits.append((base + off, b, a))
            elif mode == "changed" and a != b:
                hits.append((base + off, b, a))
            elif mode == "unchanged" and a == b:
                hits.append((base + off, b, a))
            if len(hits) > 500000:
                break
        results[kind] = hits
    return results


def load_state() -> dict:
    """Candidate addresses live in a packed binary file, not in the JSON.

    An unknown-value first pass keeps ~1M addresses. As JSON that was a 10 MB
    file to write and reparse every pass; packed big-endian u32 it is 4 MB and
    loads instantly.
    """
    if not STATE.exists():
        return {}
    state = json.loads(STATE.read_text())
    if ADDRS.exists():
        raw = ADDRS.read_bytes()
        state["addrs"] = list(struct.unpack(f">{len(raw) // 4}I", raw))
    else:
        state["addrs"] = []
    return state


def save_state(kind: str, addrs: list[int]) -> None:
    OUT.mkdir(exist_ok=True)
    STATE.write_text(json.dumps({"kind": kind, "count": len(addrs)}))
    ADDRS.write_bytes(struct.pack(f">{len(addrs)}I", *addrs))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("host")
    ap.add_argument("--pid", default="")
    ap.add_argument("--new", action="store_true", help="start a fresh search")
    ap.add_argument("--unknown", action="store_true",
                    help="with --new: keep every address, for unknown-value searches")
    ap.add_argument("--eq", type=float, help="value equals")
    ap.add_argument("--changed", action="store_true")
    ap.add_argument("--unchanged", action="store_true")
    ap.add_argument("--increased", action="store_true")
    ap.add_argument("--decreased", action="store_true")
    ap.add_argument("--list", action="store_true", help="show candidates and exit")
    ap.add_argument("--reset", action="store_true")
    ap.add_argument("--snap", metavar="NAME",
                    help="sweep and save a named snapshot, for --diff")
    ap.add_argument("--diff", nargs=2, metavar=("BEFORE", "AFTER"),
                    help="compare two snapshots across ALL widths at once")
    ap.add_argument("--near", metavar="HEX",
                    help="with --diff: only report hits within 0x2000 of this address")
    for k in FORMATS:
        ap.add_argument(f"--{k}", dest="kind", action="store_const", const=k)
    ap.add_argument("--base", default=f"{DATA_BASE:X}")
    ap.add_argument("--end", default=f"{DATA_END:X}")
    ap.set_defaults(kind="u32")
    args = ap.parse_args()

    if args.reset:
        for p in (SNAPSHOT, STATE, ADDRS):
            p.unlink(missing_ok=True)
        print("scan state cleared.")
        return 0

    if args.diff:
        base = int(args.base, 16)
        a_path, b_path = snap_path(args.diff[0]), snap_path(args.diff[1])
        for p in (a_path, b_path):
            if not p.exists():
                print(f"missing snapshot {p.name} - take it with --snap")
                return 1
        mode = ("decreased" if args.decreased else
                "increased" if args.increased else
                "unchanged" if args.unchanged else "changed")
        near = int(args.near, 16) if args.near else None
        print(f"diff {args.diff[0]} -> {args.diff[1]}, filter: {mode}")
        res = diff_widths(a_path.read_bytes(), b_path.read_bytes(), base, mode)
        for kind in ("u8", "u16", "u32", "f32"):
            hits = res[kind]
            if near is not None:
                hits = [h for h in hits if abs(h[0] - near) <= 0x2000]
            note = ""
            if 0 < len(hits) <= 40:
                note = "  <- narrow enough to inspect"
            print(f"\n  {kind:>4}: {len(hits)} hits{note}")
            for addr, b, a in hits[:12]:
                print(f"      0x{addr:08X}  {b} -> {a}")
            if len(hits) > 12:
                print(f"      ... and {len(hits) - 12} more")
        print("\nTake another snapshot after a further change and diff again;")
        print("the width whose hit count collapses is the real one.")
        return 0

    state = load_state()
    if args.list:
        addrs = state.get("addrs", [])
        print(f"{len(addrs)} candidates ({state.get('kind', '?')})")
        for a in addrs[:60]:
            print(f"  0x{a:08X}")
        if len(addrs) > 60:
            print(f"  ... and {len(addrs) - 60} more")
        return 0

    base, end = int(args.base, 16), int(args.end, 16)
    kind = args.kind if args.new else state.get("kind", args.kind)

    found = attach(args.host, pid=int(args.pid, 16) if args.pid else None,
                   prefer="tcp")
    if found is None:
        print("No game process. Start the game first.")
        return 1
    api, proc = found
    print(f"attached: 0x{proc.pid:08X}  {proc.name}")
    print(f"region 0x{base:08X}-0x{end:08X}  width {kind}")

    try:
        data = sweep(proc, base, end)
    except PS3Error as exc:
        print(f"FAIL: {exc}")
        return 1
    finally:
        api.close()

    if args.snap:
        OUT.mkdir(exist_ok=True)
        snap_path(args.snap).write_bytes(data)
        print(f"  saved snapshot '{args.snap}' ({len(data) / 1024 / 1024:.1f} MB)")
        return 0

    prev = SNAPSHOT.read_bytes() if SNAPSHOT.exists() else None

    if args.new:
        if args.unknown:
            addrs = [a for a, _v in values(data, base, kind)]
            print(f"  {len(addrs)} addresses recorded; now change the value "
                  f"in game and run --increased/--decreased/--changed")
        elif args.eq is None:
            print("--new needs either --eq VALUE or --unknown")
            return 1
        else:
            target = args.eq if kind == "f32" else int(args.eq)
            addrs = [a for a, v in values(data, base, kind) if v == target]
            print(f"  {len(addrs)} addresses hold {target}")
    else:
        old = state.get("addrs")
        if not old:
            print("No search in progress. Start one with --new.")
            return 1
        if prev is None:
            print("No previous snapshot to compare against. Use --new.")
            return 1
        before = dict(values(prev, base, kind, old))
        now = dict(values(data, base, kind, old))
        if args.eq is not None:
            target = args.eq if kind == "f32" else int(args.eq)
            addrs = [a for a in old if now.get(a) == target]
            print(f"  {len(addrs)} of {len(old)} still match {target}")
        elif args.changed:
            addrs = [a for a in old if a in now and now[a] != before.get(a)]
            print(f"  {len(addrs)} of {len(old)} changed")
        elif args.unchanged:
            addrs = [a for a in old if a in now and now[a] == before.get(a)]
            print(f"  {len(addrs)} of {len(old)} unchanged")
        elif args.increased:
            addrs = [a for a in old if a in now and before.get(a) is not None
                     and now[a] > before[a]]
            print(f"  {len(addrs)} of {len(old)} increased")
        elif args.decreased:
            addrs = [a for a in old if a in now and before.get(a) is not None
                     and now[a] < before[a]]
            print(f"  {len(addrs)} of {len(old)} decreased")
        else:
            print("Pick a filter: --eq, --changed, --unchanged, "
                  "--increased or --decreased")
            return 1

    OUT.mkdir(exist_ok=True)
    SNAPSHOT.write_bytes(data)
    save_state(kind, addrs)

    shown = addrs[:20]
    if shown:
        current = dict(values(data, base, kind, shown))
        print()
        for a in shown:
            print(f"  0x{a:08X} = {current.get(a)}")
        if len(addrs) > len(shown):
            print(f"  ... and {len(addrs) - len(shown)} more")
    if 0 < len(addrs) <= 8:
        print("\n  Few enough to test directly - poke one and see what moves.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
