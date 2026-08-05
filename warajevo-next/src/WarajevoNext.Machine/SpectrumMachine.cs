// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// SpectrumMachine — the composed 48K / 128K machine. Wires the Z80 core to
// the memory bus, the ULA, the keyboard, the (optional) tape, and the AY
// sound chip (128K only). Runs one full frame per RunFrame() call and asserts
// the 50 Hz maskable interrupt at the top of each frame.
//
//   48K:  69888 T-states / frame, 3.5 MHz -> 50.08 Hz
//   128K: 70908 T-states / frame, 3.5469 MHz -> 50.01 Hz
// ============================================================================
using WarajevoNext.Cpu;

namespace WarajevoNext.Machine;

public sealed class SpectrumMachine : IIoBus
{
    public Z80 Cpu { get; }
    public SpectrumMemory Memory { get; }
    public Ula Ula { get; }
    public Keyboard Keyboard { get; }
    public TapeDevice? Tape { get; set; }
    public Ay8912? Ay { get; }

    public int TStatesPerFrame { get; }
    public SpectrumModel Model => Memory.Model;
    public long FrameCount { get; private set; }

    public SpectrumMachine(SpectrumModel model, byte[] rom48, byte[]? rom128_0 = null, byte[]? rom128_1 = null)
    {
        Memory = new SpectrumMemory(model, rom48, rom128_0, rom128_1);
        Keyboard = new Keyboard();
        Ula = new Ula(Memory, Keyboard);
        Cpu = new Z80(Memory, this);
        if (model == SpectrumModel.OneTwentyEight)
        {
            Ay = new Ay8912();
            TStatesPerFrame = 70908;
        }
        else
        {
            TStatesPerFrame = 69888;
        }
    }

    public void Reset()
    {
        Cpu.Reset();
        // Note: memory paging locks itself on write; we do not un-lock here
        // because a real 128K needs a hard reset to un-lock, matching the
        // physical behaviour.
    }

    /// <summary>Run exactly one frame; asserts INT at the start.</summary>
    public void RunFrame()
    {
        Cpu.RequestInterrupt();
        long target = Cpu.TStates + TStatesPerFrame;
        while (Cpu.TStates < target) Cpu.Step();
        FrameCount++;
    }

    // ---- IIoBus dispatch --------------------------------------------------
    public byte In(ushort port)
    {
        // ULA — any port with A0 == 0
        if ((port & 1) == 0)
        {
            byte v = Ula.In(port);
            // Tape EAR (bit 6) if a tape is playing
            if (Tape != null && Tape.IsPlaying)
                v = (byte)((v & 0xBF) | (Tape.CurrentEar & 0x40));
            return v;
        }
        // 128K AY read: port 0xFFFD
        if (Ay != null && (port & 0xC002) == 0xC000) return Ay.ReadData();
        // Kempston joystick placeholder (0x1F) — return 0 (no directions)
        if ((port & 0x00FF) == 0x001F) return 0;
        // Floating bus / unmapped — return 0xFF
        return 0xFF;
    }

    public void Out(ushort port, byte value)
    {
        if ((port & 1) == 0) { Ula.Out(port, value); return; }
        if (Memory.Model == SpectrumModel.OneTwentyEight)
        {
            // 128K memory / paging control: port 0x7FFD (A15=0, A1=0)
            if ((port & 0xC002) == 0x4000) { Memory.Write7FFD(value); return; }
            // AY register select: 0xFFFD (A15=1, A14=1, A1=0)
            if (Ay != null && (port & 0xC002) == 0xC000) { Ay.SelectRegister(value); return; }
            // AY data write: 0xBFFD (A15=1, A14=0, A1=0)
            if (Ay != null && (port & 0xC002) == 0x8000) { Ay.WriteData(value); return; }
        }
        // Everything else ignored
    }
}
