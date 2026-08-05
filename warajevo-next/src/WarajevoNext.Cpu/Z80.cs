// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Original Warajevo (c) Zeljko Juric and Samir Ribic (GPL, Feb 2006).
// This C# port is a derivative work released under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// Z80 CPU interpreter for Warajevo Next.
//
// Design notes:
//   - Registers held as byte pairs (A/F, B/C, D/E, H/L) and their shadows,
//     plus IX/IY (16-bit), SP/PC/WZ (16-bit), I, R (with bit 7 preserved
//     across INC R), IFF1/IFF2, IM, HALT.
//   - Flags include the undocumented X (bit 3) and Y (bit 5) bits, and
//     MEMPTR/WZ is maintained for CCF/SCF, BIT n,(HL) and block instructions.
//   - Every memory and I/O access is charged through _bus.Read/Write /
//     _io.In/Out, and internal "MC" cycles are charged via Tick(n).
//     The FUSE test suite expects total T-state counts to match.
//   - Undocumented instructions covered: DDCB/FDCB, IXH/IXL/IYH/IYL halves,
//     CB SLL (0x30 block), duplicate ED opcodes and ED NONI/NOP for reserved
//     slots, and OUT (C),0 for the classic Z80 (not the CMOS variant).
// ============================================================================
using System;
using System.Runtime.CompilerServices;

namespace WarajevoNext.Cpu;

public sealed class Z80
{
    // ---- flag bits --------------------------------------------------------
    public const byte FlagC = 0x01;
    public const byte FlagN = 0x02;
    public const byte FlagP = 0x04; // parity / overflow
    public const byte FlagX = 0x08; // undoc, copy of bit 3
    public const byte FlagH = 0x10;
    public const byte FlagY = 0x20; // undoc, copy of bit 5
    public const byte FlagZ = 0x40;
    public const byte FlagS = 0x80;

    // ---- registers --------------------------------------------------------
    public byte A, F, B, C, D, E, H, L;
    public byte A_, F_, B_, C_, D_, E_, H_, L_;
    public ushort IX, IY, SP, PC, WZ;
    public byte I, R;
    public bool IFF1, IFF2;
    public byte IM;
    public bool Halted;
    // R is a 7-bit counter with bit 7 preserved.
    private byte _rHigh; // holds bit 7

    // interrupt pending
    private bool _intRequested;
    private byte _intData;

    private readonly IMemoryBus _bus;
    private readonly IIoBus _io;

    // running T-state counter, mutable by the machine (used to compare against
    // the FUSE expected totals)
    public long TStates;

    // Optional bus-contention hook. Null by default so the FUSE conformance
    // suite (FlatMemory + NullIo) keeps its exact T-state totals; the machine
    // layer installs a SpectrumContentionModel when running a real 48K/128K.
    public IContentionModel? Contention;

    // Convenience 16-bit register accessors (little-endian: low = C, high = B).
    public ushort AF { get => (ushort)((A << 8) | F); set { A = (byte)(value >> 8); F = (byte)value; } }
    public ushort BC { get => (ushort)((B << 8) | C); set { B = (byte)(value >> 8); C = (byte)value; } }
    public ushort DE { get => (ushort)((D << 8) | E); set { D = (byte)(value >> 8); E = (byte)value; } }
    public ushort HL { get => (ushort)((H << 8) | L); set { H = (byte)(value >> 8); L = (byte)value; } }
    public ushort AF_ { get => (ushort)((A_ << 8) | F_); set { A_ = (byte)(value >> 8); F_ = (byte)value; } }
    public ushort BC_ { get => (ushort)((B_ << 8) | C_); set { B_ = (byte)(value >> 8); C_ = (byte)value; } }
    public ushort DE_ { get => (ushort)((D_ << 8) | E_); set { D_ = (byte)(value >> 8); E_ = (byte)value; } }
    public ushort HL_ { get => (ushort)((H_ << 8) | L_); set { H_ = (byte)(value >> 8); L_ = (byte)value; } }

    // ---- precomputed flag tables -----------------------------------------
    private static readonly byte[] _sz53p = new byte[256];
    private static readonly byte[] _sz53 = new byte[256];
    private static readonly byte[] _parity = new byte[256];

    static Z80()
    {
        for (int i = 0; i < 256; i++)
        {
            byte f = (byte)(i & (FlagS | FlagY | FlagX));
            if (i == 0) f |= FlagZ;
            _sz53[i] = f;
            int p = i;
            p ^= p >> 4; p ^= p >> 2; p ^= p >> 1;
            _parity[i] = (byte)(((p & 1) == 0) ? FlagP : 0);
            _sz53p[i] = (byte)(_sz53[i] | _parity[i]);
        }
    }

    public Z80(IMemoryBus bus, IIoBus io)
    {
        _bus = bus;
        _io = io;
    }

    // Called by machine at power-on.
    public void Reset()
    {
        PC = 0; I = 0; R = 0; _rHigh = 0;
        IFF1 = IFF2 = false;
        IM = 0;
        Halted = false;
        // Z80 reset does not touch general registers on real hardware, but
        // it clears the flip-flops; on the Spectrum SP is typically set to
        // 0xFFFF by the ROM soon after.
    }

    public void RequestInterrupt(byte busData = 0xFF)
    {
        _intRequested = true;
        _intData = busData;
    }

    // ---- bus helpers with cycle accounting ------------------------------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Tick(int n) => TStates += n;

