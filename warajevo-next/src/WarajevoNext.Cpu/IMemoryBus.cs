// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Original Warajevo (c) Zeljko Juric and Samir Ribic (GPL, Feb 2006).
// This C# port is a derivative work released under GNU GPL v3-or-later.
// ============================================================================
namespace WarajevoNext.Cpu;

/// <summary>
/// The memory bus the Z80 core reads and writes through. The machine owns the
/// address decode (ROM vs RAM, paging) and any contention behaviour.
/// </summary>
public interface IMemoryBus
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
}
