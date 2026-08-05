// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// SpectrumContentionModel — the 48K / 128K ULA memory- and I/O-contention
// model. Any CPU M-cycle that hits a contended address while the ULA is
// fetching bitmap+attribute pauses the CPU for 0..6 extra T-states, according
// to where inside the 8-cycle ULA slot the M-cycle lands.
//
// The pattern per contended scanline is [6, 5, 4, 3, 2, 1, 0, 0] repeated 16
// times (128 contended T-states out of the 224/228 in a line). We table it
// once at construction time and index it by (tstate mod frameLen).
//
// Contended address ranges:
//   48K : 0x4000-0x7FFF.
//   128K: 0x4000-0x7FFF plus 0xC000-0xFFFF whenever an odd-numbered RAM bank
//         (1, 3, 5, 7) is paged in there.
//
// Contended I/O ports mirror the address rule plus the ULA's own "A0=0"
// classification. This is the FUSE-style simplified model: it charges one
// slot of contention for a matching cycle rather than the fully-decomposed
// C:1/C:3/N:1 sequence, which is close enough for game-level accuracy while
// still exercising the hook.
//
// The model is installed opt-in via Z80.Contention; leaving it null keeps
// the FUSE Z80 conformance suite exact.
// ============================================================================
using WarajevoNext.Cpu;

namespace WarajevoNext.Machine;

public sealed class SpectrumContentionModel : IContentionModel
{
    private readonly SpectrumMemory _mem;
    private readonly SpectrumModel _model;
    private readonly int _frameTs;
    private readonly int[] _delay;

    // First T-state of the first contended M-cycle within the frame, and the
    // number of T-states per scanline. Values from the SinclairFAQ / WoS
    // timing tables cross-checked against FUSE.
    private const int FirstContendedT_48   = 14335;
    private const int TStatesPerLine_48    = 224;
    private const int FirstContendedT_128  = 14361;
    private const int TStatesPerLine_128   = 228;
    private const int ContendedLines       = 192;
    private const int ContendedTsPerLine   = 128;

    public SpectrumContentionModel(SpectrumMemory mem)
    {
        _mem = mem;
        _model = mem.Model;
        int firstT;
        int tsPerLine;
        if (_model == SpectrumModel.OneTwentyEight)
        {
            _frameTs = 70908;
            firstT = FirstContendedT_128;
            tsPerLine = TStatesPerLine_128;
        }
        else
        {
            _frameTs = 69888;
            firstT = FirstContendedT_48;
            tsPerLine = TStatesPerLine_48;
        }
        _delay = new int[_frameTs];
        ReadOnlySpan<int> pattern = stackalloc int[] { 6, 5, 4, 3, 2, 1, 0, 0 };
        for (int line = 0; line < ContendedLines; line++)
        {
            int lineStart = firstT + line * tsPerLine;
            for (int i = 0; i < ContendedTsPerLine; i++)
            {
                int t = lineStart + i;
                if ((uint)t < (uint)_frameTs) _delay[t] = pattern[i & 7];
            }
        }
    }

    public int ContendMemory(int tstate, ushort address)
    {
        if (!IsContendedAddress(address)) return 0;
        int t = tstate % _frameTs;
        if (t < 0) t += _frameTs;
        return _delay[t];
    }

    public int ContendIo(int tstate, ushort port)
    {
        bool ulaPort = (port & 1) == 0;
        bool contendedAddr = IsContendedAddress(port);
        if (!ulaPort && !contendedAddr) return 0;
        int t = tstate % _frameTs;
        if (t < 0) t += _frameTs;
        return _delay[t];
    }

    /// <summary>Absolute T-states per frame for the modelled machine.</summary>
    public int FrameTStates => _frameTs;

    private bool IsContendedAddress(ushort a)
    {
        if (a >= 0x4000 && a < 0x8000) return true;
        if (_model == SpectrumModel.OneTwentyEight && a >= 0xC000)
        {
            // Banks 1, 3, 5, 7 sit on the contended half of the RAM.
            return (_mem.PageBank & 1) != 0;
        }
        return false;
    }
}
