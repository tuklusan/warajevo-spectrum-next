# Warajevo Spectrum Next

A modern **ZX Spectrum 48K / 128K emulator** written in C# on **.NET 10** with
an **Avalonia** cross-platform UI. Warajevo Next is a from-scratch port of the
DOS-era **Warajevo 2.50** originally written by Željko Jurić and Samir Ribić
in Sarajevo (~88,000 lines of Turbo Pascal + x86 assembly).

The port lives in [`warajevo-next/`](warajevo-next/); everything below is a
quick tour. For depth, see the docs in that directory.

## Status

| Layer     | State                                                          |
|-----------|----------------------------------------------------------------|
| Z80 CPU   | Complete. Passes **1335 / 1335** FUSE Z80 conformance cases    |
| Machine   | 48K + 128K memory paging, ULA, keyboard, tape, AY-3-8912 stub  |
| Snapshots | `.SNA` (48K), `.Z80` v1/v2/v3 (48K + 128K)                     |
| Tape      | `.TAP` pulse-timed playback                                    |
| UI        | Avalonia main window: menus, screen bitmap, keyboard, dialogs  |
| CI        | Every push scanned; branch protected by a required status check |

## Build and run

```bash
cd warajevo-next
dotnet --version   # requires .NET SDK 10.0.302+ (pinned in global.json)
dotnet build
dotnet test
dotnet run --project src/WarajevoNext.App                     # GUI
dotnet run --project src/WarajevoNext.App -- --selftest 500   # headless
```

**ROMs are not bundled.** Supply your own `48.rom` (16 KB) via one of:

1. `$WARAJEVO_NEXT_ROMS` (directory path)
2. `<AppBase>/roms/`
3. `./roms/` relative to the current working directory
4. **File → Load ROM…** in the GUI

## Keyboard mapping

| Host key             | Spectrum key   |
|----------------------|----------------|
| A-Z, 0-9             | same           |
| Enter / Space        | ENTER / SPACE  |
| Left / Right Shift   | CAPS SHIFT     |
| Left / Right Ctrl    | SYMBOL SHIFT   |

## Layout

```text
.
├── warajevo-next/          The .NET 10 + Avalonia port (subject of this repo)
│   ├── WarajevoNext.slnx
│   ├── src/
│   │   ├── WarajevoNext.Cpu/       Z80 core + IMemoryBus / IIoBus
│   │   ├── WarajevoNext.Machine/   memory, ULA, keyboard, tape, AY, snapshots
│   │   └── WarajevoNext.App/       Avalonia GUI + --selftest entry
│   ├── tests/
│   │   ├── WarajevoNext.Cpu.Tests/     FUSE Z80 conformance suite
│   │   └── WarajevoNext.Machine.Tests/ smoke tests
│   ├── README.md, ARCHITECTURE.md, TESTING.md, BUILD_NOTES.md, LICENSE
│   └── global.json, Directory.Build.props, Directory.Packages.props
├── .github/workflows/no-banned-word.yml   Push-time content scan (required)
├── .githooks/pre-push                     Mirror scan run client-side
├── src/, upstream/                        Full archival of DOS Warajevo 2.50 source
├── docs/                                  Historical documentation for the original
├── roms/, tapes/                          Preserved originals kept alongside
└── (see the "About the archived original" section below)
```

## Docs

* [`warajevo-next/README.md`](warajevo-next/README.md) — project-level README
* [`warajevo-next/ARCHITECTURE.md`](warajevo-next/ARCHITECTURE.md) — three-layer design, per-frame data flow, port dispatch
* [`warajevo-next/TESTING.md`](warajevo-next/TESTING.md) — FUSE + Machine + selftest
* [`warajevo-next/BUILD_NOTES.md`](warajevo-next/BUILD_NOTES.md) — SDK pin, NuGet.config, unsafe blocks

## Credits

* **Željko Jurić** and **Samir Ribić** — original Warajevo (Sarajevo, 1997-2001+).
* **Supratim Sanyal** (SANYALnet Labs) — the .NET 10 / Avalonia port.
* FUSE Z80 conformance suite via the `jsspeccy2` mirror.

## Licence

GNU **GPL v3-or-later**, matching the original Warajevo. See
[`warajevo-next/LICENSE`](warajevo-next/LICENSE).

---

## About the archived original

The working root of this repository is [`warajevo-next/`](warajevo-next/) —
that is the subject of the repo. Every other top-level directory
(`src/`, `upstream/`, `docs/`, `roms/`, `tapes/`) is a **complete archival
of the original DOS Warajevo 2.50** (Turbo Pascal + MASM/TASM, four upstream
archives: `Warajevo.zip`, `Specsim.zip`, `Timex.zip`, `Compiler.zip`), kept
in place for historical continuity so the port and its ancestry can be
read side-by-side in a single clone.

The same archival — untouched — is also maintained standalone at the
upstream archive repository:

**<https://github.com/tuklusan/warajevo-spectrum-2.50>**

Refer to the upstream archive (or to the top-level directories here) for
the untouched original source and its historical build instructions
(Turbo Pascal 6.0 or 7.0, MASM for DOS, TASM for DOS).
