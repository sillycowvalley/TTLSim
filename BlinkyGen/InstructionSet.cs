namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// The microcode table — mirrors Blinky_M_Microcoded_CPU.md §7 (rev 10).
//
// Hardware wiring notes the table depends on (all zero-package):
//   * '181 carry-in  = CN & (S0 ? Cflag : 1)
//       - SUB/CMP (S=0110, S0=0): CN=1 injects +1
//       - ADC     (S=1001, S0=1): CN=1 injects the C flag
//       - one AND + one spare section of flags '157 #2
//   * Shift direction = raw IR[0]; C_SRC = (TOSMODE == Shift).
//   * B bit in the pushed flags byte = /INTP (registered): BRK pushes B=1,
//     hardware entry pushes B=0.
//   * INTP is REGISTERED (spare flop of the interrupt '74s), sampled on the
//     fetch edge, so the entry sequence cannot be torn when ISET clears the
//     pending condition mid-sequence.
// ---------------------------------------------------------------------------

public sealed record Instruction(byte Opcode, string Mnemonic, string Operand,
    MicroStep[] Steps, MicroStep[]? StepsCondTrue = null);

public static class InstructionSet
{
    // --- step shorthands ---------------------------------------------------

    // T0 fetch: ADDR=PC, RAM -> IR, PC++ (+ optional edge actions)
    static MicroStep Fetch(SpOp dsp = SpOp.Hold, SpOp rsp = SpOp.Hold, bool trst = false)
        => new(Src.Ram, Dst.Ir, Asel.Pc, PcInc: true, Dsp: dsp, Rsp: rsp, Trst: trst);

    // ADDR=PC, RAM -> dst, PC++  (operand byte consume)
    static MicroStep Operand(Dst dst)
        => new(Src.Ram, dst, Asel.Pc, PcInc: true);

    // paged read: ADDR={pg,SP}, RAM -> dst
    static MicroStep SpRead(Pg pg, Dst dst, SpOp dsp = SpOp.Hold, SpOp rsp = SpOp.Hold,
                            TosMode tos = TosMode.Hold, bool nz = false, bool c = false, bool trst = false)
        => new(Src.Ram, dst, Asel.PgSp, pg, Dsp: dsp, Rsp: rsp, Tos: tos, NzWe: nz, CWe: c, Trst: trst);

    // paged write: ADDR={pg,SP}, src -> RAM
    static MicroStep SpWrite(Pg pg, Src src, SpOp dsp = SpOp.Hold, SpOp rsp = SpOp.Hold,
                             bool pcHi = false, bool trst = false)
        => new(src, Dst.RamWe, Asel.PgSp, pg, PcHiByte: pcHi, Dsp: dsp, Rsp: rsp, Trst: trst);

    // RAM -> TOS refill at the data-stack page (the standard pop tail)
    static MicroStep TosRefill(bool trst = true)
        => new(Src.Ram, Dst.Tos, Asel.PgSp, Pg.DStack, Tos: TosMode.Load, Trst: trst);

    // ALU result onto the bus
    static MicroStep AluTo(Dst dst, AluOp op, TosMode tos = TosMode.Hold,
                           bool nz = false, bool c = false, SpOp dsp = SpOp.Hold, bool trst = false)
        => new(Src.Alu, dst, Asel.Pc, Alu: op, Tos: tos, NzWe: nz, CWe: c, Dsp: dsp, Trst: trst);

    // ALU result written to memory
    static MicroStep AluToRam(AluOp op, Asel asel, Pg pg, SpOp dsp = SpOp.Hold, bool trst = false)
        => new(Src.Alu, Dst.RamWe, asel, pg, Alu: op, Dsp: dsp, Trst: trst);

    // --- binary ALU family helper -------------------------------------------
    static MicroStep[] Binary(AluOp op, bool writesC) => new[]
    {
        Fetch(),
        new MicroStep(Src.Tos, Dst.A, Asel.Pc, Dsp: SpOp.Down),
        SpRead(Pg.DStack, Dst.B),
        new MicroStep(Src.Alu, Dst.Tos, Asel.Pc, Alu: op, Tos: TosMode.Load,
                      NzWe: true, CWe: writesC, Trst: true),
    };

    // --- shift family helper: TOS shifts in place; C only, N/Z held ---------
    static MicroStep[] Shift() => new[]
    {
        Fetch(),
        new MicroStep(Tos: TosMode.Shift, CWe: true, Trst: true),
    };

    // --- the table -----------------------------------------------------------

