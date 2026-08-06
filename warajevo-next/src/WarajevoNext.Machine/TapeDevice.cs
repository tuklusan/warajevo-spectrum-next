// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// TAP tape loader. Feeds edges to the EAR bit (0x40 on port 0xFE) using
// standard Spectrum pulse timings. A fast-load option is provided that
// bypasses the ROM loading routine and dumps blocks straight into memory
// via the standard ROM entry-point trap (0x0556 LD-BYTES).
//
// TAP format (Wikipedia): sequence of {2-byte length} {length bytes} blocks.
// First byte of each block is the flag (0x00 header, 0xFF data). Last byte
// is the XOR checksum.
// ============================================================================
using WarajevoNext.Cpu;

namespace WarajevoNext.Machine;

public sealed class TapeDevice
{
    private byte[]? _tap;
    private int _blockPtr;   // start of current block in _tap
    private int _blockLen;
    private int _dataPtr;    // byte within block
    private int _bitPtr;     // bit within byte (7..0)
    private int _pulseTicks; // T-states remaining in current pulse level
    private bool _pulseHigh;
    private State _state;
    private long _cpuCounter;
    private enum State { Idle, PilotPulse, Sync1, Sync2, DataPulse1, DataPulse2, InterBlockGap }

    // Standard timings (T-states @ 3.5 MHz)
    private const int PilotPulseT = 2168;
    private const int PilotPulsesHeader = 8063;
    private const int PilotPulsesData = 3223;
    private const int Sync1T = 667;
    private const int Sync2T = 735;
    private const int Bit0T = 855;
    private const int Bit1T = 1710;
    private const int TailT = 945;
    // 1-second silence between tape blocks so the ROM's LOAD "" can finish
    // processing block N (print "Program: name", allocate buffers, ...) before
    // block N+1's pilot arrives. Real cassettes usually have ~1-2 seconds
    // of silence here. Without this gap the tape ran past the ROM.
    private const int InterBlockGapT = 3500000;

    private int _pilotPulsesLeft;

    public bool IsPlaying { get; private set; }
    public byte CurrentEar => (byte)(_pulseHigh ? 0x40 : 0x00);
    public int Blocks { get; private set; }

    // ---------------------------------------------------------------------
    // Warajevo-style ROM LD-EDGE-1 trap state machine.
    //
    // Rather than simulate cycle-accurate pulse edges (fragile, and needs
    // the CPU's IN A,(FE) to happen at exactly the right T-state), we
    // trap the ROM's LD-EDGE-1 routine at 0x05E7 and hand the caller a
    // synthetic result: B register set to a "long pulse" value on the
    // right beats, CF=1 always so the ROM never times out. This mirrors
    // Warajevo 2.50's TAPE.ASM (LEADER/SYNC/BITS at line 1901+): each
    // trap fire advances the tape-edge counter one step and returns.
    //
    // ROM's LD-BYTES outer loop then constructs bytes bit-by-bit exactly
    // as if a real cassette were feeding pulses - so border stripes render
    // naturally (ROM's own OUT (FE),A calls run), and the outer LOAD ""
    // sequences all four blocks correctly.
    // ---------------------------------------------------------------------
    private enum EdgePhase { Prep, Leader, Sync, Bits, Done }
    private EdgePhase _edgePhase = EdgePhase.Prep;
    private int _edgeCnt;        // remaining edges in current phase
    private int _edgeBitCnt;     // bits left in current byte
    private byte _edgeByte;      // current byte being clocked out
    private int _edgeDataPtr;    // absolute _tap[] index of next data byte
    private int _edgeDataEnd;    // one-past-end of current block data
    private byte _edgeBitSide;   // toggles 0/1 per edge - two edges per bit
    private byte _lastEdgeB;     // last B value returned to ROM

