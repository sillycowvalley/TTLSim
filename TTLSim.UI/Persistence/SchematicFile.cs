using System.Collections.Generic;

namespace TTLSim.UI.Persistence;

/// <summary>Top-level file shape for .ttlproj documents.</summary>
public sealed class SchematicFile
{
    public SchematicDto Schematic { get; set; } = new();
    public ViewDto? View { get; set; }
}

public sealed class SchematicDto
{
    /// <summary>Logical parts (chips and passives). Each device owns one or more units.</summary>
    public List<DeviceDto> Devices { get; set; } = new();

    /// <summary>Placed units. Every unit's DeviceId must match one entry in Devices.</summary>
    public List<UnitDto> Units { get; set; } = new();

    /// <summary>Standalone items not belonging to a device (VCC, GND).</summary>
    public List<ItemDto> Items { get; set; } = new();

    /// <summary>Logical pin-to-pin connections (current file format).</summary>
    public List<ConnectionDto> Connections { get; set; } = new();

    /// <summary>
    /// Ribbon-cable links between equal-pin-count header units. Pin count is
    /// not stored -- it is re-derived from the headers on load. Absent on files
    /// written before the feature; loads as an empty list.
    /// </summary>
    public List<HeaderLinkDto> Links { get; set; } = new();

    /// <summary>
    /// Layers. Index 0 is the visible "Default". Each unit/item's Layer field
    /// indexes into this list. Absent on files written before the feature; the
    /// loader synthesizes a single visible Default so index 0 always resolves.
    /// </summary>
    public List<LayerDto> Layers { get; set; } = new();

    /// <summary>
    /// Legacy field: wires with embedded waypoint geometry. Kept only so old
    /// files still load; the serializer migrates these into Connections at
    /// load time and never writes them.
    /// </summary>
    public List<WireDto> Wires { get; set; } = new();
}

/// <summary>
/// Logical part. PartKind discriminates between IC ("ic") and passive ("passive");
/// PartIdentifier is the part-specific key (PartNumber for ICs, Identifier for passives).
/// </summary>
public sealed class DeviceDto
{
    public string Id { get; set; } = "";
    public string Designator { get; set; } = "";
    public string PartKind { get; set; } = "";        // "ic" | "passive"
    public string PartIdentifier { get; set; } = "";  // "00", "32", "resistor", ...
    public string? Family { get; set; }               // ICs only: "Standard", "LS", "HC", ...
    public string? Value { get; set; }                // Passives only: "10k", "red", ...
    public string? Program { get; set; }              // EEPROM/ROM only: embedded Intel HEX image

    /// <summary>Memory / PLD only: explicit propagation/access delay in
    /// nanoseconds. Null means "use the part's default speed grade". Not
    /// written for 74-series or passive parts.</summary>
    public int? PropagationDelayNs { get; set; }

    /// <summary>555/556 timer only: timer-1 role ("Schmitt" | "Astable"),
    /// stored as the enum name. Null for non-timer parts.</summary>
    public string? Function1 { get; set; }

    /// <summary>556 only: timer-2 role ("Schmitt" | "Astable"). Null otherwise.</summary>
    public string? Function2 { get; set; }

    /// <summary>555/556 timer only: timer-1 astable frequency in hertz.
    /// Null for non-timer parts.</summary>
    public double? FrequencyHz1 { get; set; }

    /// <summary>556 only: timer-2 astable frequency in hertz. Null otherwise.</summary>
    public double? FrequencyHz2 { get; set; }
}

/// <summary>
/// Placed unit. UnitLetter selects which spec on the parent device's PartDefinition
/// this unit corresponds to. Stored as a string (typically one character) so the
/// empty string carries the '\0' single-unit sentinel cleanly across the JSON boundary.
/// </summary>
public sealed class UnitDto
{
    public string Id { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string UnitLetter { get; set; } = "";      // "a", "b", "" (single-unit), "?" (power)
    public PointDto Position { get; set; }
    public string Label { get; set; } = "";
    public int Rotation { get; set; }                 // 0, 90, 180, 270

    /// <summary>Index into the schematic's Layers list. 0 (Default) when absent.</summary>
    public int Layer { get; set; }

    public bool? SwitchClosed { get; set; }           // SPST switch units only; null otherwise
}

/// <summary>Standalone schematic item (VCC, GND). Devices and units use their own DTOs.</summary>
public sealed class ItemDto
{
    public string Type { get; set; } = "";            // "vcc" | "gnd"
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public PointDto Position { get; set; }
    public int Rotation { get; set; }                 // 0, 90, 180, 270

