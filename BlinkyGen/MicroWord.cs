namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// Blinky-M microword — 32 control lines across 4x 28C64 (rev 10 field set).
//
// Bit layout (authoritative — the hardware is wired to THIS):
//   ROM0: b0..b2 SRC, b3..b6 DST, b7 TRST
//   ROM1: b0..b3 ALU S0..S3, b4 ALU M, b5 ALU CN, b6..b7 ASEL
//   ROM2: b0 PCINC, b1 PCBYTE, b2..b3 DSPCTL, b4..b5 RSPCTL, b6 SPSEL, b7 NZ_WE
//   ROM3: b0..b1 TOSMODE, b2 C_WE, b3 ISET, b4 ICLR, b5..b7 PGSEL
//
// uROM address (13 bits, per 28C64):
//   A12..A5 = opcode (IR, or live data bus at T0 via the bypass '257s)
//   A4..A2  = T-state
//   A1      = COND   ('151: flag per IR[2:1], polarity per IR[0])
//   A0      = INTP   (registered interrupt-pending — sampled at fetch)
// ---------------------------------------------------------------------------

public enum Src : byte
{
    Ram = 0, Alu = 1, Tos = 2, PcByte = 3, Bp = 4, Flags = 5, Vec = 6,
    None = 7            // unused '138 output — no driver enabled
}

public enum Dst : byte
{
    Ir = 0, A = 1, B = 2, Tos = 3, PcLo = 4, PcHi = 5, AdrLo = 6, AdrHi = 7,
    Bp = 8, Flags = 9, RamWe = 10, Halt = 11,
    None = 15           // unassigned '154 output — no strobe
}

public enum Asel : byte
{
    Pc = 0,             // PC '541s drive the address bus
    Adr = 1,            // ADR '574 pair
    PgSp = 2,           // {page driver, SP counter}  (SPSEL derived from page)
    PgAdrLo = 3         // {page driver, ADRlo}
}

public enum Pg : byte   // PGSEL — page driver pattern (address high byte)
{
    Frames = 0,         // 0x00  frame/globals page
    Io = 1,             // 0x01  I/O page
    DStack = 2,         // 0x02  data stack
    RPcLo = 3,          // 0x03  return stack, PC-lo lane
    RPcHi = 4,          // 0x04  return stack, PC-hi lane
    RBp = 5,            // 0x05  return stack, BP lane
    RFlags = 6          // 0x06  return stack, flags lane
}

public enum SpOp : byte { Hold = 0, Up = 1, Down = 2 }

public enum TosMode : byte
{
    Hold = 0,
    Load = 1,           // '194 parallel load from the bus
    Shift = 2           // '194 shift; direction = raw IR[0]; bit1 doubles as C_SRC
}

// '181 function, active-high operand convention. CN semantics (see wiring note
// in InstructionSet): carry_in = CN & (S0 ? Cflag : 1).
public readonly record struct AluOp(byte S, bool M, bool CN)
{
    public static readonly AluOp Idle = new(0b0000, true, false);   // don't-care
    public static readonly AluOp Add  = new(0b1001, false, false);  // A plus B
    public static readonly AluOp Adc  = new(0b1001, false, true);   // A plus B plus C (S0=1 -> CN selects Cflag)
    public static readonly AluOp Sub  = new(0b0110, false, true);   // B minus A (S0=0 -> CN injects 1)
    public static readonly AluOp Xor  = new(0b0110, true,  false);
    public static readonly AluOp And  = new(0b1011, true,  false);
    public static readonly AluOp Or   = new(0b1110, true,  false);
    public static readonly AluOp NotA = new(0b0000, true,  false);  // F = /A
    public static readonly AluOp FA   = new(0b1111, true,  false);  // F = A  (moves, TST)
    public static readonly AluOp FB   = new(0b1010, true,  false);  // F = B  (moves)
}

public readonly record struct MicroStep(
    Src Src = Src.None,
    Dst Dst = Dst.None,
    Asel Asel = Asel.Pc,
    Pg Pg = Pg.Frames,
    AluOp Alu = default,
    bool PcInc = false,
    bool PcHiByte = false,      // PCBYTE: PC-byte source selects hi (else lo)
    SpOp Dsp = SpOp.Hold,
    SpOp Rsp = SpOp.Hold,
    TosMode Tos = TosMode.Hold,
    bool NzWe = false,
    bool CWe = false,
    bool ISet = false,
    bool IClr = false,
    bool Trst = false)
{
    // SPSEL is derived, not authored: pages 03..06 are return-stack lanes.
    public bool SpSelRsp => Asel == Asel.PgSp && Pg >= Pg.RPcLo;

    public byte Rom0 => (byte)(((byte)Src & 7) | (((byte)Dst & 15) << 3) | (Trst ? 0x80 : 0));

    public byte Rom1 => (byte)((Alu.S & 15) | (Alu.M ? 0x10 : 0) | (Alu.CN ? 0x20 : 0)
                        | (((byte)Asel & 3) << 6));

    public byte Rom2 => (byte)((PcInc ? 1 : 0) | (PcHiByte ? 2 : 0)
                        | (((byte)Dsp & 3) << 2) | (((byte)Rsp & 3) << 4)
                        | (SpSelRsp ? 0x40 : 0) | (NzWe ? 0x80 : 0));

    public byte Rom3 => (byte)(((byte)Tos & 3) | (CWe ? 4 : 0) | (ISet ? 8 : 0)
                        | (IClr ? 0x10 : 0) | (((byte)Pg & 7) << 5));
}