    /// <summary>
    /// Called by SpectrumMachine when PC lands on ROM 0x05E7 (LD-EDGE-1).
    /// LITERAL port of Warajevo 2.50 TAPE.ASM LEADER / SYNC / BITS state
    /// machine (lines 1901-1937). Semantics:
    ///   setB=true  -> caller writes bReturn into Cpu.B (Warajevo mov B,255)
    ///   setB=false -> Cpu.B UNCHANGED (Warajevo leaves B as whatever the
    ///                 caller of LD-EDGE-1 set it to)
    /// </summary>
    public bool TryHandleLdEdgeTrap(out bool setB, out byte bReturn, out byte borderColour)
    {
        setB = false; bReturn = 0xFF; borderColour = 0x02;
        if (_tap == null) return false;

        switch (_edgePhase)
        {
            case EdgePhase.Prep:
                // Warajevo TAPE.ASM ED_PREP (line 1996): open next block,
                // set EDGE_CNT from CUR_FLAG bit 7 (header vs data), move
                // state to LEADER. GETTAPE line 1587 sets EDGE_CNT=8064
                // for header (flag<0x80) or 3220 for data (flag>=0x80).
                if (_blockPtr + 2 > _tap.Length) { setB = false; borderColour = 0; return false; }
                _blockLen = _tap[_blockPtr] | (_tap[_blockPtr + 1] << 8);
                if (_blockLen < 2) return false;
                _edgeDataPtr = _blockPtr + 2;
                _edgeDataEnd = _edgeDataPtr + _blockLen;
                byte flag = _tap[_edgeDataPtr];
                _edgeCnt = flag < 0x80 ? 8064 : 3220;
                _edgePhase = EdgePhase.Leader;
                // Fall through to Leader for this call.
                goto case EdgePhase.Leader;

            case EdgePhase.Leader:
                // Warajevo LEADER (line 1901):
                //   mov B,255              ; set B for caller
                //   mov LDCNT,30           ; (not modelled - we don't count polling ticks)
                //   dec EDGE_CNT
                //   jnz LEADEND
                //   mov EDGE_CNT,2
                //   mov EDGE_ADDR,offset SYNC
                // LEADEND: jmp EDGE_OK
                // NOT 0xFF: LD-EDGE-2 falls through to a second LD-EDGE-1
                // whose INC B (0x05ED) would wrap 0xFF -> 0, hit RET Z at
                // 0x05EE and return CF=0 (failure) BEFORE reaching our
                // trap at 0x05F1. Set to a value >0xC6 (pilot threshold)
                // but well below 0xFF so INC B stays positive.
                setB = true; bReturn = 0xE8;
                borderColour = (_edgeCnt & 1) != 0 ? (byte)0x02 : (byte)0x05;  // red/cyan pilot stripe
                CurrentEdgeTStates = PilotPulseT;  // 2168 T-states = real pilot pulse
                _edgeCnt--;
                if (_edgeCnt <= 0)
                {
                    _edgeCnt = 2;
                    _edgePhase = EdgePhase.Sync;
                }
                return true;

            case EdgePhase.Sync:
                // Warajevo SYNC (line 1909):
                //   mov LDCNT,10
                //   dec EDGE_CNT
                //   jnz EDGE_OK           ; B unchanged
                //   mov EDGE_ADDR,offset BITS
                //   mov EDGE_TYP,0
                //   mov EDGE_PTR,0
                //   jmp EDGE_OK           ; B unchanged
                setB = false;                                       // Warajevo does NOT touch B in sync
                borderColour = (_edgeCnt & 1) != 0 ? (byte)0x02 : (byte)0x05;
                // Sync pulses: first is 667 T-states, second is 735 T-states
                CurrentEdgeTStates = (_edgeCnt == 2) ? Sync1T : Sync2T;
                _edgeCnt--;
                if (_edgeCnt <= 0)
                {
                    _edgePhase = EdgePhase.Bits;
                    _edgeBitSide = 0;   // Warajevo EDGE_TYP=0
                    _edgeBitCnt = 0;    // will trigger byte fetch on first BITS call
                }
                return true;

            case EdgePhase.Bits:
                // Warajevo BITS (line 1917):
                //   mov LDCNT,10
                //   xor EDGE_TYP,255       ; toggle 0<->255
                //   jnz EDGE_OK            ; first edge of a bit: B unchanged
                //   ; second edge:
                //   cmp EDGE_CNT,0
                //   jnz EDGE_NEXT          ; still bits in current byte
                //   dec EDGE_LEN
                //   cmp EDGE_LEN,-1
                //   je EDGE_BAD            ; end of block
                //   mov EDGE_BYT, [buf++]  ; fetch next byte
                //   mov EDGE_CNT,8
                // EDGE_NEXT:
                //   dec EDGE_CNT
                //   shl EDGE_BYT,1         ; extract top bit into CF
                //   jnc EDGE_OK            ; bit 0: B unchanged
                //   mov B,255              ; bit 1: set B=255
                _edgeBitSide ^= 1;   // XOR EDGE_TYP,255 (toggles 0<->1)
                if (_edgeBitSide != 0)
                {
                    // First edge of a bit: don't touch B, don't process a bit.
                    // But set edge duration based on what the current bit
                    // WOULD be (peek MSB of current byte) so ROM sees
                    // matching pulse widths on both halves of the bit.
                    setB = false;
                    borderColour = 0x06;  // yellow (data first edge)
                    bool peekBit = _edgeBitCnt > 0 && (_edgeByte & 0x80) != 0;
                    CurrentEdgeTStates = peekBit ? Bit1T : Bit0T;
                    return true;
                }
                // Second edge: process the next bit.
                if (_edgeBitCnt == 0)
                {
                    if (_edgeDataPtr >= _edgeDataEnd)
                    {
                        // EDGE_BAD equivalent: end of block. Advance to
                        // next block or stop tape.
                        _blockPtr = _edgeDataEnd;
                        CurrentBlock++;
                        if (_blockPtr >= _tap.Length)
                        {
                            IsPlaying = false;
                            _edgePhase = EdgePhase.Done;
                            setB = false; borderColour = 0;
                            return true;
                        }
                        _edgePhase = EdgePhase.Prep;
                        // Re-enter Prep on next call. For THIS call, mimic
                        // Warajevo EDGE_BAD: leave B alone, exit.
                        setB = false; borderColour = 0;
                        return true;
                    }
                    _edgeByte = _tap[_edgeDataPtr++];
                    _edgeBitCnt = 8;
                }
                // EDGE_NEXT: shl EDGE_BYT,1 - top bit goes into CF.
                bool bit = (_edgeByte & 0x80) != 0;
                _edgeByte = (byte)(_edgeByte << 1);
                _edgeBitCnt--;
                if (bit)
                {
                    setB = true; bReturn = 0xE8;    // 0xE8 not 0xFF (INC B safe)
                    borderColour = 0x01;             // blue (bit 1)
                    CurrentEdgeTStates = Bit1T;      // 1710 T-states
                }
                else
                {
                    setB = false;                    // B unchanged
                    borderColour = 0x00;             // black (bit 0)
                    CurrentEdgeTStates = Bit0T;      // 855 T-states
                }
                return true;

            case EdgePhase.Done:
                setB = false; bReturn = 0; borderColour = 0;
                return false;
        }
        return false;
    }

