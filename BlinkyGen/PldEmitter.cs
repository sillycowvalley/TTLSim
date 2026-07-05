using System.Text;

namespace BlinkyMGen;

// ---------------------------------------------------------------------------
// Emits CUPL/WinCUPL sources for both GAL stages, targeting BlinkyJED.
//
// Stage 2 decoders (UOPA/UOPB): combinational, index -> 20 control lines.
//
// Stage 1 sequencers: one combinational 22V10 per bank. Inputs are the opcode
// low bits, COND, and the T-state from the external '161 counter (T0..2).
// Outputs are IDX0..6 and TRSTN (active-low /TRST, which drives the counter's
// /LOAD). Eight outputs fit a 22V10 with room; there is no registered T inside
// the GAL, so no .d/.ar equations.
//
// Pin notes: on a 22V10 pins 14..23 are the ten output macrocells; pin 13 is
// input-only, so TRSTN goes on an output pin (23).
//
// Constants: a line that is 0 for every used index (e.g. IDX6) has no product
// terms. BlinkyJED rejects "NAME = ;" and the quoted-radix "'b'0", so an
// always-0 output is written as a contradiction over a present input and an
// always-1 as a tautology.
// ---------------------------------------------------------------------------

public static class PldEmitter
{
    public static void EmitAll(string dir, MicroDictionary dict, Sequencer seq)
    {
        EmitDecoder(Path.Combine(dir, "BLINKY_M_UOPA.pld"), dict, half: 0);
        EmitDecoder(Path.Combine(dir, "BLINKY_M_UOPB.pld"), dict, half: 1);

        foreach (var bank in seq.Banks)
        {
            if (bank.Alu) EmitAluSequencer(Path.Combine(dir, "BLINKY_M_SEQ_ALU.pld"), seq);
            else EmitSimpleSequencer(Path.Combine(dir, $"BLINKY_M_SEQ_{bank.Name}.pld"), seq, bank.Name);
        }
        EmitEntrySequencer(Path.Combine(dir, "BLINKY_M_SEQ_ENTRY.pld"), seq);
    }

    static string Constant(string name, bool value, string zeroRef)
        => value ? $"{name} = {zeroRef} # !{zeroRef};"
                 : $"{name} = {zeroRef} & !{zeroRef};";

    static string Equation(string name, IReadOnlyList<Minimizer.Cube> cubes,
                           bool inverted, Minimizer min, string[] inputNames, string zeroRef)
    {
        if (cubes.Count == 0)
            return Constant(name, inverted, zeroRef);
        string lhs = inverted ? "!" + name : name;
        var terms = cubes.Select(c => min.CubeToCupl(c, inputNames));
        return $"{lhs} = {string.Join("\n     # ", terms)};";
    }

    // ---- Stage 2 decoders --------------------------------------------------

    static readonly string[] IdxNames = { "IDX0", "IDX1", "IDX2", "IDX3", "IDX4", "IDX5", "IDX6" };

    static readonly Dictionary<int, int> V10CapByPin = new()
    {
        [14] = 8, [15] = 10, [16] = 12, [17] = 14, [18] = 16,
        [19] = 16, [20] = 14, [21] = 12, [22] = 10, [23] = 8
    };

    static void EmitDecoder(string path, MicroDictionary dict, int half)
    {
        int[] uopaLines = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 19 };
        int[] uopbLines = { 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 };
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

        var order = results.OrderByDescending(r => r.cubes.Count).ToList();
        var pinOrder = V10CapByPin.Keys.OrderByDescending(p => V10CapByPin[p]).ToList();
        var assign = order.Zip(pinOrder, (r, p) => (r, p)).OrderBy(x => x.p).ToList();

        var sb = new StringBuilder();
        string name = half == 0 ? "UOPA" : "UOPB";
        sb.Append(Header(name,
            $"Stage 2 decoder {(half == 0 ? "A" : "B")} of 2 - micro-op index to control lines.\n" +
            "Combinational. Inputs IDX0..6 (Stage-1 sequencer output).\n" +
            $"Dictionary size {dict.Count} of {MicroDictionary.Slots}; unused indices are don't-cares."));
        sb.AppendLine("/* inputs: micro-op index from the Stage-1 sequencer */");
        for (int i = 0; i < IdxNames.Length; i++)
            sb.AppendLine($"PIN {i + 2,-2} = {IdxNames[i]};");
        sb.AppendLine();
        foreach (var (r, p) in assign)
            sb.AppendLine($"PIN {p,-2} = {r.name};");
        sb.AppendLine();

