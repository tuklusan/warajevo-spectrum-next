// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// Tests for SpectrumContentionModel and its integration with the Z80 core.
//
// The FUSE conformance suite deliberately runs against FlatMemory+NullIo with
// no contention model installed; we exercise the model here through the real
// SpectrumMachine, which does install one.
// ============================================================================
using WarajevoNext.Machine;
using Xunit;

namespace WarajevoNext.MachineTests;

public class ContentionTests
{
    private static byte[] StubRom() => new byte[0x4000];

    // The published 48K pattern is [6,5,4,3,2,1,0,0] starting at T=14335.
    // Verify the model exposes those slot values directly for a contended
    // address (0x4000).
    [Theory]
    [InlineData(14335, 6)]
    [InlineData(14336, 5)]
    [InlineData(14337, 4)]
    [InlineData(14338, 3)]
    [InlineData(14339, 2)]
    [InlineData(14340, 1)]
    [InlineData(14341, 0)]
    [InlineData(14342, 0)]
    [InlineData(14343, 6)] // next 8-slot period
    public void Model_48k_ContendedSlotsMatchULAPattern(int tstate, int expected)
    {
        var mem = new SpectrumMemory(SpectrumModel.FortyEight, StubRom());
        var m = new SpectrumContentionModel(mem);
        Assert.Equal(expected, m.ContendMemory(tstate, 0x4000));
    }

    [Fact]
    public void Model_48k_UncontendedAddress_NoDelayEver()
    {
        var mem = new SpectrumMemory(SpectrumModel.FortyEight, StubRom());
        var m = new SpectrumContentionModel(mem);
        // Sweep the whole frame: 0x8000 is never contended on 48K.
        for (int t = 0; t < 69888; t++)
            Assert.Equal(0, m.ContendMemory(t, 0x8000));
    }

    [Fact]
    public void Model_48k_OutsideContentionWindow_NoDelay()
    {
        var mem = new SpectrumMemory(SpectrumModel.FortyEight, StubRom());
        var m = new SpectrumContentionModel(mem);
        // Before the first contended M-cycle (T<14335) even a contended
        // address is not delayed.
        Assert.Equal(0, m.ContendMemory(0, 0x4000));
        Assert.Equal(0, m.ContendMemory(14334, 0x4000));
        // Between two contended lines (after the 128th slot) → no delay.
        Assert.Equal(0, m.ContendMemory(14335 + 128, 0x4000));
        Assert.Equal(0, m.ContendMemory(14335 + 200, 0x4000));
    }

    [Fact]
    public void Model_128k_HighBank_ContentionFollowsPaging()
    {
        var mem = new SpectrumMemory(SpectrumModel.OneTwentyEight,
                                     StubRom(), StubRom(), StubRom());
        var m = new SpectrumContentionModel(mem);
        // Bank 1 (odd) at 0xC000 → contended.
        mem.Write7FFD(0x01);
        Assert.NotEqual(0, m.ContendMemory(14361, 0xC000));
        // Bank 2 (even) at 0xC000 → uncontended.
        mem.Write7FFD(0x02);
        Assert.Equal(0, m.ContendMemory(14361, 0xC000));
        // 0x4000-0x7FFF is always contended regardless of the top bank.
        Assert.NotEqual(0, m.ContendMemory(14361, 0x4000));
    }

    // A "benign" program that never touches the contended range must burn
    // exactly the natural T-state count per instruction; the frame budget
    // then advances in whole-instruction steps that overshoot the target by
    // at most the last instruction's cost.
    [Fact]
    public void Frame_BudgetExact_WhenProgramAvoidsContendedArea()
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        // Uncontended NOP sled at 0x8000, jumping to itself with JR $ (18 FE).
        // NOP = 4 T-states; JR = 12 T-states when taken. All fetches are at
        // 0x8000+, so contention never fires.
        m.Memory.Write(0x8000, 0x00); // NOP
        m.Memory.Write(0x8001, 0x18); // JR
        m.Memory.Write(0x8002, 0xFE); // -2  → jumps back to 0x8000
        m.Cpu.PC = 0x8000;
        m.Cpu.SP = 0xFFFF;
        long before = m.Cpu.TStates;
        m.RunFrame();
        long spent = m.Cpu.TStates - before;
        // Whole-instruction stepping can overshoot by at most 15 T-states
        // (longest instruction here is JR at 12); the interrupt Accept adds
        // 13. So the acceptable band is [69888, 69888+15].
        Assert.InRange(spent, 69888, 69888 + 15);
    }

    // Integration: at a known contended slot, an opcode fetch from 0x4000
    // costs (4 + slot-delay). Run a lone NOP (0x00) whose fetch straddles
    // T=14335 (delay=6): 4 + 6 = 10 T-states.
    [Fact]
    public void Z80_MFetchOnContendedSlot_ChargesDelay()
    {
        var m = new SpectrumMachine(SpectrumModel.FortyEight, StubRom());
        m.Memory.Write(0x4000, 0x00); // NOP
        m.Cpu.PC = 0x4000;
        // Snap the CPU clock to the first contended T-state of the frame.
        m.Cpu.TStates = 14335;
        long before = m.Cpu.TStates;
        m.Cpu.Step();
        Assert.Equal(4 + 6, m.Cpu.TStates - before);
    }
}
