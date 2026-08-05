# Testing

## FUSE Z80 conformance suite

* Source: `github.com/gasman/jsspeccy2/test/tests.in` and `tests.expected`
  (a mirror of the FUSE 1.x Z80 test bank).
* 1335 test cases covering every documented opcode, every prefixed variant
  (CB, DD, ED, FD, DDCB, FDCB), and — critically — every undocumented flag
  and side-effect combination that a real Zilog Z80 exhibits.
* Data files ship inside the test project at
  `tests/WarajevoNext.Cpu.Tests/FuseTests/` with `CopyToOutputDirectory=PreserveNewest`.
* One `[Fact] FuseSuite()` iterates every case, drives a fresh CPU + a
  `FlatMemory` + `NullIo`, compares AF, BC, DE, HL, all shadows, IX, IY,
  SP, PC, R (full byte including bit 7), IM, T-states, and every
  memory delta.

```bash
dotnet test tests/WarajevoNext.Cpu.Tests
# → FUSE Z80: 1335/1335 passed
```

## Machine smoke tests

`tests/WarajevoNext.Machine.Tests/MachineSmokeTests.cs` covers:

* 48K memory: ROM writes ignored, three RAM regions round-trip.
* 128K memory: bank switching via port 0x7FFD.
* 128K paging lock: bit 5 latches; further writes ignored.
* Keyboard: half-row select returns active-low bits correctly.
* Machine frame budget: `RunFrame()` advances at least 69,888 T-states.
* ULA render fills the border with the currently-selected palette entry.

```bash
dotnet test tests/WarajevoNext.Machine.Tests
# → 6 tests passing
```

## App self-test (headless / CI)

```bash
dotnet run --project src/WarajevoNext.App -- --selftest 500
# → selftest ok: frames=500 tstates=34944000 pc=0x????
```

Runs 500 frames against a stub ROM. `34944000` = `500 × 69888` (exact),
which independently corroborates the frame-timing budget above.
