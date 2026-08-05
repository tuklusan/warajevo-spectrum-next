// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// ULA: display generator + port 0xFE handler.
//
//   - Renders the 256x192 Spectrum screen + border to a 32-bit ARGB buffer.
//   - Reads: bits 0..4 = keyboard, bit 6 = EAR (tape), bits 5+7 = 1.
//   - Writes: bits 0..2 = border colour; bit 3 = MIC; bit 4 = beeper.
//   - FLASH attribute toggles every 16 frames.
// ============================================================================
using WarajevoNext.Cpu;

namespace WarajevoNext.Machine;

public sealed class Ula
{
    public const int ScreenW = 256;
    public const int ScreenH = 192;
    public const int BorderPx = 32; // per side
    public const int FrameW = ScreenW + BorderPx * 2;
    public const int FrameH = ScreenH + BorderPx * 2;

    private readonly SpectrumMemory _mem;
    private readonly Keyboard _kb;
    public byte Border;
    public bool Beeper;
    public byte EarInput; // driven by the tape device (0 or 0x40)
    public int Frame;      // frame counter
    public bool FlashPhase => (Frame & 0x10) != 0;

    // Standard Spectrum ULA colour palette (BRIGHT off / on) — sRGB approx.
    private static readonly uint[] Palette =
    {
        // Bright 0
        0xFF000000, 0xFF0000D7, 0xFFD70000, 0xFFD700D7,
        0xFF00D700, 0xFF00D7D7, 0xFFD7D700, 0xFFD7D7D7,
        // Bright 1
        0xFF000000, 0xFF0000FF, 0xFFFF0000, 0xFFFF00FF,
        0xFF00FF00, 0xFF00FFFF, 0xFFFFFF00, 0xFFFFFFFF,
    };

    public uint BorderColor => Palette[Border & 7];

    public Ula(SpectrumMemory mem, Keyboard kb)
    {
        _mem = mem;
        _kb = kb;
    }

    public byte In(ushort port)
    {
        // Port 0xFE — any port with A0=0 counts. High byte selects keyboard rows.
        byte v = _kb.Read((byte)(port >> 8));
        v |= 0xA0; // bits 5 and 7 always read as 1
        if ((EarInput & 0x40) != 0) v |= 0x40; else v &= 0xBF;
        return v;
    }

    public void Out(ushort port, byte value)
    {
        Border = (byte)(value & 7);
        Beeper = (value & 0x10) != 0;
    }

    /// <summary>
    /// Render one full frame (border + screen) into <paramref name="frameBuf"/>,
    /// which must be FrameW * FrameH pixels (32-bit ARGB).
    /// </summary>
    public void RenderFrame(uint[] frameBuf)
    {
        Frame++;
        uint border = Palette[Border & 7];
        // Fill border
        for (int y = 0; y < FrameH; y++)
        {
            if (y < BorderPx || y >= BorderPx + ScreenH)
            {
                int rowStart = y * FrameW;
                for (int x = 0; x < FrameW; x++) frameBuf[rowStart + x] = border;
            }
            else
            {
                int rowStart = y * FrameW;
                for (int x = 0; x < BorderPx; x++) frameBuf[rowStart + x] = border;
                for (int x = BorderPx + ScreenW; x < FrameW; x++) frameBuf[rowStart + x] = border;
            }
        }
        // Render 256x192 pixel area
        var vram = _mem.ScreenBank;
        bool flash = FlashPhase;
        for (int y = 0; y < ScreenH; y++)
        {
            // Address decode: y bits [2,1,0] within char block, y[5,4,3] within third, y[7,6] = which third
            int y76 = (y >> 6) & 3;
            int y543 = (y >> 3) & 7;
            int y210 = y & 7;
            int pixAddr = (y76 << 11) | (y210 << 8) | (y543 << 5);
            int attrRow = y >> 3;
            int attrAddr = 0x1800 + attrRow * 32;
            int outRow = (BorderPx + y) * FrameW + BorderPx;
            for (int col = 0; col < 32; col++)
            {
                byte pix = vram[pixAddr + col];
                byte attr = vram[attrAddr + col];
                int ink = attr & 7;
                int paper = (attr >> 3) & 7;
                bool bright = (attr & 0x40) != 0;
                bool fAttr = (attr & 0x80) != 0;
                if (fAttr && flash) { int t = ink; ink = paper; paper = t; }
                uint inkC = Palette[(bright ? 8 : 0) + ink];
                uint papC = Palette[(bright ? 8 : 0) + paper];
                for (int b = 0; b < 8; b++)
                {
                    uint c = ((pix & (0x80 >> b)) != 0) ? inkC : papC;
                    frameBuf[outRow + col * 8 + b] = c;
                }
            }
        }
    }
}
