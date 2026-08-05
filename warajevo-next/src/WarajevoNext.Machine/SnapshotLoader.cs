// ============================================================================
// Copyright (c) 2026 Supratim Sanyal (port) of SANYALnet Labs.
// Licensed under GNU GPL v3-or-later.
// ----------------------------------------------------------------------------
// Snapshot loaders. Two formats supported:
//   .SNA  — 48K variant (49179 bytes) and 128K variant (49181+ bytes).
//   .Z80  — v1 (compressed 48K), v2 and v3 (headered, 48K/128K).
// The loaders write directly into the machine's CPU + memory. They do NOT
// alter the caller's currently loaded ROM: an SNA/Z80 for the 128K expects
// the 128K ROMs to already be loaded.
// ============================================================================
using WarajevoNext.Cpu;

namespace WarajevoNext.Machine;

public static class SnapshotLoader
{
    public static void LoadSna(SpectrumMachine m, byte[] data)
    {
        if (data.Length < 49179) throw new ArgumentException("SNA too short");
        var cpu = m.Cpu;
        cpu.I = data[0];
        cpu.L_ = data[1]; cpu.H_ = data[2];
        cpu.E_ = data[3]; cpu.D_ = data[4];
        cpu.C_ = data[5]; cpu.B_ = data[6];
        cpu.F_ = data[7]; cpu.A_ = data[8];
        cpu.L = data[9]; cpu.H = data[10];
        cpu.E = data[11]; cpu.D = data[12];
        cpu.C = data[13]; cpu.B = data[14];
        cpu.IY = (ushort)(data[15] | (data[16] << 8));
        cpu.IX = (ushort)(data[17] | (data[18] << 8));
        bool iff = (data[19] & 0x04) != 0;
        cpu.IFF1 = iff; cpu.IFF2 = iff;
        cpu.R = data[20];
        cpu.F = data[21]; cpu.A = data[22];
        cpu.SP = (ushort)(data[23] | (data[24] << 8));
        cpu.IM = (byte)(data[25] & 3);
        // border colour data[26] — set on ULA
        m.Ula.Border = (byte)(data[26] & 7);
        // 48K RAM 0x4000..0xFFFF
        for (int i = 0; i < 0xC000; i++) m.Memory.Write((ushort)(0x4000 + i), data[27 + i]);

        if (m.Model == SpectrumModel.OneTwentyEight && data.Length >= 49181 + 3)
        {
            // 128K SNA: after the 49179 bytes:
            //   PC (2), port 0x7FFD (1), TR-DOS flag (1), then remaining banks.
            int off = 49179;
            cpu.PC = (ushort)(data[off] | (data[off + 1] << 8));
            byte port = data[off + 2];
            m.Memory.Write7FFD(port);
            // Data at 0x4000..0xFFFF above already loaded bank 5, 2, and current bank.
            // Remaining banks come after; SNA order: 0,1,3,4,6,7 minus (current, 5, 2).
            // We defer full-bank restore to a future upgrade — the common path
            // is 48K SNAs, and Z80 v2/v3 covers full 128K reliably.
        }
        else
        {
            // 48K: PC popped from stack
            ushort sp = cpu.SP;
            cpu.PC = (ushort)(m.Memory.Read(sp) | (m.Memory.Read((ushort)(sp + 1)) << 8));
            cpu.SP = (ushort)(sp + 2);
        }
    }

