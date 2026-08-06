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
        while (Cpu.TStates < target)
        {
            StepOnce();
            // Feed the tape pulse machinery by exactly the T-states the
            // last step consumed - but ONLY when the fast-load trap is
            // disabled. With fast-load on the trap adds ~44 T-states/byte
            // artificially per call (up to 180K for a 4 KB block), and if
            // Tick sees that inflated delta it blasts the newly-started
            // next-block's pilot pulses out of the tape state machine
            // before the ROM ever gets a chance to sample them. Fast-load
            // consumes blocks itself via TryReadNextBlock; the edge state
            // machine is not needed in that mode.
            if (Tape != null && Tape.IsPlaying && !FastLoad)
            {
                long now = Cpu.TStates;
                int delta = (int)(now - prev);
                if (delta > 0) Tape.Tick(delta);
                prev = now;
            }
            else
            {
                prev = Cpu.TStates;
            }
        }
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
            // Warajevo-style LD-EDGE-1 trap. Fires only when fast-load is
            // OFF and we recognise the routine. Handles both header pilot
            // and data-bit clocking in one place; ROM's outer LD-BYTES
            // runs its natural border-stripe OUT (FE) loop between calls.
            else if (!FastLoad && Cpu.PC == 0x05E7 && IsSpectrum48LdEdge())
            {
                return HandleLdEdgeTrap();
            }
        }
        return Cpu.Step();
    }

    // Sinclair 48K ROM at LD-EDGE-1 (0x05E7):
    //   05E7: 3E 16       LD A,16h
    //   05E9: 3D          DEC A
    //   05EA: 20 FD       JR NZ,05E9
    //   05EC: A7          AND A
    //   05ED: 04          INC B
    // Six bytes is enough to distinguish from any custom loader that
    // happens to sit at the same address in RAM.
    private static readonly byte[] LdEdgeSig = { 0x3E, 0x16, 0x3D, 0x20, 0xFD, 0xA7 };

    private bool IsSpectrum48LdEdge()
    {
        for (int i = 0; i < LdEdgeSig.Length; i++)
            if (Memory.Read((ushort)(0x05E7 + i)) != LdEdgeSig[i]) return false;
        return true;
    }

    private int HandleLdEdgeTrap()
    {
        long tStart = Cpu.TStates;
        if (!Tape!.TryHandleLdEdgeTrap(out byte bReturn, out byte borderColour))
        {
            // No more edges - report failure so ROM times out gracefully.
            Cpu.B = 0;
            SetCarry(false);
            PopReturn();
            return (int)(Cpu.TStates - tStart);
        }
        // Update border with the requested colour (produces classic stripes).
        int frameT = (int)(Cpu.TStates - _frameStartT);
        if (frameT < 0) frameT = 0;
        Ula.RecordBorderAt(frameT, borderColour);
        Ula.Out(0x00FE, borderColour);
        // Charge ROM-realistic time for this edge so the tape and CPU
        // stay in step. Real LD-EDGE-1 takes ~200 T-states on a short
        // pulse, ~2200 on a long pilot pulse; use a middle value that
        // preserves the ~50 Hz frame illusion.
        Cpu.TStates += 200L;
        Cpu.B = bReturn;
        SetCarry(true);
        PopReturn();
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
