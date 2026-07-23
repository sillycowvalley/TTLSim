using TTLSim.Core;

namespace TTLSim.Chips;

/// <summary>
/// Per-(part, family) timing for the 74-series parts the simulator models.
///
/// All figures are datasheet MAXIMUMS in picoseconds — the conservative bound
/// that answers "what is the fastest clock that works for ANY part I might
/// fit," across the rated voltage and temperature range. For combinational
/// parts <see cref="ChipTiming.PropagationPs"/> is tPD; for clocked parts it is
/// tCO (clock → Q). Setup and hold are carried as separate slots for the later
/// edge-checking layer; they are 0 ("not yet characterised") until that lands.
///
/// These are representative starting values. Refine them against the specific
/// vendor's datasheet (TI, Diodes, Nexperia differ by a few ns at the max);
/// this is the one and only place to edit them.
/// </summary>
public static class TtlTiming
{
    /// <summary>
    /// Resolve the propagation delay (ps) the factory should hand a model.
    /// Precedence: an explicit per-device override (the memory/GAL Propagation
    /// Delay field) wins; otherwise the (part, family) table; otherwise a
    /// per-family gate default; otherwise a global fallback.
    /// </summary>
    public static long ResolvePs(BuildDevice device)
    {
        if (device.PropagationDelayNs is int ns && ns > 0)
            return ns * 1000L;
        return Lookup(device.PartIdentifier, device.Family).PropagationPs;
    }

    /// <summary>
    /// Full timing record for a part in a family. Falls back to a per-family
    /// gate default, then a global default, when the part is not tabulated.
    /// </summary>
    public static ChipTiming Lookup(string part, string? family)
    {
        string fam = Normalize(family);
        if (Table.TryGetValue($"{fam}:{part}", out ChipTiming t)) return t;
        if (FamilyGateDefaultPs.TryGetValue(fam, out long g)) return new ChipTiming(g);
        return new ChipTiming(GlobalDefaultPs);
    }

    private static string Normalize(string? family) =>
        string.IsNullOrWhiteSpace(family) ? "HC" : family.Trim().ToUpperInvariant();

    private const long GlobalDefaultPs = 20_000;

    // Per-family delay for an ordinary small-gate package when the specific
    // part isn't tabulated below. Most gates in a family land near one value;
    // the slow exceptions (Schmitt '14, XOR '86) get their own rows.
    private static readonly Dictionary<string, long> FamilyGateDefaultPs = new()
    {
        ["L"] = 60_000,
        ["LS"] = 20_000,
        ["S"] = 7_000,
        ["STANDARD"] = 22_000,
        ["H"] = 13_000,
        ["ALS"] = 11_000,
        ["AS"] = 7_000,
        ["F"] = 7_000,
        ["HC"] = 24_000,
        ["HCT"] = 24_000,
        ["AC"] = 12_000,
        ["ACT"] = 12_000,
    };