    public static void LoadZ80(SpectrumMachine m, byte[] d)
    {
        var cpu = m.Cpu;
        cpu.A = d[0]; cpu.F = d[1];
        cpu.C = d[2]; cpu.B = d[3];
        cpu.L = d[4]; cpu.H = d[5];
        ushort pc = (ushort)(d[6] | (d[7] << 8));
        cpu.SP = (ushort)(d[8] | (d[9] << 8));
        cpu.I = d[10];
        cpu.R = (byte)((d[11] & 0x7F) | ((d[12] & 1) << 7));
        byte b12 = d[12] == 0xFF ? (byte)1 : d[12];
        m.Ula.Border = (byte)((b12 >> 1) & 7);
        bool compressed = (b12 & 0x20) != 0;
        cpu.E = d[13]; cpu.D = d[14];
        cpu.C_ = d[15]; cpu.B_ = d[16];
        cpu.E_ = d[17]; cpu.D_ = d[18];
        cpu.L_ = d[19]; cpu.H_ = d[20];
        cpu.A_ = d[21]; cpu.F_ = d[22];
        cpu.IY = (ushort)(d[23] | (d[24] << 8));
        cpu.IX = (ushort)(d[25] | (d[26] << 8));
        cpu.IFF1 = d[27] != 0; cpu.IFF2 = d[28] != 0;
        cpu.IM = (byte)(d[29] & 3);

        if (pc != 0)
        {
            // v1 (48K compressed / raw)
            cpu.PC = pc;
            byte[] mem = d.AsSpan(30).ToArray();
            byte[] ram = compressed ? Decompress(mem, endMarker: true) : mem;
            for (int i = 0; i < 0xC000 && i < ram.Length; i++) m.Memory.Write((ushort)(0x4000 + i), ram[i]);
            return;
        }
        // v2 / v3: additional header at offset 30
        int extraLen = d[30] | (d[31] << 8);
        int headerEnd = 32 + extraLen;
        cpu.PC = (ushort)(d[32] | (d[33] << 8));
        byte hwMode = d[34];
        // 128K hardware: 3,4 in v2; 4,5,6 in v3
        bool is128 = extraLen == 23 ? (hwMode == 3 || hwMode == 4)
                                    : (hwMode == 4 || hwMode == 5 || hwMode == 6);
        if (is128 && m.Model == SpectrumModel.OneTwentyEight) m.Memory.Write7FFD(d[35]);

        // Page blocks: {2-byte len, 1-byte page, len bytes}
        int off = headerEnd;
        while (off + 3 <= d.Length)
        {
            int len = d[off] | (d[off + 1] << 8);
            byte page = d[off + 2];
            off += 3;
            byte[] block;
            if (len == 0xFFFF) { block = d.AsSpan(off, 0x4000).ToArray(); off += 0x4000; }
            else { block = Decompress(d.AsSpan(off, len).ToArray(), endMarker: false); off += len; }
            LoadPage(m, page, block, is128);
        }
    }

    private static void LoadPage(SpectrumMachine m, byte page, byte[] block, bool is128)
    {
        if (block.Length < 0x4000) return;
        int baseAddr;
        if (is128)
        {
            // Z80 page numbering (128K): 3=RAM0, 4=RAM1, 5=RAM2, 6=RAM3, 7=RAM4,
            // 8=RAM5, 9=RAM6, 10=RAM7.
            int bank = page - 3;
            if (bank < 0 || bank > 7) return;
            // Bank 5 -> 0x4000, bank 2 -> 0x8000, otherwise page in
            if (bank == 5) baseAddr = 0x4000;
            else if (bank == 2) baseAddr = 0x8000;
            else
            {
                // page it in temporarily
                byte prev = (byte)(m.Memory.PageState & 0x0F);
                m.Memory.Write7FFD((byte)((m.Memory.PageState & 0xF0) | bank));
                for (int i = 0; i < 0x4000; i++) m.Memory.Write((ushort)(0xC000 + i), block[i]);
                m.Memory.Write7FFD((byte)((m.Memory.PageState & 0xF0) | prev));
                return;
            }
        }
        else
        {
            // 48K page numbering: 4=0x8000, 5=0xC000, 8=0x4000
            baseAddr = page switch { 4 => 0x8000, 5 => 0xC000, 8 => 0x4000, _ => -1 };
            if (baseAddr < 0) return;
        }
        for (int i = 0; i < 0x4000; i++) m.Memory.Write((ushort)(baseAddr + i), block[i]);
    }

    /// <summary>Z80 RLE decompressor. Marker: 0xED 0xED n b -> b*n.</summary>
    private static byte[] Decompress(byte[] src, bool endMarker)
    {
        var dst = new List<byte>(0x4000);
        int i = 0;
        while (i < src.Length)
        {
            if (endMarker && i + 4 <= src.Length &&
                src[i] == 0x00 && src[i + 1] == 0xED && src[i + 2] == 0xED && src[i + 3] == 0x00)
                break;
            if (i + 4 <= src.Length && src[i] == 0xED && src[i + 1] == 0xED)
            {
                int n = src[i + 2];
                byte b = src[i + 3];
                for (int k = 0; k < n; k++) dst.Add(b);
                i += 4;
            }
            else
            {
                dst.Add(src[i++]);
            }
        }
        return dst.ToArray();
    }
}
