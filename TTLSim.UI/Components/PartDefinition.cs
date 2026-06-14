using System;
using System.Collections.Generic;

namespace TTLSim.UI.Components;

/// <summary>
/// What kind of logic / passive symbol a unit is drawn as. The Kind decides
/// which concrete Unit subclass DeviceFactory instantiates and how that unit
/// renders itself; it doesn't constrain pin counts (NAND with two inputs and
/// NAND with four inputs share UnitKind.Nand).
/// </summary>
public enum UnitKind
{
    // Combinational logic
    Nand, Nor, And, Or, Xor, Not,
    // Sequential -- placeholder; concrete unit class added in a later phase
    DFlipFlop,
    // Passives
    Resistor, Capacitor, PolarizedCapacitor, Led, Button, Switch, SpdtSwitch, Crystal, Diode,
    // Display
    SevenSegment,
    // I/O
    HeaderOutput,
    // Special
    Power,
    // Box-shaped IC drawn as a rectangle with named pins (RAM, ROM, MCUs).
    Chip
}

/// <summary>
/// Functional subcategory for 74xx ICs. Order here is the display order in
/// the library tree -- keep it in roughly the order a CPU designer reaches
/// for them (glue logic first, ALU last).
/// </summary>
public enum IcCategory
{
    Gates,
    FlipFlops,
    Registers,
    Counters,
    Decoders,
    Multiplexers,
    Buffers,
    Alu,
    DisplayDrivers
}

/// <summary>
/// Human-readable labels for IcCategory. Kept here so adding a category is
/// one enum entry plus one label, with no UI code to touch.
/// </summary>
public static class IcCategoryLabels
{
    public static string DisplayName(IcCategory category) => category switch
    {
        IcCategory.Gates => "Gates",
        IcCategory.FlipFlops => "Flip-flops",
        IcCategory.Registers => "Registers",
        IcCategory.Counters => "Counters",
        IcCategory.Decoders => "Decoders",
        IcCategory.Multiplexers => "Multiplexers",
        IcCategory.Buffers => "Buffers",
        IcCategory.Alu => "ALU",
        IcCategory.DisplayDrivers => "Display Drivers",
        _ => category.ToString()
    };
}

/// <summary>
/// Whether a pin on a box-shaped chip is an input or an output. Used by
/// the build pipeline to separate floating-input diagnostics (TTL011) from
/// output pins that are legitimately unconnected without warning.
/// </summary>
public enum ChipPinRole { Input, Output }

/// <summary>
/// One pin on a box-shaped chip. Order in the parent ChipPartDefinition.Pins
/// array doesn't matter; pin position on the symbol is driven by PinNumber:
/// pins 1..N/2 run down the left side top-to-bottom, pins N/2+1..N run up
/// the right side bottom-to-top, mirroring a physical DIP package layout.
/// </summary>
public sealed record ChipPin(string Name, int Number, ChipPinRole Role = ChipPinRole.Input);

/// <summary>
/// One placeable unit within a device. Pin numbers refer to physical IC pins
/// (1..N) shared across the whole device; for passives they're 1 and 2.
/// </summary>
public sealed record UnitSpec(
    UnitKind Kind,
    char Letter,                  // 'a', 'b', ... ; '?' for power; '\0' for single-unit parts
    int[] InputPins,              // physical IC pin numbers; empty for passives
    int OutputPin,                // 0 if N/A
    int[]? ExtraPins = null,      // clock / preset / clear for flip-flops etc.
    bool IsSchmitt = false);

/// <summary>
/// Common base for any kind of part. Carries the reference prefix used for
/// auto-designation (U for ICs, R/C/D for passives).
/// </summary>
public abstract record PartDefinition(string Identifier, string ReferencePrefix);

/// <summary>
/// 74xx IC: pin count, power pins, functional category, and one UnitSpec per
/// gate or flip-flop. Category drives library-tree grouping.
/// </summary>
public sealed record IcPartDefinition(
    string PartNumber,            // "00", "32", "181"
    int PinCount,
    int PowerPin,
    int GroundPin,
    IcCategory Category,
    UnitSpec[] Units)
    : PartDefinition(PartNumber, "U")
{
    // ------------------------------------------------------------------ catalogue

    // 7474 (dual D flip-flop) intentionally omitted until DFlipFlopUnit lands.
    //
    // All gate ICs (00/02/04/08/10/14/20/30/32/86) have been converted to
    // single-part DIP-14 boxes -- see ChipPartDefinition. They're simulated
    // as their constituent gates by ChipFactory and export via the standard
    // DIP-14 path. No gate IcPartDefinitions remain, so the catalogue below
    // is empty; it stays as the extension point for any future multi-unit
    // IcPartDefinition that isn't a box.

    /// <summary>
    /// All known 74xx parts. The library panel iterates this and groups by
    /// Category -- adding a new chip is one line here, no UI edit required.
    /// </summary>
    public static readonly IReadOnlyList<IcPartDefinition> Catalogue =
        Array.Empty<IcPartDefinition>();
}

