using System;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Builds a Device from a PartDefinition: instantiates one Unit per UnitSpec,
/// lays them out at a drop point, and assigns the next available designator.
/// The caller is responsible for adding the resulting Device (and its Units)
/// to the schematic inside an undoable composite.
/// </summary>
public static class DeviceFactory
{
    /// <summary>
    /// Create a Device with all its units laid out around <paramref name="dropPoint"/>.
    /// For multi-unit devices the units are arranged in a horizontal chain --
    /// each gate's output pin aligned to the next gate's first input pin so
    /// that a wire would run straight between them. For single-unit parts
    /// (passives) the one unit is centred on the drop point.
    /// </summary>
    public static Device Create(PartDefinition definition, Point dropPoint, Schematic schematic)
    {
        var device = new Device(definition)
        {
            Designator = schematic.NextDesignator(definition.ReferencePrefix)
        };

        switch (definition)
        {
            case IcPartDefinition ic:
                BuildIcUnits(device, ic, dropPoint);
                break;

            case ChipPartDefinition chip:
                BuildChipUnit(device, chip, dropPoint);
                break;

            case HeaderPartDefinition header:
                BuildHeaderUnit(device, header, dropPoint);
                break;

            case PassivePartDefinition passive:
                BuildPassiveUnit(device, passive, dropPoint);
                break;

            case DisplayPartDefinition display:
                BuildDisplayUnit(device, display, dropPoint);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(definition),
                    $"Unknown part definition type: {definition.GetType().Name}");
        }

        return device;
    }

    private static void BuildIcUnits(Device device, IcPartDefinition ic, Point dropPoint)
    {
        // Lay gates out in a horizontal chain. Each gate's output Y becomes
        // the next gate's first-input Y, so a wire between them would be
        // straight. The chain is centred horizontally on dropPoint.X and
        // vertically on dropPoint.Y for the first gate's output line.
        const int interGateGap = 3;  // cells between one gate's end and the next gate's start

        // First pass: materialise units so we know their sizes.
        var units = new Unit[ic.Units.Length];
        int totalWidth = 0;
        for (int i = 0; i < ic.Units.Length; i++)
        {
            units[i] = CreateUnit(device, ic.Units[i]);
            totalWidth += units[i].Size.Width;
            if (i > 0) totalWidth += interGateGap;
        }

        // Place each unit. The "chain Y" is where each gate's output sits; we
        // align gate (i+1)'s first input to gate i's output so wires would be
        // straight even with multi-input gates of differing heights.
        int x = dropPoint.X - totalWidth / 2;
        int chainOutputY = dropPoint.Y;

        for (int i = 0; i < units.Length; i++)
        {
            var unit = units[i];
            // Output sits at Size.Height/2 below the unit's top, so position
            // the unit so its output ends up at chainOutputY.
            int unitTop = chainOutputY - unit.Size.Height / 2;
            unit.Position = new Point(x, unitTop);

            device.Units.Add(unit);
            x += unit.Size.Width + interGateGap;

            // For the next gate, align its first input to this gate's output.
            // Since BuildLeftInputsRightOutput places the first input at y=1
            // and the output at y=Size.Height/2, "chain output Y" naturally
            // continues across heterogeneous gates.
        }
    }

    private static void BuildPassiveUnit(Device device, PassivePartDefinition passive, Point dropPoint)
    {
        var spec = new UnitSpec(passive.UnitKind, '\0',
            InputPins: Array.Empty<int>(),
            OutputPin: 0);
        var unit = CreateUnit(device, spec);
        unit.Position = new Point(
            dropPoint.X - unit.Size.Width / 2,
            dropPoint.Y - unit.Size.Height / 2);
        device.Units.Add(unit);
    }

    private static void BuildChipUnit(Device device, ChipPartDefinition chip, Point dropPoint)
    {
        var spec = new UnitSpec(UnitKind.Chip, '\0',
            InputPins: Array.Empty<int>(),
            OutputPin: 0);
        var unit = CreateChipSymbol(device, spec, chip);
        unit.Position = new Point(
            dropPoint.X - unit.Size.Width / 2,
            dropPoint.Y - unit.Size.Height / 2);
        device.Units.Add(unit);
    }

    private static void BuildHeaderUnit(Device device, HeaderPartDefinition header, Point dropPoint)
    {
        var spec = new UnitSpec(UnitKind.HeaderOutput, '\0',
            InputPins: Array.Empty<int>(),
            OutputPin: 0);
        var unit = new HeaderOutputUnit(device, spec, header);
        unit.Position = new Point(
            dropPoint.X - unit.Size.Width / 2,
            dropPoint.Y - unit.Size.Height / 2);
        device.Units.Add(unit);
    }

    private static void BuildDisplayUnit(Device device, DisplayPartDefinition display, Point dropPoint)
    {
        var spec = new UnitSpec(display.UnitKind, '\0',
            InputPins: Array.Empty<int>(),
            OutputPin: 0);
        var unit = CreateUnit(device, spec);
        unit.Position = new Point(
            dropPoint.X - unit.Size.Width / 2,
            dropPoint.Y - unit.Size.Height / 2);
        device.Units.Add(unit);
    }

    private static Unit CreateUnit(Device device, UnitSpec spec) => spec.Kind switch
    {
        UnitKind.Nand => new NandGateUnit(device, spec),
        UnitKind.Nor => new NorGateUnit(device, spec),
        UnitKind.And => new AndGateUnit(device, spec),
        UnitKind.Or => new OrGateUnit(device, spec),
        UnitKind.Xor => new XorGateUnit(device, spec),
        UnitKind.Not => new NotGateUnit(device, spec),
        UnitKind.Resistor => new ResistorUnit(device, spec),
        UnitKind.Capacitor => new CapacitorUnit(device, spec),
        UnitKind.PolarizedCapacitor => new PolarizedCapacitorUnit(device, spec),
        UnitKind.Led => new LedUnit(device, spec),
        UnitKind.Button => new ButtonUnit(device, spec),
        UnitKind.Switch => new SwitchUnit(device, spec),
        UnitKind.SpdtSwitch => new SpdtSwitchUnit(device, spec),
        UnitKind.Crystal => new CrystalUnit(device, spec),
        UnitKind.Diode => new DiodeUnit(device, spec),
        UnitKind.SevenSegment => new SevenSegmentDisplayUnit(device, spec),
        UnitKind.DFlipFlop => throw new NotImplementedException(
            "DFlipFlopUnit is not yet implemented; 7474 support pending."),
        UnitKind.Power => throw new InvalidOperationException(
            "Power units are created on demand via the 'show power' action, not by DeviceFactory."),
        UnitKind.Chip => throw new InvalidOperationException(
            "Chip units require a ChipPartDefinition and are built via BuildChipUnit, not by Kind alone."),
        _ => throw new ArgumentOutOfRangeException()
    };

    /// <summary>
    /// Pick the symbol class for a box/chip part: a TO-92 transistor outline when
    /// the definition opts in via To92, otherwise the standard DIP box. Shared by
    /// placement, load, and paste so all three render the part the same way.
    /// </summary>
    public static Unit CreateChipSymbol(Device device, UnitSpec spec, ChipPartDefinition definition) =>
        definition.To92
            ? new To92Unit(device, spec, definition)
            : new ChipUnit(device, spec, definition);
}