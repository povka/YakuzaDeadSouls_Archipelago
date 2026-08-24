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
| +0x26 | `0x0154BDD6` | u8 | **Soul points** |

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

### Player outfits: the selector is in the hideout

The models exist in the EU build (`c_am_akiyama_mafia`, `_homeless`, `_narikin`,
`c_am_kiryu_american`, `b0_c_ak_haruka_devil` — see the game-files section).

**The selector is in the hideout, and the whole path is post-game.** The full
sequence, confirmed:

1. Beat the game, then **create Game Clear Data at the laptop on the desk in the
   Prosthesis Shop**.
2. Load that save into **Premium Adventure**.
3. In a hideout (Kamiyama's van or the Prosthesis Shop), select **"Change
   Outfit"**, move a character from **"Basic"** to **"Change"**, pick from the list.

Note the shape of the gate: it is not one flag on the running game, it is a
**distinct save type**. The laptop writes Game Clear Data, and Premium Adventure
is a separate load path for it. Flipping a byte in a chapter-3 session will not
reproduce that.

Each character has four outfits; the fourth is **Patriot Pack** DLC:

| Character | 2nd | 3rd | 4th (DLC) |
|---|---|---|---|
| Akiyama | Homeless (Y4) | Hipster (Y4/5) | **Gangster** |
| Majima | Chairman (Y3/4) | Shirtless | Pirate |
| Ryuji | Takoyaki Chef | Samurai | Bandolier |
| Kiryu | Ryukyu (Y3) | Dragon Mask (Y3) | **Americana** |

That maps cleanly onto the model names found in `chara.par`:
`c_am_akiyama_homeless` → Homeless, `_narikin` (成金, "nouveau riche") → Hipster,
`_mafia` → **Gangster, the DLC one**, and `c_am_kiryu_american` → **Americana,
also DLC**. Both Patriot Pack models being present in this EU build means the
DLC content ships on-disc or the pack is already installed.

**Bob Utsunomiya is not the outfit source.** Correcting the earlier note: Bob A
runs *challenges* (Endless Subterranea, Extreme Chase Challenge, Kamurocho
Survival Gauntlet) and Bob B hands out *completion rewards* plus, with the
Patriot Pack, *Apocalypse Fun Packs*. Both are Premium Adventure. Unrelated to
outfits — but see the location note below, because they matter for a different
reason.

Consequences for finding the outfit selector:

- **No in-game outfit change to diff.** The method that found money, EXP, the
  inventory, Focus and all 39 abilities needs the player to trigger the change,
  and that is unreachable before completing the game.
- **Outfits are not inventory items.** Nothing outfit-shaped exists anywhere in
  `data/items.tsv` outside the hostess-club block at 362-420. Bob's outfits use
  a separate unlock/equip system.
- **The model name is not in the data segment.** Searching for `c_am_akiyama`,
  `mafia`, `narikin` and `.gmd` found nothing. What *is* there at `0x0137B178`
  is a **preload manifest** of `.par` archive names in 64-byte entries — it even
  lists `c_am_saejima.par` and `c_am_tanimura.par` from Yakuza 4, so it is a
  generic asset list, not the current outfit.

**PARKED. Cost/benefit says stop here.** Reaching the selector requires either a
full playthrough or an externally sourced Game Clear save, and the payoff is one
filler-check type. Every other open item on this list is cheaper. Revisit if a
completed save turns up for another reason — see below, because one would unblock
considerably more than outfits.

### Strategic option: obtain a completed save

A high-completion PAL save has been obtained. Contents:

| Folder | SUB_TITLE | DETAIL |
|---|---|---|
| `BLES01399F` | System data | global/system data, 2048 B `USERDATA` |
| `BLES01399L01` | Save data 01 | **Premium Free Adventure (Kiryu)**, 130:46:12 |
| `BLES01399L05` | Save data 05 | **Clear Data**, 130:50:41, difficulty EASY |

`L01` is the valuable one — it is a save *already inside Premium Adventure*, so
it should reach the hideout outfit menu without going through the laptop step.
`L05` is the Clear Data that produces Premium Adventure the intended way.

Two facts that constrain how it can be used:

- **The title ID is `BLES01399`** (PAL disc), while this project's game is
  `NPEB02034` (EU PSN digital). Whether they share a savedata directory is
  **unverified** and is the blocking question — resolve it by looking at what
  folder the console's own Dead Souls save already occupies.
- **`USER01` is encrypted.** Entropy 8.00/8, longest identical-byte run 3,
  zero-byte frequency 0.4% (= 1/256). Indistinguishable from random. So there is
  **no offline save diffing** — a completed and a chapter-3 save cannot be
  compared byte-wise to extract flags. All flag discovery stays live-memory.
- The `ACCOUNT_ID` is the original owner's, so console use requires resigning
  (Apollo Save Tool runs on-console and does this; Bruteforce Save Data is the
  PC equivalent).

**Correction to an earlier claim in this file:** "RPCS3 needs no save signing" was
stated too confidently. RPCS3 stores savedata in its own form and it is not
established that it accepts a real encrypted PS3 save unmodified. Verify before
relying on it. The console path — resign and load — is the better-trodden one,
and the console is the ship target anyway.

Back up the existing chapter-3 save before any of this; it is the only copy of
the state behind the 39 mapped ability bits.

### Importing a foreign save: what actually has to change

`client/SfoTool` (`ydssfo`) parses PARAM.SFO and patches string fields in place.
`dump` prints every key with its format and size; `retarget <folder> <name> <id>`
rewrites `SAVEDATA_DIRECTORY` and `ACCOUNT_ID` and renames the folder to match,
so the two can never drift apart.

PARAM.SFO layout (little-endian, unlike everything else on this console): magic
`\0PSF`, then key-table and data-table offsets and an entry count at 0x08/0x0C/
0x10, then 16-byte index entries from 0x14 — `u16 keyOffset, u16 format,
u32 length, u32 maxLength, u32 dataOffset`.

Three string formats matter:

| Format | Meaning | Example |
|---|---|---|
| `0x0204` | UTF-8, null-terminated | `SAVEDATA_DIRECTORY`, max 64 |
| `0x0004` | UTF-8, **not** terminated | `ACCOUNT_ID`, max 16, zero slack |
| `0x0404` | u32 | `ATTRIBUTE` |

The `0x0004` / `0x0204` split is why the length guard uses `needed > max` rather
than `>=`: `ACCOUNT_ID` legitimately fills its field exactly, while a `0x0204`
value must leave room for its terminator.

What a rename does **not** fix:

- **`PARAM.PFD`** still holds the original signature and per-file hashes. The
  game rejects the save until it is resigned. Apollo Save Tool does this
  on-console.
- **The `PARAMS` blob** (`0x0004`, 1024 bytes) embeds the account ID a second
  time. After retargeting, the old ID still appears once per PARAM.SFO. Whether
  the game cares is untested; the resigner is expected to handle it.
- `SUB_TITLE` still reads "Save data 01" for a save now living in slot 4.
  Cosmetic, and the game likely rewrites it on next save.

Concrete state for this project: the PAL saves were renamed to `NPEB02034L04`
(Premium Adventure) and `NPEB02034L05` (Clear Data) — slots the console had free,
so no existing save is at risk — and retargeted to account `1d0d430cf4cbfc3f`.
`BLES01399F` was deliberately not imported; the console's own `NPEB02034F` system
data is left alone.

Console save slots in use before the import: `L01` (Part I Chapter 2, 0:02:20),
`L02`, `L03`. The real working save is L02 or L03, not L01.

**Still unverified and the thing most likely to break this:** savedata is
encrypted with a game-declared `SECURE_FILE_ID`. If the PAL disc build
(`BLES01399`) and the EU digital build (`NPEB02034`) declare different ones, the
game cannot decrypt the imported save at all, and no amount of resigning helps.
Only an empirical load test answers it.

### Apollo cannot resign NPEB02034 out of the box

Resigning the imported saves failed with *"Error! Save NPEB02034 couldn't be
resigned"*. Cause found by reading the Apollo source
(`bucanero-apollo-ps3`, `source/exec_cmd.c:1317`):

```c
if (!pfd_util_init((u8*) apollo_config.idps, apollo_config.user_id,
                   entry->title_id, entry->path) ||
    (pfd_util_process(PFD_CMD_UPDATE, 0) != SUCCESS))
    show_message(_("Error! Save %s couldn't be resigned"), entry->title_id);
```

`pfd_util_init` looks the title ID up in a key database
(`/dev_hdd0/game/NP0APOLLO/USRDIR/DATA/games.conf`, 1818 sections) via
`find_game_keys`, which does `strstr(game->game_ids, game_id)` against the INI
**section name**. The Dead Souls section is:

```ini
; "YAKUZA 4 Dead Souls / Ryu ga Gotoku of the End"
[BLUS30826/BLES01399/BLJM60316/BLJM55054/BLAS50310]
;disc_hash_key=
secure_file_id:*=908AA7013F9B2D0088E1CB98159101D2
```

`BLES01399` is present; **`NPEB02034` is not**. Apollo derives the title ID from
the save folder name, so renaming the import to `NPEB02034L04` is exactly what
broke the lookup — with no match it falls back to a generic disc hash key and an
empty `secure_file_ids` list, and the PFD update fails.

Note the failure is *not* caused by hand-editing PARAM.SFO after signing:
`apply_sfo_patches` runs earlier at line 1309 and has its own distinct message
("Account changes couldn't be applied"), which did not appear.

**Fix:** append `/NPEB02034` to that section name. Because matching is `strstr`
over the whole section string, one edit covers it.

**Incidental and important:** the same `secure_file_id`
`908AA7013F9B2D0088E1CB98159101D2` is shared by Yakuza 4, Dead Souls, and the
neighbouring RGG section. SEGA reused one key across these titles, which is
strong evidence the digital `NPEB02034` build uses it too — so the earlier worry
that disc and digital might declare different `SECURE_FILE_ID`s is very likely
unfounded.

**Also worth knowing:** Apollo exposes `PFD_CMD_DECRYPT` (`pfd_util.c:269`) and
the UI offers "Export decrypted save files". If that works, `USER01` can be
decrypted on-console, which would **reopen offline save diffing** — the technique
ruled out earlier when the payload measured as pure entropy. A completed save
diffed against a chapter-3 save would surface completion, chapter and substory
flags at disk speed instead of via 4-second console sweeps. Untested.

### `SAVEDATA_LIST_PARAM` marks Clear Data

Comparing the three saves on the console after import:

| Folder | `SAVEDATA_LIST_PARAM` | `SUB_TITLE` |
|---|---|---|
| `NPEB02034L02` (own, chapter 2) | `NORMAL` | Save data 02 |
| `NPEB02034L04` (Premium Adventure) | `NORMAL` | Save data 01 → fixed to 04 |
| `NPEB02034L05` (Clear Data) | **`CLEAR`** | Save data 05 |

So the game distinguishes Clear Data with a plaintext PARAM.SFO field, not
something buried in the encrypted payload. A `CLEAR` save does **not** appear in
the normal Load Game list — it is offered at the title screen under Premium
Adventure / Premium New Game. "Slot 5 is missing in-game" was expected behaviour,
not a failed import.

`SUB_TITLE` is not rewritten by a folder rename, so an imported save keeps the
slot label it had on the source console. `BLES01399L01` moved into
`NPEB02034L04` still read "Save data 01", colliding with the console's own L01 in
Apollo's list — Apollo displays the SFO `TITLE`, which is identical for every
save of the same game, so the two were indistinguishable. Patch `SUB_TITLE` to
match the new slot.

Apollo's enumeration (`saves.c:1721 read_savegames`) applies no filtering beyond
requiring a readable `PARAM.SFO`; it sets `SAVE_FLAG_LOCKED` from `ATTRIBUTE` and
`SAVE_FLAG_OWNER` when `ACCOUNT_ID` matches the current user. It derives the
title ID as `"%.9s"` of the directory name (`saves.c:1784`) — which is precisely
why renaming the folder to `NPEB02034L04` moved it outside the `games.conf` key
database.

**Order of operations matters:** any PARAM.SFO edit invalidates the hash stored
for it in PARAM.PFD, so always patch the SFO *first* and resign *afterwards*.

### SOLVED: saves can be decrypted, offline diffing works

Apollo's **"Decrypt save game files"** (`exec_cmd.c:1488 decryptSaveFile`) writes
plaintext to `/dev_hdd0/tmp/apollo/<dir_name>/`, which webMAN then serves over
HTTP. The key comes from `get_secure_file_id(entry->title_id, filename)` — i.e.
the `games.conf` entry.

**This retracts the earlier "no offline save diffing" conclusion.** That was based
on measuring the *encrypted* `USER01` and finding pure entropy, which was correct
about the file but wrong about what was possible.

Verification that the added key is right, using the console's own save as a
control:

| File | Entropy | Zeros | Longest run |
|---|---|---|---|
| `USER01` encrypted | 8.00/8 | 0.4% | 3 |
| `L02` decrypted (own save) | **0.32/8** | 96.2% | 32780 |
| `L04` decrypted (imported) | **0.59/8** | 94.1% | 32244 |

The console's own save decrypting cleanly proves `908AA7013F9B2D0088E1CB98159101D2`
is correct for `NPEB02034`, so the disc and digital builds do share SEGA's secure
file ID as predicted.

#### Save layout, first findings

Header, big-endian like everything else in this game:

| Offset | L02 (ch.2, 1:51:38) | L04 (130:46:12) | Meaning |
|---|---|---|---|
| `0x00` | `00000003` | `00000003` | format version, identical |
| `0x04` | `00000002` | `00000002` | identical, purpose unknown |
| `0x08` | `00062242` | `01AF01C7` | **play time in frames @60fps** |
| `0x3C` | `00000000` | `00000001` | differs; candidate mode/clear flag |

Play time confirmed on both samples: 1:51:38 x 60 = 401,880 vs 402,498; and
130:46:12 x 60 = 28,246,320 vs 28,246,471. Both within seconds.

Because `0x00`/`0x04` match, the imported save is **not** a format-version
mismatch — that theory is dead as an explanation for the game not listing it.

#### Diff surface

- File size 151,680 bytes; **live data ends around `0x1D28B`** (~119 KB), the rest
  is zero padding.
- Only **4,975 bytes differ (3.3%)** across **115 regions**.
- Non-zero bytes: L02 = 5,758, L04 = 9,019. The completed save carries ~3,261 more
  non-zero bytes, which is the expected signature of accumulated unlocks.

That is a small enough surface to map by hand, and it is the best available route
to the completion list, chapter and substory flags — all of which resisted
live-memory search.

**Anchoring strategy:** known live-memory structures should have counterparts in
the save. The ability bitfield (`0x0153020C`/`0x01530210` in RAM, 39 mapped bits)
is the best anchor — sparse in an early save, dense in a completed one.

#### Identified: weapon array at `0x007340`

8-byte records, big-endian:

```
[u16 ammo][u16 count][u16 itemId][u16 FFFF]
```

`itemId` indexes the same catalogue as `data/items.tsv`, so the existing item
table decodes save contents directly. `ammo = 0xFFFF` means infinite (melee).
Verified by decoding L04 and reading off real weapon names — Flesh Shredder
(674), Kagutsuchi (683), BB Gun (717), Satellite Laser (712), Golden Pistol
(664), Dragon Arm (689), Anti-Tank Missile Launcher (711). Slot id 1 is
`Locked Inventory Slot` and 0 is empty, matching the in-RAM convention.

#### Candidate flag regions (L02 early vs L04 completed)

169 x 32-byte windows gained 24+ set bits. The standouts:

| Region | Shape | Guess |
|---|---|---|
| `0x005080`-`0x0051A0` | solid `FFFFFFFF` runs, zero in L02 | bulk unlock flags |
| `0x016100`-`0x016140` | solid `FFFFFFFF` runs | bulk unlock flags |
| `0x01A5C0`-`0x01A640` | **irregular** bit patterns | partial completion - best completion-list candidate |
| `0x008700`-`0x008720` | mixed, partly set in L02 too | progressive flags |
| `0x018A00` | repeating `3ca3ca02 38e38e03` | packed counters or per-entry state |

The irregular regions are the interesting ones: solid `FFFFFFFF` means "all of
this category unlocked", while a partial pattern means individually-tracked
entries — which is what an Archipelago location pool needs.

#### The pipeline this opens

Apollo has both directions: **"Decrypt save game files"**
(`exec_cmd.c:1488`) and **"Import decrypted save files"** / `encryptSaveFile`
(`exec_cmd.c:1512`), which re-encrypts from `/dev_hdd0/tmp/apollo/<dir>/`. With
resigning after, that is a complete **read-modify-write save editing loop**.

Consequence: unlocking Premium Adventure may not require the imported save to
load at all. Find the clear flag by diffing, set it in the console's *own* save,
re-encrypt and resign. That sidesteps the entire import problem.

## First milestone: Akiyama, "2 hostesses maxed out"

Chosen 2026-08-23. Scope the first shippable AP world to **Akiyama only**, goal
condition **two hostesses raised to maximum**. All seven characters come later.

Why this goal fits Archipelago well:

- **It is pre-post-game.** Everything the project got blocked on — Premium
  Adventure, the outfit selector, Bob's challenges — sits behind beating the
  game. The hostess system does not.
- **It has a built-in item pool.** Catalogue ids 300-425 are all hostess
  content, and they *gate* the goal: a hostess cannot be maxed without the right
  outfits, accessories, drinks and gifts. That is exactly the dependency
  structure a randomizer wants, rather than an item pool bolted on beside it.
- **It has natural checks.** Per-hostess rank-ups are discrete, ordered and
  persistent.

Catalogue breakdown of the hostess block:

| Ids | Contents |
|---|---|
| 300-312 | drinks (White Champagne, Yamazaki 12, Beer, ...) |
| 313-324 | food (Fruit Platter, Chicken Basket, Chocolate, ...) |
| 355-361 | gifts (Italian Women's Suit, Italian Ring, Caviar Skin Bag, ...) |
| 362-384, 399-425 | outfits (dresses, Chinese Dress, Maid, Schoolgirl, ...) |
| 385-397 | accessories (Tiara, Cat Ears, necklaces, watches, rings) |

Note ids 308-312 (`Drink 11`-`Drink 15`) and 319-324 (`Food 6`-`Food 11`) are
placeholder names in the game's own table, matching the `Dummy`/`Temp` pattern
documented for weapons. Filter them out of any item pool.

### What still has to be found for this milestone

1. ~~The hostess roster~~ **Confirmed: exactly two.**

   | Hostess | Club |
   |---|---|
   | Erika Mizushima | Shine |
   | Yuna | Jewel |

   The same pair as Yakuza 4's Hostess Maker, reused here. Because there are
   only two, the goal "2 hostesses maxed out" is **100% of the hostess
   content**, not a subset — which makes the goal test simple and removes any
   need for the player to choose which hostesses count.

   Confirmed reachable in Part I Chapter 2, so the whole milestone is testable
   from an early save.
2. **Per-hostess progression state** — where rank/affection lives, in save and in
   RAM. Needed for both the checks and the goal test. Expect **two parallel
   structures**, one per hostess; finding one gives the other by symmetry.
3. **The "maxed" threshold** — what value counts as complete.
4. **Whether the hostess club is reachable in the current save** (Part I
   Chapter 2). If not, how early it opens.

Items 2 and 3 are what the save-diff loop is for: save, run one hostess session,
save again, decrypt both, diff. Hostess rank is persistent state, so it must
appear in `USER01`, and the diff surface for a single session should be tiny
compared to the 4,975 bytes separating a chapter-2 save from a 130-hour one.

### SOLVED: the goal condition is two key items

A controlled save diff (save, one hostess session, save) changed **51 bytes in
16 regions** out of 151,680 — versus 11,522 noisy addresses for the equivalent
RAM diff. The technique works.

#### The id-indexed key-item array

Key items live in a **flat array indexed by item id**, 8 bytes per entry, same
record layout as the RAM inventory:

```
offset = 0x00500C + (itemId * 8)        [u16 id][u16 pad][u32 quantity]
```

An owned item has `id == index` and `quantity >= 1`; an unowned one is all
zeroes. Unlike the RAM inventory this is **not** a packed list — every item has a
permanent home, so "does the player own X" is a single read at a computed
offset, with no scanning.

Validity: clean for **ids >= 550** (verified across the 130-hour save: every slot
is either empty or holds its own index). Below 550 the computed offsets collide
with other structures — the flag blocks at `0x005080`-`0x0051A0` among them — so
do not use the formula there. The top end runs to roughly id 1126 before it
abuts the weapon array at `0x007340`.

#### The goal test

Maxing a hostess grants her **Fancy Business Card**. Confirmed by comparing an
early save against the 130-hour completed one:

| Hostess | Plain card | Offset | Fancy card | Offset |
|---|---|---|---|---|
| Yuna | 1046 | `0x0070BC` | **1047** | `0x0070C4` |
| Erika | 1048 | `0x0070CC` | **1049** | `0x0070D4` |
| Saaya | 1050 | `0x0070DC` | **1051** | `0x0070E4` |

The completed save holds all six. The test save holds only 1046, granted by the
single session that was played.

So **"2 hostesses maxed out" reduces to two single-address reads** — no rank
counter, no threshold to discover, no per-hostess state machine. Whether the goal
should require Yuna + Erika specifically or any two of three depends on Saaya
(see below).

#### Saaya is a third hostess

Ids 1050/1051 exist and the 130-hour save owns both. The player reported only two
clubs reachable at Part I Chapter 2 — Erika at **Shine**, Yuna at **Jewel** — so
Saaya is either a later unlock or a third club not yet open. Resolve before
fixing the goal logic, since "2 of 2" and "2 of 3" are different worlds.

#### Still-unexplained bytes from the session diff

Candidates for hostess rank/affection and other per-session state:

| Offset | L06 -> L07 | Guess |
|---|---|---|
| `0x00203F` | `00` -> `01` | flag |
| `0x0052A3` | `00` -> `01` | flag |
| `0x005661`-`0x00568B` | float-shaped | player position/rotation |
| `0x008745` | `00` -> `40` | **drunkenness** (player reported being lightly drunk) |
| `0x008C03` | `06` -> `07` | counter |
| `0x0091CD`, `0x016189` | `eb1eae` -> `f33622` | same value twice; timestamp or RNG seed |
| `0x016424` | `00..00` -> `0800000000000006` | 8-byte record, value 6 |
| `0x018403` | `01` -> `02` | counter |
| `0x0184DC`, `0x01856C`, `0x0185B4` | zero -> set | position-shaped |

Confirmed in the same diff: **money is a u32 BE at `0x008B48`** (60000 -> 48450),
and play time at `0x08` behaved as predicted.

`0x008745` is worth chasing: the player suggested forced drunkenness as an
Archipelago trap item, and a single byte going `00` -> `40` on a save where they
were "lightly drunk" is a strong lead.

### SOLVED: the RAM key-item array

```
RAM address = 0x015342DC + (itemId * 8)
```

Found by sweeping the data segment for `1046` as u16 (the player had just been
granted Yuna's Business Card). Seven hits; the correct one was identified by
**fingerprinting** — computing the implied array base for each candidate and
checking whether *every* key item the save says the player owns (896, 917, 1046)
appears at the predicted offset. Only `0x0153638C` matched 3/3; all six others
matched 1/3, i.e. only the id that was searched for.

Verified live on console:

| Item | Address | Value |
|---|---|---|
| 1046 Yuna's Business Card | `0x0153638C` | `0416000000000001` (owned) |
| 1047 Yuna's Fancy | `0x01536394` | zeroes |
| 1048 Erika's Business Card | `0x0153639C` | zeroes |
| 1049 Erika's Fancy | `0x015363A4` | zeroes |

Same validity caveat as the save-side array: trustworthy for ids >= ~550. Note
`Inventory.Base` (`0x01534DE4`) falls inside the computed range at index ~353,
so the low ids are genuinely other structures, not key-item slots.

Implemented in `client/Ps3Mapi/KeyItems.cs`.

**Saaya belongs to Majima**, confirmed by the player — so an Akiyama-only world
has exactly two hostesses and the goal is 1047 AND 1049.

### Next: find the RAM mirror of the key-item array

The AP client must detect checks live, not from saves. The save is a
serialization of RAM structures, so an id-indexed key-item array almost
certainly exists in memory too. Cheapest route: the player now owns **Yuna's
Business Card (1046)**, so sweep RAM and search for `1046` as u16 — the hit with
an 8-byte record around it, at a stride matching neighbouring ids, is the array.

### The save is a verbatim RAM dump: `ram = saveOffset + 0x0152F2D0`

Four independent anchors confirm a single constant offset between a decrypted
`USER01` and the live data segment:

| Structure | Save offset | Computed RAM | Known RAM |
|---|---|---|---|
| Key-item array | `0x00500C` | `0x015342DC` | `0x015342DC` |
| Money | `0x008B48` | `0x01537E18` | `0x01537E18` |
| Ability bitfield | `0x000F3C` | `0x0153020C` | `0x0153020C` |

Plus five bytes from the session diff read live and matched their saved values
exactly. Implemented as `Addresses.SaveToRam` / `FromSave` / `ToSave`.

**This is the most useful result in the project.** The two techniques now
compose: save diffs give near-zero-noise discovery (51 changed bytes vs 11,522
noisy RAM addresses), and the bridge to a live, pokeable address is addition. No
searching required.

### SOLVED: hostess availability is a flag, not the business card

Tested empirically on console. Zeroing **Yuna's Business Card** (id 1046) did
**not** block requesting her — the card is a *receipt* the game issues, not a key
it checks. Item-based gating is the wrong lever.

The real gate is a **single byte**:

| Hostess | Club | Availability flag | Save offset |
|---|---|---|---|
| Erika Mizushima | Shine | **`0x0153128F`** | `0x001FBF` |
| Yuna | Jewel | **`0x0153130F`** | `0x00203F` |

Entries are **`0x80` apart**. Both verified live on console: after loading a save
taken right after Erika's intro, `0x0153128F` read `01` and `0x0153130F` read
`00`, matching the save exactly. Erika's flag was the only set byte in a
192-byte window, confirming a large sparse table. If the stride holds, Saaya's
(Majima's) is a candidate at `0x0153138F` or `0x0153120F` — untested.

Bisected from the two candidate flags in the session diff:

- Both cleared -> Yuna blocked, and the club reset to first-interaction state.
- `0x01534573` cleared, `0x0153130F` set -> Yuna **requestable**. Not the gate.
- `0x01534573` set, `0x0153130F` cleared -> Yuna **blocked**. Confirmed.

`0x01534573` is rewritten by the game during the first interaction, so it tracks
conversation progress rather than availability. Note it will fight a client that
tries to hold it at zero.

The 32 bytes surrounding `0x0153130F` are **entirely zero** — an isolated set
byte in a sparse region, which is the signature of a large flag table with most
entries unset. Erika's gate is very likely a nearby byte in the same table.
Finding it also yields the table's stride and layout, which would be a strong
lead for substory and completion flags.

Implemented in `client/Ps3Mapi/Hostesses.cs`.

#### The two hostesses are structurally parallel

Diffing a common baseline (`L06`) against a Yuna intro (`L07`) and an Erika
intro (`L08`) shows matched structures throughout, which is what made the
identification safe:

| Structure | Yuna | Erika | Delta |
|---|---|---|---|
| Availability flag | `0x203F` | `0x1FBF` | `-0x80` |
| Conversation progress | `0x52A3` | `0x52A1` | `-2` |
| Business card record | `0x70BC` (id 1046) | `0x70CC` (id 1048) | `+0x10` |
| Drunk-ish byte | `0x8745` | `0x8744` | `-1` |

The card offsets match the id-indexed formula exactly
(`0x500C + 1048*8 = 0x70CC`), a free re-confirmation of that array.

Six regions changed in **both** runs and so are session noise rather than
hostess state: `0x0A` (play time), `0x5A89`, `0x8B4A` (money), `0x8C03`,
`0x16424`, `0x18403`. Subtracting those is what reduced the search to a handful
of bytes.

Erika's run also touched `0x00257F` (`00` -> `08`) with no Yuna counterpart —
unexplained.

#### Known limitation of flag-gating

The flag blocks the *request*, which is the last step of the interaction. The
player still walks in, pays the cover charge, and sits through the intro cutscene
before hitting the wall. Gating this late inherits all the upstream cost.

Options, cheapest first:

1. **Refund and notify** — detect entry, restore the money, re-clear the flag,
   fire a `PS3 NOTIFY` toast. No new tooling. Cutscene still plays.
2. **Close the venue** — if a per-venue open/closed state exists, flipping it
   gives a stock in-game refusal at the door: no cutscene, no charge, no custom
   text, and it looks native. **Best value; not yet investigated.**
3. **Custom NPC dialogue** — requires hooking the dialogue system or patching
   text data, so an SPRX plugin, a PS3 toolchain, and PPC disassembly. A
   different scale of project; not justified before the world works.

### Karaoke: a single global leaderboard, not per-song high scores

Test: sang "Pure Love in Kamurocho", scored 740 (max 1000), saved before (`L08`)
and after (`L09`). 81 bytes changed in 14 regions.

**The board** lives at save `0x018A30` (RAM `0x01547D00`), ten 4-byte records:

```
[u8 a][u8 b][u16 score]      sorted descending
```

`L06` and `L08` — both taken before any singing — hold the *identical* ten
entries (970, 950, 930, 900, 880, 850, 820, 800, 760, 720). Those are **NPC
defaults** shipped with the game, confirmed by the player. The 740 was inserted
at rank 10, evicting the NPC entry `17 01 02D0`:

```
before:  16 03 02F8 (760)   17 01 02D0 (720)
after:   16 03 02F8 (760)   03 08 02E4 (740)   <- player
```

**Correction: a per-song table does exist.** The first scan missed it because it
only looked for *sorted descending* runs of byte-aligned u16 scores, and the
per-song table is neither sorted nor byte-aligned. See the section below.

The board itself found exactly one instance — karaoke keeps a single global top ten. The meaning of
the two leading bytes is unresolved; in the player's entry they are `03 08`, and
the 130-hour save contains many mixed entries (`04 00`, `00 09`, `08 03`, ...).
One of them is probably the song id.

### SOLVED: per-song karaoke high scores, bit-packed

```
address = 0x01547CD4 + (songId * 4)        save offset 0x00018A04 + songId*4
songs 0..10 (11 total); the leaderboard begins right after, at 0x01547D00
```

Each 4-byte record is **bit-packed**, big-endian:

| Bits | Field |
|---|---|
| 31-20 | **high score** (0-1000) |
| 19-8 | previous score |
| 7-0 | flags (observed 01-04; meaning unknown) |

Worked example: `0x2E42E401 >> 20 = 0x2E4 = 740`, and `0x2EE2EE01 >> 20 = 0x2EE
= 750`. Verified live on console for both songs the player sang.

Known song ids:

| Id | Song |
|---|---|
| `0x02` | Where Has Your Touch Gone? |
| `0x08` | Pure Love in Kamurocho |
| `0x09` | Raindrops |
| `0x0A` | GET to the Top! |

The other seven are unmapped. The 130-hour save has all eleven filled (high scores
880-970), so the table extent is certain.

Note the high score and previous score differ in the completed save (song `0x05`:
900/880, song `0x08`: 920/840), which is what identifies bits 31-20 as the
*best* rather than the most recent.

**Why the first scan missed it:** the search assumed byte-aligned `u16` values in
descending order. A 12-bit field starting at bit 20 is invisible to that, and a
flat id-indexed table has no ordering to detect. When a value is known to exist
but a search comes back empty, suspect the *encoding assumption* before
concluding the data is absent.

Implemented in `client/Ps3Mapi/Karaoke.cs`. `ReadAll` fetches all eleven records
in a single 44-byte read rather than eleven round trips, which matters on a
~1.3 MB/s link.

#### What this means for locations



A location like *"Pure Love in Kamurocho: 90+"* cannot simply read a stored
per-song best, because none exists. Two consequences:

- **Live detection is sufficient anyway.** Archipelago checks are one-way — once
  sent they stay sent — so the client only has to *observe* a qualifying score
  once. A later better run by another song evicting the entry does not un-send
  the check.
- **Retroactive detection is limited to what is still on the board.** On
  reconnect the client can scan the ten entries and award anything it finds, but
  a score pushed off the board is unrecoverable. Acceptable, given the client
  polls during play.

#### Four flag bytes also fired

| Save | RAM | Change |
|---|---|---|
| `0x001C9F` | `0x01530F6F` | `00` -> `08` |
| `0x001CBF` | `0x01530F8F` | `00` -> `02` |
| `0x001D3F` | `0x0153100F` | `00` -> `20` |
| `0x00349F` | `0x0153276F` | `00` -> `02` |

All are `0x1F mod 0x20` — the same 32-byte-stride sparse table that holds the
hostess availability flags (`0x001FBF` Erika, `0x00203F` Yuna). So that table is
general game state, not hostess-specific, and its entries hold **values**, not
just 0/1. These four are per-song-tracking candidates.

**Next experiment:** sing a *different* song and diff again. That resolves
whether the leading bytes of a board entry identify the song, and whether the
four flag bytes are per-song or just "karaoke happened".

## Prototype apworld

`world/yakuza_dead_souls/`, game name **"Yakuza: Dead Souls"**, id base
`8_960_000`. Generates against the Archipelago **source checkout** at
`D:\Dev_programs\Archipelago` — use that rather than the ProgramData install,
which downgrades some generation errors to warnings.

| | |
|---|---|
| Locations | 33 = 11 songs x 3 score tiers (800/850/900) |
| Items | `Erika's Business Card`, `Yuna's Business Card` (progression), `Submachine Gun Ammo` (filler) |
| Regions | one, `Kamurocho` - every karaoke check is reachable from the start |
| Option | `start_with_one_card` - precollect a random card so a run has something local to do |

The cards are AP items, not the in-game key items of the same name: receiving one
tells the client to set that hostess's availability flag
(`0x0153128F` / `0x0153130F`).

**Packaging:** `custom_worlds` loads **`.apworld` zip files**, not raw folders.
The zip contains one top-level directory named after the module. A raw directory
dropped in `custom_worlds` fails with a bare
`ModuleNotFoundError: No module named 'worlds.yakuza_dead_souls'`, which looks
like a code fault but is purely a packaging one. `build-apworld.ps1` does the
zip (and `-Deploy` copies it across) — PowerShell rather than Python, to keep
this repo's Python confined to the apworld itself.

**Song names are provisional.** Only ids `0x08` (Pure Love in Kamurocho) and
`0x0A` (GET to the Top!) are confirmed; the rest are `Karaoke Song NN`
placeholders. Location names go into the datapackage, so renaming them
invalidates seeds generated beforehand — confirm the real titles before any seed
is worth keeping.

## Prototype client

`client/ApClient` -> `ydsclient`, C#/.NET 10, referencing
**Archipelago.MultiClient.Net 6.7.1** and the `Ps3Mapi` library.

```
ydsclient --slot <name> [--ap <host>] [--port <n>] [--password <pw>] [--host <ps3 ip>]
```

Startup: resolve the PS3 pid via `Ps3Console.FindGameAsync`, connect PS3MAPI,
sanity-check the ELF header, then log in to Archipelago with
`ItemsHandlingFlags.AllItems`.

The loop (2 s tick):

1. `Karaoke.ReadAll` - one 44-byte read for all eleven songs - then send a check
   for every song/tier whose high score qualifies and has not been sent.
2. Apply newly received items: hostess cards set the availability flags, filler
   goes to the inventory.
3. `KeyItems.AkiyamaHostessesMaxed` -> `session.SetGoalAchieved()` once.

`AllLocationsChecked` is read at startup and seeded into the sent-set, so a
reconnect does not resend everything.

`EnforceGates()` runs once at connect and writes **both** availability flags from
what the server says was received — so a hostess the player has not been granted
is actively re-locked, rather than only being left alone.

### End-to-end test: it works

Full loop verified against a real seed, a local `MultiServer`, and the console.

Seed placed `Erika's Business Card` on *Karaoke Song 03: 850+* and
`Yuna's Business Card` on *Karaoke Song 04: 900+*. Poking those two songs'
scores to 860 and 910 produced:

```
check: song 0x03 @ 800+     check: song 0x04 @ 800+
check: song 0x03 @ 850+     check: song 0x04 @ 850+
                            check: song 0x04 @ 900+
received: Erika's Business Card     received: Submachine Gun Ammo  x3
received: Yuna's Business Card
```

Verified on the console afterwards: both hostess flags set to `01`, and three
inventory slots holding id 29 at quantity 200. **Quantity works in the player
inventory too** — the game shows them as separate slots because it will not
merge stacks, but the count field is honoured.

#### Bug found and fixed: filler duplicated on every reconnect

The server resends the entire received-items list on connect, and
`_itemsApplied` was in-memory only, so each client start re-granted everything.
Flags are idempotent so the cards were harmless, but ammo accumulated 3 slots
per restart — confirmed by restarting against the same server and watching the
count go 3 -> 6.

Fixed by persisting the applied count to
`apstate/<seed>_<slot>.txt` next to the executable, written after **each** item
rather than once per batch, so a crash mid-batch cannot lose or repeat one.
Keying on the seed means a new multiworld starts from zero.

Locations never needed this: `session.Locations.AllLocationsChecked` is read at
startup and the server is authoritative. Only item *application* is a local
side effect, and only local state can track it.

### Crash: PASV data connections get refused under load

The client died with an unhandled `SocketException (10061)` while the player was
starting a karaoke song for the second time:

```
No connection could be made because the target machine actively refused it.
  at Ps3MapiClient.OpenDataConnection()
  at KeyItems.Has(...)  <- CheckGoal, every tick
```

**Cause.** PS3MAPI is FTP-shaped: every `MEMORY GET` opens a *fresh PASV data
connection*. The console refuses one now and then when it is busy — a heavy
scene, or several reads in quick succession. The old poll made 3+ data
connections every 2 s (one for the karaoke table, two for the goal check).

Three fixes:

1. **The loop no longer dies.** It caught only `Ps3Exception`, so a raw
   `SocketException` escaped and killed the process. It now catches everything
   except cancellation, logs, and retries next tick — with repeat-suppression so
   a disconnected console does not spam the console. A transport hiccup must
   never end a multiworld session.
2. **`OpenDataConnection` retries** (3 attempts, 150/300 ms backoff) and wraps
   socket failures in `Ps3Exception` so callers see one exception type instead of
   raw `SocketException` / `AggregateException`.
3. **The goal check is one read instead of two.** Both fancy-card records sit 16
   bytes apart, so `AkiyamaHostessesMaxed` reads a single 24-byte window and
   decodes both. Halves the per-tick connections.

**Design note for anything added later:** on this transport, *reads are not
free — connections are*. Prefer one wide read over several narrow ones even when
the extra bytes are wasted; 44 bytes in one connection beats 8 bytes in three.

### Multiworld messages on the TV

Every Archipelago log message becomes a PS3 toast, so the player sees
"`Uncle_Kaz sent Comedy Skill to shishi-sims (GET to the Top!: 850+)`" on screen
without alt-tabbing.

**webMAN is the preferred channel, deliberately.** PS3MAPI already requires
webMAN, so notifications cost the player no extra setup — CCAPI was removed from
the test console for exactly that reason. webMAN also carries sound (`snd=5`, the
trophy chime); its only downside is the plain info icon, and CCAPI's trophy icon
is not worth a second install. CCAPI remains a fallback if webMAN's HTTP is
somehow unreachable.

| Channel | Port | Icon | Sound |
|---|---|---|---|
| **webMAN** `notify.ps3mapi` | 80 | info only | yes |
| CCAPI `/ccapi/notify` | 6333 | trophy | no |

Messages cap at **199 characters** and are truncated with an ellipsis.

#### Why the toasts are queued rather than sent inline

`MessageLog.OnMessageReceived` fires on Archipelago's **receive thread**. A toast
is an HTTP GET with an 8 s timeout, so sending one inline stalls that thread and
the client stops reading its own socket. Messages are queued and drained from the
poll loop instead, at most 3 per tick with the queue capped at 40, so a busy
multiworld cannot bury the screen or exhaust memory. The queue is cleared once at
connect so the server's replayed backlog does not produce a wall of toasts.

**Note this is a different hazard from the one that would apply on the PS3MAPI
control socket.** If toasts went through `PS3 NOTIFY` over TCP 7887, a call from
another thread would inject a command mid `PASV` -> `MEMORY GET` -> `226`
sequence and desync which response belongs to which request — genuine protocol
corruption. `Notifier` uses HTTP on a separate connection, so that specific
failure cannot happen; the reason to queue is thread-blocking, not corruption.

A single static `HttpClient` is reused for the process. One per toast exhausts
sockets once messages arrive in bulk.

### Abilities as locations, benefits as items

All 39 of Akiyama's abilities are now in the world: **buying one is the check,
the ability itself is the item.**

| | |
|---|---|
| Locations | `Ability: <name>`, ids `BASE_ID + 1000 + index` |
| Items | `<name>`, `useful` — nothing in the logic requires one |
| Filler | `Submachine Gun Ammo`, `Soul Points` (+5 per item, saturating at 255) |

Pool is now **72 locations / 43 items** (33 karaoke + 39 abilities).

#### The bit layout

Two big-endian u32 words, and `data/ability_bits.tsv` is the source of truth for
names, addresses, bits and ordering:

```
0x0153020C   bits 2-20, 22-31
0x01530210   bits 0-9
```

`Abilities.cs` reads them as an 8-byte window covering both, so one read gets all
39. Verified live: `0x01530210 = 0x00000002` decoded to exactly `Head Tracking`,
which the TSV lists at bit 1.

#### `SyncAbilitiesAsync` enforces rather than applies

Each tick it compares the bitfield against the set of abilities Archipelago has
sent:

- **set but not granted** — the player just bought it. Send the check, then
  **clear the bit**. They paid, they got the check, but the ability does not
  work until the item arrives.
- **granted but not set** — turn it on.
- Write back only when something differed.

This is the same idempotent-enforcement shape as `EnforceGates`, and it is why
abilities are deliberately **not** handled in `Apply` — that runs once per item,
which cannot re-assert state the game or a reload changed.

**Known rough edge:** after the bit is cleared the game shows the ability as
unpurchased, so the player can buy it again and waste soul points. The check is
already sent so there is no exploit, just wasted currency. Refunding would need
per-ability costs, which are not mapped.

#### One source of truth for names

`build-apworld.ps1` copies `data/ability_bits.tsv` into the world folder before
zipping, and `Abilities.py` reads it back with `pkgutil.get_data` (which works
through zipimport). So the apworld and the client derive ability names *and*
their id ordering from the same file. Reordering that file breaks existing seeds.

### Known fragility: the ids are duplicated

Location and item ids exist in two places with nothing enforcing agreement:

| | |
|---|---|
| `world/yakuza_dead_souls/Locations.py` | `BASE_ID + song*10 + tierIndex` |
| `client/ApClient/ApIds.cs` | `BaseId + songId*MaxTiersPerSong + tierIndex` |

Same for the three item ids and for `SCORE_TIERS` / `Karaoke.ScoreTiers`. Change
one side and the client silently desyncs from generated seeds — checks land on
the wrong locations rather than erroring. Generating the C# side from the `.py`
(or both from `data/`) is the obvious fix and is not done.

### The storage box: 133 slots, and it stacks

Bounded from the 130-hour save (storage save offset `0x005BD4`, RAM
`0x01534EA4`):

- **133 slots occupied contiguously**, no gaps, ending at RAM `0x015352CC`.
- The key-item array's valid range does not begin until `0x0153540C`, leaving
  room for ~40 more slots the game has never been seen to use. `StorageSlots`
  is set to the proven 133 rather than the structurally-possible 173.
- Same 8-byte record as the player inventory: `[u16 id][u16 pad][u32 quantity]`.

**Storage stacks; the player inventory does not.** The completed save holds
`Submachine Gun Ammo` x2591, `Rifle Ammo` x2373, `Gatling Gun Ammo` x1215 — all
in single slots. That settles a question left open since the Tauriner
experiments, where three of the same item took three separate *inventory* slots.
So the quantity field is real, it is just the inventory that refuses to merge.

Added to `Inventory`:

| Method | Behaviour |
|---|---|
| `FindStorageSlot(game, id)` | an existing stack of that item, else the first empty slot |
| `GrantToStorage(game, id, qty)` | adds to an existing stack, or starts a new one |
| `GrantAnywhere(game, id, qty)` | player inventory first, storage as overflow |

The AP client uses `GrantAnywhere` for filler, so a full 24-slot inventory sends
ammo to the box instead of discarding it.

### Lead: the Completion List is a location source

This build has a **completion list** with rewards collected from Bob B once he
texts you. Yakuza completion lists enumerate dozens to hundreds of discrete
tasks, each individually tracked — which is exactly the shape an Archipelago
location pool needs, and far richer than the 39 ability purchases that are
currently the only location candidates. Worth finding the completion bitfield
even before story-progress detection is solved.

### The storage box at `0x01534EA4`

Found by putting two items into it and searching for the distinctive id 629
(Cutie Girl Figure). It uses the **same 8-byte record format** as the player
inventory and sits **immediately after** it:

```
0x01534DE4  player inventory, 24 slots
0x01534EA4  storage box          (0x01534DE4 + 24*8)
```

Extent not yet bounded. Useful for two reasons: it is somewhere to put an
Archipelago item when the player's 24 slots are full, and its contents are
themselves potential checks.

### Ability bits persist in the save file

An `FFFFFFFF` written to `0x01530210` during testing survived a save and
reload, while `0x0153020C` came back as `0` from the older save state. So the
ability bitfield is **saved game state, not runtime-only** — granting an
ability sticks and does not need re-applying on load.

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

Mapped by clearing the field, granting 255 soul points, and buying abilities
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
