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
// /LOAD).
//
// Shared IDX bus: the nine sequencers drive one common IDX/TRSTN bus. Each
// sequencer's outputs are tristated by a BANKEN input (pin 11) driven from the
// bank-select '154, so only the selected bank drives the bus. Because every
// selected bank must define ALL bus lines, a sequencer whose IDXn is constant 0
// over its used states emits "IDXn = GND;" (a real driven-low output), not an
// omitted pin - otherwise that bus line would float while the bank is selected.
//
// Sequencers emit IDX0..5 only. IDX6 is the >=64 bit and the dictionary's max
// index is 57, so IDX6 is 0 for every bank and is dropped entirely (no pin, no
// bus line). The decoders still see a 7-bit index space with IDX6 tied low.
//
// Decoders (UOPA/UOPB): a control line constant across ALL indices still takes
// no macrocell and is listed as a board tie - they are not on a shared bus.
// ---------------------------------------------------------------------------

public static class PldEmitter
{
    public static void EmitAll(string dir, MicroDictionary dict, Sequencer seq, Action? progress = null)
    {
        EmitDecoder(Path.Combine(dir, "BLINKY_M_UOPA.pld"), dict, half: 0); progress?.Invoke();
        EmitDecoder(Path.Combine(dir, "BLINKY_M_UOPB.pld"), dict, half: 1); progress?.Invoke();

        foreach (var bank in seq.Banks)
        {
            if (bank.Alu) EmitAluSequencer(Path.Combine(dir, "BLINKY_M_SEQ_ALU.pld"), seq);
            else EmitSimpleSequencer(Path.Combine(dir, $"BLINKY_M_SEQ_{bank.Name}.pld"), seq, bank.Name);
            progress?.Invoke();
        }
        EmitEntrySequencer(Path.Combine(dir, "BLINKY_M_SEQ_ENTRY.pld"), seq); progress?.Invoke();
    }

    // ---- Stage 2 decoders --------------------------------------------------

    static readonly string[] IdxNames = { "IDX0", "IDX1", "IDX2", "IDX3", "IDX4", "IDX5", "IDX6" };

    static readonly int[] IdxPins = { 14, 15, 16, 17, 18, 19, 20 };
    const int TrstPin = 23;
    const int BankEnPin = 11;   // BANKEN output-enable input, all sequencers

    // Sequencers emit IDX0..5 (IDX6 dropped: max index 57 < 64).
    const int SeqIdxBits = 6;

    // 22V10 macrocell term capacities by pin (graduated).
    static readonly Dictionary<int, int> V10CapByPin = new()
    {
        [14] = 8,
        [15] = 10,
        [16] = 12,
        [17] = 14,
        [18] = 16,
        [19] = 16,
        [20] = 14,
        [21] = 12,
        [22] = 10,
        [23] = 8
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

        // Heaviest lines to widest macrocells. Constant lines take no pin.
        var live = results.Where(r => r.cubes.Count > 0).ToList();
        var gndTies = results.Where(r => r.cubes.Count == 0 && !r.inv).Select(r => r.name).ToList();
        var vccTies = results.Where(r => r.cubes.Count == 0 && r.inv).Select(r => r.name).ToList();

        var order = live.OrderByDescending(r => r.cubes.Count).ToList();
        var pinOrder = V10CapByPin.Keys.OrderByDescending(p => V10CapByPin[p]).ToList();
        var assign = order.Zip(pinOrder, (r, p) => (r, p)).OrderBy(x => x.p).ToList();

        var sb = new StringBuilder();
        string name = half == 0 ? "UOPA" : "UOPB";
        sb.Append(Header(name,
            $"Stage 2 decoder {(half == 0 ? "A" : "B")} of 2 - micro-op index to control lines.\n" +
            "Combinational. Inputs IDX0..6 (Stage-1 sequencer output).\n" +
            $"Dictionary size {dict.Count} of {MicroDictionary.Slots}; unused indices are don't-cares." +
            TieNote(gndTies, vccTies)));
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
            string lhs = r.inv ? "!" + r.name : r.name;
            sb.AppendLine($"{lhs} = {string.Join("\n     # ", r.cubes.Select(c => min.CubeToCupl(c, IdxNames)))};");
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }

    // ---- Stage 1 sequencers ------------------------------------------------

    static void EmitSimpleSequencer(string path, Sequencer seq, string bank)
    {
        const int bits = 8;   // OPL0..3=0..3, COND=4, T0..2=5..7
        string[] names = { "OPL0", "OPL1", "OPL2", "OPL3", "COND", "T0", "T1", "T2" };

        bool[][] idxVal = NewGrid(SeqIdxBits, 1 << bits);
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
                    for (int b = 0; b < SeqIdxBits; b++)
                        idxVal[b][addr] = ((cell.Index >> b) & 1) != 0;
                }

        var inputPins = new List<string>
        {
            "PIN 2  = OPL0;", "PIN 3  = OPL1;", "PIN 4  = OPL2;", "PIN 5  = OPL3;",
            "PIN 6  = COND;", "PIN 8  = T0;", "PIN 9  = T1;", "PIN 10 = T2;"
        };
        WriteSequencer(path, $"SEQ_{bank}",
            $"Stage 1 sequencer for the {bank} family. Combinational 22V10.\n" +
            "Inputs OPL0..3 (opcode low), COND, T0..2 (external '161 counter).\n" +
            "Outputs IDX0..5 (micro-op index) and TRSTN (/TRST -> counter /LOAD).\n" +
            "BANKEN (pin 11) tristates all outputs so banks share one IDX bus.",
            names, bits, idxVal, care, trst, inputPins);
    }