        foreach (var (r, p) in assign)
        {
            int cap = V10CapByPin[p];
            sb.AppendLine($"/* {r.name} (pin {p}, {r.cubes.Count} term(s) of {cap}{(r.inv ? ", active-low" : "")}) */");
            sb.AppendLine(Equation(r.name, r.cubes, r.inv, min, IdxNames, "IDX0"));
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }

    // ---- Stage 1: simple family sequencer ----------------------------------
    // Inputs: OPL0..3 (opcode low nibble), COND, T0..2. 8 inputs.
    // Outputs: IDX0..6 + TRSTN. Combinational.

    static void EmitSimpleSequencer(string path, Sequencer seq, string bank)
    {
        const int bits = 8;   // OPL0..3=0..3, COND=4, T0..2=5..7
        var min = new Minimizer(bits);
        string[] names = { "OPL0", "OPL1", "OPL2", "OPL3", "COND", "T0", "T1", "T2" };

        bool[][] idxVal = NewGrid(MicroDictionary.IndexBits, 1 << bits);
        var care = new bool[1 << bits];
        var trst = new bool[1 << bits];

        for (int lo = 0; lo < 16; lo++)
            for (int cond = 0; cond < 2; cond++)
                for (int t = 0; t < Sequencer.MaxT; t++)
                {
                    var cell = seq.Simple(bank, lo, t, cond);
                    int addr = lo | (cond << 4) | (t << 5);
                    care[addr] = cell.Defined;
                    trst[addr] = cell.Trst;
                    for (int b = 0; b < MicroDictionary.IndexBits; b++)
                        idxVal[b][addr] = ((cell.Index >> b) & 1) != 0;
                }

        var sb = new StringBuilder();
        sb.Append(Header($"SEQ_{bank}",
            $"Stage 1 sequencer for the {bank} family. Combinational 22V10.\n" +
            "Inputs OPL0..3 (opcode low), COND, T0..2 (external '161 counter).\n" +
            "Outputs IDX0..6 (micro-op index) and TRSTN (/TRST -> counter /LOAD)."));
        EmitSeqPins(sb, cond: true);
        EmitSeqBody(sb, min, names, idxVal, care, trst, zeroRef: "OPL0");
        File.WriteAllText(path, sb.ToString());
    }

    // ---- Stage 1: ALU sequencer (merged quadrant) --------------------------
    // Inputs: O0..5 (opcode[5:0]), T0..2. 9 inputs. No COND (ALU has none).

    static void EmitAluSequencer(string path, Sequencer seq)
    {
        const int bits = 9;   // O0..5=0..5, T0..2=6..8
        var min = new Minimizer(bits);
        string[] names = { "O0", "O1", "O2", "O3", "O4", "O5", "T0", "T1", "T2" };

        bool[][] idxVal = NewGrid(MicroDictionary.IndexBits, 1 << bits);
        var care = new bool[1 << bits];
        var trst = new bool[1 << bits];

        for (int o = 0; o < 64; o++)
            for (int t = 0; t < Sequencer.MaxT; t++)
            {
                var cell = seq.Alu(o, t);
                int addr = o | (t << 6);
                care[addr] = cell.Defined;
                trst[addr] = cell.Trst;
                for (int b = 0; b < MicroDictionary.IndexBits; b++)
                    idxVal[b][addr] = ((cell.Index >> b) & 1) != 0;
            }

        var sb = new StringBuilder();
        sb.Append(Header("SEQ_ALU",
            "Stage 1 sequencer for the ALU quadrant (0x40..0x7F), merged.\n" +
            "The '138 ORs the four nibble enables into one bank enable; this GAL\n" +
            "sees opcode[5:0] (O0..5) and T0..2 (external '161 counter).\n" +
            "Outputs IDX0..6 and TRSTN (/TRST -> counter /LOAD)."));
        sb.AppendLine("PIN 1  = CLK;");
        sb.AppendLine("PIN 2  = O0;");
        sb.AppendLine("PIN 3  = O1;");
        sb.AppendLine("PIN 4  = O2;");
        sb.AppendLine("PIN 5  = O3;");
        sb.AppendLine("PIN 6  = O4;");
        sb.AppendLine("PIN 7  = O5;");
        sb.AppendLine("PIN 8  = T0;");
        sb.AppendLine("PIN 9  = T1;");
        sb.AppendLine("PIN 10 = T2;");
        sb.AppendLine();
        EmitOutputPins(sb);
        EmitSeqBody(sb, min, names, idxVal, care, trst, zeroRef: "O0");
        File.WriteAllText(path, sb.ToString());
    }

