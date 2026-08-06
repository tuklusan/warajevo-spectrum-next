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
    public SpectrumContentionModel Contention { get; }

    public int TStatesPerFrame { get; }
    public SpectrumModel Model => Memory.Model;
    public long FrameCount { get; private set; }

    /// <summary>
    /// When true, and the ROM at 0x0000-0x3FFF is recognised as the Sinclair
    /// 48K image, calls into the LD-BYTES entry-point (0x0556) are trapped
    /// and satisfied directly out of the TAP stream, skipping the ~5.5 s/KB
    /// edge decoding. Turn off for cycle-accurate loading (custom loaders).
    /// </summary>
    public bool FastLoad { get; set; } = true;

    // Sentinel bytes of the Sinclair 48K ROM at LD-BYTES (SA/LD-RET is at
    // 0x053F; the actual routine at 0x0556 begins the LOAD path with a
    // brief housekeeping preamble that saves D, disables interrupts and
    // primes the border colour before dropping into the pilot-scan loop):
    //   0556: 14           INC  D
    //   0557: 08           EX   AF,AF'
    //   0558: 15           DEC  D
    //   0559: F3           DI
    //   055A: 3E 0F        LD   A,0Fh          ; border colour
    //   055C: D3 FE        OUT  (FEh),A
    // The 128K "ROM 1" 48K-compatibility image has the same bytes here, so
    // the same sentinel covers both models. Six bytes are more than enough
    // to disambiguate against random RAM contents.
    private static readonly byte[] LdBytesSig = { 0x14, 0x08, 0x15, 0xF3, 0x3E, 0x0F };

    public SpectrumMachine(SpectrumModel model, byte[] rom48, byte[]? rom128_0 = null, byte[]? rom128_1 = null)
    {
        Memory = new SpectrumMemory(model, rom48, rom128_0, rom128_1);
        Keyboard = new Keyboard();
        Ula = new Ula(Memory, Keyboard);
        Cpu = new Z80(Memory, this);
        // Install the ULA memory- and I/O-contention hook so games that time
        // themselves off the border stripes (Uridium, Aquaplane, ...) run at
        // the same speed as on real hardware.
        Contention = new SpectrumContentionModel(Memory);
        Cpu.Contention = Contention;
        if (model == SpectrumModel.OneTwentyEight)
        {
            Ay = new Ay8912();
            TStatesPerFrame = 70908;
            Ula.TStatesPerLine = 228;
            // 128K interrupt fires ~14361 T-states before the first visible
            // screen line; our rendered top border is 32 of the 48 real
            // top-border lines, so its T=0 sits 32 * 228 T-states earlier.
            Ula.FirstBorderT = 14361 - 32 * 228;
        }
        else
        {
            TStatesPerFrame = 69888;
            Ula.TStatesPerLine = 224;
            Ula.FirstBorderT = 14335 - 32 * 224;
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
        _frameStartT = Cpu.TStates;
        long target = Cpu.TStates + TStatesPerFrame;
        long prev = Cpu.TStates;
        while (Cpu.TStates < target) StepOnce();
        _ = prev;   // Tape.Tick pulse-machinery feed removed - see below
        // Tape.Tick used to be called here to feed the old pulse-edge
        // state machine, but with the Warajevo-style LD-EDGE trap
        // (1aa4002) the trap owns the state machine and ticking the old
        // pulse code in parallel races it. Fast-load path (default) has
        // never needed Tick either - LD-BYTES trap consumes whole blocks
        // via TryReadNextBlock. Direct-EAR loaders (custom loaders that
        // read port 0xFE and time edges themselves, no ROM 0x0556 /
        // 0x05E7 call) will need their own dedicated hook.
        FrameCount++;
    }

    /// <summary>
    /// Runs one CPU instruction, or intercepts a fast-load trap first. Public
    /// so tests can drive a single step without needing a whole frame budget.
    /// </summary>
    public int StepOnce()
    {
        if (Tape != null && Tape.IsPlaying)
        {
            if (Cpu.PC == 0x0556 && IsSpectrum48LdBytes())
            {
                if (FastLoad) return HandleFastLoadTrap();
                // Normal-load: reset the LD-EDGE state machine so the trap
                // starts fresh on this block, and let ROM enter LD-BYTES.
                Tape.ResetEdgeMachine();
            }
            // Warajevo-style trap fires on the IN A,(FE) INSIDE LD-EDGE-1
            // (ROM 0x05F1), NOT on LD-EDGE-1's entry. Warajevo's Z80.ASM
            // INAN handler (line 4581+) sniffs every IN A,(n=254) and if
            // the surrounding ROM bytes match LD-EDGE's polling pattern,
            // it sets B via the state machine, sets EDGE_AFTER=PC+8, and
            // resumes execution after the AND 20 / JR Z loop. ROM's own
            // OUT (FE),A at 0x0601 then toggles the border colour and
            // 0x0603 SCF / 0x0604 RET returns success to LD-BYTES.
            else if (!FastLoad && Cpu.PC == 0x05F1 && IsSpectrum48LdEdgePoll())
            {
                return HandleLdEdgePollTrap();
            }
        }
        return Cpu.Step();
    }

    // Sinclair 48K ROM at LD-EDGE-1 polling IN (0x05F1..0x05F9):
    //   05F1: DB FE       IN A,(FEh)
    //   05F3: 1F          RRA
    //   05F4: D0          RET NC
    //   05F5: A9          XOR C
    //   05F6: E6 20       AND 20h
    //   05F8: 28 F3       JR Z,05EDh
    // If we recognise this exact bytestream we know we're on the real
    // Sinclair ROM (or a 128K compatible ROM 1 which is identical here).
    private static readonly byte[] LdEdgePollSig = { 0xDB, 0xFE, 0x1F, 0xD0, 0xA9, 0xE6, 0x20, 0x28, 0xF3 };

    private bool IsSpectrum48LdEdgePoll()
    {
        for (int i = 0; i < LdEdgePollSig.Length; i++)
            if (Memory.Read((ushort)(0x05F1 + i)) != LdEdgePollSig[i]) return false;
        return true;
    }

    // Line-by-line equivalent of Warajevo Z80.ASM INAN handler (line 4581+)
    // taking the KEYV1 alternate branch (line 4618-4629): recognise the
    // LD-EDGE polling loop, run the tape state machine, jump PC to
    // (current + 8) so execution resumes at the border-toggle code at
    // 0x05FA. ROM's own OUT (FE),A at 0x0601 handles the stripe, and its
    // SCF/RET at 0x0603/04 completes LD-EDGE-1.
    private int HandleLdEdgePollTrap()
    {
        long tStart = Cpu.TStates;
        if (!Tape!.TryHandleLdEdgeTrap(out bool setB, out byte bReturn, out _))
        {
            // End of tape / EDGE_BAD. Let ROM's RET NC at 0x05F4 fall
            // through by clearing CF and jumping PC past the poll loop -
            // but with A having bit 0 = 0 so RRA leaves CF=0 and RET NC
            // returns to caller with failure.
            Cpu.A = 0;
            Cpu.PC = 0x05F3;   // fall through RRA / RET NC = failure
            return (int)(Cpu.TStates - tStart);
        }
        if (setB) Cpu.B = bReturn;   // Warajevo LEADER/BITS: mov B,255
        // Warajevo Z80.ASM line 4624-4625: EDGE_AFTER = PC + 8.
        // At PC=0x05F1, PC+8 = 0x05F9. The DEC-and-jump-to-NOP trick
        // in Warajevo effectively advances one more to 0x05FA. Skip
        // straight there.
        Cpu.PC = 0x05FA;
        // Set A up so ROM's post-poll code produces a sensible edge.
        // At 0x05FA ROM does: LD A,C / CPL / LD C,A / AND 07 / OR 08 /
        // OUT (FE),A / SCF / RET. This toggles border and RETurns
        // success. We don't need to modify anything else.
        Cpu.TStates += 200L;
        return (int)(Cpu.TStates - tStart);
    }

    private bool IsSpectrum48LdBytes()
    {
        for (int i = 0; i < LdBytesSig.Length; i++)
            if (Memory.Read((ushort)(0x0556 + i)) != LdBytesSig[i]) return false;
        return true;
    }

    // Simulate the 48K LD-BYTES routine. On entry:
    //   AF' : A' = expected flag, F' CF = 1 LOAD / 0 VERIFY
    //   IX  : destination in RAM
    //   DE  : payload length (bytes between flag and checksum)
    // On exit (via popped RET address):
    //   CF = 1  loaded/verified OK
    //   CF = 0  wrong flag, short block, or bad checksum
    private int HandleFastLoadTrap()
    {
        long tStart = Cpu.TStates;
        // At entry to LD-BYTES the ROM caller convention (verified from the
        // actual 48K ROM sites at 0x076C `XOR A / SCF / CALL 0556` and 0x0800
        // `LD A,FFh / CALL 0556`) is that the expected flag byte is in MAIN A
        // and the LOAD-vs-VERIFY selector is MAIN F.CF. LD-BYTES's own first
        // real instruction at 0x0557 is `EX AF,AF'`, which then swaps them
        // into the shadow so the routine can use main F freely for its own
        // work - but that swap has not happened yet when our trap fires. An
        // earlier revision of this file mistakenly read the shadow AF' here,
        // which returned uninitialised/leftover bytes and made the trap
        // reject the DATA block that follows every header (A'=0 forever, so
        // flag=0xFF was always a mismatch and every LOAD "" failed 0:1).
        byte expectedFlag = (byte)(Cpu.AF >> 8);
        bool isLoad = (Cpu.AF & 1) != 0;
        ushort dest = Cpu.IX;
        ushort remaining = Cpu.DE;
        ushort returnAddr = (ushort)(Memory.Read(Cpu.SP) | (Memory.Read((ushort)(Cpu.SP + 1)) << 8));
        FastLoadDiag?.Invoke($"trap: PC=0556 A={expectedFlag:X2} CF={(isLoad?1:0)} IX={dest:X4} DE={remaining} ret={returnAddr:X4} SP={Cpu.SP:X4}");

        if (!Tape!.TryReadNextBlock(out byte flag, out ReadOnlySpan<byte> payload, out byte checksum))
        {
            FastLoadDiag?.Invoke("trap: no more blocks -> CF=0");
            SetCarry(false); PopReturn();
            return (int)(Cpu.TStates - tStart);
        }
        if (flag != expectedFlag)
        {
            FastLoadDiag?.Invoke($"trap: flag mismatch got={flag:X2} want={expectedFlag:X2} -> CF=0");
            SetCarry(false); PopReturn();
            return (int)(Cpu.TStates - tStart);
        }

        // Copy min(DE, payload.Length) bytes into memory, and XOR the whole
        // block for checksum verification the way LD-BYTES does — the ROM
        // continues clocking bytes past DE=0 for the sole purpose of getting
        // to the checksum. Undershoot (DE > payload.Length) is a short-block
        // failure: the real ROM would time out mid-byte.
        // Copy min(DE, payload.Length) bytes into memory, and XOR the whole
        // block for checksum verification the way LD-BYTES does — the ROM
        // continues clocking bytes past DE=0 for the sole purpose of getting
        // to the checksum. Undershoot (DE > payload.Length) is a short-block
        // failure: the real ROM would time out mid-byte.
        //
        // NOTE on VERIFY (CF'=0): the ROM's LD-CONTRL / LD-LOOK-H uses VERIFY
        // as its header-scan primitive — it wants LD-BYTES to _read_ the
        // header block into memory anyway so it can inspect the program name
        // and type, then decide whether to load the data block. Skipping the
        // write on VERIFY breaks LOAD "" completely. We always write.
        int toCopy = Math.Min(remaining, payload.Length);
        byte chk = flag;
        for (int i = 0; i < toCopy; i++)
        {
            Memory.Write((ushort)(dest + i), payload[i]);
            chk ^= payload[i];
        }
        for (int i = toCopy; i < payload.Length; i++) chk ^= payload[i];
        _ = isLoad; // kept for future VERIFY behaviour once we distinguish LD-LOOK-H

        Cpu.IX = (ushort)(dest + toCopy);
        Cpu.DE = (ushort)(remaining - toCopy);
        bool ok = (chk == checksum) && (remaining <= payload.Length);
        SetCarry(ok);
        FastLoadDiag?.Invoke($"trap: flag={flag:X2} payload={payload.Length} copied={toCopy} chk={chk:X2} expChk={checksum:X2} ok={ok}");

        // A real 48K load runs at ~44 T-states per data bit averaged with
        // pilot/sync overhead — call it ~44 T/byte over the whole block so
        // things timed off the tape don't leap forward instantly.
        Cpu.TStates += (payload.Length + 2) * 44L;

        PopReturn();
        return (int)(Cpu.TStates - tStart);
    }

    /// <summary>Optional diagnostic sink for fast-load trap events.</summary>
    public Action<string>? FastLoadDiag { get; set; }

    private void SetCarry(bool c)
    {
        Cpu.F = (byte)(c ? (Cpu.F | Z80.FlagC) : (Cpu.F & ~Z80.FlagC));
    }

    private void PopReturn()
    {
        byte lo = Memory.Read(Cpu.SP);
        byte hi = Memory.Read((ushort)(Cpu.SP + 1));
        Cpu.SP += 2;
        Cpu.PC = (ushort)((hi << 8) | lo);
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

    // Anchors the current frame's T-state 0 so we can hand Ula a frame-local
    // T-state on every OUT 0xFE (needed for per-scanline border stripes).
    private long _frameStartT;

    public void Out(ushort port, byte value)
    {
        if ((port & 1) == 0)
        {
            int frameT = (int)(Cpu.TStates - _frameStartT);
            if (frameT < 0) frameT = 0;
            Ula.RecordBorderAt(frameT, value);
            Ula.Out(port, value);
            return;
        }
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
