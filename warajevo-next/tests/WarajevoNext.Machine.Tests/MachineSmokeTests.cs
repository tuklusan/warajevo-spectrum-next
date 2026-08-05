// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ============================================================================
using WarajevoNext.Machine;
using Xunit;

namespace WarajevoNext.MachineTests;

public class MachineSmokeTests
{
    private static byte[] StubRom() => new byte[0x4000];

    [Fact]
    public void Memory_48k_ReadWriteBanks()
    {
        var mem = new SpectrumMemory(SpectrumModel.FortyEight, StubRom());
        // ROM writes are ignored
        mem.Write(0x0000, 0x12);
        Assert.Equal(0x00, mem.Read(0x0000));
        // RAM at 0x4000, 0x8000, 0xC000
        mem.Write(0x4000, 0xAA); mem.Write(0x8000, 0xBB); mem.Write(0xC000, 0xCC);
        Assert.Equal(0xAA, mem.Read(0x4000));
        Assert.Equal(0xBB, mem.Read(0x8000));
        Assert.Equal(0xCC, mem.Read(0xC000));
    }

    [Fact]
    public void Memory_128k_BankSwitching()
    {
        var mem = new SpectrumMemory(SpectrumModel.OneTwentyEight, StubRom(), StubRom(), StubRom());
        // Write into bank 0 while paged in
        mem.Write7FFD(0x00);
        mem.Write(0xC000, 0x11);
        // Switch to bank 1
        mem.Write7FFD(0x01);
        mem.Write(0xC000, 0x22);
        // Back to 0 and read
        mem.Write7FFD(0x00);
        Assert.Equal(0x11, mem.Read(0xC000));
        mem.Write7FFD(0x01);
        Assert.Equal(0x22, mem.Read(0xC000));
    }

    [Fact]
    public void Memory_128k_PagingLock()
    {
        var mem = new SpectrumMemory(SpectrumModel.OneTwentyEight, StubRom(), StubRom(), StubRom());
        mem.Write7FFD(0x00);
        mem.Write(0xC000, 0xAA);
        mem.Write7FFD(0x20); // lock
        mem.Write7FFD(0x01); // ignored
        Assert.Equal(0xAA, mem.Read(0xC000));
    }

    [Fact]
    public void Keyboard_HalfRowRead()
    {
        var kb = new Keyboard();
        kb.SetKey(SpectrumKey.Space, true); // row 7, bit 0
        // Half-row A15=0 selects row 7
        byte v = kb.Read(0x7F);
        Assert.Equal(0x1E, v);
    }

    [Fact]
    public void Machine_RunFrame_AdvancesTStatesAndFrameCount()
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        long before = m.Cpu.TStates;
        m.RunFrame();
        Assert.True(m.Cpu.TStates >= before + 69888);
        Assert.Equal(1, m.FrameCount);
    }

    [Fact]
    public void Ula_RendersBorderAndScreen()
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        m.Ula.Border = 2; // red
        var buf = new uint[Ula.FrameW * Ula.FrameH];
        m.Ula.RenderFrame(buf);
        Assert.Equal(m.Ula.BorderColor, buf[0]);
    }
}
