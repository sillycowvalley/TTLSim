using System.Text;

namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// Emits the CUPL/WinCUPL sources for both GAL stages.
//
// Stage 2 (decoder): two combinational 22V10s, UOPA and UOPB, mapping the
// 7-bit micro-op index to the 20 control lines. Pure SOP, minimized here.
//
// Stage 1 (sequencer): one registered 22V10 per family plus the entry bank.
// Inputs are opcode-low[3:0] and COND; the T-state is held in the GAL's own
// registers (next-state = T+1, or 0 when /TRST). Outputs are IDX[6:0] and
// /TRST. Reset-to-T0 is the 22V10 asynchronous reset, driven by /RST.
//
// These are fitter inputs: BlinkyJED / WinCUPL produce the fuse maps and the
// authoritative term/fit report.
// ---------------------------------------------------------------------------

public static class PldEmitter
{
    public static void EmitAll(string dir, MicroDictionary dict, Sequencer seq)
    {
        EmitDecoder(Path.Combine(dir, "BLINKY_M_UOPA.pld"), dict, half: 0);
        EmitDecoder(Path.Combine(dir, "BLINKY_M_UOPB.pld"), dict, half: 1);

        foreach (int fam in seq.Families.Where(f => f != 0xF))  // 0xF folds into CTL bank
            EmitSequencer(dir, seq, dict, fam);
        EmitEntrySequencer(Path.Combine(dir, "BLINKY_M_SEQ_ENTRY.pld"), seq, dict);
    }

    // ---- Stage 2 -----------------------------------------------------------

    static readonly string[] IdxNames = { "IDX0", "IDX1", "IDX2", "IDX3", "IDX4", "IDX5", "IDX6" };

    // Macrocell term capacities of a 22V10 (graduated).
    static readonly int[] V10Caps = { 8, 10, 12, 14, 16, 16, 14, 12, 10, 8 };

    static void EmitDecoder(string path, MicroDictionary dict, int half)
    {
        var lineIndices = half == 0
            ? Enumerable.Range(0, 10).ToArray()      // SRC,DST,ALUFN,ISET group
            : Enumerable.Range(10, 10).ToArray();    // AMODE,SPOP,TOS,flags group

        // In the folded 20-line vector the two halves are contiguous except
        // ISET (index 19) belongs with UOPA and TOSSH..C_WE with UOPB. Re-pick
        // explicitly to match the design doc's UOPA/UOPB split.
        int[] uopaLines = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 19 };  // SRC,DST,ALUFN,ISET
        int[] uopbLines = { 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 }; // AMODE,SPOP,TOSSH,NZ,C
        var lines = half == 0 ? uopaLines : uopbLines;

        var min = new Minimizer(MicroDictionary.IndexBits);
        var results = new List<(string name, IReadOnlyList<Minimizer.Cube> cubes, bool inv)>();

        foreach (int line in lines)
        {
            var value = new bool[MicroDictionary.Slots];
            var care = new bool[MicroDictionary.Slots];
            for (int idx = 0; idx < dict.Count; idx++)
            {
                care[idx] = true;
                value[idx] = dict.LineValue(idx, line) != 0;
            }
            var (cubes, inv) = min.Best(value, care);
            results.Add((MicroOp.DecoderLineNames[line], cubes, inv));
        }

        // Assign heavier lines to wider macrocells.
        var order = results.OrderByDescending(r => r.cubes.Count).ToList();
        var pins = new[] { 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 };
        var capByPin = new Dictionary<int, int>
        {
            [14] = 8, [15] = 10, [16] = 12, [17] = 14, [18] = 16,
            [19] = 16, [20] = 14, [21] = 12, [22] = 10, [23] = 8
        };
        var pinOrder = pins.OrderByDescending(p => capByPin[p]).ToList();
        var assign = order.Zip(pinOrder, (r, p) => (r, p))
                          .OrderBy(x => x.p).ToList();

        var sb = new StringBuilder();
        string name = half == 0 ? "UOPA" : "UOPB";
        sb.Append(Header(name,
            $"Stage 2 decoder {(half == 0 ? "A" : "B")} of 2 — micro-op index -> control lines.\n" +
            " * Combinational. Inputs IDX0..6 (sequence ROM/GAL output).\n" +
            $" * Dictionary size {dict.Count} of {MicroDictionary.Slots}; unused indices are don't-cares."));
        foreach (var (r, p) in assign)
            sb.AppendLine($"PIN {p,-2} = {r.name};");
        sb.AppendLine();