    /// <summary>Reset the edge state machine so the next 0x05E7 trap starts fresh at the current block.</summary>
    public void ResetEdgeMachine()
    {
        _edgePhase = EdgePhase.Prep;
        _edgeCnt = 0;
        _edgeBitCnt = 0;
        _edgeBitSide = 0;
    }
    public int CurrentBlock { get; private set; }

    // Diagnostics
    public string StateName => _state.ToString();
    public int PilotPulsesLeft => _pilotPulsesLeft;
    public int DataPtr => _dataPtr - _blockPtr - 2;
    public int BlockLen => _blockLen;
    public string EdgePhaseName => _edgePhase.ToString();
    public int EdgeCnt => _edgeCnt;
    public int EdgeBitCnt => _edgeBitCnt;
    public int EdgeDataPos => _edgeDataPtr - _blockPtr - 2;
    // Duration of the LAST edge reported by TryHandleLdEdgeTrap (T-states).
    // Used by the trap caller to charge Cpu.TStates so tape playback runs
    // at real-Spectrum wall clock rather than emulator-CPU speed.
    public int CurrentEdgeTStates { get; private set; } = PilotPulseT;

    public void LoadTap(byte[] data)
    {
        _tap = data;
        _blockPtr = 0;
        Blocks = 0;
        int p = 0;
        while (p + 2 <= data.Length)
        {
            int len = data[p] | (data[p + 1] << 8);
            Blocks++;
            p += 2 + len;
            if (p > data.Length) break;
        }
        CurrentBlock = 0;
        Rewind();
    }

    public void Rewind()
    {
        _blockPtr = 0;
        CurrentBlock = 0;
        IsPlaying = false;
        _state = State.Idle;
        _pulseHigh = false;
    }

    public void Play() { if (_tap != null && _blockPtr < _tap.Length) { StartBlock(); IsPlaying = true; } }
    public void Stop() => IsPlaying = false;

    /// <summary>
    /// Snap the current block's pulse machinery back to the very start of
    /// its pilot. Called by SpectrumMachine on the first PC=0x0556 landing
    /// so the ROM's LD-BYTES sees a fresh, full pilot rather than whatever
    /// mid-pilot state the tape had drifted to while the machine sat at the
    /// BASIC READY prompt. Mirrors "user press Play on tape" cassette
    /// behaviour: pulses only start streaming when the loader is listening.
    /// </summary>
    public void SyncToBlockStart()
    {
        if (_tap == null || _blockPtr + 2 > _tap.Length) return;
        StartBlock();
        IsPlaying = true;
    }

