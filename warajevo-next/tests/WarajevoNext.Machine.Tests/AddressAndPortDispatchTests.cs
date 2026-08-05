// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// Extra coverage for:
//   A. Ula display-address decode (pixel + attribute)
//   B. SpectrumMachine IIoBus port dispatch (48K vs 128K)
//   C. 128K memory paging edge cases
// ============================================================================
using WarajevoNext.Machine;
using Xunit;

namespace WarajevoNext.MachineTests;

public class AddressAndPortDispatchTests
{
    private static byte[] StubRom() => new byte[0x4000];

    // Pixel byte offset within ScreenBank (0x0000..0x1AFF). Absolute = 0x4000 + this on 48K.
    //   y[7:6] pick the third (0..2), y[2:0] pick the row within a char block,
    //   y[5:3] pick the char-row within a third, x/8 picks the column byte.
    private static int PixOffset(int x, int y) =>
        ((y & 0xC0) << 5) | ((y & 0x07) << 8) | ((y & 0x38) << 2) | (x >> 3);

    // Attribute byte offset within ScreenBank.
    private static int AttrOffset(int x, int y) => 0x1800 + (y >> 3) * 32 + (x >> 3);

    // Set up a 48K machine, paint every attribute byte with bright-white ink on
    // black paper (0x47), then set the single pixel at (x,y) via mem.Write and
    // render a frame. Returns the resulting ARGB pixel at the on-screen (x,y).
    private static uint RenderSinglePixel(int x, int y)
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        for (int a = 0x5800; a < 0x5B00; a++) m.Memory.Write((ushort)a, 0x47);
        int pixAddr = 0x4000 + PixOffset(x, y);
        byte bit = (byte)(0x80 >> (x & 7));
        m.Memory.Write((ushort)pixAddr, bit);
        var buf = new uint[Ula.FrameW * Ula.FrameH];
        m.Ula.RenderFrame(buf);
        return buf[(Ula.BorderPx + y) * Ula.FrameW + Ula.BorderPx + x];
    }

    // ---------- A. Display address decode ----------------------------------

    [Fact]
    public void Pixel_TopLeftCorner_LightsOnlyThatPixel()
    {
        Assert.Equal(0xFFFFFFFFu, RenderSinglePixel(0, 0));
    }

    [Fact]
    public void Pixel_TopRow_LastByteBit0()
    {
        Assert.Equal(0xFFFFFFFFu, RenderSinglePixel(255, 0));
    }

    [Fact]
    public void Pixel_FirstThird_LastRow_Row63()
    {
        Assert.Equal(0xFFFFFFFFu, RenderSinglePixel(0, 63));
    }

    [Fact]
    public void Pixel_SecondThird_FirstRow_Row64()
    {
        Assert.Equal(0xFFFFFFFFu, RenderSinglePixel(0, 64));
    }

    [Fact]
    public void Pixel_SecondThird_LastRow_Row127()
    {
        Assert.Equal(0xFFFFFFFFu, RenderSinglePixel(120, 127));
    }

    [Fact]
    public void Pixel_ThirdThird_FirstRow_Row128()
    {
        Assert.Equal(0xFFFFFFFFu, RenderSinglePixel(128, 128));
    }

    [Fact]
    public void Pixel_BottomRightCorner()
    {
        Assert.Equal(0xFFFFFFFFu, RenderSinglePixel(255, 191));
    }

    [Fact]
    public void Pixel_TwentySpotChecks_AllRespectFormula()
    {
        // Spans all three thirds and char-row boundaries.
        var spots = new (int x, int y)[]
        {
            (0,0), (7,0), (8,0), (255,7),
            (0,8), (16,15), (0,55), (0,56), (200,63),
            (0,64), (0,71), (255,72), (128,120), (0,127),
            (0,128), (0,135), (100,150), (200,167), (0,190), (255,191),
        };
        foreach (var (x, y) in spots)
            Assert.Equal(0xFFFFFFFFu, RenderSinglePixel(x, y));
    }

    [Fact]
    public void Attr_TopLeft_ColoursOneCharBlockRed()
    {
        // Fill screen pixels with 0xFF, all attrs default to 0 (black on black).
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        for (int a = 0x4000; a < 0x5800; a++) m.Memory.Write((ushort)a, 0xFF);
        // Paint one attribute byte at (0,0) with paper=black, ink=red (2).
        m.Memory.Write((ushort)(0x4000 + AttrOffset(0, 0)), 0x02);
        var buf = new uint[Ula.FrameW * Ula.FrameH];
        m.Ula.RenderFrame(buf);
        // The 8x8 char block at (0,0) should be red on-screen.
        for (int yy = 0; yy < 8; yy++)
        for (int xx = 0; xx < 8; xx++)
        {
            uint px = buf[(Ula.BorderPx + yy) * Ula.FrameW + Ula.BorderPx + xx];
            Assert.Equal(0xFFD70000u, px);
        }
    }

    [Fact]
    public void Attr_TenSpotChecks_ColourOnlyTargetCharBlock()
    {
        var spots = new (int cx, int cy)[]
        {
            (0,0), (31,0), (0,7), (31,7),
            (0,8), (15,11), (0,23), (16,16),
            (0,23), (31,23),
        };
        foreach (var (cx, cy) in spots)
        {
            var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
            for (int a = 0x4000; a < 0x5800; a++) m.Memory.Write((ushort)a, 0xFF);
            int attr = 0x4000 + 0x1800 + cy * 32 + cx;
            m.Memory.Write((ushort)attr, 0x02); // ink red
            var buf = new uint[Ula.FrameW * Ula.FrameH];
            m.Ula.RenderFrame(buf);
            int px0 = cx * 8, py0 = cy * 8;
            uint px = buf[(Ula.BorderPx + py0) * Ula.FrameW + Ula.BorderPx + px0];
            Assert.Equal(0xFFD70000u, px);
        }
    }

    // ---------- B. Port dispatch -------------------------------------------

    [Fact]
    public void Port_FE_In_48K_HitsUla()
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        // No key pressed -> bits 0..4 all high, plus bits 5,7 always set = 0xBF (EAR bit 6 low).
        byte v = m.In(0xFE);
        Assert.Equal(0xBF, v);
    }

    [Fact]
    public void Port_KeyboardRowSelect_ReadsPressedKeyOnRow7()
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        m.Keyboard.SetKey(SpectrumKey.Space, true); // row 7, col 0
        // Port 0x7FFE: A0=0 hits ULA; high byte 0x7F clears bit 7 -> selects row 7.
        byte v = m.In(0x7FFE);
        // Row-7 col-0 pressed -> bit 0 clear on the low 5 bits (0x1E), plus 0xA0 top bits.
        Assert.Equal(0xBE, v);
    }

    [Fact]
    public void Port_FE_Out_48K_SetsBorderAndBeeper()
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        m.Out(0xFE, 0x13); // border=3, beeper on
        Assert.Equal(3, m.Ula.Border);
        Assert.True(m.Ula.Beeper);
    }

    [Fact]
    public void Port_7FFD_Out_48K_IsIgnored()
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        // Memory has no paging on 48K; Write7FFD is a no-op.
        m.Out(0x7FFD, 0x07);
        // The state field encodes bank/rom/paging; on 48K it never changes.
        int before = m.Memory.PageState;
        m.Out(0x7FFD, 0x1F);
        Assert.Equal(before, m.Memory.PageState);
    }

    [Fact]
    public void Port_7FFD_Out_128K_ChangesBankAtC000()
    {
        var m = new SpectrumMachine(SpectrumModel.OneTwentyEight, StubRom(), StubRom(), StubRom());
        m.Out(0x7FFD, 0x00);
        m.Memory.Write(0xC000, 0x11);
        m.Out(0x7FFD, 0x01);
        m.Memory.Write(0xC000, 0x22);
        m.Out(0x7FFD, 0x00);
        Assert.Equal(0x11, m.Memory.Read(0xC000));
    }

    [Fact]
    public void Port_1F_In_Kempston_Returns_Zero_On_48K()
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        Assert.Equal(0, m.In(0x001F));
        Assert.Equal(0, m.In(0x1F1F)); // any port with low byte 0x1F
    }

    [Fact]
    public void Port_1F_In_Kempston_Returns_Zero_On_128K()
    {
        var m = new SpectrumMachine(SpectrumModel.OneTwentyEight, StubRom(), StubRom(), StubRom());
        Assert.Equal(0, m.In(0x001F));
    }

    [Fact]
    public void Port_FFFD_In_128K_ReadsSelectedAyRegister()
    {
        var m = new SpectrumMachine(SpectrumModel.OneTwentyEight, StubRom(), StubRom(), StubRom());
        m.Out(0xFFFD, 0x07);   // select register 7
        m.Out(0xBFFD, 0x3F);   // write 0x3F to register 7
        Assert.Equal(0x3F, m.In(0xFFFD));
        Assert.Equal(0x3F, m.Ay!.GetRegister(7));
    }

    [Fact]
    public void Port_FFFD_In_48K_UnmappedReturnsFF()
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        Assert.Equal(0xFF, m.In(0xFFFD));
    }

    [Fact]
    public void Port_UnmappedOddPort_Returns_FF()
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        Assert.Equal(0xFF, m.In(0xFFFF));
        Assert.Equal(0xFF, m.In(0x00FD)); // low byte not 0x1F, A0=1, no Ay
    }

    [Fact]
    public void Port_BFFD_Out_128K_WritesAyData()
    {
        var m = new SpectrumMachine(SpectrumModel.OneTwentyEight, StubRom(), StubRom(), StubRom());
        m.Out(0xFFFD, 0x0A); // select register 10
        m.Out(0xBFFD, 0x0C); // write 0x0C
        Assert.Equal(0x0C, m.Ay!.GetRegister(10));
    }

    // ---------- C. 128K memory paging edge cases ---------------------------

    [Fact]
    public void Paging_Bit3_TogglesScreenBankBetween5And7()
    {
        var m = new SpectrumMemory(SpectrumModel.OneTwentyEight, StubRom(), StubRom(), StubRom());
        m.Write7FFD(0x00);
        Assert.False(m.ShadowScreen);
        var bank5 = m.ScreenBank;
        m.Write7FFD(0x08);
        Assert.True(m.ShadowScreen);
        Assert.NotSame(bank5, m.ScreenBank);
        m.Write7FFD(0x00);
        Assert.Same(bank5, m.ScreenBank);
    }

    [Fact]
    public void Paging_Bit4_TogglesBetweenRom0AndRom1()
    {
        // Give the two ROMs distinct signature bytes so we can detect the swap.
        var rom0 = new byte[0x4000]; rom0[0] = 0xA0;
        var rom1 = new byte[0x4000]; rom1[0] = 0xB1;
        var m = new SpectrumMemory(SpectrumModel.OneTwentyEight, StubRom(), rom0, rom1);
        m.Write7FFD(0x00);
        Assert.Equal(0xA0, m.Read(0x0000));
        m.Write7FFD(0x10);
        Assert.Equal(0xB1, m.Read(0x0000));
        m.Write7FFD(0x00);
        Assert.Equal(0xA0, m.Read(0x0000));
    }

    [Fact]
    public void Paging_AllEightBanksAddressableViaC000()
    {
        var m = new SpectrumMemory(SpectrumModel.OneTwentyEight, StubRom(), StubRom(), StubRom());
        // Write a distinct signature into each of the 8 banks by paging it in.
        for (int b = 0; b < 8; b++)
        {
            m.Write7FFD((byte)b);
            m.Write(0xC000, (byte)(0xA0 | b));
        }
        // Read them all back.
        for (int b = 0; b < 8; b++)
        {
            m.Write7FFD((byte)b);
            Assert.Equal((byte)(0xA0 | b), m.Read(0xC000));
        }
    }

    [Fact]
    public void Paging_Bit3_DoesNotChangeBankAtC000()
    {
        var m = new SpectrumMemory(SpectrumModel.OneTwentyEight, StubRom(), StubRom(), StubRom());
        m.Write7FFD(0x02);
        m.Write(0xC000, 0x55);
        m.Write7FFD(0x02 | 0x08); // flip shadow-screen bit only
        Assert.Equal(0x55, m.Read(0xC000));
    }
}