    static void EmitAluSequencer(string path, Sequencer seq)
    {
        const int bits = 9;   // O0..5=0..5, T0..2=6..8
        string[] names = { "O0", "O1", "O2", "O3", "O4", "O5", "T0", "T1", "T2" };

        bool[][] idxVal = NewGrid(SeqIdxBits, 1 << bits);
        var care = new bool[1 << bits];
        var trst = new bool[1 << bits];

        for (int o = 0; o < 64; o++)
            for (int t = 0; t < Sequencer.MaxT; t++)
            {
                var cell = seq.Alu(o, t);
                int addr = o | (t << 6);
                care[addr] = cell.Defined;
                trst[addr] = cell.Trst;
                for (int b = 0; b < SeqIdxBits; b++)
                    idxVal[b][addr] = ((cell.Index >> b) & 1) != 0;
            }

        var inputPins = new List<string>
        {
            "PIN 2  = O0;", "PIN 3  = O1;", "PIN 4  = O2;", "PIN 5  = O3;",
            "PIN 6  = O4;", "PIN 7  = O5;", "PIN 8  = T0;", "PIN 9  = T1;", "PIN 10 = T2;"
        };
        WriteSequencer(path, "SEQ_ALU",
            "Stage 1 sequencer for the ALU quadrant (0x40..0x7F), merged.\n" +
            "The '138 ORs the four nibble enables into one bank enable; this GAL\n" +
            "sees opcode[5:0] (O0..5) and T0..2 (external '161 counter).\n" +
            "Outputs IDX0..5 and TRSTN (/TRST -> counter /LOAD).\n" +
            "BANKEN (pin 11) tristates all outputs so banks share one IDX bus.",
            names, bits, idxVal, care, trst, inputPins);
    }

    static void EmitEntrySequencer(string path, Sequencer seq)
    {
        const int bits = 3;
        string[] names = { "T0", "T1", "T2" };

        bool[][] idxVal = NewGrid(SeqIdxBits, 1 << bits);
        var care = new bool[1 << bits];
        var trst = new bool[1 << bits];
        for (int t = 0; t < Sequencer.MaxT && t < (1 << bits); t++)
        {
            var cell = seq.EntryAt(t);
            care[t] = cell.Defined;
            trst[t] = cell.Trst;
            for (int b = 0; b < SeqIdxBits; b++)
                idxVal[b][t] = ((cell.Index >> b) & 1) != 0;
        }

        var inputPins = new List<string> { "PIN 8  = T0;", "PIN 9  = T1;", "PIN 10 = T2;" };
        WriteSequencer(path, "SEQ_ENTRY",
            "Stage 1 sequencer for interrupt entry (INTP = 1, opcode-independent).\n" +
            "Combinational; enabled by INTP, T0..2 the only inputs.\n" +
            "Outputs IDX0..5 and TRSTN (/TRST -> counter /LOAD).\n" +
            "BANKEN (pin 11) tristates all outputs so banks share one IDX bus.",
            names, bits, idxVal, care, trst, inputPins);
    }

