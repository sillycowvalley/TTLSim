namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// The micro-op dictionary: the distinct MicroOp values across the whole
// instruction set, each assigned a stable 7-bit index.
//
// Index assignment matters for the Stage-2 decoder GALs. If indices are sorted
// by the whole packed control vector, a decoder line like DST0 ends up 1 for a
// scattered set of indices and needs many product terms. Sorting instead by the
// fields that drive the widest lines (DST, then AMODE, then SRC) makes each of
// those lines a small number of contiguous index ranges, which minimize into
// far fewer cubes. The remaining fields tie-break for determinism.
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

        // Cluster the fat fields into contiguous index ranges. Order of keys is
        // deliberate: DST drives the widest decoder lines, then AMODE, then SRC;
        // the rest tie-break so the ordering is total and deterministic.
        var ordered = distinct
            .OrderBy(o => (int)o.Dst)
            .ThenBy(o => (int)o.Amode)
            .ThenBy(o => (int)o.Src)
            .ThenBy(o => (int)o.AluFn)
            .ThenBy(o => (int)o.Sp)
            .ThenBy(o => o.TosShift)
            .ThenBy(o => o.NzWe)
            .ThenBy(o => o.CWe)
            .ThenBy(o => o.ISet)
            .ToList();

        if (ordered.Count > Slots)
            throw new InvalidOperationException(
                $"{ordered.Count} micro-ops exceeds the {Slots}-slot index");

        Ops = ordered;
        indexOf = new Dictionary<MicroOp, int>();
        for (int i = 0; i < ordered.Count; i++) indexOf[ordered[i]] = i;
    }

    /// <summary>The value of decoder line <paramref name="line"/> for index
    /// <paramref name="idx"/> (used indices only; unused are don't-cares).</summary>
    public int LineValue(int idx, int line) => Ops[idx].DecoderLines()[line];
}