    // Charge any ULA-imposed extra delay BEFORE the natural cost of the bus
    // cycle. When no model is installed these are no-ops and the T-state
    // totals collapse back to the plain Z80 timings the FUSE tests expect.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ContendM(ushort addr)
    {
        if (Contention != null) TStates += Contention.ContendMemory((int)TStates, addr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ContendIO(ushort port)
    {
        if (Contention != null) TStates += Contention.ContendIo((int)TStates, port);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadByte(ushort addr) { ContendM(addr); Tick(3); return _bus.Read(addr); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteByte(ushort addr, byte val) { ContendM(addr); Tick(3); _bus.Write(addr, val); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte FetchOpcode()
    {
        // opcode fetch is 4 T-states, and increments R.
        ContendM(PC);
        byte b = _bus.Read(PC);
        Tick(4);
        PC++;
        R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
        return b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte FetchByte() { ContendM(PC); byte b = _bus.Read(PC); Tick(3); PC++; return b; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort FetchWord() { byte lo = FetchByte(); byte hi = FetchByte(); return (ushort)((hi << 8) | lo); }

    private byte InPort(ushort port)
    {
        // IN r,(C) and IN A,(n) are 11 T-states total; the opcode fetches
        // already account for 4, and the port I/O takes 4 more T-states
        // (1 for address setup, 3 for read).
        ContendIO(port);
        Tick(4);
        return _io.In(port);
    }

    private void OutPort(ushort port, byte val)
    {
        ContendIO(port);
        Tick(4);
        _io.Out(port, val);
    }

    private byte GetR() => (byte)((R & 0x7F) | (_rHigh & 0x80));
    private void SetR(byte v) { R = (byte)(v & 0x7F); _rHigh = (byte)(v & 0x80); }

    // ============================================================
    // Execute one instruction, return T-states consumed for that
    // instruction (delta on TStates).
    // ============================================================
    public int Step()
    {
        long start = TStates;

        if (_intRequested && IFF1)
        {
            _intRequested = false;
            AcceptInterrupt(_intData);
            return (int)(TStates - start);
        }

        if (Halted)
        {
            // Executing a NOP in-place; wait for an interrupt.
            R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
            Tick(4);
            return (int)(TStates - start);
        }

        byte op = FetchOpcode();
        ExecuteMain(op);
        return (int)(TStates - start);
    }

    private void AcceptInterrupt(byte busData)
    {
        if (Halted) { Halted = false; PC++; }
        IFF1 = IFF2 = false;
        R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
        switch (IM)
        {
            case 0:
                // Treat like IM 1 for simplicity if bus data isn't a real opcode.
                Tick(7);
                Push(PC);
                PC = 0x0038;
                WZ = PC;
                break;
            case 1:
                Tick(7);
                Push(PC);
                PC = 0x0038;
                WZ = PC;
                break;
            case 2:
                Tick(7);
                Push(PC);
                ushort vec = (ushort)((I << 8) | busData);
                ContendM(vec); byte lo = _bus.Read(vec); Tick(3);
                ContendM((ushort)(vec + 1)); byte hi = _bus.Read((ushort)(vec + 1)); Tick(3);
                PC = (ushort)((hi << 8) | lo);
                WZ = PC;
                break;
        }
    }

    // ---- flag helpers ----------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetFlag(byte flag, bool cond) { if (cond) F |= flag; else F &= (byte)~flag; }

    private byte Add8(byte a, byte b, byte carryIn)
    {
        int sum = a + b + carryIn;
        int res = sum & 0xFF;
        byte f = (byte)(_sz53[res] & (FlagS | FlagZ | FlagY | FlagX));
        if (((a ^ b ^ res) & 0x10) != 0) f |= FlagH;
        if ((((a ^ ~b) & (a ^ res)) & 0x80) != 0) f |= FlagP;
        if (sum > 0xFF) f |= FlagC;
        F = f;
        return (byte)res;
    }

    private byte Sub8(byte a, byte b, byte carryIn)
    {
        int diff = a - b - carryIn;
        int res = diff & 0xFF;
        byte f = (byte)(_sz53[res] & (FlagS | FlagZ | FlagY | FlagX));
        f |= FlagN;
        if (((a ^ b ^ res) & 0x10) != 0) f |= FlagH;
        if ((((a ^ b) & (a ^ res)) & 0x80) != 0) f |= FlagP;
        if (diff < 0) f |= FlagC;
        F = f;
        return (byte)res;
    }

    private void Cp8(byte a, byte b)
    {
        int diff = a - b;
        int res = diff & 0xFF;
        byte f = (byte)(res & FlagS);
        if (res == 0) f |= FlagZ;
        f |= (byte)(b & (FlagY | FlagX));  // CP takes YF/XF from the operand, not the result
        f |= FlagN;
        if (((a ^ b ^ res) & 0x10) != 0) f |= FlagH;
        if ((((a ^ b) & (a ^ res)) & 0x80) != 0) f |= FlagP;
        if (diff < 0) f |= FlagC;
        F = f;
    }

    private byte Inc8(byte v)
    {
        byte res = (byte)(v + 1);
        byte f = (byte)(F & FlagC);
        f |= (byte)(_sz53[res] & (FlagS | FlagZ | FlagY | FlagX));
        if ((v & 0x0F) == 0x0F) f |= FlagH;
        if (v == 0x7F) f |= FlagP;
        F = f;
        return res;
    }

    private byte Dec8(byte v)
    {
        byte res = (byte)(v - 1);
        byte f = (byte)(F & FlagC);
        f |= FlagN;
        f |= (byte)(_sz53[res] & (FlagS | FlagZ | FlagY | FlagX));
        if ((v & 0x0F) == 0x00) f |= FlagH;
        if (v == 0x80) f |= FlagP;
        F = f;
        return res;
    }

    private void And8(byte v)
    {
        A &= v;
        F = (byte)(_sz53p[A] | FlagH);
    }
    private void Xor8(byte v)
    {
        A ^= v;
        F = _sz53p[A];
    }
    private void Or8(byte v)
    {
        A |= v;
        F = _sz53p[A];
    }

    private ushort Add16(ushort a, ushort b)
    {
        int sum = a + b;
        WZ = (ushort)(a + 1);
        byte f = (byte)(F & (FlagS | FlagZ | FlagP));
        if (((a ^ b ^ sum) & 0x1000) != 0) f |= FlagH;
        if (sum > 0xFFFF) f |= FlagC;
        f |= (byte)((sum >> 8) & (FlagY | FlagX));
        F = f;
        return (ushort)sum;
    }

    private ushort Adc16(ushort a, ushort b)
    {
        int carry = (F & FlagC) != 0 ? 1 : 0;
        int sum = a + b + carry;
        int res = sum & 0xFFFF;
        WZ = (ushort)(a + 1);
        byte f = 0;
        if ((res & 0x8000) != 0) f |= FlagS;
        if (res == 0) f |= FlagZ;
        if (((a ^ b ^ sum) & 0x1000) != 0) f |= FlagH;
        if ((((a ^ ~b) & (a ^ sum)) & 0x8000) != 0) f |= FlagP;
        if (sum > 0xFFFF) f |= FlagC;
        f |= (byte)((res >> 8) & (FlagY | FlagX));
        F = f;
        return (ushort)res;
    }

    private ushort Sbc16(ushort a, ushort b)
    {
        int carry = (F & FlagC) != 0 ? 1 : 0;
        int diff = a - b - carry;
        int res = diff & 0xFFFF;
        WZ = (ushort)(a + 1);
        byte f = FlagN;
        if ((res & 0x8000) != 0) f |= FlagS;
        if (res == 0) f |= FlagZ;
        if (((a ^ b ^ diff) & 0x1000) != 0) f |= FlagH;
        if ((((a ^ b) & (a ^ diff)) & 0x8000) != 0) f |= FlagP;
        if (diff < 0) f |= FlagC;
        f |= (byte)((res >> 8) & (FlagY | FlagX));
        F = f;
        return (ushort)res;
    }

    // rotates and shifts
    private byte Rlc(byte v)
    {
        byte c = (byte)((v >> 7) & 1);
        byte r = (byte)((v << 1) | c);
        F = (byte)(_sz53p[r] | c);
        return r;
    }
    private byte Rrc(byte v)
    {
        byte c = (byte)(v & 1);
        byte r = (byte)((v >> 1) | (c << 7));
        F = (byte)(_sz53p[r] | c);
        return r;
    }
    private byte Rl(byte v)
    {
        byte oldC = (byte)(F & FlagC);
        byte newC = (byte)((v >> 7) & 1);
        byte r = (byte)((v << 1) | oldC);
        F = (byte)(_sz53p[r] | newC);
        return r;
    }
    private byte Rr(byte v)
    {
        byte oldC = (byte)(F & FlagC);
        byte newC = (byte)(v & 1);
        byte r = (byte)((v >> 1) | (oldC << 7));
        F = (byte)(_sz53p[r] | newC);
        return r;
    }
    private byte Sla(byte v)
    {
        byte c = (byte)((v >> 7) & 1);
        byte r = (byte)(v << 1);
        F = (byte)(_sz53p[r] | c);
        return r;
    }
    private byte Sra(byte v)
    {
        byte c = (byte)(v & 1);
        byte r = (byte)((v >> 1) | (v & 0x80));
        F = (byte)(_sz53p[r] | c);
        return r;
    }
    private byte Sll(byte v) // undocumented
    {
        byte c = (byte)((v >> 7) & 1);
        byte r = (byte)((v << 1) | 1);
        F = (byte)(_sz53p[r] | c);
        return r;
    }
    private byte Srl(byte v)
    {
        byte c = (byte)(v & 1);
        byte r = (byte)(v >> 1);
        F = (byte)(_sz53p[r] | c);
        return r;
    }

    // BIT n, r
    private void Bit(int n, byte v, ushort addrForYX)
    {
        // For BIT n,(HL) and BIT n,(IX+d)/(IY+d) the Y/X flags come from
        // the high byte of the address that was read (WZ), not from the value.
        byte res = (byte)(v & (1 << n));
        byte f = (byte)(F & FlagC);
        f |= FlagH;
        if (res == 0) f |= (byte)(FlagZ | FlagP);
        if (n == 7 && res != 0) f |= FlagS;
        f |= (byte)((addrForYX >> 8) & (FlagY | FlagX));
        F = f;
    }

    private void BitReg(int n, byte v)
    {
        byte res = (byte)(v & (1 << n));
        byte f = (byte)(F & FlagC);
        f |= FlagH;
        if (res == 0) f |= (byte)(FlagZ | FlagP);
        if (n == 7 && res != 0) f |= FlagS;
        f |= (byte)(v & (FlagY | FlagX));
        F = f;
    }

    // Push and pop
    private void Push(ushort v)
    {
        SP--; WriteByte(SP, (byte)(v >> 8));
        SP--; WriteByte(SP, (byte)v);
    }
    private ushort Pop()
    {
        byte lo = ReadByte(SP); SP++;
        byte hi = ReadByte(SP); SP++;
        return (ushort)((hi << 8) | lo);
    }

    private void JumpRelative(sbyte d)
    {
        PC = (ushort)(PC + d);
        WZ = PC;
    }

    // ============================================================
    // Main opcode dispatch (0x00 - 0xFF)
    // ============================================================
    private void ExecuteMain(byte op)
    {
        switch (op)
        {
            // ---- 0x00..0x0F ----------------------------------------
            case 0x00: break; // NOP
            case 0x01: BC = FetchWord(); break;
            case 0x02: WriteByte(BC, A); WZ = (ushort)(((BC + 1) & 0xFF) | (A << 8)); break;
            case 0x03: Tick(2); BC++; break;
            case 0x04: B = Inc8(B); break;
            case 0x05: B = Dec8(B); break;
            case 0x06: B = FetchByte(); break;
            case 0x07: { // RLCA
                byte c = (byte)((A >> 7) & 1);
                A = (byte)((A << 1) | c);
                F = (byte)((F & (FlagS | FlagZ | FlagP)) | c | (A & (FlagY | FlagX)));
                break; }
            case 0x08: { var t = AF; AF = AF_; AF_ = t; break; } // EX AF,AF'
            case 0x09: HL = Add16(HL, BC); Tick(7); break;
            case 0x0A: A = ReadByte(BC); WZ = (ushort)(BC + 1); break;
            case 0x0B: Tick(2); BC--; break;
            case 0x0C: C = Inc8(C); break;
            case 0x0D: C = Dec8(C); break;
            case 0x0E: C = FetchByte(); break;
            case 0x0F: { // RRCA
                byte c = (byte)(A & 1);
                A = (byte)((A >> 1) | (c << 7));
                F = (byte)((F & (FlagS | FlagZ | FlagP)) | c | (A & (FlagY | FlagX)));
                break; }

            // ---- 0x10..0x1F ----------------------------------------
            case 0x10: { // DJNZ d
                Tick(1);
                sbyte d = (sbyte)FetchByte();
                B--;
                if (B != 0) { Tick(5); JumpRelative(d); }
                break; }
            case 0x11: DE = FetchWord(); break;
            case 0x12: WriteByte(DE, A); WZ = (ushort)(((DE + 1) & 0xFF) | (A << 8)); break;
            case 0x13: Tick(2); DE++; break;
            case 0x14: D = Inc8(D); break;
            case 0x15: D = Dec8(D); break;
            case 0x16: D = FetchByte(); break;
            case 0x17: { // RLA
                byte c = (byte)((A >> 7) & 1);
                A = (byte)((A << 1) | (F & FlagC));
                F = (byte)((F & (FlagS | FlagZ | FlagP)) | c | (A & (FlagY | FlagX)));
                break; }
            case 0x18: { sbyte d = (sbyte)FetchByte(); Tick(5); JumpRelative(d); break; }
            case 0x19: HL = Add16(HL, DE); Tick(7); break;
            case 0x1A: A = ReadByte(DE); WZ = (ushort)(DE + 1); break;
            case 0x1B: Tick(2); DE--; break;
            case 0x1C: E = Inc8(E); break;
            case 0x1D: E = Dec8(E); break;
            case 0x1E: E = FetchByte(); break;
            case 0x1F: { // RRA
                byte c = (byte)(A & 1);
                A = (byte)((A >> 1) | ((F & FlagC) << 7));
                F = (byte)((F & (FlagS | FlagZ | FlagP)) | c | (A & (FlagY | FlagX)));
                break; }

            // ---- 0x20..0x2F ----------------------------------------
            case 0x20: { sbyte d = (sbyte)FetchByte(); if ((F & FlagZ) == 0) { Tick(5); JumpRelative(d); } break; }
            case 0x21: HL = FetchWord(); break;
            case 0x22: { ushort a = FetchWord(); WriteByte(a, L); WriteByte((ushort)(a + 1), H); WZ = (ushort)(a + 1); break; }
            case 0x23: Tick(2); HL++; break;
            case 0x24: H = Inc8(H); break;
            case 0x25: H = Dec8(H); break;
            case 0x26: H = FetchByte(); break;
            case 0x27: Daa(); break;
            case 0x28: { sbyte d = (sbyte)FetchByte(); if ((F & FlagZ) != 0) { Tick(5); JumpRelative(d); } break; }
            case 0x29: HL = Add16(HL, HL); Tick(7); break;
            case 0x2A: { ushort a = FetchWord(); L = ReadByte(a); H = ReadByte((ushort)(a + 1)); WZ = (ushort)(a + 1); break; }
            case 0x2B: Tick(2); HL--; break;
            case 0x2C: L = Inc8(L); break;
            case 0x2D: L = Dec8(L); break;
            case 0x2E: L = FetchByte(); break;
            case 0x2F: A = (byte)~A; F = (byte)((F & (FlagS | FlagZ | FlagP | FlagC)) | FlagH | FlagN | (A & (FlagY | FlagX))); break;

            // ---- 0x30..0x3F ----------------------------------------
            case 0x30: { sbyte d = (sbyte)FetchByte(); if ((F & FlagC) == 0) { Tick(5); JumpRelative(d); } break; }
            case 0x31: SP = FetchWord(); break;
            case 0x32: { ushort a = FetchWord(); WriteByte(a, A); WZ = (ushort)(((a + 1) & 0xFF) | (A << 8)); break; }
            case 0x33: Tick(2); SP++; break;
            case 0x34: { byte v = ReadByte(HL); Tick(1); v = Inc8(v); WriteByte(HL, v); break; }
            case 0x35: { byte v = ReadByte(HL); Tick(1); v = Dec8(v); WriteByte(HL, v); break; }
            case 0x36: { byte v = FetchByte(); WriteByte(HL, v); break; }
            case 0x37: // SCF
                F = (byte)((F & (FlagS | FlagZ | FlagP)) | FlagC | (A & (FlagY | FlagX)));
                break;
            case 0x38: { sbyte d = (sbyte)FetchByte(); if ((F & FlagC) != 0) { Tick(5); JumpRelative(d); } break; }
            case 0x39: HL = Add16(HL, SP); Tick(7); break;
            case 0x3A: { ushort a = FetchWord(); A = ReadByte(a); WZ = (ushort)(a + 1); break; }
            case 0x3B: Tick(2); SP--; break;
            case 0x3C: A = Inc8(A); break;
            case 0x3D: A = Dec8(A); break;
            case 0x3E: A = FetchByte(); break;
            case 0x3F: // CCF
                {
                    byte oldC = (byte)(F & FlagC);
                    F = (byte)((F & (FlagS | FlagZ | FlagP)) | (oldC != 0 ? FlagH : 0) | (oldC ^ FlagC) | (A & (FlagY | FlagX)));
                    break;
                }

            // ---- 0x40..0x7F: LD r,r' and LD r,(HL) / LD (HL),r + HALT ----
            case 0x40: break; // LD B,B
            case 0x41: B = C; break;
            case 0x42: B = D; break;
            case 0x43: B = E; break;
            case 0x44: B = H; break;
            case 0x45: B = L; break;
            case 0x46: B = ReadByte(HL); break;
            case 0x47: B = A; break;
            case 0x48: C = B; break;
            case 0x49: break;
            case 0x4A: C = D; break;
            case 0x4B: C = E; break;
            case 0x4C: C = H; break;
            case 0x4D: C = L; break;
            case 0x4E: C = ReadByte(HL); break;
            case 0x4F: C = A; break;
            case 0x50: D = B; break;
            case 0x51: D = C; break;
            case 0x52: break;
            case 0x53: D = E; break;
            case 0x54: D = H; break;
            case 0x55: D = L; break;
            case 0x56: D = ReadByte(HL); break;
            case 0x57: D = A; break;
            case 0x58: E = B; break;
            case 0x59: E = C; break;
            case 0x5A: E = D; break;
            case 0x5B: break;
            case 0x5C: E = H; break;
            case 0x5D: E = L; break;
            case 0x5E: E = ReadByte(HL); break;
            case 0x5F: E = A; break;
            case 0x60: H = B; break;
            case 0x61: H = C; break;
            case 0x62: H = D; break;
            case 0x63: H = E; break;
            case 0x64: break;
            case 0x65: H = L; break;
            case 0x66: H = ReadByte(HL); break;
            case 0x67: H = A; break;
            case 0x68: L = B; break;
            case 0x69: L = C; break;
            case 0x6A: L = D; break;
            case 0x6B: L = E; break;
            case 0x6C: L = H; break;
            case 0x6D: break;
            case 0x6E: L = ReadByte(HL); break;
            case 0x6F: L = A; break;
            case 0x70: WriteByte(HL, B); break;
            case 0x71: WriteByte(HL, C); break;
            case 0x72: WriteByte(HL, D); break;
            case 0x73: WriteByte(HL, E); break;
            case 0x74: WriteByte(HL, H); break;
            case 0x75: WriteByte(HL, L); break;
            case 0x76: Halted = true; PC--; break; // HALT
            case 0x77: WriteByte(HL, A); break;
            case 0x78: A = B; break;
            case 0x79: A = C; break;
            case 0x7A: A = D; break;
            case 0x7B: A = E; break;
            case 0x7C: A = H; break;
            case 0x7D: A = L; break;
            case 0x7E: A = ReadByte(HL); break;
            case 0x7F: break;

            // ---- 0x80..0xBF: ADD/ADC/SUB/SBC/AND/XOR/OR/CP r ----
            case 0x80: A = Add8(A, B, 0); break;
            case 0x81: A = Add8(A, C, 0); break;
            case 0x82: A = Add8(A, D, 0); break;
            case 0x83: A = Add8(A, E, 0); break;
            case 0x84: A = Add8(A, H, 0); break;
            case 0x85: A = Add8(A, L, 0); break;
            case 0x86: A = Add8(A, ReadByte(HL), 0); break;
            case 0x87: A = Add8(A, A, 0); break;
            case 0x88: A = Add8(A, B, (byte)(F & FlagC)); break;
            case 0x89: A = Add8(A, C, (byte)(F & FlagC)); break;
            case 0x8A: A = Add8(A, D, (byte)(F & FlagC)); break;
            case 0x8B: A = Add8(A, E, (byte)(F & FlagC)); break;
            case 0x8C: A = Add8(A, H, (byte)(F & FlagC)); break;
            case 0x8D: A = Add8(A, L, (byte)(F & FlagC)); break;
            case 0x8E: A = Add8(A, ReadByte(HL), (byte)(F & FlagC)); break;
            case 0x8F: A = Add8(A, A, (byte)(F & FlagC)); break;
            case 0x90: A = Sub8(A, B, 0); break;
            case 0x91: A = Sub8(A, C, 0); break;
            case 0x92: A = Sub8(A, D, 0); break;
            case 0x93: A = Sub8(A, E, 0); break;
            case 0x94: A = Sub8(A, H, 0); break;
            case 0x95: A = Sub8(A, L, 0); break;
            case 0x96: A = Sub8(A, ReadByte(HL), 0); break;
            case 0x97: A = Sub8(A, A, 0); break;
            case 0x98: A = Sub8(A, B, (byte)(F & FlagC)); break;
            case 0x99: A = Sub8(A, C, (byte)(F & FlagC)); break;
            case 0x9A: A = Sub8(A, D, (byte)(F & FlagC)); break;
            case 0x9B: A = Sub8(A, E, (byte)(F & FlagC)); break;
            case 0x9C: A = Sub8(A, H, (byte)(F & FlagC)); break;
            case 0x9D: A = Sub8(A, L, (byte)(F & FlagC)); break;
            case 0x9E: A = Sub8(A, ReadByte(HL), (byte)(F & FlagC)); break;
            case 0x9F: A = Sub8(A, A, (byte)(F & FlagC)); break;
            case 0xA0: And8(B); break;
            case 0xA1: And8(C); break;
            case 0xA2: And8(D); break;
            case 0xA3: And8(E); break;
            case 0xA4: And8(H); break;
            case 0xA5: And8(L); break;
            case 0xA6: And8(ReadByte(HL)); break;
            case 0xA7: And8(A); break;
            case 0xA8: Xor8(B); break;
            case 0xA9: Xor8(C); break;
            case 0xAA: Xor8(D); break;
            case 0xAB: Xor8(E); break;
            case 0xAC: Xor8(H); break;
            case 0xAD: Xor8(L); break;
            case 0xAE: Xor8(ReadByte(HL)); break;
            case 0xAF: Xor8(A); break;
            case 0xB0: Or8(B); break;
            case 0xB1: Or8(C); break;
            case 0xB2: Or8(D); break;
            case 0xB3: Or8(E); break;
            case 0xB4: Or8(H); break;
            case 0xB5: Or8(L); break;
            case 0xB6: Or8(ReadByte(HL)); break;
            case 0xB7: Or8(A); break;
            case 0xB8: Cp8(A, B); break;
            case 0xB9: Cp8(A, C); break;
            case 0xBA: Cp8(A, D); break;
            case 0xBB: Cp8(A, E); break;
            case 0xBC: Cp8(A, H); break;
            case 0xBD: Cp8(A, L); break;
            case 0xBE: Cp8(A, ReadByte(HL)); break;
            case 0xBF: Cp8(A, A); break;

            // ---- 0xC0..0xFF ----------------------------------------
            case 0xC0: Tick(1); if ((F & FlagZ) == 0) { PC = Pop(); WZ = PC; } break;
            case 0xC1: BC = Pop(); break;
            case 0xC2: { ushort a = FetchWord(); WZ = a; if ((F & FlagZ) == 0) PC = a; break; }
            case 0xC3: { ushort a = FetchWord(); PC = a; WZ = a; break; }
            case 0xC4: { ushort a = FetchWord(); WZ = a; if ((F & FlagZ) == 0) { Tick(1); Push(PC); PC = a; } break; }
            case 0xC5: Tick(1); Push(BC); break;
            case 0xC6: A = Add8(A, FetchByte(), 0); break;
            case 0xC7: Tick(1); Push(PC); PC = 0x00; WZ = 0; break;
            case 0xC8: Tick(1); if ((F & FlagZ) != 0) { PC = Pop(); WZ = PC; } break;
            case 0xC9: PC = Pop(); WZ = PC; break;
            case 0xCA: { ushort a = FetchWord(); WZ = a; if ((F & FlagZ) != 0) PC = a; break; }
            case 0xCB: ExecuteCB(); break;
            case 0xCC: { ushort a = FetchWord(); WZ = a; if ((F & FlagZ) != 0) { Tick(1); Push(PC); PC = a; } break; }
            case 0xCD: { ushort a = FetchWord(); WZ = a; Tick(1); Push(PC); PC = a; break; }
            case 0xCE: A = Add8(A, FetchByte(), (byte)(F & FlagC)); break;
            case 0xCF: Tick(1); Push(PC); PC = 0x08; WZ = 0x08; break;
            case 0xD0: Tick(1); if ((F & FlagC) == 0) { PC = Pop(); WZ = PC; } break;
            case 0xD1: DE = Pop(); break;
            case 0xD2: { ushort a = FetchWord(); WZ = a; if ((F & FlagC) == 0) PC = a; break; }
            case 0xD3: { byte n = FetchByte(); ushort port = (ushort)((A << 8) | n); OutPort(port, A); WZ = (ushort)((port + 1) & 0xFF | (A << 8)); break; }
            case 0xD4: { ushort a = FetchWord(); WZ = a; if ((F & FlagC) == 0) { Tick(1); Push(PC); PC = a; } break; }
            case 0xD5: Tick(1); Push(DE); break;
            case 0xD6: A = Sub8(A, FetchByte(), 0); break;
            case 0xD7: Tick(1); Push(PC); PC = 0x10; WZ = 0x10; break;
            case 0xD8: Tick(1); if ((F & FlagC) != 0) { PC = Pop(); WZ = PC; } break;
            case 0xD9: { // EXX
                var t = BC; BC = BC_; BC_ = t;
                t = DE; DE = DE_; DE_ = t;
                t = HL; HL = HL_; HL_ = t; break; }
            case 0xDA: { ushort a = FetchWord(); WZ = a; if ((F & FlagC) != 0) PC = a; break; }
            case 0xDB: { // IN A,(n)
                byte n = FetchByte();
                ushort port = (ushort)((A << 8) | n);
                A = InPort(port);
                WZ = (ushort)(port + 1);
                break; }
            case 0xDC: { ushort a = FetchWord(); WZ = a; if ((F & FlagC) != 0) { Tick(1); Push(PC); PC = a; } break; }
            case 0xDD: ExecuteIndex(ref IX); break;
            case 0xDE: A = Sub8(A, FetchByte(), (byte)(F & FlagC)); break;
            case 0xDF: Tick(1); Push(PC); PC = 0x18; WZ = 0x18; break;
            case 0xE0: Tick(1); if ((F & FlagP) == 0) { PC = Pop(); WZ = PC; } break;
            case 0xE1: HL = Pop(); break;
            case 0xE2: { ushort a = FetchWord(); WZ = a; if ((F & FlagP) == 0) PC = a; break; }
            case 0xE3: { // EX (SP),HL
                byte lo = ReadByte(SP);
                byte hi = ReadByte((ushort)(SP + 1));
                Tick(1);
                WriteByte((ushort)(SP + 1), H);
                WriteByte(SP, L);
                Tick(2);
                L = lo; H = hi;
                WZ = HL;
                break; }
            case 0xE4: { ushort a = FetchWord(); WZ = a; if ((F & FlagP) == 0) { Tick(1); Push(PC); PC = a; } break; }
            case 0xE5: Tick(1); Push(HL); break;
            case 0xE6: And8(FetchByte()); break;
            case 0xE7: Tick(1); Push(PC); PC = 0x20; WZ = 0x20; break;
            case 0xE8: Tick(1); if ((F & FlagP) != 0) { PC = Pop(); WZ = PC; } break;
            case 0xE9: PC = HL; break;
            case 0xEA: { ushort a = FetchWord(); WZ = a; if ((F & FlagP) != 0) PC = a; break; }
            case 0xEB: { var t = DE; DE = HL; HL = t; break; }
            case 0xEC: { ushort a = FetchWord(); WZ = a; if ((F & FlagP) != 0) { Tick(1); Push(PC); PC = a; } break; }
            case 0xED: ExecuteED(); break;
            case 0xEE: Xor8(FetchByte()); break;
            case 0xEF: Tick(1); Push(PC); PC = 0x28; WZ = 0x28; break;
            case 0xF0: Tick(1); if ((F & FlagS) == 0) { PC = Pop(); WZ = PC; } break;
            case 0xF1: AF = Pop(); break;
            case 0xF2: { ushort a = FetchWord(); WZ = a; if ((F & FlagS) == 0) PC = a; break; }
            case 0xF3: IFF1 = IFF2 = false; break;
            case 0xF4: { ushort a = FetchWord(); WZ = a; if ((F & FlagS) == 0) { Tick(1); Push(PC); PC = a; } break; }
            case 0xF5: Tick(1); Push(AF); break;
            case 0xF6: Or8(FetchByte()); break;
            case 0xF7: Tick(1); Push(PC); PC = 0x30; WZ = 0x30; break;
            case 0xF8: Tick(1); if ((F & FlagS) != 0) { PC = Pop(); WZ = PC; } break;
            case 0xF9: SP = HL; Tick(2); break;
            case 0xFA: { ushort a = FetchWord(); WZ = a; if ((F & FlagS) != 0) PC = a; break; }
            case 0xFB: IFF1 = IFF2 = true; break;
            case 0xFC: { ushort a = FetchWord(); WZ = a; if ((F & FlagS) != 0) { Tick(1); Push(PC); PC = a; } break; }
            case 0xFD: ExecuteIndex(ref IY); break;
            case 0xFE: Cp8(A, FetchByte()); break;
            case 0xFF: Tick(1); Push(PC); PC = 0x38; WZ = 0x38; break;
        }
    }

    private void Daa()
    {
        int a = A;
        int correction = 0;
        int carry = F & FlagC;
        int half = F & FlagH;
        int neg = F & FlagN;
        if (half != 0 || (a & 0x0F) > 9) correction |= 0x06;
        if (carry != 0 || a > 0x99) { correction |= 0x60; carry = FlagC; }
        int newA;
        if (neg != 0) newA = a - correction;
        else newA = a + correction;
        newA &= 0xFF;
        byte f = (byte)(_sz53p[newA] | neg | carry);
        if (((a ^ newA) & 0x10) != 0) f |= FlagH;
        A = (byte)newA;
        F = f;
    }

    // ============================================================
    // CB-prefixed rotates/shifts/bits
    // ============================================================
    private void ExecuteCB()
    {
        byte op = FetchOpcode();
        int reg = op & 7;
        byte v = reg switch
        {
            0 => B, 1 => C, 2 => D, 3 => E, 4 => H, 5 => L,
            6 => ReadByte(HL),
            _ => A
        };
        if (reg == 6) Tick(1);

        int fam = op >> 6;
        int idx = (op >> 3) & 7;
        byte res;
        if (fam == 0)
        {
            res = idx switch
            {
                0 => Rlc(v),
                1 => Rrc(v),
                2 => Rl(v),
                3 => Rr(v),
                4 => Sla(v),
                5 => Sra(v),
                6 => Sll(v),
                _ => Srl(v)
            };
            if (reg == 6) WriteByte(HL, res);
            else Assign(reg, res);
        }
        else if (fam == 1)
        {
            if (reg == 6) BitReg(idx, v);
            else BitReg(idx, v);
            return;
        }
        else if (fam == 2)
        {
            res = (byte)(v & ~(1 << idx));
            if (reg == 6) WriteByte(HL, res);
            else Assign(reg, res);
        }
        else
        {
            res = (byte)(v | (1 << idx));
            if (reg == 6) WriteByte(HL, res);
            else Assign(reg, res);
        }
    }

    private void Assign(int reg, byte v)
    {
        switch (reg) { case 0: B = v; break; case 1: C = v; break; case 2: D = v; break;
                       case 3: E = v; break; case 4: H = v; break; case 5: L = v; break;
                       case 7: A = v; break; }
    }

    // ============================================================
    // DD/FD prefix — IX/IY family
    // ============================================================
    private void ExecuteIndex(ref ushort ix)
    {
        byte op = FetchOpcode();
        switch (op)
        {
            case 0x09: ix = Add16(ix, BC); Tick(7); return;
            case 0x19: ix = Add16(ix, DE); Tick(7); return;
            case 0x21: ix = FetchWord(); return;
            case 0x22: { ushort a = FetchWord(); WriteByte(a, (byte)ix); WriteByte((ushort)(a + 1), (byte)(ix >> 8)); WZ = (ushort)(a + 1); return; }
            case 0x23: Tick(2); ix++; return;
            case 0x24: ix = (ushort)((Inc8((byte)(ix >> 8)) << 8) | (ix & 0xFF)); return;
            case 0x25: ix = (ushort)((Dec8((byte)(ix >> 8)) << 8) | (ix & 0xFF)); return;
            case 0x26: ix = (ushort)((FetchByte() << 8) | (ix & 0xFF)); return;
            case 0x29: ix = Add16(ix, ix); Tick(7); return;
            case 0x2A: { ushort a = FetchWord(); byte lo = ReadByte(a); byte hi = ReadByte((ushort)(a + 1)); ix = (ushort)((hi << 8) | lo); WZ = (ushort)(a + 1); return; }
            case 0x2B: Tick(2); ix--; return;
            case 0x2C: ix = (ushort)((ix & 0xFF00) | Inc8((byte)ix)); return;
            case 0x2D: ix = (ushort)((ix & 0xFF00) | Dec8((byte)ix)); return;
            case 0x2E: ix = (ushort)((ix & 0xFF00) | FetchByte()); return;
            case 0x34: { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea; byte v = ReadByte(ea); Tick(1); v = Inc8(v); WriteByte(ea, v); return; }
            case 0x35: { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea; byte v = ReadByte(ea); Tick(1); v = Dec8(v); WriteByte(ea, v); return; }
            case 0x36: { sbyte d = (sbyte)FetchByte(); byte v = FetchByte(); Tick(2); ushort ea = (ushort)(ix + d); WZ = ea; WriteByte(ea, v); return; }
            case 0x39: ix = Add16(ix, SP); Tick(7); return;

            case 0x44: B = (byte)(ix >> 8); return;
            case 0x45: B = (byte)ix; return;
            case 0x4C: C = (byte)(ix >> 8); return;
            case 0x4D: C = (byte)ix; return;
            case 0x54: D = (byte)(ix >> 8); return;
            case 0x55: D = (byte)ix; return;
            case 0x5C: E = (byte)(ix >> 8); return;
            case 0x5D: E = (byte)ix; return;
            case 0x60: ix = (ushort)((B << 8) | (ix & 0xFF)); return;
            case 0x61: ix = (ushort)((C << 8) | (ix & 0xFF)); return;
            case 0x62: ix = (ushort)((D << 8) | (ix & 0xFF)); return;
            case 0x63: ix = (ushort)((E << 8) | (ix & 0xFF)); return;
            case 0x64: return; // LD IXH,IXH
            case 0x65: ix = (ushort)(((ix & 0xFF) << 8) | (ix & 0xFF)); return;
            case 0x67: ix = (ushort)((A << 8) | (ix & 0xFF)); return;
            case 0x68: ix = (ushort)((ix & 0xFF00) | B); return;
            case 0x69: ix = (ushort)((ix & 0xFF00) | C); return;
            case 0x6A: ix = (ushort)((ix & 0xFF00) | D); return;
            case 0x6B: ix = (ushort)((ix & 0xFF00) | E); return;
            case 0x6C: ix = (ushort)((ix & 0xFF00) | (ix >> 8)); return;
            case 0x6D: return; // LD IXL,IXL
            case 0x6F: ix = (ushort)((ix & 0xFF00) | A); return;

            case 0x46: case 0x4E: case 0x56: case 0x5E: case 0x66: case 0x6E: case 0x7E:
                { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea; byte v = ReadByte(ea);
                  switch (op) { case 0x46: B = v; break; case 0x4E: C = v; break; case 0x56: D = v; break;
                                case 0x5E: E = v; break; case 0x66: H = v; break; case 0x6E: L = v; break;
                                case 0x7E: A = v; break; } return; }
            case 0x70: case 0x71: case 0x72: case 0x73: case 0x74: case 0x75: case 0x77:
                { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea;
                  byte v = op switch { 0x70 => B, 0x71 => C, 0x72 => D, 0x73 => E,
                                       0x74 => H, 0x75 => L, _ => A };
                  WriteByte(ea, v); return; }

            case 0x7C: A = (byte)(ix >> 8); return;
            case 0x7D: A = (byte)ix; return;

            case 0x84: A = Add8(A, (byte)(ix >> 8), 0); return;
            case 0x85: A = Add8(A, (byte)ix, 0); return;
            case 0x86: { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea; A = Add8(A, ReadByte(ea), 0); return; }
            case 0x8C: A = Add8(A, (byte)(ix >> 8), (byte)(F & FlagC)); return;
            case 0x8D: A = Add8(A, (byte)ix, (byte)(F & FlagC)); return;
            case 0x8E: { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea; A = Add8(A, ReadByte(ea), (byte)(F & FlagC)); return; }
            case 0x94: A = Sub8(A, (byte)(ix >> 8), 0); return;
            case 0x95: A = Sub8(A, (byte)ix, 0); return;
            case 0x96: { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea; A = Sub8(A, ReadByte(ea), 0); return; }
            case 0x9C: A = Sub8(A, (byte)(ix >> 8), (byte)(F & FlagC)); return;
            case 0x9D: A = Sub8(A, (byte)ix, (byte)(F & FlagC)); return;
            case 0x9E: { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea; A = Sub8(A, ReadByte(ea), (byte)(F & FlagC)); return; }
            case 0xA4: And8((byte)(ix >> 8)); return;
            case 0xA5: And8((byte)ix); return;
            case 0xA6: { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea; And8(ReadByte(ea)); return; }
            case 0xAC: Xor8((byte)(ix >> 8)); return;
            case 0xAD: Xor8((byte)ix); return;
            case 0xAE: { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea; Xor8(ReadByte(ea)); return; }
            case 0xB4: Or8((byte)(ix >> 8)); return;
            case 0xB5: Or8((byte)ix); return;
            case 0xB6: { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea; Or8(ReadByte(ea)); return; }
            case 0xBC: Cp8(A, (byte)(ix >> 8)); return;
            case 0xBD: Cp8(A, (byte)ix); return;
            case 0xBE: { sbyte d = (sbyte)FetchByte(); Tick(5); ushort ea = (ushort)(ix + d); WZ = ea; Cp8(A, ReadByte(ea)); return; }

            case 0xE1: ix = Pop(); return;
            case 0xE3: { // EX (SP),IX
                byte lo = ReadByte(SP);
                byte hi = ReadByte((ushort)(SP + 1));
                Tick(1);
                WriteByte((ushort)(SP + 1), (byte)(ix >> 8));
                WriteByte(SP, (byte)ix);
                Tick(2);
                ix = (ushort)((hi << 8) | lo);
                WZ = ix;
                return; }
            case 0xE5: Tick(1); Push(ix); return;
            case 0xE9: PC = ix; return;
            case 0xF9: SP = ix; Tick(2); return;

            case 0xCB: ExecuteIndexCB(ref ix); return;

            default:
                // Unhandled DD/FD opcode: treat the prefix as a no-op prefix, execute the fetched
                // opcode normally. Do NOT rewind PC (that caused infinite recursion on chained
                // prefixes such as DD FD 00 00). R and PC were correctly advanced by FetchOpcode.
                ExecuteMain(op);
                return;
        }
    }

    private void ExecuteIndexCB(ref ushort ix)
    {
        // The displacement comes BEFORE the opcode for DDCB/FDCB. Both the
        // opcode fetch and the displacement fetch are done as normal memory
        // reads (no R increment for the CB slot's second byte here — the
        // way FUSE expects it is: PC bytes are opcode fetches only for the
        // first two prefix bytes, but here the displacement and opcode read
        // that follow are memory reads with an extra 2 T-states each).
        ContendM(PC); sbyte d = (sbyte)_bus.Read(PC); Tick(3); PC++;
        ContendM(PC); byte op = _bus.Read(PC); Tick(3); PC++;
        Tick(2);
        ushort ea = (ushort)(ix + d);
        WZ = ea;
        byte v = ReadByte(ea);
        Tick(1);
        int reg = op & 7;
        int fam = op >> 6;
        int idx = (op >> 3) & 7;
        byte res;
        if (fam == 0)
        {
            res = idx switch
            {
                0 => Rlc(v),
                1 => Rrc(v),
                2 => Rl(v),
                3 => Rr(v),
                4 => Sla(v),
                5 => Sra(v),
                6 => Sll(v),
                _ => Srl(v)
            };
            WriteByte(ea, res);
            if (reg != 6) Assign(reg, res);
        }
        else if (fam == 1)
        {
            Bit(idx, v, ea);
            return;
        }
        else if (fam == 2)
        {
            res = (byte)(v & ~(1 << idx));
            WriteByte(ea, res);
            if (reg != 6) Assign(reg, res);
        }
        else
        {
            res = (byte)(v | (1 << idx));
            WriteByte(ea, res);
            if (reg != 6) Assign(reg, res);
        }
    }

    // ============================================================
    // ED-prefix (misc, block instructions)
    // ============================================================
    private void ExecuteED()
    {
        byte op = FetchOpcode();
        switch (op)
        {
            case 0x40: case 0x48: case 0x50: case 0x58: case 0x60: case 0x68: case 0x70: case 0x78:
                { // IN r,(C)
                    ushort port = BC;
                    byte v = InPort(port);
                    WZ = (ushort)(port + 1);
                    F = (byte)((F & FlagC) | _sz53p[v]);
                    switch (op) { case 0x40: B = v; break; case 0x48: C = v; break; case 0x50: D = v; break;
                                  case 0x58: E = v; break; case 0x60: H = v; break; case 0x68: L = v; break;
                                  case 0x70: break; case 0x78: A = v; break; }
                    return; }
            case 0x41: case 0x49: case 0x51: case 0x59: case 0x61: case 0x69: case 0x71: case 0x79:
                { // OUT (C),r
                    ushort port = BC;
                    byte v = op switch { 0x41 => B, 0x49 => C, 0x51 => D, 0x59 => E,
                                         0x61 => H, 0x69 => L, 0x71 => 0, _ => A };
                    OutPort(port, v);
                    WZ = (ushort)(port + 1);
                    return; }
            case 0x42: HL = Sbc16(HL, BC); Tick(7); return;
            case 0x4A: HL = Adc16(HL, BC); Tick(7); return;
            case 0x52: HL = Sbc16(HL, DE); Tick(7); return;
            case 0x5A: HL = Adc16(HL, DE); Tick(7); return;
            case 0x62: HL = Sbc16(HL, HL); Tick(7); return;
            case 0x6A: HL = Adc16(HL, HL); Tick(7); return;
            case 0x72: HL = Sbc16(HL, SP); Tick(7); return;
            case 0x7A: HL = Adc16(HL, SP); Tick(7); return;
            case 0x43: { ushort a = FetchWord(); WriteByte(a, C); WriteByte((ushort)(a + 1), B); WZ = (ushort)(a + 1); return; }
            case 0x4B: { ushort a = FetchWord(); C = ReadByte(a); B = ReadByte((ushort)(a + 1)); WZ = (ushort)(a + 1); return; }
            case 0x53: { ushort a = FetchWord(); WriteByte(a, E); WriteByte((ushort)(a + 1), D); WZ = (ushort)(a + 1); return; }
            case 0x5B: { ushort a = FetchWord(); E = ReadByte(a); D = ReadByte((ushort)(a + 1)); WZ = (ushort)(a + 1); return; }
            case 0x63: { ushort a = FetchWord(); WriteByte(a, L); WriteByte((ushort)(a + 1), H); WZ = (ushort)(a + 1); return; }
            case 0x6B: { ushort a = FetchWord(); L = ReadByte(a); H = ReadByte((ushort)(a + 1)); WZ = (ushort)(a + 1); return; }
            case 0x73: { ushort a = FetchWord(); WriteByte(a, (byte)SP); WriteByte((ushort)(a + 1), (byte)(SP >> 8)); WZ = (ushort)(a + 1); return; }
            case 0x7B: { ushort a = FetchWord(); byte lo = ReadByte(a); byte hi = ReadByte((ushort)(a + 1)); SP = (ushort)((hi << 8) | lo); WZ = (ushort)(a + 1); return; }
            case 0x44: case 0x4C: case 0x54: case 0x5C: case 0x64: case 0x6C: case 0x74: case 0x7C:
                { // NEG
                    byte old = A; A = Sub8(0, old, 0);
                    return; }
            case 0x45: case 0x55: case 0x65: case 0x75: // RETN
                IFF1 = IFF2; PC = Pop(); WZ = PC; return;
            case 0x4D: case 0x5D: case 0x6D: case 0x7D: // RETI
                IFF1 = IFF2; PC = Pop(); WZ = PC; return;
            case 0x46: case 0x4E: case 0x66: case 0x6E: IM = 0; return;
            case 0x56: case 0x76: IM = 1; return;
            case 0x5E: case 0x7E: IM = 2; return;
            case 0x47: Tick(1); I = A; return;
            case 0x4F: Tick(1); R = A; return;
            case 0x57: { // LD A,I
                Tick(1); A = I;
                byte f = (byte)(F & FlagC);
                f |= (byte)(_sz53[A] & (FlagS | FlagZ | FlagY | FlagX));
                if (IFF2) f |= FlagP;
                F = f; return; }
            case 0x5F: { // LD A,R
                Tick(1); A = R;
                byte f = (byte)(F & FlagC);
                f |= (byte)(_sz53[A] & (FlagS | FlagZ | FlagY | FlagX));
                if (IFF2) f |= FlagP;
                F = f; return; }
            case 0x67: { // RRD
                byte m = ReadByte(HL); Tick(4);
                byte newM = (byte)(((A & 0x0F) << 4) | (m >> 4));
                byte newA = (byte)((A & 0xF0) | (m & 0x0F));
                WriteByte(HL, newM); A = newA;
                F = (byte)((F & FlagC) | _sz53p[A]);
                WZ = (ushort)(HL + 1);
                return; }
            case 0x6F: { // RLD
                byte m = ReadByte(HL); Tick(4);
                byte newM = (byte)((m << 4) | (A & 0x0F));
                byte newA = (byte)((A & 0xF0) | (m >> 4));
                WriteByte(HL, newM); A = newA;
                F = (byte)((F & FlagC) | _sz53p[A]);
                WZ = (ushort)(HL + 1);
                return; }
            // Block instructions
            case 0xA0: BlockLd(+1, false); return; // LDI
            case 0xA8: BlockLd(-1, false); return; // LDD
            case 0xB0: BlockLd(+1, true);  return; // LDIR
            case 0xB8: BlockLd(-1, true);  return; // LDDR
            case 0xA1: BlockCp(+1, false); return; // CPI
            case 0xA9: BlockCp(-1, false); return; // CPD
            case 0xB1: BlockCp(+1, true);  return; // CPIR
            case 0xB9: BlockCp(-1, true);  return; // CPDR
            case 0xA2: BlockIn(+1, false); return; // INI
            case 0xAA: BlockIn(-1, false); return; // IND
            case 0xB2: BlockIn(+1, true);  return; // INIR
            case 0xBA: BlockIn(-1, true);  return; // INDR
            case 0xA3: BlockOut(+1, false); return; // OUTI
            case 0xAB: BlockOut(-1, false); return; // OUTD
            case 0xB3: BlockOut(+1, true);  return; // OTIR
            case 0xBB: BlockOut(-1, true);  return; // OTDR
            default:
                // NONI (undefined ED opcode) — behaves as two-byte NOP
                return;
        }
    }

    private void BlockLd(int step, bool repeat)
    {
        byte v = ReadByte(HL);
        WriteByte(DE, v);
        Tick(2);
        DE = (ushort)(DE + step);
        HL = (ushort)(HL + step);
        BC--;
        byte n = (byte)(v + A);
        byte f = (byte)(F & (FlagS | FlagZ | FlagC));
        if ((n & 0x02) != 0) f |= FlagY;
        if ((n & 0x08) != 0) f |= FlagX;
        if (BC != 0) f |= FlagP;
        F = f;
        if (repeat && BC != 0)
        {
            Tick(5);
            PC = (ushort)(PC - 2);
            WZ = (ushort)(PC + 1);
        }
    }

    private void BlockCp(int step, bool repeat)
    {
        byte v = ReadByte(HL);
        Tick(5);
        byte oldC = (byte)(F & FlagC);
        int diff = A - v;
        byte res = (byte)(diff & 0xFF);
        byte f = (byte)(oldC | FlagN);
        if ((res & 0x80) != 0) f |= FlagS;
        if (res == 0) f |= FlagZ;
        if (((A ^ v ^ res) & 0x10) != 0) f |= FlagH;
        byte n = (byte)(res - ((f & FlagH) != 0 ? 1 : 0));
        if ((n & 0x02) != 0) f |= FlagY;
        if ((n & 0x08) != 0) f |= FlagX;
        HL = (ushort)(HL + step);
        BC--;
        if (BC != 0) f |= FlagP;
        F = f;
        if (repeat && BC != 0 && res != 0)
        {
            Tick(5);
            PC = (ushort)(PC - 2);
            WZ = (ushort)(PC + 1);
        }
        else
        {
            WZ = (ushort)(WZ + step);
        }
    }

    private void BlockIn(int step, bool repeat)
    {
        Tick(1);
        ushort port = BC;
        byte v = InPort(port);
        WriteByte(HL, v);
        WZ = (ushort)(BC + step);
        B--;
        HL = (ushort)(HL + step);
        byte f = 0;
        if ((v & 0x80) != 0) f |= FlagN;
        int k = (v + ((C + step) & 0xFF)) & 0xFF;
        if (k < v) f |= (byte)(FlagH | FlagC);
        f |= _parity[(byte)((k & 7) ^ B)];
        f |= (byte)(_sz53[B] & (FlagS | FlagZ | FlagY | FlagX));
        F = f;
        if (repeat && B != 0)
        {
            Tick(5);
            PC = (ushort)(PC - 2);
        }
    }

    private void BlockOut(int step, bool repeat)
    {
        Tick(1);
        byte v = ReadByte(HL);
        B--;
        ushort port = BC;
        OutPort(port, v);
        HL = (ushort)(HL + step);
        WZ = (ushort)(BC + step);
        byte f = 0;
        if ((v & 0x80) != 0) f |= FlagN;
        int k = (v + L) & 0xFF;
        if (k < v) f |= (byte)(FlagH | FlagC);
        f |= _parity[(byte)((k & 7) ^ B)];
        f |= (byte)(_sz53[B] & (FlagS | FlagZ | FlagY | FlagX));
        F = f;
        if (repeat && B != 0)
        {
            Tick(5);
            PC = (ushort)(PC - 2);
        }
    }
}