    public static readonly Instruction[] All =
    {
        // CONTROL -------------------------------------------------------------
        new(0x00, "NOP", "-", new[] { Fetch(trst: true) }),

        new(0x01, "RET", "-", new[]
        {
            Fetch(rsp: SpOp.Down),
            SpRead(Pg.RBp,   Dst.Bp),
            SpRead(Pg.RPcHi, Dst.PcHi),
            SpRead(Pg.RPcLo, Dst.PcLo, trst: true),
        }),

        new(0x02, "BRK", "-", new[]
        {
            Fetch(),                                        // PC++ -> pushes PC+1
            SpWrite(Pg.RPcLo,  Src.PcByte),
            SpWrite(Pg.RPcHi,  Src.PcByte, pcHi: true),
            SpWrite(Pg.RFlags, Src.Flags),                  // B = /INTP = 1
            SpWrite(Pg.RBp,    Src.Bp, rsp: SpOp.Up),       // one count per frame
            new MicroStep(Src.Vec, Dst.PcLo, ISet: true),   // VEC = 0xFE (IRQ/BRK)
            new MicroStep(Src.Vec, Dst.PcHi, Trst: true),
        }),

        new(0x03, "RTI", "-", new[]
        {
            Fetch(rsp: SpOp.Down),
            SpRead(Pg.RBp,    Dst.Bp),
            SpRead(Pg.RFlags, Dst.Flags, nz: true, c: true),  // '157 restore path
            SpRead(Pg.RPcHi,  Dst.PcHi),
            SpRead(Pg.RPcLo,  Dst.PcLo, trst: true),
        }),

        new(0x04, "CLI", "-", new[] { Fetch(), new MicroStep(IClr: true, Trst: true) }),
        new(0x05, "SEI", "-", new[] { Fetch(), new MicroStep(ISet: true, Trst: true) }),

        // ALU (0x1_) — nibble placement is load-bearing (fill mux = IR[3:2],
        // direction = IR[0]) ---------------------------------------------------
        new(0x10, "ADD", "-", Binary(AluOp.Add, writesC: true)),
        new(0x11, "ADC", "-", Binary(AluOp.Adc, writesC: true)),
        new(0x12, "SUB", "-", Binary(AluOp.Sub, writesC: true)),
        new(0x13, "XOR", "-", Binary(AluOp.Xor, writesC: false)),

        new(0x14, "NOT", "-", new[]
        {
            Fetch(),
            new MicroStep(Src.Tos, Dst.A, Asel.Pc),
            AluTo(Dst.Tos, AluOp.NotA, TosMode.Load, nz: true, trst: true),
        }),

        new(0x15, "TST", "-", new[]
        {
            Fetch(),
            new MicroStep(Src.Tos, Dst.A, Asel.Pc),
            new MicroStep(Alu: AluOp.FA, NzWe: true, Trst: true),   // flags only
        }),

        new(0x16, "SHL", "-", Shift()),
        new(0x17, "SHR", "-", Shift()),

        new(0x18, "AND", "-", Binary(AluOp.And, writesC: false)),

        new(0x19, "CMP", "-", new[]
        {
            Fetch(dsp: SpOp.Down),
            SpRead(Pg.DStack, Dst.B, dsp: SpOp.Up),                 // NOS, non-destructive
            new MicroStep(Src.Tos, Dst.A, Asel.Pc),
            new MicroStep(Alu: AluOp.Sub, NzWe: true, CWe: true, Trst: true),
        }),

        new(0x1B, "ASR", "-", Shift()),
        new(0x1C, "OR",  "-", Binary(AluOp.Or, writesC: false)),
        new(0x1E, "ROL", "-", Shift()),
        new(0x1F, "ROR", "-", Shift()),

        // STACK (0x2_) ----------------------------------------------------------
        new(0x20, "DUP", "-", new[]
        {
            Fetch(),
            SpWrite(Pg.DStack, Src.Tos, dsp: SpOp.Up, trst: true),
        }),

        new(0x21, "OVER", "-", new[]
        {
            Fetch(dsp: SpOp.Down),
            SpRead(Pg.DStack, Dst.A, dsp: SpOp.Up),                 // read NOS (a)
            SpWrite(Pg.DStack, Src.Tos, dsp: SpOp.Up),              // push old TOS (b)
            AluTo(Dst.Tos, AluOp.FA, TosMode.Load, trst: true),     // TOS <- a
        }),

        new(0x22, "TUCK", "-", new[]
        {
            Fetch(dsp: SpOp.Down),
            SpRead(Pg.DStack, Dst.A),                               // a from NOS slot
            SpWrite(Pg.DStack, Src.Tos, dsp: SpOp.Up),              // b over a's slot
            AluToRam(AluOp.FA, Asel.PgSp, Pg.DStack, dsp: SpOp.Up, trst: true), // a above
        }),

        new(0x23, "SWAP", "-", new[]
        {
            Fetch(dsp: SpOp.Down),
            SpRead(Pg.DStack, Dst.A),
            SpWrite(Pg.DStack, Src.Tos, dsp: SpOp.Up),
            AluTo(Dst.Tos, AluOp.FA, TosMode.Load, trst: true),
        }),

        new(0x24, "DROP", "-", new[]
        {
            Fetch(dsp: SpOp.Down),
            TosRefill(),
        }),

        new(0x25, "NIP", "-", new[]
        {
            Fetch(dsp: SpOp.Down, trst: true),                      // pure pointer move
        }),

        new(0x26, "ROT", "-", new[]
        {
            Fetch(dsp: SpOp.Down),
            SpRead(Pg.DStack, Dst.A, dsp: SpOp.Down),               // b
            SpRead(Pg.DStack, Dst.B),                               // a
            AluToRam(AluOp.FA, Asel.PgSp, Pg.DStack, dsp: SpOp.Up), // b -> a's slot
            SpWrite(Pg.DStack, Src.Tos, dsp: SpOp.Up),              // c -> b's slot
            AluTo(Dst.Tos, AluOp.FB, TosMode.Load, trst: true),     // TOS <- a
        }),

        new(0x28, ">R", "-", new[]
        {
            Fetch(),
            SpWrite(Pg.RPcLo, Src.Tos, rsp: SpOp.Up, dsp: SpOp.Down),
            TosRefill(),
        }),

        new(0x29, "R>", "-", new[]
        {
            Fetch(rsp: SpOp.Down),
            SpWrite(Pg.DStack, Src.Tos, dsp: SpOp.Up),
            SpRead(Pg.RPcLo, Dst.Tos, tos: TosMode.Load, trst: true),
        }),

        new(0x2A, "R@", "-", new[]
        {
            Fetch(rsp: SpOp.Down),
            SpWrite(Pg.DStack, Src.Tos, dsp: SpOp.Up),
            SpRead(Pg.RPcLo, Dst.Tos, rsp: SpOp.Up, tos: TosMode.Load, trst: true),
        }),

        // MEMORY / I-O (0x3_) -----------------------------------------------------
        new(0x30, "PUSH #", "ii", new[]
        {
            Fetch(),
            SpWrite(Pg.DStack, Src.Tos, dsp: SpOp.Up),              // spill
            new MicroStep(Src.Ram, Dst.Tos, Asel.Pc, PcInc: true, Tos: TosMode.Load, Trst: true),
        }),

        new(0x31, "LOAD", "aaaa", new[]
        {
            Fetch(),
            Operand(Dst.AdrLo),
            Operand(Dst.AdrHi),
            SpWrite(Pg.DStack, Src.Tos, dsp: SpOp.Up),              // spill
            new MicroStep(Src.Ram, Dst.Tos, Asel.Adr, Tos: TosMode.Load, Trst: true),
        }),

        new(0x32, "STORE", "aaaa", new[]
        {
            Fetch(),
            Operand(Dst.AdrLo),
            Operand(Dst.AdrHi),
            new MicroStep(Src.Tos, Dst.RamWe, Asel.Adr, Dsp: SpOp.Down),
            TosRefill(),
        }),

        new(0x33, "FETCH", "-", new[]
        {
            Fetch(),
            new MicroStep(Src.Tos, Dst.AdrLo, Asel.Pc),
            new MicroStep(Src.Ram, Dst.Tos, Asel.PgAdrLo, Pg.Frames, Tos: TosMode.Load, Trst: true),
        }),

        new(0x34, "STORE!", "-", new[]
        {
            Fetch(dsp: SpOp.Down),
            new MicroStep(Src.Tos, Dst.AdrLo, Asel.Pc),
            SpRead(Pg.DStack, Dst.B, dsp: SpOp.Down),               // data
            new MicroStep(Src.Alu, Dst.RamWe, Asel.PgAdrLo, Pg.Frames, Alu: AluOp.FB),
            TosRefill(),
        }),

        new(0x35, "IN #", "pp", new[]
        {
            Fetch(),
            Operand(Dst.AdrLo),
            SpWrite(Pg.DStack, Src.Tos, dsp: SpOp.Up),              // spill
            new MicroStep(Src.Ram, Dst.Tos, Asel.PgAdrLo, Pg.Io, Tos: TosMode.Load, Trst: true),
        }),

        new(0x36, "OUT #", "pp", new[]
        {
            Fetch(),
            Operand(Dst.AdrLo),
            new MicroStep(Src.Tos, Dst.RamWe, Asel.PgAdrLo, Pg.Io, Dsp: SpOp.Down),
            TosRefill(),
        }),

        new(0x37, "IN", "-", new[]                                  // ( p -- v )
        {
            Fetch(),
            new MicroStep(Src.Tos, Dst.AdrLo, Asel.Pc),
            new MicroStep(Src.Ram, Dst.Tos, Asel.PgAdrLo, Pg.Io, Tos: TosMode.Load, Trst: true),
        }),

        new(0x38, "OUT", "-", new[]                                 // ( d p -- )
        {
            Fetch(dsp: SpOp.Down),
            new MicroStep(Src.Tos, Dst.AdrLo, Asel.Pc),
            SpRead(Pg.DStack, Dst.B, dsp: SpOp.Down),               // data
            new MicroStep(Src.Alu, Dst.RamWe, Asel.PgAdrLo, Pg.Io, Alu: AluOp.FB),
            TosRefill(),
        }),

        // FRAME (0x4_) ---------------------------------------------------------------
        new(0x40, "ENTER", "kk", new[]
        {
            Fetch(),
            Operand(Dst.B),
            new MicroStep(Src.Bp, Dst.A, Asel.Pc),
            AluTo(Dst.Bp, AluOp.Add, trst: true),
        }),

        new(0x41, "LOCAL@", "oo", new[]
        {
            Fetch(),
            Operand(Dst.B),
            new MicroStep(Src.Bp, Dst.A, Asel.Pc),
            SpWrite(Pg.DStack, Src.Tos, dsp: SpOp.Up),              // spill; ALU settles
            AluTo(Dst.AdrLo, AluOp.Add),
            new MicroStep(Src.Ram, Dst.Tos, Asel.PgAdrLo, Pg.Frames, Tos: TosMode.Load, Trst: true),
        }),

        new(0x42, "LOCAL!", "oo", new[]
        {
            Fetch(),
            Operand(Dst.B),
            new MicroStep(Src.Bp, Dst.A, Asel.Pc),
            AluTo(Dst.AdrLo, AluOp.Add),
            new MicroStep(Src.Tos, Dst.RamWe, Asel.PgAdrLo, Pg.Frames, Dsp: SpOp.Down),
            TosRefill(),
        }),

        // FLOW (0x5_) ----------------------------------------------------------------
        new(0x50, "JUMP", "aaaa", Jump()),
        new(0x51, "CALL", "aaaa", new[]
        {
            Fetch(),
            Operand(Dst.B),                                         // target lo
            Operand(Dst.A),                                         // target hi
            SpWrite(Pg.RPcLo, Src.PcByte),
            SpWrite(Pg.RPcHi, Src.PcByte, pcHi: true),
            SpWrite(Pg.RBp,   Src.Bp, rsp: SpOp.Up),                // one count per frame
            AluTo(Dst.PcLo, AluOp.FB),
            AluTo(Dst.PcHi, AluOp.FA, trst: true),
        }),

        // Conditionals: COND=0 row skips the operand bytes; COND=1 row is JUMP.
        Bcc(0x5A, "BEQ"), Bcc(0x5B, "BNE"),
        Bcc(0x5C, "BCS"), Bcc(0x5D, "BCC"),
        Bcc(0x5E, "BMI"), Bcc(0x5F, "BPL"),

        // 0xFF — HALT anchor: fetch, then the HALT strobe stops the clock.
        new(0xFF, "HALT", "-", new[]
        {
            Fetch(),
            new MicroStep(Dst: Dst.Halt, Trst: true),
        }),
    };

