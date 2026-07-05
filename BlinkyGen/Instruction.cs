namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// An instruction: opcode, mnemonic, operand shape, and its ordered T-states.
// Each step carries a micro-op plus its sequencing bit (Trst). Conditionals
// carry a second sequence for the taken case.
// ---------------------------------------------------------------------------

/// <summary>One T-state: the datapath micro-op plus the /TRST sequencing bit
/// (true = this step ends the instruction). Trst is stored on the step, not
/// on the micro-op, because it belongs to Stage 1 (the sequencer), not to the
/// Stage 2 decode.</summary>
public readonly record struct Step(MicroOp Op, bool Trst = false);

/// <summary>Operand shape, which also fixes instruction length.</summary>
public enum OperandShape
{
    None,       // 1 byte
    Imm,        // ii   immediate
    Port,       // pp   port number
    Count,      // kk   frame size
    Offset,     // oo   signed local offset
    Addr        // aaaa 16-bit absolute (little-endian)
}

public sealed record Instruction(
    int Opcode,
    string Mnemonic,
    OperandShape Operand,
    IReadOnlyList<Step> Steps,
    IReadOnlyList<Step>? TakenSteps = null)
{
    public int Length => Operand switch
    {
        OperandShape.None => 1,
        OperandShape.Addr => 3,
        _ => 2
    };

    public string Family => (Opcode >> 4) switch
    {
        0x0 => "CTL",
        0x1 => "SHIFT",
        0x2 => "STK",
        0x3 => "MEM",
        0x4 or 0x5 or 0x6 or 0x7 => "ALU",
        0x8 => "FRM",
        0x9 => "FLOW",
        0xF => "CTL",   // HALT anchor decodes with control
        _ => "?"
    };
}
