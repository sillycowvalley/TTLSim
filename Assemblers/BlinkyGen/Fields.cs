namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// Rev 14 folded micro-op field alphabet.
//
// The two-stage GAL control store decodes a 7-bit micro-op index into 20
// control lines. These enums name the values each field can take; MicroOp
// packs them and the decoder GALs (Stage 2) realise them. See
// Blinky_M_GAL_Control.md for the architecture.
// ---------------------------------------------------------------------------

/// <summary>Bus source (3 lines, fanned by a '138). PCBlo/PCBhi replace the
/// old PCBYTE modifier bit — the fold that turns a modifier into two codes.</summary>
public enum Src
{
    Ram = 0,
    Alu = 1,
    Tos = 2,
    PcbLo = 3,
    PcbHi = 4,
    Bp = 5,
    Flags = 6,
    Vec = 7
    // No "None": a step always has a defined source. SRC is only consulted
    // when a driver is actually enabled, so idle steps carry Src.Ram harmlessly.
}

/// <summary>Bus destination (4 lines, fanned by a '154, phase-gated by /CLK).
/// Halt and Iclr ride as destination codes rather than standalone lines.</summary>
public enum Dst
{
    Ir = 0,
    A = 1,
    B = 2,
    Tos = 3,
    PcLo = 4,
    PcHi = 5,
    AdrLo = 6,
    AdrHi = 7,
    Bp = 8,
    Flags = 9,
    RamWe = 10,
    Halt = 11,
    Iclr = 12,
    None = 15
}

/// <summary>'181 function source, driven onto the S/M/CN pins through the
/// ALUFN '153 mux (2 lines). Ir routes the opcode's own function bits; the
/// other three are the strapped patterns non-ALU instructions need.</summary>
public enum AluFn
{
    Ir = 0,         // opcode-driven: the ALU family
    Fa = 1,         // F = A   (moves, TST)
    Fb = 2,         // F = B   (moves, JUMP/CALL target)
    AddAB = 3       // A plus B (ENTER, LOCAL address math)
}

/// <summary>Address-bus source and page (4 lines). The old ASEL + PGSEL +
/// SPSEL fields fold into this single AMODE: the ADRlo-vs-SP choice is
/// implied by the page, and only nine modes ever occur.</summary>
public enum AMode
{
    Pc = 0,             // program counter drives the address bus
    Adr = 1,            // ADR latch drives it
    ZpAdrLo = 2,        // {page 0x00, ADRlo}  frame/zero-page
    IoAdrLo = 3,        // {page 0x01, ADRlo}  I/O page
    DStack = 4,         // {page 0x02, DSP}
    RPcLo = 5,          // {page 0x03, RSP}    return-stack PC-lo lane
    RPcHi = 6,          // {page 0x04, RSP}
    RBp = 7,            // {page 0x05, RSP}
    RFlags = 8          // {page 0x06, RSP}
}

/// <summary>Stack-pointer op (3 lines, fanned by a '138). Old DSPCTL + RSPCTL
/// fold into this since only these combinations occur.</summary>
public enum SpOp
{
    None = 0,
    DspUp = 1,
    DspDown = 2,
    RspUp = 3,
    RspDown = 4,
    DspDownRspUp = 5    // the >R combined move
}
