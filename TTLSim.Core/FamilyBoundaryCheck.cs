namespace TTLSim.Core;

/// <summary>
/// Logic-family output/input voltage-level compatibility check for the build
/// pipeline (diagnostics TTL030 and TTL031).
///
/// A bipolar-TTL output (74 / 74L / 74H / 74S / 74LS / 74AS / 74ALS / 74F)
/// guarantees only a TTL-level V_OH -- roughly 2.4-2.7 V minimum at a 5 V rail.
/// A plain-CMOS input (74HC / 74AC) needs V_IH of about 0.7*VCC = 3.5 V to read
/// a solid HIGH. The TTL output's HIGH therefore lands in the CMOS input's
/// forbidden band: the receiver may read it as LOW, sit mid-rail, or draw
/// through-current. The fix is an HCT/ACT part at the boundary (CMOS-rail
/// output, TTL-level input) -- e.g. the 74HCT04 fence a 74LS181's outputs need
/// before they reach the 74HC world.
///
/// This fires ONLY for the genuinely-incompatible direction: a bipolar-TTL
/// OUTPUT sharing a net with a plain-CMOS (HC/AC) INPUT. Everything else stays
/// silent because it works at 5 V:
///   - HCT/ACT as driver   -> near-rail V_OH, drives HC happily.
///   - HCT/ACT as receiver -> TTL-level V_IH, reads LS happily (this IS the fence).
///   - HC/AC   -> HC/AC     -> near-rail V_OH clears the 3.5 V V_IH.
///   - anything -> TTL / HCT / ACT input (~2.0 V V_IH).
///
/// Parts with no 74-family (GALs, memories, discretes) are skipped: their input
/// thresholds are not a 74-series question -- e.g. an ATF16V8B has TTL-level
/// inputs and takes an LS output directly -- so they never raise a false
/// HC-receiver flag.
///
/// LVC is different in kind, not just in level: it is a 3.3 V family the
/// simulator does not model at all (the whole electrical model assumes one
/// 5 V rail). Any LVC part present therefore raises TTL031 as a hard Error --
/// LVC parts (the Teensy level-shifter harness) belong on export-only
/// schematics or on a hidden layer. Because their presence blocks the build
/// outright, the TTL030 net walk never needs LVC-specific level rules.
/// </summary>
public static class FamilyBoundaryCheck
{
    /// <summary>
    /// Severity of the level-mismatch diagnostic. As an <b>Error</b> it blocks
    /// the build the same way the short-circuit (TTL001/004) and power
    /// (TTL002/003) errors do -- the board is genuinely wrong until a fence is
    /// added. Change this single line to <see cref="DiagnosticSeverity.Warning"/>
    /// if you would rather keep the schematic simulatable while the mismatch is
    /// present (the logic simulator ignores analog levels, so the run itself is
    /// still valid).
    /// </summary>
    private const DiagnosticSeverity Severity = DiagnosticSeverity.Error;

    private const string Code = "TTL030";

    /// <summary>Diagnostic code for "an LVC part is present". Always an Error:
    /// the 3.3 V family is outside the simulator's electrical model entirely,
    /// so there is no fence that makes it right.</summary>
    private const string LvcCode = "TTL031";

