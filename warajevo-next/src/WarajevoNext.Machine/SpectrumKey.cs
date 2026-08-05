// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ============================================================================
namespace WarajevoNext.Machine;

/// <summary>
/// The 40 keys of the Spectrum keyboard, encoded by half-row and bit.
/// The high byte of port 0xFE selects the half-row (active low), the low
/// five bits (0..4) return each key's pressed state (active low).
/// </summary>
public enum SpectrumKey
{
    // Row 0: CAPS-SHIFT, Z, X, C, V (bit 0..4), selected by A8=0
    CapsShift, Z, X, C, V,
    // Row 1: A, S, D, F, G, selected by A9=0
    A, S, D, F, G,
    // Row 2: Q, W, E, R, T
    Q, W, E, R, T,
    // Row 3: 1, 2, 3, 4, 5
    D1, D2, D3, D4, D5,
    // Row 4: 0, 9, 8, 7, 6
    D0, D9, D8, D7, D6,
    // Row 5: P, O, I, U, Y
    P, O, I, U, Y,
    // Row 6: ENTER, L, K, J, H
    Enter, L, K, J, H,
    // Row 7: SPACE, SYM-SHIFT, M, N, B
    Space, SymShift, M, N, B
}
