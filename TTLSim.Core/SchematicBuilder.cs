namespace TTLSim.Core;

public sealed class SchematicBuilder
{
    private readonly IChipFactory chipFactory;
    private readonly Microsoft.Extensions.Logging.ILogger? logger;
    private readonly DiagnosticSeverity contentionSeverity;

    /// <param name="contentionIsWarning">
    /// Downgrade the TTL005 output-contention check from Error to Warning, so
    /// a schematic with two outputs on a net still builds and runs. Off by
    /// default: two totem-pole outputs on one wire is a short circuit, and
    /// running the simulation anyway just produces a permanently Unknown net
    /// that hides the real fault.
    /// </param>
    public SchematicBuilder(IChipFactory? chipFactory = null,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        bool contentionIsWarning = false)
    {
        this.chipFactory = chipFactory ?? NullChipFactory.Instance;
        this.logger = logger;
        this.contentionSeverity = contentionIsWarning
            ? DiagnosticSeverity.Warning
            : DiagnosticSeverity.Error;
    }

    public BuildResult Build(IBuildInput input)
    {
        List<Diagnostic> diagnostics = new();

        // Phase 1: net extraction.
        NetTable netTable = NetTable.Build(input.Connections);

        // Phase 1b: per-unit floating-pin diagnostics.
        foreach (BuildDevice dev in input.Devices)
        {
            // Passives (resistors, networks, caps, LEDs, switches, ...) have no
            // CMOS inputs to tie off -- an unconnected terminal is a deliberately
            // unused element, not a floating logic input. The "tie unused CMOS
            // inputs to VCC or GND" advice is meaningless for them, so skip the
            // unused/floating/dangling diagnostics (TTL010/011/012) entirely.
            // Their pins still reach the simulator's net map via InputPinNumbers;
            // only the diagnostics are suppressed here.
            if (dev.IsPassive)
                continue;

            foreach (BuildUnit unit in dev.Units)
            {
                string label = unit.Letter == '\0'
                    ? dev.Designator
                    : $"{dev.Designator}{unit.Letter}";

                int connectedInputs = 0;
                List<int> floatingInputs = new();
                foreach (int pin in unit.InputPinNumbers)
                {
                    if (netTable.FindNet(new PinRef(unit.UnitId, pin)) is not null)
                        connectedInputs++;
                    else
                        floatingInputs.Add(pin);
                }

                // Check single output pin (gate-style units).
                bool outputConnected = false;
                bool hasOutput = unit.OutputPinNumber is not null;
                if (unit.OutputPinNumber is int outPin)
                {
                    outputConnected = netTable.FindNet(new PinRef(unit.UnitId, outPin)) is not null;
                }

                // Check multiple output pins (chip-style units).
                int connectedOutputs = 0;
                int totalOutputs = 0;
                if (unit.OutputPinNumbers is { Count: > 0 } outPins)
                {
                    totalOutputs = outPins.Count;
                    hasOutput = true;
                    foreach (int opin in outPins)
                    {
                        if (netTable.FindNet(new PinRef(unit.UnitId, opin)) is not null)
                            connectedOutputs++;
                    }
                    outputConnected = connectedOutputs > 0;
                }

                bool anyInputs = unit.InputPinNumbers.Count > 0;
                bool allInputsFloating = anyInputs && connectedInputs == 0;
                bool outputFloating = hasOutput && !outputConnected;

                // Case 1: entirely unused unit.
                if (allInputsFloating && outputFloating)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Warning,
                        Code: "TTL010",
                        Message: $"{label} is unused. Tie unused CMOS inputs to VCC or GND.",
                        ItemId: unit.UnitId));
                    continue;
                }

