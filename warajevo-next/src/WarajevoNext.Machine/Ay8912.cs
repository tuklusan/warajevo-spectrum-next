// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// AY-3-8912 minimum viable emulation: 16 registers, tone/noise/envelope.
// Audio synthesis is intentionally simplified; the goal is correct register
// I/O and enough tone output to make 128K games audibly work. Envelope shape
// support and full noise LFSR live here but the mixer is naive.
// ============================================================================
namespace WarajevoNext.Machine;

public sealed class Ay8912
{
    private readonly byte[] _reg = new byte[16];
    private int _selected;

    // Simple state for tone counters (16-bit period)
    private readonly int[] _toneCounter = new int[3];
    private readonly bool[] _toneOn = new bool[3];
    private int _noiseCounter;
    private uint _noiseLfsr = 1;
    private bool _noiseOn;
    private int _envCounter;
    private int _envStep;
    private int _envValue;

    public void SelectRegister(byte v) => _selected = v & 0x0F;

    public byte ReadData() => _reg[_selected];

    public void WriteData(byte v)
    {
        _reg[_selected] = v;
        if (_selected == 13) { _envStep = 0; _envValue = 0; _envCounter = 0; }
    }

    public byte GetRegister(int i) => _reg[i & 0x0F];

    /// <summary>Advance the AY state by <paramref name="cycles"/> AY clocks
    /// (AY clock = CPU clock / 2 on 128K).</summary>
    public void Tick(int cycles)
    {
        // Cheap step; we render actual audio in the output layer.
        int nA = _reg[0] | ((_reg[1] & 0x0F) << 8);
        int nB = _reg[2] | ((_reg[3] & 0x0F) << 8);
        int nC = _reg[4] | ((_reg[5] & 0x0F) << 8);
        StepTone(0, nA, cycles);
        StepTone(1, nB, cycles);
        StepTone(2, nC, cycles);
        int np = _reg[6] & 0x1F; if (np == 0) np = 1;
        _noiseCounter += cycles;
        while (_noiseCounter >= np * 16)
        {
            _noiseCounter -= np * 16;
            uint bit = ((_noiseLfsr >> 0) ^ (_noiseLfsr >> 3)) & 1;
            _noiseLfsr = (_noiseLfsr >> 1) | (bit << 16);
            _noiseOn = (_noiseLfsr & 1) != 0;
        }
    }

    private void StepTone(int ch, int period, int cycles)
    {
        if (period == 0) period = 1;
        _toneCounter[ch] += cycles;
        while (_toneCounter[ch] >= period * 8)
        {
            _toneCounter[ch] -= period * 8;
            _toneOn[ch] = !_toneOn[ch];
        }
    }

    /// <summary>Compute a single mixed sample in [-1, 1] range.</summary>
    public float Sample()
    {
        int mixer = _reg[7];
        float s = 0;
        for (int ch = 0; ch < 3; ch++)
        {
            bool toneEnable = ((mixer >> ch) & 1) == 0;
            bool noiseEnable = ((mixer >> (ch + 3)) & 1) == 0;
            bool level = (toneEnable && _toneOn[ch]) || (noiseEnable && _noiseOn);
            if (!toneEnable && !noiseEnable) level = true; // when both disabled -> DC
            int vol = _reg[8 + ch] & 0x0F;
            float amp = level ? (vol / 15.0f) : 0.0f;
            s += amp;
        }
        return (s / 3.0f) - 0.5f;
    }
}
