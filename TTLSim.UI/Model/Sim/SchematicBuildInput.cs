using TTLSim.Core;
using TTLSim.UI.Components;

namespace TTLSim.UI.Model.Sim;

/// <summary>
/// Adapter that exposes a Schematic to the simulator's SchematicBuilder.
/// Translates UI model types (Schematic / Device / Unit / Connection /
/// VccSymbol / GndSymbol / ClockSource) into the abstract Build* records
/// the Core builder expects.
///
/// <para>
/// Layer activity is applied here: items on an invisible layer are fully
/// inactive, so this adapter skips inactive units (and any device left with no
/// active units), inactive standalone items, and any connection or header-link
/// pin-pair with an inactive endpoint. This is the single choke point for the
/// simulator -- the build sees exactly the active subset of the schematic.
/// </para>
/// </summary>
public sealed class SchematicBuildInput : IBuildInput
{
    private readonly Schematic schematic;

    public SchematicBuildInput(Schematic schematic)
    {
        this.schematic = schematic;
    }

    /// <summary>
    /// One TTL022 Info per hidden, non-empty layer, naming the layer and how
    /// many items it excludes from this build. Hidden means fully inactive --
    /// the rest of this adapter silently filters those items, their wires and
    /// their header links out, and the builder never knows they exist; this
    /// note is the only place the exclusion becomes visible. A forgotten
    /// hidden layer (the classic: the whole clock module) then announces
    /// itself on the first lines of the build output instead of failing as
    /// unexplained silence. Layer 0 (Default) is pinned visible, so the scan
    /// starts at 1. Empty hidden layers exclude nothing and stay quiet.
    /// </summary>
    public IReadOnlyList<Diagnostic> InputNotes
    {
        get
        {
            List<Diagnostic> notes = new();
            for (int layerId = 1; layerId < schematic.Layers.Count; layerId++)
            {
                if (schematic.Layers[layerId].Visible) continue;

                int count = 0;
                foreach (SchematicItem item in schematic.Items)
                    if (item.LayerId == layerId)
                        count++;
                if (count == 0) continue;

                notes.Add(new Diagnostic(
                    DiagnosticSeverity.Info,
                    Code: "TTL022",
                    Message: $"Layer \u201c{schematic.Layers[layerId].Name}\u201d is hidden: "
                           + $"{count} item(s) excluded from this build."));
            }
            return notes;
        }
    }