    // Shared sequencer writer. All sequencers drive one common IDX/TRSTN bus,
    // tristated by BANKEN (pin 11). Every selected bank must define ALL bus
    // lines, so a constant-0 IDX bit is emitted as "IDXn = GND;" (a real
    // driven-low output), never omitted. Each output carries ".oe = BANKEN".
    static void WriteSequencer(string path, string name, string blurb, string[] inputNames,
                               int bits, bool[][] idxVal, bool[] care, bool[] trst,
                               List<string> inputPins)
    {
        var min = new Minimizer(bits);

        var idxResult = new (IReadOnlyList<Minimizer.Cube> cubes, bool inv)[SeqIdxBits];
        for (int b = 0; b < SeqIdxBits; b++)
            idxResult[b] = min.Best(idxVal[b], care);
        (IReadOnlyList<Minimizer.Cube> cubes, bool inv) trstResult = min.Best(trst, care);

        // On the shared bus every IDX bit and TRSTN takes a pin - constants are
        // driven (GND/VCC), not tied off - so all outputs are live pins.
        var sb = new StringBuilder();
        sb.Append(Header(name, blurb));
        sb.AppendLine("PIN 1  = CLK;");
        foreach (var p in inputPins) sb.AppendLine(p);
        sb.AppendLine($"PIN {BankEnPin} = BANKEN;   /* output-enable: bank select from the '154 */");
        sb.AppendLine();

        for (int b = 0; b < SeqIdxBits; b++)
            sb.AppendLine($"PIN {IdxPins[b],-2} = IDX{b};");
        sb.AppendLine($"PIN {TrstPin} = TRSTN;   /* /TRST -> external '161 /LOAD */");
        sb.AppendLine();

        for (int b = 0; b < SeqIdxBits; b++)
        {
            var (cubes, inv) = idxResult[b];
            if (cubes.Count == 0)
            {
                // Constant over this bank's used states. Best() returns the
                // complement cover for an all-0 care-set (inv = true, no cubes)
                // -> drive GND; an all-1 care-set would give inv = false -> VCC.
                sb.AppendLine($"/* IDX{b}: constant {(inv ? "0" : "1")} for this bank */");
                sb.AppendLine($"IDX{b} = {(inv ? "GND" : "VCC")};");
                sb.AppendLine();
                continue;
            }
            sb.AppendLine($"/* IDX{b}: {cubes.Count} term(s){(inv ? ", active-low" : "")} */");
            string lhs = inv ? "!IDX" + b : "IDX" + b;
            sb.AppendLine($"{lhs} = {string.Join("\n     # ", cubes.Select(c => min.CubeToCupl(c, inputNames)))};");
            sb.AppendLine();
        }

        {
            var (cubes, inv) = trstResult;
            if (cubes.Count == 0)
            {
                sb.AppendLine($"/* TRSTN: constant {(inv ? "0" : "1")} for this bank */");
                sb.AppendLine($"TRSTN = {(inv ? "GND" : "VCC")};");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"/* TRSTN - asserted low at end of instruction: {cubes.Count} term(s) */");
                // Best minimized active-high TRST; !TRSTN carries that cover.
                string lhs = inv ? "TRSTN" : "!TRSTN";
                sb.AppendLine($"{lhs} = {string.Join("\n     # ", cubes.Select(c => min.CubeToCupl(c, inputNames)))};");
                sb.AppendLine();
            }
        }

        // Output-enable: every output drives only while this bank is selected.
        sb.AppendLine("/* tristate all outputs unless this bank is selected */");
        for (int b = 0; b < SeqIdxBits; b++)
            sb.AppendLine($"IDX{b}.oe = BANKEN;");
        sb.AppendLine("TRSTN.oe = BANKEN;");
        sb.AppendLine();

        File.WriteAllText(path, sb.ToString());
    }

    // ---- helpers -----------------------------------------------------------

    static string TieNote(List<string> gnd, List<string> vcc)
    {
        if (gnd.Count == 0 && vcc.Count == 0) return "";
        var parts = new List<string>();
        if (gnd.Count > 0) parts.Add($"tie to GND: {string.Join(", ", gnd)}");
        if (vcc.Count > 0) parts.Add($"tie to VCC: {string.Join(", ", vcc)}");
        return "\nConstant outputs (no macrocell used) - " + string.Join("; ", parts) + ".";
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