    // Keyed "FAMILY:PART". Only LS and HC are seeded for now (the two families
    // in play); add families/parts as rows, no code change. Combinational →
    // PropagationPs = tPD(max). Clocked → PropagationPs = tCO(max), with
    // SetupPs/HoldPs left 0 until the edge-checking layer characterises them.
    private static readonly Dictionary<string, ChipTiming> Table = new()
    {
        // ---- gates that differ from the family gate default --------------
        ["LS:04"] = new(15_000),
        ["HC:04"] = new(24_000),   // hex inverter
        ["LS:14"] = new(22_000),
        ["HC:14"] = new(31_000),   // Schmitt inverter (slower)
        ["LS:86"] = new(30_000),
        ["HC:86"] = new(35_000),   // XOR (slower)
        // 00/02/08/10/20/30/32 fall through to the family gate default.

        // ---- combinational MSI (tPD max) --------------------------------
        ["LS:283"] = new(25_000),
        ["HC:283"] = new(40_000),  // 4-bit adder (carry path)
        ["LS:138"] = new(41_000),
        ["HC:138"] = new(38_000),  // 3:8 decoder
        ["LS:139"] = new(38_000),
        ["HC:139"] = new(33_000),  // dual 2:4 decoder
        ["LS:151"] = new(33_000),
        ["HC:151"] = new(43_000),  // 8:1 mux (select -> Y path)
        ["LS:153"] = new(22_000),
        ["HC:153"] = new(34_000),  // dual 4:1 mux
        ["LS:154"] = new(36_000),
        ["HC:154"] = new(45_000),  // 4:16 decoder
        ["LS:157"] = new(18_000),
        ["HC:157"] = new(30_000),  // quad 2:1 mux
        ["LS:181"] = new(30_000),
        ["HC:181"] = new(45_000),  // ALU (single '181, worst path)
        ["LS:182"] = new(22_000),
        ["HC:182"] = new(40_000),  // carry lookahead generator
        ["LS:244"] = new(18_000),
        ["HC:244"] = new(30_000),  // octal buffer
        ["LS:245"] = new(12_000),
        ["HC:245"] = new(30_000),  // octal transceiver
        ["LS:541"] = new(18_000),
        ["HC:541"] = new(30_000),  // octal buffer
        ["LS:125"] = new(25_000),
        ["HC:125"] = new(36_000),  // quad 3-state buffer (worst path: LS enable tPZL 25 ns; HC tpd/ten/tdis all 36 ns full-range)
        ["LS:126"] = new(25_000),
        ["HC:126"] = new(36_000),  // as the '125, active-high enables
        ["LS:47"] = new(100_000),
        ["HC:47"] = new(100_000), // BCD→7-seg decoder (slow)
        ["LS:688"] = new(25_000),
        ["HC:688"] = new(45_000),  // 8-bit identity comparator
        ["LS:670"] = new(35_000),
        ["HC:670"] = new(45_000),  // 4x4 register file (read address → Q)


        // ---- clocked (tCO = clock → Q, max) -----------------------------
        ["LS:74"] = new(40_000),
        ["HC:74"] = new(44_000),  // dual D flip-flop
        ["LS:161"] = new(27_000),
        ["HC:161"] = new(44_000),  // sync counter
        ["LS:163"] = new(27_000),
        ["HC:163"] = new(44_000),  // sync counter
        ["LS:173"] = new(27_000),
        ["HC:173"] = new(44_000),  // quad D register (3-state)
        ["LS:273"] = new(30_000),
        ["HC:273"] = new(44_000),  // octal D flip-flop, async clear
        ["LS:374"] = new(28_000),
        ["HC:374"] = new(44_000),  // octal D flip-flop, 3-state
        ["HCT:374"] = new(44_000),                            // the physical part (HC374 unobtainable)
        ["LS:574"] = new(28_000),
        ["HC:574"] = new(44_000),  // '374 silicon core, flow-through pinout; rows anchored to the '374 (legacy TI-class HC; newer processes spec 35)
        ["HCT:574"] = new(44_000),
        ["HC:573"] = new(45_000),
        ["HCT:573"] = new(53_000), // transparent latch (Nexperia -40..125 max; D->Q and LE->Q both hit the row value, OE paths faster; no LS row until an LS sheet is verified)
        ["HC:373"] = new(45_000),
        ["HCT:373"] = new(53_000),  // '573 silicon core, interleaved pinout; rows anchored to the '573 (same die, same D->Q / LE->Q paths). No LS row for the same reason.
        ["LS:377"] = new(30_000),
        ["HC:377"] = new(44_000),  // octal D flip-flop, clock enable
        ["LS:191"] = new(27_000),
        ["HC:191"] = new(44_000),  // sync up/down counter
        ["LS:194"] = new(35_000),
        ["HC:194"] = new(44_000),  // universal shift register (LS: Fairchild CLK->Q tPHL max; HC: Nexperia CP->Q max, full temp range)
        ["LS:299"] = new(40_000),
        ["HC:299"] = new(60_000),  // 8-bit universal shift reg, 3-state I/O (LS: Motorola clear->Q max; HC: Nexperia CP->Q max -40..125C, covers the faster OE paths)
        ["LS:390"] = new(35_000),
        ["HC:390"] = new(44_000),  // dual decade counter
        ["LS:393"] = new(35_000),
        ["HC:393"] = new(44_000),  // dual binary counter
    };
}

/// <summary>
/// One part's timing, datasheet max, in picoseconds. <see cref="PropagationPs"/>
/// is tPD (combinational) or tCO (clocked). <see cref="SetupPs"/>/<see cref="HoldPs"/>
/// apply to clocked parts only and are 0 until characterised.
/// </summary>
public readonly record struct ChipTiming(
    long PropagationPs,
    long SetupPs = 0,
    long HoldPs = 0);