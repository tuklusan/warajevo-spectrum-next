// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// IContentionModel — pluggable bus-contention hook consulted by the Z80 core
// before each memory or I/O access. The model returns the number of *extra*
// T-states to charge for this access, on top of the natural cost of the bus
// cycle. Returning 0 makes the access uncontended (the default when no model
// is installed at all).
//
// The hooks are:
//   ContendMemory(tstate, address) — called before every memory M-cycle
//     (opcode fetch, operand fetch, data read, data write).
//   ContendIo(tstate, port)         — called before every I/O cycle.
//
// The `tstate` argument is the CPU's absolute running T-state count at the
// moment the bus cycle begins. Contention models that are frame-locked (like
// the ULA on a real Spectrum) must reduce it modulo the frame length.
//
// The model is opt-in: FUSE Z80 conformance tests use FlatMemory + NullIo
// with no contention, and leave Z80.Contention null so no extra Tick fires.
// ============================================================================
namespace WarajevoNext.Cpu;

public interface IContentionModel
{
    int ContendMemory(int tstate, ushort address);
    int ContendIo(int tstate, ushort port);
}
