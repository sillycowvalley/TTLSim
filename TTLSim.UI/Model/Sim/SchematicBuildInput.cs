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
                        var inputList = new List<int>();
                        var outputList = new List<int>();
                        foreach (Pin p in u.Pins)
                        {
                            if (p.Number == cp.PowerPin || p.Number == cp.GroundPin)
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
                        SwitchClosed: (u is SwitchUnit sw && sw.IsClosed) || (u is SpdtSwitchUnit spdt && spdt.ThrowB)));
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
                    IsPassive: dev.Definition is PassivePartDefinition);
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
                    _ => null
                };
                if (kind is null) continue;

                List<int> pins = new();
                foreach (Pin p in item.Pins)
                    pins.Add(p.Number);

                long? period = item switch
                {
                    ClockSource cs => (long)(1e12 / cs.FrequencyHz),
                    CanOscillator osc => (long)(1e12 / osc.FrequencyHz),
                    _ => null
                };

                yield return new BuildItem(item.Id, kind.Value, pins, period);
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