    public IEnumerable<BuildDevice> Devices
    {
        get
        {
            foreach (Device dev in schematic.Devices)
            {
                int? powerPin;
                int? groundPin;
                UnitSpec[] specs;

                // Two part-definition flavours: gate-style (IcPartDefinition,
                // multiple UnitSpec) and box-style (ChipPartDefinition, pins as
                // a flat list belonging to a single ChipUnit).
                switch (dev.Definition)
                {
                    case IcPartDefinition ic:
                        powerPin = ic.PowerPin;
                        groundPin = ic.GroundPin;
                        specs = ic.Units;
                        break;
                    case HeaderPartDefinition:
                        powerPin = null;
                        groundPin = null;
                        specs = Array.Empty<UnitSpec>();
                        break;
                    case ChipPartDefinition cp:
                        powerPin = cp.PowerPin;
                        groundPin = cp.GroundPin;
                        specs = Array.Empty<UnitSpec>();
                        break;
                    default:
                        powerPin = null;
                        groundPin = null;
                        specs = Array.Empty<UnitSpec>();
                        break;
                }

                List<BuildUnit> units = new();
                foreach (Unit u in dev.Units)
                {
                    // Layer filter: a unit on an invisible layer is inactive --
                    // not simulated. A device whose units are all inactive
                    // (every shipped part is single-unit, so usually that means
                    // the one unit is hidden) is skipped entirely below.
                    if (!schematic.IsItemActive(u))
                        continue;

                    int[] inputs;
                    int? output;
                    IReadOnlyList<int>? outputPins = null;

                    UnitSpec? spec = null;
                    foreach (UnitSpec s in specs)
                        if (s.Letter == u.UnitLetter) { spec = s; break; }

                    if (spec is not null)
                    {
                        // Gate-style unit: UnitSpec declares inputs and a single output.
                        inputs = spec.InputPins ?? Array.Empty<int>();
                        output = (spec.OutputPin) == 0 ? null : spec.OutputPin;
                    }
                    else if (dev.Definition is ChipPartDefinition cp)
                    {
                        // ChipUnit (box-shaped IC): separate inputs from outputs
                        // for diagnostics. Both lists feed into the net map so the
                        // chip model can drive its outputs. A GAL's roles follow
                        // its loaded fuse map (a pin is whatever the program makes
                        // it); every other chip uses its static ChipPin.Role.
                        var roles = GalRoles(dev, cp)
                            ?? cp.Pins.ToDictionary(p => p.Number, p => p.Role);

                        // No-connect pins: a pin named "NC" has no bond wire on
                        // the die -- it is neither an input nor an output, so it
                        // joins neither list. Without this, an NC pin's default
                        // ChipPinRole.Input makes TTL011 flag it as a floating
                        // CMOS input (28C64 pins 1/26, 28C128 pin 1, 6264 pin 1,
                        // W24512 pins 1/2, 7420 pins 3/11, 7430 pins 9/10/13).
                        // "NC" is already the catalogue-wide convention -- the
                        // label exporter prints NC blank, and GalPinModel labels
                        // unprogrammed OLMCs "NC" -- so the name is the single
                        // source of truth; no per-chip definition changes needed.
                        // (GALs are unaffected: a programmed GAL's roles come
                        // from GalRoles, and no static GAL pin is named "NC".)
                        // A wire drawn to an NC pin still forms a net via
                        // Connections; the pin is merely exempt from the
                        // floating-input and dangling-output diagnostics.
                        var ncPins = new HashSet<int>();
                        foreach (ChipPin cpin in cp.Pins)
                            if (cpin.Name == "NC")
                                ncPins.Add(cpin.Number);

                        var inputList = new List<int>();
                        var outputList = new List<int>();
                        foreach (Pin p in u.Pins)
                        {
                            if (p.Number == cp.PowerPin || p.Number == cp.GroundPin)
                                continue;
                            if (ncPins.Contains(p.Number))
                                continue;
                            if (roles.TryGetValue(p.Number, out var role) &&
                                role == ChipPinRole.Output)
                                outputList.Add(p.Number);
                            else
                                inputList.Add(p.Number);
                        }
                        inputs = inputList.ToArray();
                        output = null;
                        outputPins = outputList.Count > 0 ? outputList : null;
                    }
                    else if (dev.Definition is HeaderPartDefinition)
                    {
                        inputs = Array.Empty<int>();
                        output = null;
                    }
                    else
                    {
                        // Fallback (passives, displays, etc.): all pins as inputs.
                        inputs = u.Pins.Select(p => p.Number).ToArray();
                        output = null;
                    }

                    units.Add(new BuildUnit(u.Id, u.UnitLetter, inputs, output,
                        OutputPinNumbers: outputPins,
                        SwitchClosed: (u is SwitchUnit sw && sw.IsClosed) || (u is SpdtSwitchUnit spdt && spdt.ThrowB),
                        SwitchPositions: u is DipSwitchUnit dip ? (bool[])dip.PositionsClosed.Clone() : null));
                }

                // Whole device on an invisible layer (no active units): drop it
                // -- no power/ground expectation, no simulation.
                if (units.Count == 0)
                    continue;

                yield return new BuildDevice(
                    DeviceId: dev.Id,
                    Designator: dev.Designator,
                    PartIdentifier: dev.Definition.Identifier,
                    Family: dev.Family?.ToString(),
                    PowerPinNumber: powerPin,
                    GroundPinNumber: groundPin,
                    Units: units,
                    Program: dev.Program,
                    PropagationDelayNs: dev.PropagationDelayNs,
                    Function1: dev.Function,
                    Function2: dev.Function2,
                    FrequencyHz1: dev.FrequencyHz,
                    FrequencyHz2: dev.FrequencyHz2,
                    IsPassive: dev.Definition is PassivePartDefinition or DipSwitchPartDefinition);
            }
        }
    }

    /// <summary>
    /// Pin roles for a GAL derived from its loaded JEDEC fuse map, or null when
    /// the device isn't a programmed GAL (the caller then falls back to the
    /// static ChipPin.Role). An array input, clock, or output-enable pin is an
    /// Input (floating-checked); a driving or unconfigured output-capable pin is
    /// an Output.
    /// </summary>
    private static Dictionary<int, ChipPinRole>? GalRoles(Device dev, ChipPartDefinition cp)
    {
        var derived = TTLSim.Chips.Pld.GalPinModel.TryDerive(cp.PartNumber, dev.Program);
        if (derived is null) return null;

        var roles = new Dictionary<int, ChipPinRole>();
        foreach (var gp in derived)
        {
            roles[gp.Number] = gp.Role switch
            {
                TTLSim.Chips.Pld.GalPinRole.Input => ChipPinRole.Input,
                TTLSim.Chips.Pld.GalPinRole.Clock => ChipPinRole.Input,
                TTLSim.Chips.Pld.GalPinRole.OutputEnable => ChipPinRole.Input,
                _ => ChipPinRole.Output,   // Output, Unused -- legitimately open
            };
        }
        return roles;
    }

