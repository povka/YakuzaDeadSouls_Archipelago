# Yakuza: Dead Souls Archipelago

An [Archipelago](https://archipelago.gg) world for **Yakuza: Dead Souls**, played
on **real PlayStation 3 hardware**.

> **Status: nothing playable yet.** No world, no client, no check list. What
> does work: verified read *and write* access to the running game, its live
> memory map, and a value scanner. See [`notes/REVERSE.md`](notes/REVERSE.md).

---

## Why hardware, and why that is the hard part

Dead Souls never left the PS3. No PC port, no remaster, nothing announced. So
unlike [Empire Earth](https://github.com/povka/EmpireEarth_Archipelago), where
the client attaches to a local Windows process and reads it directly, everything
here happens over the network to a jailbroken console.

The primitives all exist. **PS3MAPI**, which webMAN MOD serves on port 7887,
does process memory read and write, and loads SPRX modules into a running game.
That covers everything the Empire Earth client needs `ReadProcessMemory`,
`WriteProcessMemory` and `CreateRemoteThread` for.

What does *not* exist on retail hardware is a breakpoint debugger. The plan
around that: **reverse engineer in RPCS3, play on the console.** The PS3 has no
ASLR, so static addresses found in the emulator are valid on metal. The emulator
is a workbench; the deliverable runs on the real machine.

---

## Requirements

- A **jailbroken PS3** — CFW or PS3HEN — with **webMAN MOD** installed and the
  **PS3MAPI server enabled**. That switch is on the **Setup** page, not the
  PS3MAPI page: in the *VSH MENU* section, on the `DEL CFW SYSCALLS` line,
  there is a `PS3MAPI [Enabled/Disabled]` dropdown, defaulting to Disabled.
  Set it and **reboot** — the server only binds port 7887 at boot. Without it
  the tools fall back to a read-only HTTP path that cannot write.
- **Yakuza: Dead Souls.** Developed against `NPEB02034`, the EU PSN digital
  release. The disc releases are unverified — addresses will differ.
- **Python 3.11+** on the PC. 64-bit is fine — nothing here loads `CCAPI.dll`,
  deliberately. See [`tools/ccapi.py`](tools/ccapi.py) for why.
- Console and PC on the same LAN.

Developed against a Slim CECH-25xx running **Evilnat 4.93 (Cobra 8.5)**. CCAPI
is optional; if present it is used for nicer notifications and nothing else.

---

## What works today

The link, verified end to end against a real console running the real game.

```bash
py tools/probe.py 192.168.1.129 --notify
```

Lists processes, attaches to the game, reads its ELF header to prove the bytes
are not being corrupted, benchmarks the round trip, and fires an on-screen
toast. `--addr <hex>` dumps 64 bytes anywhere — the tool for the address parity
check against RPCS3. `--scan` shows which console services are up.

```bash
py tools/elfmap.py 192.168.1.129
```

Reads the running game's own ELF program headers to map its segments. On the
confirmed target (`NPEB02034`, EU PSN digital) that gives:

| Segment | Range | Size | What it is for |
|---|---|---|---|
| code (RX) | `0x00010000` – `0x01310768` | 19.0 MB | function addresses; load this in Ghidra |
| data (RW) | `0x01320000` – `0x0172C408` | 4.0 MB | game state; point the value scanner here |

ELF64 big-endian, PPC64, entry `0x01353C20`.

```bash
py tools/scan.py 192.168.1.129 --new --eq 5000
```

Value scanner — the Cheat Engine step, over the network. Sweeps the whole 4 MB
data segment in under four seconds, so every pass re-reads everything and
filters locally. Chain passes to narrow down:

```bash
py tools/scan.py 192.168.1.129 --new --eq 5000   # money is 5000
# spend some money in game
py tools/scan.py 192.168.1.129 --eq 4200         # now it is 4200
py tools/scan.py 192.168.1.129 --list
```

Unknown-value searches work too — `--new --unknown`, then `--increased`,
`--decreased`, `--changed` or `--unchanged` as the value moves. Widths `--u8`,
`--u16`, `--u32` (default) and `--f32`, all read big-endian.

---

## Layout

| Path | What it is |
|---|---|
| `tools/ps3mapi.py` | PS3MAPI client — the PS3 analogue of EE's `Memory.py` |
| `tools/ccapi.py` | CCAPI's HTTP surface: notifications. Not memory, and the file explains why |
| `tools/probe.py` | Acceptance test for the link, and a memory dumper |
| `tools/elfmap.py` | Reads the running game's segment layout from its ELF headers |
| `tools/scan.py` | Value scanner over the data segment — finds addresses for game state |
| `assets/archipelago.png` | The Archipelago logo, 512x512, for stage-2 in-game UI |
| `notes/REVERSE.md` | Findings, decisions, dead ends |

---

## Three things that will silently give wrong answers

- **Never read memory through `/ps3mapi.ps3?MEMORY GET`.** webMAN 1.47.48's
  JSON bridge zeroes the high nibble of every byte. The output looks plausible
  and is wrong. The tell is that every byte comes back `<= 0x0F`. This repo
  routes reads through `/getmem.ps3mapi` instead; `probe.py` checks for the bug
  explicitly.
- **The PPU is big-endian.** Every struct format in this repo is `>`. A value
  that reads as `0x01000000` is 1.
- **Zeros prove nothing.** A wrong PID reads as zeros rather than erroring, and
  so does unmapped memory. Validate the PID against the process list first.

---

## Credits

- **Ryu Ga Gotoku Studio** and **SEGA** for the game.
- [**Archipelago**](https://github.com/ArchipelagoMW/Archipelago) for the
  multiworld framework.
- [**webMAN MOD**](https://github.com/aldostools/webMAN-MOD) by aldostools, and
  the PS3MAPI authors, for the memory API this is built on.
- [**RPCS3**](https://rpcs3.net/) — not needed to play, but the reason the
  reverse engineering is possible at all.

## AI disclosure

Claude was used as an assistance tool during development.
