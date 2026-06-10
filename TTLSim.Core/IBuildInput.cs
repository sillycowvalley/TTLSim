namespace TTLSim.Core;

/// <summary>
/// Abstract input to the SchematicBuilder. Decouples the simulation engine
/// from any specific schematic representation (e.g. the WinForms Schematic).
/// </summary>
public interface IBuildInput
{
    /// <summary>All devices (chips, passives, displays) in the schematic.</summary>
    IEnumerable<BuildDevice> Devices { get; }

    /// <summary>Stand-alone items that aren't devices: VCC, GND, clock sources.</summary>
    IEnumerable<BuildItem> Items { get; }

    /// <summary>Pin-to-pin connections. Each pair declares the two pins share a net.</summary>
    IEnumerable<(PinRef A, PinRef B)> Connections { get; }
}

/// <summary>What the builder needs to know about a device.</summary>
public sealed record BuildDevice(
    string DeviceId,
    string Designator,
    string PartIdentifier,    // "00", "08", "393", "47", "7seg-ca", "resistor"
    string? Family,            // "HC", "LS", null for passives/displays
    int? PowerPinNumber,       // IC supply pin (14, 16, ...); null if N/A
    int? GroundPinNumber,      // IC ground pin (7, 8, ...); null if N/A
    IReadOnlyList<BuildUnit> Units,
    string? Program = null,    // EEPROM/ROM Intel HEX image; null otherwise
    int? PropagationDelayNs = null);   // memory/PLD explicit delay (ns); null = use part default

/// <summary>One placeable unit within a device.</summary>
/// <param name="OutputPinNumber">Single output pin for gate-style units (from UnitSpec).</param>
/// <param name="OutputPinNumbers">Multiple output pins for box-style chips (from ChipPinRole).
/// Kept separate from InputPinNumbers so floating-input diagnostics ignore them,
/// but still included in the net map so the chip model can drive them.</param>
public sealed record BuildUnit(
    string UnitId,
    char Letter,
    IReadOnlyList<int> InputPinNumbers,
    int? OutputPinNumber,
    IReadOnlyList<int>? OutputPinNumbers = null,
    bool SwitchClosed = false);

/// <summary>A stand-alone non-device item (VCC, GND, clock).</summary>
public sealed record BuildItem(
    string ItemId,
    BuildItemKind Kind,
    IReadOnlyList<int> PinNumbers,
    long? ClockPeriodPicoseconds = null);

public enum BuildItemKind
{
    Vcc,
    Gnd,
    ClockSource
}