    private void StartBlock()
    {
        if (_tap == null || _blockPtr + 2 > _tap.Length) { IsPlaying = false; return; }
        _blockLen = _tap[_blockPtr] | (_tap[_blockPtr + 1] << 8);
        _dataPtr = _blockPtr + 2;
        _bitPtr = 7;
        byte flag = _tap[_dataPtr];
        _pilotPulsesLeft = flag < 0x80 ? PilotPulsesHeader : PilotPulsesData;
        _state = State.PilotPulse;
        // Real tape convention (matches FUSE / SpecEmu / most emulators): EAR
        // bit STARTS HIGH just before the pilot leader begins, and each
        // subsequent edge toggles it. ROM's LD-EDGE-1 reads bit 6 through
        // an RRA which puts it into CF, and its polling loop expects the
        // signal to already be present when it starts sampling.
        _pulseHigh = true;
        _pulseTicks = PilotPulseT;
    }

    /// <summary>Advance the tape by <paramref name="tstates"/> and flip EAR at pulse edges.</summary>
    public void Tick(int tstates)
    {
        if (!IsPlaying || _tap == null) return;
        _cpuCounter += tstates;
        while (tstates > 0)
        {
            int step = Math.Min(tstates, _pulseTicks);
            _pulseTicks -= step;
            tstates -= step;
            if (_pulseTicks == 0) NextEdge();
        }
    }

    private void NextEdge()
    {
        _pulseHigh = !_pulseHigh;
        switch (_state)
        {
            case State.PilotPulse:
                _pilotPulsesLeft--;
                if (_pilotPulsesLeft <= 0) { _state = State.Sync1; _pulseTicks = Sync1T; }
                else _pulseTicks = PilotPulseT;
                break;
            case State.Sync1:
                _state = State.Sync2; _pulseTicks = Sync2T; break;
            case State.Sync2:
                _state = State.DataPulse1; SetBitTime(); break;
            case State.DataPulse1:
                _state = State.DataPulse2; SetBitTime(); break;
            case State.DataPulse2:
                _bitPtr--;
                if (_bitPtr < 0) { _bitPtr = 7; _dataPtr++; }
                if (_dataPtr - _blockPtr - 2 >= _blockLen)
                {
                    // End of block; move to inter-block gap, THEN start next
                    _blockPtr += 2 + _blockLen;
                    CurrentBlock++;
                    if (_blockPtr >= (_tap?.Length ?? 0)) { IsPlaying = false; _state = State.Idle; return; }
                    _state = State.InterBlockGap;
                    _pulseTicks = InterBlockGapT;
                    // Hold the EAR line low during silence so any polling
                    // loop reads "no signal" rather than a stuck-high edge.
                    _pulseHigh = false;
                }
                else { _state = State.DataPulse1; SetBitTime(); }
                break;
            case State.InterBlockGap:
                // Gap timed out - now really start the next block's pilot.
                _state = State.Idle;
                StartBlock();
                break;
            case State.Idle:
                _pulseTicks = 1000; break;
        }
    }

    private void SetBitTime()
    {
        int bit = (_tap![_dataPtr] >> _bitPtr) & 1;
        _pulseTicks = bit == 1 ? Bit1T : Bit0T;
    }

    // ------------------------------------------------------------------
    // Fast-load support. The 0x0556 ROM trap calls this to grab the next
    // block ready for direct memory copy, bypassing the edge machinery.
    // Layout of one TAP block: [len-lo][len-hi][flag][data...][xor].
    //   `flag`     - first byte of the block (0x00 header, 0xFF data)
    //   `payload`  - the bytes between the flag and the trailing XOR
    //                (this is exactly what the ROM would deposit in RAM)
    //   `checksum` - the trailing XOR byte; the block is valid when
    //                (flag XOR all-payload) == checksum.
    // On success the block pointer advances past this block; IsPlaying
    // clears when the tape is exhausted.
    // ------------------------------------------------------------------
    public bool TryReadNextBlock(out byte flag, out ReadOnlySpan<byte> payload, out byte checksum)
    {
        flag = 0; payload = default; checksum = 0;
        if (_tap == null) return false;
        if (_blockPtr + 2 > _tap.Length) return false;
        int len = _tap[_blockPtr] | (_tap[_blockPtr + 1] << 8);
        if (len < 2) return false;                     // need at least flag+checksum
        int start = _blockPtr + 2;
        if (start + len > _tap.Length) return false;   // truncated block
        flag = _tap[start];
        checksum = _tap[start + len - 1];
        payload = new ReadOnlySpan<byte>(_tap, start + 1, len - 2);
        _blockPtr += 2 + len;
        CurrentBlock++;
        if (_blockPtr >= _tap.Length) { IsPlaying = false; _state = State.Idle; return true; }
        // Resync the pulse-edge state machine on the new block so that any
        // subsequent Tick() calls (e.g. from SpectrumMachine.RunFrame after
        // the fast-load trap has returned) emit pilot / sync / data pulses
        // for the block AFTER the one we just fast-loaded, not for a stale
        // position mid-way through the block we already consumed.
        StartBlock();
        return true;
    }
}
