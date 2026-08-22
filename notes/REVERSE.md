# Yakuza: Dead Souls — Archipelago reverse engineering notes

Same purpose as the Empire Earth project's `notes/REVERSE.md`: everything
learned, including the dead ends and *why* they were dead, so nothing gets
retried. Started 2026-08-20.

---

## Target

**Yakuza: Dead Souls** (*Ryū ga Gotoku: Of the End*), PS3 exclusive, 2011 JP /
2012 west. No PC port, no remaster, none announced. There is no second target
to fall back to.

**Development target: `NPEB02034` — the EU PSN digital release.** Confirmed two
ways: the license file `EP0177-NPEB02034_00-YAKUZADSPSNEU001.rap` (`EP0177` is
SEGA Europe, `YAKUZADSPSNEU001` the title), and by running it. Installed from
`D:\PS3\packages\YAKUZA_DEAD_SOULS.pkg` (21.5 GB).

Beware searching for this ID online — at least one forum database attributes
`NPEB02034` to *Deadfall Adventures*, which is wrong. The `.rap` filename is
authoritative.

The disc IDs `BLES01399` (EU), `BLUS30931` (US) and `BLJM60378` (JP) are
conventional and **have not been checked against a real dump**. Nothing depends
on them; the digital release is what is being built against.

The game must run on **real hardware**. That is the whole constraint and it is
what shaped every decision below.

### Console — confirmed 2026-08-20

**Slim CECH-25xx, minver 3.40, Evilnat 4.93 CFW, CCAPI installed.**

This is the best case available and it removes every hardware caveat:

- **Real CFW, not HEN.** Full syscall 8/9 peek/poke, no HEN restrictions, and
  CCAPI is legitimately installed (it must *not* be on a HEN console — moot
  here).
- **Evilnat 4.93 ships Cobra 8.5.** SPRX plugin loading needs Cobra 8.3 or
  above, so the stage-2 architecture below is available today, not aspirational.
  `/dev_hdd0/plugins/` and `boot_plugins.txt` are the loading path.
- **minver 3.40** only ever mattered for downgrade eligibility. Irrelevant now
  that CFW is on.

Nothing in the plan is gated on hardware any more. Every remaining risk is
reverse engineering risk.

---

## Tooling

