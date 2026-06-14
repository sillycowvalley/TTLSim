using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

// Two ClockSource types exist: TTLSim.Chips.Sources.ClockSource is the IChip
// simulation model; the one we deal with here is the UI SchematicItem. Alias
// it so there is no ambiguity anywhere in this file.
using UiClockSource = TTLSim.UI.Components.ClockSource;

namespace TTLSim.UI.Persistence;

/// <summary>
/// Converts between the live schematic object graph and the flat
/// <see cref="SchematicDto"/> shape, in both directions.
///
/// This is the single home for DTO &lt;-&gt; model reconstruction. Both
/// <see cref="SchematicSerializer"/> (whole-file save/load) and
/// <see cref="ClipboardService"/> (copy/paste of a selection) call into it,
/// so the part-lookup tables and the per-unit construction logic live in
/// exactly one place.
///
/// <para>
/// <see cref="ToDto"/> takes loose collections rather than a whole
/// <see cref="Schematic"/> so a caller can serialise an arbitrary subset
/// (the selection). Connections whose endpoints are not in the supplied item
/// set are dropped -- the same rule the loader applies to dangling legacy
/// wires.
/// </para>
///
/// <para>
/// <see cref="FromDto"/> rebuilds the object graph. The <see cref="IdPolicy"/>
/// argument decides whether ids and designators are kept verbatim
/// (<see cref="IdPolicy.Preserve"/> -- the load path) or regenerated fresh
/// with connection endpoints remapped (<see cref="IdPolicy.Fresh"/> -- the
/// paste path).
/// </para>
/// </summary>
public static class SchematicDtoMapper
{
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
            [ChipPartDefinition.Ic62256.PartNumber] = ChipPartDefinition.Ic62256,
            [ChipPartDefinition.Ic6116.PartNumber] = ChipPartDefinition.Ic6116,
            [ChipPartDefinition.Ic2114.PartNumber] = ChipPartDefinition.Ic2114,

            // Timers
            [ChipPartDefinition.IcNe555.PartNumber] = ChipPartDefinition.IcNe555,
            [ChipPartDefinition.IcNe556.PartNumber] = ChipPartDefinition.IcNe556,
            [ChipPartDefinition.IcGal16V8.PartNumber] = ChipPartDefinition.IcGal16V8,
            [ChipPartDefinition.IcGal20V8.PartNumber] = ChipPartDefinition.IcGal20V8,
            [ChipPartDefinition.Ds1813.PartNumber] = ChipPartDefinition.Ds1813,

            // Flip-flops & counters
            [ChipPartDefinition.Ic7474.PartNumber] = ChipPartDefinition.Ic7474,
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
            [ChipPartDefinition.Ic74273.PartNumber] = ChipPartDefinition.Ic74273,
            [ChipPartDefinition.Ic74377.PartNumber] = ChipPartDefinition.Ic74377,
            [ChipPartDefinition.Ic74574.PartNumber] = ChipPartDefinition.Ic74574,
            [ChipPartDefinition.Ic74299.PartNumber] = ChipPartDefinition.Ic74299,

