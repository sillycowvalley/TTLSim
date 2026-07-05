namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// The canonical rev 14 instruction table — the single source of truth.
// Everything else (dictionary, sequencer, PLDs, HTML matrix, assembler
// tables) is a projection of this.
//
// Opcode map (rev 14):
//   CTL   0x0_    SHIFT 0x1_   STK 0x2_   MEM 0x3_
//   ALU   0x40-0x7F   (opcode = 01 M CN S3 S2 S1 S0; the '181 code IS the opcode)
//   FRM   0x8_    FLOW  0x9_
//   NOP = 0x00, HALT = 0xFF anchors.
//
// Hardware wiring the table depends on (all zero-package, ratified rev 14):
//   * '181 carry-in = CN & (S0 ? Cflag : 1). SUB/CMP inject 1; ADC injects C.
//   * Shift direction = raw IR[0]; C_SRC = TOS shift; fill = IR[3:2].
//   * B bit in the pushed flags byte = /INTP (registered): BRK B=1, entry B=0.
//   * INTP registered, sampled on the TRST edge entering T0.
//   * ALUFN '153 mux: Ir routes IR[5:0] to '181 S/M/CN; Fa/Fb/AddAB strapped.
// ---------------------------------------------------------------------------

public static class InstructionSet
{
    // ---- '181 function codes (active-high operand convention) --------------
    // These are the validated codes that produced the simulated listing.
    public readonly record struct Alu181(int S, bool M, bool Cn)
    {
        public int QuadrantOpcode => 0x40 | (M ? 0x20 : 0) | (Cn ? 0x10 : 0) | (S & 0xF);
    }

    public static readonly Alu181 Add  = new(0b1001, false, false);
    public static readonly Alu181 Adc  = new(0b1001, false, true);
    public static readonly Alu181 Sub  = new(0b0110, false, true);
    public static readonly Alu181 Xor  = new(0b0110, true,  false);
    public static readonly Alu181 And  = new(0b1011, true,  false);
    public static readonly Alu181 Or   = new(0b1110, true,  false);
    public static readonly Alu181 NotA = new(0b0000, true,  false);   // F = /A
    public static readonly Alu181 Fa   = new(0b1111, true,  false);   // F = A
    public static readonly Alu181 Fb   = new(0b1010, true,  false);   // F = B

    // ---- step shorthands ---------------------------------------------------

    static Step Fetch(SpOp sp = SpOp.None, bool trst = false)
        => new Step(new MicroOp(Src.Ram, Dst.Ir, Amode: AMode.Pc, Sp: sp), trst);

    static Step Consume(Dst dst)
        => new Step(new MicroOp(Src.Ram, dst, Amode: AMode.Pc));

    static Step SpRead(AMode page, Dst dst, SpOp sp = SpOp.None,
                       bool nz = false, bool c = false, bool trst = false)
        => new Step(new MicroOp(Src.Ram, dst, Amode: page, Sp: sp, NzWe: nz, CWe: c), trst);

    static Step SpWrite(Src src, AMode page, SpOp sp = SpOp.None, bool trst = false)
        => new Step(new MicroOp(src, Dst.RamWe, Amode: page, Sp: sp), trst);

    static Step TosRefill(bool trst = true)
        => new Step(new MicroOp(Src.Ram, Dst.Tos, Amode: AMode.DStack), trst);

    static Step AluTo(Dst dst, AluFn fn, bool nz = false, bool c = false,
                      SpOp sp = SpOp.None, bool trst = false)
        => new Step(new MicroOp(Src.Alu, dst, AluFn: fn, Amode: AMode.Adr,
                           Sp: sp, NzWe: nz, CWe: c), trst);

    static Step Move(Src src, Dst dst, SpOp sp = SpOp.None, bool trst = false)
        => new Step(new MicroOp(src, dst, Amode: AMode.Adr, Sp: sp), trst);

    // ---- the fixed (non-ALU-quadrant) instructions -------------------------

