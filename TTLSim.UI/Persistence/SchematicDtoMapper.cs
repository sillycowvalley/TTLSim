using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TTLSim.Core;
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
            [ChipPartDefinition.Ic28C16.PartNumber] = ChipPartDefinition.Ic28C16,
            [ChipPartDefinition.Ic62256.PartNumber] = ChipPartDefinition.Ic62256,
            [ChipPartDefinition.Ic7C199.PartNumber] = ChipPartDefinition.Ic7C199,
            [ChipPartDefinition.Ic6116.PartNumber] = ChipPartDefinition.Ic6116,
            [ChipPartDefinition.Ic2114.PartNumber] = ChipPartDefinition.Ic2114,
            [ChipPartDefinition.Ic6264.PartNumber] = ChipPartDefinition.Ic6264,
            [ChipPartDefinition.IcW24512.PartNumber] = ChipPartDefinition.IcW24512,

            // Timers
            [ChipPartDefinition.IcNe555.PartNumber] = ChipPartDefinition.IcNe555,
            [ChipPartDefinition.IcNe556.PartNumber] = ChipPartDefinition.IcNe556,
            [ChipPartDefinition.IcGal16V8.PartNumber] = ChipPartDefinition.IcGal16V8,
            [ChipPartDefinition.IcGal20V8.PartNumber] = ChipPartDefinition.IcGal20V8,
            [ChipPartDefinition.IcGal22V10.PartNumber] = ChipPartDefinition.IcGal22V10,
            [ChipPartDefinition.Ds1813.PartNumber] = ChipPartDefinition.Ds1813,

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
            [ChipPartDefinition.Ic74374.PartNumber] = ChipPartDefinition.Ic74374,
            [ChipPartDefinition.Ic74377.PartNumber] = ChipPartDefinition.Ic74377,
            [ChipPartDefinition.Ic74574.PartNumber] = ChipPartDefinition.Ic74574,
            [ChipPartDefinition.Ic74299.PartNumber] = ChipPartDefinition.Ic74299,
            [ChipPartDefinition.Ic74595.PartNumber] = ChipPartDefinition.Ic74595,

            // Counters
            [ChipPartDefinition.Ic74161.PartNumber] = ChipPartDefinition.Ic74161,
            [ChipPartDefinition.Ic74163.PartNumber] = ChipPartDefinition.Ic74163,
            [ChipPartDefinition.Ic74191.PartNumber] = ChipPartDefinition.Ic74191,
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
            [PassivePartDefinition.ResistorNetwork.Identifier] = PassivePartDefinition.ResistorNetwork,
            [PassivePartDefinition.Capacitor.Identifier] = PassivePartDefinition.Capacitor,
            [PassivePartDefinition.PolarizedCapacitor.Identifier] = PassivePartDefinition.PolarizedCapacitor,
            [PassivePartDefinition.Led.Identifier] = PassivePartDefinition.Led,
            [PassivePartDefinition.Button.Identifier] = PassivePartDefinition.Button,
            [PassivePartDefinition.Button4.Identifier] = PassivePartDefinition.Button4,
            [PassivePartDefinition.Switch.Identifier] = PassivePartDefinition.Switch,
            [PassivePartDefinition.SpdtSwitch.Identifier] = PassivePartDefinition.SpdtSwitch,
            [PassivePartDefinition.Jumper2.Identifier] = PassivePartDefinition.Jumper2,
            [PassivePartDefinition.Jumper3.Identifier] = PassivePartDefinition.Jumper3,
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
            [HeaderPartDefinition.HeaderOut3.Identifier] = HeaderPartDefinition.HeaderOut3,
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
        IEnumerable<Connection> connections,
        IEnumerable<HeaderLink>? links = null,
        IReadOnlyList<Layer>? layers = null)
    {
        var dto = new SchematicDto();

        // Layer table. Items carry an index into this list (UnitDto.Layer /
        // ItemDto.Layer). When a caller serialises without layers (an old call
        // site, or a copy with none supplied) the table is left empty and
        // FromDto falls everything back to the Default layer.
        if (layers is not null)
        {
            foreach (var layer in layers)
                dto.Layers.Add(new LayerDto { Name = layer.Name, Visible = layer.Visible });
        }

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
                case VccSymbol or GndSymbol or UiClockSource or CanOscillator or ICosmeticItem:
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
                Color = connection.Color == TTLColor.Black
                    ? null : connection.Color.ToString()
            });
        }

        if (links is not null)
        {
            foreach (var link in links)
            {
                // Only links fully inside the supplied item set are emitted.
                // For a whole-schematic save every header is present so nothing
                // is lost; for a selection copy this drops a link with an
                // endpoint outside the selection.
                if (!itemIds.Contains(link.A.Id) || !itemIds.Contains(link.B.Id))
                    continue;

                dto.Links.Add(new HeaderLinkDto
                {
                    Id = link.Id,
                    AId = link.A.Id,
                    BId = link.B.Id,
                    Reversed = link.Reversed
                });
            }
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
            Program = device.Program,
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
        Layer = unit.LayerId,
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
            Layer = item.LayerId,
            Type = item switch
            {
                VccSymbol => "vcc",
                GndSymbol => "gnd",
                UiClockSource => "clock",
                // CanOscillatorDip8 derives from CanOscillator, so it MUST be
                // matched first -- otherwise the half-size DIP-8 oscillator
                // would serialise (and round-trip) as a full-size "canosc".
                CanOscillatorDip8 => "canosc8",
                CanOscillator => "canosc",
                RectangleItem => "rect",
                TextLabelItem => "text",
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
        if (item is CanOscillator osc)
            dto.FrequencyHz = osc.FrequencyHz;

        if (item is RectangleItem rect)
        {
            dto.Width = rect.Width;
            dto.Height = rect.Height;
            dto.Filled = rect.Filled;
            dto.FillColor = rect.FillColor.ToString();
            dto.BorderColor = rect.BorderColor.ToString();
        }
        if (item is TextLabelItem text)
        {
            dto.FontSize = text.FontSize;
            dto.TextColor = text.TextColor.ToString();
        }

        if (item is IDesignatedItem des)
            dto.Designator = des.Designator;

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
        public List<HeaderLink> Links { get; } = new();

        /// <summary>
        /// Rebuilt layer table, populated on the LOAD path only. On paste the
        /// destination schematic already owns its layers, so any layer a pasted
        /// item needs is matched/created directly against the designatorScope
        /// instead and this list stays empty.
        /// </summary>
        public List<Layer> Layers { get; } = new();

        /// <summary>
        /// Units present in the source DTO that could not be reconstructed
        /// (a not-yet-constructable kind) and were therefore omitted. Non-zero
        /// means this rebuild is a PARTIAL of the source: the caller must
        /// surface that, never silently accept fewer items than were asked for.
        /// </summary>
        public int SkippedUnits { get; set; }

        /// <summary>
        /// Connections present in the source DTO whose endpoint(s) did not
        /// resolve -- a wire onto a skipped unit, or (on paste) onto an item
        /// outside the copied set -- and were therefore omitted. Non-zero means
        /// a partial rebuild.
        /// </summary>
        public int DroppedConnections { get; set; }

        /// <summary>
        /// Header links in the source DTO whose endpoints did not resolve to
        /// two equal-pin-count headers (a header was skipped, or pin counts no
        /// longer match) and were therefore omitted. Non-zero means a partial
        /// rebuild.
        /// </summary>
        public int DroppedLinks { get; set; }

        /// <summary>True when anything in the source DTO failed to come across.</summary>
        public bool IsPartial => SkippedUnits > 0 || DroppedConnections > 0 || DroppedLinks > 0;
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

        // ---- Layer table ----------------------------------------------------
        // Map each source layer index to a destination layer index.
        //
        //  Preserve (load): the file's table is the source of truth. Rebuild it
        //  verbatim into result.Layers; an old file with no table gets the one
        //  visible Default. The per-item index is used as-is (clamped).
        //
        //  Fresh (paste): match each source layer to the live schematic BY NAME.
        //  An existing same-named layer is reused; a source layer with no name
        //  match is appended to the live schematic and that new index is used.
        //  This lands a pasted block on the same-named layer it came from, and
        //  creates that layer in the destination when it isn't there yet.
        int[] layerRemap = Array.Empty<int>();
        if (fresh)
        {
            var dest = designatorScope!.Layers;
            layerRemap = new int[dto.Layers.Count];
            for (int i = 0; i < dto.Layers.Count; i++)
            {
                var src = dto.Layers[i];
                int found = dest.FindIndex(l =>
                    string.Equals(l.Name, src.Name, StringComparison.Ordinal));
                if (found >= 0)
                {
                    layerRemap[i] = found;
                }
                else
                {
                    dest.Add(new Layer(src.Name, src.Visible));
                    layerRemap[i] = dest.Count - 1;
                }
            }
        }
        else
        {
            if (dto.Layers.Count == 0)
                result.Layers.Add(new Layer("Default", visible: true));
            else
                foreach (var l in dto.Layers)
                    result.Layers.Add(new Layer(l.Name, l.Visible));
        }

        int RemapLayer(int sourceId)
        {
            if (fresh)
                return sourceId >= 0 && sourceId < layerRemap.Length ? layerRemap[sourceId] : 0;
            // Load: ids index the file's own table; clamp out-of-range to Default.
            return sourceId >= 0 && sourceId < result.Layers.Count ? sourceId : 0;
        }

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
            {
                result.SkippedUnits++;   // unconstructable kind -- logged in TryCreateUnit
                continue;
            }

            if (fresh)
                unit.Id = Guid.NewGuid().ToString("N");

            unit.LayerId = RemapLayer(unitDto.Layer);

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
            item.LayerId = RemapLayer(itemDto.Layer);
            // Paste (Fresh): fresh designator, unique against the live schematic
            // so a pasted oscillator never collides. Load (Preserve): keep verbatim.
            if (item is IDesignatedItem des)
                des.Designator = fresh
                    ? designatorScope!.NextDesignator(des.ReferencePrefix)
                    : (itemDto.Designator ?? "");
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
            {
                result.DroppedConnections++;
                continue;
            }

            var connection = new Connection(a, b)
            {
                Id = fresh ? Guid.NewGuid().ToString("N") : connDto.Id
            };
            if (!string.IsNullOrEmpty(connDto.Color)
                && Enum.TryParse<TTLColor>(connDto.Color, ignoreCase: true, out var wc))
            {
                connection.Color = wc;
            }
            result.Connections.Add(connection);
        }

        // ---- Pass 5: header links ------------------------------------------
        // Resolve both endpoints against the rebuilt items. A link is dropped
        // if either endpoint is missing or not a header, or if the two headers
        // no longer have matching pin counts.
        foreach (var linkDto in dto.Links)
        {
            if (!itemsByOldId.TryGetValue(linkDto.AId, out var ia) ||
                !itemsByOldId.TryGetValue(linkDto.BId, out var ib) ||
                ia is not HeaderOutputUnit ha ||
                ib is not HeaderOutputUnit hb ||
                ha.Pins.Count() != hb.Pins.Count())
            {
                result.DroppedLinks++;
                continue;
            }

            result.Links.Add(new HeaderLink(ha, hb)
            {
                Id = fresh ? Guid.NewGuid().ToString("N") : linkDto.Id,
                Reversed = linkDto.Reversed
            });
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
            Program = dto.Program     // EEPROM/ROM Intel HEX image; null for other parts
        };

        // A file value overrides the constructor-seeded default speed grade; a
        // null in the file (an older file, or a memory part the user never
        // overrode) leaves the seeded default in place so the grid still shows
        // it. Assigning null here unconditionally would blank out that default.
        if (dto.PropagationDelayNs is int delayNs)
            device.PropagationDelayNs = delayNs;

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

        // 555/556 timer settings. The Device constructor has already applied
        // sensible defaults (Schmitt + 1000 Hz), so older files without these
        // fields load fine; here we just override from whatever the file
        // carried. Without this block a 555/556 with a non-default function or
        // frequency would lose those settings on load and on paste.
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
            case UnitKind.ResistorNetwork: unit = new ResistorNetworkUnit(device, spec); break;
            case UnitKind.Capacitor: unit = new CapacitorUnit(device, spec); break;
            case UnitKind.PolarizedCapacitor: unit = new PolarizedCapacitorUnit(device, spec); break;
            case UnitKind.Led: unit = new LedUnit(device, spec); break;
            case UnitKind.Button: unit = new ButtonUnit(device, spec); break;
            case UnitKind.Switch: unit = new SwitchUnit(device, spec); break;
            case UnitKind.SpdtSwitch: unit = new SpdtSwitchUnit(device, spec); break;
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
        if (unit is SpdtSwitchUnit spdt && dto.SwitchClosed is bool pos)
            spdt.ThrowB = pos;

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
        "canosc" => new CanOscillator { FrequencyHz = dto.FrequencyHz ?? 1_000_000.0 },
        "canosc8" => new CanOscillatorDip8 { FrequencyHz = dto.FrequencyHz ?? 1_000_000.0 },
        "rect" => new RectangleItem
        {
            Width = dto.Width ?? 20,
            Height = dto.Height ?? 12,
            Filled = dto.Filled ?? true,
            FillColor = ParseTtlColor(dto.FillColor),
            BorderColor = ParseTtlColor(dto.BorderColor)
        },
        "text" => new TextLabelItem
        {
            FontSize = dto.FontSize ?? 4.0f,
            TextColor = ParseTtlColor(dto.TextColor)
        },
        _ => throw new System.IO.InvalidDataException(
            $"Unknown standalone item type '{dto.Type}'. " +
            "Expected 'vcc', 'gnd', 'clock', 'canosc', 'canosc8', 'rect', or 'text'.")
    };

    private static TTLColor ParseTtlColor(string? name) =>
        Enum.TryParse<TTLColor>(name, out var c) ? c : TTLColor.Grey;

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