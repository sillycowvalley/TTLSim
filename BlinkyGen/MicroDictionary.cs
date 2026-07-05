namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// The micro-op dictionary: the distinct MicroOp values across the whole
// instruction set (every step of every opcode, both branch paths, and the
// interrupt-entry sequence), each assigned a stable 7-bit index.
//
// This is the pivot of the two-stage control store: Stage 1 emits an index,
// Stage 2 decodes it. 7 bits give 128 slots; the machine uses ~57.
// ---------------------------------------------------------------------------

public sealed class MicroDictionary
{
    public IReadOnlyList<MicroOp> Ops { get; }
    readonly Dictionary<MicroOp, int> indexOf;

    public const int IndexBits = 7;
    public const int Slots = 1 << IndexBits;

    public int Count => Ops.Count;
    public int Index(MicroOp op) => indexOf[op];

    public MicroDictionary(IReadOnlyList<Instruction> program)
    {
        var distinct = new HashSet<MicroOp>();
        foreach (var ins in program)
        {
            foreach (var s in ins.Steps) distinct.Add(s.Op);
            if (ins.TakenSteps is { } t)
                foreach (var s in t) distinct.Add(s.Op);
        }
        foreach (var s in InstructionSet.Entry) distinct.Add(s.Op);
        distinct.Add(InstructionSet.IllegalFill.Op);

        // Stable, deterministic order: sort by the packed decoder-line value so
        // indices cluster on-sets for the fitter and never shuffle between runs.
        var ordered = distinct
            .OrderBy(PackKey)
            .ToList();

        if (ordered.Count > Slots)
            throw new InvalidOperationException(
                $"{ordered.Count} micro-ops exceeds the {Slots}-slot index");

        Ops = ordered;
        indexOf = new Dictionary<MicroOp, int>();
        for (int i = 0; i < ordered.Count; i++) indexOf[ordered[i]] = i;
    }

    static int PackKey(MicroOp op)
    {
        var bits = op.DecoderLines();
        int v = 0;
        for (int i = 0; i < bits.Length; i++) v |= bits[i] << i;
        return v;
    }

    /// <summary>The value of decoder line <paramref name="line"/> for index
    /// <paramref name="idx"/>; used indices only. Unused indices are
    /// don't-cares to the caller.</summary>
    public int LineValue(int idx, int line) => Ops[idx].DecoderLines()[line];
}
