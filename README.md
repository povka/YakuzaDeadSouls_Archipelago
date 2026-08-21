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
- **.NET 9 SDK** on the PC.
- Your console's LAN address, configured once — see below.
- Console and PC on the same LAN.

Developed against a Slim CECH-25xx running **Evilnat 4.93 (Cobra 8.5)**. CCAPI
is optional; if present it is used for nicer notifications and nothing else.

---

## What works today

Everything a multiworld client needs, verified against a real console running
the real game: read, write, on-screen messaging, and granting an item.

Tell the tools where your console is, once. Any of these work, in order of
precedence:

```bash
echo 192.168.1.50 > console.txt
```

or set `YDS_PS3_HOST`, or pass the address as an argument. `console.txt` is
git-ignored, so it stays a local setting.

```bash
dotnet run --project client/Probe
```

Lists processes, attaches, proves the bytes are not corrupted by reading the
ELF header, prints money/HP/EXP/inventory, dumps the segment layout from the
live ELF program headers, and benchmarks the link (~1.3 MB/s).

```bash
dotnet run --project client/Scanner -- snap before
# change something in game
dotnet run --project client/Scanner -- snap after
dotnet run --project client/Scanner -- delta before after 50 --all
```

A full 4 MB sweep of the data segment takes about three seconds. Only `snap`
talks to the console; every other command works offline on saved snapshots,
which is what lets one capture be reinterpreted at any width instead of
committing to a guess up front.

| Command | What it does |
|---|---|
| `snap <name>` | sweep and save |
| `eq <snap> <value>` | addresses holding a value |
| `delta <a> <b> <n>` | values that changed by exactly `n` |
| `filter <a> <b> <mode>` | changed / unchanged / increased / decreased |
| `slots <a> <b> <c>` | slot-array fill pattern across three snapshots |

The last two are the ones that actually cracked this game. `delta` found EXP
after a direct value search returned **zero hits at every width** — the game
stores experience counting up while the UI shows a countdown. `slots` found the
inventory, whose signature is two *different* addresses receiving the *same*
item id at different times, because items here do not stack.

---

## Languages

**C# / .NET 9 everywhere except the apworld.**

The apworld has to be Python — Archipelago's world API *is* Python, and
`.apworld` is a zipped Python package loaded inside the generator's process.
That part is small and never touches the PS3: item and location tables, logic
rules, options.

Everything else is C#, which is not just preference. The PS3 tooling ecosystem
is overwhelmingly C# — PS3Lib, NetCheatPS3, and Dnawrkshp's PS3MAPI-NCAPI,
whose protocol this transport was written from — and Archipelago ships an
official [.NET client library](https://www.nuget.org/packages/Archipelago.MultiClient.Net)
with no dependencies.

## Layout

| Path | What it is |
|---|---|
| `client/Ps3Mapi/` | Transport, typed big-endian access, addresses, inventory, notifications |
| `client/Probe/` | Acceptance test for the link |
| `client/Scanner/` | Value scanner over the data segment |
| `assets/archipelago.png` | The Archipelago logo, 512x512, for stage-2 in-game UI |
| `notes/REVERSE.md` | Findings, decisions, dead ends |

---

## Three things that will silently give wrong answers

- **Never read memory through `/ps3mapi.ps3?MEMORY GET`.** webMAN 1.47.48's
  JSON bridge zeroes the high nibble of every byte. The output looks plausible
  and is wrong. The tell is that every byte comes back `<= 0x0F`. This repo
  routes reads through the binary TCP path instead, and `GameProcess.LooksLikeGame`
  catches it by checking for the ELF header.
- **The PPU is big-endian.** Every read goes through `BinaryPrimitives.Read*BigEndian`.
  A value that reads as `0x01000000` is 1.
- **Zeros prove nothing.** A wrong PID reads as zeros rather than erroring, and
  so does unmapped memory. The PID changes every time the game launches, so
  never cache it — resolve it with `Ps3Console.FindGameAsync` each run.

---

## Credits

- **Ryu Ga Gotoku Studio** and **SEGA** for the game.
- [**Archipelago**](https://github.com/ArchipelagoMW/Archipelago) for the
  multiworld framework.
- [**webMAN MOD**](https://github.com/aldostools/webMAN-MOD) by aldostools, and
  the PS3MAPI authors, for the memory API this is built on.

## AI disclosure

Claude was used as an assistance tool during development.
