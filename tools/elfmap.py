"""Map a running game's memory layout by reading its ELF headers.

    py tools/elfmap.py 192.168.1.129

The EBOOT is mapped with its ELF header at 0x00010000, program headers and
all, so the segment layout can be read straight out of the live process. No
dump, no decryption, no Ghidra - just the link.

That gives the two numbers everything else needs: where executable code lives
(the search space for function addresses) and where writable data lives (the
search space for game state, which is what a value scanner should be pointed
at).

Verified working against a retail PS3 game over the HTTP transport.
"""

from __future__ import annotations

import argparse
import struct
import sys

from ps3mapi import EBOOT_BASE, ELF_MAGIC, PS3Error, attach

PT_TYPES = {
    0: "NULL", 1: "LOAD", 2: "DYNAMIC", 3: "INTERP", 4: "NOTE",
    5: "SHLIB", 6: "PHDR", 7: "TLS",
    0x60000001: "SCE_1", 0x60000002: "SCE_2",
}


def decode_flags(value: int) -> str:
    return "".join(c for c, bit in zip("RWX", (4, 2, 1)) if value & bit)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("host", help="console IP address")
    ap.add_argument("--pid", default="", help="hex PID (default: auto-detect)")
    ap.add_argument("--prefer", choices=("auto", "tcp", "http"), default="auto")
    args = ap.parse_args()

    found = attach(args.host, pid=int(args.pid, 16) if args.pid else None,
                   prefer=args.prefer)
    if found is None:
        print("No game process. Start the game first.")
        return 1
    api, proc = found
    print(f"attached: 0x{proc.pid:08X}  {proc.name or '(no name)'}")

    head = proc.read(EBOOT_BASE, 64)
    if head is None:
        print(f"FAIL: cannot read {EBOOT_BASE:08X}")
        return 1
    if not head.startswith(ELF_MAGIC):
        print(f"FAIL: no ELF magic at {EBOOT_BASE:08X} - got {head[:8].hex(' ').upper()}")
        if all(b <= 0x0F for b in head):
            print("Every byte <= 0x0F: that is the JSON-bridge nibble bug.")
        return 1

    e_type, e_machine = struct.unpack_from(">HH", head, 16)
    e_entry, e_phoff, _e_shoff = struct.unpack_from(">QQQ", head, 24)
    e_phentsize, e_phnum = struct.unpack_from(">HH", head, 54)

    print(f"\nELF64 big-endian, type {e_type}, machine {e_machine} "
          f"({'PPC64' if e_machine == 21 else '?'})")
    print(f"entry 0x{e_entry:08X}   {e_phnum} program headers at 0x{e_phoff:X}")
    print("\nNote: on PPC64 ELFv1 the entry points at a function descriptor in")
    print("the data segment, not at code. That is normal, not a misread.\n")

    raw = proc.read(EBOOT_BASE + e_phoff, e_phnum * e_phentsize)
    if raw is None:
        print("FAIL: cannot read program headers")
        return 1

    print(f"{'#':>2}  {'type':<8} {'flags':>5}  {'vaddr':>10} {'end':>10} "
          f"{'memsz':>10} {'filesz':>10}")
    code = data = None
    for i in range(e_phnum):
        off = i * e_phentsize
        p_type, p_flags = struct.unpack_from(">II", raw, off)
        _o, vaddr, _pa, filesz, memsz, _al = struct.unpack_from(">QQQQQQ", raw, off + 8)
        if memsz == 0:
            continue
        flags = decode_flags(p_flags)
        print(f"{i:>2}  {PT_TYPES.get(p_type, hex(p_type)):<8} {flags:>5}  "
              f"0x{vaddr:08X} 0x{vaddr + memsz:08X} {memsz:>10} {filesz:>10}")
        if p_type == 1 and "X" in flags and code is None:
            code = (vaddr, memsz)
        elif p_type == 1 and flags == "RW" and data is None:
            data = (vaddr, memsz)

    print()
    if code:
        print(f"  code (RX)  0x{code[0]:08X} - 0x{code[0] + code[1]:08X}"
              f"   {code[1] / 1024 / 1024:.1f} MB")
        print("             function addresses live here; load this range in Ghidra")
    if data:
        print(f"  data (RW)  0x{data[0]:08X} - 0x{data[0] + data[1]:08X}"
              f"   {data[1] / 1024 / 1024:.1f} MB")
        print("             game state lives here; point the value scanner at it")

    api.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
