// OpcodeTable.cs — the Addy v1 opcode map for AddyASM.
//
// Hand-maintained: Addy has no microcode store to generate from — the decode
// lives in two GAL22V10s (addy_gal1_seq.pld / addy_gal2_ena.pld), and this
// table mirrors the same design doc (addy_cpu_v1_DESIGN.md). Change one,
// change all three.
//
// Instruction word, fixed fields:
//
//     [15:11] op   [10:8] rd   [7:5] rs   [4:3] rsvd   [2:0] rt
//                              [7:0] imm8 (aliases rs/rt)
//
// r7 is the PC; any instruction writing r7 is a jump. The assembler's jump
// and branch mnemonics (JMP/JZ/JNZ/JR/BRA/BRZ/BRNZ, plus NOP) are pseudo
// instructions over these opcodes and live in Program.cs.

using System.Collections.Generic;

namespace Addy.Assembler;

public enum OperandShape
{
    None,       // no operands                       (HLT)
    RdRsRt,     // rd, rs, rt — or rd, rt with rs=rd (ADD, SUB)
    RdRs,       // rd, rs                            (MOV, MOVZ, MOVNZ)
    RsRt,       // rs, rt — flags only, rd ignored   (CMP, CMN)
    Rs,         // rs only                           (TST, OUT)
    Rd,         // rd only                           (IN)
    RdImm8,     // rd, imm8                          (ADDI ... CMPI, LDIZ ...)
}

public readonly record struct OpcodeEntry(
    string Mnemonic, byte Op5, OperandShape Shape);

public static class OpcodeTable
{
    public static readonly IReadOnlyList<OpcodeEntry> Entries = new OpcodeEntry[]
    {
        // ---- class 00: register ALU (A <- rs, B <- rt) -------------------
        new("ADD",    0b00000, OperandShape.RdRsRt),
        new("SUB",    0b00001, OperandShape.RdRsRt),
        new("MOV",    0b00010, OperandShape.RdRs),
        new("CMN",    0b00100, OperandShape.RsRt),
        new("CMP",    0b00101, OperandShape.RsRt),
        new("TST",    0b00110, OperandShape.Rs),

        // ---- class 01: immediate ALU (A <- rd, B <- imm) -----------------
        new("ADDI",   0b01000, OperandShape.RdImm8),
        new("SUBI",   0b01001, OperandShape.RdImm8),
        new("LDI",    0b01010, OperandShape.RdImm8),
        new("ADDIH",  0b01011, OperandShape.RdImm8),   // rd + (imm << 8)
        new("CMPI",   0b01101, OperandShape.RdImm8),

        // ---- class 10: conditional (WE gated by Z; flags held) -----------
        new("MOVZ",   0b10000, OperandShape.RdRs),
        new("MOVNZ",  0b10001, OperandShape.RdRs),
        new("LDIZ",   0b10010, OperandShape.RdImm8),
        new("LDINZ",  0b10011, OperandShape.RdImm8),
        new("ADDIZ",  0b10100, OperandShape.RdImm8),
        new("ADDINZ", 0b10101, OperandShape.RdImm8),
        new("SUBIZ",  0b10110, OperandShape.RdImm8),
        new("SUBINZ", 0b10111, OperandShape.RdImm8),

        // ---- class 11: special -------------------------------------------
        new("OUT",    0b11000, OperandShape.Rs),
        new("IN",     0b11001, OperandShape.Rd),
        new("HLT",    0b11111, OperandShape.None),
    };

    // HLT is emitted as all-ones (not just op<<11): 0xFFFF is what an erased
    // EEPROM reads, so assembled HLTs are bit-identical to blank memory and a
    // runaway PC freezes the machine exactly like a deliberate halt.
    public const ushort HltWord = 0xFFFF;

    public static ushort Word(byte op5, int rd = 0, int rs = 0, int rt = 0, int imm8 = 0)
        => (ushort)(((op5 & 0x1F) << 11) | ((rd & 7) << 8) |
                    ((rs & 7) << 5) | (rt & 7) | (imm8 & 0xFF));
}