        var minR = new Minimizer(MicroDictionary.IndexBits);
        foreach (var (r, p) in assign)
        {
            int cap = capByPin[p];
            var terms = r.cubes.Select(c => minR.CubeToCupl(c, IdxNames)).ToList();
            string body = string.Join("\n     # ", terms);
            sb.AppendLine($"/* {r.name} (pin {p}, {terms.Count} term(s) of {cap}{(r.inv ? ", active-low" : "")}) */");
            sb.AppendLine(r.inv ? $"!{r.name} = {body};" : $"{r.name} = {body};");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    // ---- Stage 1 -----------------------------------------------------------

    static void EmitSequencer(string dir, Sequencer seq, MicroDictionary dict, int fam)
    {
        string fname = Sequencer.FamilyName(fam);
        string path = Path.Combine(dir, $"BLINKY_M_SEQ_{fname}.pld");

        // Inputs: OPL0..3 (opcode low nibble), COND. State: T0..T2 registered.
        // Outputs: IDX0..6 (combinational), TRSTN (/TRST, registered-safe),
        //          and next T is the registered T+1 / 0.
        string[] inputs = { "OPL0", "OPL1", "OPL2", "OPL3", "COND", "T0", "T1", "T2" };
        // input encoding order for the minimizer: OPL0..3=bits0..3, COND=bit4, T0..2=bits5..7
        int bits = 8;
        var min = new Minimizer(bits);

        // Build the per-line truth tables across the 256-point (opLow,cond,T) space.
        bool[][] idxVal = NewGrid(MicroDictionary.IndexBits, 1 << bits);
        var idxCare = new bool[1 << bits];
        var trstVal = new bool[1 << bits];

        for (int opl = 0; opl < 16; opl++)
            for (int cond = 0; cond < 2; cond++)
                for (int t = 0; t < Sequencer.MaxT; t++)
                {
                    var cell = seq.Bank(fam, opl, t, cond);
                    int addr = opl | (cond << 4) | (t << 5);
                    idxCare[addr] = cell.Defined;
                    trstVal[addr] = cell.Trst;
                    for (int b = 0; b < MicroDictionary.IndexBits; b++)
                        idxVal[b][addr] = ((cell.Index >> b) & 1) != 0;
                }

        var sb = new StringBuilder();
        sb.Append(Header($"SEQ_{fname}",
            $"Stage 1 sequencer for the {fname} family (opcode nibble 0x{fam:X}_).\n" +
            " * Registered 22V10. Inputs OPL0..3 (opcode low), COND; the T-state\n" +
            "   is held in this GAL's registers (T0..T2), next = T+1 or 0 on /TRST.\n" +
            " * Reset-to-T0 via the async-reset (AR) product term on /RST.\n" +
            " * Outputs IDX0..6 select the micro-op; the decoder GALs expand it.\n" +
            " * DRAFT — the registered next-state and AR equations below are a\n" +
            "   template for the fitter; BlinkyJED/WinCUPL produce the fuse map."));

        sb.AppendLine("PIN 1  = CLK;");
        sb.AppendLine("PIN 2  = OPL0;");
        sb.AppendLine("PIN 3  = OPL1;");
        sb.AppendLine("PIN 4  = OPL2;");
        sb.AppendLine("PIN 5  = OPL3;");
        sb.AppendLine("PIN 6  = COND;");
        sb.AppendLine("PIN 7  = !RST;     /* active-low reset -> async T0 */");
        sb.AppendLine();
        sb.AppendLine("PIN 14 = IDX0;");
        sb.AppendLine("PIN 15 = IDX1;");
        sb.AppendLine("PIN 16 = IDX2;");
        sb.AppendLine("PIN 17 = IDX3;");
        sb.AppendLine("PIN 18 = IDX4;");
        sb.AppendLine("PIN 19 = IDX5;");
        sb.AppendLine("PIN 20 = IDX6;");
        sb.AppendLine("PIN 21 = T0;      /* registered T-state, fed back */");
        sb.AppendLine("PIN 22 = T1;");
        sb.AppendLine("PIN 23 = T2;");
        sb.AppendLine("PIN 13 = TRSTN;   /* /TRST, low = end of instruction */");
        sb.AppendLine();

        string[] names = { "OPL0", "OPL1", "OPL2", "OPL3", "COND", "T0", "T1", "T2" };

        for (int b = 0; b < MicroDictionary.IndexBits; b++)
        {
            var (cubes, inv) = min.Best(idxVal[b], idxCare);
            var terms = cubes.Select(c => min.CubeToCupl(c, names)).ToList();
            string body = string.Join("\n     # ", terms);
            sb.AppendLine($"/* IDX{b}: {terms.Count} term(s){(inv ? ", active-low" : "")} */");
            sb.AppendLine(inv ? $"!IDX{b} = {body};" : $"IDX{b} = {body};");
            sb.AppendLine();
        }

        // /TRST as a combinational output over the same inputs.
        {
            var (cubes, inv) = min.Best(trstVal, idxCare);
            var terms = cubes.Select(c => min.CubeToCupl(c, names)).ToList();
            string body = string.Join("\n     # ", terms);
            sb.AppendLine($"/* TRSTN — asserted (low) at end of instruction: {terms.Count} term(s) */");
            // TRST is active in the table (true = end); TRSTN is its complement.
            sb.AppendLine(inv ? $"TRSTN = {body};" : $"!TRSTN = {body};");
            sb.AppendLine();
        }

        sb.AppendLine("/* Registered T-state: next = 0 on /TRST or /RST, else T+1.");
        sb.AppendLine("   Written as a template; the fitter realises the ripple. */");
        sb.AppendLine("FIELD Tstate = [T2..0];");
        sb.AppendLine("T0.d = !TRSTN & !T0;");
        sb.AppendLine("T1.d = !TRSTN & (T1 $ T0);");
        sb.AppendLine("T2.d = !TRSTN & (T2 $ (T1 & T0));");
        sb.AppendLine("T0.ar = !RST;   T1.ar = !RST;   T2.ar = !RST;");

        File.WriteAllText(path, sb.ToString());
    }

    static void EmitEntrySequencer(string path, Sequencer seq, MicroDictionary dict)
    {
        string[] names = { "T0", "T1", "T2" };
        var min = new Minimizer(3);
        int space = 8;

        bool[][] idxVal = NewGrid(MicroDictionary.IndexBits, space);
        var care = new bool[space];
        var trst = new bool[space];
        for (int t = 0; t < Sequencer.MaxT && t < space; t++)
        {
            var cell = seq.EntryAt(t);
            care[t] = cell.Defined;
            trst[t] = cell.Trst;
            for (int b = 0; b < MicroDictionary.IndexBits; b++)
                idxVal[b][t] = ((cell.Index >> b) & 1) != 0;
        }

        var sb = new StringBuilder();
        sb.Append(Header("SEQ_ENTRY",
            "Stage 1 sequencer for interrupt entry (INTP = 1, opcode-independent).\n" +
            " * Registered 22V10 enabled by INTP; T-state is its only live input.\n" +
            " * Same registered-T / async-reset structure as the family banks."));
        sb.AppendLine("PIN 1  = CLK;");
        sb.AppendLine("PIN 7  = !RST;");
        sb.AppendLine("PIN 21 = T0;");
        sb.AppendLine("PIN 22 = T1;");
        sb.AppendLine("PIN 23 = T2;");
        sb.AppendLine("PIN 14 = IDX0;  PIN 15 = IDX1;  PIN 16 = IDX2;  PIN 17 = IDX3;");
        sb.AppendLine("PIN 18 = IDX4;  PIN 19 = IDX5;  PIN 20 = IDX6;");
        sb.AppendLine("PIN 13 = TRSTN;");
        sb.AppendLine();

        for (int b = 0; b < MicroDictionary.IndexBits; b++)
        {
            var (cubes, inv) = min.Best(idxVal[b], care);
            var terms = cubes.Select(c => min.CubeToCupl(c, names)).ToList();
            sb.AppendLine($"/* IDX{b}: {terms.Count} term(s){(inv ? ", active-low" : "")} */");
            sb.AppendLine(inv ? $"!IDX{b} = {string.Join(" # ", terms)};"
                              : $"IDX{b} = {string.Join(" # ", terms)};");
        }
        sb.AppendLine();
        sb.AppendLine("T0.ar = !RST;  T1.ar = !RST;  T2.ar = !RST;");
        File.WriteAllText(path, sb.ToString());
    }

    static bool[][] NewGrid(int rows, int cols)
    {
        var g = new bool[rows][];
        for (int i = 0; i < rows; i++) g[i] = new bool[cols];
        return g;
    }

    static string Header(string name, string blurb) =>
        $"""
        /* BLINKY_M_{name} — Blinky-M rev 14 GAL control          GENERATED
         *
         * Emitted by BlinkyMGen from the canonical instruction table. Do not
         * hand-edit: regenerate. Verify with BlinkyJED, cross-check in WinCUPL.
         *
        {string.Join("\n", blurb.Split('\n').Select(l => " * " + l))}
         */
        Name     {name};
        PartNo   00;
        Date     generated;
        Revision 14;
        Designer BlinkyMGen;
        Company  ;
        Assembly ;
        Location ;
        Device   g22v10;

        """;
}