    static IEnumerable<Instruction> Fixed()
    {
        // CONTROL -------------------------------------------------------------
        yield return new(0x00, "NOP", OperandShape.None, new[] { Fetch(trst: true) });

        yield return new(0x01, "RET", OperandShape.None, new[]
        {
            Fetch(SpOp.RspDown),
            SpRead(AMode.RBp,   Dst.Bp),
            SpRead(AMode.RPcHi, Dst.PcHi),
            SpRead(AMode.RPcLo, Dst.PcLo, trst: true),
        });

        yield return new(0x02, "BRK", OperandShape.None, new[]
        {
            Fetch(),
            SpWrite(Src.PcbLo, AMode.RPcLo),
            SpWrite(Src.PcbHi, AMode.RPcHi),
            SpWrite(Src.Flags, AMode.RFlags),
            SpWrite(Src.Bp,    AMode.RBp, SpOp.RspUp),
            new Step(new MicroOp(Src.Vec, Dst.PcLo, Amode: AMode.Adr, ISet: true)),
            new Step(new MicroOp(Src.Vec, Dst.PcHi, Amode: AMode.Adr), Trst: true),
        });

        yield return new(0x03, "RTI", OperandShape.None, new[]
        {
            Fetch(SpOp.RspDown),
            SpRead(AMode.RBp,     Dst.Bp),
            SpRead(AMode.RFlags,  Dst.Flags, nz: true, c: true),
            SpRead(AMode.RPcHi,   Dst.PcHi),
            SpRead(AMode.RPcLo,   Dst.PcLo, trst: true),
        });

        yield return new(0x04, "CLI", OperandShape.None, new[]
        {
            Fetch(),
            new Step(new MicroOp(Dst: Dst.Iclr, Amode: AMode.Adr), Trst: true),
        });

        yield return new(0x05, "SEI", OperandShape.None, new[]
        {
            Fetch(),
            new Step(new MicroOp(Amode: AMode.Adr, ISet: true), Trst: true),
        });

        // SHIFT (0x1_) — fill IR[3:2], direction IR[0]; C only, N/Z held ------
        foreach (var (op, name) in new[]
                 { (0x10, "SHL"), (0x11, "SHR"), (0x13, "ASR"), (0x14, "ROL"), (0x15, "ROR") })
        {
            yield return new(op, name, OperandShape.None, new[]
            {
                Fetch(),
                new Step(new MicroOp(Amode: AMode.Adr, TosShift: true, CWe: true), Trst: true),
            });
        }

        // STACK (0x2_) --------------------------------------------------------
        yield return new(0x20, "DUP", OperandShape.None, new[]
        {
            Fetch(),
            SpWrite(Src.Tos, AMode.DStack, SpOp.DspUp, trst: true),
        });

        yield return new(0x21, "OVER", OperandShape.None, new[]
        {
            Fetch(SpOp.DspDown),
            SpRead(AMode.DStack, Dst.A, SpOp.DspUp),
            SpWrite(Src.Tos, AMode.DStack, SpOp.DspUp),
            AluTo(Dst.Tos, AluFn.Fa, trst: true),
        });

        yield return new(0x22, "TUCK", OperandShape.None, new[]
        {
            Fetch(SpOp.DspDown),
            SpRead(AMode.DStack, Dst.A),
            SpWrite(Src.Tos, AMode.DStack, SpOp.DspUp),
            new Step(new MicroOp(Src.Alu, Dst.RamWe, AluFn: AluFn.Fa, Amode: AMode.DStack, Sp: SpOp.DspUp), Trst: true),
        });

        yield return new(0x23, "SWAP", OperandShape.None, new[]
        {
            Fetch(SpOp.DspDown),
            SpRead(AMode.DStack, Dst.A),
            SpWrite(Src.Tos, AMode.DStack, SpOp.DspUp),
            AluTo(Dst.Tos, AluFn.Fa, trst: true),
        });

        yield return new(0x24, "DROP", OperandShape.None, new[]
        {
            Fetch(SpOp.DspDown),
            TosRefill(),
        });

        yield return new(0x25, "NIP", OperandShape.None, new[]
        {
            Fetch(SpOp.DspDown, trst: true),
        });

        yield return new(0x26, "ROT", OperandShape.None, new[]
        {
            Fetch(SpOp.DspDown),
            SpRead(AMode.DStack, Dst.A, SpOp.DspDown),
            SpRead(AMode.DStack, Dst.B),
            new Step(new MicroOp(Src.Alu, Dst.RamWe, AluFn: AluFn.Fa, Amode: AMode.DStack, Sp: SpOp.DspUp)),
            SpWrite(Src.Tos, AMode.DStack, SpOp.DspUp),
            AluTo(Dst.Tos, AluFn.Fb, trst: true),
        });

        yield return new(0x28, ">R", OperandShape.None, new[]
        {
            Fetch(),
            SpWrite(Src.Tos, AMode.RPcLo, SpOp.DspDownRspUp),
            TosRefill(),
        });

        yield return new(0x29, "R>", OperandShape.None, new[]
        {
            Fetch(SpOp.RspDown),
            SpWrite(Src.Tos, AMode.DStack, SpOp.DspUp),
            SpRead(AMode.RPcLo, Dst.Tos, trst: true),
        });

        yield return new(0x2A, "R@", OperandShape.None, new[]
        {
            Fetch(SpOp.RspDown),
            SpWrite(Src.Tos, AMode.DStack, SpOp.DspUp),
            SpRead(AMode.RPcLo, Dst.Tos, SpOp.RspUp, trst: true),
        });

        // MEMORY / I-O (0x3_) -------------------------------------------------
        yield return new(0x30, "PUSH", OperandShape.Imm, new[]
        {
            Fetch(),
            SpWrite(Src.Tos, AMode.DStack, SpOp.DspUp),
            new Step(new MicroOp(Src.Ram, Dst.Tos, Amode: AMode.Pc), Trst: true),
        });

        yield return new(0x31, "LOAD", OperandShape.Addr, new[]
        {
            Fetch(),
            Consume(Dst.AdrLo),
            Consume(Dst.AdrHi),
            SpWrite(Src.Tos, AMode.DStack, SpOp.DspUp),
            new Step(new MicroOp(Src.Ram, Dst.Tos, Amode: AMode.Adr), Trst: true),
        });

        yield return new(0x32, "STORE", OperandShape.Addr, new[]
        {
            Fetch(),
            Consume(Dst.AdrLo),
            Consume(Dst.AdrHi),
            new Step(new MicroOp(Src.Tos, Dst.RamWe, Amode: AMode.Adr, Sp: SpOp.DspDown)),
            TosRefill(),
        });

        yield return new(0x33, "FETCH", OperandShape.None, new[]
        {
            Fetch(),
            Move(Src.Tos, Dst.AdrLo),
            new Step(new MicroOp(Src.Ram, Dst.Tos, Amode: AMode.ZpAdrLo), Trst: true),
        });

        yield return new(0x34, "STORE!", OperandShape.None, new[]
        {
            Fetch(SpOp.DspDown),
            Move(Src.Tos, Dst.AdrLo),
            SpRead(AMode.DStack, Dst.B, SpOp.DspDown),
            new Step(new MicroOp(Src.Alu, Dst.RamWe, AluFn: AluFn.Fb, Amode: AMode.ZpAdrLo)),
            TosRefill(),
        });

        yield return new(0x35, "IN", OperandShape.Port, new[]
        {
            Fetch(),
            Consume(Dst.AdrLo),
            SpWrite(Src.Tos, AMode.DStack, SpOp.DspUp),
            new Step(new MicroOp(Src.Ram, Dst.Tos, Amode: AMode.IoAdrLo), Trst: true),
        });

        yield return new(0x36, "OUT", OperandShape.Port, new[]
        {
            Fetch(),
            Consume(Dst.AdrLo),
            new Step(new MicroOp(Src.Tos, Dst.RamWe, Amode: AMode.IoAdrLo, Sp: SpOp.DspDown)),
            TosRefill(),
        });

        yield return new(0x37, "INS", OperandShape.None, new[]
        {
            Fetch(),
            Move(Src.Tos, Dst.AdrLo),
            new Step(new MicroOp(Src.Ram, Dst.Tos, Amode: AMode.IoAdrLo), Trst: true),
        });

        yield return new(0x38, "OUTS", OperandShape.None, new[]
        {
            Fetch(SpOp.DspDown),
            Move(Src.Tos, Dst.AdrLo),
            SpRead(AMode.DStack, Dst.B, SpOp.DspDown),
            new Step(new MicroOp(Src.Alu, Dst.RamWe, AluFn: AluFn.Fb, Amode: AMode.IoAdrLo)),
            TosRefill(),
        });

        // FRAME (0x8_) --------------------------------------------------------
        yield return new(0x80, "ENTER", OperandShape.Count, new[]
        {
            Fetch(),
            Consume(Dst.B),
            Move(Src.Bp, Dst.A),
            AluTo(Dst.Bp, AluFn.AddAB, trst: true),
        });

        yield return new(0x81, "LOCAL@", OperandShape.Offset, new[]
        {
            Fetch(),
            Consume(Dst.B),
            Move(Src.Bp, Dst.A),
            SpWrite(Src.Tos, AMode.DStack, SpOp.DspUp),
            AluTo(Dst.AdrLo, AluFn.AddAB),
            new Step(new MicroOp(Src.Ram, Dst.Tos, Amode: AMode.ZpAdrLo), Trst: true),
        });

        yield return new(0x82, "LOCAL!", OperandShape.Offset, new[]
        {
            Fetch(),
            Consume(Dst.B),
            Move(Src.Bp, Dst.A),
            AluTo(Dst.AdrLo, AluFn.AddAB),
            new Step(new MicroOp(Src.Tos, Dst.RamWe, Amode: AMode.ZpAdrLo, Sp: SpOp.DspDown)),
            TosRefill(),
        });

        // FLOW (0x9_) ---------------------------------------------------------
        yield return new(0x90, "JUMP", OperandShape.Addr, JumpSteps());

        yield return new(0x91, "CALL", OperandShape.Addr, new[]
        {
            Fetch(),
            Consume(Dst.B),
            Consume(Dst.A),
            SpWrite(Src.PcbLo, AMode.RPcLo),
            SpWrite(Src.PcbHi, AMode.RPcHi),
            SpWrite(Src.Bp,    AMode.RBp, SpOp.RspUp),
            AluTo(Dst.PcLo, AluFn.Fb),
            AluTo(Dst.PcHi, AluFn.Fa, trst: true),
        });

        // CMP: shares SUB's '181 code, so it cannot live in the quadrant.
        // Placed at 0x96 in FLOW, whose low six bits (010110) read as SUB
        // through ALUFN=Ir; non-destructive (NOS restored, TOS held).
        yield return new(0x96, "CMP", OperandShape.None, new[]
        {
            Fetch(SpOp.DspDown),
            SpRead(AMode.DStack, Dst.B, SpOp.DspUp),
            Move(Src.Tos, Dst.A),
            new Step(new MicroOp(AluFn: AluFn.Ir, Amode: AMode.Adr, NzWe: true, CWe: true), Trst: true),
        });

        foreach (var (op, name) in new[]
                 { (0x9A, "BEQ"), (0x9B, "BNE"), (0x9C, "BCS"),
                   (0x9D, "BCC"), (0x9E, "BMI"), (0x9F, "BPL") })
        {
            yield return new(op, name, OperandShape.Addr,
                Steps: new[]
                {
                    Fetch(),
                    new Step(new MicroOp(Src.Ram, Dst.None, Amode: AMode.Pc)),   // skip lo (PC++)
                    new Step(new MicroOp(Src.Ram, Dst.None, Amode: AMode.Pc), Trst: true), // skip hi
                },
                TakenSteps: JumpSteps());
        }

        // HALT anchor ---------------------------------------------------------
        yield return new(0xFF, "HALT", OperandShape.None, new[]
        {
            Fetch(),
            new Step(new MicroOp(Dst: Dst.Halt, Amode: AMode.Adr), Trst: true),
        });
    }

