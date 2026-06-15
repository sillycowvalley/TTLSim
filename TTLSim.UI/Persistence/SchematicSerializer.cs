using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using TTLSim.Core;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Persistence;

/// <summary>
/// Saves and loads schematics to/from .ttlproj JSON files.
///
/// Devices and units are persisted separately. Connections reference pins
/// by (itemId, pinNumber) where itemId is a unit's Id or a standalone item's
/// Id -- both are SchematicItems and share an id map on load.
///
/// Legacy files written by older versions of the editor stored Wires with
/// embedded waypoints; on load those are migrated to plain Connections and
/// the waypoint geometry is discarded (it's now derived at paint time).
/// </summary>
public static class SchematicSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ----------------------------------------------------- part lookup tables

    private static readonly Dictionary<string, IcPartDefinition> IcLookup =
        IcPartDefinition.Catalogue.ToDictionary(p => p.PartNumber);

    private static readonly Dictionary<string, ChipPartDefinition> ChipLookup =
        new()
        {
            // Memory
            [ChipPartDefinition.Ic28C256.PartNumber] = ChipPartDefinition.Ic28C256,
            [ChipPartDefinition.Ic28C128.PartNumber] = ChipPartDefinition.Ic28C128,
            [ChipPartDefinition.Ic28C64.PartNumber] = ChipPartDefinition.Ic28C64,
            [ChipPartDefinition.Ic28C16.PartNumber] = ChipPartDefinition.Ic28C16,
            [ChipPartDefinition.Ic62256.PartNumber] = ChipPartDefinition.Ic62256,
            [ChipPartDefinition.Ic6116.PartNumber] = ChipPartDefinition.Ic6116,
            [ChipPartDefinition.Ic2114.PartNumber] = ChipPartDefinition.Ic2114,

            // Timers
            [ChipPartDefinition.IcNe555.PartNumber] = ChipPartDefinition.IcNe555,
            [ChipPartDefinition.IcNe556.PartNumber] = ChipPartDefinition.IcNe556,

            [ChipPartDefinition.Ds1813.PartNumber] = ChipPartDefinition.Ds1813,

            // PLD
            [ChipPartDefinition.IcGal16V8.PartNumber] = ChipPartDefinition.IcGal16V8,
            [ChipPartDefinition.IcGal20V8.PartNumber] = ChipPartDefinition.IcGal20V8,

            // Flip-flops & counters
            [ChipPartDefinition.Ic7474.PartNumber] = ChipPartDefinition.Ic7474,
            [ChipPartDefinition.Ic74107.PartNumber] = ChipPartDefinition.Ic74107,
            [ChipPartDefinition.Ic74390.PartNumber] = ChipPartDefinition.Ic74390,
            [ChipPartDefinition.Ic74393.PartNumber] = ChipPartDefinition.Ic74393,

            // Gates
            [ChipPartDefinition.Ic7400.PartNumber] = ChipPartDefinition.Ic7400,
            [ChipPartDefinition.Ic7402.PartNumber] = ChipPartDefinition.Ic7402,
            [ChipPartDefinition.Ic7408.PartNumber] = ChipPartDefinition.Ic7408,
            [ChipPartDefinition.Ic7404.PartNumber] = ChipPartDefinition.Ic7404,
            [ChipPartDefinition.Ic7414.PartNumber] = ChipPartDefinition.Ic7414,
            [ChipPartDefinition.Ic7410.PartNumber] = ChipPartDefinition.Ic7410,
            [ChipPartDefinition.Ic7420.PartNumber] = ChipPartDefinition.Ic7420,
            [ChipPartDefinition.Ic7430.PartNumber] = ChipPartDefinition.Ic7430,
            [ChipPartDefinition.Ic7432.PartNumber] = ChipPartDefinition.Ic7432,
            [ChipPartDefinition.Ic7486.PartNumber] = ChipPartDefinition.Ic7486,

            // Registers
            [ChipPartDefinition.Ic74173.PartNumber] = ChipPartDefinition.Ic74173,
            [ChipPartDefinition.Ic74175.PartNumber] = ChipPartDefinition.Ic74175,
            [ChipPartDefinition.Ic74273.PartNumber] = ChipPartDefinition.Ic74273,
            [ChipPartDefinition.Ic74373.PartNumber] = ChipPartDefinition.Ic74373,
            [ChipPartDefinition.Ic74377.PartNumber] = ChipPartDefinition.Ic74377,
            [ChipPartDefinition.Ic74574.PartNumber] = ChipPartDefinition.Ic74574,
            [ChipPartDefinition.Ic74299.PartNumber] = ChipPartDefinition.Ic74299,
            [ChipPartDefinition.Ic74595.PartNumber] = ChipPartDefinition.Ic74595,

            // Counters
            [ChipPartDefinition.Ic74161.PartNumber] = ChipPartDefinition.Ic74161,
            [ChipPartDefinition.Ic74163.PartNumber] = ChipPartDefinition.Ic74163,
            [ChipPartDefinition.Ic74193.PartNumber] = ChipPartDefinition.Ic74193,

            // RAM
            [ChipPartDefinition.Ic74189.PartNumber] = ChipPartDefinition.Ic74189,

            // Bus / buffers / muxes
            [ChipPartDefinition.Ic74151.PartNumber] = ChipPartDefinition.Ic74151,
            [ChipPartDefinition.Ic74153.PartNumber] = ChipPartDefinition.Ic74153,
            [ChipPartDefinition.Ic74157.PartNumber] = ChipPartDefinition.Ic74157,
            [ChipPartDefinition.Ic74245.PartNumber] = ChipPartDefinition.Ic74245,
            [ChipPartDefinition.Ic74244.PartNumber] = ChipPartDefinition.Ic74244,
            [ChipPartDefinition.Ic74257.PartNumber] = ChipPartDefinition.Ic74257,
            [ChipPartDefinition.Ic74541.PartNumber] = ChipPartDefinition.Ic74541,

            // Decoders
            [ChipPartDefinition.Ic74138.PartNumber] = ChipPartDefinition.Ic74138,
            [ChipPartDefinition.Ic74139.PartNumber] = ChipPartDefinition.Ic74139,
            [ChipPartDefinition.Ic74154.PartNumber] = ChipPartDefinition.Ic74154,

            // ALU / adders
            [ChipPartDefinition.Ic74181.PartNumber] = ChipPartDefinition.Ic74181,
            [ChipPartDefinition.Ic74182.PartNumber] = ChipPartDefinition.Ic74182,
            [ChipPartDefinition.Ic74283.PartNumber] = ChipPartDefinition.Ic74283,
            [ChipPartDefinition.Ic74688.PartNumber] = ChipPartDefinition.Ic74688,

            // Display drivers
            [ChipPartDefinition.Ic7447.PartNumber] = ChipPartDefinition.Ic7447,
            [ChipPartDefinition.Ic7448.PartNumber] = ChipPartDefinition.Ic7448,
        };

    private static readonly Dictionary<string, PassivePartDefinition> PassiveLookup =
        new()
        {
            [PassivePartDefinition.Resistor.Identifier] = PassivePartDefinition.Resistor,
            [PassivePartDefinition.Capacitor.Identifier] = PassivePartDefinition.Capacitor,
            [PassivePartDefinition.PolarizedCapacitor.Identifier] = PassivePartDefinition.PolarizedCapacitor,
            [PassivePartDefinition.Led.Identifier] = PassivePartDefinition.Led,
            [PassivePartDefinition.Button.Identifier] = PassivePartDefinition.Button,
            [PassivePartDefinition.Switch.Identifier] = PassivePartDefinition.Switch,
            [PassivePartDefinition.SpdtSwitch.Identifier] = PassivePartDefinition.SpdtSwitch,
            [PassivePartDefinition.Jumper2.Identifier] = PassivePartDefinition.Jumper2,
            [PassivePartDefinition.Jumper3.Identifier] = PassivePartDefinition.Jumper3,
            [PassivePartDefinition.Crystal.Identifier] = PassivePartDefinition.Crystal,
            [PassivePartDefinition.Diode.Identifier] = PassivePartDefinition.Diode,
        };

    private static readonly Dictionary<string, HeaderPartDefinition> HeaderLookup =
        new()
        {
            [HeaderPartDefinition.HeaderOut2.Identifier] = HeaderPartDefinition.HeaderOut2,
            [HeaderPartDefinition.HeaderOut4.Identifier] = HeaderPartDefinition.HeaderOut4,
            [HeaderPartDefinition.HeaderOut3.Identifier] = HeaderPartDefinition.HeaderOut3,
            [HeaderPartDefinition.HeaderOut6.Identifier] = HeaderPartDefinition.HeaderOut6,
            [HeaderPartDefinition.HeaderOut8.Identifier] = HeaderPartDefinition.HeaderOut8,
        };

    private static readonly Dictionary<string, DisplayPartDefinition> DisplayLookup =
        new()
        {
            [DisplayPartDefinition.SevenSegmentCommonAnode.Identifier] =
                DisplayPartDefinition.SevenSegmentCommonAnode,
            [DisplayPartDefinition.SevenSegmentCommonCathode.Identifier] =
                DisplayPartDefinition.SevenSegmentCommonCathode,
        };

    // ------------------------------------------------------------------ Save

    public static void Save(string path, Schematic schematic, float zoom, PointF pan)
    {
        var file = new SchematicFile
        {
            Schematic = ToDto(schematic),
            View = new ViewDto
            {
                Zoom = zoom,
                Pan = new PointDto { X = (int)pan.X, Y = (int)pan.Y }
            }
        };
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static SchematicDto ToDto(Schematic schematic)
    {
        var dto = new SchematicDto();

        foreach (var device in schematic.Devices)
            dto.Devices.Add(DeviceToDto(device));

        foreach (var item in schematic.Items)
        {
            switch (item)
            {
                case Unit unit:
                    dto.Units.Add(UnitToDto(unit));
                    break;
                case VccSymbol or GndSymbol or ClockSource or CanOscillator:
                    dto.Items.Add(StandaloneItemToDto(item));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Cannot serialize item of type {item.GetType().Name}: no DTO mapping defined.");
            }
        }

        foreach (var connection in schematic.Connections)
            dto.Connections.Add(ConnectionToDto(connection));

        return dto;
    }

    private static DeviceDto DeviceToDto(Device device) => device.Definition switch
    {
        IcPartDefinition ic => new DeviceDto
        {
            Id = device.Id,
            Designator = device.Designator,
            PartKind = "ic",
            PartIdentifier = ic.PartNumber,
            Family = device.Family?.ToString()
        },
        ChipPartDefinition chip => new DeviceDto
        {
            Id = device.Id,
            Designator = device.Designator,
            PartKind = "chip",
            PartIdentifier = chip.PartNumber,
            Family = chip.IsSeries74 ? device.Family?.ToString() : null,
            Program = device.Program,       // EEPROM/ROM Intel HEX image; null otherwise
            PropagationDelayNs = device.UsesExplicitDelay ? device.PropagationDelayNs : null,
            // 555/556 timer settings; null for non-timer chips.
            Function1 = device.IsTimer ? device.Function?.ToString() : null,
            FrequencyHz1 = device.IsTimer ? device.FrequencyHz : null,
            Function2 = device.Is556 ? device.Function2?.ToString() : null,
            FrequencyHz2 = device.Is556 ? device.FrequencyHz2 : null
        },
        PassivePartDefinition p => new DeviceDto
        {
            Id = device.Id,
            Designator = device.Designator,
            PartKind = "passive",
            PartIdentifier = p.Identifier,
            Family = null,
            Value = device.Value
        },
        HeaderPartDefinition h => new DeviceDto
        {
            Id = device.Id,
            Designator = device.Designator,
            PartKind = "header",
            PartIdentifier = h.Identifier,
            Family = null
        },
        DisplayPartDefinition d => new DeviceDto
        {
            Id = device.Id,
            Designator = device.Designator,
            PartKind = "display",
            PartIdentifier = d.Identifier,
            Family = null
        },
        _ => throw new InvalidOperationException(
            $"Unknown PartDefinition type: {device.Definition.GetType().Name}")
    };

    private static UnitDto UnitToDto(Unit unit) => new()
    {
        Id = unit.Id,
        DeviceId = unit.Device.Id,
        UnitLetter = unit.UnitLetter == '\0' ? "" : unit.UnitLetter.ToString(),
        Position = new PointDto { X = unit.Position.X, Y = unit.Position.Y },
        Label = unit.Label,
        Rotation = (int)unit.Rotation,
        SwitchClosed = unit switch
        {
            SwitchUnit sw => sw.IsClosed,
            SpdtSwitchUnit spdt => spdt.ThrowB,
            _ => (bool?)null
        }
    };

    private static ItemDto StandaloneItemToDto(SchematicItem item)
    {
        var dto = new ItemDto
        {
            Id = item.Id,
            Label = item.Label,
            Position = new PointDto { X = item.Position.X, Y = item.Position.Y },
            Rotation = (int)item.Rotation,
            Type = item switch
            {
                VccSymbol => "vcc",
                GndSymbol => "gnd",
                ClockSource => "clock",
                CanOscillatorDip8 => "canosc8",
                CanOscillator => "canosc",
                _ => throw new InvalidOperationException(
                    $"No standalone-item discriminator for {item.GetType().Name}")
            }
        };

        if (item is ClockSource clk)
        {
            dto.FrequencyHz = clk.FrequencyHz;
            dto.DutyCycle = clk.DutyCycle;
            dto.StartHigh = clk.StartHigh;
        }

        if (item is CanOscillator osc)
            dto.FrequencyHz = osc.FrequencyHz;

        if (item is IDesignatedItem des)
            dto.Designator = des.Designator;

        return dto;
    }

    private static ConnectionDto ConnectionToDto(Connection connection) => new()
    {
        Id = connection.Id,
        A = PinRefToDto(connection.A),
        B = PinRefToDto(connection.B),
        Color = connection.Color == WireColor.Black ? null : connection.Color.ToString()
    };

    private static PinRefDto PinRefToDto(Pin pin) => new()
    {
        ItemId = pin.Owner!.Id,
        PinNumber = pin.Number
    };

    // ------------------------------------------------------------------ Load

    public sealed class LoadResult
    {
        public Schematic Schematic { get; init; } = new();
        public float? Zoom { get; init; }
        public PointF? Pan { get; init; }
    }

    public static LoadResult Load(string path)
    {
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<SchematicFile>(json, JsonOptions)
            ?? throw new InvalidDataException("File is empty or invalid.");

        var schematic = new Schematic();

        // Pass 1: Devices, indexed by id.
        var devicesById = new Dictionary<string, Device>();
        foreach (var dto in file.Schematic.Devices)
        {
            var device = CreateDevice(dto);
            schematic.Devices.Add(device);
            devicesById[device.Id] = device;
        }

        // Pass 2: Units. Resolve each unit's device by id, instantiate the
        // right Unit subclass from the device's PartDefinition, and add to
        // both Device.Units and Schematic.Items. Build an id map so
        // connections can resolve pin endpoints.
        var itemsById = new Dictionary<string, SchematicItem>();
        foreach (var dto in file.Schematic.Units)
        {
            if (!devicesById.TryGetValue(dto.DeviceId, out var device))
                throw new InvalidDataException(
                    $"Unit '{dto.Id}' references unknown device id '{dto.DeviceId}'.");

            var unit = CreateUnit(device, dto);
            schematic.Items.Add(unit);
            itemsById[unit.Id] = unit;
        }

        // Pass 3: Standalone items (VCC, GND, ClockSource).
        foreach (var dto in file.Schematic.Items)
        {
            var item = CreateStandaloneItem(dto);
            item.Id = dto.Id;
            item.Label = dto.Label;
            item.Position = new Point(dto.Position.X, dto.Position.Y);
            item.Rotation = ParseRotation(dto.Rotation);

            // Designated items keep the file's designator; files that predate
            // designators get the next free one (unique against what's loaded).
            if (item is IDesignatedItem des)
                des.Designator = string.IsNullOrEmpty(dto.Designator)
                    ? schematic.NextDesignator(des.ReferencePrefix)
                    : dto.Designator;

            schematic.Items.Add(item);
            itemsById[item.Id] = item;
        }

        // Pass 4a: Connections (current file format).
        foreach (var dto in file.Schematic.Connections)
        {
            var a = ResolvePin(dto.A, itemsById);
            var b = ResolvePin(dto.B, itemsById);
            if (a == null || b == null) continue;

            var connection = new Connection(a, b) { Id = dto.Id };
            if (!string.IsNullOrEmpty(dto.Color)
                && Enum.TryParse<WireColor>(dto.Color, ignoreCase: true, out var wc))
            {
                connection.Color = wc;
            }
            schematic.Connections.Add(connection);
        }

        // Pass 4b: Legacy Wires migrated to Connections. Free-point endpoints
        // (which existed for T-junctions in the old model) are dropped: they
        // don't have a pin to attach to. Waypoint geometry is discarded; the
        // new model derives geometry at render time.
        foreach (var dto in file.Schematic.Wires)
        {
            if (dto.A.FreePoint != null || dto.B.FreePoint != null) continue;

            var a = ResolveLegacyPin(dto.A, itemsById);
            var b = ResolveLegacyPin(dto.B, itemsById);
            if (a == null || b == null) continue;
            schematic.Connections.Add(new Connection(a, b) { Id = dto.Id });
        }

        return new LoadResult
        {
            Schematic = schematic,
            Zoom = file.View?.Zoom,
            Pan = file.View != null ? new PointF(file.View.Pan.X, file.View.Pan.Y) : null
        };
    }

    private static Rotation ParseRotation(int degrees) => degrees switch
    {
        0 => Rotation.R0,
        90 => Rotation.R90,
        180 => Rotation.R180,
        270 => Rotation.R270,
        _ => throw new InvalidDataException(
            $"Invalid rotation '{degrees}'. Expected 0, 90, 180, or 270.")
    };

    private static Device CreateDevice(DeviceDto dto)
    {
        PartDefinition definition = dto.PartKind switch
        {
            "ic" => IcLookup.TryGetValue(dto.PartIdentifier, out var ic)
                ? ic
                : throw new InvalidDataException(
                    $"Unknown IC part number '{dto.PartIdentifier}'. Known: {string.Join(", ", IcLookup.Keys)}."),
            "chip" => ChipLookup.TryGetValue(dto.PartIdentifier, out var chip)
                ? chip
                : throw new InvalidDataException(
                    $"Unknown chip part number '{dto.PartIdentifier}'. Known: {string.Join(", ", ChipLookup.Keys)}."),
            "passive" => PassiveLookup.TryGetValue(dto.PartIdentifier, out var p)
                ? p
                : throw new InvalidDataException(
                    $"Unknown passive '{dto.PartIdentifier}'. Known: {string.Join(", ", PassiveLookup.Keys)}."),
            "display" => DisplayLookup.TryGetValue(dto.PartIdentifier, out var d)
                ? d
                : throw new InvalidDataException(
                    $"Unknown display '{dto.PartIdentifier}'. Known: {string.Join(", ", DisplayLookup.Keys)}."),
            "header" => HeaderLookup.TryGetValue(dto.PartIdentifier, out var h)
                ? h
                : throw new InvalidDataException(
                    $"Unknown header '{dto.PartIdentifier}'. Known: {string.Join(", ", HeaderLookup.Keys)}."),
            _ => throw new InvalidDataException(
                $"Unknown part kind '{dto.PartKind}'. Expected 'ic', 'chip', 'passive', 'header', or 'display'.")
        };

        var device = new Device(definition)
        {
            Id = dto.Id,
            Designator = dto.Designator,
            Program = dto.Program,     // EEPROM/ROM Intel HEX image; null for other parts
            PropagationDelayNs = dto.PropagationDelayNs   // memory/PLD only; null otherwise
        };

        bool isFamilyBearer = definition is IcPartDefinition
            || (definition is ChipPartDefinition cp && cp.IsSeries74);

        if (isFamilyBearer && !string.IsNullOrEmpty(dto.Family))
        {
            if (Enum.TryParse<TtlFamily>(dto.Family, out var fam))
                device.Family = fam;
            else
                throw new InvalidDataException(
                    $"Unknown TTL family '{dto.Family}' on device {dto.Designator}.");
        }
        else if (definition is PassivePartDefinition)
        {
            device.Family = null;
            device.Value = dto.Value;
        }

        // 555/556 timer settings. The Device constructor has already applied
        // sensible defaults (Schmitt + 1000 Hz), so older files without these
        // fields load fine; here we just override from whatever the file
        // carried.
        if (device.IsTimer)
        {
            if (!string.IsNullOrEmpty(dto.Function1)
                && Enum.TryParse<TimerFunction>(dto.Function1, out var fn1))
                device.Function = fn1;
            if (dto.FrequencyHz1 is double hz1)
                device.FrequencyHz = hz1;

            if (device.Is556)
            {
                if (!string.IsNullOrEmpty(dto.Function2)
                    && Enum.TryParse<TimerFunction>(dto.Function2, out var fn2))
                    device.Function2 = fn2;
                if (dto.FrequencyHz2 is double hz2)
                    device.FrequencyHz2 = hz2;
            }
        }

        return device;
    }

    private static Unit CreateUnit(Device device, UnitDto dto)
    {
        char letter = string.IsNullOrEmpty(dto.UnitLetter) ? '\0' : dto.UnitLetter[0];

        // Find the spec matching this unit's letter.
        UnitSpec? spec = device.Definition switch
        {
            IcPartDefinition ic => Array.Find(ic.Units, u => u.Letter == letter),
            ChipPartDefinition => new UnitSpec(UnitKind.Chip, '\0',
                InputPins: Array.Empty<int>(), OutputPin: 0),
            PassivePartDefinition p => new UnitSpec(p.UnitKind, '\0',
                InputPins: Array.Empty<int>(), OutputPin: 0),
            HeaderPartDefinition => new UnitSpec(UnitKind.HeaderOutput, '\0',
                InputPins: Array.Empty<int>(), OutputPin: 0),
            DisplayPartDefinition d => new UnitSpec(d.UnitKind, '\0',
                InputPins: Array.Empty<int>(), OutputPin: 0),
            _ => null
        };

        if (spec == null)
            throw new InvalidDataException(
                $"Device {device.Designator} ({device.Definition.Identifier}) has no unit with letter '{letter}'.");

        Unit unit = spec.Kind switch
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
            UnitKind.Chip => DeviceFactory.CreateChipSymbol(device, spec, (ChipPartDefinition)device.Definition),
            UnitKind.HeaderOutput => new HeaderOutputUnit(device, spec, (HeaderPartDefinition)device.Definition),
            UnitKind.DFlipFlop => throw new NotImplementedException(
                "Cannot load unit of kind DFlipFlop: not yet implemented."),
            UnitKind.Power => throw new NotImplementedException(
                "Cannot load Power units: not yet implemented."),
            _ => throw new InvalidDataException($"Unknown UnitKind: {spec.Kind}")
        };

        unit.Id = dto.Id;
        unit.Label = dto.Label;
        unit.Position = new Point(dto.Position.X, dto.Position.Y);
        unit.Rotation = ParseRotation(dto.Rotation);
        if (unit is SwitchUnit swUnit && dto.SwitchClosed is bool closed)
            swUnit.IsClosed = closed;
        if (unit is SpdtSwitchUnit spdt && dto.SwitchClosed is bool pos)
            spdt.ThrowB = pos;
        device.Units.Add(unit);
        return unit;
    }

    private static SchematicItem CreateStandaloneItem(ItemDto dto) => dto.Type switch
    {
        "vcc" => new VccSymbol(),
        "gnd" => new GndSymbol(),
        "clock" => new ClockSource
        {
            FrequencyHz = dto.FrequencyHz ?? 1_000_000.0,
            DutyCycle = dto.DutyCycle ?? 0.5,
            StartHigh = dto.StartHigh ?? false
        },
        "canosc" => new CanOscillator { FrequencyHz = dto.FrequencyHz ?? 1_000_000.0 },
        "canosc8" => new CanOscillatorDip8 { FrequencyHz = dto.FrequencyHz ?? 1_000_000.0 },
        _ => throw new InvalidDataException(
            $"Unknown standalone item type '{dto.Type}'. Expected 'vcc', 'gnd', or 'clock'.")
    };

    private static Pin? ResolvePin(PinRefDto dto,
        Dictionary<string, SchematicItem> itemsById)
    {
        if (string.IsNullOrEmpty(dto.ItemId)) return null;
        if (!itemsById.TryGetValue(dto.ItemId, out var item)) return null;
        return item.Pins.FirstOrDefault(p => p.Number == dto.PinNumber);
    }

    private static Pin? ResolveLegacyPin(EndpointDto dto,
        Dictionary<string, SchematicItem> itemsById)
    {
        if (string.IsNullOrEmpty(dto.ItemId)) return null;
        if (!itemsById.TryGetValue(dto.ItemId, out var item)) return null;
        return item.Pins.FirstOrDefault(p => p.Number == dto.PinNumber);
    }
}