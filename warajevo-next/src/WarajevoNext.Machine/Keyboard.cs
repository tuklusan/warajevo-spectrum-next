// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ============================================================================
namespace WarajevoNext.Machine;

/// <summary>
/// 8x5 keyboard matrix, one bit per key. A key is "pressed" when its bit is 0
/// (the ULA returns active-low). The high byte of port 0xFE selects the half-
/// rows to read; bits 0..4 return the key state.
/// </summary>
public sealed class Keyboard
{
    // 8 rows x 5 keys per row; true = pressed
    private readonly bool[,] _pressed = new bool[8, 5];

    public void SetKey(SpectrumKey key, bool pressed)
    {
        int idx = (int)key;
        int row = idx / 5;
        int col = idx % 5;
        _pressed[row, col] = pressed;
    }

    public void ReleaseAll()
    {
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 5; c++)
                _pressed[r, c] = false;
    }

    /// <summary>Read half-row bits for a port-FE address (high byte selects rows).</summary>
    public byte Read(byte rowMaskHigh)
    {
        // rowMaskHigh: A15..A8; each 0 bit selects that half-row for reading.
        byte result = 0x1F; // no keys pressed = all high
        for (int r = 0; r < 8; r++)
        {
            if ((rowMaskHigh & (1 << r)) == 0)
            {
                for (int c = 0; c < 5; c++)
                    if (_pressed[r, c]) result &= (byte)~(1 << c);
            }
        }
        return result;
    }
}
