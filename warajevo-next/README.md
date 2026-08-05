# Warajevo Next

A modern **.NET 10 + Avalonia** port of the DOS-era **Warajevo 2.50** ZX Spectrum
emulator originally written in Sarajevo by **Zeljko Juric** and **Samir Ribic**
(~88,000 lines of Turbo Pascal + x86 assembly).

Warajevo Next targets **ZX Spectrum 48K and 128K**, runs on Windows / Linux /
macOS, and inherits the original project's **GNU GPL v3-or-later** licence.

## Status

| Layer   | State                                                           |
|---------|-----------------------------------------------------------------|
| Z80 CPU | Complete. Passes **1335 / 1335** FUSE Z80 test cases            |
| Machine | 48K + 128K memory paging, ULA, keyboard, tape, AY-3-8912 stub   |
| Snapshots | `.SNA` (48K), `.Z80` v1/v2/v3 (48K + 128K)                    |
| Tape    | `.TAP` pulse-timed playback                                     |
| UI      | Avalonia main window: menus, screen bitmap, keyboard, dialogs   |

CPU tests: `dotnet test tests/WarajevoNext.Cpu.Tests` → 1 fact, 1335 sub-cases.
Machine tests: `dotnet test tests/WarajevoNext.Machine.Tests` → 6 facts.

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