    static Step[] JumpSteps() => new[]
    {
        Fetch(),
        Consume(Dst.B),
        Consume(Dst.A),
        AluTo(Dst.PcLo, AluFn.Fb),
        AluTo(Dst.PcHi, AluFn.Fa, trst: true),
    };

    // ---- the ALU quadrant (0x40-0x7F), generated from '181 codes -----------
    //
    // Every quadrant opcode is a valid instruction: the opcode's low six bits
    // are the '181 function, routed by ALUFN=Ir. Named ops get their mnemonic;
    // the rest are ALU #n. Three named ops have special microprograms (NOT
    // unary, TST flags-only); the default is the 4-T binary op. C is written
    // when M = 0 (arithmetic); logic (M = 1) preserves it.

    static IEnumerable<Instruction> AluQuadrant()
    {
        var named = new Dictionary<int, string>
        {
            [Add.QuadrantOpcode] = "ADD",
            [Adc.QuadrantOpcode] = "ADC",
            [Sub.QuadrantOpcode] = "SUB",
            [Xor.QuadrantOpcode] = "XOR",
            [And.QuadrantOpcode] = "AND",
            [Or.QuadrantOpcode]  = "OR",
            [NotA.QuadrantOpcode] = "NOT",
            [Fa.QuadrantOpcode]   = "TST",
        };

        for (int op = 0x40; op <= 0x7F; op++)
        {
            bool m = (op & 0x20) != 0;          // logic when set
            bool writesC = !m;                  // arithmetic writes carry
            string name = named.TryGetValue(op, out var n) ? n : $"ALU_{op:X2}";

            Step[] steps;
            if (name == "NOT")
            {
                steps = new[]
                {
                    Fetch(),
                    Move(Src.Tos, Dst.A),
                    AluTo(Dst.Tos, AluFn.Ir, nz: true, trst: true),
                };
            }
            else if (name == "TST")
            {
                steps = new[]
                {
                    Fetch(),
                    Move(Src.Tos, Dst.A),
                    new Step(new MicroOp(AluFn: AluFn.Ir, Amode: AMode.Adr, NzWe: true), Trst: true),
                };
            }
            else
            {
                steps = new[]
                {
                    Fetch(),
                    new Step(new MicroOp(Src.Tos, Dst.A, Amode: AMode.Adr, Sp: SpOp.DspDown)),
                    SpRead(AMode.DStack, Dst.B),
                    AluTo(Dst.Tos, AluFn.Ir, nz: true, c: writesC, trst: true),
                };
            }
            yield return new(op, name, OperandShape.None, steps);
        }
    }