    static MicroStep[] Jump() => new[]
    {
        Fetch(),
        Operand(Dst.B),
        Operand(Dst.A),
        AluTo(Dst.PcLo, AluOp.FB),
        AluTo(Dst.PcHi, AluOp.FA, trst: true),
    };

    static Instruction Bcc(byte opcode, string mnemonic) => new(opcode, mnemonic, "aaaa",
        Steps: new[]                                                // not taken
        {
            Fetch(),
            new MicroStep(PcInc: true),                             // skip lo
            new MicroStep(PcInc: true, Trst: true),                 // skip hi
        },
        StepsCondTrue: Jump());                                     // taken

    // Hardware interrupt entry — the INTP=1 rows, shared by every opcode.
    // Runs BEFORE the fetch increments PC: raw PC is pushed, B = /INTP = 0.
    public static readonly MicroStep[] Entry =
    {
        SpWrite(Pg.RPcLo,  Src.PcByte),
        SpWrite(Pg.RPcHi,  Src.PcByte, pcHi: true),
        SpWrite(Pg.RFlags, Src.Flags),
        SpWrite(Pg.RBp,    Src.Bp, rsp: SpOp.Up),
        new MicroStep(Src.Vec, Dst.PcLo, ISet: true),
        new MicroStep(Src.Vec, Dst.PcHi, Trst: true),
    };

    // Fill for unassigned opcodes and unreachable T-states: halt visibly.
    public static readonly MicroStep IllegalFill = new(Dst: Dst.Halt, Trst: true);
}