    // ---- Stage 1: entry sequencer ------------------------------------------

    static void EmitEntrySequencer(string path, Sequencer seq)
    {
        const int bits = 3;
        var min = new Minimizer(bits);
        string[] names = { "T0", "T1", "T2" };

        bool[][] idxVal = NewGrid(MicroDictionary.IndexBits, 1 << bits);
        var care = new bool[1 << bits];
        var trst = new bool[1 << bits];
        for (int t = 0; t < Sequencer.MaxT && t < (1 << bits); t++)
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
            "Combinational; enabled by INTP, T0..2 the only inputs.\n" +
            "Outputs IDX0..6 and TRSTN (/TRST -> counter /LOAD)."));
        sb.AppendLine("PIN 1  = CLK;");
        sb.AppendLine("PIN 8  = T0;");
        sb.AppendLine("PIN 9  = T1;");
        sb.AppendLine("PIN 10 = T2;");
        sb.AppendLine();
        EmitOutputPins(sb);
        EmitSeqBody(sb, min, names, idxVal, care, trst, zeroRef: "T0");
        File.WriteAllText(path, sb.ToString());
    }

    // ---- shared sequencer helpers ------------------------------------------

    static void EmitSeqPins(StringBuilder sb, bool cond)
    {
        sb.AppendLine("PIN 1  = CLK;");
        sb.AppendLine("PIN 2  = OPL0;");
        sb.AppendLine("PIN 3  = OPL1;");
        sb.AppendLine("PIN 4  = OPL2;");
        sb.AppendLine("PIN 5  = OPL3;");
        if (cond) sb.AppendLine("PIN 6  = COND;");
        sb.AppendLine("PIN 8  = T0;");
        sb.AppendLine("PIN 9  = T1;");
        sb.AppendLine("PIN 10 = T2;");
        sb.AppendLine();
        EmitOutputPins(sb);
    }

    static void EmitOutputPins(StringBuilder sb)
    {
        sb.AppendLine("PIN 14 = IDX0;");
        sb.AppendLine("PIN 15 = IDX1;");
        sb.AppendLine("PIN 16 = IDX2;");
        sb.AppendLine("PIN 17 = IDX3;");
        sb.AppendLine("PIN 18 = IDX4;");
        sb.AppendLine("PIN 19 = IDX5;");
        sb.AppendLine("PIN 20 = IDX6;");
        sb.AppendLine("PIN 23 = TRSTN;   /* /TRST -> external '161 /LOAD */");
        sb.AppendLine();
    }

    static void EmitSeqBody(StringBuilder sb, Minimizer min, string[] names,
                            bool[][] idxVal, bool[] care, bool[] trst, string zeroRef)
    {
        for (int b = 0; b < MicroDictionary.IndexBits; b++)
        {
            var (cubes, inv) = min.Best(idxVal[b], care);
            sb.AppendLine($"/* IDX{b}: {cubes.Count} term(s){(inv ? ", active-low" : "")} */");
            sb.AppendLine(Equation($"IDX{b}", cubes, inv, min, names, zeroRef));
            sb.AppendLine();
        }

        // /TRST active-high in the table; TRSTN is active-low to the counter.
        var (tc, tinv) = min.Best(trst, care);
        sb.AppendLine($"/* TRSTN - asserted low at end of instruction: {tc.Count} term(s) */");
        if (tc.Count == 0)
            sb.AppendLine(Constant("TRSTN", tinv, zeroRef));
        else
        {
            // Best minimized TRST (active-high). !TRSTN carries that cover;
            // if Best chose the complement, TRSTN carries it directly.
            string lhs = tinv ? "TRSTN" : "!TRSTN";
            sb.AppendLine($"{lhs} = {string.Join("\n     # ", tc.Select(c => min.CubeToCupl(c, names)))};");
        }
        sb.AppendLine();
    }

    static bool[][] NewGrid(int rows, int cols)
    {
        var g = new bool[rows][];
        for (int i = 0; i < rows; i++) g[i] = new bool[cols];
        return g;
    }

    static string Header(string name, string blurb) =>
        $"""
        /* BLINKY_M_{name} - Blinky-M rev 14 GAL control          GENERATED
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