    public IEnumerable<BuildItem> Items
    {
        get
        {
            // ActiveItems already excludes anything on an invisible layer.
            foreach (SchematicItem item in schematic.ActiveItems)
            {
                BuildItemKind? kind = item switch
                {
                    VccSymbol => BuildItemKind.Vcc,
                    GndSymbol => BuildItemKind.Gnd,
                    ClockSource => BuildItemKind.ClockSource,
                    CanOscillator => BuildItemKind.ClockSource,
                    TestbenchItem => BuildItemKind.Testbench,
                    _ => null
                };
                if (kind is null) continue;

                // Pin order matters for the testbench and only for the
                // testbench: TestbenchItem keeps its Pins list in program
                // column order, so column i of the CSV is PinNumbers[i].
                List<int> pins = new();
                foreach (Pin p in item.Pins)
                    pins.Add(p.Number);

                long? period = item switch
                {
                    ClockSource cs => (long)(1e12 / cs.FrequencyHz),
                    CanOscillator osc => (long)(1e12 / osc.FrequencyHz),
                    // The testbench's frequency is a ROW rate, not a square
                    // wave: one row per period, no half-period toggling.
                    TestbenchItem tb when tb.FrequencyHz > 0 => (long)(1e12 / tb.FrequencyHz),
                    _ => null
                };

                string? program = item is TestbenchItem bench ? bench.Program : null;

                yield return new BuildItem(item.Id, kind.Value, pins, period, program);
            }
        }
    }

    public IEnumerable<(PinRef A, PinRef B)> Connections
    {
        get
        {
            foreach (Connection c in schematic.Connections)
            {
                if (c.A.Owner is null || c.B.Owner is null)
                    continue;

                // Layer filter: drop a wire with an endpoint on an invisible
                // layer. Both owners are non-null here, so IsItemActive applies
                // directly to each.
                if (!schematic.IsItemActive(c.A.Owner) || !schematic.IsItemActive(c.B.Owner))
                    continue;

                yield return (
                    new PinRef(c.A.Owner.Id, c.A.Number),
                    new PinRef(c.B.Owner.Id, c.B.Number));
            }

            // Header links (ribbon cables): tie pin i of header A to pin i of
            // header B for every pin, unconditionally. The cosmetic Reversed
            // flag never affects this -- the mapping is always A.i <-> B.i.
            // Each link expands to N independent pin-pairs that NetTable unions,
            // exactly as if N wires had been drawn between the two headers.
            // (Export is unaffected: the EasyEDA writer reads schematic.Links
            // directly and skips them; links never appear on a PCB.)
            foreach (HeaderLink link in schematic.Links)
            {
                // Layer filter: a link is inactive when either endpoint header
                // is inactive -- hiding a module's layer hides its header and
                // deactivates the link's pin-pairs for free.
                if (!schematic.IsLinkActive(link))
                    continue;

                int n = link.PinCount;
                for (int i = 1; i <= n; i++)
                    yield return (new PinRef(link.A.Id, i), new PinRef(link.B.Id, i));
            }

            // Net labels / bus ports: every active label pin sharing a
            // (name, bit) key is one net, tied with no drawn wire -- the
            // helper yields chained synthetic pin-pairs that NetTable unions
            // exactly like the header-link pairs above. Label pins belong to
            // no BuildDevice or BuildItem; NetTable accepts arbitrary PinRefs
            // and the builder's diagnostics only walk declared devices, so
            // the extra pins are inert beyond their net-tying effect.
            // Activity (hidden layers) and empty-name skipping are handled
            // inside NetLabelTiePairs.
            foreach (var (a, b) in schematic.NetLabelTiePairs())
                yield return (new PinRef(a.Owner!.Id, a.Number),
                              new PinRef(b.Owner!.Id, b.Number));

            // Device-internal bonds for the 4-pin breadboard pushbutton: its two
            // legs per terminal (pins 1&2 and 3&4) are physically common, so union
            // them here. The doubled legs then share their terminal's net -- the
            // floating-pin diagnostic won't flag an unused leg, and the contact
            // model sees one node per terminal. Export is unaffected (it reads
            // schematic.Connections, not this adapter).
            foreach (Device dev in schematic.Devices)
            {
                if (dev.Definition.Identifier != "button-4") continue;
                foreach (Unit u in dev.Units)
                {
                    // Layer filter: skip a hidden button's internal bonds.
                    if (!schematic.IsItemActive(u))
                        continue;

                    yield return (new PinRef(u.Id, 1), new PinRef(u.Id, 2));
                    yield return (new PinRef(u.Id, 3), new PinRef(u.Id, 4));
                }
            }
        }
    }
}