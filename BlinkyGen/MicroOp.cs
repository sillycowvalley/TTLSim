namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// A single rev 14 micro-op: one T-state of datapath action, in folded form.
//
// The instruction table is a list of these per opcode. The dictionary
// deduplicates them into the ~57 distinct combinations and assigns each a
// 7-bit index; the Stage 2 GALs decode index -> these fields.
//
// TRST is NOT part of the micro-op — it belongs to sequencing (Stage 1), so
// it lives on the Instruction step, not here. Likewise PCINC is derived
// (Src == Ram && Amode == Pc), TOS load is implied by (Dst == Tos), and the
// page/SP-select are folded into Amode. What remains is the 20 decoder lines.
// ---------------------------------------------------------------------------

public readonly record struct MicroOp(
    Src Src = Src.Ram,
    Dst Dst = Dst.None,
    AluFn AluFn = AluFn.Ir,
    AMode Amode = AMode.Adr,
    SpOp Sp = SpOp.None,
    bool TosShift = false,
    bool NzWe = false,
    bool CWe = false,
    bool ISet = false)
{
    /// <summary>PCINC is derived, not stored: an instruction-stream read is
    /// the only case the PC should advance, and it is exactly a RAM read at
    /// the PC address.</summary>
    public bool PcInc => Src == Src.Ram && Amode == AMode.Pc;

    /// <summary>TOS parallel-load is implied by targeting TOS from the bus.</summary>
    public bool TosLoad => Dst == Dst.Tos && !TosShift;

    /// <summary>The 20 Stage-2 decoder lines, MSB-first per field, as a flat
    /// bit vector. Used by the minimizer and the matrix. Order here defines
    /// the line order everywhere downstream.</summary>
    public int[] DecoderLines()
    {
        int src = (int)Src, dst = (int)Dst, fn = (int)AluFn, am = (int)Amode, sp = (int)Sp;
        return new[]
        {
            src & 1, (src >> 1) & 1, (src >> 2) & 1,               // SRC0..2
            dst & 1, (dst >> 1) & 1, (dst >> 2) & 1, (dst >> 3) & 1, // DST0..3
            fn & 1, (fn >> 1) & 1,                                  // ALUFN0..1
            am & 1, (am >> 1) & 1, (am >> 2) & 1, (am >> 3) & 1,     // AMODE0..3
            sp & 1, (sp >> 1) & 1, (sp >> 2) & 1,                    // SPOP0..2
            TosShift ? 1 : 0,                                        // TOSSH
            NzWe ? 1 : 0,                                            // NZ_WE
            CWe ? 1 : 0,                                             // C_WE
            ISet ? 1 : 0                                             // ISET
        };
    }

    public static readonly string[] DecoderLineNames =
    {
        "SRC0", "SRC1", "SRC2",
        "DST0", "DST1", "DST2", "DST3",
        "ALUFN0", "ALUFN1",
        "AMODE0", "AMODE1", "AMODE2", "AMODE3",
        "SPOP0", "SPOP1", "SPOP2",
        "TOSSH", "NZ_WE", "C_WE", "ISET"
    };

    /// <summary>Compact human tag for the matrix and listings.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Dst != Dst.None || Src != Src.Ram || Amode != AMode.Adr)
            parts.Add($"{Src}\u2192{Dst} @{Amode}");
        if (AluFn != AluFn.Ir) parts.Add($"ALU={AluFn}");
        else if (Src == Src.Alu || NzWe) parts.Add("ALU=IR");
        if (Sp != SpOp.None) parts.Add(Sp.ToString());
        if (TosShift) parts.Add("SHIFT");
        if (NzWe) parts.Add("NZ");
        if (CWe) parts.Add("C");
        if (ISet) parts.Add("ISET");
        if (PcInc) parts.Add("PC++");
        return parts.Count == 0 ? "(idle)" : string.Join("  ", parts);
    }
}
