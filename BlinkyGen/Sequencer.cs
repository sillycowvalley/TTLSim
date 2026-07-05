namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// Stage 1 sequencing model. For each bank this gives the micro-op index and
// the /TRST bit at every reachable input point. Banks:
//
//   CTL   (0x0_)   SHIFT (0x1_)   STK (0x2_)   MEM (0x3_)
//   ALU   (0x4_..0x7_) merged, keyed by opcode[5:0]  (the '138 ORs the four
//                       nibble enables into one bank enable)
//   FRM   (0x8_)   FLOW  (0x9_)
//   CTLX  (0xF_)   the HALT anchor bank (one opcode, 0xFF)
//   ENTRY          interrupt entry, opcode-independent
//
// The T-state is produced by an external '161 counter (as in stage 0) and
// enters each GAL as inputs T0..2; /TRST (active-low) drives the counter's
// /LOAD. The GALs are therefore purely combinational.
// ---------------------------------------------------------------------------

public sealed class Sequencer
{
    public sealed record Cell(int Index, bool Trst, bool Defined);

    public const int MaxT = 8;

    // Bank identity: a nibble for the simple families, or the synthetic "ALU".
    public sealed record Bank(string Name, bool Alu, int Nibble);

    public IReadOnlyList<Bank> Banks { get; }

    readonly int fillIndex;
    // simple banks: name -> [opLow 16][T 8][cond 2]
    readonly Dictionary<string, Cell[,,]> simple = new();
    // ALU bank: [op6 64][T 8]
    readonly Cell[,] alu;
    // entry: [T 8]
    readonly Cell[] entry;

    public Sequencer(IReadOnlyList<Instruction> program, MicroDictionary dict)
    {
        fillIndex = dict.Index(InstructionSet.IllegalFill.Op);
        var fill = new Cell(fillIndex, true, false);

        var banks = new List<Bank>();

        // Simple families (everything except the ALU quadrant).
        var simpleFamilies = program
            .Where(i => !(i.Opcode >= 0x40 && i.Opcode <= 0x7F))
            .GroupBy(i => i.Opcode >> 4)
            .OrderBy(g => g.Key);

        foreach (var g in simpleFamilies)
        {
            string name = FamilyName(g.Key);
            var grid = new Cell[16, MaxT, 2];
            for (int lo = 0; lo < 16; lo++)
                for (int t = 0; t < MaxT; t++)
                    for (int c = 0; c < 2; c++)
                        grid[lo, t, c] = fill;
            foreach (var ins in g)
            {
                int lo = ins.Opcode & 0xF;
                FillSteps(grid, lo, ins.Steps, 0, dict);
                FillSteps(grid, lo, ins.TakenSteps ?? ins.Steps, 1, dict);
            }
            simple[name] = grid;
            banks.Add(new Bank(name, false, g.Key));
        }

        // ALU quadrant merged into one bank keyed by opcode[5:0].
        alu = new Cell[64, MaxT];
        for (int o = 0; o < 64; o++)
            for (int t = 0; t < MaxT; t++)
                alu[o, t] = fill;
        foreach (var ins in program.Where(i => i.Opcode >= 0x40 && i.Opcode <= 0x7F))
        {
            int o6 = ins.Opcode & 0x3F;
            for (int t = 0; t < ins.Steps.Count; t++)
            {
                var s = ins.Steps[t];
                alu[o6, t] = new Cell(dict.Index(s.Op), s.Trst, true);
            }
        }
        banks.Add(new Bank("ALU", true, 0x4));

        // Interrupt entry.
        entry = new Cell[MaxT];
        for (int t = 0; t < MaxT; t++) entry[t] = fill;
        for (int t = 0; t < InstructionSet.Entry.Length; t++)
        {
            var s = InstructionSet.Entry[t];
            entry[t] = new Cell(dict.Index(s.Op), s.Trst, true);
        }

        Banks = banks;
    }

    static void FillSteps(Cell[,,] grid, int lo, IReadOnlyList<Step> steps, int cond, MicroDictionary dict)
    {
        for (int t = 0; t < steps.Count; t++)
        {
            var s = steps[t];
            grid[lo, t, cond] = new Cell(dict.Index(s.Op), s.Trst, true);
        }
    }

    public Cell Simple(string bank, int opLow, int t, int cond) => simple[bank][opLow, t, cond];
    public Cell Alu(int op6, int t) => alu[op6, t];
    public Cell EntryAt(int t) => entry[t];

    public static string FamilyName(int nibble) => nibble switch
    {
        0x0 => "CTL",
        0x1 => "SHIFT",
        0x2 => "STK",
        0x3 => "MEM",
        0x8 => "FRM",
        0x9 => "FLOW",
        0xF => "CTLX",
        _ => $"F{nibble:X}"
    };
}