    // ---- the interrupt-entry micro-sequence (INTP = 1, all opcodes) --------

    public static readonly Step[] Entry =
    {
        SpWrite(Src.PcbLo, AMode.RPcLo),
        SpWrite(Src.PcbHi, AMode.RPcHi),
        SpWrite(Src.Flags, AMode.RFlags),
        SpWrite(Src.Bp,    AMode.RBp, SpOp.RspUp),
        new Step(new MicroOp(Src.Vec, Dst.PcLo, Amode: AMode.Adr, ISet: true)),
        new Step(new MicroOp(Src.Vec, Dst.PcHi, Amode: AMode.Adr), Trst: true),
    };

    /// <summary>Fill for unassigned opcodes and unreachable T-states: halt.</summary>
    public static readonly Step IllegalFill =
        new Step(new MicroOp(Dst: Dst.Halt, Amode: AMode.Adr), Trst: true);

    // ---- assembled table + invariants --------------------------------------

    static List<Instruction>? cache;

    public static IReadOnlyList<Instruction> All
    {
        get
        {
            if (cache is not null) return cache;
            var list = Fixed().Concat(AluQuadrant())
                              .OrderBy(i => i.Opcode).ToList();

            // Invariant: no duplicate opcodes.
            var seen = new HashSet<int>();
            foreach (var i in list)
                if (!seen.Add(i.Opcode))
                    throw new InvalidOperationException($"Duplicate opcode 0x{i.Opcode:X2} ({i.Mnemonic})");

            // Invariant: CMP's low six bits equal the SUB '181 function code,
            // so ALUFN=Ir yields subtract. Guards against a silent renumber.
            var cmp = list.First(i => i.Mnemonic == "CMP");
            int cmpFn = cmp.Opcode & 0x3F;
            int subFn = (Sub.M ? 0x20 : 0) | (Sub.Cn ? 0x10 : 0) | (Sub.S & 0xF);
            if (cmpFn != subFn)
                throw new InvalidOperationException(
                    $"CMP opcode low6 (0x{cmpFn:X2}) must equal the SUB '181 code (0x{subFn:X2})");

            // Invariant: every step's terminal is well-formed.
            foreach (var i in list)
            {
                if (i.Steps.Count == 0 || !i.Steps[^1].Trst)
                    throw new InvalidOperationException($"{i.Mnemonic}: last step must set TRST");
                if (i.Steps.Count > 8)
                    throw new InvalidOperationException($"{i.Mnemonic}: more than 8 T-states");
                if (i.TakenSteps is { } t && (t.Count == 0 || !t[^1].Trst))
                    throw new InvalidOperationException($"{i.Mnemonic}: taken path must set TRST");
            }

            cache = list;
            return cache;
        }
    }
}