            // Counters
            [ChipPartDefinition.Ic74161.PartNumber] = ChipPartDefinition.Ic74161,
            [ChipPartDefinition.Ic74163.PartNumber] = ChipPartDefinition.Ic74163,
            [ChipPartDefinition.Ic74193.PartNumber] = ChipPartDefinition.Ic74193,

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
            [PassivePartDefinition.Crystal.Identifier] = PassivePartDefinition.Crystal,
            [PassivePartDefinition.Diode.Identifier] = PassivePartDefinition.Diode,
        };

    private static readonly Dictionary<string, DisplayPartDefinition> DisplayLookup =
        new()
        {
            [DisplayPartDefinition.SevenSegmentCommonAnode.Identifier] =
                DisplayPartDefinition.SevenSegmentCommonAnode,
            [DisplayPartDefinition.SevenSegmentCommonCathode.Identifier] =
                DisplayPartDefinition.SevenSegmentCommonCathode,
        };

    private static readonly Dictionary<string, HeaderPartDefinition> HeaderLookup =
        new()
        {
            [HeaderPartDefinition.HeaderOut2.Identifier] = HeaderPartDefinition.HeaderOut2,
            [HeaderPartDefinition.HeaderOut4.Identifier] = HeaderPartDefinition.HeaderOut4,
            [HeaderPartDefinition.HeaderOut6.Identifier] = HeaderPartDefinition.HeaderOut6,
            [HeaderPartDefinition.HeaderOut8.Identifier] = HeaderPartDefinition.HeaderOut8,
        };

    // Fetched fresh per access rather than cached: Logging.Log.Reset()
    // (called on Build) disposes and replaces the logger factory, which
    // would leave a cached ILogger stale.
    private static Microsoft.Extensions.Logging.ILogger Log =>
        Logging.Log.For(nameof(SchematicDtoMapper));

    // ====================================================================
    //  model -> DTO
    // ====================================================================

    /// <summary>
    /// Build a <see cref="SchematicDto"/> from loose collections. Pass a whole
    /// schematic's contents to serialise everything, or a selection to
    /// serialise a subset.
    ///
    /// <para>
    /// A connection is included only when BOTH of its endpoint items appear
    /// in <paramref name="items"/>. Connections to items outside the set are
    /// silently dropped -- for a whole-schematic call every item is present
    /// so nothing is lost; for a selection call this is the desired "only
    /// copy wires fully inside the selection" behaviour.
    /// </para>
    /// </summary>
    public static SchematicDto ToDto(
        IEnumerable<Device> devices,
        IEnumerable<SchematicItem> items,
        IEnumerable<Connection> connections)
    {
        var dto = new SchematicDto();

        foreach (var device in devices)
            dto.Devices.Add(DeviceToDto(device));

        // Track which item ids made it into the DTO so connections can be
        // filtered to those fully inside the set.
        var itemIds = new HashSet<string>();

        foreach (var item in items)
        {
            switch (item)
            {
                case Unit unit:
                    dto.Units.Add(UnitToDto(unit));
                    break;
                case VccSymbol or GndSymbol or UiClockSource:
                    dto.Items.Add(StandaloneItemToDto(item));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Cannot serialize item of type {item.GetType().Name}: no DTO mapping defined.");
            }
            itemIds.Add(item.Id);
        }

        foreach (var connection in connections)
        {
            // A connection needs both endpoint pins to have owners at all --
            // an ownerless pin can't be referenced by id, so such a
            // connection is genuinely uncopyable and is dropped.
            if (connection.A.Owner is not { } ownerA || connection.B.Owner is not { } ownerB)
                continue;

            // Each endpoint is INTERNAL when its owning item is in the copied
            // set, EXTERNAL when it isn't. A fully-internal connection copies
            // as before. A half-external connection ("copy this item plus the
            // wire dangling off it") is kept: the external endpoint records
            // the ORIGINAL item id, and on paste resolves against the live
            // destination schematic instead of the old->new id map. A wire
            // with BOTH endpoints external would have no internal anchor at
            // all -- nothing of it was really copied -- so it is dropped.
            bool aExternal = !itemIds.Contains(ownerA.Id);
            bool bExternal = !itemIds.Contains(ownerB.Id);

            if (aExternal && bExternal)
                continue;

            dto.Connections.Add(new ConnectionDto
            {
                Id = connection.Id,
                A = new PinRefDto
                {
                    ItemId = ownerA.Id,
                    PinNumber = connection.A.Number,
                    External = aExternal
                },
                B = new PinRefDto
                {
                    ItemId = ownerB.Id,
                    PinNumber = connection.B.Number,
                    External = bExternal
                },
                Color = connection.Color == WireColor.Black
                    ? null : connection.Color.ToString()
            });
        }

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
            PropagationDelayNs = device.UsesExplicitDelay ? device.PropagationDelayNs : null
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
        SwitchClosed = unit is SwitchUnit sw ? sw.IsClosed : null
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
                UiClockSource => "clock",
                _ => throw new InvalidOperationException(
                    $"No standalone-item discriminator for {item.GetType().Name}")
            }
        };

        if (item is UiClockSource clk)
        {
            dto.FrequencyHz = clk.FrequencyHz;
            dto.DutyCycle = clk.DutyCycle;
            dto.StartHigh = clk.StartHigh;
        }

        return dto;
    }

    // ====================================================================
    //  DTO -> model
    // ====================================================================

    /// <summary>
    /// The rebuilt object graph. The caller decides what to do with it:
    /// <see cref="SchematicSerializer"/> drops the lists straight into a fresh
    /// <see cref="Schematic"/>; <see cref="ClipboardService"/> hands them to
    /// the canvas to add through the undo stack.
    /// </summary>
    public sealed class MapResult
    {
        public List<Device> Devices { get; } = new();
        public List<SchematicItem> Items { get; } = new();
        public List<Connection> Connections { get; } = new();
    }

    /// <summary>
    /// Rebuild the object graph from a <see cref="SchematicDto"/>.
    ///
    /// <para>
    /// With <see cref="IdPolicy.Preserve"/> the dto's ids and designators are
    /// kept verbatim -- this is the load path, where the file IS the source
    /// of truth.
    /// </para>
    ///
    /// <para>
    /// With <see cref="IdPolicy.Fresh"/> every device and item is given a new
    /// id, connection endpoints are remapped through the old-&gt;new id table,
    /// and each device is given the next free designator for its reference
    /// prefix. <paramref name="designatorScope"/> MUST be supplied in this
    /// case -- it is the schematic the new designators are made unique
    /// against.
    /// </para>
    ///
    /// <para>
    /// Units of a kind that cannot currently be constructed (Power,
    /// DFlipFlop) are skipped with a log warning rather than throwing, so a
    /// selection that somehow contains one still pastes the rest. Connections
    /// referencing a skipped unit's pins are dropped along with it.
    /// </para>
    /// </summary>
    public static MapResult FromDto(
        SchematicDto dto,
        IdPolicy idPolicy,
        Schematic? designatorScope = null)
    {
        if (idPolicy == IdPolicy.Fresh && designatorScope is null)
            throw new ArgumentNullException(nameof(designatorScope),
                "IdPolicy.Fresh requires a designatorScope to make new designators unique against.");

        var result = new MapResult();
        bool fresh = idPolicy == IdPolicy.Fresh;

        // old id -> new device. Built in the device pass, read in the unit pass.
        var devicesByOldId = new Dictionary<string, Device>();
        // old item id -> rebuilt item. Built across the unit + standalone
        // passes, read in the connection pass.
        var itemsByOldId = new Dictionary<string, SchematicItem>();

        // ---- Pass 1: devices ------------------------------------------------
        foreach (var deviceDto in dto.Devices)
        {
            var device = CreateDevice(deviceDto);
            if (fresh)
            {
                device.Id = Guid.NewGuid().ToString("N");
                device.Designator =
                    designatorScope!.NextDesignator(device.Definition.ReferencePrefix);
            }
            result.Devices.Add(device);
            devicesByOldId[deviceDto.Id] = device;
        }

        // ---- Pass 2: units --------------------------------------------------
        foreach (var unitDto in dto.Units)
        {
            if (!devicesByOldId.TryGetValue(unitDto.DeviceId, out var device))
                throw new InvalidOperationException(
                    $"Unit '{unitDto.Id}' references unknown device id '{unitDto.DeviceId}'.");

            Unit? unit = TryCreateUnit(device, unitDto);
            if (unit is null)
                continue;   // unconstructable kind -- already logged; skip it

            if (fresh)
                unit.Id = Guid.NewGuid().ToString("N");

            result.Items.Add(unit);
            itemsByOldId[unitDto.Id] = unit;
        }

        // ---- Pass 3: standalone items (VCC, GND, ClockSource) ---------------
        foreach (var itemDto in dto.Items)
        {
            var item = CreateStandaloneItem(itemDto);
            item.Id = fresh ? Guid.NewGuid().ToString("N") : itemDto.Id;
            item.Label = itemDto.Label;
            item.Position = new Point(itemDto.Position.X, itemDto.Position.Y);
            item.Rotation = ParseRotation(itemDto.Rotation);
            result.Items.Add(item);
            itemsByOldId[itemDto.Id] = item;
        }

        // ---- Pass 4: connections -------------------------------------------
        // Resolve each endpoint against the rebuilt items. An endpoint whose
        // owning item was skipped (unconstructable unit) won't be in the map,
        // so the connection is dropped.
        foreach (var connDto in dto.Connections)
        {
            var a = ResolvePin(connDto.A, itemsByOldId, designatorScope);
            var b = ResolvePin(connDto.B, itemsByOldId, designatorScope);
            if (a is null || b is null)
                continue;

            var connection = new Connection(a, b)
            {
                Id = fresh ? Guid.NewGuid().ToString("N") : connDto.Id
            };
            if (!string.IsNullOrEmpty(connDto.Color)
                && Enum.TryParse<WireColor>(connDto.Color, ignoreCase: true, out var wc))
            {
                connection.Color = wc;
            }
            result.Connections.Add(connection);
        }

        return result;
    }

    // ------------------------------------------------------- reconstruction

    private static Rotation ParseRotation(int degrees) => degrees switch
    {
        0 => Rotation.R0,
        90 => Rotation.R90,
        180 => Rotation.R180,
        270 => Rotation.R270,
        _ => throw new System.IO.InvalidDataException(
            $"Invalid rotation '{degrees}'. Expected 0, 90, 180, or 270.")
    };

    private static Device CreateDevice(DeviceDto dto)
    {
        PartDefinition definition = dto.PartKind switch
        {
            "ic" => IcLookup.TryGetValue(dto.PartIdentifier, out var ic)
                ? ic
                : throw new System.IO.InvalidDataException(
                    $"Unknown IC part number '{dto.PartIdentifier}'. Known: {string.Join(", ", IcLookup.Keys)}."),
            "chip" => ChipLookup.TryGetValue(dto.PartIdentifier, out var chip)
                ? chip
                : throw new System.IO.InvalidDataException(
                    $"Unknown chip part number '{dto.PartIdentifier}'. Known: {string.Join(", ", ChipLookup.Keys)}."),
            "passive" => PassiveLookup.TryGetValue(dto.PartIdentifier, out var p)
                ? p
                : throw new System.IO.InvalidDataException(
                    $"Unknown passive '{dto.PartIdentifier}'. Known: {string.Join(", ", PassiveLookup.Keys)}."),
            "display" => DisplayLookup.TryGetValue(dto.PartIdentifier, out var d)
                ? d
                : throw new System.IO.InvalidDataException(
                    $"Unknown display '{dto.PartIdentifier}'. Known: {string.Join(", ", DisplayLookup.Keys)}."),
            "header" => HeaderLookup.TryGetValue(dto.PartIdentifier, out var h)
                ? h
                : throw new System.IO.InvalidDataException(
                    $"Unknown header '{dto.PartIdentifier}'. Known: {string.Join(", ", HeaderLookup.Keys)}."),
            _ => throw new System.IO.InvalidDataException(
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
                throw new System.IO.InvalidDataException(
                    $"Unknown TTL family '{dto.Family}' on device {dto.Designator}.");
        }
        else if (definition is PassivePartDefinition)
        {
            device.Family = null;
            device.Value = dto.Value;
        }

        return device;
    }

    /// <summary>
    /// Build the concrete <see cref="Unit"/> for a <see cref="UnitDto"/>, or
    /// return null if the unit's kind cannot currently be constructed
    /// (Power, DFlipFlop). A null return is logged; callers skip the unit.
    /// </summary>
    private static Unit? TryCreateUnit(Device device, UnitDto dto)
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

        if (spec is null)
            throw new System.IO.InvalidDataException(
                $"Device {device.Designator} ({device.Definition.Identifier}) has no unit with letter '{letter}'.");

        Unit unit;
        switch (spec.Kind)
        {
            case UnitKind.Nand: unit = new NandGateUnit(device, spec); break;
            case UnitKind.Nor: unit = new NorGateUnit(device, spec); break;
            case UnitKind.And: unit = new AndGateUnit(device, spec); break;
            case UnitKind.Or: unit = new OrGateUnit(device, spec); break;
            case UnitKind.Xor: unit = new XorGateUnit(device, spec); break;
            case UnitKind.Not: unit = new NotGateUnit(device, spec); break;
            case UnitKind.Resistor: unit = new ResistorUnit(device, spec); break;
            case UnitKind.Capacitor: unit = new CapacitorUnit(device, spec); break;
            case UnitKind.PolarizedCapacitor: unit = new PolarizedCapacitorUnit(device, spec); break;
            case UnitKind.Led: unit = new LedUnit(device, spec); break;
            case UnitKind.Button: unit = new ButtonUnit(device, spec); break;
            case UnitKind.Switch: unit = new SwitchUnit(device, spec); break;
            case UnitKind.Crystal: unit = new CrystalUnit(device, spec); break;
            case UnitKind.Diode: unit = new DiodeUnit(device, spec); break;
            case UnitKind.SevenSegment: unit = new SevenSegmentDisplayUnit(device, spec); break;
            case UnitKind.Chip:
                unit = DeviceFactory.CreateChipSymbol(device, spec, (ChipPartDefinition)device.Definition);
                break;
            case UnitKind.HeaderOutput:
                unit = new HeaderOutputUnit(device, spec, (HeaderPartDefinition)device.Definition);
                break;

            case UnitKind.DFlipFlop:
            case UnitKind.Power:
                // Not constructable yet. Skip rather than throw so a stray
                // one in a pasted (or loaded) selection doesn't sink the
                // whole operation. When these kinds get real Unit classes,
                // add cases above and they start round-tripping for free.
                Log.LogWarning(
                    "Skipping unit {UnitId} on device {Designator}: unit kind {Kind} is not yet constructable.",
                    dto.Id, device.Designator, spec.Kind);
                return null;

            default:
                throw new System.IO.InvalidDataException($"Unknown UnitKind: {spec.Kind}");
        }

        unit.Id = dto.Id;
        unit.Label = dto.Label;
        unit.Position = new Point(dto.Position.X, dto.Position.Y);
        unit.Rotation = ParseRotation(dto.Rotation);
        if (unit is SwitchUnit swUnit && dto.SwitchClosed is bool closed)
            swUnit.IsClosed = closed;
        device.Units.Add(unit);
        return unit;
    }

    private static SchematicItem CreateStandaloneItem(ItemDto dto) => dto.Type switch
    {
        "vcc" => new VccSymbol(),
        "gnd" => new GndSymbol(),
        "clock" => new UiClockSource
        {
            FrequencyHz = dto.FrequencyHz ?? 1_000_000.0,
            DutyCycle = dto.DutyCycle ?? 0.5,
            StartHigh = dto.StartHigh ?? false
        },
        _ => throw new System.IO.InvalidDataException(
            $"Unknown standalone item type '{dto.Type}'. Expected 'vcc', 'gnd', or 'clock'.")
    };

    private static Pin? ResolvePin(PinRefDto dto,
        Dictionary<string, SchematicItem> itemsByOldId,
        Schematic? destination)
    {
        if (string.IsNullOrEmpty(dto.ItemId)) return null;

        SchematicItem? item;
        if (dto.External)
        {
            if (destination is null) return null;
            item = destination.Items.FirstOrDefault(i => i.Id == dto.ItemId);
        }
        else
        {
            itemsByOldId.TryGetValue(dto.ItemId, out item);
        }
        if (item is null) return null;

        return item.Pins.FirstOrDefault(p => p.Number == dto.PinNumber);
    }
}

/// <summary>
/// Controls how <see cref="SchematicDtoMapper.FromDto"/> treats ids and
/// designators when rebuilding the object graph.
/// </summary>
public enum IdPolicy
{
    /// <summary>
    /// Keep the dto's ids and designators verbatim. The load path uses this:
    /// the file is the source of truth and its identities must survive.
    /// </summary>
    Preserve,

    /// <summary>
    /// Regenerate every device/item id, remap connection endpoints through
    /// the old-&gt;new table, and assign each device the next free designator.
    /// The paste path uses this so pasted parts never collide with what's
    /// already on the canvas.
    /// </summary>
    Fresh
}