                // Case 2: at least one input is floating but the unit is partly wired.
                if (anyInputs && floatingInputs.Count > 0)
                {
                    string pins = floatingInputs.Count == 1
                        ? $"pin {floatingInputs[0]}"
                        : "pins " + string.Join(", ", floatingInputs);
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Warning,
                        Code: "TTL011",
                        Message: $"{label} has floating input ({pins}). Tie unused CMOS inputs to VCC or GND.",
                        ItemId: unit.UnitId,
                        PinNumber: floatingInputs[0]));
                }

                // Case 3a: chip-style unit (multiple outputs) with inputs
                // wired and at least one output dangling -- always warn.
                // Gate-style units are handled below, at device scope.
                if (hasOutput && outputFloating && connectedInputs > 0
                    && unit.OutputPinNumbers is { Count: > 0 })
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Warning,
                        Code: "TTL012",
                        Message: $"{label} output is not connected.",
                        ItemId: unit.UnitId));
                }
            }

            // Case 3b: gate-style TTL012 -- device scope.
            // If no gate on this device has its output connected, every gate
            // with wired inputs gets a TTL012. Using only some gates on a
            // quad IC is normal; using none of them is wasted silicon.
            bool anyGateOutputDriven = false;
            foreach (BuildUnit u in dev.Units)
            {
                if (u.OutputPinNumber is int op
                    && netTable.FindNet(new PinRef(u.UnitId, op)) is not null)
                {
                    anyGateOutputDriven = true;
                    break;
                }
            }
            if (!anyGateOutputDriven)
            {
                foreach (BuildUnit u in dev.Units)
                {
                    if (u.OutputPinNumber is not int op) continue;
                    if (netTable.FindNet(new PinRef(u.UnitId, op)) is not null) continue;

                    int wiredInputs = 0;
                    foreach (int ip in u.InputPinNumbers)
                        if (netTable.FindNet(new PinRef(u.UnitId, ip)) is not null)
                            wiredInputs++;
                    if (wiredInputs == 0) continue;   // already covered by TTL010

                    string ulabel = u.Letter == '\0'
                        ? dev.Designator
                        : $"{dev.Designator}{u.Letter}";
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Warning,
                        Code: "TTL012",
                        Message: $"{ulabel} output is not connected.",
                        ItemId: u.UnitId));
                }
            }
        }

        // Phase 1c: VCC/GND short detection.
        HashSet<int> vccNets = NetsFromItems(input, netTable, BuildItemKind.Vcc);
        HashSet<int> gndNets = NetsFromItems(input, netTable, BuildItemKind.Gnd);

        foreach (int sharedNet in vccNets.Intersect(gndNets))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                Code: "TTL001",
                Message: $"VCC and GND are connected on net {sharedNet} -- short circuit.",
                NetId: sharedNet));
        }

        // Phase 1c (cont.): output-pin-to-rail short. An IC output sitting on a
        // net that is also tied straight to VCC or GND is fighting the rail --
        // a hard short the instant that output drives the opposite level. This
        // is distinct from TTL001 (rail-to-rail): here exactly one rail shares
        // the net with a driving output. Reuses the vccNets/gndNets computed
        // above.
        //
        // Inactive (hidden-layer) rails and outputs are already absent from the
        // build, so a correctly-hidden stub stays silent; the short only fires
        // when the stub and the module driving the pin are both visible. A
        // pull-up/down doesn't match -- its rail symbol sits on the resistor's
        // far pin, a different net.
        foreach (BuildDevice dev in input.Devices)
        {
            foreach (BuildUnit unit in dev.Units)
            {
                string label = unit.Letter == '\0'
                    ? dev.Designator
                    : $"{dev.Designator}{unit.Letter}";

                foreach (int outPin in OutputPinsOf(unit))
                {
                    Net? net = netTable.FindNet(new PinRef(unit.UnitId, outPin));
                    if (net is null) continue;

                    string? rail =
                        vccNets.Contains(net.Id) ? "VCC" :
                        gndNets.Contains(net.Id) ? "GND" : null;
                    if (rail is null) continue;

                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        Code: "TTL004",
                        Message: $"{label} output (pin {outPin}) is shorted to {rail} -- net {net.Id}.",
                        NetId: net.Id));
                }
            }
        }

        // Phase 1d: unsupported-part detection. A part placed on the canvas
        // but not handled by the chip factory would otherwise vanish from
        // the built simulator (its outputs would never drive, its inputs
        // never observed) and the user would chase the resulting silence.
        // Surface it as a hard error so they know the schematic can't be
        // simulated end-to-end yet.
        //
        // The diagnostic's ItemId points at the device's first unit (not the
        // device id itself) because the locator in the OutputPanel resolves
        // ids against the schematic's item list, not its device list. For a
        // box-chip there's only one unit; for a multi-gate IC the first slot
        // is a close-enough landing spot.
        foreach (BuildDevice dev in input.Devices)
        {
            if (chipFactory.IsSimulated(dev)) continue;
            string? locatorId = dev.Units.Count > 0 ? dev.Units[0].UnitId : null;
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                Code: "TTL020",
                Message: $"{dev.Designator} ({dev.PartIdentifier}) has no simulation model yet.",
                ItemId: locatorId));
        }

        // Phase 1e: power-pin connectivity. Every IC must have its VCC and
        // GND pins wired. This parallels the floating-input check (Phase 1b)
        // but is an Error, not a Warning: an unpowered chip can neither be
        // simulated nor built into a working board. Power pins live at
        // device scope (PowerPinNumber/GroundPinNumber, null for passives /
        // displays, which are skipped). They're excluded from the per-unit
        // input/output lists but the owning unit still carries them as real
        // pins, so the net-table key is PinRef(unitId, powerPin); for a
        // multi-unit device the shared power net may sit on any unit, so the
        // pin counts as connected if ANY unit's pin is on a net. Locator
        // points at the device's first unit (same reason as TTL020).
        foreach (BuildDevice dev in input.Devices)
        {
            string? powerLocatorId = dev.Units.Count > 0 ? dev.Units[0].UnitId : null;

            if (dev.PowerPinNumber is int vccPin
                && !dev.Units.Any(u => netTable.FindNet(new PinRef(u.UnitId, vccPin)) is not null))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    Code: "TTL002",
                    Message: $"{dev.Designator} VCC (pin {vccPin}) is not connected.",
                    ItemId: powerLocatorId,
                    PinNumber: vccPin));
            }

            if (dev.GroundPinNumber is int gndPin
                && !dev.Units.Any(u => netTable.FindNet(new PinRef(u.UnitId, gndPin)) is not null))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    Code: "TTL003",
                    Message: $"{dev.Designator} GND (pin {gndPin}) is not connected.",
                    ItemId: powerLocatorId,
                    PinNumber: gndPin));
            }
        }

        // Phase 1f: logic-family output/input level compatibility. A bipolar-TTL
        // output (e.g. 74LS181) driving a plain-CMOS input (74HC/74AC) sits in
        // that input's forbidden band and needs an HCT/ACT fence. Lives in its
        // own checker (TTL030); see FamilyBoundaryCheck for the level rationale.
        // Runs before the error gate so, at Error severity, a missing fence
        // blocks the build like the short-circuit and power errors above.
        diagnostics.AddRange(FamilyBoundaryCheck.Check(input, netTable));

        // Phase 1g: electrical sanity scan over the finished net table --
        // output-vs-output contention (TTL005) and input-only nets with nothing
        // driving them (TTL013). Both are things TTL004 and TTL011 look like
        // they'd catch but don't: TTL004 only sees an output fighting a RAIL,
        // and TTL011 only sees a pin on NO net at all. An input wired to
        // another input is "connected", and two outputs wired together are both
        // "connected" -- yet the first stays Unknown forever and the second is
        // a dead short. Runs before the error gate so a short blocks the build.
        diagnostics.AddRange(ScanNetsForElectricalFaults(input, netTable));

        // If we've hit errors, don't try to build chips.
        bool anyErrors = diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        if (anyErrors)
            return new BuildResult(diagnostics, netTable, simulator: null);

        // Phase 2: bind chip models.
        List<IChip> chips = new();

        // Identify power-rail nets so passives can classify themselves.
        Dictionary<int, Signal> powerNets = new();
        foreach (BuildItem item in input.Items)
        {
            Signal? railValue = item.Kind switch
            {
                BuildItemKind.Vcc => Signal.High,
                BuildItemKind.Gnd => Signal.Low,
                _ => (Signal?)null
            };
            if (railValue is null) continue;

            foreach (int pin in item.PinNumbers)
            {
                Net? net = netTable.FindNet(new PinRef(item.ItemId, pin));
                if (net is not null) powerNets[net.Id] = railValue.Value;
            }
        }

        foreach (BuildItem item in input.Items)
        {
            Dictionary<int, Net> pinMap = MapItemPins(item, netTable);
            IChip? chip = chipFactory.CreateForItem(item, pinMap);
            if (chip is not null)
                chips.Add(chip);
        }

        foreach (BuildDevice dev in input.Devices)
        {
            Dictionary<string, IReadOnlyDictionary<int, Net>> unitPinMaps = new();
            foreach (BuildUnit unit in dev.Units)
                unitPinMaps[unit.UnitId] = MapUnitPins(unit, netTable);

            int chipsBeforeDevice = chips.Count;
            foreach (IChip chip in chipFactory.CreateForUnits(dev, unitPinMaps, powerNets))
                chips.Add(chip);

            // A powered IC that cleared the TTL020 (no model) and TTL002/003
            // (power) checks but produced no chip means a TryCreate* returned
            // null -- almost always a required pin left unconnected. Without
            // this the part silently vanishes from the simulation rather than
            // simulating wrongly, which is hard to spot (it once cost a dead
            // PC high nibble). Visual-only parts (LEDs, caps, crystals,
            // headers) have no power pin and legitimately yield nothing, so
            // the power-pin test excludes them with no extra bookkeeping.
            if (chips.Count == chipsBeforeDevice && dev.PowerPinNumber is not null)
            {
                string? nullLocatorId = dev.Units.Count > 0 ? dev.Units[0].UnitId : null;
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    Code: "TTL021",
                    Message: $"{dev.Designator} ({dev.PartIdentifier}) has a simulation "
                           + "model but none was built -- a required pin is likely "
                           + "unconnected. The part is absent from the simulation.",
                    ItemId: nullLocatorId));
            }
        }

        Simulator sim = new(netTable, chips, logger);
        return new BuildResult(diagnostics, netTable, sim);
    }

    private static Dictionary<int, Net> MapUnitPins(BuildUnit unit, NetTable table)
    {
        Dictionary<int, Net> map = new();
        foreach (int pin in unit.InputPinNumbers)
        {
            Net? net = table.FindNet(new PinRef(unit.UnitId, pin));
            if (net is not null) map[pin] = net;
        }
        if (unit.OutputPinNumber is int outPin)
        {
            Net? net = table.FindNet(new PinRef(unit.UnitId, outPin));
            if (net is not null) map[outPin] = net;
        }
        if (unit.OutputPinNumbers is { } outputPins)
        {
            foreach (int pin in outputPins)
            {
                Net? net = table.FindNet(new PinRef(unit.UnitId, pin));
                if (net is not null) map[pin] = net;
            }
        }
        return map;
    }

    /// <summary>One output pin, tagged with enough context to name it in a message.</summary>
    private readonly record struct OutputPinRef(
        string Label, string UnitId, int Pin, string PartIdentifier);

    /// <summary>
    /// Phase 1g. Walks every net once and emits two diagnostics.
    ///
    /// <para>
    /// <b>TTL005 -- output contention.</b> Two or more output pins on one net,
    /// where EVERY contributing part is a known push-pull device (see
    /// <see cref="TotemPoleParts"/>). That's a hard short: both stages drive
    /// all the time, so the moment they disagree there's a path from VCC to GND
    /// through two output transistors. The classic cause is a swapped
    /// input/output pin -- D and Q on a register sit on adjacent pins, so
    /// wiring a latch into Q instead of D lands an output on an output and
    /// leaves the D input dangling (which TTL013 then catches as the other half
    /// of the same mistake).
    /// </para>
    /// <para>
    /// If ANY part on the net is not in the totem-pole table -- a '245, a GAL,
    /// an EEPROM, or simply a part nobody has classified -- this stays silent
    /// and hands the job to the runtime detector (Net.DetectFault), which sees
    /// actual contention rather than guessing from part numbers. A shared bus
    /// must not error at build time.
    /// </para>
    ///
    /// <para>
    /// <b>TTL013 -- undriven input net.</b> A net whose every pin is a declared
    /// logic input. Nothing can ever drive it, so it sits at Unknown for the
    /// whole run and quietly poisons everything downstream. Passives, headers,
    /// rails, clocks and net labels all contribute pins that are NOT declared
    /// logic inputs, so their presence on a net is enough to exempt it -- a
    /// pull-up's resistor terminal, a header's off-board pin and a VCC symbol
    /// each count as "something might drive this". That keeps the check
    /// conservative: it fires only when the net is unambiguously input-only.
    /// </para>
    /// </summary>
    private List<Diagnostic> ScanNetsForElectricalFaults(IBuildInput input, NetTable netTable)
    {
        List<Diagnostic> result = new();

        Dictionary<int, List<OutputPinRef>> outputsOnNet = new();
        HashSet<PinRef> logicInputPins = new();
        Dictionary<PinRef, string> pinLabels = new();

        foreach (BuildDevice dev in input.Devices)
        {
            foreach (BuildUnit unit in dev.Units)
            {
                string label = unit.Letter == '\0'
                    ? dev.Designator
                    : $"{dev.Designator}{unit.Letter}";

                foreach (int pin in OutputPinsOf(unit))
                {
                    PinRef pinRef = new(unit.UnitId, pin);
                    pinLabels[pinRef] = label;

                    Net? net = netTable.FindNet(pinRef);
                    if (net is null) continue;

                    if (!outputsOnNet.TryGetValue(net.Id, out List<OutputPinRef>? list))
                        outputsOnNet[net.Id] = list = new List<OutputPinRef>();
                    list.Add(new OutputPinRef(label, unit.UnitId, pin, dev.PartIdentifier));
                }

                // Passive terminals are listed as "inputs" by the build adapter
                // for want of anywhere better, but a resistor or a switch very
                // much can drive a net (PullDriver / contact pass-through), so
                // they must not count towards "this net is inputs-only".
                if (dev.IsPassive) continue;

                foreach (int pin in unit.InputPinNumbers)
                {
                    PinRef pinRef = new(unit.UnitId, pin);
                    logicInputPins.Add(pinRef);
                    pinLabels[pinRef] = label;
                }
            }
        }

        // TTL005 -- two or more always-driving outputs on one net.
        foreach ((int netId, List<OutputPinRef> pins) in outputsOnNet)
        {
            if (pins.Count < 2) continue;
            if (!pins.All(p => TotemPoleParts.IsTotemPole(p.PartIdentifier))) continue;

            string names = string.Join(", ", pins
                .OrderBy(p => p.Label, StringComparer.Ordinal)
                .ThenBy(p => p.Pin)
                .Select(p => $"{p.Label} pin {p.Pin} ('{p.PartIdentifier})"));

            result.Add(new Diagnostic(
                contentionSeverity,
                Code: "TTL005",
                Message: $"Output contention on net {netId}: {names} all drive it. "
                       + "Two totem-pole outputs on one net is a short circuit -- "
                       + "check for a swapped input/output pin.",
                NetId: netId,
                ItemIds: pins.Select(p => p.UnitId).Distinct().ToList()));
        }

        // TTL013 -- net made entirely of logic inputs; nothing can drive it.
        foreach (Net net in netTable.Nets)
        {
            if (net.Pins.Count == 0) continue;
            if (outputsOnNet.ContainsKey(net.Id)) continue;

            bool inputsOnly = true;
            foreach (PinRef pin in net.Pins)
            {
                if (!logicInputPins.Contains(pin)) { inputsOnly = false; break; }
            }
            if (!inputsOnly) continue;

            string names = string.Join(", ", net.Pins
                .Select(p => $"{pinLabels.GetValueOrDefault(p, p.ItemId)} pin {p.PinNumber}")
                .OrderBy(s => s, StringComparer.Ordinal));

            result.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                Code: "TTL013",
                Message: $"Net {net.Id} has nothing driving it: {names} are all inputs. "
                       + "It will stay Unknown for the whole run.",
                NetId: net.Id,
                ItemIds: net.Pins.Select(p => p.ItemId).Distinct().ToList()));
        }

        return result;
    }

    /// <summary>All output pin numbers on a unit -- the single gate-style
    /// output and any box-chip outputs together. Empty for input-only units
    /// (passives, displays).</summary>
    private static IEnumerable<int> OutputPinsOf(BuildUnit unit)
    {
        if (unit.OutputPinNumber is int single)
            yield return single;
        if (unit.OutputPinNumbers is { Count: > 0 } many)
            foreach (int p in many)
                yield return p;
    }

    private static HashSet<int> NetsFromItems(IBuildInput input, NetTable table, BuildItemKind kind)
    {
        HashSet<int> result = new();
        foreach (BuildItem item in input.Items)
        {
            if (item.Kind != kind) continue;
            foreach (int pinNum in item.PinNumbers)
            {
                Net? net = table.FindNet(new PinRef(item.ItemId, pinNum));
                if (net is not null)
                    result.Add(net.Id);
            }
        }
        return result;
    }

    private static Dictionary<int, Net> MapItemPins(BuildItem item, NetTable table)
    {
        Dictionary<int, Net> map = new();
        foreach (int pin in item.PinNumbers)
        {
            Net? net = table.FindNet(new PinRef(item.ItemId, pin));
            if (net is not null) map[pin] = net;
        }
        return map;
    }

    private static Dictionary<int, Net> MapDevicePins(BuildDevice dev, NetTable table)
    {
        Dictionary<int, Net> map = new();
        foreach (BuildUnit unit in dev.Units)
        {
            foreach (int pin in unit.InputPinNumbers)
            {
                Net? net = table.FindNet(new PinRef(unit.UnitId, pin));
                if (net is not null) map[pin] = net;
            }
            if (unit.OutputPinNumber is int outPin)
            {
                Net? net = table.FindNet(new PinRef(unit.UnitId, outPin));
                if (net is not null) map[outPin] = net;
            }
            if (unit.OutputPinNumbers is { } outputPins)
            {
                foreach (int pin in outputPins)
                {
                    Net? net = table.FindNet(new PinRef(unit.UnitId, pin));
                    if (net is not null) map[pin] = net;
                }
            }
        }
        return map;
    }

    private sealed class NullChipFactory : IChipFactory
    {
        public static readonly NullChipFactory Instance = new();
        public IChip? CreateForDevice(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet) => null;
        public IChip? CreateForItem(BuildItem item, IReadOnlyDictionary<int, Net> pinToNet) => null;
        public IEnumerable<IChip> CreateForUnits(
            BuildDevice device,
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, Net>> unitPinMaps,
            IReadOnlyDictionary<int, Signal> powerNets) => System.Array.Empty<IChip>();

        // The null factory has no opinion about what is and isn't simulated --
        // it claims everything is fine so existing builder consumers (and the
        // builder's own tests) keep working without supplying a real factory.
        public bool IsSimulated(BuildDevice device) => true;
    }
}