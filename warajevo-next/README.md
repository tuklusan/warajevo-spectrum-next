# Warajevo Next

A modern **.NET 10 + Avalonia** port of the DOS-era **Warajevo 2.50** ZX Spectrum
emulator originally written in Sarajevo by **Zeljko Juric** and **Samir Ribic**
(~88,000 lines of Turbo Pascal + x86 assembly).

Warajevo Next targets **ZX Spectrum 48K and 128K**, runs on Windows / Linux /
macOS, and inherits the original project's **GNU GPL v3-or-later** licence.

## Status

| Layer   | State                                                                                                                        |
|---------|------------------------------------------------------------------------------------------------------------------------------|
| Z80 CPU | Complete. Passes **1335 / 1335** FUSE Z80 test cases                                                                         |
| Machine | 48K + 128K memory paging, ULA, keyboard, tape, AY-3-8912 stub                                                                |
| Snapshots | `.SNA` (48K), `.Z80` v1/v2/v3 (48K + 128K)                                                                                 |
| Tape    | `.TAP` playback + **0x0556 fast-load trap** (LD-BYTES) - loads whole blocks straight from the TAP into RAM, matches ROM CF / IX / DE contract, XOR-checksum validated |
| UI      | Avalonia main window: menus, screen bitmap, keyboard, dialogs                                                                |
| Control | Optional **TCP control server** (`WARAJEVO_NEXT_CTRL_PORT`) for scripted key injection and PNG snapshots - see below         |

CPU tests: `dotnet test tests/WarajevoNext.Cpu.Tests` → 1 fact, 1335 sub-cases.
Machine tests: `dotnet test tests/WarajevoNext.Machine.Tests` → 6+ facts (includes a fast-load-trap test).

## Tape compatibility snapshot (2026-08-05)

Batch-tested every `.TAP` in `test-media/tap/` via the fast-load trap
(auto-typed LOAD "" from `WARAJEVO_NEXT_AUTOLOAD_FRAME`, then driven
through titles via the TCP control server). 13 of 16 reach a visible
title, menu or actual gameplay screen. Highlights:

* **Fairlight** (The Edge, 1985) - Bo Jangeborg's isometric engine playing
* **Arkanoid** (Imagine, 1987) - title -> attract cycle -> intro scroll
* **Skool Daze** (Microsphere, 1985) - classroom rendered, HUD live
* **Wheelie** (Microsphere, 1983) - full menu screen
* **Abu Simbel Profanation** (Dinamic, 1985) - control-select
* **DreamWalker 48K** / **MultiDude** - tape loads cleanly on both; both
  hand off to the **Nirvana engine** splash (rendering that engine's
  per-row attribute output is a ULA-side task, not a tape-side one -
  the *loader path* handles Nirvana games identically to any other)

Full table + screenshots in the [repo-root README](../README.md#tape-compatibility-as-of-2026-08-05)
and `docs/screenshots/`. Three known-failure classes documented
there too - custom EAR-polling loaders (Sentinel, Aquaplane), Elite
Rapid Loader (Paperboy) and per-title black-screen (yazziejr,
Shock Megademo) - each with the root cause identified.

## Build

```bash
dotnet --version   # requires .NET SDK 10.0.302 or newer (pinned in global.json)
dotnet build
dotnet test
dotnet run --project src/WarajevoNext.App -- --selftest 500
```

## Run

The GUI starts with `dotnet run --project src/WarajevoNext.App`.

**ROMs are not bundled.** Warajevo Next expects a copy of the ZX Spectrum ROM
(e.g. `48.rom`, 16 KB) supplied by the user. It looks for `48.rom` in:

1. `$WARAJEVO_NEXT_ROMS` (environment variable, directory path)
2. `<AppBase>/roms/`
3. `./roms/` relative to the current directory

Or use **File → Load ROM…** from the menu.

## Running scripted / headless (TCP control server)

Set `WARAJEVO_NEXT_CTRL_PORT` (recommend `10001`) before launching. The
app opens a plain line-oriented TCP server on that port. Any telnet or
netcat client can drive it:

```
$ telnet host 10001
WarajevoNext CTRL v1  -  type HELP for commands
STATUS
OK model=FortyEight pc=0x02AB sp=0xFFFB af=0x0054 tape=loaded frames=1167
KEY SPACE
OK
KEY SPACE,1,SPACE           # multiple presses in one command
OK
SNAP C:/tmp/screen.png
OK snap C:/tmp/screen.png (9913 bytes)
QUIT
```

Grammar:

| Verb                    | Effect                                                                |
|-------------------------|-----------------------------------------------------------------------|
| `KEY <tok,tok,...>`     | Press each token as one chord (200 ms hold + 400 ms release)          |
| `HOLD <chord>`          | Press-and-hold with no release                                        |
| `RELEASE <chord>`       | Release a held chord                                                  |
| `SNAP <path>`           | Dump the ULA framebuffer to a PNG                                     |
| `PC?`                   | Report `Cpu.PC` as hex                                                |
| `STATUS`                | One-line machine status                                               |
| `QUIT` / `HELP`         | as expected                                                           |

Tokens match `SpectrumKey` enum names case-insensitively; chord parts
join with `+` (`SS+P` for Symbol-Shift+P, `CS+9` for Caps-Shift+9);
bare digits get D-prefixed (`0..9` -> `D0..D9`). All Spectrum-side
mutation is marshalled to the Avalonia UI thread via `Dispatcher`, so
the socket thread never touches the CPU / ULA / Keyboard directly.

Also useful for hands-off startup:

| Env                                | Effect                                                       |
|------------------------------------|--------------------------------------------------------------|
| `WARAJEVO_NEXT_AUTOLOAD_FRAME=200` | At frame 200, auto-type `LOAD ""` and Enter                  |
| `WARAJEVO_NEXT_STARTKEYS_FRAME=N`  | At frame N, type WARAJEVO_NEXT_STARTKEYS                     |
| `WARAJEVO_NEXT_STARTKEYS=SPACE,1`  | Comma-separated key list to type after tape load             |
| `WARAJEVO_NEXT_SNAP_DIR=<dir>`     | Dump periodic PNGs of the ULA framebuffer                    |
| `WARAJEVO_NEXT_SNAP_EVERY=50`      | Frame stride between snapshots                               |

## Keyboard mapping

| Host key             | Spectrum key   |
|----------------------|----------------|
| A-Z, 0-9             | same           |
| Enter / Space        | ENTER / SPACE  |
| Left / Right Shift   | CAPS SHIFT     |
| Left / Right Ctrl    | SYMBOL SHIFT   |

## Layout

```
WarajevoNext/
├── WarajevoNext.slnx
├── global.json
├── Directory.Build.props
├── Directory.Packages.props
├── src/
│   ├── WarajevoNext.Cpu/       Z80 core + IMemoryBus/IIoBus abstractions
│   ├── WarajevoNext.Machine/   memory, ULA, keyboard, tape, AY, snapshots
│   └── WarajevoNext.App/       Avalonia GUI + --selftest entry
└── tests/
    ├── WarajevoNext.Cpu.Tests/     FUSE Z80 conformance suite
    └── WarajevoNext.Machine.Tests/ smoke tests
```

## Credits

* **Zeljko Juric** and **Samir Ribic** — original Warajevo (1997-2001+, Sarajevo).
* **Supratim Sanyal** (SANYALnet Labs) — the .NET 10 / Avalonia port.
* FUSE Z80 conformance suite via the `jsspeccy2` mirror.

## Licence

GPL v3-or-later, matching the original Warajevo. See `LICENSE`.
