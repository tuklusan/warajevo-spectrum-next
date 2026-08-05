# Architecture

Three-layer design; each layer is a plain .NET class library with no
GUI dependency beneath it:

```
+------------------------+
|  WarajevoNext.App      |  Avalonia UI, screen bitmap, file dialogs, timer loop
|  (WinExe, GUI)         |
+-----------+------------+
            |  references
            v
+------------------------+
|  WarajevoNext.Machine  |  SpectrumMemory  Ula  Keyboard  TapeDevice  Ay8912
|  (library)             |  SpectrumMachine (composed; implements IIoBus)
|                        |  SnapshotLoader (SNA + Z80 v1/v2/v3)
+-----------+------------+
            |  references
            v
+------------------------+
|  WarajevoNext.Cpu      |  Z80 interpreter
|  (library)             |  IMemoryBus / IIoBus abstractions
+------------------------+
```

## Data flow, per frame

1. `SpectrumMachine.RunFrame()`
   * `Cpu.RequestInterrupt()` (raises INT — the ULA vertical blank)
   * loop `Cpu.Step()` until `TStates >= target`
2. `Ula.RenderFrame(uint[])` walks bank-5 (or bank-7 shadow) VRAM and
   attributes and paints a 320×256 ARGB buffer including border.
3. The App copies that buffer into an Avalonia `WriteableBitmap` and
   invalidates the `Image` control.

Timing per frame:
* 48K: 69,888 T-states (3.5 MHz / 50.08 Hz).
* 128K: 70,908 T-states (3.5469 MHz / 50.01 Hz).

## Contract between layers

The CPU knows about **two ports of the outside world**:
`IMemoryBus.Read/Write(ushort)` and `IIoBus.In/Out(ushort)`. It charges
T-states via an internal `Tick(n)` on every memory / I/O cycle. Anything
below or above those two interfaces is invisible to it — this is what let
the FUSE conformance suite drive the CPU with an in-memory `FlatMemory` +
`NullIo` and reach 100 % pass in a hosted test.

`SpectrumMachine` implements `IIoBus` and dispatches ports:

| Port bits                          | Handler                       |
|------------------------------------|-------------------------------|
| A0 == 0                            | `Ula.In` / `Ula.Out`          |
| 0x7FFD (A15=0, A1=0)               | `SpectrumMemory.Write7FFD`    |
| 0xFFFD (A15=1, A14=1, A1=0) write  | `Ay8912.SelectRegister`       |
| 0xFFFD read                        | `Ay8912.ReadData`             |
| 0xBFFD (A15=1, A14=0, A1=0) write  | `Ay8912.WriteData`            |
| 0x1F (Kempston)                    | returns 0 (placeholder)       |

## Where things live

* Full opcode set + all documented + undocumented flag behaviour is in a
  single `Z80.cs`. The 1335 FUSE cases pin the undocumented pieces (DDCB /
  FDCB, IXH/IXL halves, undocumented SLL, MEMPTR/WZ, block-instruction
  flags, R-bit-7 preservation).
* Snapshot loaders write directly through `SpectrumMemory` so all the
  page-decode logic stays in one place.
* The Avalonia UI is deliberately thin: a `DispatcherTimer` at 20 ms drives
  `RunFrame()` and `RenderFrame()`; no background threads.
