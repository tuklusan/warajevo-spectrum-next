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
    private enum State { Idle, PilotPulse, Sync1, Sync2, DataPulse1, DataPulse2 }

    // Standard timings (T-states @ 3.5 MHz)
    private const int PilotPulseT = 2168;
    private const int PilotPulsesHeader = 8063;
    private const int PilotPulsesData = 3223;
    private const int Sync1T = 667;
    private const int Sync2T = 735;
    private const int Bit0T = 855;
    private const int Bit1T = 1710;
    private const int TailT = 945;

    private int _pilotPulsesLeft;

    public bool IsPlaying { get; private set; }
    public byte CurrentEar => (byte)(_pulseHigh ? 0x40 : 0x00);
    public int Blocks { get; private set; }
    public int CurrentBlock { get; private set; }

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

    private void StartBlock()
    {
        if (_tap == null || _blockPtr + 2 > _tap.Length) { IsPlaying = false; return; }
        _blockLen = _tap[_blockPtr] | (_tap[_blockPtr + 1] << 8);
        _dataPtr = _blockPtr + 2;
        _bitPtr = 7;
        byte flag = _tap[_dataPtr];
        _pilotPulsesLeft = flag < 0x80 ? PilotPulsesHeader : PilotPulsesData;
        _state = State.PilotPulse;
        _pulseHigh = false;
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
                    // End of block; move to next
                    _blockPtr += 2 + _blockLen;
                    CurrentBlock++;
                    if (_blockPtr >= (_tap?.Length ?? 0)) { IsPlaying = false; _state = State.Idle; return; }
                    _state = State.Idle;
                    _pulseTicks = TailT;
                    StartBlock();
                }
                else { _state = State.DataPulse1; SetBitTime(); }
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