    /// <summary>Index into the schematic's Layers list. 0 (Default) when absent.</summary>
    public int Layer { get; set; }

    // Clock source signal properties. Null for VCC/GND.
    public double? FrequencyHz { get; set; }
    public double? DutyCycle { get; set; }
    public bool? StartHigh { get; set; }
    // Reference designator ("X1") for designated items (the canned oscillator).
    // Null on VCC/GND/CLK and on older files; the loader auto-assigns when missing.
    public string? Designator { get; set; }

    // Cosmetic rectangle (Type "rect"). Null on every other item type.
    // Colours are stored as TTLColor enum names ("Grey", "Blue", ...).
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool? Filled { get; set; }
    public string? FillColor { get; set; }
    public string? BorderColor { get; set; }

    // Cosmetic text label (Type "text"). The text itself rides on Label above.
    public float? FontSize { get; set; }
    public string? TextColor { get; set; }

}

/// <summary>
/// One layer's persisted state. Layers are referenced by index from each
/// unit/item's Layer field. Absent on files written before the feature; the
/// loader synthesizes a single visible "Default" so index 0 always resolves.
/// </summary>
public sealed class LayerDto
{
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
}

/// <summary>Current file format: a pure pin-to-pin connection with no geometry.</summary>
public sealed class ConnectionDto
{
    public string Id { get; set; } = "";
    public PinRefDto A { get; set; } = new();
    public PinRefDto B { get; set; } = new();

    /// <summary>
    /// Wire colour, stored as the enum name (e.g. "Red"). Null/missing on
    /// older files; loader falls back to Black.
    /// </summary>
    public string? Color { get; set; }
}

/// <summary>Reference to a pin by (item id, pin number).</summary>
public sealed class PinRefDto
{
    public string? ItemId { get; set; }
    public int PinNumber { get; set; }

    // True when this endpoint points at an item OUTSIDE the copied set: a
    // wire was copied but the item on this end was not. On paste, an external
    // endpoint resolves against the live destination schematic by ItemId
    // rather than through the paste's old->new id map. Default false keeps
    // every existing .ttlproj and every fully-internal connection unchanged.
    public bool External { get; set; }
}

/// <summary>
/// A ribbon-cable link between two header units, referenced by unit id. The
/// link ties pin i of A to pin i of B. Pin count is not stored -- it is derived
/// from the headers on load (both must still have matching pin counts, or the
/// link is dropped). Reversed is the cosmetic draw flag only.
/// </summary>
public sealed class HeaderLinkDto
{
    public string Id { get; set; } = "";
    public string AId { get; set; } = "";
    public string BId { get; set; } = "";
    public bool Reversed { get; set; }
}

// -----------------------------------------------------------------------------
// Legacy DTOs below. These are only kept so old .ttlproj files still load. The
// serializer reads them on load (migrating pin-anchored wires to Connections;
// free-point wires are dropped) and never writes them.
// -----------------------------------------------------------------------------

public sealed class WireDto
{
    public string Id { get; set; } = "";
    public EndpointDto A { get; set; } = new();
    public EndpointDto B { get; set; } = new();
    public List<PointDto> Waypoints { get; set; } = new();
}

public sealed class EndpointDto
{
    // Either (ItemId + PinNumber) for a pin endpoint, or FreePoint for a free point.
    // ItemId may refer to a Unit's Id or a standalone Item's Id; on load both are
    // SchematicItems so they share an id map.
    public string? ItemId { get; set; }
    public int PinNumber { get; set; }
    public PointDto? FreePoint { get; set; }
}

public struct PointDto
{
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class ViewDto
{
    public float Zoom { get; set; }
    public PointDto Pan { get; set; }
}