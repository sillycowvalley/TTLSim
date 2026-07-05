namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// Stage 1 sequencing model. For each family bank, and for the interrupt-entry
// bank, this gives the micro-op index and the /TRST bit for every reachable
// (opcode-low, T, COND) point. It is the truth table the per-family registered
// 22V10s implement; the T-state itself is held in the GAL's registers, so the
// live inputs are opcode-low[3:0], COND (and INTP selects the entry bank).
// ---------------------------------------------------------------------------

public sealed class Sequencer
{
    public sealed record Cell(int Index, bool Trst, bool Defined);

    public IReadOnlyList<int> Families { get; }           // high nibbles present
    public const int MaxT = 8;

    readonly MicroDictionary dict;
    // family -> [opLow (16)][T (8)][cond (2)] = Cell
    readonly Dictionary<int, Cell[,,]> banks = new();
    readonly Cell[,] entry;                                // [T (8)][unused]

    public Sequencer(IReadOnlyList<Instruction> program, MicroDictionary dictionary)
    {
        dict = dictionary;
        var fillCell = new Cell(dict.Index(InstructionSet.IllegalFill.Op), true, false);

        var byFamily = program.GroupBy(i => i.Opcode >> 4)
                              .ToDictionary(g => g.Key, g => g.ToList());
        Families = byFamily.Keys.OrderBy(k => k).ToList();

        foreach (var (fam, instrs) in byFamily)
        {
            var grid = new Cell[16, MaxT, 2];
            for (int lo = 0; lo < 16; lo++)
                for (int t = 0; t < MaxT; t++)
                    for (int c = 0; c < 2; c++)
                        grid[lo, t, c] = fillCell;

            foreach (var ins in instrs)
            {
                int lo = ins.Opcode & 0xF;
                Fill(grid, lo, ins.Steps, condColumn: 0);
                var taken = ins.TakenSteps ?? ins.Steps;
                Fill(grid, lo, taken, condColumn: 1);
            }
            banks[fam] = grid;
        }

        // Interrupt entry: opcode-independent, T-indexed only.
        entry = new Cell[MaxT, 1];
        for (int t = 0; t < MaxT; t++) entry[t, 0] = fillCell;
        for (int t = 0; t < InstructionSet.Entry.Length; t++)
        {
            var s = InstructionSet.Entry[t];
            entry[t, 0] = new Cell(dict.Index(s.Op), s.Trst, true);
        }
    }

    void Fill(Cell[,,] grid, int lo, IReadOnlyList<Step> steps, int condColumn)
    {
        for (int t = 0; t < steps.Count; t++)
        {
            var s = steps[t];
            grid[lo, t, condColumn] = new Cell(dict.Index(s.Op), s.Trst, true);
        }
    }

    public Cell Bank(int family, int opLow, int t, int cond) => banks[family][opLow, t, cond];
    public Cell EntryAt(int t) => entry[t, 0];
    public bool HasFamily(int family) => banks.ContainsKey(family);

    public static string FamilyName(int nibble) => nibble switch
    {
        0x0 => "CTL",
        0x1 => "SHIFT",
        0x2 => "STK",
        0x3 => "MEM",
        0x4 or 0x5 or 0x6 or 0x7 => "ALU",
        0x8 => "FRM",
        0x9 => "FLOW",
        0xF => "CTLX",
        _ => $"F{nibble:X}"
    };
}