    /// <summary>
    /// Scan every net for a bipolar-TTL output driving a plain-CMOS input and
    /// emit one <see cref="Diagnostic"/> per offending net. The result is empty
    /// when every boundary is either same-level or correctly fenced with HCT/ACT.
    /// Additionally emits one TTL031 Error per LVC-family device present.
    /// </summary>
    public static IEnumerable<Diagnostic> Check(IBuildInput input, NetTable netTable)
    {
        // TTL031 -- LVC presence. One Error per device, before any net-level
        // analysis: a 3.3 V part in a 5 V simulation is wrong regardless of
        // what it is wired to. The locator points at the device's first unit
        // for the same OutputPanel-resolution reason as TTL020. The escape
        // hatch is the layer system: a device whose units all sit on a hidden
        // layer never reaches the build input (TTL022 reports the exclusion),
        // so parking the LVC harness on its own layer and hiding it lets the
        // 5 V side of the schematic simulate.
        foreach (BuildDevice dev in input.Devices)
        {
            if (Normalize(dev.Family) != "LVC") continue;
            string? locatorId = dev.Units.Count > 0 ? dev.Units[0].UnitId : null;
            yield return new Diagnostic(
                DiagnosticSeverity.Error,
                LvcCode,
                $"{dev.Designator} (74LVC{dev.PartIdentifier}) is an LVC part. LVC "
                + "is a 3.3 V family the simulator does not model -- LVC parts are "
                + "for export/documentation schematics only. Remove it, or move it "
                + "to a layer and hide the layer, to simulate the rest of the "
                + "circuit.",
                ItemId: locatorId);
        }

        // Reverse map: each logic pin -> what family drives/receives it, keyed
        // by PinRef exactly as the net table keys its pins, so a pin found on a
        // net classifies in O(1). Only the two families that can form the bad
        // pairing are recorded (a TTL-level output pin, or a CMOS-level input
        // pin); passive and family-less parts contribute nothing.
        Dictionary<PinRef, PinFacts> facts = new();

        foreach (BuildDevice dev in input.Devices)
        {
            if (dev.IsPassive) continue;

            string fam = Normalize(dev.Family);
            bool ttlOut = DrivesTtlHighLevel(fam);   // is a candidate driver
            bool cmosIn = NeedsCmosInputHigh(fam);   // is a candidate receiver
            if (!ttlOut && !cmosIn) continue;        // HCT/ACT and unknowns: safe both ways

            string display = dev.Family ?? fam;

            foreach (BuildUnit unit in dev.Units)
            {
                if (ttlOut)
                {
                    if (unit.OutputPinNumber is int single)
                        Record(facts, unit.UnitId, single, dev.Designator, display, isOutput: true);
                    if (unit.OutputPinNumbers is { Count: > 0 } many)
                        foreach (int p in many)
                            Record(facts, unit.UnitId, p, dev.Designator, display, isOutput: true);
                }

                if (cmosIn)
                {
                    foreach (int p in unit.InputPinNumbers)
                        Record(facts, unit.UnitId, p, dev.Designator, display, isOutput: false);
                }
            }
        }

        // Walk each net once. A net carrying both a bipolar-TTL output and a
        // plain-CMOS input is the boundary that needs an HCT/ACT fence. One
        // diagnostic per net -- each bus line is its own boundary needing its
        // own buffer pin, so an 8-bit bus legitimately surfaces eight of these.
        foreach (Net net in netTable.Nets)
        {
            List<PinFacts> drivers = new();
            List<PinFacts> receivers = new();

            foreach (PinRef pin in net.Pins)
            {
                if (!facts.TryGetValue(pin, out PinFacts f)) continue;
                if (f.IsOutput) drivers.Add(f);
                else receivers.Add(f);
            }

            if (drivers.Count == 0 || receivers.Count == 0) continue;

            string driverText = string.Join(", ", drivers
                .Select(d => $"{d.Designator} (74{d.Family})").Distinct());
            string receiverText = string.Join(", ", receivers
                .Select(r => $"{r.Designator} (74{r.Family})").Distinct());

            // Locator: every unit on both sides of the boundary, plus the net,
            // so a double-click in the output panel highlights the whole thing.
            List<string> ids = new();
            foreach (PinFacts f in drivers) if (!ids.Contains(f.UnitId)) ids.Add(f.UnitId);
            foreach (PinFacts f in receivers) if (!ids.Contains(f.UnitId)) ids.Add(f.UnitId);

            yield return new Diagnostic(
                Severity,
                Code,
                $"Logic-level mismatch on net {net.Id}: TTL-level output {driverText} "
                + $"drives CMOS-level input {receiverText}. A 74HC/AC input needs about "
                + "3.5 V for a HIGH, above the 2.4-2.7 V a TTL output guarantees -- add an "
                + "HCT/ACT fence (e.g. 74HCT04) at the boundary.",
                ItemIds: ids,
                NetId: net.Id);
        }
    }

    private static void Record(Dictionary<PinRef, PinFacts> facts,
        string unitId, int pin, string designator, string family, bool isOutput)
    {
        // Drivers are recorded before receivers, so if a part ever declared the
        // same pin as both (it shouldn't, for these families) the output role
        // is kept -- the conservative choice for a level check.
        PinRef key = new(unitId, pin);
        if (!facts.ContainsKey(key))
            facts[key] = new PinFacts(unitId, designator, family, isOutput);
    }

    private static string Normalize(string? family) =>
        string.IsNullOrWhiteSpace(family) ? "" : family.Trim().ToUpperInvariant();

    // Bipolar-TTL families: V_OH(min) is only a TTL level (~2.4-2.7 V at 5 V).
    private static bool DrivesTtlHighLevel(string fam) => fam switch
    {
        "STANDARD" or "L" or "H" or "S" or "LS" or "AS" or "ALS" or "F" => true,
        _ => false
    };

    // Plain-CMOS-input families: V_IH(min) ~= 0.7*VCC (3.5 V at 5 V). HCT/ACT
    // are deliberately absent -- their inputs are TTL-compatible (V_IH ~2.0 V).
    private static bool NeedsCmosInputHigh(string fam) => fam switch
    {
        "HC" or "AC" => true,
        _ => false
    };

    /// <summary>One classified pin: which unit/part owns it, its family
    /// (for the message), and whether it is a driver (output) or receiver
    /// (input) for level purposes.</summary>
    private readonly record struct PinFacts(
        string UnitId, string Designator, string Family, bool IsOutput);
}
