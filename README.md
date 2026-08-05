# Warajevo Spectrum Emulator 2.50 Source Archive

This repository is intended to preserve and study the available source code for
Warajevo 2.50, the DOS-based ZX Spectrum emulator written by Željko Jurić and
Samir Ribić in Sarajevo.

## Status

The Warajevo authors state on the official project site that Warajevo was
released as open source under the GNU GPL in February 2006. The site does not
identify a GPL version, and the source archives must be checked for any more
specific licence text or source-file headers before this repository declares a
particular GPL version.

This repository must not include Sinclair ZX Spectrum ROM images unless their
redistribution rights are separately established.

## Expected upstream source archives

The official source release is split into four archives:

- `Warajevo.zip` — user environment
- `Specsim.zip` — Spectrum 48K/128K emulator kernel
- `Timex.zip` — Timex Sinclair 2068 kernel
- `Compiler.zip` — ZX snapshot compiler

Official source page:

`https://worldofspectrum.net/warajevo/Download.html`

## Warajevo Next — the .NET 10 + Avalonia port

A modern port of Warajevo lives under [`warajevo-next/`](warajevo-next/). It
targets the Spectrum 48K and 128K, is written in C# on .NET 10, and uses
Avalonia for its cross-platform UI. The Z80 core passes **1335 / 1335** cases
from the FUSE Z80 conformance suite. See
[`warajevo-next/README.md`](warajevo-next/README.md) and
[`warajevo-next/ARCHITECTURE.md`](warajevo-next/ARCHITECTURE.md) for details.

## Proposed repository layout

```text
src/
  environment/
  spectrum-kernel/
  timex-kernel/
  zx-compiler/
upstream/
  Warajevo.zip
  Specsim.zip
  Timex.zip
  Compiler.zip
docs/
  UPSTREAM.md
  LICENSING.md
```

The untouched ZIP archives should be retained under `upstream/`, while their
contents should be extracted into the matching `src/` directories.

## Historical build requirements

According to the official project page, the source requires:

- Turbo Pascal 6.0 or 7.0, or Delphi 1 with MS-DOS units
- MASM for DOS
- TASM for DOS

The initial archival import should not modify the original source. Build-system
modernisation should be done in later commits so the provenance remains clear.

## Attribution

Original authors:

- Željko Jurić
- Samir Ribić

This repository is an archival/community import and is not presented as an
official repository of the original authors.
