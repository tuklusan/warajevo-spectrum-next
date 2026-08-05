// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Original Warajevo (c) Zeljko Juric and Samir Ribic (GPL, Feb 2006).
// This C# port is a derivative work released under GNU GPL v3-or-later.
// ============================================================================
namespace WarajevoNext.Cpu;

/// <summary>
/// The I/O bus the Z80 core reads and writes through. On the Spectrum this
/// covers the ULA (0xFE), 128K paging (0x7FFD), AY (0xFFFD/0xBFFD), and so on.
/// </summary>
public interface IIoBus
{
    byte In(ushort port);
    void Out(ushort port, byte value);
}
