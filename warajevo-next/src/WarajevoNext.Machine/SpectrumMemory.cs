// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// 48K and 128K Spectrum memory maps.
//
//   48K:  0x0000-0x3FFF = ROM (16K, from user-supplied 48.rom)
//         0x4000-0xFFFF = 48K contiguous RAM. Screen at 0x4000.
//   128K: 0x0000-0x3FFF = one of two 16K ROMs selected by port 0x7FFD bit 4
//         0x4000-0x7FFF = RAM bank 5   (contains the "normal" screen)
//         0x8000-0xBFFF = RAM bank 2
//         0xC000-0xFFFF = RAM bank selected by port 0x7FFD bits 0..2
//         Port 0x7FFD bit 3 selects RAM bank 5 or 7 as the displayed screen.
//         Bit 5 locks paging until next reset.
// ============================================================================
using WarajevoNext.Cpu;

namespace WarajevoNext.Machine;

public enum SpectrumModel { Sixteen, FortyEight, OneTwentyEight }

public sealed class SpectrumMemory : IMemoryBus
{
    public SpectrumModel Model { get; }
    private readonly byte[][] _rom;   // 1 (48K) or 2 (128K)
    private readonly byte[][] _bank;  // 8 x 16K banks (128K) or a single flat block (48K)
    private int _romIndex;
    private int _pageBank;      // bank at 0xC000 (128K)
    private bool _shadowScreen; // 128K bit 3
    private bool _paged;        // 128K bit 5: paging enabled (true means writes accepted)
    public int PageState => (_paged ? 0 : 0x20) | (_romIndex << 4) | (_shadowScreen ? 0x08 : 0) | _pageBank;

    public bool ShadowScreen => _shadowScreen;

    public SpectrumMemory(SpectrumModel model, byte[] rom48, byte[]? rom128_0 = null, byte[]? rom128_1 = null)
    {
        Model = model;
        _paged = true;
        _pageBank = 0;
        _romIndex = 0;
        _shadowScreen = false;

        if (model == SpectrumModel.OneTwentyEight)
        {
            if (rom128_0 == null || rom128_1 == null) throw new ArgumentNullException(nameof(rom128_0));
            _rom = new[] { rom128_0, rom128_1 };
            _bank = new byte[8][];
            for (int i = 0; i < 8; i++) _bank[i] = new byte[0x4000];
        }
        else
        {
            _rom = new[] { rom48 };
            // Represent 48K RAM as 3 x 16K "banks" 5, 2, 0 (matching 128K layout).
            _bank = new byte[8][];
            for (int i = 0; i < 8; i++) _bank[i] = new byte[0x4000];
        }
    }

    /// <summary>Returns the RAM bank actually shown on the display (5 or 7).</summary>
    public byte[] ScreenBank => _bank[_shadowScreen ? 7 : 5];

    public byte Read(ushort a)
    {
        int page = a >> 14;
        return page switch
        {
            0 => _rom[_romIndex][a],
            1 => _bank[5][a & 0x3FFF],
            2 => _bank[2][a & 0x3FFF],
            _ => _bank[_pageBank][a & 0x3FFF],
        };
    }

    public void Write(ushort a, byte v)
    {
        int page = a >> 14;
        switch (page)
        {
            case 0: break; // ROM ignored
            case 1: _bank[5][a & 0x3FFF] = v; break;
            case 2: _bank[2][a & 0x3FFF] = v; break;
            default: _bank[_pageBank][a & 0x3FFF] = v; break;
        }
    }

    public void Write7FFD(byte v)
    {
        if (Model != SpectrumModel.OneTwentyEight) return;
        if (!_paged) return; // paging locked
        _pageBank = v & 0x07;
        _shadowScreen = (v & 0x08) != 0;
        _romIndex = (v & 0x10) != 0 ? 1 : 0;
        _paged = (v & 0x20) == 0; // once set, cannot be un-set
    }

    public void LoadRom(byte[] rom, int slot = 0) => Array.Copy(rom, _rom[slot], Math.Min(rom.Length, 0x4000));
}