/// <summary>
/// Passive part: one unit, no power pins, no family. Value lives on the
/// owning Device as an optional string.
/// </summary>
public sealed record PassivePartDefinition(
    string Identifier,            // "resistor", "capacitor", "led"
    string ReferencePrefix,       // "R", "C", "D"
    UnitKind UnitKind)
    : PartDefinition(Identifier, ReferencePrefix)
{
    public static readonly PassivePartDefinition Resistor = new("resistor", "R", UnitKind.Resistor);
    public static readonly PassivePartDefinition Capacitor = new("capacitor", "C", UnitKind.Capacitor);
    public static readonly PassivePartDefinition PolarizedCapacitor = new("polarized-capacitor", "C", UnitKind.PolarizedCapacitor);
    public static readonly PassivePartDefinition Led = new("led", "D", UnitKind.Led);
    public static readonly PassivePartDefinition Button = new("button", "SW", UnitKind.Button);
    public static readonly PassivePartDefinition Switch = new("switch", "S", UnitKind.Switch);
    public static readonly PassivePartDefinition SpdtSwitch = new("spdt-switch", "S", UnitKind.SpdtSwitch);
    public static readonly PassivePartDefinition Crystal = new("crystal", "Y", UnitKind.Crystal);
    public static readonly PassivePartDefinition Diode = new("diode", "D", UnitKind.Diode);
}

/// <summary>
/// Polarity of a multi-segment LED display. Common-anode displays share their
/// anodes (typically tied to VCC through a current-limiting resistor per
/// segment) and are driven LOW per segment to light; common-cathode is the
/// inverse. The choice of polarity dictates which decoder/driver IC pairs
/// with the display (7447 for CA, 7448 for CC).
/// </summary>
public enum DisplayKind
{
    CommonAnode,
    CommonCathode
}

/// <summary>
/// LED display part: a multi-segment indicator with a polarity. Unlike a plain
/// passive it has more than two terminals; unlike a chip it has no part-number
/// catalogue and pins are referenced by function (a..g, dp, com) rather than
/// physical pin number. UnitKind drives the concrete Unit class; DisplayKind
/// is carried on the definition so the unit can adjust pin direction / labels
/// without two near-identical classes.
/// </summary>
public sealed record DisplayPartDefinition(
    string Identifier,            // "7seg-ca", "7seg-cc"
    DisplayKind Kind,
    UnitKind UnitKind)
    : PartDefinition(Identifier, "DS")
{
    public static readonly DisplayPartDefinition SevenSegmentCommonAnode =
        new("7seg-ca", DisplayKind.CommonAnode, UnitKind.SevenSegment);

    public static readonly DisplayPartDefinition SevenSegmentCommonCathode =
        new("7seg-cc", DisplayKind.CommonCathode, UnitKind.SevenSegment);
}

/// <summary>
/// Pin-header connector used as an external observation point. Has no chip
/// model -- it's a pure net terminator that displays the resolved signal on
/// each of its pins in sim mode. Pin count is fixed per definition (2/4/6);
/// each variant is a separate static instance so the library tree can list
/// them individually.
/// </summary>
public sealed record HeaderPartDefinition(
    string Identifier,            // "hdr-out-2", "hdr-out-4", "hdr-out-6"
    int PinCount,
    bool IsOutput)                // outputs only for now; inputs land later
    : PartDefinition(Identifier, "J")
{
    public static readonly HeaderPartDefinition HeaderOut2 = new("hdr-out-2", 2, true);
    public static readonly HeaderPartDefinition HeaderOut3 = new("hdr-out-3", 3, true);
    public static readonly HeaderPartDefinition HeaderOut4 = new("hdr-out-4", 4, true);
    public static readonly HeaderPartDefinition HeaderOut6 = new("hdr-out-6", 6, true);
    public static readonly HeaderPartDefinition HeaderOut8 = new("hdr-out-8", 8, true);
}