**C# / .NET 10 everywhere except the apworld.** The apworld has to be Python —
Archipelago's world API *is* Python and `.apworld` is a zipped Python package
loaded inside the generator's process. That part never touches the PS3: item and
location tables, logic rules, options. Everything else is C#, which also matches
the PS3 ecosystem (PS3Lib, NetCheatPS3, PS3MAPI-NCAPI are all C#), and
Archipelago ships an official .NET client library with no dependencies.

.NET 10 is LTS to Nov 2028. .NET 9 expires Nov 2026 and was only ever chosen
because it happened to be installed.

### Projects

| Path | What it is |
|---|---|
| `client/Ps3Mapi/` | library: transport, typed big-endian access, addresses, inventory, abilities, notifications |
| `client/Probe/` | acceptance test — attaches, verifies the ELF header, prints player state |
| `client/Rpcs3Probe/` | same against a running RPCS3 |
| `client/Scanner/` | value scanner: `snap`, `eq`, `delta`, `filter`, `slots`, `event`, `watch`, `list` |
| `client/ItemProbe/` | inventory and memory poking: `read`, `fill`, `unlock`, `restore`, `peek`, `poke`, `fillrange`, `find`, `strings`, `ids` |

### Host configuration

Resolved in precedence order: command-line argument, then the `YDS_PS3_HOST`
environment variable, then `console.txt` (searched next to the executable and up
to the repo root, git-ignored). `--rpcs3` targets a local emulator instead and
needs no address.

### The searches that actually worked

- `delta <a> <b> <n>` — found EXP after a direct value search returned **zero
  hits at every width**, because the game stores exp counting up while the UI
  shows a countdown.
- `slots <a> <b> <c>` — found the inventory. Items do not stack, so there is no
  count to watch; the signature is two *different* addresses taking the *same*
  value at different times.
- `event <idleA> <idleB> <after>` — subtracts ambient churn using two idle
  snapshots. Roughly 1400-1800 u32 addresses change on their own even sitting in
  a menu.
- `watch <file>` — polls a list and prints changes. Mapped all 39 abilities.

### Data files

| File | Contents |
|---|---|
| `data/items.tsv` | 1128 entries, ids 0-1127, dumped from the game's name table |
| `data/ability_bits.tsv` | address + bit + name for all 39 of Akiyama's abilities |
| `data/ability_names_alt.tsv` | a 69-entry ability string table that is **not** the menu list; kept because it is real, but it does not match bit order |

---

## The decision that makes this tractable

Reverse engineer in **RPCS3**, ship and play on **hardware**.

Retail PS3 has **no ASLR**. The EBOOT maps at the addresses in its own program
headers, and it does so identically in the emulator and on the console. So a
static address found with RPCS3's debugger — which has real breakpoints, a
memory viewer and PPU disassembly — is valid on metal.

This matters because retail hardware has **no breakpoint debugger**. Proper
debugging wants a DECR devkit; CCAPI's "RTE debugging" is not `x64dbg`. The
Empire Earth project leaned hard on hardware breakpoints (see its REVERSE.md,
"Hardware breakpoints: what was learned"). Without the emulator as a lab, that
entire technique is gone and the work becomes blind poking over a socket.

### VERIFIED 2026-08-21 — addresses are identical on both

The parity check passed. With the same save loaded in RPCS3, every address found
on hardware read correctly in the emulator, **at the same guest address, with no
translation**:

| | Hardware | RPCS3 |
|---|---|---|
| ELF header at `0x00010000` | `7F454C4602020166` | `7F454C4602020166` |
| money `0x01537E18` | 60000 | 60000 |
| HP `0x0154BDB4/B6` | 300 / 300 | 300 / 300 |
| exp `0x0154BDCC` | 0 | 0 |

So the no-ASLR argument holds in practice, and the plan is sound: **find it in
the emulator, ship it to hardware.**

### THE RULE: design for the console; RPCS3 comes free

RPCS3 is a legitimate **ship** target — plenty of people can emulate but cannot
get a jailbroken PS3 — but the console sets the constraints. Designing for the
harder target makes the emulator work for free; the reverse would need a
rewrite. So: nothing may depend on a capability the console lacks. RPCS3 is allowed for finding addresses, setting
breakpoints and reading code — its *output* is addresses and offsets, and those
were verified to transfer unchanged. The scanner is a dev tool and never ships.

The real hazard is not the scanner, it is letting emulator speed leak into the
**client's runtime design**. At ~2 GB/s, "sweep the data segment every tick and
diff it" is a perfectly good way to detect checks. On hardware that same sweep
is **4.0 seconds**. A design validated in the emulator would be dead on arrival
on the console.

So the client polls a small fixed set of known addresses. Measured budget:

| Approach | Cost per tick | Max rate |
|---|---|---|
| 5 addresses, one read each | 305 ms | 3.3 Hz |
| The same 5 as **2 span reads** | **122 ms** | **8.2 Hz** |
| Full 4 MB sweep | 4.0 s | unusable |

Two consequences to design around:

1. **Read spans, not addresses.** One 64 KB read costs the same as a 4-byte
   read, so covering many values in one request is 2.5x better than fetching
   them individually. `MemoryBlock` exists for exactly this: fetch a span, then
   slice fields out of it by absolute address.
2. **Clustering matters.** Everything confirmed so far lives inside a 92 KB
   window (`0x01534DE4`-`0x0154BDCC`), which two reads cover. Any future value
   far outside that window costs another whole round trip. If progression flags
   turn out to be scattered, the answer is **tiered polling** - inventory and
   health every tick, chapter flags once a second - not more reads per tick.

Anything not yet checked on hardware is unverified, however well it works in
the emulator.

### Where the two targets actually differ

Very little, which is what makes dual-target shipping cheap. Confirmed so far:

| | Console | RPCS3 |
|---|---|---|
| Guest addresses | identical | identical |
| Reads | 1.3 MB/s | ~2 GB/s |
| Writes to the RW data segment | yes | **yes** — so granting items works on both |
| Writes to the inter-segment padding `0x01310768` | yes | **refused** |

The padding is claimed by no program header, so RPCS3 evidently does not map it
writable where lv2 handed out full pages. It only matters because it was the
chosen scratchpad for write tests: on RPCS3, write to any address in the RW data
segment and restore it afterwards.

`0x0154BDC8` was previously recommended here as "provably inert". **It is not** —
it holds cumulative total EXP. Writing there does not move the level display,
which is all that was ever demonstrated, but that is not the same as being
unused.

### Reading RPCS3

RPCS3 maps the guest address space into its own process at a fixed base — on
64-bit Windows that is **`0x300000000`**, so a guest address is simply
`base + address`. `Rpcs3Target` (in the Ps3Mapi library) attaches with
`ReadProcessMemory` and confirms the base by checking for the EBOOT's ELF magic
at guest `0x00010000`, falling back to walking committed regions if the fixed
base ever moves.

Two consequences worth planning around:

- **Breakpoints are now available.** RPCS3 has a real debugger, so "what writes
  this address" is answerable — the single technique the Empire Earth project
  leaned on hardest and that retail hardware cannot provide.
- **Scanning the emulator is local memory, not a 1.3 MB/s network link.** A
  sweep that costs ~3 seconds against the console is essentially instant
  against RPCS3. For the search-heavy phase, the emulator is simply the better
  target, and anything found there transfers unchanged.

Caveat that still stands: heap pointers hold different absolute values run to
run. The *chain* (static base + offsets) carries over, not a resolved address.

---

## Transport: PS3MAPI — all of this is measured, 2026-08-20

Console state as found: ports **21** (FTP), **80** (webMAN web server) and
**6333** (CCAPI) open; **7887 closed** — the PS3MAPI TCP server is off by
default. webMAN MOD **1.47.48**, PS3MAPI server **0x125**, firmware **0x493**.

### The trap: `/ps3mapi.ps3?MEMORY GET` returns corrupt data

webMAN's JSON bridge **zeroes the high nibble of every byte** it returns from a
memory read. Verified against webMAN's own GUI viewer at the same address:

```
true (GUI):   7F 45 4C 46 02 02 01 66 ... 00 15 ... 01 35 3C 20
JSON bridge:  0F 05 0C 06 02 02 01 06 ... 00 05 ... 01 05 0C 00
```

Bytes whose high nibble is already 0 survive untouched, which is exactly what
makes this dangerous — the output looks structured and plausible. A whole
address map could have been built on it before anyone noticed. **The tell is
that every byte comes back `<= 0x0F`.** `probe.py` now checks for this
explicitly.

The bug is in the response encoder only. Non-memory commands over the bridge
(`SERVER GETVERSION`, `PS3 GETFWVERSION`, `PS3 NOTIFY`) are fine.

### What actually works

**`/getmem.ps3mapi?proc=&addr=&len=` on port 80** returns a correct HTML
hexdump. Measured limits:

| | |
|---|---|
| Max per request | **256 bytes** — larger silently truncates to 256, no error |
| Keep-alive | **None** — console closes the connection each request |
| Latency | ~28–31 ms regardless of size |
| Throughput | ~8 KB/sec |

Rows render 16 bytes each, so a request must be rounded up to a row boundary or
it parses as nothing. A 4-byte read costs the same as a 256-byte one, so always
fetch whole rows and slice.

### TCP 7887 — enabled, verified, and the transport we use

Turned on 2026-08-20 and **confirmed working**, reads and writes both.

Enabling it is not where you would look: it is on the **Setup** page, not the
PS3MAPI page, inside the *VSH MENU* section on the `DEL CFW SYSCALLS` line —
`<select name="sc8">`, default `0` (Disabled). Set to `1` and **reboot**; the
server only binds the port at boot.

| | HTTP `/getmem.ps3mapi` | TCP 7887 |
|---|---|---|
| Max per read | 256 B | **64 KB** (no cap found) |
| Latency | ~23 ms | ~61 ms |
| Throughput | ~9 KB/sec | **~1045 KB/sec** |
| Writes | impossible | **yes** |

Note the tradeoff: TCP has *higher* per-read latency, because it sets up a PASV
data connection each time. It wins on throughput by ~120x, and throughput is
what matters, because one 64 KB read costs the same as a 4-byte one. Never
issue many small reads on this transport; read a span and slice it.

Writes verified by round trip into the inter-segment padding (see below):
a 16-byte pattern read back exactly, `write_u32`/`read_u32` agreed, and
`write_f32(1.5)` produced raw `3F C0 00 00` — correct big-endian IEEE-754, so
the endianness handling is right end to end.

### A safe place to test writes

`0x01310768`–`0x01320000` is the **page-alignment gap between the code and data
segments** — 63,640 bytes that no program header claims, reading as all zeros.
Nothing in the game references it, so it is the place to exercise a write
without risking game state. Restore it to zeros afterwards anyway.

### Three more quirks, each of which cost time

- **`PROCESS GETALLPID`'s JSON is ambiguous — do not parse it.** The emitter
  drops the comma immediately after every hex value, gluing the next element
  on. Two live PIDs came back as `0x10102000x10003000`; the true values were
  `0x1010200` and `0x1000300`. Note the naive read yields `0x10003000`, an
  8-digit PID that does not exist — and a wrong PID reads as zeros rather than
  erroring, so this would have looked like "the game maps nothing".
- **`PROCESS GETNAME` returns empty**, even for the XMB. The GUI's `<option>`
  labels are the only source of names, and they are good ones:
  `01010200_main_EBOOT.BIN` and `01000300_main_vsh.self`.
- **A wrong PID reads as zeros, not an error.** Unmapped memory reads as zeros
  too. Zeros never prove anything — validate the PID first.

Addresses are parsed as hex with or without `0x`. `hexview.ps3` returns 501 on
this build, and `&dump=` does not bypass the 256-byte cap.

### Two things that come free

Worth recording because the Empire Earth project paid dearly for the equivalent:

- **`PS3 NOTIFY <message>`** puts a toast on screen — see the section below.
  **CCAPI's `/ccapi/notify?id=<icon>&msg=` is nicer still**, carrying trophy
  icons rather than the plain info icon.
- **`MODULE LOAD <pid> <path>`** loads an SPRX into a running process over the
  network. This is the injection path, and it is *better* than EE's
  `CreateRemoteThread` + stub: code in an SPRX runs on the game's own thread.
  EE is currently stuck precisely because injected threads crash on state
  transitions (its `0x00551F3A` end-of-match problem). That class of bug does
  not exist here.

---

## The item channel: `PS3 NOTIFY` renders over the running game

**Confirmed visually 2026-08-20** (photographed off the TV). Sending
`PS3 NOTIFY  --AP-- Test ping` through the JSON bridge drew a toast **on top of
Dead Souls while it was running** — not on the XMB, not only at the dashboard.
That is the whole requirement for item-received messaging, and it works with
zero reverse engineering.

Compare what the Empire Earth project paid for the equivalent: a long stretch of
its REVERSE.md locating `EEUserInterface::ShowGameMessage`, decoding the UI
event queue, allocating a page in the target, writing an x86 stub and running it
on a remote thread. Here it is one HTTP GET.

What the photo settles:

- The `--AP--` prefix renders intact, dashes and all.
- A **leading space is visually absorbed** into the icon padding — harmless,
  but it buys nothing, so do not bother with one.

Send cost is ~16-33 ms, so the message rate is not a constraint.

### Use `/notify.ps3mapi`, not the `PS3 NOTIFY` bridge command

The bridge command works but only takes text. The real endpoint, read off
webMAN's own form, takes more:

```
/notify.ps3mapi?msg=<text>&icon=<0-50>&snd=<sound>
```

| Parameter | Range |
|---|---|
| `msg` | **199 characters** max, per the form's `maxlength` |
| `icon` | **0-50**, indexing the XMB's own icon set; 0 is the plain info "i" |
| `snd` | `""` silent, 1 simple, 2 double, 3 triple, 0 cancel, 4 cursor, **5 trophy**, 6 decide, 7 option, 8 system_ok, 9 system_ng |

`snd=5` (trophy) suits an item landing far better than a silent info popup.
Icon names are known for 0-19 from CCAPI's `NotifyIcon` enum — Info, Caution,
Friend, Slider, WrongWay, Dialog, DialogShadow, Text, Pointer, Grab, Hand, Pen,
Finger, Arrow, ArrowRight, Progress, Trophy1-4 — and 20-50 are undocumented.

### Icons: only CCAPI honours them, and only from the built-in set

Measured, id by id, watching the screen:

| Route | Icon | Sound |
|---|---|---|
| webMAN `/notify.ps3mapi?icon=0-50` | **ignored** — all 51 draw the info "i" | `snd=5` works |
| webMAN `/popup.ps3?icon=<n>` | **ignored** | — |
| webMAN `/popup.ps3?icon=<name>&rco=<plugin>` | **ignored** — named lookups fall back too | — |
| CCAPI `/ccapi/notify?id=` | **works** | `snd` ignored (tested, silent) |

Confirmed CCAPI ids: **2 = friend**, **12 = gold trophy**. Ids 0, 1, 15, 16, 17,
19 fall back to the info icon; 3-11, 13, 14, 18, 20, 22 are all distinct icons
(not individually catalogued — cosmetic, and stage 2 supersedes it).

Two traps: CCAPI's `ccapi.h` enum order does **not** match what the XMB draws —
it calls id 12 "Finger" and it is a gold trophy — and an unmapped id degrades
silently to the info icon rather than erroring. Only ship an id seen on screen.

### DEAD END: a custom Archipelago logo in an XMB notification

**Not achievable via webMAN. Do not retry.** The evidence is below, including a
first attempt that was wrong — read the method note before trusting the verdict.

No notification API on the PS3 accepts an image path. Every icon parameter is
either a numeric index into a built-in set, or the *name* of an icon already
inside an `.rco` — Sony's compiled resource containers in
`/dev_flash/vsh/resource/`.

The promising route was `/popup.ps3?...&icon=<rsc_icon>&rco=<plugin_name>`,
which takes a named icon from a chosen RCO. Pair that with a `mappath.ps3mapi`
redirect pointing an RCO at a modified copy on HDD and you get a custom icon
with **no flash write** and full reversibility.

**Method note — the first test of this was invalid.** It passed an invented
icon name (`tex_trophy`) and an RCO name that does not exist on the console
(`explore_plugin`; the real files are `explore_plugin_game`, `explore_plugin_ft`,
`explore_plugin_full`, `explore_plugin_np`). Since an unrecognised icon is
*documented* to fall back to info, those results could not distinguish a bad
input from a broken feature. Do not repeat that mistake: the console's own
directory listing gives all 123 RCO names, so verify before testing.

**Retested properly, and it does fail.** Five combinations using the documented
example name `item_tex_cam_facebook` and `item_tex_trophy` against *verified
existing* RCOs — `np_sns_plugin`, `photo_network_sharing_plugin`, `xai_plugin`,
`np_trophy_plugin` — all fell back to the info icon.

That, together with numeric icons also being ignored on both webMAN endpoints
while CCAPI's ids work fine, means **webMAN MOD 1.47.48's icon resolution is
simply non-functional here**. There is nothing to hang the RCO trick on.

The only remaining route is overwriting an RCO in `/dev_flash` via `/dev_blind`:
persistent, system-wide for every notification, and a botched write is how
consoles get bricked. For a cosmetic gain, a bad trade.

**The answer is stage 2.** A PS3-CKit SPRX draws in the game's own UI, where
`assets/archipelago.png` (512x512, from the Archipelago source `data/icon.png`)
renders directly at any size and position, with no system modification. That
version also looks like part of the game rather than a system popup — the
Empire Earth `--GAME--` banner standard.

Note the tooling overlap: reading names out of an RCO needs a real parser (the
name tables are compressed; a naive scan finds only texture blobs), and
*building* an RCO around our PNG needs the same. So even the "good" branch of
this required rcomage-class tooling. Stage 2 needs none of it.

Until then the best available is **CCAPI id 12, the gold trophy**, which at
least reads as "you received something".

### Bursts queue — confirmed, and it settles a design question

Four notifications fired back to back with **no delay** all appeared, counting
`1 of 4` through `4 of 4`, as did a spaced set. So XMB toasts **queue rather
than replace each other**.

This matters more than it sounds. Archipelago sends items in bursts — a release
or collect can fire a dozen at once — and the working assumption was that the
client would need an outbound queue with a minimum gap between sends. **It does
not.** Send them as they arrive.

### Still unverified

- **Punctuation.** `:` `&` `%` go out URL-encoded; whether they arrive literal
  or as `%3A`-style escapes has not been read off the screen. Matters because
  Archipelago item names look like `Weapon: Shotgun`.
- **Length.** 60 and 120 character messages were accepted; where truncation
  begins is unknown.

---

## Confirmed addresses

`NPEB02034` (EU PSN digital). All values **big-endian**.

| Address | Type | What | How confirmed |
|---|---|---|---|
| `0x01534DE4` | array | **INVENTORY** — 8-byte records, stride 8 | Wrote a record, item appeared |
| `0x01537E18` | u32 | **Money (yen)** | Wrote 12345, HUD showed 12,345 |
| `0x0154BDB4` | u16 | **HP current** | Wrote 90 over 300, bar dropped to ~1/3 |
| `0x0154BDB6` | u16 | **HP max** | Reads 300 alongside current |
| `0x0154BDCC` | u32 | **EXP** | Wrote 100, display moved to "50 to next" |
| `0x0154BDC8` | u32 | EXP mirror — **inert** | Writing here alone changed nothing |
| `0x01536731` | u8 | Ammo **display only** | UI followed it; the gun did not |

### THE INVENTORY — and granting an item is one 8-byte write

The single most important find so far, because "receive an item" is the one
thing a multiworld client absolutely must do.

An array of 8-byte records at **`0x01534DE4`**, stride 8:

```
+0x00  u16  item id
+0x02  u16  padding, observed 0
+0x04  u32  quantity
```

A free slot is eight zero bytes. **Writing one well-formed record into a free
slot grants the item** — confirmed by writing `00 0B 00 00 00 00 00 01` into a
free slot and watching a third Tauriner appear in the menu, then a fourth
through `grant_item()`.

**Nothing else needs updating.** The item-count bytes at `0x0160FD1A` and
`0x01615152` still read 2 after the write, and the header at `0x01534DE0` still
read 6, yet the game displayed 3 items. Those counters are derived or cosmetic;
the slot array is authoritative.

`Tauriner = id 11`. Ids look small and dense, so the pool can be enumerated by
writing each id and reading the name off the menu — no name table needed.

#### How it was found, and why the first attempt failed

An earlier 8-byte record table at `0x01536628` looked exactly like an inventory
— right shape, right neighbourhood, and it contained the working ammo value —
but it was not. A player carrying nothing had 38 populated records there.
Writing a well-formed record into its "free" slots granted nothing.

The real one was only findable after learning that **items do not stack**: each
Tauriner takes its own slot at qty 1. That changed the search signature
completely. Instead of hunting a stack count going 1 -> 2, the thing to look
for was **two different slots receiving the same id at different times**:

```
slot A:  0 -> id -> id      (filled by purchase 1)
slot B:  0 -> 0  -> id      (filled by purchase 2)
```

Three snapshots (none / one / two) and that pattern found it immediately. The
`0 -> 1 -> 2` u8 candidates the naive search produced were item *counters*, not
inventory at all.

The lesson is about game-specific behaviour beating generic technique: no
amount of scanning would have found this while searching for the wrong shape.
One sentence of "items don't stack here" was worth more than the whole filter
pipeline.

### Chapter progression: no sequential counter exists

Tested properly across **two** chapter transitions (1→2 and 2→3), with two idle
snapshots taken beforehand to subtract ambient churn (~1600 u32 addresses move
on their own while standing still).

**Zero addresses went 1 → 2 → 3, at any width.** So the game does not keep a
simple incrementing chapter number. That fits the on-screen text being
"Part I Chapter 2: Cut Off" — a named entry, likely keyed by scene id rather
than a sequence.

Five addresses did go 1 → 2 at the first transition. All were eliminated by
watching them live through the second:

| Address | Behaviour | Verdict |
|---|---|---|
| `0x01657DA8`, `0x016F3E48` | move in lockstep, random small values | per-frame / RNG |
| `0x013A19B0`, `0x013A19B8` | flicker to `0xFFFFFFFF` and back | transient "current X" index |
| `0x0160984C` | 0 → 1 → 2 within one transition | scene counter, not chapter |

Note none of them drove the pause-menu chapter text either - poking them to 7,
3, 4, 5, 6 changed nothing on screen.

**Why a chapter counter was the wrong target anyway.** For Archipelago we do not
need the chapter *number*, only a reliable signal that a chapter ended. And a
chapter transition is the worst possible event to diff against: it reloads an
area and rewrites megabytes. A bit-accumulation search over both transitions
found 7,416 candidate flag bytes in 331 clusters - far too many to act on.

**The right event is a small one.** A substory completion is discrete, sets
presumably one flag, does not reload the map, and is itself the location we
want. Substories unlock in chapter 3. Take the idle pair standing next to the
NPC, complete it, snapshot before moving anywhere, and the diff should be tiny.

Regions worth re-checking once a clean substory diff narrows things down - these
gained bits at *both* transitions rather than only the second, which is the
flag-like signature:

```
0x0164A407 - 0x0164A6EA    e.g. 0x0164A41B  00 -> 80 -> C0
                                0x0164A41F  30 -> B0 -> F0
0x0150EACC - 0x0150EE9F    changed at the first transition, stable after
```

### The stats struct, re-read at level 7

Several fields only became legible once the character had levelled. What was
recorded at level 1 was partly wrong.

| Offset | Address | Type | Meaning |
|---|---|---|---|
| +0x00 | `0x0154BDB0` | u32 | unknown, reads 2 |
| +0x04 | `0x0154BDB4` | u16 | HP current |
| +0x06 | `0x0154BDB6` | u16 | HP max |
| +0x08 | `0x0154BDB8` | **f32** | **Focus current** |
| +0x0C | `0x0154BDBC` | **f32** | **Focus max** (4000.0) |
| +0x10 | `0x0154BDC0` | f32 | unknown, reads 1.0 — multiplier? |
| +0x14 | `0x0154BDC4` | u8 | **Level** |
| +0x18 | `0x0154BDC8` | u32 | EXP **total**, cumulative |
| +0x1C | `0x0154BDCC` | u32 | EXP **within the current level** |
| +0x26 | `0x0154BDD6` | u8 | **Ability points** |

Note the struct mixes widths freely — HP is a `u16` pair and Focus is an `f32`
pair four bytes later. Reading either with the wrong accessor returns a
plausible number rather than an error: `0x457A0000` (4000.0) read as `u16`
gives 17786.

#### Focus, and the evidence that settled it

Found by writing 1000.0 over a full 4000.0 gauge. The in-game bar did **not**
appear to change, because the pause menu had already drawn itself — the same
caching that hid the level display earlier.

What proved it was the *next* read: the value had become **1036.0**. The game
had regenerated it. A value that climbs on its own after being written is being
actively read and written by the game, which is far stronger evidence than a
static readback — that only shows nobody objected.

This also inverts the ammo-display failure. There the display followed while
behaviour did not; here behaviour followed while the display appeared not to.
**Watch what the game does with a value, not what the screen shows.**

**Correction: `+0x18` is not an "EXP mirror".** It was recorded as one because
at level 1 it held the same value as `+0x1C`. At level 7 they read 4950 and 900
— total versus current-level progress. Two fields that happen to agree early
are not the same field.

### DEAD END: the next-level threshold is not readable

The probe printed `to next: 4294966546`, which is unsigned underflow from a
hardcoded `Level1Threshold = 150` — a level-1 observation mistaken for a
constant. Thresholds scale with level: 150 at level 1, 1650 at level 7.

Searching for 1650 across the data segment gave exactly one u16 hit, at
`0x014F4ABC`, and it is **coincidence** — the surrounding values are noise and
an `SLLZ` magic sits just after, so that region is SEGA-compressed data, not a
table.

Two points fit `150 + (level-1) * 250`, but a line through two points always
fits, so that is a guess and is **not** shipped. The display now shows level,
current EXP and total EXP, all of which are read directly, and omits
"to next" entirely.

If the threshold is ever needed, the way in is a breakpoint on the level-up
routine in RPCS3, not a memory search.

### The character stats struct at `0x0154BDB0`

Three of the five confirmed values live in one structure, which makes its
unknown fields the cheapest place to look for more:

```
offset  address     bytes          meaning
+0x00   0x0154BDB0  00 00 00 02    u32 = 2       unknown
+0x04   0x0154BDB4  01 2C          u16 = 300     HP current   CONFIRMED
+0x06   0x0154BDB6  01 2C          u16 = 300     HP max       CONFIRMED
+0x08   0x0154BDB8  00 00 00 00                  unknown
+0x0C   0x0154BDBC  45 7A 00 00    f32 = 4000.0  unknown
+0x10   0x0154BDC0  3F 80 00 00    f32 = 1.0     unknown (multiplier?)
+0x14   0x0154BDC4  01 00 00 00                  unknown
+0x18   0x0154BDC8  <exp>          u32           EXP mirror, inert
+0x1C   0x0154BDCC  <exp>          u32           EXP          CONFIRMED
```

### Two lessons that will keep paying out

**Search the change, not the number.** EXP is stored counting *up*; the "N to
next level" on screen is computed as `threshold - exp`. Scanning for the
displayed 150 -> 100 returned **zero hits at every width**. Diffing for a
*delta of 50* found it instantly. Anything shown as a countdown, a percentage,
or a bar needs the same treatment - which is also how HP was found without ever
seeing a number.

**Mirrors are silent.** `0x0154BDC8` and `0x0154BDCC` held identical values and
only one was read. Writing to the wrong one looks exactly like a no-op, not
like an error. Every address in this project therefore earns its place by
moving something on screen, and mirrors are recorded as such rather than
deleted, so they are not "rediscovered" later.

### Addresses survive a game restart. The PID does not.

Verified 2026-08-21: after quitting to the menu and starting a **new game**, all
of money, HP and EXP read correctly from the same addresses. No ASLR holds in
practice, the static data segment is reliable, and **the client can hardcode
addresses**.

The PID, however, changed from `0x01010200` to `0x01030200` — a `0x20000` step
per launch. So:

**Never hardcode the PID.** Always resolve it through `attach()` or
`api.game_pid()`.

This bit during that very check. A script with the old PID baked in reported
money 0, exp 0, HP 0/0 — which looks exactly like "the addresses moved" and
nearly produced the wrong conclusion that the whole approach was unstable. It
is the documented "a wrong PID reads as zeros rather than erroring" trap, and
knowing about it did not prevent walking into it.

If every value suddenly reads 0, **check the PID before suspecting anything
else.**

### A value that drives the display may not drive behaviour

Ammo has **two** representations. `0x01536731` is what the HUD prints - writing
99 there made the UI say 99 - but the weapon still reloaded after 13 rounds.
Its real magazine count lives somewhere else and was never touched.

This is the EXP-mirror lesson inverted. There, two copies existed and only one
was read for the display. Here, the value that *is* read for the display is not
the one the game acts on.

The general rule both cases point at: **a write that visibly "works" proves only
what that one address feeds.** It does not prove the address is the real value.
Confirm behaviour, not just pixels - fire the gun, do not just read the number.

### Displays can lag the memory

Money redrew immediately. The level-up display did not update until the value
that actually drives it was written. So a correct address can look wrong if the
display is cached - do not rule one out on a single unchanged reading.

### Money — `0x01537E18`

Found on the **first scan pass**: `scan.py --new --eq 60000` returned exactly
one candidate out of 1,061,122. Unusually clean — most games leave several
copies of a displayed value lying around. Writing 12345 changed it in-game, so
this is the live value and not a display mirror.

The surrounding memory is worth noting:

```
0x01537E08  00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 04
0x01537E18  00 00 EA 60 00 00 00 00 00 00 00 00 00 00 00 00
0x01537E28  00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

The value sits alone in a field of zeros with a lone `04` immediately before it
— the shape of a structured save/stats block rather than a scratch buffer. If
that holds, other wanted values (HP, chapter progress, upgrade and inventory
flags) are plausibly neighbours at small offsets from here. **Scan for the next
value, then check how far it lands from `0x01537E18`** — if the block theory is
right, later finds get much cheaper than this one.

### The parity check this unlocks

This address is the RPCS3 test: load the same save in the emulator, read
`0x01537E18`, and if it reads the same the no-ASLR assumption holds and the
emulator-as-debugger plan is validated. Cheap now, and it decides whether
breakpoints are available for the rest of the project.

---

## The memory map — read live from the running game

`tools/elfmap.py` reads the EBOOT's own ELF headers straight out of the live
process. No dump, no decryption, no Ghidra needed to get this:

```
ELF64 big-endian, PPC64, entry 0x01353C20, 8 program headers at 0x40

  code (RX)  0x00010000 - 0x01310768   19.0 MB
  data (RW)  0x01320000 - 0x0172C408    4.0 MB
  TLS        0x0136015C                 672 B
  SCE_1      0x01310700                  40 B
  SCE_2      0x01310728                  64 B
```

So:

- **Function addresses live in `0x00010000`–`0x01310768`.** That is the range to
  load in Ghidra.
- **Game state lives in `0x01320000`–`0x0172C408`.** That is where a value
  scanner should be pointed. Four megabytes is a small enough haystack to scan
  even at 8 KB/sec if narrowed sensibly.

The entry point at `0x01353C20` sits inside the *data* segment. That is correct
and not a misread — on PPC64 ELFv1 the entry is a function descriptor (OPD),
not code.

---

## Why not CCAPI, given it is installed

CCAPI is the faster transport and the obvious question. It is **not** being used
for memory, and the reason is worth recording so it does not get revisited.

CCAPI is two protocols on port 6333:

- An **HTTP surface** for console commands — `/ccapi/notify?id=<icon>&msg=<text>`,
  `/ccapi/shutdown?mode=<n>`. Documented and trivial. Implemented in
  [`tools/ccapi.py`](../tools/ccapi.py).
- A **binary command-ID protocol** for everything else, memory included. A
  packet carrying a command id goes to the console, which switches on that id.
  This format is **not publicly documented** (v2.00–2.50 also encrypted it).

So `CCAPIGetMemory`/`CCAPISetMemory` are reachable only through `CCAPI.dll`,
which is **32-bit x86**. Every Python wrapper — `iMoD1998/PS3API` and the rest —
therefore demands 32-bit Python. **An Archipelago client runs inside
Archipelago's own 64-bit Python**, so the DLL can never be loaded in-process.
That is the blocker, and it is structural rather than a matter of effort.

The escape hatches, none worth taking:

| Route | Verdict |
|---|---|
| 32-bit helper process wrapping the DLL, local socket to the client | Works, but costs every player a second process and an install step |
| Reverse the binary packet format | A side quest, not a project |
| Just use PS3MAPI | Fully documented, already written, fast enough for RE |

And the point is moot at stage 2 anyway: an SPRX removes the network from the
hot path entirely, so the transport's speed stops mattering.

**What CCAPI is still worth keeping for:**

- **Notifications with icons**, including trophy icons — a much better fit for
  "item received" than PS3MAPI's plain toast.
- **The tools ecosystem speaks it.** Memory searchers such as NetCheatPS3 talk
  CCAPI, and value-scanning on real hardware pairs well with RPCS3's debugger:
  scan on console to find *where*, break in the emulator to learn *why*.

---

## Planned architecture

Two stages, because the first is cheap and finds the addresses the second needs.

1. **Python client polling PS3MAPI.** Structurally the same as the EE client —
   `attach()`, `ProcessHandle.read_u32`, `resolve()` for pointer chains. Good
   enough to prove checks and items work.
2. **SPRX plugin inside the game process**, talking to a thin PC-side
   Archipelago bridge over TCP. Kills the per-read latency, allows real function
   hooks, and allows calling the game's own UI. **Unblocked** — Cobra 8.5 is
   well past the 8.3 minimum — and **do not build this from scratch**: use
   PS3-CKit, which already solves the loader, the SPRX build and the hooking.
   The work is a Dead Souls base patch. See "Prior art" below.

Do not start on 2 before 1 has found stable addresses.

## Toolchain

| Tool | What it is for |
|---|---|
| **RPCS3** | Breakpoints, memory viewer, PPU disassembly. Understanding *why*. |
| **NetCheatPS3** (over CCAPI) | Value scanning on real hardware. Finding *where*. |
| **Ghidra** | Static analysis of the decrypted EBOOT. PowerPC 32, big-endian. |
| **`tools/probe.py`** (PS3MAPI) | Link test, latency benchmark, memory dumper. |

The scan-on-console / break-in-emulator pairing is the useful bit: a hardware
scan gives an address that is true on the machine we ship to, and the emulator
then explains what writes it.

---

## Two differences from every previous project here

Both will cause silent wrong answers rather than errors, so they are worth
stating loudly:

1. **The PPU is big-endian.** Every struct format is `>`. A value reading as
   `0x01000000` is 1.
2. **PPC64 architecture, ELF32 executable.** Ghidra handles PowerPC, but
   `EBOOT.BIN` is an encrypted SELF and must be decrypted to ELF first. Retail
   keys are public.

---

## Game files are readable, and faster than memory

webMAN's web server serves the installed game directory over HTTP, and it is
**~17 MB/s** — more than 10x PS3MAPI's 1.3 MB/s.

**It does NOT honour HTTP `Range`.** Sending one returns the whole file anyway,
silently. The 63 `chara_arc` archives are ~10 MB each so this went unnoticed
(517 MB in 38 s reads like a fast partial fetch), but `chara.par` is ~1 GB and
every "96 KB header fetch" against it downloads the lot. Download large
archives once to disk and query the local copy.

Game data lives at `/dev_hdd0/game/NPEB02034/USRDIR/data/`.

### PAR archives

`PARC` is SEGA's archive format, big-endian here:

```
0x00  "PARC"
0x04  flags (0x02 = big endian)
0x10  u32 directory count
0x14  u32 directory table offset
0x18  u32 file count
0x1C  u32 file table offset
```

Filenames sit as plain ASCII between the header and the directory table, so
`re.findall(rb'[ -~]{4,}', data[0x20:diroffset])` reads the contents listing
without decompressing anything. Entry *data* is compressed (`SLLZ` blocks) and
textures are `DXT1`/`DXT5`, so plaintext search inside entries finds nothing.

Model naming: `c_am_` character/adult male, `c_fw_`/`c_bw_` female hostess
face/body, `c_zn_` zombie, `c_ak_` child. Suffixes `_di _tn _mt _sp _rd` are
texture maps, `.gmd` is the model.

### PAR file table format (decoded)

After the 64-byte-per-entry name table, the file table at `fileOffset` holds
32-byte entries, big-endian:

```
+0x00  u32  flags     0x80000000 = SLLZ-compressed, 0 = stored plain
+0x04  u32  uncompressed size
+0x08  u32  compressed size
+0x0C  u32  data offset
+0x10  u32  0x20
+0x1C  u32  timestamp
```

Names live as 64-byte NUL-padded fields starting at 0x60, one per file, in the
same order as the file table.

**SLLZ decompression is unsolved.** Header is little-endian inside a big-endian
container: `"SLLZ"`, endian byte, version, `u16` header size (0x10), `u32`
uncompressed size, `u32` compressed size. The payload is not a plain
flag-byte + literal/match LZ77 — sixteen combinations of flag polarity, offset
width (11/12/13 bits), offset base and length base all failed to produce a full
decode. Entries stored with flags `0` are readable directly and are the way in
where one exists.

### The ability bitfield at `0x01530210`

Mapped by clearing the field, granting 255 ability points, and buying abilities
one at a time while `watch` recorded which bit flipped. Confirmed:

| Bit | Ability | Bit | Ability |
|---|---|---|---|
| 0 | Max Focus: Epic | 5 | Unarmed Expertise |
| 1 | Head Tracking | 6 | Demolition Man |
| 2 | Head Lock-On | 7 | Rapid Reload |
| 3 | Super Grip | 8 | Head Hunter |
| 4 | Iron Arm | 9 | Unarmed Mastery |

Bits 1-9 are the Combat tab in menu order; bit 0 is the Basic tab's Max Focus:
Epic. Recorded in `data/ability_bits.tsv`.

#### SOLVED — all 39 of Akiyama's abilities

The first pass reported ~30 abilities "not firing". That was **contamination
from my own earlier test**: `0x01530210` and `0x01530214` had been left at
`FFFFFFFF`, so every ability stored there already read as owned and buying it
changed nothing. An all-ones field cannot tell you anything — every write is a
no-op. *Restore test writes immediately.*

It also produced a wrong theory (a "second storage location"). There is no
second location; the array simply extends **backwards** from `0x01530210`.

After clearing `0x015301F0`-`0x01530220` and re-buying, all 39 purchases fired,
each flipping exactly one bit. Full mapping in `data/ability_bits.tsv`.

| Word | Bits used | Contents |
|---|---|---|
| `0x0153020C` | 2-20, 22-31 | combat moves, Focus chain, all slot and skill upgrades |
| `0x01530210` | 0-9 | Max Focus: Epic, then Combat tab in menu order |

Notable:

- **Slot upgrades are bits, not counts.** `Inventory Slots: 12/14/18/20/22/24`
  each own a bit (`0x0153020C` bits 5-10), as do weapon and accessory slots.
  Granting inventory space is a single bit set.
- **Bit 21 of `0x0153020C` is unused**, sitting between Weapon Skill: Expert
  (20) and Armor Skill: Intermediate (22).
- **`0x01530210` bits 10-31 are unused**, and the words either side are zero.
  Ample room for the other three protagonists, which fits the four-character
  structure.

#### Why the earlier offset hunt was doomed

The menu calls these *"Max Focus: Epic"*, *"Focus Recovery: Enhanced"* and
*"Inventory Slots: 12"*. The string table dumped from `0x30D6CF03` calls the
same things *"Epic Max Focus"*, *"Enhanced Focus Recovery"* and *"Carry 12
Items"*. **It is a different string table** — not the menu list — so no offset
was ever going to reconcile bit order with it. That file is kept as
`data/ability_names_alt.tsv` rather than deleted, since it is still a real
in-game table, just not this one.

Lesson: before hunting for an offset between two orderings, confirm the two
lists are actually the same list.

### DEAD END for now: ability bit mapping from game files

`ikusei/` turned out to be **hostess club training** data, not the player
ability tree — `sug`, `eha`, `och`, `oto`, `wat` are hostess abbreviations and
`sabori` is Japanese for slacking off. Only `ikusei_fighter.bin` (3855 bytes
uncompressed) and `ft_hurt.bin` look combat-related, and both are SLLZ, so they
are locked behind the unsolved decompressor.

No pointer table to the ability name strings exists in either the data segment
or the 1 MB around the strings themselves, so the names are not referenced by
address from an indexable array.

**What is established about the bitfield:** bit 0 = *Epic Max Focus*, bit 1 =
*Head Tracking*. Those are indices 4 and 29 in the menu-order name table, so
the bitfield has **its own ordering** and no constant offset maps between them.

### Where things actually live

| Archive | Contents |
|---|---|
| `chara_arc/install.par` | the four protagonists' base models |
| `chara_arc/download.par` | **hostess club DLC** — cabaret girls and their dresses |
| `chara.par` | everything else, including alternate outfits |

`1.edat` is an NPDRM-encrypted file (magic `NPD\0`) and yields nothing readable.

### Alternate outfits: present in the EU build

Confirmed in `chara.par`:

```
c_am_kiryu_american          Kiryu's "Americana" outfit
c_am_kiryu_darts_american    darts-minigame variant
b0_c_ak_haruka_devil         Haruka's "Devil" outfit
```

**Method note.** An earlier pass concluded these were absent. That was wrong
twice over: it searched only `chara_arc` and not `chara.par`, and it searched
for `stars`/`stripe` from the US pre-order name. The asset is named
`american`. A negative result from a guessed keyword over an incomplete search
area is worth nothing — widen the area and vary the term before concluding
anything is missing.

---

## Prior art

**For Dead Souls itself: none.** ModDB has a sound effect / HD font / HUD mod
and that is essentially the whole scene. No file format documentation, no
published RE, no tooling. Unlike Empire Earth — where `dbobjects.dat` gave a
complete object database to read names and epochs out of — the game-specific
work starts from zero.

**For Archipelago on real PS3 hardware: it has been done.** Panda291's
[Ratchet & Clank 1 world](https://github.com/Panda291/Archipelago/tree/main/worlds/RAC1)
runs on a modded PS3. Digging past its setup guide, the RAC ecosystem has
proven *both* of the architectures planned here:

### Stage 1 — the approach we already have

[**racman**](https://github.com/MichaelRelaxen/racman) is the memory layer under
bordplate's RAC1 randomizer. Its requirements are ours exactly: **webMAN MOD**
on a jailbroken PS3 (CFW or HEN), same network, type in the console's IP. It
insists on the *full* webMAN MOD build, which is consistent with needing the
PS3MAPI server rather than just the web UI.

Worth noting its RPCS3 caveat: patch loading and anything that edits game code
is **not supported** under emulation. So the emulator is the weaker target for
this class of tool, not the stronger one — which cuts against the usual
assumption and supports building for hardware first.

### Stage 2 — and it is a maintained framework, not a from-scratch build

[**PS3-CKit**](https://github.com/tge-was-taken/ps3-ckit) by TGE is a "PS3 C code
injection framework": run arbitrary C inside the game and hook existing
functions. The shape is a handwritten assembly loader inserted at a chosen
address during `main`, which loads `mod.sprx` from the game directory; the whole
thing ships as a `.pkg`.

Each game needs its own **base patch**, supplying the loader with the import
functions it needs at boot. That is the game-specific work — bounded, and with
two reference implementations to read: RAC1 multiplayer and
[persona5-randomizer](https://github.com/AAGaming00/persona5-randomizer), the
latter being evidence it generalises beyond one series.

This meaningfully de-risks stage 2. The plan was "write an SPRX and a PC bridge
from scratch"; the plan is now "write a Dead Souls base patch for an existing
framework". Also worth a look when the time comes:
[SPRXPatcher](https://github.com/NotNite/SPRXPatcher) and
[ps3SprxBlank](https://github.com/MichaelGriffin1/ps3SprxBlank).

---

## Done

- [x] Link verified end to end. `probe.py` connects, lists processes, reads the
      game's ELF header, and benchmarks the round trip.
- [x] Game identified and confirmed: `NPEB02034`, EU PSN digital.
- [x] Both notification paths confirmed on screen (PS3MAPI plain, CCAPI trophy).
- [x] CCAPI on 6333 and webMAN on 80 coexist without trouble.
- [x] Memory map of the running game read live — code and data ranges above.

- [x] PS3MAPI server enabled on 7887; `TcpTransport` verified for read *and*
      write. Memory access is fully solved.
- [x] `tools/scan.py` — value scanner. Sweeps the 4 MB data segment in 3.8s at
      ~1095 KB/sec, 1.06 M u32 candidates per pass.

## Open, next session

The plumbing is done. Everything below is game reverse engineering.

- [ ] **Find money.** The classic first value, and the proof the scanner works
      on real game state. `scan.py --new --eq <amount>`, spend some, `--eq
      <new amount>`, repeat until a handful remain. Poke one and watch the HUD.
- [ ] **Stage 2 carries the Archipelago logo with it.** `assets/archipelago.png`
      is ready; the XMB route is a proven dead end (above), so the SPRX is the
      only way to get it on screen. Worth remembering that this is a *feature*
      of stage 2, not just a performance win.
- [ ] From there: HP, chapter/progress counters, inventory, weapon upgrade
      flags. These are the raw material for checks and items.
- [ ] Extract `EBOOT.BIN` from the installed game, decrypt to ELF, load into
      Ghidra as **PowerPC 64 big-endian** at base `0x00010000`, code range
      `0x00010000`–`0x01310768`.
- [ ] Get the same build running in RPCS3, so breakpoints become available for
      "what writes this address".
- [ ] **Address parity check** between RPCS3 and hardware. Now cheap: sweep the
      same region on both and compare. Decides whether the emulator-as-lab plan
      holds.

NetCheatPS3 is no longer needed for scanning — `scan.py` is faster to iterate
with and speaks the same transport as the eventual client.

## Check and item design — sketch only, nothing verified

Four protagonists (Akiyama, Majima, Ryuji, Kiryu) with chapter structure;
weapons and Kamiyama's weapon upgrades; substories; the Coliseum; inventory;
body upgrades. Plenty of checkable content. **None of this is confirmed against
the game yet** and no memory layout is known for any of it. Recorded here as a
starting shape, not as a design.
